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
    private static bool IsExportForwarder(ObjectFile of, ObjMethod m)
    {
        if (m.Name == "main") return false;
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

    private int _outTypeDefRow = ModuleTypeDefRow;   // row 1 = <Module>
    private int _outFieldRow;

    private readonly string _exportClass;
    private int _exportTypeDefRow;   // 0 = no export type

    // Output TypeDef rows of synthesized empty opaque-handle value types (e.g.
    // sqlite3_stmt) created for the export surface. PeWriter unions these into
    // the set it promotes to public.
    private readonly HashSet<int> _exportOpaqueTypeRows = new();

    private readonly IReadOnlyList<string> _libs;

    public MetadataMerger(IReadOnlyList<ObjectFile> objs, string exportClass = null,
        IReadOnlyList<string> libs = null)
    {
        _objs = objs;
        _exportClass = exportClass;
        _libs = libs;
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

        // Append read-only source fields (rows N+1..M) for each Mutable (.data)
        // global, so the .cctor can cpblk their bytes into the writable target.
        // Field-row order is final after this; later passes touch only TypeDef/method rows.
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

        // ── Opaque-handle TypeDefs for the export surface ─────────────────────
        //    An exported function whose signature names a forward-declared-only
        //    opaque struct (e.g. `sqlite3_stmt*` — never given a body in this
        //    build) references it via a MODULE-SCOPED TypeRef, with no matching
        //    TypeDef in the assembly. A C# consumer compiled by Roslyn against the
        //    output can't bind that TypeRef to a same-assembly type, so the
        //    forwarder method is rejected (CS0570 "not supported by the
        //    language"). Synthesize an empty PUBLIC value-type TypeDef per such
        //    opaque name so the existing module-scoped TypeRef resolves and the
        //    consumer can spell the pointer parameter type. Must run AFTER all
        //    real value-type TypeDefs are reserved (so we don't duplicate a name
        //    that has a real body) and is the LAST TypeDef-reserving pass.
        if (_exportClass != null)
            ReserveExportOpaqueTypeDefs();

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

    private int _argcToken;
    private int _makeArgvToken;
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

            int size = GetFieldDataSize(md, fd);
            int align = GetFieldDataAlignment(md, fd, size);

            var sec = of.Coff.GetSection(loc.SectionNumber);
            byte[] data = new byte[size];
            // A field living in an UNINITIALIZED-data (BSS) section — e.g. the
            // shim's 8 MB `g_heap[]` or SQLite's zero-initialized globals — has
            // no bytes in the file (PointerToRawData == 0). Its FieldRVA data is
            // simply `size` zero bytes; reading the file would over-read. Only
            // sections with real raw data are patched/copied.
            const uint IMAGE_SCN_CNT_UNINITIALIZED_DATA = 0x00000080;
            bool isBss = sec.PointerToRawData == 0 ||
                         (sec.Characteristics & IMAGE_SCN_CNT_UNINITIALIZED_DATA) != 0;
            if (!isBss)
            {
                byte[] secData = of.Coff.GetPatchedSectionData(sec);
                Array.Copy(secData, loc.Offset, data, 0,
                    Math.Max(0, Math.Min(size, secData.Length - loc.Offset)));
            }

            var sigReader = md.GetBlobReader(fd.Signature);
            var sigB = new BlobBuilder();
            EcmaSignatureRewriter.RewriteFieldSignature(sigReader, map, sigB);

            string secName = sec.Name ?? "";
            CopiedField.FieldKind kind =
                isBss ? CopiedField.FieldKind.Bss
                : secName.StartsWith(".rdata", StringComparison.Ordinal) ? CopiedField.FieldKind.ReadOnly
                : CopiedField.FieldKind.Mutable;

            _outFieldRow++;
            map.SetField(fh, _outFieldRow);
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd.Attributes,
                Name = md.GetString(fd.Name),
                SignatureBlob = Builder.GetOrAddBlob(sigB),
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

    /// <summary>Total output Field rows (targets + appended Mutable sources).
    /// Used as the upper bound for value-type TypeDefs' field list.</summary>
    public int TotalFieldRows => _outFieldRow;

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
            var reader = md.GetBlobReader(def.Signature);
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

    /// <summary>For every exported function whose signature names an opaque
    /// struct that has no TypeDef in the merged output (a forward-declared-only
    /// type referenced via a module-scoped TypeRef, e.g. <c>sqlite3_stmt</c>),
    /// synthesize an empty PUBLIC value-type TypeDef of that (namespace, name).
    /// The existing module-scoped TypeRef in the signature then binds to it, so a
    /// Roslyn-compiled C# consumer can name the pointer parameter type. Real
    /// struct bodies are already reserved by earlier passes and are skipped here.
    /// </summary>
    private void ReserveExportOpaqueTypeDefs()
    {
        // Names already backed by a real (possibly bodied) TypeDef — never shadow.
        var definedNames = new HashSet<(string ns, string name)>();
        foreach (var ct in CopiedTypeDefs)
            definedNames.Add((ct.Namespace, ct.Name));

        // Reserve one empty public TypeDef per opaque (ns,name) seen in an
        // exported signature.
        var reserved = new Dictionary<(string ns, string name), int>();
        foreach (var of in _objs)
        {
            var md = of.Md;
            foreach (var m in of.Methods)
            {
                // ExportedMethods is not yet populated here (this pass runs BEFORE the
                // method-prediction loop), so re-derive the export set via the shared
                // IsExportForwarder predicate instead of consuming the list.
                if (!IsExportForwarder(of, m)) continue;
                var mdef = md.GetMethodDefinition(m.Handle);

                var reader = md.GetBlobReader(mdef.Signature);
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
        _exportOpaqueTypeRows.Add(copied.PredictedRow);
        reserved[key] = copied.PredictedRow;
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
