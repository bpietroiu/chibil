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
    public static byte[] LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs,
        string exportClass = null, Dictionary<string, string> pinvokeMap = null)
    {
        if (objs == null || objs.Count == 0)
            throw new LinkException("no input objects.");
        return new PeWriter(objs, libs ?? new List<string>(), exportClass, pinvokeMap).Write();
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
    private readonly string _exportClass;
    private readonly Dictionary<string, string> _pinvokeMap;
    private int _firstForwarderRow;   // first MethodDef row owned by the export class

    public PeWriter(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null,
        Dictionary<string, string> pinvokeMap = null)
    {
        _objs = objs;
        _libs = libs;
        _exportClass = ValidateExportClass(exportClass);
        _pinvokeMap = pinvokeMap ?? new Dictionary<string, string>();
    }

    private static string ValidateExportClass(string name)
    {
        if (name == null) return null;
        if (name.Length == 0) throw new LinkException("--export-class name must not be empty.");
        foreach (var part in name.Split('.'))
            if (part.Length == 0)
                throw new LinkException($"--export-class '{name}' has an empty namespace/type segment.");
        return name;
    }

    public byte[] Write()
    {
        var merger = new MetadataMerger(_objs, _exportClass);
        merger.MergeAndPredict();

        // Resolve cross-object references and synthesize native P/Invoke stubs.
        // Runs after defined-method prediction (so the export table is complete)
        // and before the entry row, reserving any P/Invoke MethodDef rows in the
        // plan so the row-prediction assertions below still hold.
        SymbolResolver.Resolve(merger, _objs, _libs, _pinvokeMap);

        // Collect FieldRVA pointer relocations (function/data pointers baked into
        // initialized globals) and, if any exist, synthesize a <Module> .cctor
        // that applies them at load time. Reserve its row BEFORE the entry row so
        // its body is encoded with the rest of the plan.
        var fieldRelocs = FieldDataRelocator.Collect(merger, _objs);
        var fieldInits = new List<(int targetRow, int sourceRow, int size)>();
        foreach (var cf in merger.CopiedFields)
            if (cf.Kind == MetadataMerger.CopiedField.FieldKind.Mutable)
                fieldInits.Add((cf.PredictedRow, cf.SourceFieldRow, cf.Size));

        MetadataMerger.SynthMethod cctorSynth = null;
        if (fieldRelocs.Count > 0 || fieldInits.Count > 0)
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
                Il = FieldDataRelocator.BuildCctorIl(fieldRelocs, fieldInits),
                MaxStack = 4,   // cpblk needs 3; reloc phase peaks at 3 — 4 is safe
                Attributes = MethodAttributes.Private | MethodAttributes.Static
                           | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                           | MethodAttributes.RTSpecialName,
            };
            merger.ReserveSynthRow(cctorSynth);
        }

        // Reserve the entry method's row (last in the plan) so its token is known.
        var (_, entryHandle) = merger.ReserveEntryRow();

        // Synthesize the entry IL (main's final token already baked). The entry
        // signature uses ELEMENT_TYPE_STRING/SZARRAY primitives — no TypeRef.
        var entry = EntrySynthesizer.Synthesize(merger);

        // Reserve forwarder rows AFTER the entry so they form the contiguous tail
        // owned by the export class. Built here (post-prediction) because each
        // forwarder body calls an exported method whose final token is now known.
        var forwarders = merger.ExportTypeDefRow != 0
            ? ForwarderSynthesizer.Build(merger)
            : new List<MetadataMerger.SynthMethod>();
        _firstForwarderRow = merger.Plan.Count + 1;   // first row to be reserved next
        foreach (var fwd in forwarders)
            merger.ReserveSynthRow(fwd);

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
                // Synthesized method with prebuilt IL (FieldRVA .cctor, OS
                // intrinsic, argv helpers). LocalSig is nil unless the body
                // needs locals (the argv-marshalling loop).
                il = slot.Synth.Il;
                maxStack = slot.Synth.MaxStack;
                localSig = slot.Synth.LocalSig;
                initLocals = slot.Synth.InitLocals;
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
        // merger's predictions). The blob is handed to ManagedPEBuilder as
        // mappedFieldData; it places the bytes in a data section and rewrites the
        // FieldRVA cells to the real image RVAs itself. All such data is READ-ONLY
        // (string literals + `$init` cpblk sources), so no writable section is needed.
        var mappedFieldData = new BlobBuilder();

        // Pass 1: target fields — rows 1..N, one per CopiedField in PredictedRow order.
        // .rdata literals stay HasFieldRVA (read-only data); .data/BSS become plain
        // CLR static fields (writable on every platform), losing HasFieldRVA.
        foreach (var cf in merger.CopiedFields)
        {
            System.Reflection.Metadata.FieldDefinitionHandle fh;
            if (cf.Kind == MetadataMerger.CopiedField.FieldKind.ReadOnly)
            {
                int align = cf.Alignment <= 0 ? 1 : cf.Alignment;
                while ((mappedFieldData.Count % align) != 0) mappedFieldData.WriteByte(0);
                int dataOffset = mappedFieldData.Count;
                mappedFieldData.WriteBytes(cf.Data);
                fh = mdBuilder.AddFieldDefinition(cf.Attributes, mdBuilder.GetOrAddString(cf.Name), cf.SignatureBlob);
                mdBuilder.AddFieldRelativeVirtualAddress(fh, dataOffset);
            }
            else
            {
                // Mutable (.data) or Bss: plain CLR static field, NO HasFieldRVA / no FieldRVA row.
                var attrs = cf.Attributes & ~FieldAttributes.HasFieldRVA;
                fh = mdBuilder.AddFieldDefinition(attrs, mdBuilder.GetOrAddString(cf.Name), cf.SignatureBlob);
            }
            AssertRow(cf.PredictedRow, MetadataTokens.GetRowNumber(fh), $"Field '{cf.Name}'");
        }

        // Pass 2: read-only SOURCE fields for Mutable targets — rows N+1.. in SourceFieldRow order.
        foreach (var cf in merger.CopiedFields)
        {
            if (cf.Kind != MetadataMerger.CopiedField.FieldKind.Mutable) continue;
            int align = cf.Alignment <= 0 ? 1 : cf.Alignment;
            while ((mappedFieldData.Count % align) != 0) mappedFieldData.WriteByte(0);
            int dataOffset = mappedFieldData.Count;
            mappedFieldData.WriteBytes(cf.Data);
            var srcFh = mdBuilder.AddFieldDefinition(
                FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                mdBuilder.GetOrAddString(cf.Name + "$init"),
                cf.SignatureBlob);
            AssertRow(cf.SourceFieldRow, MetadataTokens.GetRowNumber(srcFh), $"Field '{cf.Name}$init'");
            mdBuilder.AddFieldRelativeVirtualAddress(srcFh, dataOffset);
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

        // ── Export class TypeDef (row 2), public static class owning the forwarder
        //    tail. With zero forwarders, MethodList points past the end (empty).
        if (merger.ExportTypeDefRow != 0)
        {
            int firstForwarderRow = FirstForwarderRow(merger); // = totalMethods+1 when there are no forwarders
            string full = _exportClass;
            int dot = full.LastIndexOf('.');
            string ns = dot < 0 ? "" : full[..dot];
            string nm = dot < 0 ? full : full[(dot + 1)..];
            var exportTd = mdBuilder.AddTypeDefinition(
                System.Reflection.TypeAttributes.Public
                    | System.Reflection.TypeAttributes.Abstract
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.Class
                    | System.Reflection.TypeAttributes.BeforeFieldInit,
                ns.Length == 0 ? default : mdBuilder.GetOrAddString(ns),
                mdBuilder.GetOrAddString(nm),
                merger.GetOrAddCoreObjectRef(),
                MetadataTokens.FieldDefinitionHandle(merger.TotalFieldRows + 1),  // owns no fields
                MetadataTokens.MethodDefinitionHandle(firstForwarderRow));
            AssertRow(merger.ExportTypeDefRow, MetadataTokens.GetRowNumber(exportTd), "TypeDef export class");
        }

        // ── Step 5a1: value-type TypeDefs referenced by field signatures ──────
        // These own no fields/methods, so their lists point past the end of the
        // Field/MethodDef tables (1-based, exclusive upper bound = count + 1).
        int totalFields = merger.TotalFieldRows;
        int totalMethods = merger.Plan.Count;
        var promotedTypeDefRows = merger.ExportTypeDefRow != 0
            ? merger.BuildExportReferencedTypeRows()
            : new System.Collections.Generic.HashSet<int>();
        foreach (var ct in merger.CopiedTypeDefs)
        {
            var tdH = mdBuilder.AddTypeDefinition(
                System.Reflection.TypeAttributes.SequentialLayout
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.AnsiClass
                    | (promotedTypeDefRows.Contains(ct.PredictedRow)
                        ? System.Reflection.TypeAttributes.Public
                        : (System.Reflection.TypeAttributes)0),
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
                // SetLastError=true makes the CLR atomically capture the native
                // last-error immediately after each P/Invoke, so a subsequent
                // GetLastError() reflects the call that just ran and is not clobbered
                // by GC/bookkeeping between the two transitions. We mark every stub
                // EXCEPT an explicit GetLastError import: marking GetLastError itself
                // SetLastError=true makes its IL stub overwrite the very value the
                // caller is trying to read (the CLR's post-call capture resets the
                // OS error), which breaks the C "ReadFile then GetLastError()" EOF
                // idiom in the disk VFS. So the *producers* of last-error capture it;
                // the *reader* must stay a plain PreserveSig call.
                var importAttrs = MethodImportAttributes.CallingConventionCDecl
                    | MethodImportAttributes.ExactSpelling
                    | MethodImportAttributes.CharSetAnsi;
                if (stub.Name != "GetLastError")
                    importAttrs |= MethodImportAttributes.SetLastError;
                mdBuilder.AddMethodImport(
                    pinvokeH,
                    importAttrs,
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

        // All FieldRVA data is read-only now (string literals + `$init` cpblk
        // sources; mutable C globals are plain CLR static fields), so it can ride
        // in ManagedPEBuilder's standard mappedFieldData section, which the builder
        // places and addresses itself — no custom writable .sdata section and no
        // FieldRVA rebase post-pass needed.
        var peBuilder = new ManagedPEBuilder(
            peHeader,
            rootBuilder,
            ilBuilder,
            mappedFieldData: mappedFieldData,
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

    // First MethodDef row owned by the export class. Set when forwarders are
    // reserved (just after the entry row), BEFORE any later reservation, so it
    // equals the first forwarder's predicted row. Falls back to "past the end"
    // (empty range) when there are no forwarders.
    private int FirstForwarderRow(MetadataMerger merger)
        => _firstForwarderRow != 0 ? _firstForwarderRow : merger.Plan.Count + 1;

    private static void AssertRow(int expected, int actual, string what)
    {
        if (expected != actual)
            throw new LinkException(
                $"row-prediction mismatch for {what}: predicted {expected}, got {actual}.");
    }
}
