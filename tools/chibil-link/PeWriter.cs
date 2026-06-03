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
        string exportClass = null, Dictionary<string, string> pinvokeMap = null, bool debuggable = false,
        bool shared = false, string assemblyName = "a", string entrySymbol = "main",
        List<string> libSearchPaths = null)
    {
        if (objs == null || objs.Count == 0)
            throw new LinkException("no input objects.");

        // API facade: when objects carry a .chiapi manifest and no explicit
        // --export-class was given, build a `<group>.Api` facade restricted to the
        // manifest's public functions (Plan 1: functions only; Plan 2 adds types).
        HashSet<string> apiFns = null;
        HashSet<string> apiTypes = null;
        string apiNs = null;
        List<ApiEnum> apiEnums = null;
        string effectiveExportClass = exportClass;
        if (effectiveExportClass == null)
        {
            string group = null;
            var fns = new HashSet<string>();
            var tys = new HashSet<string>();
            var seenEnumTags = new HashSet<string>();
            var ens = new List<ApiEnum>();
            foreach (var o in objs)
                if (o.Api != null)
                {
                    group ??= o.Api.Group;
                    fns.UnionWith(o.Api.Functions);
                    tys.UnionWith(o.Api.Types);
                    foreach (var e in o.Api.Enums)
                        if (seenEnumTags.Add(e.Tag))
                            ens.Add(e);
                }
            if (group != null)
            {
                apiNs = SanitizeNs(group);
                effectiveExportClass = apiNs + ".Api";
                apiFns = fns;
                apiTypes = tys;
                if (ens.Count > 0) apiEnums = ens;
            }
        }

        return new PeWriter(objs, libs ?? new List<string>(), effectiveExportClass, pinvokeMap, debuggable,
            shared, assemblyName, entrySymbol, libSearchPaths ?? new List<string>(), apiFns,
            apiTypes, apiNs, apiEnums).Write();
    }

    // Turn a header base name into a valid namespace segment (letters/digits/underscore;
    // a leading digit is prefixed with '_'). e.g. "quickjs-libc" -> "quickjs_libc".
    private static string SanitizeNs(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
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
    private readonly bool _debuggable;   // -g: emit DebuggableAttribute (JIT optimizer disabled)
    private readonly bool _shared;       // -shared: library with no entry point (no 'main' needed)
    private readonly string _assemblyName;   // assembly identity (from -o base name)
    private readonly string _entrySymbol;    // C entry symbol for an executable (-e, default 'main')
    private readonly List<string> _libSearchPaths;   // -L native-library probe dirs
    private readonly HashSet<string> _apiFunctionNames; // null = no manifest restriction
    private readonly HashSet<string> _apiTypeNames;     // null = no re-namespacing
    private readonly string _apiNamespace;
    private readonly List<ApiEnum> _apiEnums;           // null = no enum synthesis
    private int _firstForwarderRow;   // first MethodDef row owned by the export class

    public PeWriter(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null,
        Dictionary<string, string> pinvokeMap = null, bool debuggable = false,
        bool shared = false, string assemblyName = "a", string entrySymbol = "main",
        List<string> libSearchPaths = null, HashSet<string> apiFunctionNames = null,
        HashSet<string> apiTypeNames = null, string apiNamespace = null,
        List<ApiEnum> apiEnums = null)
    {
        _objs = objs;
        _libs = libs;
        _exportClass = ValidateExportClass(exportClass);
        _pinvokeMap = pinvokeMap ?? new Dictionary<string, string>();
        _debuggable = debuggable;
        _shared = shared;
        _assemblyName = string.IsNullOrEmpty(assemblyName) ? "a" : assemblyName;
        _entrySymbol = string.IsNullOrEmpty(entrySymbol) ? "main" : entrySymbol;
        _libSearchPaths = libSearchPaths ?? new List<string>();
        _apiFunctionNames = apiFunctionNames;
        _apiTypeNames = apiTypeNames;
        _apiNamespace = apiNamespace;
        _apiEnums = apiEnums;
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
        var merger = new MetadataMerger(_objs, _exportClass, _libs, _entrySymbol, _apiFunctionNames,
            _apiTypeNames, _apiNamespace, _apiEnums);
        merger.MergeAndPredict();
        merger.ApplyApiTypeNamespacing();

        // Resolve cross-object references and synthesize native P/Invoke stubs.
        // Runs after defined-method prediction (so the export table is complete)
        // and before the entry row, reserving any P/Invoke MethodDef rows in the
        // plan so the row-prediction assertions below still hold.
        SymbolResolver.Resolve(merger, _objs, _libs, _pinvokeMap, _libSearchPaths);

        // Collect FieldRVA pointer relocations (function/data pointers baked into
        // initialized globals) and, if any exist, synthesize a <Module> .cctor
        // that applies them at load time. Reserve its row BEFORE the entry row so
        // its body is encoded with the rest of the plan.
        var fieldRelocs = FieldDataRelocator.Collect(merger, _objs);
        var fieldInits = new List<(int targetRow, int sourceRow, int size)>();
        foreach (var cf in merger.CopiedFields)
            if (cf.Kind == MetadataMerger.CopiedField.FieldKind.Mutable)
                fieldInits.Add((cf.PredictedRow, cf.SourceFieldRow, cf.Size));

        // Native DATA imports are initialized from their library at load time; this
        // IL is spliced ahead of the FieldRVA relocations in the single <Module>
        // .cctor (a type may have only one). It has no trailing ret.
        byte[] dataInitIl = merger.BuildDataImportInitIl();

        MetadataMerger.SynthMethod cctorSynth = null;
        if (fieldRelocs.Count > 0 || fieldInits.Count > 0 || dataInitIl.Length > 0)
        {
            // void .cctor() — default calling convention, no params, returns void.
            var cctorSig = new BlobBuilder();
            new BlobEncoder(cctorSig)
                .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
                .Parameters(0, ret => ret.Void(), _ => { });

            // .cctor body = [data-import init] + [FieldRVA relocs ... ret], or just
            // [data-import init][ret] when there are no relocations.
            byte[] tail = (fieldRelocs.Count > 0 || fieldInits.Count > 0)
                ? FieldDataRelocator.BuildCctorIl(fieldRelocs, fieldInits)   // ends in ret
                : new byte[] { 0x2A };                                       // ret
            byte[] body = new byte[dataInitIl.Length + tail.Length];
            System.Buffer.BlockCopy(dataInitIl, 0, body, 0, dataInitIl.Length);
            System.Buffer.BlockCopy(tail, 0, body, dataInitIl.Length, tail.Length);

            cctorSynth = new MetadataMerger.SynthMethod
            {
                Name = ".cctor",
                SignatureBlob = merger.Builder.GetOrAddBlob(cctorSig),
                Il = body,
                MaxStack = 4,   // cpblk needs 3; reloc phase peaks at 3 — 4 is safe
                LocalSig = merger.BuildDataImportInitLocalSig(),
                InitLocals = dataInitIl.Length > 0,
                Attributes = MethodAttributes.Private | MethodAttributes.Static
                           | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                           | MethodAttributes.RTSpecialName,
            };
            merger.ReserveSynthRow(cctorSynth);
        }

        // Reserve the entry method's row (last in the plan) so its token is known,
        // and synthesize the entry IL (entry symbol's final token already baked).
        // A -shared library has NO entry point — skip both; the resulting PE is a
        // DLL the CLR never invokes directly (referenced from C#, run via a host).
        MethodDefinitionHandle entryHandle = default;
        EntrySynthesizer.Result entry = null;
        if (!_shared)
        {
            (_, entryHandle) = merger.ReserveEntryRow();
            entry = EntrySynthesizer.Synthesize(merger, _entrySymbol);
        }

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

            // Real methods may carry EH clauses (setjmp/longjmp filter regions);
            // synth/entry never do.
            int offset = (slot.Method != null)
                ? AddBody(bodyEncoder, il, maxStack, localSig, initLocals, slot.Method.ExceptionRegions, slot.Obj, merger)
                : AddBody(bodyEncoder, il, maxStack, localSig, initLocals);
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
                MetadataTokens.FieldDefinitionHandle(merger.TotalGlobalFieldRows + 1),  // owns no fields
                MetadataTokens.MethodDefinitionHandle(firstForwarderRow));
            AssertRow(merger.ExportTypeDefRow, MetadataTokens.GetRowNumber(exportTd), "TypeDef export class");
        }

        // ── Step 5a0b: emit named member fields for ExplicitLayout public structs ──
        // These are appended AFTER all global/RVA fields so that <Module>'s field
        // range (rows 1..G) is unaffected.  Member fields are emitted in
        // CopiedTypeDef (PredictedRow) order so each struct's range is contiguous.
        // Enum CopiedTypeDefs (synthesized by SynthesizeApiEnums) are also emitted
        // here: value__ + static literal fields, using mf.Attributes/HasLayout/IsLiteral.
        foreach (var ct in merger.CopiedTypeDefs)
        {
            bool firstMember = true;
            foreach (var mf in ct.Members)
            {
                var mfh = mdBuilder.AddFieldDefinition(
                    mf.Attributes,
                    mdBuilder.GetOrAddString(mf.Name), mf.Signature);
                if (firstMember)
                {
                    AssertRow(ct.FirstFieldRow, MetadataTokens.GetRowNumber(mfh), $"member field '{ct.Name}.{mf.Name}'");
                    firstMember = false;
                }
                if (mf.HasLayout) mdBuilder.AddFieldLayout(mfh, mf.Offset);
                if (mf.IsLiteral) mdBuilder.AddConstant(mfh, mf.LiteralValue);
            }
        }

        // ── Step 5a1: value-type TypeDefs referenced by field signatures ──────
        // Each struct TypeDef's FieldList must be monotonically non-decreasing.
        // We track a cursor starting just past the global fields.  TypeDefs with
        // named members point their FieldList at their FirstFieldRow (= the cursor
        // value when prediction assigned them their rows); member-less TypeDefs
        // inherit the current cursor so the range stays non-decreasing.
        int totalGlobalFields = merger.TotalGlobalFieldRows;
        int totalMethods = merger.Plan.Count;
        // fieldListCursor: the FieldList value for the next TypeDef that has no
        // members of its own (it points past the previous member block or past
        // the globals if no member-bearing struct has been emitted yet).
        int fieldListCursor = totalGlobalFields + 1;
        var promotedTypeDefRows = merger.ExportTypeDefRow != 0
            ? merger.BuildExportReferencedTypeRows()
            : new System.Collections.Generic.HashSet<int>();
        promotedTypeDefRows.UnionWith(merger.ApiPublicTypeRows); // manifest-declared public types
        foreach (var ct in merger.CopiedTypeDefs)
        {
            // Determine this TypeDef's FieldList start.
            int fieldListStart;
            if (ct.Members.Count > 0)
            {
                // Member-bearing struct: use the predicted first-member row.
                fieldListStart = ct.FirstFieldRow;
                // Advance cursor past this struct's members for the next TypeDef.
                fieldListCursor = fieldListStart + ct.Members.Count;
            }
            else
            {
                // No members: keep the cursor (monotonically non-decreasing).
                fieldListStart = fieldListCursor;
            }

            var tdH = mdBuilder.AddTypeDefinition(
                (ct.IsEnum
                    ? System.Reflection.TypeAttributes.AutoLayout     // enums use AutoLayout (0), NOT SequentialLayout
                    : ct.ExplicitLayout
                        ? System.Reflection.TypeAttributes.ExplicitLayout
                        : System.Reflection.TypeAttributes.SequentialLayout)
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.AnsiClass
                    | (promotedTypeDefRows.Contains(ct.PredictedRow)
                        ? System.Reflection.TypeAttributes.Public
                        : (System.Reflection.TypeAttributes)0),
                ct.Namespace.Length == 0 ? default : mdBuilder.GetOrAddString(ct.Namespace),
                mdBuilder.GetOrAddString(ct.Name),
                ct.BaseType,
                MetadataTokens.FieldDefinitionHandle(fieldListStart),
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
        // Identity follows the output base name (like `gcc -o foo`), so a C#
        // <Reference> resolves to the right assembly at runtime.
        mdBuilder.AddModule(
            0,
            mdBuilder.GetOrAddString(_assemblyName + ".dll"),
            mdBuilder.GetOrAddGuid(Guid.NewGuid()),
            default, default);

        var asmHandle = mdBuilder.AddAssembly(
            mdBuilder.GetOrAddString(_assemblyName),
            new Version(0, 0, 0, 0),
            default,
            default,
            0,
            AssemblyHashAlgorithm.Sha1);

        // -g: mark the assembly debuggable so the JIT leaves optimizations off and the
        // embedded PDB's sequence points actually bind as breakpoints. Without this
        // the JIT inlines/optimizes and VS reports "no executable code of the
        // debugger's target code type is associated with this line."
        if (_debuggable)
            merger.AddDebuggableAttribute(asmHandle);

        // ── Step 5c: serialize a pure-MSIL executable PE ──────────────────────
        var rootBuilder = new MetadataRootBuilder(mdBuilder);

        // A -shared library gets the DLL characteristic and no entry point; an
        // executable gets the standard executable header (and a CLR entry below).
        var peHeader = _shared
            ? new PEHeaderBuilder(imageCharacteristics:
                System.Reflection.PortableExecutable.Characteristics.ExecutableImage |
                System.Reflection.PortableExecutable.Characteristics.Dll)
            : PEHeaderBuilder.CreateExecutableHeader();

        // All FieldRVA data is read-only now (string literals + `$init` cpblk
        // sources; mutable C globals are plain CLR static fields), so it can ride
        // in ManagedPEBuilder's standard mappedFieldData section, which the builder
        // places and addresses itself — no custom writable .sdata section and no
        // FieldRVA rebase post-pass needed.
        // Embed a Portable PDB so the assembly is debuggable in VS with no sidecar:
        // transcode each TU's .chidbg line points to sequence points, placed at the
        // FINAL MethodDef RIDs via the merger's token map.
        var debugDir = BuildEmbeddedPdb(merger, _objs, mdBuilder, entryHandle);

        var peBuilder = new ManagedPEBuilder(
            peHeader,
            rootBuilder,
            ilBuilder,
            mappedFieldData: mappedFieldData,
            entryPoint: entryHandle,
            debugDirectoryBuilder: debugDir,
            flags: CorFlags.ILOnly);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }

    /// <summary>
    /// Build an embedded Portable PDB from the per-TU <c>.chidbg</c> side-streams.
    /// Each object's local MethodDef RID is mapped to its final RID; line points
    /// become sequence points (line-level — chibil has no column info). Returns null
    /// if no object carried debug info.
    /// </summary>
    private static DebugDirectoryBuilder BuildEmbeddedPdb(
        MetadataMerger merger, IReadOnlyList<ObjectFile> objs,
        MetadataBuilder mdBuilder, MethodDefinitionHandle entryHandle)
    {
        var byRid = new Dictionary<int, PortablePdbWriter.MethodDebug>();
        foreach (var of in objs)
        {
            if (of.Debug == null)
                continue;
            foreach (var kv in of.Debug.Methods)
            {
                int finalRid = merger.MapToken(of, 0x06000000 | kv.Key) & 0x00FFFFFF;
                if (finalRid == 0)
                    continue;
                var info = kv.Value;
                var m = new PortablePdbWriter.MethodDebug
                {
                    DocumentName = of.Debug.SourceFile,
                    Hash = of.Debug.SourceHash,
                };
                foreach (var (il, line, sc, ec) in info.Points)
                    m.SequencePoints.Add(new PortablePdbWriter.SeqPoint
                    {
                        IlOffset = il,
                        StartLine = line, EndLine = line,
                        // Real source columns from chibil (1-based). Fall back to a
                        // line-level 1..2 span when unknown so the point stays
                        // non-empty (an empty span would mark it hidden).
                        StartColumn = sc > 0 ? sc : 1,
                        EndColumn = ec > sc ? ec : (sc > 0 ? sc + 1 : 2),
                    });
                foreach (var scope in info.Scopes)
                {
                    var si = new PortablePdbWriter.ScopeInfo
                    {
                        StartOffset = scope.Start,
                        Length = scope.Length,
                    };
                    foreach (var (slot, name) in scope.Locals)
                        si.Locals.Add(new PortablePdbWriter.LocalVar { Slot = slot, Name = name });
                    m.Scopes.Add(si);
                }
                byRid[finalRid] = m;
            }
        }
        if (byRid.Count == 0)
            return null;

        var rowCounts = mdBuilder.GetRowCounts();
        int methodCount = rowCounts[(int)TableIndex.MethodDef];
        int entryRid = entryHandle.IsNil ? 0 : MetadataTokens.GetRowNumber(entryHandle);

        var (pdbBytes, pdbId) = PortablePdbWriter.Build(methodCount, byRid, rowCounts, entryRid);

        var pdbBlob = new BlobBuilder();
        pdbBlob.WriteBytes(pdbBytes);

        var dir = new DebugDirectoryBuilder();
        dir.AddCodeViewEntry("chibil.pdb", pdbId, portablePdbVersion: 0x0100);
        dir.AddEmbeddedPortablePdbEntry(pdbBlob, portablePdbVersion: 0x0100);
        return dir;
    }

    private static int AddBody(
        MethodBodyStreamEncoder encoder,
        byte[] il,
        int maxStack,
        StandaloneSignatureHandle localSig,
        bool initLocals,
        System.Collections.Immutable.ImmutableArray<System.Reflection.Metadata.ExceptionRegion> ehRegions = default,
        ObjectFile of = null,
        MetadataMerger merger = null)
    {
        int ehCount = ehRegions.IsDefaultOrEmpty ? 0 : ehRegions.Length;

        // attributeInstructions == false: we have no branch/exception fixups to
        // apply; the IL is already final. AddMethodBody reserves the blob and
        // returns its offset + a writable Instructions blob to copy IL into.
        var body = encoder.AddMethodBody(
            codeSize: il.Length,
            maxStack: maxStack,
            exceptionRegionCount: ehCount,
            hasSmallExceptionRegions: false,   // fat format: always valid
            localVariablesSignature: localSig,
            attributes: initLocals ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None);

        var writer = new BlobWriter(body.Instructions);
        writer.WriteBytes(il);

        // Re-emit each EH clause (the linker dropped these before setjmp/longjmp).
        // Offsets are IL-relative and unchanged by RelocationFixer; a Catch clause's
        // type token is remapped into the merged image.
        for (int r = 0; r < ehCount; r++)
        {
            var reg = ehRegions[r];
            switch (reg.Kind)
            {
                case System.Reflection.Metadata.ExceptionRegionKind.Filter:
                    body.ExceptionRegions.AddFilter(reg.TryOffset, reg.TryLength, reg.HandlerOffset, reg.HandlerLength, reg.FilterOffset);
                    break;
                case System.Reflection.Metadata.ExceptionRegionKind.Finally:
                    body.ExceptionRegions.AddFinally(reg.TryOffset, reg.TryLength, reg.HandlerOffset, reg.HandlerLength);
                    break;
                case System.Reflection.Metadata.ExceptionRegionKind.Fault:
                    body.ExceptionRegions.AddFault(reg.TryOffset, reg.TryLength, reg.HandlerOffset, reg.HandlerLength);
                    break;
                case System.Reflection.Metadata.ExceptionRegionKind.Catch:
                    // chibil emits only setjmp/longjmp filters; a typed catch would
                    // need its CatchType token remapped into the merged image.
                    throw new LinkException(
                        $"{of?.Path}: typed catch EH clauses are not supported (only setjmp filters).");
            }
        }
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
