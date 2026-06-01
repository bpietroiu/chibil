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

        // Resolve cross-object references and synthesize native P/Invoke stubs.
        // Runs after defined-method prediction (so the export table is complete)
        // and before the entry row, reserving any P/Invoke MethodDef rows in the
        // plan so the row-prediction assertions below still hold.
        SymbolResolver.Resolve(merger, _objs, _libs);

        // Collect FieldRVA pointer relocations (function/data pointers baked into
        // initialized globals) and, if any exist, synthesize a <Module> .cctor
        // that applies them at load time. Reserve its row BEFORE the entry row so
        // its body is encoded with the rest of the plan.
        var fieldRelocs = FieldDataRelocator.Collect(merger, _objs);
        MetadataMerger.SynthMethod cctorSynth = null;
        if (fieldRelocs.Count > 0)
        {
            // void .cctor() — default calling convention, no params, returns void.
            var cctorSig = new BlobBuilder();
            new BlobEncoder(cctorSig)
                .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
                .Parameters(0, ret => ret.Void(), _ => { });
            cctorSynth = new MetadataMerger.SynthMethod
            {
                Name = ".cctor",
                SignatureBlob = merger.Builder.GetOrAddBlob(cctorSig),
                Il = FieldDataRelocator.BuildCctorIl(fieldRelocs),
                MaxStack = 4,
                Attributes = MethodAttributes.Private | MethodAttributes.Static
                           | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                           | MethodAttributes.RTSpecialName,
            };
            merger.ReserveSynthRow(cctorSynth);
        }

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

            if (slot.PInvoke != null)
            {
                // P/Invoke stubs have NO body; they are not encoded into the
                // method-body stream and carry a -1 body offset on their row.
                bodyOffsets[i] = -1;
                continue;
            }

            byte[] il;
            int maxStack;
            StandaloneSignatureHandle localSig;
            bool initLocals;

            if (slot.Synth != null)
            {
                // Synthesized method with prebuilt IL (the FieldRVA .cctor).
                il = slot.Synth.Il;
                maxStack = slot.Synth.MaxStack;
                localSig = default;
                initLocals = false;
            }
            else if (slot.Method == null)
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

        // ── Step 5a0: data fields (string literals / initialized globals) ─────
        // Copy each HasFieldRVA field's bytes into the mapped-field-data blob and
        // emit a Field row + FieldRVA pointing at its offset. All such fields are
        // owned by <Module>, so they occupy Field rows 1..N (matching the
        // merger's predictions). The blob is handed to the PE builder, which
        // places it in a data section and rewrites the FieldRVA placeholders.
        var mappedFieldData = new BlobBuilder();
        // Map each field's output row -> its byte offset within the field-data blob.
        // The rebaser uses these to set FieldRVA = .sdata base + dataOffset DIRECTLY,
        // which is robust to however ManagedPEBuilder assigned the placeholder RVAs.
        var fieldDataOffsets = new Dictionary<int, int>();
        foreach (var cf in merger.CopiedFields)
        {
            int align = cf.Alignment <= 0 ? 1 : cf.Alignment;
            while ((mappedFieldData.Count % align) != 0) mappedFieldData.WriteByte(0);
            int dataOffset = mappedFieldData.Count;
            mappedFieldData.WriteBytes(cf.Data);

            var fh = mdBuilder.AddFieldDefinition(
                cf.Attributes,
                mdBuilder.GetOrAddString(cf.Name),
                cf.SignatureBlob);
            AssertRow(cf.PredictedRow, MetadataTokens.GetRowNumber(fh), $"Field '{cf.Name}'");
            mdBuilder.AddFieldRelativeVirtualAddress(fh, dataOffset);
            fieldDataOffsets[MetadataTokens.GetRowNumber(fh)] = dataOffset;
        }

        // ── Step 5a: <Module> TypeDef (row 1), owns all fields + methods ──────
        var moduleTypeDef = mdBuilder.AddTypeDefinition(
            default,
            default,
            mdBuilder.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        AssertRow(MetadataMerger.ModuleTypeDefRow, MetadataTokens.GetRowNumber(moduleTypeDef), "TypeDef <Module>");

        // ── Step 5a1: value-type TypeDefs referenced by field signatures ──────
        // These own no fields/methods, so their lists point past the end of the
        // Field/MethodDef tables (1-based, exclusive upper bound = count + 1).
        int totalFields = merger.CopiedFields.Count;
        int totalMethods = merger.Plan.Count;
        foreach (var ct in merger.CopiedTypeDefs)
        {
            var tdH = mdBuilder.AddTypeDefinition(
                System.Reflection.TypeAttributes.SequentialLayout
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.AnsiClass,
                ct.Namespace.Length == 0 ? default : mdBuilder.GetOrAddString(ct.Namespace),
                mdBuilder.GetOrAddString(ct.Name),
                ct.BaseType,
                MetadataTokens.FieldDefinitionHandle(totalFields + 1),
                MetadataTokens.MethodDefinitionHandle(totalMethods + 1));
            AssertRow(ct.PredictedRow, MetadataTokens.GetRowNumber(tdH), $"TypeDef '{ct.Name}'");
            if (ct.LayoutSize >= 0)
                mdBuilder.AddTypeLayout(tdH, (ushort)ct.LayoutPack, (uint)ct.LayoutSize);
        }

        // ── Step 4: populate MethodDef rows in the predicted order ────────────
        for (int i = 0; i < merger.Plan.Count; i++)
        {
            var slot = merger.Plan[i];

            if (slot.PInvoke != null)
            {
                var stub = slot.PInvoke;
                var pinvokeH = mdBuilder.AddMethodDefinition(
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
                    MethodImplAttributes.PreserveSig,
                    mdBuilder.GetOrAddString(stub.Name),
                    stub.SignatureBlob,
                    bodyOffset: -1,                              // no body
                    parameterList: MetadataTokens.ParameterHandle(1));
                mdBuilder.AddMethodImport(
                    pinvokeH,
                    MethodImportAttributes.CallingConventionCDecl
                        | MethodImportAttributes.ExactSpelling
                        | MethodImportAttributes.CharSetAnsi,
                    mdBuilder.GetOrAddString(stub.Name),
                    (ModuleReferenceHandle)stub.ModuleRef);
                AssertRow(slot.PredictedRow, MetadataTokens.GetRowNumber(pinvokeH),
                    $"P/Invoke MethodDef '{stub.Name}'");
                continue;
            }

            BlobHandle sig;
            StringHandle name;
            MethodAttributes attrs;
            MethodImplAttributes impl;

            if (slot.Synth != null)
            {
                sig = slot.Synth.SignatureBlob;
                name = mdBuilder.GetOrAddString(slot.Synth.Name);
                attrs = slot.Synth.Attributes;
                impl = MethodImplAttributes.IL;
            }
            else if (slot.Method == null)
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

        byte[] pe;
        var peBuilder = new WritableDataPEBuilder(
            peHeader,
            rootBuilder,
            ilBuilder,
            fieldData: mappedFieldData,
            entryPoint: entryHandle,
            flags: CorFlags.ILOnly);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        pe = peBlob.ToArray();

        // The field data lives in our own .sdata section at SDataRva. Set each
        // FieldRVA to (SDataRva + the field's blob offset) DIRECTLY from the offsets
        // we recorded above. We deliberately do NOT infer a single rebase delta from
        // the placeholder RVAs ManagedPEBuilder produced: with mappedFieldData=null
        // those placeholders are laid out by an internal heuristic whose base shifts
        // by a file-alignment quantum once the field data crosses certain size
        // thresholds, which silently mis-addressed every literal. Patching the 32-bit
        // RVA cells changes no table size, so SDataRva is unaffected.
        if (peBuilder.SDataRva > 0 && merger.CopiedFields.Count > 0)
            FieldRvaRebaser.SetAbsolute(pe, peBuilder.SDataRva, fieldDataOffsets);
        return pe;
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
