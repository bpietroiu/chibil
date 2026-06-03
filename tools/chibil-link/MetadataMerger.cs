using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Asm2Obj;

namespace ChibilLink;

/// <summary>
/// Merges the metadata of one or more CoreCLR-target COFF objects into a single
/// shared <see cref="MetadataBuilder"/> destined for a pure-MSIL PE.
///
/// Responsibilities (Phase 0):
///   • Copy + dedup AssemblyRefs (by name) across all objects.
///   • Copy the TypeRefs referenced by surviving method signatures.
///   • Provide a single shared <c>&lt;Module&gt;</c> TypeDef that owns every method.
///   • PREDICT the final MethodDef row of every method across all objects, plus
///     the synthesized entry method (assigned last), so IL token fixup can run
///     before any MethodDef row is added. This is the ordering contract demanded
///     by System.Reflection.Metadata's PE emission (a method body must be encoded
///     before its MethodDef row, yet the body's IL holds tokens of OTHER methods'
///     final rows).
///
/// The actual MethodDef-row POPULATION (AddMethodDefinition) is performed by
/// <see cref="PeWriter"/> during PE emission, because the body offset returned by
/// MethodBodyStreamEncoder must be baked into the row. This class exposes the
/// predicted ordering (<see cref="Plan"/>) and the per-object token maps so the
/// writer can populate rows in the predicted order and assert the predictions.
///
/// Per-object phase tables (TypeRef/TypeDef/StandAloneSig/etc.) are copied here
/// eagerly so that <see cref="MapToken"/> resolves signature/IL tokens to their
/// final rows. Method rows are reserved (predicted) but added later by the writer.
/// </summary>
public sealed class MetadataMerger
{
    public readonly MetadataBuilder Builder = new();

    private readonly IReadOnlyList<ObjectFile> _objs;

    // Per object: a TokenMap that maps input handles -> output rows for that obj.
    private readonly Dictionary<ObjectFile, TokenMap> _maps = new();

    // Dedup AssemblyRefs by name -> output AssemblyReferenceHandle.
    private readonly Dictionary<string, AssemblyReferenceHandle> _assemblyRefByName = new();

    // Dedup TypeRefs across objects, keyed by (mapped resolution-scope token,
    // namespace, name) -> output TypeReferenceHandle. Two objects that reference
    // the same corlib type must share one output row.
    private readonly Dictionary<(int scope, string ns, string name), TypeReferenceHandle> _typeRefByKey = new();

    // Dedup ModuleRefs (P/Invoke native libraries) by name.
    private readonly Dictionary<string, EntityHandle> _moduleRefByName = new();

    /// <summary>The core-library AssemblyRef actually emitted (mscorlib facade
    /// kept verbatim, or remapped). Recorded for diagnostics.</summary>
    public string CoreLibReferenceUsed { get; private set; } = "mscorlib (verbatim)";

    /// <summary>Name of the synthesized C-callable OS-detection intrinsic
    /// (<c>int __chibil_os_is_windows()</c>). The C side declares this exact name;
    /// SymbolResolver matches it and the SynthMethod carries it.</summary>
    public const string OsIsWindowsIntrinsicName = "__chibil_os_is_windows";

    // Predicted emission plan: every method, in the deterministic order their
    // MethodDef rows are assigned. The synthesized entry is appended by
    // ReserveEntryRow() as the final entry (with Obj == null).
    public sealed class MethodSlot
    {
        public ObjectFile Obj;          // null => synthesized entry / P/Invoke
        public ObjMethod Method;        // null => synthesized entry / P/Invoke
        public int PredictedRow;        // 1-based MethodDef row
        public PInvokeStub PInvoke;     // non-null => synthesized native import (no body)
        public SynthMethod Synth;       // non-null => synthesized method with prebuilt IL (e.g. .cctor)
    }

    /// <summary>A fully synthesized method (name, signature, IL already built by
    /// the writer) reserved during prediction. Used for the module initializer
    /// that applies FieldRVA pointer relocations at load time.</summary>
    public sealed class SynthMethod
    {
        public string Name;
        public BlobHandle SignatureBlob;
        public byte[] Il;
        public int MaxStack;
        public MethodAttributes Attributes;
        // Optional local-variable signature for synth bodies that need locals
        // (e.g. the argv-marshalling helper's loop). Nil = no locals.
        public StandaloneSignatureHandle LocalSig;
        public bool InitLocals;
    }

    /// <summary>A synthesized P/Invoke MethodDef reserved during prediction and
    /// populated (AddMethodDefinition + AddMethodImport) by the writer in plan
    /// order. Has no body, so it is excluded from body-stream emission.</summary>
    public sealed class PInvokeStub
    {
        public string Name;
        public BlobHandle SignatureBlob;     // already rewritten into the shared heap
        public EntityHandle ModuleRef;       // native library ModuleRef
    }

    public readonly List<MethodSlot> Plan = new();

    /// <summary>Real methods marked extern-linkage (UnmanagedExport) and eligible
    /// for export (excludes the synthesized entry and the C 'main').</summary>
    public readonly List<(ObjectFile of, ObjMethod m)> ExportedMethods = new();

    private const MethodAttributes UnmanagedExportFlag = (MethodAttributes)0x0008;

    /// <summary>The single criterion for "this real method is an export forwarder":
    /// not the C entry <c>main</c>, and flagged extern-linkage (UnmanagedExport).
    /// Shared by the method-prediction loop (which builds <see cref="ExportedMethods"/>)
    /// and <see cref="ReserveExportOpaqueTypeDefs"/> (which runs BEFORE that loop, so it
    /// cannot consume the list) to keep the filter from drifting between the two.</summary>
    private bool IsExportForwarder(ObjectFile of, ObjMethod m)
    {
        if (m.Name == _entrySymbol) return false;   // the executable entry isn't an export
        if (_apiFunctionNames != null && !_apiFunctionNames.Contains(m.Name)) return false; // manifest-restricted
        var mdef = of.Md.GetMethodDefinition(m.Handle);
        return (mdef.Attributes & UnmanagedExportFlag) != 0;
    }

    // Output <Module> TypeDef is row 1; methods all hang off it.
    public const int ModuleTypeDefRow = 1;

    // Running output-row counter for predicted MethodDef rows.
    private int _outMethodRow;

    // ── Defined data fields (HasFieldRVA globals / string literals) ───────────
    // A value-type TypeDef referenced by a copied field signature (e.g.
    // $ArrayType$..., a sized array value type). Copied AFTER <Module> (row 1).
    public sealed class CopiedTypeDef
    {
        public string Name;
        public string Namespace;
        public EntityHandle BaseType;   // mapped base (System.ValueType TypeRef)
        public int LayoutSize;          // ClassLayout size (>=0 => emit)
        public int LayoutPack;          // ClassLayout packing
        public int PredictedRow;        // 1-based output TypeDef row (>=2)
        public bool ExplicitLayout;                 // true → emit ExplicitLayout + per-field offsets
        public List<MemberField> Members = new();   // named member fields (may be empty)
        public int FirstFieldRow;                   // predicted output field row of Members[0], or 0
    }

    public sealed class MemberField
    {
        public string Name;
        public BlobHandle Signature;   // output-blob field signature (tokens already mapped)
        public int Offset;             // FieldLayout offset
    }

    // A field with RVA-mapped initial data (string literal / initialized global).
    public sealed class CopiedField
    {
        public FieldAttributes Attributes;
        public string Name;
        public BlobHandle SignatureBlob;   // rewritten into shared heap
        public byte[] Data;                // initial bytes for the mapped-field-data blob
        public int Alignment;
        public int PredictedRow;           // 1-based output Field row

        // Source location of this field's initial data, for resolving the
        // pointer relocations that target/originate inside it (g_vfs's function
        // pointers, sqlite3.c's static method tables, string-pointer globals…).
        public ObjectFile SourceObj;
        public int SourceSection;          // COFF section number (1-based) the data lives in
        public int SourceOffset;           // byte offset of the data within that section
        public int Size;                   // data length in bytes

        public enum FieldKind { ReadOnly, Mutable, Bss }   // .rdata literal / .data / zero-init
        public FieldKind Kind;
        // Row of the synthesized read-only <name>$init source field, appended after
        // all target rows. VALID ONLY when Kind == Mutable; 0 (invalid as a field
        // token) for ReadOnly/Bss — never emit a token from it without checking Kind.
        public int SourceFieldRow;
    }

    public readonly List<CopiedTypeDef> CopiedTypeDefs = new();
    public readonly List<CopiedField> CopiedFields = new();

    // Name -> synthesized P/Invoke stub method token, recorded by SymbolResolver. Lets
    // a static-data relocation (which carries only a symbol name) bind a function
    // pointer to the same stub a call site would use. First write wins (one libc
    // signature per name).
    public readonly Dictionary<string, int> PInvokeStubByName = new();

    // COMMON symbols (external tentative-definition globals) collected during the
    // data-field pass, merged by name and allocated as one zero-init .bss slot each.
    private sealed class CommonSym
    {
        public int Size;
        public readonly List<(ObjectFile of, FieldDefinitionHandle fh)> Sites = new();
    }
    private readonly Dictionary<string, CommonSym> _commons = new(StringComparer.Ordinal);

    // Dedup value-type TypeDefs by (namespace, name, size) across objects.
    // Namespace is included (mirroring TypeRef dedup) so identically-named types
    // in different namespaces with the same size do not wrongly collide.
    private readonly Dictionary<(string ns, string name, int size), CopiedTypeDef> _typeDefByKey = new();

    // Synthesized fixed-size storage value types (ClassLayout == size) for Mutable
    // fields whose initialized data extent exceeds their declared struct size
    // (flexible array members / trailing padding). Cached by size so identically
    // sized fields share one type. Value is the `valuetype <T>` field signature.
    private readonly Dictionary<int, BlobHandle> _sizedStorageSig = new();

    private int _outTypeDefRow = ModuleTypeDefRow;   // row 1 = <Module>
    private int _outFieldRow;

    private readonly string _exportClass;
    private readonly HashSet<string> _apiFunctionNames; // null = no manifest restriction
    private int _exportTypeDefRow;   // 0 = no export type

    // Output TypeDef rows of synthesized empty opaque-handle value types (e.g.
    // sqlite3_stmt) created for the export surface. PeWriter unions these into
    // the set it promotes to public.
    private readonly HashSet<int> _exportOpaqueTypeRows = new();

    private readonly HashSet<string> _apiTypeNames;  // null = no re-namespacing
    private readonly string _apiNamespace;
    // Output TypeDef rows of public-API types re-namespaced into the facade namespace.
    public readonly HashSet<int> ApiPublicTypeRows = new();

    private readonly IReadOnlyList<string> _libs;
    private readonly string _entrySymbol;

    public MetadataMerger(IReadOnlyList<ObjectFile> objs, string exportClass = null,
        IReadOnlyList<string> libs = null, string entrySymbol = "main",
        HashSet<string> apiFunctionNames = null,
        HashSet<string> apiTypeNames = null, string apiNamespace = null)
    {
        _objs = objs;
        _exportClass = exportClass;
        _libs = libs;
        _entrySymbol = string.IsNullOrEmpty(entrySymbol) ? "main" : entrySymbol;
        _apiFunctionNames = apiFunctionNames;
        _apiTypeNames = apiTypeNames;
        _apiNamespace = apiNamespace;
    }

    // After the merge, move each public-API type's canonical TypeDef into the facade
    // namespace and mark it for public visibility. The linker already deduped same-named
    // value types across TUs into one CopiedTypeDef, so this is a one-row metadata edit;
    // the forwarders and <Module> methods reference it by token and are unaffected.
    public void ApplyApiTypeNamespacing()
    {
        if (_apiTypeNames == null || _apiNamespace == null) return;
        foreach (var ct in CopiedTypeDefs)
            if (ct.Namespace.Length == 0 && _apiTypeNames.Contains(ct.Name))
            {
                ct.Namespace = _apiNamespace;
                ApiPublicTypeRows.Add(ct.PredictedRow);
            }
    }

    /// <summary>A synthesized native DATA import: storage allocated for an unresolved
    /// global, initialized at module load from <see cref="Lib"/> via NativeLibrary.
    /// </summary>
    public sealed class DataImport
    {
        public string Name;
        public int FieldRow;     // output Field row of the synthesized storage
        public int Size;         // bytes to copy from the native symbol
        public string Lib;       // native library module name (e.g. libc.so.6)
    }
    public readonly List<DataImport> DataImports = new();

    /// <summary>Output TypeDef row reserved for the export class (row 2), or 0 if disabled.</summary>
    public int ExportTypeDefRow => _exportTypeDefRow;

    /// <summary>Get-or-add a deduped TypeRef to a core-library type (mscorlib scope).</summary>
    private EntityHandle GetOrAddCoreTypeRef(string ns, string name)
    {
        if (!_assemblyRefByName.TryGetValue("mscorlib", out var corlib))
            throw new LinkException($"no core-library AssemblyRef available for {ns}.{name}.");
        var key = (MetadataTokens.GetToken(corlib), ns, name);
        if (_typeRefByKey.TryGetValue(key, out var existing))
            return existing;
        var h = Builder.AddTypeReference(corlib, Builder.GetOrAddString(ns), Builder.GetOrAddString(name));
        _typeRefByKey[key] = h;
        return h;
    }

    /// <summary>TypeRef to System.Object in the core library, for the export class's base.</summary>
    public EntityHandle GetOrAddCoreObjectRef() => GetOrAddCoreTypeRef("System", "Object");

    /// <summary>Emit <c>[assembly: Debuggable(isJITTrackingEnabled: true,
    /// isJITOptimizerDisabled: true)]</c>. Disabling the JIT optimizer is what lets
    /// the embedded PDB's sequence points bind as breakpoints and keeps locals alive
    /// for the Locals/Autos window; without it VS and netcoredbg report "no
    /// executable code of the debugger's target code type is associated with this
    /// line." Gated by chibil-link's <c>-g</c>.</summary>
    public void AddDebuggableAttribute(AssemblyDefinitionHandle asm)
    {
        var dbgType = GetOrAddCoreTypeRef("System.Diagnostics", "DebuggableAttribute");
        var sig = new BlobBuilder();
        new BlobEncoder(sig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: true)
            .Parameters(2, ret => ret.Void(),
                p => { p.AddParameter().Type().Boolean(); p.AddParameter().Type().Boolean(); });
        var ctor = Builder.AddMemberReference(dbgType, Builder.GetOrAddString(".ctor"), Builder.GetOrAddBlob(sig));

        // CustomAttribute value blob: prolog 0x0001, two fixed bool args (both true),
        // then 0 named arguments.
        var ca = new BlobBuilder();
        ca.WriteUInt16(0x0001);
        ca.WriteByte(1);   // isJITTrackingEnabled = true
        ca.WriteByte(1);   // isJITOptimizerDisabled = true
        ca.WriteUInt16(0); // named-argument count
        Builder.AddCustomAttribute(asm, ctor, Builder.GetOrAddBlob(ca));
    }

    /// <summary>TypeRef to System.ValueType in the core library, for the base type
    /// of a synthesized opaque-handle value-type TypeDef.</summary>
    private EntityHandle GetOrAddCoreValueTypeRef() => GetOrAddCoreTypeRef("System", "ValueType");

    public TokenMap MapFor(ObjectFile of) => _maps[of];

    /// <summary>Map an original token from <paramref name="of"/> to its final
    /// output token. Throws <see cref="LinkException"/> on an unmapped token.</summary>
    public int MapToken(ObjectFile of, int originalToken)
    {
        if (originalToken == 0) return 0;
        var map = _maps[of];
        int mapped = ((int)((uint)originalToken & 0x70000000)) == 0x70000000
            ? map.MapUserStringToken(originalToken)
            : map.MapToken(originalToken);
        if (mapped == 0 || (mapped & 0x00FFFFFF) == 0)
            throw new LinkException(
                $"token 0x{originalToken:X8} in object '{of.Path}' has no mapping " +
                "(unresolved external symbol or resolver bug)");
        return mapped;
    }

    /// <summary>
    /// Phase 1: copy AssemblyRefs + TypeRefs for every object and predict the
    /// MethodDef rows for every real method. Must be called before any method
    /// body / row is emitted. Does NOT add MethodDef rows (the writer does that).
    /// </summary>
    public void MergeAndPredict()
    {
        foreach (var of in _objs)
            _maps[of] = new TokenMap(of.Md, Builder);

        // ── AssemblyRefs (dedup by name) ──────────────────────────────────────
        foreach (var of in _objs)
            CopyAssemblyRefs(of);

        // ── TypeRefs (copied per object; needed by method signatures) ─────────
        foreach (var of in _objs)
            CopyTypeRefs(of);

        _usesSetjmp = DetectSetjmpUsage();

        // ── Value-type TypeDefs + HasFieldRVA fields (string literals / globals)
        //    Predicted here so field/IL signatures referencing them remap, and
        //    field-data RVAs can be assigned by the writer. Must run BEFORE the
        //    StandAloneSig rewrite below so any value-type TypeDef referenced by
        //    a local-variable signature is already predicted (mapped).
        // Reserve the export class's TypeDef row (row 2) BEFORE value-type TypeDefs
        // so they shift to 3+ and the export type sits directly after <Module>.
        if (_exportClass != null)
            _exportTypeDefRow = ++_outTypeDefRow;

        foreach (var of in _objs)
            CopyDataFieldsAndTypeDefs(of);

        // Allocate one zero-init .bss slot per COMMON symbol name (external tentative
        // globals), unless a strong section-bound definition already exists. Must run
        // after every object's section-bound definitions are predicted.
        AllocateCommonSymbols();

        // Bind cross-object DATA references to their defining global's row by name
        // (the data analog of SymbolResolver's function resolution). Must run after
        // every object's local definitions are predicted, before the mutable source
        // fields are appended (so references bind to the writable target rows).
        ResolveCrossObjectFields();

        // Whatever data references remain unresolved are native DATA imports
        // (libc/termcap globals: environ, stdin/stdout/stderr, errno, …). When
        // linking against native libraries (-l), synthesize storage for each and
        // record it for load-time initialization from the library. Pure MSIL cannot
        // import native data directly, so the value is copied at module load.
        SynthesizeDataImports();

        // setjmp/longjmp carrier globals (zero-init; field order must still be open).
        if (_usesSetjmp) ReserveSetjmpCarrierFields();

        // Append read-only source fields (rows N+1..M) for each Mutable (.data)
        // global, so the .cctor can cpblk their bytes into the writable target.
        // Field-row order for global fields is final after this.
        ReserveMutableSourceFields();

        // ── Value-type TypeDefs referenced ONLY by local-variable signatures
        //    (e.g. a `char b[16]` fixed-array local with no field) — these have
        //    no HasFieldRVA field to pull them in, so ensure them here before the
        //    StandAloneSig rewrite, or their token in the local sig maps to row 0
        //    ("a valid typedef/typeref token is expected to follow VALUETYPE").
        foreach (var of in _objs)
            EnsureStandaloneSigTypeDefs(of);

        // ── Value-type TypeDefs referenced by METHOD signatures (params/return).
        //    SQLite passes structs through many APIs; a method param/return type
        //    can reference a value-type TypeDef pulled in by neither a field nor a
        //    local sig. Predict them here so the rewritten method signature maps
        //    them to a real row instead of row 0 (which corrupts <Module>).
        foreach (var of in _objs)
            EnsureMethodSigTypeDefs(of);

        // ── Value-type TypeDefs referenced by EXTERNAL-CALL (MemberRef) signatures.
        //    A function CALLED but not defined here (e.g. make_word -> WORD_DESC*,
        //    sigsetjmp -> jmp_buf) whose struct type appears in no defined method,
        //    field, or local sig is otherwise never copied; SymbolResolver then
        //    rewrites that call-site signature into a P/Invoke stub with the struct
        //    token mapping to row 0 (VALUETYPE TypeDef[0]) — a corrupt signature
        //    CoreCLR rejects, poisoning the whole assembly's load. Ensure them here.
        foreach (var of in _objs)
            EnsureMemberRefSigTypeDefs(of);

        // ── Opaque-handle TypeDefs for the export surface ─────────────────────
        //    An exported function whose signature names a forward-declared-only
        //    opaque struct (e.g. `sqlite3_stmt*` — never given a body in this
        //    build) references it via a MODULE-SCOPED TypeRef, with no matching
        //    TypeDef in the assembly. A C# consumer compiled by Roslyn against the
        //    output can't bind that TypeRef to a same-assembly type, so the
        //    forwarder method is rejected (CS0570 "not supported by the
        //    language"). Synthesize an empty PUBLIC value-type TypeDef per such
        //    opaque name so the existing module-scoped TypeRef resolves and the
        //    consumer can spell the pointer parameter type. Also required for
        //    INTERNAL correctness: a dangling opaque TypeRef in any imported call's
        //    signature makes the JIT throw InvalidProgramException. Must run AFTER all
        //    real value-type TypeDefs are reserved (so we don't duplicate a name
        //    that has a real body) and is the LAST TypeDef-reserving pass.
        ReserveOpaqueTypeDefs();

        // Assign consecutive output field rows to named member fields of ExplicitLayout
        // public structs, AFTER all TypeDef-reserving passes (including
        // EnsureMethodSigTypeDefs which is where public struct TypeDefs like MlPoint
        // are first added to CopiedTypeDefs). TypeDefs added by ReserveOpaqueTypeDefs
        // are always empty structs (no named members), so they get FirstFieldRow=0.
        // Global field rows (1..G) are already finalized; member fields start at G+1.
        ReserveMemberFields();

        // ── StandAloneSigs (local-variable sigs) ──────────────────────────────
        foreach (var of in _objs)
            CopyStandaloneSigs(of);

        // ── Predict MethodDef rows in a fixed order: per object, methods in the
        //    order they appear in ObjectFile.Methods. Entry method appended last
        //    via ReserveEntryRow().
        foreach (var of in _objs)
        {
            var map = _maps[of];
            foreach (var m in of.Methods)
            {
                _outMethodRow++;
                map.SetMethodDef(m.Handle, _outMethodRow);
                Plan.Add(new MethodSlot { Obj = of, Method = m, PredictedRow = _outMethodRow });

                if (_exportClass != null && IsExportForwarder(of, m))
                    ExportedMethods.Add((of, m));
            }
        }

        // setjmp/longjmp helpers: after the C-function rows (use the carrier field
        // tokens reserved above), before SymbolResolver maps the call sites.
        if (_usesSetjmp) ReserveSetjmpHelpers();
    }

    /// <summary>Number of parameters in a method's signature (raw count, incl. any
    /// hidden trailing va-buffer param). Used to emit the forwarder's ldarg sequence.</summary>
    public int MethodParamCount(ObjectFile of, ObjMethod m)
    {
        var def = of.Md.GetMethodDefinition(m.Handle);
        var reader = of.Md.GetBlobReader(def.Signature);
        var header = reader.ReadSignatureHeader();
        if (header.IsGeneric) reader.ReadCompressedInteger();
        // ParamCount (ECMA-335 II.23.2.1) does NOT include the vararg SENTINEL, so
        // the raw count is exactly the number of ldarg slots to forward — no skip needed.
        return reader.ReadCompressedInteger();
    }

    /// <summary>Expose the shared builder under the spec's short name <c>Md</c>.</summary>
    public MetadataBuilder Md => Builder;

    /// <summary>Dedup ModuleRef rows by native-library name. Returns the
    /// ModuleReference handle for use as a MethodImport scope.</summary>
    public EntityHandle GetOrAddModuleRef(string name)
    {
        if (_moduleRefByName.TryGetValue(name, out var existing))
            return existing;
        var h = Builder.AddModuleReference(Builder.GetOrAddString(name));
        _moduleRefByName[name] = h;
        return h;
    }

    /// <summary>Reserve (predict) the MethodDef row for a synthesized P/Invoke
    /// stub and record it in the Plan. The actual AddMethodDefinition +
    /// AddMethodImport are performed by the writer in plan order so the body
    /// offsets of bodied methods stay consistent. Returns the reserved token.</summary>
    public int ReservePInvokeRow(PInvokeStub stub)
    {
        _outMethodRow++;
        var slot = new MethodSlot { PredictedRow = _outMethodRow, PInvoke = stub };
        Plan.Add(slot);
        return MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(_outMethodRow));
    }

    /// <summary>Reserve the MethodDef row for a fully synthesized method (e.g. the
    /// FieldRVA-relocation module initializer). Returns the reserved token.</summary>
    public int ReserveSynthRow(SynthMethod synth)
    {
        _outMethodRow++;
        Plan.Add(new MethodSlot { PredictedRow = _outMethodRow, Synth = synth });
        return MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(_outMethodRow));
    }

    private int _osIsWindowsToken;

    /// <summary>Reserve a C-callable intrinsic <c>int __chibil_os_is_windows()</c> whose
    /// body is <c>call bool [mscorlib]System.OperatingSystem::IsWindows(); ret</c> (the
    /// bool result is the i4 the C <c>int</c> ABI expects). Deduped. Returns its MethodDef
    /// token. Used by the cross-OS disk VFS to pick its backend at runtime.</summary>
    public int ReserveOsIsWindowsIntrinsic()
    {
        if (_osIsWindowsToken != 0) return _osIsWindowsToken;

        var boolSig = new BlobBuilder();
        new BlobEncoder(boolSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().Boolean(), _ => { });
        var osType = GetOrAddCoreTypeRef("System", "OperatingSystem");
        var isWin = Builder.AddMemberReference(osType, Builder.GetOrAddString("IsWindows"),
            Builder.GetOrAddBlob(boolSig));

        var mSig = new BlobBuilder();
        new BlobEncoder(mSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().Int32(), _ => { });

        var il = new BlobBuilder();
        il.WriteByte(0x28);
        // `call` with a MemberRef token (0x0A...) targeting the static IsWindows() —
        // valid per ECMA-335 II.25.4; unlike the MethodDef tokens written elsewhere.
        il.WriteInt32(MetadataTokens.GetToken(isWin));
        il.WriteByte(0x2A);                                                // ret
        var synth = new SynthMethod
        {
            Name = OsIsWindowsIntrinsicName,
            SignatureBlob = Builder.GetOrAddBlob(mSig),
            Il = il.ToArray(),
            MaxStack = 1,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        };
        _osIsWindowsToken = ReserveSynthRow(synth);
        return _osIsWindowsToken;
    }

    // ── setjmp/longjmp runtime (MUSL-3) ──────────────────────────────────────
    // setjmp is lowered (in chibil) to a try/filter/handler wrap; longjmp throws a
    // stock System.Exception after stashing (buf,val,active) in three carrier
    // globals synthesized here. The setjmp filter matches on active && buf==&jb, so
    // a real exception is never mistaken for a longjmp and nested setjmps resolve.
    private bool _usesSetjmp;
    private int _ljBufRow, _ljValRow, _ljActiveRow;
    private readonly Dictionary<string, int> _setjmpHelperTokens = new(StringComparer.Ordinal);

    /// <summary>True if any object references a setjmp/longjmp runtime helper —
    /// chibil emits a call to a <c>__chibil_longjmp*</c> helper for every longjmp
    /// (set+throw) and every setjmp (the filter calls __chibil_longjmp_match).</summary>
    private bool DetectSetjmpUsage()
    {
        foreach (var of in _objs)
        {
            var md = of.Md;
            int n = md.GetTableRowCount(TableIndex.MemberRef);
            for (int r = 1; r <= n; r++)
            {
                var mr = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
                if (md.GetString(mr.Name).StartsWith("__chibil_longjmp", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Reserve the 3 zero-init carrier globals. Must run while field-row
    /// order is still open (before ReserveMutableSourceFields).</summary>
    private void ReserveSetjmpCarrierFields()
    {
        BlobHandle FieldSig(bool ptr)
        {
            var b = new BlobBuilder();
            var t = new BlobEncoder(b).FieldSignature();
            if (ptr) t.IntPtr(); else t.Int32();
            return Builder.GetOrAddBlob(b);
        }
        int AddF(string name, bool ptr, int size)
        {
            _outFieldRow++;
            CopiedFields.Add(new CopiedField
            {
                Attributes = FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                Name = name,
                SignatureBlob = FieldSig(ptr),
                Data = new byte[size],            // zero-initialized
                Alignment = size,
                PredictedRow = _outFieldRow,
                Kind = CopiedField.FieldKind.Bss,
                SourceObj = _objs[0],             // no relocations (Section 0): never matched by the relocator
                SourceSection = 0,
                SourceOffset = 0,
                Size = size,
            });
            return _outFieldRow;
        }
        _ljBufRow = AddF("__chibil_lj_buf", ptr: true, size: 8);
        _ljValRow = AddF("__chibil_lj_val", ptr: false, size: 4);
        _ljActiveRow = AddF("__chibil_lj_active", ptr: false, size: 4);
    }

    /// <summary>Reserve the setjmp/longjmp helper methods (after the carrier fields
    /// and the C-function rows, before SymbolResolver maps the call sites).</summary>
    private void ReserveSetjmpHelpers()
    {
        int tokBuf = 0x04000000 | _ljBufRow;
        int tokVal = 0x04000000 | _ljValRow;
        int tokAct = 0x04000000 | _ljActiveRow;

        var ctorSig = new BlobBuilder();
        new BlobEncoder(ctorSig).MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: true)
            .Parameters(0, ret => ret.Void(), _ => { });
        int excCtor = MetadataTokens.GetToken(Builder.AddMemberReference(
            GetOrAddCoreTypeRef("System", "Exception"),
            Builder.GetOrAddString(".ctor"), Builder.GetOrAddBlob(ctorSig)));

        void Ldsflda(BlobBuilder il, int tok) { il.WriteByte(0x7F); il.WriteInt32(tok); } // ldsflda (0x7F); 0x7C is ldflda
        BlobHandle MSig(int np, Action<ReturnTypeEncoder> ret, Action<ParametersEncoder> ps)
        {
            var b = new BlobBuilder();
            new BlobEncoder(b).MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
                .Parameters(np, ret, ps);
            return Builder.GetOrAddBlob(b);
        }
        int Helper(string name, BlobHandle sig, byte[] il, int maxStack)
        {
            int tok = ReserveSynthRow(new SynthMethod
            {
                Name = name,
                SignatureBlob = sig,
                Il = il,
                MaxStack = maxStack,
                Attributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            });
            _setjmpHelperTokens[name] = tok;
            return tok;
        }

        // void __chibil_longjmp(native int buf, int val):
        //   lj_val = val + (val==0); lj_buf = buf; lj_active = 1; throw new Exception()
        {
            var il = new BlobBuilder();
            Ldsflda(il, tokVal); il.WriteByte(0x03); il.WriteByte(0x03); il.WriteByte(0x16);
            il.WriteByte(0xFE); il.WriteByte(0x01); il.WriteByte(0x58); il.WriteByte(0x54); // ceq;add;stind.i4
            Ldsflda(il, tokBuf); il.WriteByte(0x02); il.WriteByte(0xDF);                      // ldarg.0;stind.i
            Ldsflda(il, tokAct); il.WriteByte(0x17); il.WriteByte(0x54);                      // ldc.i4.1;stind.i4
            il.WriteByte(0x73); il.WriteInt32(excCtor); il.WriteByte(0x7A);                   // newobj;throw
            Helper("__chibil_longjmp",
                MSig(2, r => r.Void(), p => { p.AddParameter().Type().IntPtr(); p.AddParameter().Type().Int32(); }),
                il.ToArray(), maxStack: 4);
        }
        // int __chibil_longjmp_match(native int buf): (active!=0) & (lj_buf==buf)
        {
            var il = new BlobBuilder();
            Ldsflda(il, tokAct); il.WriteByte(0x4A); il.WriteByte(0x16); il.WriteByte(0xFE); il.WriteByte(0x03); // ldind.i4;ldc.i4.0;cgt.un
            Ldsflda(il, tokBuf); il.WriteByte(0x4D); il.WriteByte(0x02); il.WriteByte(0xFE); il.WriteByte(0x01); // ldind.i;ldarg.0;ceq
            il.WriteByte(0x5F); il.WriteByte(0x2A);                                                              // and;ret
            Helper("__chibil_longjmp_match",
                MSig(1, r => r.Type().Int32(), p => p.AddParameter().Type().IntPtr()), il.ToArray(), maxStack: 3);
        }
        // native int __chibil_longjmp_buf(void): return lj_buf
        {
            var il = new BlobBuilder();
            Ldsflda(il, tokBuf); il.WriteByte(0x4D); il.WriteByte(0x2A);
            Helper("__chibil_longjmp_buf", MSig(0, r => r.Type().IntPtr(), _ => { }), il.ToArray(), maxStack: 1);
        }
        // int __chibil_longjmp_val(void): return lj_val
        {
            var il = new BlobBuilder();
            Ldsflda(il, tokVal); il.WriteByte(0x4A); il.WriteByte(0x2A);
            Helper("__chibil_longjmp_val", MSig(0, r => r.Type().Int32(), _ => { }), il.ToArray(), maxStack: 1);
        }
        // void __chibil_longjmp_clear(void): lj_active = 0
        {
            var il = new BlobBuilder();
            Ldsflda(il, tokAct); il.WriteByte(0x16); il.WriteByte(0x54); il.WriteByte(0x2A);
            Helper("__chibil_longjmp_clear", MSig(0, r => r.Void(), _ => { }), il.ToArray(), maxStack: 2);
        }
    }

    /// <summary>Map a <c>__chibil_longjmp*</c> call site to its synthesized helper
    /// (or 0 if the runtime was not reserved). Used by SymbolResolver.</summary>
    public int ResolveSetjmpHelper(string name) =>
        _setjmpHelperTokens.TryGetValue(name, out int tok) ? tok : 0;

    private int _argcToken;
    private int _makeArgvToken;
    private int _makeEnvpToken;
    private EntityHandle _getCmdLineArgsRef;

    /// <summary>MemberRef to <c>string[] [mscorlib]System.Environment::GetCommandLineArgs()</c>.
    /// Element [0] is the executable path and [1..] the args — exactly C's argv shape.</summary>
    private EntityHandle GetCommandLineArgsRef()
    {
        if (!_getCmdLineArgsRef.IsNil) return _getCmdLineArgsRef;
        var sig = new BlobBuilder();
        new BlobEncoder(sig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().SZArray().String(), _ => { });
        var envType = GetOrAddCoreTypeRef("System", "Environment");
        _getCmdLineArgsRef = Builder.AddMemberReference(
            envType, Builder.GetOrAddString("GetCommandLineArgs"), Builder.GetOrAddBlob(sig));
        return _getCmdLineArgsRef;
    }

    /// <summary>Reserve <c>int __chibil_argc()</c> = <c>GetCommandLineArgs().Length</c>
    /// (the C <c>argc</c>). Deduped; returns its MethodDef token.</summary>
    public int ReserveArgcHelper()
    {
        if (_argcToken != 0) return _argcToken;
        var sig = new BlobBuilder();
        new BlobEncoder(sig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().Int32(), _ => { });
        var il = new BlobBuilder();
        il.WriteByte(0x28); il.WriteInt32(MetadataTokens.GetToken(GetCommandLineArgsRef())); // call GetCommandLineArgs
        il.WriteByte(0x8E);   // ldlen
        il.WriteByte(0x69);   // conv.i4
        il.WriteByte(0x2A);   // ret
        _argcToken = ReserveSynthRow(new SynthMethod
        {
            Name = "__chibil_argc",
            SignatureBlob = Builder.GetOrAddBlob(sig),
            Il = il.ToArray(),
            MaxStack = 1,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        });
        return _argcToken;
    }

    /// <summary>Reserve <c>void* __chibil_make_argv()</c>: marshal
    /// <c>GetCommandLineArgs()</c> into a freshly malloc'd, NUL-terminated
    /// <c>char**</c> of UTF-8 C strings (a NULL terminator at index argc, per the
    /// C standard). Memory is intentionally never freed — it lives for the process.
    /// Deduped; returns its MethodDef token.</summary>
    public int ReserveMakeArgvHelper()
    {
        if (_makeArgvToken != 0) return _makeArgvToken;

        var marshalType = GetOrAddCoreTypeRef("System.Runtime.InteropServices", "Marshal");
        // native int Marshal::AllocHGlobal(int32)
        var allocSig = new BlobBuilder();
        new BlobEncoder(allocSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(1, ret => ret.Type().IntPtr(), p => p.AddParameter().Type().Int32());
        var allocRef = Builder.AddMemberReference(
            marshalType, Builder.GetOrAddString("AllocHGlobal"), Builder.GetOrAddBlob(allocSig));
        // native int Marshal::StringToCoTaskMemUTF8(string)
        var s2mSig = new BlobBuilder();
        new BlobEncoder(s2mSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(1, ret => ret.Type().IntPtr(), p => p.AddParameter().Type().String());
        var s2mRef = Builder.AddMemberReference(
            marshalType, Builder.GetOrAddString("StringToCoTaskMemUTF8"), Builder.GetOrAddBlob(s2mSig));

        int tokCmdline = MetadataTokens.GetToken(GetCommandLineArgsRef());
        int tokAlloc = MetadataTokens.GetToken(allocRef);
        int tokS2m = MetadataTokens.GetToken(s2mRef);

        // locals: [0] string[] a, [1] native int block, [2] int32 i, [3] int32 n
        var localSig = new BlobBuilder();
        var locals = new BlobEncoder(localSig).LocalVariableSignature(4);
        locals.AddVariable().Type().SZArray().String();
        locals.AddVariable().Type().IntPtr();
        locals.AddVariable().Type().Int32();
        locals.AddVariable().Type().Int32();
        var localSigHandle = Builder.AddStandaloneSignature(Builder.GetOrAddBlob(localSig));

        // See EntrySynthesizer for the byte-offset table behind the two branch
        // displacements (br.s +0x13, blt.s -0x17).
        var il = new BlobBuilder();
        il.WriteByte(0x28); il.WriteInt32(tokCmdline);  // call GetCommandLineArgs
        il.WriteByte(0x0A);                             // stloc.0   a
        il.WriteByte(0x06);                             // ldloc.0
        il.WriteByte(0x8E);                             // ldlen
        il.WriteByte(0x69);                             // conv.i4
        il.WriteByte(0x0D);                             // stloc.3   n = a.Length
        il.WriteByte(0x09);                             // ldloc.3
        il.WriteByte(0x17);                             // ldc.i4.1
        il.WriteByte(0x58);                             // add
        il.WriteByte(0x1E);                             // ldc.i4.8
        il.WriteByte(0x5A);                             // mul       (n+1)*8
        il.WriteByte(0x28); il.WriteInt32(tokAlloc);    // call AllocHGlobal
        il.WriteByte(0x0B);                             // stloc.1   block
        il.WriteByte(0x16);                             // ldc.i4.0
        il.WriteByte(0x0C);                             // stloc.2   i = 0
        il.WriteByte(0x2B); il.WriteByte(0x13);         // br.s CHK
        // LOOPBODY:
        il.WriteByte(0x07);                             // ldloc.1   block
        il.WriteByte(0x08);                             // ldloc.2   i
        il.WriteByte(0x1E);                             // ldc.i4.8
        il.WriteByte(0x5A);                             // mul       i*8
        il.WriteByte(0xD3);                             // conv.i
        il.WriteByte(0x58);                             // add       addr = block + i*8
        il.WriteByte(0x06);                             // ldloc.0   a
        il.WriteByte(0x08);                             // ldloc.2   i
        il.WriteByte(0x9A);                             // ldelem.ref  a[i]
        il.WriteByte(0x28); il.WriteInt32(tokS2m);      // call StringToCoTaskMemUTF8
        il.WriteByte(0xDF);                             // stind.i   *addr = ptr
        il.WriteByte(0x08);                             // ldloc.2
        il.WriteByte(0x17);                             // ldc.i4.1
        il.WriteByte(0x58);                             // add
        il.WriteByte(0x0C);                             // stloc.2   i++
        // CHK:
        il.WriteByte(0x08);                             // ldloc.2   i
        il.WriteByte(0x09);                             // ldloc.3   n
        il.WriteByte(0x32); il.WriteByte(0xE9);         // blt.s LOOPBODY
        // argv[n] = NULL
        il.WriteByte(0x07);                             // ldloc.1   block
        il.WriteByte(0x09);                             // ldloc.3   n
        il.WriteByte(0x1E);                             // ldc.i4.8
        il.WriteByte(0x5A);                             // mul       n*8
        il.WriteByte(0xD3);                             // conv.i
        il.WriteByte(0x58);                             // add       block + n*8
        il.WriteByte(0x16);                             // ldc.i4.0
        il.WriteByte(0xD3);                             // conv.i
        il.WriteByte(0xDF);                             // stind.i   *(block+n*8) = NULL
        il.WriteByte(0x07);                             // ldloc.1   return block
        il.WriteByte(0x2A);                             // ret

        var argvSig = new BlobBuilder();
        new BlobEncoder(argvSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().IntPtr(), _ => { });
        _makeArgvToken = ReserveSynthRow(new SynthMethod
        {
            Name = "__chibil_make_argv",
            SignatureBlob = Builder.GetOrAddBlob(argvSig),
            Il = il.ToArray(),
            MaxStack = 4,
            LocalSig = localSigHandle,
            InitLocals = true,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        });
        return _makeArgvToken;
    }

    /// <summary>Reserve <c>void* __chibil_make_envp()</c>: marshal the process
    /// environment (Environment.GetEnvironmentVariables()) into a freshly
    /// AllocHGlobal'd, NULL-terminated <c>char**</c> of UTF-8 <c>"KEY=VALUE"</c> C
    /// strings (a NULL terminator at index count, per the C standard) — the third
    /// parameter of <c>int main(int, char**, char** envp)</c>. Memory is intentionally
    /// never freed; it lives for the process. Deduped; returns its MethodDef token.</summary>
    public int ReserveMakeEnvpHelper()
    {
        if (_makeEnvpToken != 0) return _makeEnvpToken;

        var environ   = GetOrAddCoreTypeRef("System", "Environment");
        var idict     = GetOrAddCoreTypeRef("System.Collections", "IDictionary");
        var idictEnum = GetOrAddCoreTypeRef("System.Collections", "IDictionaryEnumerator");
        var ienum     = GetOrAddCoreTypeRef("System.Collections", "IEnumerator");
        var icoll     = GetOrAddCoreTypeRef("System.Collections", "ICollection");
        var strType   = GetOrAddCoreTypeRef("System", "String");
        var marshal   = GetOrAddCoreTypeRef("System.Runtime.InteropServices", "Marshal");

        EntityHandle MRef(EntityHandle parent, string name, int nParams, bool instance,
            Action<ReturnTypeEncoder> ret, Action<ParametersEncoder> ps)
        {
            var sb = new BlobBuilder();
            new BlobEncoder(sb).MethodSignature(SignatureCallingConvention.Default, 0, instance)
                .Parameters(nParams, ret, ps);
            return Builder.AddMemberReference(parent, Builder.GetOrAddString(name), Builder.GetOrAddBlob(sb));
        }

        int tokGetEnv  = MetadataTokens.GetToken(MRef(environ, "GetEnvironmentVariables", 0, false, r => r.Type().Type(idict, false), _ => { }));
        int tokCount   = MetadataTokens.GetToken(MRef(icoll, "get_Count", 0, true, r => r.Type().Int32(), _ => { }));
        int tokGetEnum = MetadataTokens.GetToken(MRef(idict, "GetEnumerator", 0, true, r => r.Type().Type(idictEnum, false), _ => { }));
        int tokMove    = MetadataTokens.GetToken(MRef(ienum, "MoveNext", 0, true, r => r.Type().Boolean(), _ => { }));
        int tokKey     = MetadataTokens.GetToken(MRef(idictEnum, "get_Key", 0, true, r => r.Type().Object(), _ => { }));
        int tokVal     = MetadataTokens.GetToken(MRef(idictEnum, "get_Value", 0, true, r => r.Type().Object(), _ => { }));
        int tokConcat  = MetadataTokens.GetToken(MRef(strType, "Concat", 3, false, r => r.Type().String(),
                            p => { p.AddParameter().Type().Object(); p.AddParameter().Type().Object(); p.AddParameter().Type().Object(); }));
        int tokAlloc   = MetadataTokens.GetToken(MRef(marshal, "AllocHGlobal", 1, false, r => r.Type().IntPtr(), p => p.AddParameter().Type().Int32()));
        int tokS2m     = MetadataTokens.GetToken(MRef(marshal, "StringToCoTaskMemUTF8", 1, false, r => r.Type().IntPtr(), p => p.AddParameter().Type().String()));
        int tokEq      = MetadataTokens.GetToken(Builder.GetOrAddUserString("="));

        // locals: [0] IDictionary d, [1] native int block, [2] int32 i, [3] int32 n,
        //         [4] IDictionaryEnumerator e
        var localSig = new BlobBuilder();
        var locals = new BlobEncoder(localSig).LocalVariableSignature(5);
        locals.AddVariable().Type().Type(idict, false);
        locals.AddVariable().Type().IntPtr();
        locals.AddVariable().Type().Int32();
        locals.AddVariable().Type().Int32();
        locals.AddVariable().Type().Type(idictEnum, false);
        var localSigHandle = Builder.AddStandaloneSignature(Builder.GetOrAddBlob(localSig));

        // Offsets verified by hand: br.s CHK = +0x28 (CHK at 0x4C from 0x24);
        // brtrue.s LOOPBODY = -0x31 (0x24 from 0x55) = 0xCF.
        var il = new BlobBuilder();
        il.WriteByte(0x28); il.WriteInt32(tokGetEnv);  // call GetEnvironmentVariables -> IDictionary
        il.WriteByte(0x0A);                            // stloc.0   d
        il.WriteByte(0x06);                            // ldloc.0
        il.WriteByte(0x6F); il.WriteInt32(tokCount);   // callvirt get_Count
        il.WriteByte(0x0D);                            // stloc.3   n
        il.WriteByte(0x09);                            // ldloc.3
        il.WriteByte(0x17);                            // ldc.i4.1
        il.WriteByte(0x58);                            // add
        il.WriteByte(0x1E);                            // ldc.i4.8
        il.WriteByte(0x5A);                            // mul       (n+1)*8
        il.WriteByte(0x28); il.WriteInt32(tokAlloc);   // call AllocHGlobal
        il.WriteByte(0x0B);                            // stloc.1   block
        il.WriteByte(0x06);                            // ldloc.0
        il.WriteByte(0x6F); il.WriteInt32(tokGetEnum); // callvirt GetEnumerator
        il.WriteByte(0x13); il.WriteByte(0x04);        // stloc.s 4 e
        il.WriteByte(0x16);                            // ldc.i4.0
        il.WriteByte(0x0C);                            // stloc.2   i = 0
        il.WriteByte(0x2B); il.WriteByte(0x28);        // br.s CHK
        // LOOPBODY (0x24): *(block + i*8) = StringToCoTaskMemUTF8(Concat(e.Key, "=", e.Value))
        il.WriteByte(0x07);                            // ldloc.1   block
        il.WriteByte(0x08);                            // ldloc.2   i
        il.WriteByte(0x1E);                            // ldc.i4.8
        il.WriteByte(0x5A);                            // mul       i*8
        il.WriteByte(0xD3);                            // conv.i
        il.WriteByte(0x58);                            // add       addr
        il.WriteByte(0x11); il.WriteByte(0x04);        // ldloc.s 4 e
        il.WriteByte(0x6F); il.WriteInt32(tokKey);     // callvirt get_Key -> object
        il.WriteByte(0x72); il.WriteInt32(tokEq);      // ldstr "="
        il.WriteByte(0x11); il.WriteByte(0x04);        // ldloc.s 4 e
        il.WriteByte(0x6F); il.WriteInt32(tokVal);     // callvirt get_Value -> object
        il.WriteByte(0x28); il.WriteInt32(tokConcat);  // call Concat(object,object,object)
        il.WriteByte(0x28); il.WriteInt32(tokS2m);     // call StringToCoTaskMemUTF8
        il.WriteByte(0xDF);                            // stind.i   *addr = ptr
        il.WriteByte(0x08);                            // ldloc.2
        il.WriteByte(0x17);                            // ldc.i4.1
        il.WriteByte(0x58);                            // add
        il.WriteByte(0x0C);                            // stloc.2   i++
        // CHK (0x4C):
        il.WriteByte(0x11); il.WriteByte(0x04);        // ldloc.s 4 e
        il.WriteByte(0x6F); il.WriteInt32(tokMove);    // callvirt MoveNext
        il.WriteByte(0x2D); il.WriteByte(0xCF);        // brtrue.s LOOPBODY
        // envp[n] = NULL
        il.WriteByte(0x07);                            // ldloc.1   block
        il.WriteByte(0x09);                            // ldloc.3   n
        il.WriteByte(0x1E);                            // ldc.i4.8
        il.WriteByte(0x5A);                            // mul
        il.WriteByte(0xD3);                            // conv.i
        il.WriteByte(0x58);                            // add
        il.WriteByte(0x16);                            // ldc.i4.0
        il.WriteByte(0xD3);                            // conv.i
        il.WriteByte(0xDF);                            // stind.i   *(block+n*8) = NULL
        il.WriteByte(0x07);                            // ldloc.1   return block
        il.WriteByte(0x2A);                            // ret

        var envpSig = new BlobBuilder();
        new BlobEncoder(envpSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(0, ret => ret.Type().IntPtr(), _ => { });
        _makeEnvpToken = ReserveSynthRow(new SynthMethod
        {
            Name = "__chibil_make_envp",
            SignatureBlob = Builder.GetOrAddBlob(envpSig),
            Il = il.ToArray(),
            MaxStack = 4,
            LocalSig = localSigHandle,
            InitLocals = true,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        });
        return _makeEnvpToken;
    }

    /// <summary>
    /// Synthesize an adapter bridging a Layer-2 (cdecl) call site to a Layer-1
    /// (va-buffer) chibil-defined variadic. The adapter has the CALL-SITE signature
    /// (<paramref name="mrSig"/>, rewritten into the shared heap), packs its variadic
    /// arguments (those past the <paramref name="nFixed"/> fixed params) into a
    /// localloc'd 8-byte-slot buffer, then calls the definition
    /// (<paramref name="definedToken"/>) with (fixed args…, va-buffer pointer). A
    /// zero-vararg call just passes a null buffer. Returns the adapter MethodDef token.
    /// </summary>
    public int ReserveVariadicAdapter(ObjectFile of, BlobHandle mrSig, int definedToken, int nFixed)
    {
        // Adapter's own signature = the (rewritten) call-site signature.
        var sigB = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(of.Md.GetBlobReader(mrSig), _maps[of], sigB);
        var sigBlob = Builder.GetOrAddBlob(sigB);

        // Parse the call-site signature: param count and a per-param stind opcode.
        var r = of.Md.GetBlobReader(mrSig);
        var hdr = r.ReadSignatureHeader();
        if (hdr.IsGeneric) r.ReadCompressedInteger();
        int paramCount = r.ReadCompressedInteger();
        SkipTypeReturnStind(ref r);                       // return type (advance only)
        var stind = new byte[paramCount];
        for (int i = 0; i < paramCount; i++) stind[i] = SkipTypeReturnStind(ref r);
        int nVar = paramCount - nFixed;
        if (nVar < 0) nVar = 0;

        var il = new BlobBuilder();
        if (nVar > 0)
        {
            il.WriteByte(0x20); il.WriteInt32(8 * nVar);  // ldc.i4 (8*nVar)
            il.WriteByte(0xFE); il.WriteByte(0x0F);        // localloc
            il.WriteByte(0x0A);                            // stloc.0   (va-buffer base)
            for (int i = 0; i < nVar; i++)
            {
                il.WriteByte(0x06);                        // ldloc.0   (base)
                if (i > 0)
                {
                    il.WriteByte(0x20); il.WriteInt32(i * 8); // ldc.i4 i*8
                    il.WriteByte(0xD3);                    // conv.i
                    il.WriteByte(0x58);                    // add  -> slot addr
                }
                il.WriteByte(0x0E); il.WriteByte((byte)(nFixed + i)); // ldarg.s <vararg>
                il.WriteByte(stind[nFixed + i]);           // stind.<kind>  (*slot = value)
            }
        }
        for (int j = 0; j < nFixed; j++) { il.WriteByte(0x0E); il.WriteByte((byte)j); } // ldarg.s <fixed>
        if (nVar > 0) il.WriteByte(0x06);                  // ldloc.0  (buffer ptr)
        else { il.WriteByte(0x16); il.WriteByte(0xE0); }   // ldc.i4.0; conv.u  (null __va)
        il.WriteByte(0x28); il.WriteInt32(definedToken);   // call <def>
        il.WriteByte(0x2A);                                // ret

        StandaloneSignatureHandle localSig = default;
        if (nVar > 0)
        {
            var ls = new BlobBuilder();
            new BlobEncoder(ls).LocalVariableSignature(1).AddVariable().Type().IntPtr();
            localSig = Builder.AddStandaloneSignature(Builder.GetOrAddBlob(ls));
        }

        return ReserveSynthRow(new SynthMethod
        {
            Name = "__chibil_va_adapter",
            SignatureBlob = sigBlob,
            Il = il.ToArray(),
            MaxStack = System.Math.Max(nFixed + 2, 3),
            LocalSig = localSig,
            InitLocals = nVar > 0,
            Attributes = System.Reflection.MethodAttributes.Public
                       | System.Reflection.MethodAttributes.Static
                       | System.Reflection.MethodAttributes.HideBySig,
        });
    }

    /// <summary>Advance <paramref name="r"/> past one signature Type and return the
    /// <c>stind</c> opcode for storing a value of that type into a va-buffer slot.</summary>
    private static byte SkipTypeReturnStind(ref BlobReader r)
    {
        byte et = r.ReadByte();
        while (et == 0x1F || et == 0x20) { r.ReadCompressedInteger(); et = r.ReadByte(); } // CMOD_REQD/OPT
        switch (et)
        {
            case 0x0F: case 0x10: case 0x45: case 0x1D: // PTR, BYREF, PINNED, SZARRAY
                SkipTypeReturnStind(ref r); return 0xDF;   // stind.i
            case 0x11: case 0x12: case 0x13: case 0x1E:    // VALUETYPE, CLASS, VAR, MVAR
                r.ReadCompressedInteger(); return 0xDF;
            case 0x14:                                     // ARRAY <Type><shape>
                SkipTypeReturnStind(ref r);
                r.ReadCompressedInteger();
                int sz = r.ReadCompressedInteger(); for (int i = 0; i < sz; i++) r.ReadCompressedInteger();
                int lo = r.ReadCompressedInteger(); for (int i = 0; i < lo; i++) r.ReadCompressedSignedInteger();
                return 0xDF;
            case 0x15:                                     // GENERICINST <Type><argc><args>
                SkipTypeReturnStind(ref r);
                int ac = r.ReadCompressedInteger(); for (int i = 0; i < ac; i++) SkipTypeReturnStind(ref r);
                return 0xDF;
            case 0x1B:                                     // FNPTR <MethodSig>
                var h2 = r.ReadSignatureHeader(); if (h2.IsGeneric) r.ReadCompressedInteger();
                int pc = r.ReadCompressedInteger(); SkipTypeReturnStind(ref r);
                for (int i = 0; i < pc; i++) SkipTypeReturnStind(ref r);
                return 0xDF;
            case 0x02: case 0x03: case 0x04: case 0x05:
            case 0x06: case 0x07: case 0x08: case 0x09:
                return 0x54;                               // stind.i4
            case 0x0A: case 0x0B: return 0x55;             // stind.i8
            case 0x0C: return 0x56;                        // stind.r4
            case 0x0D: return 0x57;                        // stind.r8
            default: return 0xDF;                          // STRING/I/U/OBJECT/VOID/… -> stind.i
        }
    }

    /// <summary>Reserve the MethodDef row for the synthesized entry method.
    /// Returns the predicted row (and handle). Call exactly once, after
    /// MergeAndPredict, before emitting bodies.</summary>
    public (int row, MethodDefinitionHandle handle) ReserveEntryRow()
    {
        _outMethodRow++;
        Plan.Add(new MethodSlot { Obj = null, Method = null, PredictedRow = _outMethodRow });
        return (_outMethodRow, MetadataTokens.MethodDefinitionHandle(_outMethodRow));
    }

    private void CopyAssemblyRefs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.AssemblyRef); r++)
        {
            var inH = MetadataTokens.AssemblyReferenceHandle(r);
            var ar = md.GetAssemblyReference(inH);
            string name = md.GetString(ar.Name);

            if (_assemblyRefByName.TryGetValue(name, out var existing))
            {
                map.SetAssemblyRef(inH, MetadataTokens.GetRowNumber(existing));
                continue;
            }

            AssemblyReferenceHandle outH = AddAssemblyRef(of, ar, name);
            _assemblyRefByName[name] = outH;
            map.SetAssemblyRef(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    private AssemblyReferenceHandle AddAssemblyRef(ObjectFile of, AssemblyReference ar, string name)
    {
        var md = of.Md;
        // Phase 0: copy the AssemblyRef verbatim. CoreCLR ships an mscorlib
        // facade that forwards to System.Private.CoreLib, so an in-process load
        // resolves the verbatim mscorlib reference. (If a future input needs a
        // System.Runtime remap, branch here.)
        if (name == "mscorlib")
            CoreLibReferenceUsed = "mscorlib (verbatim facade)";

        return Builder.AddAssemblyReference(
            Builder.GetOrAddString(name),
            ar.Version,
            ar.Culture.IsNil ? default : Builder.GetOrAddString(md.GetString(ar.Culture)),
            ar.PublicKeyOrToken.IsNil ? default : Builder.GetOrAddBlob(md.GetBlobBytes(ar.PublicKeyOrToken)),
            ar.Flags,
            ar.HashValue.IsNil ? default : Builder.GetOrAddBlob(md.GetBlobBytes(ar.HashValue)));
    }

    private void CopyTypeRefs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.TypeRef); r++)
        {
            var inH = MetadataTokens.TypeReferenceHandle(r);
            var tr = md.GetTypeReference(inH);
            EntityHandle outScope = tr.ResolutionScope.IsNil
                ? default
                : map.MapEntity(tr.ResolutionScope);
            string ns = md.GetString(tr.Namespace);
            string name = md.GetString(tr.Name);

            var key = (outScope.IsNil ? 0 : MetadataTokens.GetToken(outScope), ns, name);
            if (_typeRefByKey.TryGetValue(key, out var existing))
            {
                map.SetTypeRef(inH, MetadataTokens.GetRowNumber(existing));
                continue;
            }

            var outH = Builder.AddTypeReference(
                outScope,
                Builder.GetOrAddString(ns),
                Builder.GetOrAddString(name));
            _typeRefByKey[key] = outH;
            map.SetTypeRef(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    private void CopyStandaloneSigs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.StandAloneSig); r++)
        {
            var inH = MetadataTokens.StandaloneSignatureHandle(r);
            var ss = md.GetStandaloneSignature(inH);
            var sigReader = md.GetBlobReader(ss.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteStandaloneSignatureBlob(sigReader, map, sigBuilder);
            var outH = Builder.AddStandaloneSignature(Builder.GetOrAddBlob(sigBuilder));
            map.SetStandaloneSig(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    /// <summary>
    /// Copy every HasFieldRVA field (string literals / initialized globals)
    /// defined in <paramref name="of"/>, along with the value-type TypeDefs its
    /// signature references, predicting their output rows and capturing the
    /// initial data bytes for the mapped-field-data blob the writer emits.
    /// Only fields whose data lives in a real section are copied (a BSS/common
    /// global with no initializer is not a data symbol and is out of scope).
    /// </summary>
    /// <summary>
    /// Allocate storage for COMMON symbols — external tentative-definition globals
    /// (<c>int g;</c> at file scope) that COFF emits as Sect=0/Value=size rather than
    /// a section-bound slot. Real linkers merge all commons of a name into one
    /// zero-initialized .bss object sized to the largest declaration. Each gets one
    /// <see cref="CopiedField"/> (Bss), and every site's FieldDef is mapped to it. A
    /// name that already has a strong section-bound definition is left to that
    /// definition (its sites resolve by name in <see cref="ResolveCrossObjectFields"/>).
    /// </summary>
    private void AllocateCommonSymbols()
    {
        var strongDef = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cf in CopiedFields) strongDef.Add(cf.Name);

        foreach (var (name, ci) in _commons)
        {
            if (strongDef.Contains(name)) continue;   // strong def wins; refs resolve by name

            var (of0, fh0) = ci.Sites[0];
            var md0 = of0.Md;
            var fd0 = md0.GetFieldDefinition(fh0);
            EnsureFieldTypeDefs(of0, fd0);

            var sigReader = md0.GetBlobReader(fd0.Signature);
            var sigB = new BlobBuilder();
            EcmaSignatureRewriter.RewriteFieldSignature(sigReader, _maps[of0], sigB);

            _outFieldRow++;
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd0.Attributes,
                Name = name,
                SignatureBlob = Builder.GetOrAddBlob(sigB),
                Data = new byte[ci.Size],            // zero-initialized
                Alignment = GetFieldDataAlignment(md0, fd0, ci.Size),
                PredictedRow = _outFieldRow,
                Kind = CopiedField.FieldKind.Bss,
                SourceObj = of0,
                SourceSection = 0,                   // no backing section (zero-init)
                SourceOffset = 0,
                Size = ci.Size,
            });

            // Point every declaration site (across all objects) at the one slot.
            foreach (var (of, fh) in ci.Sites)
                _maps[of].SetField(fh, _outFieldRow);
        }
    }

    /// <summary>
    /// Build the IL that initializes every synthesized <see cref="DataImports"/>
    /// slot from its native library at module load: for each, look up the symbol's
    /// address via <c>NativeLibrary.TryGetExport</c> and, if found, <c>cpblk</c> the
    /// value into the slot. Returns IL with NO trailing <c>ret</c> (the caller splices
    /// it into the &lt;Module&gt; .cctor); empty if there are no data imports. Uses one
    /// local (the out IntPtr address); see <see cref="BuildDataImportInitLocalSig"/>.
    /// A symbol missing from the library (e.g. glibc-only program_invocation_name on
    /// musl) is simply left zero.
    /// </summary>
    public byte[] BuildDataImportInitIl()
    {
        if (DataImports.Count == 0) return System.Array.Empty<byte>();

        var nl = GetOrAddNativeLibraryTypeRef();
        // native int NativeLibrary::Load(string)
        var loadSig = new BlobBuilder();
        new BlobEncoder(loadSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(1, ret => ret.Type().IntPtr(), p => p.AddParameter().Type().String());
        int loadTok = MetadataTokens.GetToken(Builder.AddMemberReference(
            nl, Builder.GetOrAddString("Load"), Builder.GetOrAddBlob(loadSig)));
        // bool NativeLibrary::TryGetExport(native int, string, native int&)
        var tgeSig = new BlobBuilder();
        new BlobEncoder(tgeSig)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(3, ret => ret.Type().Boolean(), p =>
            {
                p.AddParameter().Type().IntPtr();
                p.AddParameter().Type().String();
                p.AddParameter().Type(isByRef: true).IntPtr();
            });
        int tgeTok = MetadataTokens.GetToken(Builder.AddMemberReference(
            nl, Builder.GetOrAddString("TryGetExport"), Builder.GetOrAddBlob(tgeSig)));

        var il = new BlobBuilder();
        foreach (var di in DataImports)
        {
            // The conditional body to skip when the symbol is absent: copy `Size`
            // bytes from the resolved address (local 0) into the slot's field.
            var body = new BlobBuilder();
            body.WriteByte(0x7F); body.WriteInt32(0x04000000 | di.FieldRow);   // ldsflda <field>
            body.WriteByte(0x06);                                              // ldloc.0  (src addr)
            if (di.Size <= 127) { body.WriteByte(0x1F); body.WriteByte((byte)di.Size); } // ldc.i4.s
            else { body.WriteByte(0x20); body.WriteInt32(di.Size); }          // ldc.i4
            body.WriteByte(0xFE); body.WriteByte(0x12); body.WriteByte(0x01); // unaligned. 1
            body.WriteByte(0xFE); body.WriteByte(0x17);                       // cpblk
            byte[] bodyBytes = body.ToArray();

            il.WriteByte(0x72); il.WriteInt32(MetadataTokens.GetToken(Builder.GetOrAddUserString(di.Lib)));  // ldstr lib
            il.WriteByte(0x28); il.WriteInt32(loadTok);                       // call NativeLibrary.Load
            il.WriteByte(0x72); il.WriteInt32(MetadataTokens.GetToken(Builder.GetOrAddUserString(di.Name))); // ldstr name
            il.WriteByte(0x12); il.WriteByte(0x00);                           // ldloca.s 0  (&addr)
            il.WriteByte(0x28); il.WriteInt32(tgeTok);                        // call TryGetExport
            il.WriteByte(0x2C); il.WriteByte((byte)bodyBytes.Length);         // brfalse.s past body
            il.WriteBytes(bodyBytes);
        }
        return il.ToArray();
    }

    private AssemblyReferenceHandle _systemRuntimeRef;
    private EntityHandle _nativeLibraryTypeRef;

    /// <summary>TypeRef to <c>System.Runtime.InteropServices.NativeLibrary</c>. The
    /// mscorlib/System.Runtime facades do NOT forward NativeLibrary (unlike
    /// OperatingSystem/Marshal), so it is referenced directly from
    /// System.Private.CoreLib where the type is actually defined. CoreCLR binds its
    /// loaded core library regardless of the ref version.</summary>
    private EntityHandle GetOrAddNativeLibraryTypeRef()
    {
        if (!_nativeLibraryTypeRef.IsNil) return _nativeLibraryTypeRef;
        if (_systemRuntimeRef.IsNil)
        {
            if (!_assemblyRefByName.TryGetValue("System.Private.CoreLib", out _systemRuntimeRef))
            {
                byte[] pkt = { 0x7C, 0xEC, 0x85, 0xD7, 0xBE, 0xA7, 0x79, 0x8E }; // 7cec85d7bea7798e
                _systemRuntimeRef = Builder.AddAssemblyReference(
                    Builder.GetOrAddString("System.Private.CoreLib"),
                    new System.Version(10, 0, 0, 0),
                    default,
                    Builder.GetOrAddBlob(pkt),
                    default,    // AssemblyFlags: token form, not a full public key
                    default);
                _assemblyRefByName["System.Private.CoreLib"] = _systemRuntimeRef;
            }
        }
        _nativeLibraryTypeRef = Builder.AddTypeReference(_systemRuntimeRef,
            Builder.GetOrAddString("System.Runtime.InteropServices"),
            Builder.GetOrAddString("NativeLibrary"));
        return _nativeLibraryTypeRef;
    }

    /// <summary>Local-variable signature for the data-import initializer: a single
    /// <c>native int</c> (the out address). Nil if there are no data imports.</summary>
    public StandaloneSignatureHandle BuildDataImportInitLocalSig()
    {
        if (DataImports.Count == 0) return default;
        var sig = new BlobBuilder();
        var locals = new BlobEncoder(sig).LocalVariableSignature(1);
        locals.AddVariable().Type().IntPtr();
        return Builder.AddStandaloneSignature(Builder.GetOrAddBlob(sig));
    }

    /// <summary>
    /// Synthesize storage for native DATA imports — the data analog of
    /// <see cref="SymbolResolver"/>'s P/Invoke synthesis. Any field reference still
    /// unmapped after cross-object resolution and common allocation is an external
    /// global with no definition in the link (environ, stdin/stdout/stderr, errno,
    /// optarg, termcap BC/PC/UP, …). MSIL has no native-data-import facility, so we
    /// allocate a zero-init slot, map every reference to it, and record a
    /// <see cref="DataImport"/> so the writer's module initializer can copy the
    /// value from the native library at load time. Gated on <c>-l</c>: a
    /// self-contained link still errors on genuinely-unresolved data.
    /// </summary>
    private void SynthesizeDataImports()
    {
        if (_libs == null || _libs.Count == 0) return;
        string lib = SymbolResolver.MapLib(_libs[0]);

        // Group still-unmapped field references by name (first site drives the
        // signature/size; every site is then pointed at the one slot).
        var firstSite = new Dictionary<string, (ObjectFile of, FieldDefinitionHandle fh)>(StringComparer.Ordinal);
        var sites = new Dictionary<string, List<(ObjectFile of, FieldDefinitionHandle fh)>>(StringComparer.Ordinal);
        foreach (var of in _objs)
        {
            var md = of.Md;
            var map = _maps[of];
            int rows = md.GetTableRowCount(TableIndex.Field);
            for (int r = 1; r <= rows; r++)
            {
                var fh = MetadataTokens.FieldDefinitionHandle(r);
                if (map.MapField(fh).RowId() != 0) continue;   // already resolved
                string name = md.GetString(md.GetFieldDefinition(fh).Name);
                // Skip compiler artifacts (padding members, string literals).
                if (string.IsNullOrEmpty(name) || name.IndexOf(' ') >= 0 ||
                    name.StartsWith("?") || name.StartsWith("$")) continue;
                if (!sites.TryGetValue(name, out var lst))
                {
                    lst = new List<(ObjectFile, FieldDefinitionHandle)>();
                    sites[name] = lst;
                    firstSite[name] = (of, fh);
                }
                lst.Add((of, fh));
            }
        }

        foreach (var (name, first) in firstSite)
        {
            var (of0, fh0) = first;
            var md0 = of0.Md;
            var fd0 = md0.GetFieldDefinition(fh0);
            EnsureFieldTypeDefs(of0, fd0);
            int size = GetFieldDataSize(md0, fd0);

            var sigReader = md0.GetBlobReader(fd0.Signature);
            var sigB = new BlobBuilder();
            EcmaSignatureRewriter.RewriteFieldSignature(sigReader, _maps[of0], sigB);

            _outFieldRow++;
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd0.Attributes,
                Name = name,
                SignatureBlob = Builder.GetOrAddBlob(sigB),
                Data = new byte[size],
                Alignment = GetFieldDataAlignment(md0, fd0, size),
                PredictedRow = _outFieldRow,
                Kind = CopiedField.FieldKind.Bss,
                SourceObj = of0,
                SourceSection = 0,
                SourceOffset = 0,
                Size = size,
            });
            foreach (var (of, fh) in sites[name])
                _maps[of].SetField(fh, _outFieldRow);

            DataImports.Add(new DataImport { Name = name, FieldRow = _outFieldRow, Size = size, Lib = lib });
        }
    }

    /// <summary>
    /// Cross-object DATA symbol resolution — the data analog of
    /// <see cref="SymbolResolver"/>'s function resolution. A global defined in TU A
    /// and referenced from TU B is emitted in B as a FieldDef with no storage (no
    /// HasFieldRVA / no section), which <see cref="CopyDataFieldsAndTypeDefs"/>
    /// never maps. Bind each such unmapped FieldDef to the output row of the matching
    /// DEFINED global by NAME. Names defined in no object (libc data: environ, stdin,
    /// optarg, …) stay unmapped and surface later as the data-import frontier.
    /// </summary>
    private void ResolveCrossObjectFields()
    {
        // name -> output field row for every DEFINED (copied) global. Last def wins,
        // mirroring the function table's COMDAT-ish policy.
        var definedField = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cf in CopiedFields)
            definedField[cf.Name] = cf.PredictedRow;

        foreach (var of in _objs)
        {
            var md = of.Md;
            var map = _maps[of];
            int rows = md.GetTableRowCount(TableIndex.Field);
            for (int r = 1; r <= rows; r++)
            {
                var fh = MetadataTokens.FieldDefinitionHandle(r);
                if (map.MapField(fh).RowId() != 0) continue;   // already a local definition
                string name = md.GetString(md.GetFieldDefinition(fh).Name);
                if (definedField.TryGetValue(name, out int outRow))
                    map.SetField(fh, outRow);                  // ref -> defining global's row
            }
        }
    }

    private void CopyDataFieldsAndTypeDefs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        var dataLoc = of.Coff.BuildFieldDataLocationMap();

        for (int r = 1; r <= md.GetTableRowCount(TableIndex.Field); r++)
        {
            var fh = MetadataTokens.FieldDefinitionHandle(r);
            var fd = md.GetFieldDefinition(fh);
            if ((fd.Attributes & FieldAttributes.HasFieldRVA) == 0) continue;

            int token = MetadataTokens.GetToken(fh);
            if (!dataLoc.TryGetValue(token, out var loc))
                continue; // pure extern declaration — no storage; resolved by name later.
            if (loc.SectionNumber <= 0)
            {
                // COMMON symbol (external tentative definition, e.g. `int g;`):
                // Sect=0 with the size carried in Offset/Value. Real linkers merge
                // these by name into one zero-init .bss slot (size = max). Collect
                // now; AllocateCommonSymbols allocates after all section-bound
                // definitions are known (a strong def, if any, wins).
                if (loc.Offset > 0)
                {
                    string cn = md.GetString(fd.Name);
                    if (!_commons.TryGetValue(cn, out var ci)) { ci = new CommonSym(); _commons[cn] = ci; }
                    ci.Size = Math.Max(ci.Size, loc.Offset);
                    ci.Sites.Add((of, fh));
                }
                continue;
            }

            // Ensure the value-type TypeDef(s) referenced by the field signature
            // are copied first, so the rewritten signature resolves.
            try { EnsureFieldTypeDefs(of, fd); }
            catch (Exception ex)
            {
                throw new LinkException(
                    $"field '{md.GetString(fd.Name)}' (row {r}, sigBlob 0x{MetadataTokens.GetHeapOffset(fd.Signature):X}) FieldRVA processing failed: {ex.Message}");
            }

            int typeSize = GetFieldDataSize(md, fd);
            var sec = of.Coff.GetSection(loc.SectionNumber);

            // A field living in an UNINITIALIZED-data (BSS) section — e.g. the
            // shim's 8 MB `g_heap[]` or SQLite's zero-initialized globals — has
            // no bytes in the file (PointerToRawData == 0). Its FieldRVA data is
            // simply zero bytes; reading the file would over-read. Only sections
            // with real raw data are patched/copied.
            const uint IMAGE_SCN_CNT_UNINITIALIZED_DATA = 0x00000080;
            bool isBss = sec.PointerToRawData == 0 ||
                         (sec.Characteristics & IMAGE_SCN_CNT_UNINITIALIZED_DATA) != 0;

            // Size an INITIALIZED global by its data EXTENT — up to the next FieldRVA
            // symbol in the section (or the section end) — not just the struct's fixed
            // ClassLayout size. A struct with a flexible array member filled by the
            // initializer (e.g. MicroPython's mp_rom_obj_tuple_t) has more data than
            // sizeof(struct); the fixed size would truncate the FAM bytes and leave its
            // pointer relocations "outside" the field. BSS keeps its type size.
            int size = typeSize;
            byte[] secData = isBss ? null : of.Coff.GetPatchedSectionData(sec);
            if (!isBss)
            {
                int next = secData.Length;
                foreach (var kv in dataLoc)
                {
                    var l2 = kv.Value;
                    if (l2.SectionNumber == loc.SectionNumber && l2.Offset > loc.Offset && l2.Offset < next)
                        next = l2.Offset;
                }
                size = Math.Max(typeSize, next - loc.Offset);
            }
            int align = GetFieldDataAlignment(md, fd, size);

            byte[] data = new byte[size];
            if (!isBss)
                Array.Copy(secData, loc.Offset, data, 0,
                    Math.Max(0, Math.Min(size, secData.Length - loc.Offset)));

            var sigReader = md.GetBlobReader(fd.Signature);
            var sigB = new BlobBuilder();
            EcmaSignatureRewriter.RewriteFieldSignature(sigReader, map, sigB);

            string secName = sec.Name ?? "";
            CopiedField.FieldKind kind =
                isBss ? CopiedField.FieldKind.Bss
                : secName.StartsWith(".rdata", StringComparison.Ordinal) ? CopiedField.FieldKind.ReadOnly
                : CopiedField.FieldKind.Mutable;

            // A Mutable field whose initialized data extent exceeds its declared
            // struct size (flexible array member / trailing padding) needs storage
            // sized to the extent: the .cctor copies `size` bytes into it, so a
            // struct-sized field would overflow the copy into the next static field.
            BlobHandle sigBlob = Builder.GetOrAddBlob(sigB);
            if (kind == CopiedField.FieldKind.Mutable && size > typeSize)
                sigBlob = GetOrAddSizedStorageFieldSig(size);

            _outFieldRow++;
            map.SetField(fh, _outFieldRow);
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd.Attributes,
                Name = md.GetString(fd.Name),
                SignatureBlob = sigBlob,
                Data = data,
                Alignment = align,
                PredictedRow = _outFieldRow,
                Kind = kind,
                SourceObj = of,
                SourceSection = loc.SectionNumber,
                SourceOffset = loc.Offset,
                Size = size,
            });
        }
    }

    /// <summary>Reserve one read-only source-field row per Mutable field, appended
    /// AFTER all target rows, so the .cctor can reference it and the writer's row
    /// assertions hold. Targets keep rows 1..N; sources get N+1..M.</summary>
    private void ReserveMutableSourceFields()
    {
        foreach (var cf in CopiedFields)
            if (cf.Kind == CopiedField.FieldKind.Mutable)
                cf.SourceFieldRow = ++_outFieldRow;
    }

    /// <summary>Total global field rows (target + mutable-source fields belonging to
    /// &lt;Module&gt;). Struct member fields are appended AFTER this; use
    /// <see cref="TotalFieldRows"/> for the grand total.</summary>
    public int TotalGlobalFieldRows => _outFieldRow;

    // Grand total including struct member fields (set by ReserveMemberFields).
    // Initialized to -1 as a sentinel; ReserveMemberFields always sets a real value.
    private int _totalFieldRows = -1;

    /// <summary>Grand total of all output Field rows: global fields + struct member fields.
    /// Valid only after <see cref="ReserveMemberFields"/> has been called.</summary>
    public int TotalFieldRows => _totalFieldRows;

    /// <summary>
    /// Assign consecutive output field rows to each CopiedTypeDef's named member
    /// fields, in CopiedTypeDef (PredictedRow) order, AFTER all global field rows
    /// are finalized. Member field rows start at TotalGlobalFieldRows+1 and are
    /// contiguous within each TypeDef, so TypeDef field-range semantics are satisfied.
    /// Also records the per-TypeDef FirstFieldRow for PeWriter's field-range starts
    /// and computes the grand-total field count.
    /// </summary>
    public void ReserveMemberFields()
    {
        int outFieldRow = _outFieldRow;   // continues from last global field row
        foreach (var ct in CopiedTypeDefs)
        {
            if (ct.Members.Count == 0) { ct.FirstFieldRow = 0; continue; }
            ct.FirstFieldRow = outFieldRow + 1;   // first member's output field row
            outFieldRow += ct.Members.Count;
        }
        _totalFieldRows = outFieldRow;
    }

    /// <summary>
    /// Ensure every value-type TypeDef referenced by any local-variable
    /// signature in <paramref name="of"/> is copied/predicted. A fixed-array
    /// local such as <c>char b[16]</c> introduces a <c>$ArrayType$…</c> TypeDef
    /// that may be referenced ONLY by the local sig (no HasFieldRVA field pulls
    /// it in), so without this pass its token in the rewritten local sig would
    /// map to row 0 and the loader rejects the image.
    /// </summary>
    private void EnsureStandaloneSigTypeDefs(ObjectFile of)
    {
        var md = of.Md;
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.StandAloneSig); r++)
        {
            var ss = md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(r));
            var reader = md.GetBlobReader(ss.Signature);
            SignatureHeader header = reader.ReadSignatureHeader();
            if (header.Kind != SignatureKind.LocalVariables) continue;
            int varCount = reader.ReadCompressedInteger();
            for (int i = 0; i < varCount; i++)
                ScanSigTypeForTypeDefs(of, ref reader);
        }
    }

    /// <summary>
    /// Ensure every value-type TypeDef referenced by a METHOD signature
    /// (parameter or return type) is copied/predicted. SQLite passes structs by
    /// value/pointer through many APIs (e.g. <c>sqlite3_value</c>,
    /// <c>sqlite3_vfs</c>), so a method's param/return type may reference a
    /// value-type TypeDef that NO field or local-variable signature pulls in.
    /// Without this pass the rewriter maps that token to row 0, the method
    /// signature decodes as malformed, and the CLR rejects the whole
    /// <c>&lt;Module&gt;</c> type (entry point becomes unresolvable).
    /// </summary>
    private void EnsureMethodSigTypeDefs(ObjectFile of)
    {
        var md = of.Md;
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.MethodDef); r++)
        {
            var def = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(r));
            EnsureSigTypeDefs(of, def.Signature);
        }
    }

    /// <summary>Ensure value-type TypeDefs referenced by external FUNCTION call-site
    /// (MemberRef-on-&lt;Module&gt;) signatures — see the caller in MergeAndPredict.</summary>
    private void EnsureMemberRefSigTypeDefs(ObjectFile of)
    {
        var md = of.Md;
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.MemberRef); r++)
        {
            var mr = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
            if (mr.Parent.Kind != HandleKind.TypeDefinition) continue;
            var parent = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
            if (md.GetString(parent.Name) != "<Module>") continue;
            if (mr.GetKind() != MemberReferenceKind.Method) continue;
            EnsureSigTypeDefs(of, mr.Signature);
        }
    }

    /// <summary>Walk a MethodDefSig blob (return + params) ensuring every value-type
    /// TypeDef it names is copied/predicted, so the rewritten signature maps to a real
    /// row rather than row 0.</summary>
    private void EnsureSigTypeDefs(ObjectFile of, BlobHandle sigBlob)
    {
        var reader = of.Md.GetBlobReader(sigBlob);
        SignatureHeader header = reader.ReadSignatureHeader();
        if (header.IsGeneric) reader.ReadCompressedInteger(); // generic param count
        int paramCount = reader.ReadCompressedInteger();
        ScanSigTypeForTypeDefs(of, ref reader);              // return type
        for (int p = 0; p < paramCount; p++)
        {
            // A SENTINEL (vararg "...") may appear before a param; skip it.
            if (reader.RemainingBytes > 0)
            {
                byte peek = reader.ReadByte();
                if (peek != (byte)SignatureTypeCode.Sentinel) reader.Offset -= 1;
            }
            ScanSigTypeForTypeDefs(of, ref reader);
        }
    }

    /// <summary>Output rows of the value-type TypeDefs referenced (directly or via
    /// pointers/arrays) by any exported method's signature. PeWriter promotes these
    /// to public so external C# can name the pointer parameter types.</summary>
    public HashSet<int> BuildExportReferencedTypeRows()
    {
        var rows = new HashSet<int>();
        foreach (var (of, m) in ExportedMethods)
        {
            var def = of.Md.GetMethodDefinition(m.Handle);
            var reader = of.Md.GetBlobReader(def.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric) reader.ReadCompressedInteger();
            int paramCount = reader.ReadCompressedInteger();
            CollectSigTypeDefRows(of, ref reader, rows);                 // return type
            for (int p = 0; p < paramCount; p++)
            {
                if (reader.RemainingBytes > 0)
                {
                    byte peek = reader.ReadByte();
                    if (peek != (byte)SignatureTypeCode.Sentinel) reader.Offset -= 1;
                }
                CollectSigTypeDefRows(of, ref reader, rows);
            }
        }
        // Empty opaque-handle value types (sqlite3_stmt, …) are referenced via
        // module-scoped TypeRefs, not TypeDef handles, so CollectSigTypeDefRows
        // never sees them — add their rows explicitly.
        rows.UnionWith(_exportOpaqueTypeRows);
        return rows;
    }

    private void CollectSigTypeDefRows(ObjectFile of, ref BlobReader reader, HashSet<int> rows)
    {
    again:
        var tc = reader.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
            {
                EntityHandle modH = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, modH, rows);
                goto again;
            }
            case SignatureTypeCode.Pinned:
            case SignatureTypeCode.ByReference:
                goto again;
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.SZArray:
                CollectSigTypeDefRows(of, ref reader, rows);
                return;
            case SignatureTypeCode.Array:
            {
                CollectSigTypeDefRows(of, ref reader, rows);  // element type
                reader.ReadCompressedInteger();               // rank
                int bounds = reader.ReadCompressedInteger();
                for (int b = 0; b < bounds; b++) reader.ReadCompressedInteger();
                int los = reader.ReadCompressedInteger();
                for (int l = 0; l < los; l++) reader.ReadCompressedSignedInteger();
                return;
            }
            case SignatureTypeCode.GenericTypeInstance:
            {
                reader.ReadByte();
                EntityHandle genH = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, genH, rows);
                int args = reader.ReadCompressedInteger();
                for (int a = 0; a < args; a++) CollectSigTypeDefRows(of, ref reader, rows);
                return;
            }
            case SignatureTypeCode.TypeHandle:
            {
                reader.Offset -= 1; reader.ReadByte();
                EntityHandle th = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, th, rows);
                return;
            }
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                reader.ReadCompressedInteger();
                return;
            case SignatureTypeCode.FunctionPointer:
            {
                var h = reader.ReadSignatureHeader();
                if (h.IsGeneric) reader.ReadCompressedInteger();
                int count = reader.ReadCompressedInteger();
                CollectSigTypeDefRows(of, ref reader, rows);  // return
                for (int p = 0; p < count; p++) CollectSigTypeDefRows(of, ref reader, rows);
                return;
            }
            default:
                return; // primitive
        }
    }

    private void AddRowIfTypeDef(ObjectFile of, EntityHandle h, HashSet<int> rows)
    {
        if (h.Kind != HandleKind.TypeDefinition) return;
        int row = MetadataTokens.GetRowNumber(_maps[of].MapTypeDef((TypeDefinitionHandle)h));
        if (row != 0) rows.Add(row);
    }

    /// <summary>Walk one Type element of a signature blob, recursing through
    /// composite forms, and copy any embedded value-type/class TypeDef. Advances
    /// the reader past the Type exactly as the rewriter would.</summary>
    /// NOTE: three walkers share this exact reader-advance grammar with different
    /// leaf actions — ScanSigTypeForTypeDefs (ensure-copied), CollectSigTypeDefRows
    /// (collect rows), CollectSigOpaqueTypeRefs (reserve opaque TypeDefs). Keep the
    /// reader-advance logic in all three in sync.
    private void ScanSigTypeForTypeDefs(ObjectFile of, ref BlobReader reader)
    {
    again:
        var tc = reader.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
            {
                EntityHandle modH = reader.ReadTypeHandle();
                if (modH.Kind == HandleKind.TypeDefinition)
                    EnsureTypeDefCopied(of, (TypeDefinitionHandle)modH);
                goto again;
            }
            case SignatureTypeCode.Pinned:
            case SignatureTypeCode.ByReference:
                goto again;
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.SZArray:
                ScanSigTypeForTypeDefs(of, ref reader);
                return;
            case SignatureTypeCode.Array:
            {
                ScanSigTypeForTypeDefs(of, ref reader); // element type
                reader.ReadCompressedInteger();         // rank
                int boundsCount = reader.ReadCompressedInteger();
                for (int b = 0; b < boundsCount; b++) reader.ReadCompressedInteger();
                int loCount = reader.ReadCompressedInteger();
                for (int l = 0; l < loCount; l++) reader.ReadCompressedSignedInteger();
                return;
            }
            case SignatureTypeCode.GenericTypeInstance:
            {
                reader.ReadByte();                       // Class/ValueType tag
                EntityHandle genH = reader.ReadTypeHandle();
                if (genH.Kind == HandleKind.TypeDefinition)
                    EnsureTypeDefCopied(of, (TypeDefinitionHandle)genH);
                int args = reader.ReadCompressedInteger();
                for (int a = 0; a < args; a++) ScanSigTypeForTypeDefs(of, ref reader);
                return;
            }
            case SignatureTypeCode.TypeHandle:
            {
                // Step back to read the raw Class/ValueType tag, then the token.
                reader.Offset -= 1;
                reader.ReadByte();
                EntityHandle th = reader.ReadTypeHandle();
                if (th.Kind == HandleKind.TypeDefinition)
                    EnsureTypeDefCopied(of, (TypeDefinitionHandle)th);
                return;
            }
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                reader.ReadCompressedInteger();
                return;
            case SignatureTypeCode.FunctionPointer:
            {
                var h = reader.ReadSignatureHeader();
                if (h.IsGeneric) reader.ReadCompressedInteger();
                int count = reader.ReadCompressedInteger();
                ScanSigTypeForTypeDefs(of, ref reader); // return type
                for (int p = 0; p < count; p++) ScanSigTypeForTypeDefs(of, ref reader);
                return;
            }
            default:
                // Primitive (I4, U4, Void, String, etc.) — single byte, done.
                return;
        }
    }

    /// <summary>Copy (deduped) the value-type TypeDef(s) the field signature
    /// references, mapping their input handle to the predicted output row.</summary>
    private void EnsureFieldTypeDefs(ObjectFile of, FieldDefinition fd)
    {
        var md = of.Md;
        var sigReader = md.GetBlobReader(fd.Signature);
        sigReader.ReadSignatureHeader(); // FIELD
        // Recurse through the WHOLE field type, not just a top-level value type:
        // a global like `BtShared *sqlite3SharedCacheList` is PTR→VALUETYPE→TypeDef,
        // and the pointee's TypeDef must still be copied/predicted or the rewritten
        // signature maps it to row 0 (decodes as malformed → CLR rejects <Module>).
        ScanSigTypeForTypeDefs(of, ref sigReader);
    }

    /// <summary>For every function signature (defined methods AND external-call
    /// MemberRefs) that names an opaque struct with no TypeDef in the merged output
    /// — a forward-declared-only type referenced via a module-scoped TypeRef, e.g.
    /// <c>_IO_FILE</c> (FILE), <c>sqlite3_stmt</c> — synthesize an empty value-type
    /// TypeDef of that (namespace, name) and redirect the TypeRef to it. Without
    /// this the module-scoped TypeRef dangles (no matching TypeDef), and the JIT
    /// throws InvalidProgramException when it imports a call whose signature uses the
    /// type. (Export builds additionally promote these to public — see MaybeReserveOpaque.)
    /// </summary>
    private void ReserveOpaqueTypeDefs()
    {
        // Names already backed by a real (possibly bodied) TypeDef — never shadow.
        var definedNames = new HashSet<(string ns, string name)>();
        foreach (var ct in CopiedTypeDefs)
            definedNames.Add((ct.Namespace, ct.Name));

        var reserved = new Dictionary<(string ns, string name), int>();
        foreach (var of in _objs)
        {
            var md = of.Md;
            // Defined method signatures.
            for (int r = 1; r <= md.GetTableRowCount(TableIndex.MethodDef); r++)
                ScanSigForOpaque(of, md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(r)).Signature,
                    definedNames, reserved);
            // External call-site (MemberRef-on-<Module>) signatures — the ones
            // SymbolResolver turns into P/Invoke stubs (e.g. fileno(FILE*)).
            for (int r = 1; r <= md.GetTableRowCount(TableIndex.MemberRef); r++)
            {
                var mr = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
                if (mr.Parent.Kind != HandleKind.TypeDefinition) continue;
                if (md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent).Name) != "<Module>") continue;
                if (mr.GetKind() != MemberReferenceKind.Method) continue;
                ScanSigForOpaque(of, mr.Signature, definedNames, reserved);
            }
            // FIELD signatures (e.g. a `struct flags_alist[]` global whose element
            // struct is opaque) — the field's type must resolve for ldsfld/ldsflda.
            for (int r = 1; r <= md.GetTableRowCount(TableIndex.Field); r++)
            {
                var rdr = md.GetBlobReader(md.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(r)).Signature);
                rdr.ReadSignatureHeader();   // FIELD
                CollectSigOpaqueTypeRefs(of, ref rdr, definedNames, reserved);
            }
            // LOCAL-variable signatures.
            for (int r = 1; r <= md.GetTableRowCount(TableIndex.StandAloneSig); r++)
            {
                var rdr = md.GetBlobReader(md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(r)).Signature);
                if (rdr.ReadSignatureHeader().Kind != SignatureKind.LocalVariables) continue;
                int cnt = rdr.ReadCompressedInteger();
                for (int i = 0; i < cnt; i++) CollectSigOpaqueTypeRefs(of, ref rdr, definedNames, reserved);
            }
        }

        if (reserved.Count == 0) return;

        // Redirect EVERY object's module-scoped TypeRef whose (ns,name) matches a
        // reserved opaque type to that type's output TypeDef row, so rewritten
        // signatures emit a TypeDef token (decodable by Roslyn) rather than the
        // module-scoped TypeRef. (An opaque type may be named by exported sigs in
        // more than one object, and each object's own TypeRef must be redirected.)
        foreach (var of in _objs)
        {
            var md = of.Md;
            var map = _maps[of];
            for (int r = 1; r <= md.GetTableRowCount(TableIndex.TypeRef); r++)
            {
                var trH = MetadataTokens.TypeReferenceHandle(r);
                var tr = md.GetTypeReference(trH);
                if (tr.ResolutionScope.Kind != HandleKind.ModuleDefinition) continue;
                var k = (md.GetString(tr.Namespace), md.GetString(tr.Name));
                if (reserved.TryGetValue(k, out int row))
                    map.RedirectTypeRefToTypeDef(trH, row);
            }
        }
    }

    /// <summary>Walk one signature Type, and for any embedded module-scoped
    /// TypeRef whose (namespace, name) is NOT backed by a TypeDef, reserve an
    /// empty public value-type TypeDef so the TypeRef resolves. Advances the
    /// reader past the Type exactly as the rewriter would.</summary>
    /// <summary>Walk a whole MethodDefSig (return + params), reserving opaque
    /// TypeDefs for any module-scoped TypeRef it names with no backing TypeDef.</summary>
    private void ScanSigForOpaque(ObjectFile of, BlobHandle sigBlob,
        HashSet<(string ns, string name)> definedNames, Dictionary<(string ns, string name), int> reserved)
    {
        var reader = of.Md.GetBlobReader(sigBlob);
        var header = reader.ReadSignatureHeader();
        if (header.IsGeneric) reader.ReadCompressedInteger();
        int paramCount = reader.ReadCompressedInteger();
        CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);   // return
        for (int p = 0; p < paramCount; p++)
        {
            if (reader.RemainingBytes > 0)
            {
                byte peek = reader.ReadByte();
                if (peek != (byte)SignatureTypeCode.Sentinel) reader.Offset -= 1;
            }
            CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);
        }
    }

    private void CollectSigOpaqueTypeRefs(ObjectFile of, ref BlobReader reader,
        HashSet<(string ns, string name)> definedNames, Dictionary<(string ns, string name), int> reserved)
    {
    again:
        var tc = reader.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
                reader.ReadTypeHandle();   // modifier type (CallConvCdecl etc.) — never opaque
                goto again;
            case SignatureTypeCode.Pinned:
            case SignatureTypeCode.ByReference:
                goto again;
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.SZArray:
                CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);
                return;
            case SignatureTypeCode.Array:
                CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);   // element
                reader.ReadCompressedInteger();                                 // rank
                int bounds = reader.ReadCompressedInteger();
                for (int b = 0; b < bounds; b++) reader.ReadCompressedInteger();
                int los = reader.ReadCompressedInteger();
                for (int l = 0; l < los; l++) reader.ReadCompressedSignedInteger();
                return;
            case SignatureTypeCode.GenericTypeInstance:
                reader.ReadByte();
                MaybeReserveOpaque(of, reader.ReadTypeHandle(), definedNames, reserved);
                int args = reader.ReadCompressedInteger();
                for (int a = 0; a < args; a++) CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);
                return;
            case SignatureTypeCode.TypeHandle:
                reader.Offset -= 1; reader.ReadByte();
                MaybeReserveOpaque(of, reader.ReadTypeHandle(), definedNames, reserved);
                return;
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                reader.ReadCompressedInteger();
                return;
            case SignatureTypeCode.FunctionPointer:
            {
                var h = reader.ReadSignatureHeader();
                if (h.IsGeneric) reader.ReadCompressedInteger();
                int count = reader.ReadCompressedInteger();
                CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);   // return
                for (int p = 0; p < count; p++) CollectSigOpaqueTypeRefs(of, ref reader, definedNames, reserved);
                return;
            }
            default:
                return; // primitive
        }
    }

    private void MaybeReserveOpaque(ObjectFile of, EntityHandle h,
        HashSet<(string ns, string name)> definedNames, Dictionary<(string ns, string name), int> reserved)
    {
        if (h.Kind != HandleKind.TypeReference) return;   // TypeDef names already exist
        var tr = of.Md.GetTypeReference((TypeReferenceHandle)h);
        // Only forward-declared module-local opaque types (resolution scope =
        // the module itself) lack a TypeDef. A TypeRef into another assembly
        // (mscorlib etc.) resolves on its own and must not be shadowed.
        if (tr.ResolutionScope.Kind != HandleKind.ModuleDefinition) return;

        string ns = of.Md.GetString(tr.Namespace);
        string name = of.Md.GetString(tr.Name);
        var key = (ns, name);
        if (definedNames.Contains(key) || reserved.ContainsKey(key)) return;

        _outTypeDefRow++;
        var copied = new CopiedTypeDef
        {
            Name = name,
            Namespace = ns,
            BaseType = GetOrAddCoreValueTypeRef(),
            // Opaque handle: never instantiated by value, only used as a pointer
            // target. Give it an explicit 1-byte ClassLayout so the TypeDef has a
            // definite non-zero size (a zero-field, no-layout value type can trip
            // the loader); 1 byte mirrors an empty C# struct.
            LayoutSize = 1,
            LayoutPack = 1,
            PredictedRow = _outTypeDefRow,
        };
        CopiedTypeDefs.Add(copied);
        // Promote to PUBLIC only for the export surface (C# consumers must name the
        // type); internal opaque types just need to exist so the JIT resolves them.
        if (_exportClass != null) _exportOpaqueTypeRows.Add(copied.PredictedRow);
        reserved[key] = copied.PredictedRow;
    }

    /// <summary>
    /// Field signature <c>valuetype &lt;T&gt;</c> where T is a synthesized value type
    /// with <c>ClassLayout == size</c>. Gives a Mutable field whose initialized data
    /// extent exceeds its declared struct size (flexible array member / trailing
    /// padding) storage that matches the bytes the <c>&lt;Module&gt;.cctor</c> copies
    /// into it — otherwise the copy overflows into the adjacent static field.
    /// </summary>
    private BlobHandle GetOrAddSizedStorageFieldSig(int size)
    {
        if (_sizedStorageSig.TryGetValue(size, out var cached)) return cached;

        _outTypeDefRow++;
        var td = new CopiedTypeDef
        {
            Name = $"$FieldStorage${size}",
            Namespace = "",
            BaseType = GetOrAddCoreValueTypeRef(),
            LayoutSize = size,
            LayoutPack = 1,
            PredictedRow = _outTypeDefRow,
        };
        CopiedTypeDefs.Add(td);

        var b = new BlobBuilder();
        new BlobEncoder(b).FieldSignature()
            .Type(MetadataTokens.TypeDefinitionHandle(td.PredictedRow), isValueType: true);
        var sig = Builder.GetOrAddBlob(b);
        _sizedStorageSig[size] = sig;
        return sig;
    }

    private void EnsureTypeDefCopied(ObjectFile of, TypeDefinitionHandle inH)
    {
        var md = of.Md;
        var map = _maps[of];

        if (MetadataTokens.GetRowNumber(map.MapTypeDef(inH)) != 0) return; // already mapped

        var td = md.GetTypeDefinition(inH);
        string name = md.GetString(td.Name);
        string ns = md.GetString(td.Namespace);
        var layout = td.GetLayout();
        int size = layout.IsDefault ? -1 : layout.Size;

        if (_typeDefByKey.TryGetValue((ns, name, size), out var existing))
        {
            map.SetTypeDef(inH, existing.PredictedRow);
            return;
        }

        EntityHandle baseType = td.BaseType.IsNil ? default : map.MapEntity(td.BaseType);

        _outTypeDefRow++;
        var copied = new CopiedTypeDef
        {
            Name = name,
            Namespace = ns,
            BaseType = baseType,
            LayoutSize = size,
            LayoutPack = layout.IsDefault ? 0 : layout.PackingSize,
            PredictedRow = _outTypeDefRow,
        };
        _typeDefByKey[(ns, name, size)] = copied;
        CopiedTypeDefs.Add(copied);
        map.SetTypeDef(inH, copied.PredictedRow);

        // Extract named member fields from ExplicitLayout public structs.
        // Scalars and pointers have no type tokens in their field signatures, so
        // RewriteFieldSignature is correct and future-proofs nested-struct members.
        if ((td.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) != 0)
        {
            copied.ExplicitLayout = true;
            foreach (var fh2 in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh2);
                string fn = md.GetString(fd.Name);
                if (fn == "<alignment member>") continue;  // skip chibil's size filler
                var sr = md.GetBlobReader(fd.Signature);
                var ob = new BlobBuilder();
                EcmaSignatureRewriter.RewriteFieldSignature(sr, map, ob);
                copied.Members.Add(new MemberField
                {
                    Name = fn,
                    Signature = Builder.GetOrAddBlob(ob),
                    Offset = fd.GetOffset(),
                });
            }
        }
    }

    private static int GetFieldDataSize(MetadataReader md, FieldDefinition fd)
    {
        var sr = md.GetBlobReader(fd.Signature);
        sr.ReadSignatureHeader();
    again:
        SignatureTypeCode tc = sr.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.OptionalModifier:
            case SignatureTypeCode.RequiredModifier:
                sr.ReadTypeHandle(); goto again;
            case SignatureTypeCode.Boolean:
            case SignatureTypeCode.SByte:
            case SignatureTypeCode.Byte: return 1;
            case SignatureTypeCode.Char:
            case SignatureTypeCode.Int16:
            case SignatureTypeCode.UInt16: return 2;
            case SignatureTypeCode.Int32:
            case SignatureTypeCode.UInt32:
            case SignatureTypeCode.Single: return 4;
            case SignatureTypeCode.Int64:
            case SignatureTypeCode.UInt64:
            case SignatureTypeCode.Double: return 8;
            case SignatureTypeCode.IntPtr:
            case SignatureTypeCode.UIntPtr: return 8; // CoreCLR targets are 64-bit here
            case SignatureTypeCode.Pointer:          // T* (e.g. char *sqlite3_data_directory)
            case SignatureTypeCode.FunctionPointer: return 8;
            case SignatureTypeCode.TypeHandle:
                {
                    // TypeHandle TypeCode (Class/ValueType) is a single byte per ECMA-335 II.23.2.4
                    sr.Offset -= 1; sr.ReadByte();
                    EntityHandle th = sr.ReadTypeHandle();
                    if (th.Kind != HandleKind.TypeDefinition)
                        throw new LinkException($"field '{md.GetString(fd.Name)}' RVA data references a non-TypeDef value type.");
                    var td = md.GetTypeDefinition((TypeDefinitionHandle)th);
                    var lay = td.GetLayout();
                    if (lay.IsDefault || lay.Size == 0)
                        throw new LinkException($"field '{md.GetString(fd.Name)}' value type '{md.GetString(td.Name)}' has no ClassLayout size.");
                    return lay.Size;
                }
            default:
                throw new LinkException($"cannot size FieldRVA data for field '{md.GetString(fd.Name)}' (sig 0x{(byte)tc:X2}).");
        }
    }

    private static int GetFieldDataAlignment(MetadataReader md, FieldDefinition fd, int size)
    {
        var sr = md.GetBlobReader(fd.Signature);
        sr.ReadSignatureHeader();
    again:
        SignatureTypeCode tc = sr.ReadSignatureTypeCode();
        if (tc == SignatureTypeCode.OptionalModifier || tc == SignatureTypeCode.RequiredModifier)
        {
            sr.ReadTypeHandle(); goto again;
        }
        if (tc == SignatureTypeCode.TypeHandle)
        {
            // TypeHandle TypeCode (Class/ValueType) is a single byte per ECMA-335 II.23.2.4
            sr.Offset -= 1; sr.ReadByte();
            EntityHandle th = sr.ReadTypeHandle();
            if (th.Kind == HandleKind.TypeDefinition)
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)th);
                var lay = td.GetLayout();
                int pack = lay.IsDefault ? 0 : lay.PackingSize;
                if (pack > 0) return PowerOfTwoFloor(pack);
            }
        }
        // Natural alignment must be a POWER OF TWO. Math.Min(size, 8) alone can
        // yield non-power-of-two values (e.g. a 6-byte string literal -> 6), which
        // both desynchronizes the mapped-field-data padding and produces a FieldRVA
        // the CLR cannot address correctly (the literal then reads back as zero).
        // Floor to the largest power of two <= min(size, 8); this is always >= the
        // true element alignment of a byte/char array (1) and of any packed struct.
        return PowerOfTwoFloor(Math.Min(size, 8));
    }

    /// <summary>Largest power of two &lt;= v (clamped to [1, 8]).</summary>
    private static int PowerOfTwoFloor(int v)
    {
        if (v <= 1) return 1;
        if (v >= 8) return 8;
        if (v >= 4) return 4;
        return 2;
    }

    /// <summary>Rewrite a method's signature blob into the shared heap.</summary>
    public BlobHandle RewriteMethodSignature(ObjectFile of, ObjMethod m)
    {
        var md = of.Md;
        var def = md.GetMethodDefinition(m.Handle);
        var sigReader = md.GetBlobReader(def.Signature);
        var sigBuilder = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(sigReader, _maps[of], sigBuilder);
        return Builder.GetOrAddBlob(sigBuilder);
    }

    /// <summary>Map the local-variable signature handle for a method (or nil).
    /// Throws if a non-nil local-var sig fails to remap, converting a silent
    /// runtime InvalidProgramException into a clear link error.</summary>
    public StandaloneSignatureHandle MapLocalSig(ObjectFile of, ObjMethod m)
    {
        if (m.LocalSig.IsNil) return default;
        var mapped = _maps[of].MapStandaloneSig(m.LocalSig);
        if (mapped.IsNil || MetadataTokens.GetRowNumber(mapped) == 0)
            throw new LinkException($"lost local-variable signature for method {m.Name}");
        return mapped;
    }
}
