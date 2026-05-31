using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

/// <summary>
/// Public entry to the link pipeline. Turns one or more CoreCLR-target COFF
/// objects into a pure-MSIL (.NET <c>ILOnly</c>) PE assembly with a synthesized
/// managed entry point.
/// </summary>
public static class LinkPipeline
{
    public static byte[] LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs)
    {
        if (objs == null || objs.Count == 0)
            throw new LinkException("no input objects.");
        return new PeWriter(objs, libs ?? new List<string>()).Write();
    }
}

/// <summary>
/// Performs steps 3–5 of the ordering contract: fix IL, encode bodies in the
/// predicted order, populate MethodDef rows in the same order (asserting the
/// predictions), add the module/type/assembly rows, set the entry point, and
/// serialize a pure-MSIL PE.
///
/// Cooperation with <see cref="MetadataMerger"/>: the merger predicts MethodDef
/// rows and copies AssemblyRef/TypeRef/StandAloneSig (steps 1). This writer owns
/// the body encoding + row population because the body offset returned by the
/// MethodBodyStreamEncoder must be baked into each MethodDef row — which can only
/// happen during PE emission.
/// </summary>
public sealed class PeWriter
{
    private readonly IReadOnlyList<ObjectFile> _objs;
    private readonly List<string> _libs;

    public PeWriter(IReadOnlyList<ObjectFile> objs, List<string> libs)
    {
        _objs = objs;
        _libs = libs;
    }

    public byte[] Write()
    {
        var merger = new MetadataMerger(_objs);
        merger.MergeAndPredict();

        // Reserve the entry method's row (last in the plan) so its token is known.
        var (entryRow, entryHandle) = merger.ReserveEntryRow();

        // Synthesize the entry IL (main's final token already baked). The entry
        // signature uses ELEMENT_TYPE_STRING/SZARRAY primitives — no TypeRef.
        var entry = EntrySynthesizer.Synthesize(merger);

        var mdBuilder = merger.Builder;

        // ── Step 3: encode every method body in the predicted order ───────────
        var ilBuilder = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(ilBuilder);

        // bodyOffsets[i] aligns with merger.Plan[i].
        var bodyOffsets = new int[merger.Plan.Count];

        for (int i = 0; i < merger.Plan.Count; i++)
        {
            var slot = merger.Plan[i];
            byte[] il;
            int maxStack;
            StandaloneSignatureHandle localSig;
            bool initLocals;

            if (slot.Method == null)
            {
                // Synthesized entry.
                il = entry.Il;
                maxStack = entry.MaxStack;
                localSig = default;
                initLocals = false;
            }
            else
            {
                il = RelocationFixer.Fix(slot.Method, slot.Obj, merger);
                maxStack = slot.Method.MaxStack;
                localSig = merger.MapLocalSig(slot.Obj, slot.Method);
                initLocals = slot.Method.InitLocals;
            }

            int offset = AddBody(bodyEncoder, il, maxStack, localSig, initLocals);
            bodyOffsets[i] = offset;
        }

        // ── Step 5a: <Module> TypeDef (row 1), owns all methods starting at 1 ──
        var moduleTypeDef = mdBuilder.AddTypeDefinition(
            default,
            default,
            mdBuilder.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        AssertRow(MetadataMerger.ModuleTypeDefRow, MetadataTokens.GetRowNumber(moduleTypeDef), "TypeDef <Module>");

        // ── Step 4: populate MethodDef rows in the predicted order ────────────
        for (int i = 0; i < merger.Plan.Count; i++)
        {
            var slot = merger.Plan[i];
            BlobHandle sig;
            StringHandle name;
            MethodAttributes attrs;
            MethodImplAttributes impl;

            if (slot.Method == null)
            {
                sig = entry.SignatureBlob;
                name = entry.Name;
                attrs = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                impl = MethodImplAttributes.IL;
            }
            else
            {
                var def = slot.Obj.Md.GetMethodDefinition(slot.Method.Handle);
                sig = merger.RewriteMethodSignature(slot.Obj, slot.Method);
                name = mdBuilder.GetOrAddString(slot.Method.Name);
                // Strip UnmanagedExport (it requires a VTableFixup/export table we
                // don't emit); keep Static. Make accessible so the entry can call.
                attrs = (def.Attributes & ~MethodAttributes.MemberAccessMask & ~MethodAttributes.PinvokeImpl)
                        | MethodAttributes.Public | MethodAttributes.Static;
                attrs &= ~MethodAttributes.UnmanagedExport;
                impl = def.ImplAttributes;
            }

            var h = mdBuilder.AddMethodDefinition(
                attrs,
                impl,
                name,
                sig,
                bodyOffsets[i],
                // Phase 0: no Param rows are emitted, so all methods point at row 1
                parameterList: MetadataTokens.ParameterHandle(1));
            AssertRow(slot.PredictedRow, MetadataTokens.GetRowNumber(h),
                slot.Method == null ? "entry MethodDef" : $"MethodDef '{slot.Method.Name}'");
        }

        // ── Step 5b: Assembly + Module rows ───────────────────────────────────
        mdBuilder.AddModule(
            0,
            mdBuilder.GetOrAddString("a.dll"),
            mdBuilder.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        mdBuilder.AddAssembly(
            mdBuilder.GetOrAddString("a"),
            new Version(0, 0, 0, 0),
            default,
            default,
            0,
            AssemblyHashAlgorithm.Sha1);

        // ── Step 5c: serialize a pure-MSIL executable PE ──────────────────────
        var rootBuilder = new MetadataRootBuilder(mdBuilder);

        var peHeader = PEHeaderBuilder.CreateExecutableHeader();

        var peBuilder = new ManagedPEBuilder(
            peHeader,
            rootBuilder,
            ilBuilder,
            entryPoint: entryHandle,
            flags: CorFlags.ILOnly);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }

    private static int AddBody(
        MethodBodyStreamEncoder encoder,
        byte[] il,
        int maxStack,
        StandaloneSignatureHandle localSig,
        bool initLocals)
    {
        // attributeInstructions == false: we have no branch/exception fixups to
        // apply; the IL is already final. AddMethodBody reserves the blob and
        // returns its offset + a writable Instructions blob to copy IL into.
        var body = encoder.AddMethodBody(
            codeSize: il.Length,
            maxStack: maxStack,
            exceptionRegionCount: 0,
            hasSmallExceptionRegions: true,
            localVariablesSignature: localSig,
            attributes: initLocals ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None);

        var writer = new BlobWriter(body.Instructions);
        writer.WriteBytes(il);
        return body.Offset;
    }

    private static void AssertRow(int expected, int actual, string what)
    {
        if (expected != actual)
            throw new LinkException(
                $"row-prediction mismatch for {what}: predicted {expected}, got {actual}.");
    }
}
