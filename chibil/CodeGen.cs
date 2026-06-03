using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class CodeGen
{
    private readonly CompilerOptions _options;
    private readonly TypeSystem _types;
    private readonly Tokenizer _tokenizer;
    private readonly DataModel _dm;

    // ─── TU-level state ──────────────────────────────────────────
    private MetadataBuilder _md;
    private CoffHeaderBuilder _coffHeader;
    private ManagedCoffSymbolTableBuilder _symtab;
    private CodeViewSymbolBuilder _codeviewSymbols;
    private CodeViewFileHandle _cvFile;
    private RelocatableMethodBodyStreamEncoder _bodyEncoder;

    // Managed-PDB side-stream (.chibildbg): the primary source file + per-method
    // (MethodDef RID -> [(IL offset, line)]). chibil-link transcodes this to a
    // Portable PDB. chibil marks every line against the primary _cvFile, so one
    // document per TU suffices.
    private string _dbgSourceFile;
    private byte[] _dbgSourceHash;
    private readonly List<(int Rid, int IlSize,
        List<(int Il, int Line, int StartCol, int EndCol)> Pts,
        List<(int Start, int Len, List<(int Slot, string Name)> Locals)> Scopes)> _dbgMethods = new();
    // Per-function: measured IL range [start, start+len) of each lexical scope, keyed
    // by the parser's scope index. Drives nested LocalScope emission so block-scoped
    // and shadowed locals are visible only within their block.
    private Dictionary<int, (int Start, int Len)> _dbgScopeRanges = new();

    private BlobBuilder _ilStreamBuilder, _ilRelocBuilder;
    private BlobBuilder _dataStream, _dataRelocs;
    private BlobBuilder _rdataStream;
    private BlobBuilder _nepStream, _nepRelocs;
    private BlobBuilder _ilFixupStream, _ilFixupRelocs;
    private int _bssSize;

    private AssemblyReferenceHandle _mscorlibRef;
    private TypeDefinitionHandle _moduleTypeDef;

    // Lazy TypeRef handles (created on first use)
    private TypeReferenceHandle _callConvCdeclRef;
    private TypeReferenceHandle _callConvStdcallRef;
    private TypeReferenceHandle _isSignUnspecifiedByteRef;
    private TypeReferenceHandle _isConstRef;
    private TypeReferenceHandle _isVolatileRef;
    private TypeReferenceHandle _isLongRef;
    private TypeReferenceHandle _nativeCppClassAttrRef;
    private TypeReferenceHandle _valueTypeRef;
    private TypeReferenceHandle _interlockedRef;
    private bool _callConvCdeclCreated, _callConvStdcallCreated;
    private bool _isSignUnspecifiedByteCreated, _isConstCreated, _isVolatileCreated, _isLongCreated;
    private bool _nativeCppClassAttrCreated, _valueTypeCreated, _interlockedCreated;

    // Metadata row tracking
    private int _nextFieldRow = 1, _nextMethodRow = 1, _nextParamRow = 1;

    // Function/field registrations
    private readonly Dictionary<Obj, MethodDefinitionHandle> _methodDefs = new();
    private readonly Dictionary<Obj, FieldDefinitionHandle> _fieldDefs = new();
    private readonly Dictionary<string, MemberReferenceHandle> _externalFuncRefs = new();
    private readonly Dictionary<int, TypeDefinitionHandle> _structTypeDefs = new();
    private readonly Dictionary<string, TypeDefinitionHandle> _arrayTypeDefs = new();
    private readonly Dictionary<string, TypeReferenceHandle> _forwardDeclTypeRefs = new();
    private readonly List<(int typeId, CType type, string name)> _pendingTypeDefs = new();
    private readonly Dictionary<string, FieldDefinitionHandle> _globalFieldsByName = new();

    // Tracks which functions have their address taken (need __unep@ slot)
    private readonly HashSet<string> _addressTakenFuncs = new();

    // Bare-name NEP COFF symbols (func name → COFF symbol for the NEP thunk alias)
    private readonly Dictionary<string, CoffSymbolHandle> _nepBareNameSymbols = new();

    // Method-body offsets in .text$mn (func → offset), captured during IL emission.
    // Used in CoreCLR target mode to emit plain bare-name function aliases without
    // the IJW NEP thunk.
    private readonly Dictionary<Obj, int> _methodBodyOffsets = new();

    // Anonymous global counter and TU hash
    private int _anonGlobalCounter;
    private string _tuHash;

    // Name mangling backref tables (reset per function)
    private List<string> _nameBackRefs;
    private Dictionary<string, int> _argBackRefs;

    // __unep@ fields for address-taken cdecl functions
    private readonly Dictionary<string, FieldDefinitionHandle> _unepFields = new();

    // __CxxPureMSILEntry state
    private MethodDefinitionHandle _mainMethod;
    private Obj _mainObj;
    private MethodDefinitionHandle _cxxPureMsilEntry;
    private bool _hasMain;

    // Architecture helpers derived from DataModel
    private int PtrSize => _dm.PointerSize;
    private bool Is32 => _dm.PointerSize == 4;
    private string SymPrefix => Is32 ? "_" : "";
    private Machine TargetMachine => Is32 ? Machine.I386 : Machine.Amd64; // LP64: add ARM64
    private CodeViewMachine CvMachine => Is32 ? CodeViewMachine.I386 : CodeViewMachine.Amd64;

    // Mscorlib hashes
    private byte[] MscorlibHash => Is32
        ? new byte[] { 0x32, 0xCD, 0x81, 0x47, 0x47, 0x14, 0x67, 0x52, 0xE5, 0x5E, 0x2B, 0xF7, 0xEC, 0x50, 0x8A, 0x87, 0x55, 0xC8, 0xB9, 0x5C }
        : new byte[] { 0x28, 0xDC, 0x37, 0x8B, 0x8E, 0x25, 0x7A, 0xAC, 0xDD, 0x91, 0x4D, 0xF4, 0x16, 0x57, 0x67, 0x49, 0x13, 0xC1, 0x99, 0xCE };
    private static readonly byte[] MscorlibPkt = { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 };

    // ─── Per-function state ──────────────────────────────────────
    private RelocatableInstructionEncoder _enc;
    private Obj _currentFn;
    private Dictionary<Obj, int> _localSlots;
    private Dictionary<Obj, int> _paramSlots;
    private List<(CType ty, int slot)> _scratchLocals;
    private int _scratchLocalBase;
    // Dedicated va_arg scratch locals (a pointer-to-va_list and a va_list);
    // both are Ptr-typed, so GetOrAddScratchLocal would alias them to one slot.
    // Allocate a distinct pair lazily, once per function.
    private int _vaArgPApLocal = -1;
    private int _vaArgApLocal = -1;
    // Scratch __va_list_tag* holders for va_start / va_copy (see VaStart).
    private int _vaStartStructLocal = -1;
    private int _vaCopyStructLocal = -1;
    // ── setjmp/longjmp wrap state (MUSL-3) ────────────────────────────────
    // A function containing setjmp is wrapped in `Lhead: .try { body } filter/handler`
    // so a longjmp (which throws via __chibil_longjmp) resumes it: the handler stores
    // the longjmp value into the matching setjmp's result local and `leave`s to Lhead,
    // re-entering the try (IL forbids branching INTO a try). Returns inside the try
    // become store-retval + `leave` to the epilogue. See EmitSetjmpWrappedBody.
    private bool _setjmpWrap;
    private LabelHandle _setjmpEpiLabel, _setjmpLhead;
    private int _setjmpRetvalLocal = -1;
    private int _setjmpBufLocal = -1;
    private List<(Node Jb, int Sjval)> _setjmpSites;
    private bool _setjmpTryOpen;            // true once the try region has started (governs Return)
    private bool _setjmpDeferStart;         // resume-at-site mode: single setjmp (in or out of a loop)
    private LabelHandle _setjmpTryStartLabel;
    // Statement labels marked BEFORE the setjmp site (loop headers, user labels). A
    // branch from inside the resume-at-site try to one of these exits the protected
    // region and must be a `leave`, not a `br`.
    private readonly HashSet<LabelHandle> _setjmpOuterLabels = new();
    // (trampoline, target) pairs: a redirected cross-out branch jumps to `trampoline`,
    // emitted inside the try as `trampoline: leave target`.
    private readonly List<(LabelHandle tramp, LabelHandle target)> _setjmpLeaveTrampolines = new();
    private readonly Dictionary<string, MemberReferenceHandle> _runtimeHelperRefs = new();
    private int _maxStack, _stackDepth;
    private Dictionary<string, LabelHandle> _labels;
    private int _labelCount;
    private StandaloneSignatureHandle _localsSigHandle;

    public CodeGen(CompilerOptions options, Tokenizer tokenizer, TypeSystem types)
    {
        _options = options;
        _tokenizer = tokenizer;
        _types = types;
        _dm = options.DataModel;
    }

    private int Count() => _labelCount++;

    // ═══════════════════════════════════════════════════════════════
    //  Stack tracking
    // ═══════════════════════════════════════════════════════════════

    private void Push() { _stackDepth++; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Push(int n) { _stackDepth += n; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Pop() { Debug.Assert(_stackDepth > 0, "stack underflow"); _stackDepth--; }
    private void Pop(int n) { Debug.Assert(_stackDepth >= n, "stack underflow"); _stackDepth -= n; }

    // ═══════════════════════════════════════════════════════════════
    //  Lazy TypeRef accessors
    // ═══════════════════════════════════════════════════════════════

    private TypeReferenceHandle GetCallConvCdeclRef()
    {
        if (!_callConvCdeclCreated)
        {
            _callConvCdeclRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("CallConvCdecl"));
            _callConvCdeclCreated = true;
        }
        return _callConvCdeclRef;
    }

    private TypeReferenceHandle GetCallConvStdcallRef()
    {
        if (!_callConvStdcallCreated)
        {
            _callConvStdcallRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("CallConvStdcall"));
            _callConvStdcallCreated = true;
        }
        return _callConvStdcallRef;
    }

    private TypeReferenceHandle GetIsSignUnspecifiedByteRef()
    {
        if (!_isSignUnspecifiedByteCreated)
        {
            _isSignUnspecifiedByteRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsSignUnspecifiedByte"));
            _isSignUnspecifiedByteCreated = true;
        }
        return _isSignUnspecifiedByteRef;
    }

    private TypeReferenceHandle GetIsConstRef()
    {
        if (!_isConstCreated)
        {
            _isConstRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsConst"));
            _isConstCreated = true;
        }
        return _isConstRef;
    }

    private TypeReferenceHandle GetIsVolatileRef()
    {
        if (!_isVolatileCreated)
        {
            _isVolatileRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsVolatile"));
            _isVolatileCreated = true;
        }
        return _isVolatileRef;
    }

    private TypeReferenceHandle GetIsLongRef()
    {
        if (!_isLongCreated)
        {
            _isLongRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("IsLong"));
            _isLongCreated = true;
        }
        return _isLongRef;
    }

    private TypeReferenceHandle GetNativeCppClassAttrRef()
    {
        if (!_nativeCppClassAttrCreated)
        {
            _nativeCppClassAttrRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Runtime.CompilerServices"),
                _md.GetOrAddString("NativeCppClassAttribute"));
            _nativeCppClassAttrCreated = true;
        }
        return _nativeCppClassAttrRef;
    }

    private TypeReferenceHandle GetValueTypeRef()
    {
        if (!_valueTypeCreated)
        {
            _valueTypeRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System"),
                _md.GetOrAddString("ValueType"));
            _valueTypeCreated = true;
        }
        return _valueTypeRef;
    }

    private TypeReferenceHandle GetInterlockedRef()
    {
        if (!_interlockedCreated)
        {
            _interlockedRef = _md.AddTypeReference(_mscorlibRef,
                _md.GetOrAddString("System.Threading"),
                _md.GetOrAddString("Interlocked"));
            _interlockedCreated = true;
        }
        return _interlockedRef;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type encoding: CType → MSIL signature bytes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Encode a C type into an MSIL signature using the builder directly.
    /// Uses raw byte writes for modopt/modreq since the BlobEncoder API
    /// doesn't support all patterns we need.
    /// </summary>
    private void EncodeType(BlobBuilder sig, CType ty)
    {
        // Handle const/volatile on this type (for pointer-level qualifiers)
        if (ty.IsConst)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsConstRef()));
        }
        if (ty.IsVolatile)
        {
            sig.WriteByte((byte)SignatureTypeCode.RequiredModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsVolatileRef()));
        }

        switch (ty.Kind)
        {
            case TypeKind.Void:
                sig.WriteByte((byte)SignatureTypeCode.Void);
                break;
            case TypeKind.Bool:
                sig.WriteByte((byte)SignatureTypeCode.Boolean);
                break;
            case TypeKind.Char:
                if (ty.IsUnsigned)
                {
                    // unsigned char: uint8 (no modopt)
                    sig.WriteByte((byte)SignatureTypeCode.Byte);
                }
                else
                {
                    // plain char or signed char: modopt(IsSignUnspecifiedByte) int8
                    // Note: C distinguishes plain char from signed char, but both map
                    // to int8 with the modopt marker. The modopt is harmless for
                    // signed char and required for plain char.
                    sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
                    sig.WriteByte((byte)SignatureTypeCode.SByte);
                }
                break;
            case TypeKind.Short:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt16 : (byte)SignatureTypeCode.Int16);
                break;
            case TypeKind.Int:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Enum:
                // Enums are plain int32
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                break;
            case TypeKind.Long:
                // LLP64: long = 4 bytes with modopt(IsLong)
                // LP64: long = 8 bytes, would be int64
                if (_dm.LongSize == 4)
                {
                    sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsLongRef()));
                    sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt32 : (byte)SignatureTypeCode.Int32);
                }
                else
                {
                    // LP64: long is 8 bytes = int64
                    sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt64 : (byte)SignatureTypeCode.Int64);
                }
                break;
            case TypeKind.LLong:
                sig.WriteByte(ty.IsUnsigned ? (byte)SignatureTypeCode.UInt64 : (byte)SignatureTypeCode.Int64);
                break;
            case TypeKind.Float:
                sig.WriteByte((byte)SignatureTypeCode.Single);
                break;
            case TypeKind.Double:
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.LDouble:
                // long double → modopt(IsLong) float64 (both LP64 and LLP64)
                sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
                sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsLongRef()));
                sig.WriteByte((byte)SignatureTypeCode.Double);
                break;
            case TypeKind.Ptr:
                if (ty.Base.Kind == TypeKind.Func)
                {
                    // Pointer to function → FNPTR directly (no extra Ptr wrapper)
                    sig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                    EncodeFnPtrSignature(sig, ty.Base);
                }
                else
                {
                    sig.WriteByte((byte)SignatureTypeCode.Pointer);
                    EncodeType(sig, ty.Base);
                }
                break;
            case TypeKind.Array:
                if (ty.ArrayLen < 0)
                {
                    // Incomplete array → pointer to element
                    sig.WriteByte((byte)SignatureTypeCode.Pointer);
                    EncodeType(sig, ty.Base);
                }
                else
                {
                    // Fixed-size array → ValueType of array TypeDef
                    string arrayName = MangleArrayTypeName(ty);
                    if (_arrayTypeDefs.TryGetValue(arrayName, out var arrayTd))
                    {
                        sig.WriteByte((byte)(SignatureTypeCode)0x11);
                        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(arrayTd));
                    }
                    else
                    {
                        // Shouldn't happen if PreAllocate ran correctly
                        sig.WriteByte((byte)SignatureTypeCode.Pointer);
                        EncodeType(sig, ty.Base);
                    }
                }
                break;
            case TypeKind.Struct:
            case TypeKind.Union:
            {
                CType canonical = ty;
                while (canonical.Origin != null) canonical = canonical.Origin;
                if (canonical.IsNestedMember)
                    throw new InvalidOperationException(
                        $"Internal error: nested member type '{GetStructName(canonical)}' reached signature encoding (in function '{_currentFn?.Name}')");
                int typeId = GetTypeId(ty);
                if (_structTypeDefs.TryGetValue(typeId, out var structTd))
                {
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(structTd));
                }
                else
                {
                    // Forward-declared struct → TypeRef
                    string name = GetStructName(ty);
                    if (!_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
                    {
                        typeRef = _md.AddTypeReference(default, default, _md.GetOrAddString(name));
                        _forwardDeclTypeRefs[name] = typeRef;
                    }
                    sig.WriteByte((byte)(SignatureTypeCode)0x11);
                    sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(typeRef));
                }
                break;
            }
            case TypeKind.Func:
            {
                // Function type used as a value (function pointer parameter) → FNPTR
                sig.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                EncodeFnPtrSignature(sig, ty);
                break;
            }
            case TypeKind.Vla:
                // VLA → pointer to base element
                sig.WriteByte((byte)SignatureTypeCode.Pointer);
                EncodeType(sig, ty.Base);
                break;
            default:
                // Native int for anything else (shouldn't happen)
                sig.WriteByte((byte)SignatureTypeCode.IntPtr);
                break;
        }
    }

    /// <summary>Encode an inline function pointer signature for FNPTR in method/local signatures.</summary>
    private void EncodeFnPtrSignature(BlobBuilder sig, CType funcTy)
    {
        // Calling convention byte per ECMA-335:
        // MSVC /clr uses CDecl (0x01) for cdecl, StdCall (0x02) for __stdcall,
        // and Default (0x00) for __clrcall function pointers.
        // In CoreCLR/pure-MSIL there is no native interop: every C function is a
        // managed method with the Default (0x00) convention, so force Default to
        // match the ldftn'd method pointer (Spot 1) and the calli sig (Spot 3).
        byte conv = _options.Target == TargetProfile.CoreClr
            ? (byte)SignatureCallingConvention.Default
            : (funcTy.CallConv switch
            {
                CallConv.Clrcall => (byte)SignatureCallingConvention.Default,
                CallConv.Stdcall => (byte)SignatureCallingConvention.StdCall,
                _ => (byte)SignatureCallingConvention.CDecl,
            });
        sig.WriteByte(conv);

        // Count parameters
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        sig.WriteCompressedInteger(paramCount);

        // Return type
        EncodeReturnType(sig, funcTy);

        // Parameters
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);
    }

    /// <summary>Encode the return type for a function, with modopt(CallConvCdecl) for cdecl.</summary>
    private void EncodeReturnType(BlobBuilder sig, CType funcTy)
    {
        // For cdecl functions: modopt(CallConvCdecl) on return type
        if (funcTy.CallConv == CallConv.Cdecl)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetCallConvCdeclRef()));
        }
        else if (funcTy.CallConv == CallConv.Stdcall)
        {
            sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
            sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetCallConvStdcallRef()));
        }

        if (funcTy.ReturnTy.Kind == TypeKind.Void)
            sig.WriteByte((byte)SignatureTypeCode.Void);
        else
            EncodeType(sig, funcTy.ReturnTy);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Type identity helpers
    // ═══════════════════════════════════════════════════════════════

    // Monotonic counter for stable, collision-free type IDs
    private readonly Dictionary<CType, int> _typeIdMap = new(ReferenceEqualityComparer.Instance);
    private int _nextTypeId;

    /// <summary>Get a stable, collision-free identity for a struct/union type for dedup.</summary>
    private int GetTypeId(CType ty)
    {
        // Walk through Origin chain to find the canonical type
        CType canonical = ty;
        while (canonical.Origin != null) canonical = canonical.Origin;
        if (!_typeIdMap.TryGetValue(canonical, out int id))
        {
            id = ++_nextTypeId;
            _typeIdMap[canonical] = id;
        }
        return id;
    }

    private string GetStructName(CType ty)
    {
        // Prefer TagName (set by parser from struct/union tag) over Name
        // (which Declarator overwrites with the variable/parameter name)
        string tag = GetTagName(ty);
        if (tag != null)
            return tag;
        if (ty.Name != null)
            return Util.GetTokenText(ty.Name);
        // Anonymous struct — use a generated name
        return $"<anon_{GetTypeId(ty):X8}>";
    }

    /// <summary>Walk the Origin chain to find the tag name of an enum/struct/union.</summary>
    private static string GetTagName(CType ty)
    {
        CType cur = ty;
        while (cur != null)
        {
            if (cur.TagName != null)
                return cur.TagName;
            cur = cur.Origin;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Name mangling
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Produce an MSVC-compatible decorated name for a C function.
    /// Format: ?name@@$$J0YA(ret)(params)@Z  for cdecl
    ///         ?name@@$$J0YM(ret)(params)@Z  for __clrcall
    /// </summary>
    private string MangleFunctionName(Obj fn)
    {
        CType funcTy = fn.Ty;
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only reaches here on x86 (normalized to Cdecl on x64)
            _ => "A", // cdecl
        };
        // Static functions get TU-hash-scoped names to avoid cross-TU collisions
        string name = fn.IsStatic ? $"{fn.Name}_?A0x{_tuHash}" : fn.Name;
        var sb = new StringBuilder();
        sb.Append($"?{name}@@$$J0Y{cc}");

        // Initialize backref tables for this function
        _nameBackRefs = new List<string> { fn.Name }; // function name = slot 0
        _argBackRefs = new Dictionary<string, int>();

        // Return type: uses name backrefs but does NOT participate in arg backref table
        MangleType(sb, funcTy.ReturnTy, isReturn: true);

        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            MangleArgType(sb, p);
            paramCount++;
        }
        if (paramCount == 0)
            sb.Append("XZ"); // void params: X = no params, Z = terminator
        else if (funcTy.IsVariadic)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
        return sb.ToString();
    }

    /// <summary>
    /// Mangle a function argument type with backreference support.
    /// If the full mangled type string was seen before, emit a digit (0-9).
    /// Otherwise emit the full type and register it for future backrefs.
    /// </summary>
    private void MangleArgType(StringBuilder sb, CType ty)
    {
        // Mangle into a temp buffer WITHOUT name or arg backrefs to get the
        // canonical arg-type key. Both tables must be disabled: name backrefs
        // change the string (preventing arg-type matches), and arg backrefs
        // would pollute the live table for nested func-ptr params.
        var savedNameBackRefs = _nameBackRefs;
        var savedArgBackRefs = _argBackRefs;
        _nameBackRefs = null;
        _argBackRefs = new Dictionary<string, int>(); // isolated table for canonical pass
        var tmp = new StringBuilder();
        MangleType(tmp, ty, isReturn: false);
        string canonical = tmp.ToString();
        _nameBackRefs = savedNameBackRefs;
        _argBackRefs = savedArgBackRefs;

        // Check arg-type backref table using the canonical (no-backref) key
        if (_argBackRefs.TryGetValue(canonical, out int slot))
        {
            sb.Append((char)('0' + slot));
            return;
        }

        // No arg-type match — mangle again WITH name backrefs for final output
        MangleType(sb, ty, isReturn: false);

        // Register the canonical key if multi-char and slots available
        if (canonical.Length > 1 && _argBackRefs.Count < 10)
            _argBackRefs[canonical] = _argBackRefs.Count;
    }

    private void MangleType(StringBuilder sb, CType ty, bool isReturn)
    {
        // Strip qualifiers for mangling
        switch (ty.Kind)
        {
            case TypeKind.Void: sb.Append('X'); break;
            case TypeKind.Bool: sb.Append("_N"); break;
            case TypeKind.Char:
                if (ty.IsUnsigned) sb.Append('E');
                else if (ty.Origin?.Kind == TypeKind.Char && !ty.IsUnsigned) sb.Append('D'); // plain char
                else sb.Append('D'); // default char is plain char
                break;
            case TypeKind.Short:
                sb.Append(ty.IsUnsigned ? 'G' : 'F');
                break;
            case TypeKind.Int:
                sb.Append(ty.IsUnsigned ? 'I' : 'H');
                break;
            case TypeKind.Enum:
                {
                    string enumName = GetTagName(ty);
                    if (enumName != null)
                    {
                        if (isReturn) sb.Append("?A");
                        sb.Append("W4");
                        MangleTagName(sb, enumName);
                    }
                    else
                    {
                        // Anonymous enum with no tag or typedef — mangle as underlying int
                        sb.Append(ty.IsUnsigned ? 'I' : 'H');
                    }
                    break;
                }
            case TypeKind.Long:
                if (_dm.LongSize == 4)
                    sb.Append(ty.IsUnsigned ? 'K' : 'J');
                else
                    sb.Append(ty.IsUnsigned ? "_K" : "_J"); // LP64: long=8 bytes
                break;
            case TypeKind.LLong:
                sb.Append(ty.IsUnsigned ? "_K" : "_J");
                break;
            case TypeKind.Float: sb.Append('M'); break;
            case TypeKind.Double: sb.Append('N'); break;
            case TypeKind.LDouble: sb.Append("O"); break; // long double in MSVC mangling
            case TypeKind.Ptr:
                ManglePointer(sb, ty);
                break;
            case TypeKind.Array:
                // Array parameter decays to pointer (handled by ManglePointer
                // when the parser produces Ptr(Array) via FuncParams decay).
                // This branch handles the 1D case; multi-dim is caught by
                // ManglePointer's baseTy.Kind == Array check.
                ManglePointer(sb, _types.PointerTo(ty.Base));
                break;
            case TypeKind.Struct:
                if (isReturn) sb.Append("?A");
                sb.Append('U');
                MangleTagName(sb, GetStructName(ty));
                break;
            case TypeKind.Union:
                if (isReturn) sb.Append("?A");
                sb.Append('T');
                MangleTagName(sb, GetStructName(ty));
                break;
            case TypeKind.Func:
                // Function pointer type
                MangleFuncPtr(sb, ty);
                break;
        }
    }

    /// <summary>
    /// Emit a struct/union/enum tag name with name-backref support.
    /// First occurrence: emit name + "@@" (global scope) and register in name table.
    /// Subsequent: emit digit + "@" (backref + scope terminator).
    /// </summary>
    private void MangleTagName(StringBuilder sb, string name)
    {
        if (_nameBackRefs != null)
        {
            int idx = _nameBackRefs.IndexOf(name);
            if (idx >= 0)
            {
                // Name backref: digit replaces name@, then @ for scope
                sb.Append((char)('0' + idx));
                sb.Append('@');
                return;
            }
            if (_nameBackRefs.Count < 10)
                _nameBackRefs.Add(name);
        }
        // First occurrence: name + @@ (global scope)
        sb.Append(name);
        sb.Append("@@");
    }

    private void ManglePointer(StringBuilder sb, CType ty)
    {
        string e = Is32 ? "" : "E"; // __ptr64 on 64-bit
        CType baseTy = ty.Base;

        if (baseTy.Kind == TypeKind.Func)
        {
            // Function pointer: P6/Q6/R6/S6 depending on pointer-self qualifiers
            MangleFuncPtr(sb, baseTy, ty.IsConst, ty.IsVolatile);
            return;
        }

        if (baseTy.Kind == TypeKind.Array)
        {
            // Pointer to array (from multi-dim array param decay):
            // emit pointer qualifiers + Y-encoded array dimensions
            char ptrQualArr;
            if (ty.IsConst && ty.IsVolatile) ptrQualArr = 'S';
            else if (ty.IsConst) ptrQualArr = 'Q';
            else if (ty.IsVolatile) ptrQualArr = 'R';
            else ptrQualArr = 'P';

            char pteeQualArr;
            if (baseTy.IsConst && baseTy.IsVolatile) pteeQualArr = 'D';
            else if (baseTy.IsConst) pteeQualArr = 'B';
            else if (baseTy.IsVolatile) pteeQualArr = 'C';
            else pteeQualArr = 'A';

            sb.Append($"{ptrQualArr}{e}{pteeQualArr}");
            MangleArrayDims(sb, baseTy);
            return;
        }

        // Pointer-self qualifiers: P=none, Q=const, R=volatile, S=const volatile
        char ptrQual;
        if (ty.IsConst && ty.IsVolatile) ptrQual = 'S';
        else if (ty.IsConst) ptrQual = 'Q';
        else if (ty.IsVolatile) ptrQual = 'R';
        else ptrQual = 'P';

        // Pointee qualifiers: A=none, B=const, C=volatile, D=const volatile
        char pteeQual;
        if (baseTy.IsConst && baseTy.IsVolatile) pteeQual = 'D';
        else if (baseTy.IsConst) pteeQual = 'B';
        else if (baseTy.IsVolatile) pteeQual = 'C';
        else pteeQual = 'A';

        sb.Append($"{ptrQual}{e}{pteeQual}");

        MangleType(sb, baseTy, isReturn: false);
    }

    private void MangleFuncPtr(StringBuilder sb, CType funcTy, bool ptrIsConst = false, bool ptrIsVolatile = false)
    {
        string cc = funcTy.CallConv switch
        {
            CallConv.Clrcall => "M",
            CallConv.Stdcall => "G", // only on x86
            _ => "A",
        };
        // Pointer-self qualifiers: P=none, Q=const, R=volatile, S=const volatile
        char ptrQual;
        if (ptrIsConst && ptrIsVolatile) ptrQual = 'S';
        else if (ptrIsConst) ptrQual = 'Q';
        else if (ptrIsVolatile) ptrQual = 'R';
        else ptrQual = 'P';
        sb.Append($"{ptrQual}6{cc}");
        MangleType(sb, funcTy.ReturnTy, isReturn: false);
        int count = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            // Func ptr params share the outer function's backref tables
            // (only when called from MangleFunctionName context)
            if (_argBackRefs != null)
                MangleArgType(sb, p);
            else
                MangleType(sb, p, isReturn: false);
            count++;
        }
        if (count == 0)
            sb.Append("XZ"); // void params inside func ptr also use XZ
        else if (funcTy.IsVariadic && funcTy.Params != null)
            sb.Append("ZZ");
        else
            sb.Append("@Z");
    }

    /// <summary>
    /// Emit MSVC Y-encoding for inner array dimensions in multi-dim array parameter decay.
    /// Format: Y<ndims><bound1>...<boundN><elemtype>
    /// </summary>
    private void MangleArrayDims(StringBuilder sb, CType ty)
    {
        sb.Append('Y');
        // Count inner dimensions and collect bounds
        int ndims = 0;
        var dims = new List<int>();
        CType cur = ty;
        while (cur.Kind == TypeKind.Array)
        {
            ndims++;
            dims.Add(cur.ArrayLen);
            cur = cur.Base;
        }
        sb.Append(EncodeNumber(ndims));
        foreach (int dim in dims)
            sb.Append(EncodeNumber(dim));
        MangleType(sb, cur, isReturn: false);
    }

    /// <summary>MSVC number encoding for array dimensions.</summary>
    private static string EncodeNumber(int value)
    {
        if (value == 0) return "A@";
        if (value >= 1 && value <= 10) return ((char)('0' + value - 1)).ToString();
        // Hex encoding: nibbles A-P (A=0, P=15), MSB first, terminated by @
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, (char)('A' + (value & 0xF)));
            value >>= 4;
        }
        sb.Append('@');
        return sb.ToString();
    }

    /// <summary>
    /// Generate array TypeDef name: $ArrayType$$$BY(ndims)(bounds)(elemtype)
    /// </summary>
    private string MangleArrayTypeName(CType ty)
    {
        Debug.Assert(ty.Kind == TypeKind.Array);

        // Neutralize backref tables — array TypeDef names must be stable keys
        // independent of which function's mangling state is active
        var savedNameBackRefs = _nameBackRefs;
        var savedArgBackRefs = _argBackRefs;
        _nameBackRefs = null;
        _argBackRefs = null;

        var sb = new StringBuilder("$ArrayType$$$BY");

        // Count dimensions
        int ndims = 0;
        var dims = new List<int>();
        CType cur = ty;
        while (cur.Kind == TypeKind.Array)
        {
            ndims++;
            dims.Add(cur.ArrayLen);
            cur = cur.Base;
        }
        sb.Append(EncodeNumber(ndims));
        foreach (int dim in dims)
            sb.Append(EncodeNumber(dim));

        // Element type code
        MangleType(sb, cur, isReturn: false);

        _nameBackRefs = savedNameBackRefs;
        _argBackRefs = savedArgBackRefs;
        return sb.ToString();
    }

    private string MangleStaticLocalName(Obj var)
    {
        return $"?A0x{_tuHash}.{var.Name}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 1: Metadata Registration
    // ═══════════════════════════════════════════════════════════════

    private void RegisterMetadata(Obj prog, string objName)
    {
        // Phase 1: Pre-allocate struct/array TypeDefs
        PreAllocateStructTypeDefs(prog);

        // Phase 2: <Module> TypeDef (must be row 1)
        _moduleTypeDef = _md.AddTypeDefinition(
            TypeAttributes.Class, default, _md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
            MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

        // Phase 3: Register functions
        RegisterFunctions(prog);

        // Phase 3b: Pre-register __unep@ fields for address-taken cdecl functions
        // (IJW-only — pure MSIL objects have no __unep@ machinery).
        if (_options.Target == TargetProfile.Ijw) RegisterUnepFields(prog);

        // Phase 4: Register global fields
        RegisterGlobalFields(prog);

        // Phase 5: Materialize struct/array TypeDefs
        MaterializeStructTypeDefs();

        // Module row
        _md.AddModule(0, _md.GetOrAddString(objName), _md.GetOrAddGuid(Guid.NewGuid()), default, default);
    }

    // ─── Phase 1: Pre-allocate struct/array TypeDefs ──────────────
    // We predict handles based on row position. <Module> is TypeDef row 1.
    // All struct/array TypeDefs will be rows 2, 3, 4, ... in the order they're discovered.
    private int _nextStructTypeDefRow = 2; // starts at 2 since <Module> is row 1

    private void PreAllocateStructTypeDefs(Obj prog)
    {
        var visited = new HashSet<Node>();
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            PreAllocateFromType(fn.Ty);
            if (fn.IsFunction && fn.IsDefinition && fn.IsLive)
            {
                for (Obj local = fn.Locals; local != null; local = local.Next)
                    PreAllocateFromType(local.Ty);
                for (Obj param = fn.Params; param != null; param = param.Next)
                    PreAllocateFromType(param.Ty);
                if (fn.Body != null)
                    PreAllocateFromNode(fn.Body, visited);
            }
        }
    }

    private void PreAllocateFromType(CType ty)
    {
        if (ty == null) return;
        CType canonical = ty;
        while (canonical.Origin != null) canonical = canonical.Origin;

        switch (canonical.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                if (canonical.Members != null) // Only complete types
                {
                    // Skip nested member types — they're flattened into the parent
                    if (canonical.IsNestedMember) break;
                    int id = GetTypeId(canonical);
                    if (!_structTypeDefs.ContainsKey(id))
                    {
                        // Reserve a predicted handle
                        var predictedHandle = MetadataTokens.TypeDefinitionHandle(_nextStructTypeDefRow++);
                        _structTypeDefs[id] = predictedHandle;
                        string name = GetStructName(canonical);
                        _pendingTypeDefs.Add((id, canonical, name));

                        // Do NOT recurse into member types — nested structs/unions are
                        // flattened into the parent as opaque byte ranges, matching MSVC
                        // /clr /BC behavior. TypeDefs are only created for types that
                        // appear directly in function signatures, local/global variable
                        // types, and pointer targets.
                    }
                }
                break;
            case TypeKind.Array:
                if (canonical.ArrayLen >= 0)
                {
                    string arrayName = MangleArrayTypeName(canonical);
                    if (!_arrayTypeDefs.ContainsKey(arrayName))
                    {
                        var predictedHandle = MetadataTokens.TypeDefinitionHandle(_nextStructTypeDefRow++);
                        _arrayTypeDefs[arrayName] = predictedHandle;
                        _pendingTypeDefs.Add((0, canonical, arrayName));
                    }
                    PreAllocateFromType(canonical.Base);
                }
                break;
            case TypeKind.Ptr:
                PreAllocateFromType(canonical.Base);
                break;
            case TypeKind.Func:
                PreAllocateFromType(canonical.ReturnTy);
                for (CType p = canonical.Params; p != null; p = p.Next)
                    PreAllocateFromType(p);
                break;
        }
    }

    private void PreAllocateFromNode(Node node, HashSet<Node> visited)
    {
        if (node == null || !visited.Add(node)) return;
        if (node.Ty != null)
        {
            // Don't create TypeDefs for struct/union types that appear only as
            // member-access intermediaries. MSVC flattens nested struct members
            // into the parent — no TypeDef for `struct Inner` in `o.inner.a`.
            // The type will still get a TypeDef if it's used independently in a
            // function signature, local variable, or global variable.
            bool isMemberAccess = node.Kind == NodeKind.Member &&
                (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union);
            if (!isMemberAccess)
                PreAllocateFromType(node.Ty);
        }
        if (node.FuncTy != null) PreAllocateFromType(node.FuncTy);
        PreAllocateFromNode(node.Lhs, visited);
        PreAllocateFromNode(node.Rhs, visited);
        PreAllocateFromNode(node.Cond, visited);
        PreAllocateFromNode(node.Then, visited);
        PreAllocateFromNode(node.Els, visited);
        PreAllocateFromNode(node.Init, visited);
        PreAllocateFromNode(node.Inc, visited);
        PreAllocateFromNode(node.Body, visited);
        PreAllocateFromNode(node.Next, visited);
        for (Node arg = node.Args; arg != null; arg = arg.Next)
            PreAllocateFromNode(arg, visited);
        PreAllocateFromNode(node.CasAddr, visited);
        PreAllocateFromNode(node.CasOld, visited);
        PreAllocateFromNode(node.CasNew, visited);
        PreAllocateFromNode(node.AtomicExpr, visited);
    }

    // ─── Phase 3: Register functions ─────────────────────────────

    private void RegisterFunctions(Obj prog)
    {
        // Pass A: defined functions → MethodDef
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            RegisterFunction(fn);
        }

        // Pass B: External function MemberRefs are created on-demand during IL emission
        // (GenFunCall calls RegisterExternalFunction when it encounters a call to an
        // undefined function). MSVC only emits MemberRefs for functions that actually
        // appear in IL — declared-but-never-called functions don't get MemberRefs.
    }

    private void RegisterFunction(Obj fn)
    {
        CType funcTy = fn.Ty;
        bool isCdecl = funcTy.CallConv != CallConv.Clrcall;

        // Build method signature
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT calling convention

        // Parameter count
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        // Real variadic definitions (explicit prototype + ...) carry a hidden
        // trailing va-buffer pointer param. K&R unprototyped functions also set
        // IsVariadic but have Params == null and must be left unchanged.
        bool hasVaPtr = funcTy.IsVariadic && funcTy.Params != null;
        if (hasVaPtr) paramCount++;
        sig.WriteCompressedInteger(paramCount);

        // Return type
        EncodeReturnType(sig, funcTy);

        // Parameters
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);
        if (hasVaPtr)
            EncodeType(sig, _types.TyVaList); // hidden __va pointer

        // Method attributes
        MethodAttributes attrs = MethodAttributes.Assembly | MethodAttributes.Static;
        if (isCdecl && !fn.IsStatic)
            attrs |= (MethodAttributes)0x0008; // UnmanagedExport

        var methodDef = _md.AddMethodDefinition(
            attrs,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            _md.GetOrAddString(fn.Name),
            _md.GetOrAddBlob(sig),
            0,
            MetadataTokens.ParameterHandle(_nextParamRow));
        _nextMethodRow++;

        // Add parameter rows
        int paramIdx = 1;
        for (CType p = funcTy.Params; p != null; p = p.Next)
        {
            string paramName = p.Name != null ? Util.GetTokenText(p.Name) : $"_a{paramIdx}";
            _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString(paramName), paramIdx);
            _nextParamRow++;
            paramIdx++;
        }
        if (hasVaPtr)
        {
            _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("__va"), paramIdx);
            _nextParamRow++;
            paramIdx++;
        }

        _methodDefs[fn] = methodDef;

        // Pre-register COFF symbol
        string mangledName = MangleFunctionName(fn);
        _symtab.PreRegisterFunctionClrToken(mangledName, methodDef);

        // If this is main and targeting IJW, register __CxxPureMSILEntry.
        // In CoreCLR mode the shim is never emitted, so skip registration to
        // avoid orphaned MethodDef rows and COFF symbols pointing at byte 0 of
        // .text$mn.
        if (fn.Name == "main" && _options.Target == TargetProfile.Ijw)
        {
            _hasMain = true;
            _mainMethod = methodDef;
            _mainObj = fn;
            RegisterCxxPureMSILEntry(fn);
        }
    }

    private void RegisterCxxPureMSILEntry(Obj mainFn)
    {
        // Signature: int __clrcall(int argc, char** argv, char** envp)
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT calling convention
        sig.WriteCompressedInteger(3); // 3 params

        // Return type: int32 (no CallConvCdecl modopt — this is __clrcall)
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        // Param 1: int argc
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        // Param 2: char** argv — Ptr Ptr modopt(IsSignUnspecifiedByte) SByte.
        // The IsSignUnspecifiedByte modopt marks plain `char` whose signedness
        // is implementation-defined; MSVC and asm2obj both emit it on `char**`
        // params and link.exe compares signature bytes including this marker.
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
        sig.WriteByte((byte)SignatureTypeCode.SByte);

        // Param 3: char** envp — same encoding, '0' backreference in mangling.
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.OptionalModifier);
        sig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(GetIsSignUnspecifiedByteRef()));
        sig.WriteByte((byte)SignatureTypeCode.SByte);

        _cxxPureMsilEntry = _md.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            _md.GetOrAddString("__CxxPureMSILEntry"),
            _md.GetOrAddBlob(sig),
            0,
            MetadataTokens.ParameterHandle(_nextParamRow));
        _nextMethodRow++;

        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("argc"), 1);
        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("argv"), 2);
        _md.AddParameter(ParameterAttributes.None, _md.GetOrAddString("envp"), 3);
        _nextParamRow += 3;

        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _symtab.PreRegisterFunctionClrToken(mangledName, _cxxPureMsilEntry);
    }

    private void RegisterExternalFunction(Obj fn)
    {
        CType funcTy = fn.Ty;

        // Build MemberRef signature
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT
        int paramCount = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
        // Real variadic callees (explicit prototype + ...) carry a hidden
        // trailing va-buffer pointer param so a forward-declared MemberRef
        // matches the definition's MethodDef sig. K&R unprototyped (Params==null)
        // are left unchanged.
        bool hasVaPtr = funcTy.IsVariadic && funcTy.Params != null;
        if (hasVaPtr) paramCount++;
        sig.WriteCompressedInteger(paramCount);
        EncodeReturnType(sig, funcTy);
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);
        if (hasVaPtr)
            EncodeType(sig, _types.TyVaList); // hidden __va pointer

        var memberRef = _md.AddMemberReference(
            _moduleTypeDef, _md.GetOrAddString(fn.Name), _md.GetOrAddBlob(sig));
        _externalFuncRefs[fn.Name] = memberRef;

        // Add DecoratedNameAttribute
        string mangledName = MangleFunctionName(fn);
        AddDecoratedNameAttribute(memberRef, mangledName);

        // Register external CLR token
        _symtab.AddExternalClrToken(mangledName, memberRef);
    }

    /// <summary>
    /// Layer 2: build a MemberRef for a native __cdecl variadic callee whose
    /// signature is monomorphized to the concrete call site: the fixed prototype
    /// params followed by the default-promoted types of the actual variadic args.
    /// No hidden va-buffer pointer is emitted. Each call site may produce a
    /// distinct MemberRef (different concrete arg types ⇒ different signature),
    /// so this deliberately does NOT consult / populate the by-name
    /// <c>_externalFuncRefs</c> cache (which holds one fixed-only signature per
    /// name and would otherwise alias mismatched signatures).
    /// </summary>
    private MemberReferenceHandle RegisterConcreteVarargCall(Obj fn, CType funcTy, Node firstArg)
    {
        int nFixed = 0;
        for (CType p = funcTy.Params; p != null; p = p.Next) nFixed++;

        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT conv byte; cdecl is encoded via a modopt
                             // on the return type (see EncodeReturnType), matching
                             // how chibil encodes every other cdecl external.

        // Param count = fixed params + concrete (variadic) args.
        int totalArgs = 0;
        for (Node a = firstArg; a != null; a = a.Next) totalArgs++;
        sig.WriteCompressedInteger(totalArgs);

        EncodeReturnType(sig, funcTy);

        // Fixed params from the prototype.
        for (CType p = funcTy.Params; p != null; p = p.Next)
            EncodeType(sig, p);

        // Concrete variadic args, with default argument promotions applied.
        int idx = 0;
        for (Node a = firstArg; a != null; a = a.Next, idx++)
        {
            if (idx < nFixed) continue;
            EncodeType(sig, VaPromote(a.Ty));
        }

        var memberRef = _md.AddMemberReference(
            _moduleTypeDef, _md.GetOrAddString(fn.Name), _md.GetOrAddBlob(sig));

        string mangledName = MangleFunctionName(fn);
        AddDecoratedNameAttribute(memberRef, mangledName);
        _symtab.AddExternalClrToken(mangledName, memberRef);

        return memberRef;
    }

    private void AddDecoratedNameAttribute(EntityHandle target, string mangledName)
    {
        // DecoratedNameAttribute custom attribute
        // We need a MemberRef to the constructor: .ctor(string)
        // For now, use raw blob encoding
        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteSerializedString(mangledName);
        attrBlob.WriteUInt16(0x0000); // NumNamed

        // TypeRef for DecoratedNameAttribute
        var decoratedNameRef = _md.AddTypeReference(_mscorlibRef,
            _md.GetOrAddString("System.Runtime.CompilerServices"),
            _md.GetOrAddString("DecoratedNameAttribute"));

        // MemberRef for .ctor(string)
        var ctorSig = new BlobBuilder();
        ctorSig.WriteByte(0x20); // HASTHIS
        ctorSig.WriteCompressedInteger(1); // 1 param
        ctorSig.WriteByte((byte)SignatureTypeCode.Void); // return void
        ctorSig.WriteByte((byte)SignatureTypeCode.String); // param: string

        var ctorRef = _md.AddMemberReference(decoratedNameRef, _md.GetOrAddString(".ctor"), _md.GetOrAddBlob(ctorSig));

        _md.AddCustomAttribute(target, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    // ─── Phase 3b: Pre-register __unep@ fields ─────────────────────

    private void RegisterUnepFields(Obj prog)
    {
        foreach (string funcName in _addressTakenFuncs)
        {
            // Find the function (defined or extern)
            Obj fn = null;
            for (Obj f = prog; f != null; f = f.Next)
            {
                if (f.IsFunction && f.Name == funcName && f.Ty.CallConv != CallConv.Clrcall)
                {
                    fn = f; break;
                }
            }
            if (fn == null) continue;

            string mangledName = MangleFunctionName(fn);
            string unepName = $"__unep@{mangledName}";

            var unepFieldSig = new BlobBuilder();
            unepFieldSig.WriteByte(0x06); // FIELD
            unepFieldSig.WriteByte((byte)SignatureTypeCode.IntPtr);

            var unepField = _md.AddFieldDefinition(
                FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
                _md.GetOrAddString(unepName), _md.GetOrAddBlob(unepFieldSig));
            _nextFieldRow++;
            _md.AddFieldRelativeVirtualAddress(unepField, 0);

            _unepFields[funcName] = unepField;
        }
    }

    // ─── Phase 4: Register global fields ─────────────────────────

    private void RegisterGlobalFields(Obj prog)
    {
        // Only DEFINITIONS get a field up front. Extern declarations are registered
        // lazily, on first reference by emitted IL (see GetOrRegisterGlobalField), so
        // an UNUSED extern produces no symbol at all — matching a real linker (ld emits
        // nothing for an unused extern). Eagerly emitting extern fields turned every
        // unused declaration into a phantom undefined symbol that chibil-link then
        // misclassified as a native data import.
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction || !g.IsDefinition) continue;
            RegisterGlobalField(g);
        }
    }

    /// <summary>Field for a referenced global; an extern's field is created here on
    /// first IL reference (so unused externs never get one).</summary>
    private FieldDefinitionHandle GetOrRegisterGlobalField(Obj g)
    {
        if (_fieldDefs.TryGetValue(g, out var fd)) return fd;
        if (_globalFieldsByName.TryGetValue(g.Name, out var fd2)) return fd2;
        RegisterExternField(g);
        return _fieldDefs[g];
    }

    private void RegisterGlobalField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        string fieldName;
        if (g.StaticLocalFn != null)
        {
            fieldName = MangleStaticLocalName(g);
        }
        else if (g.IsAnonymous)
        {
            fieldName = $"?A0x{_tuHash}.unnamed-global-{_anonGlobalCounter++}";
        }
        else
        {
            fieldName = g.Name;
        }

        FieldAttributes fieldAttrs = FieldAttributes.Assembly | FieldAttributes.Static;

        // All global definitions get HasFieldRVA — even tentative (common) definitions
        // and zero-initialized globals. The COFF symbol table determines whether
        // the symbol is section-bound (.data/.bss) or common (Sect=0, Value=size).
        fieldAttrs |= FieldAttributes.HasFieldRVA;

        var fieldDef = _md.AddFieldDefinition(fieldAttrs,
            _md.GetOrAddString(fieldName), _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        // FieldRVA table entry required when HasFieldRVA is set.
        // Actual RVA is 0 — resolved via COFF relocations at link time.
        if ((fieldAttrs & FieldAttributes.HasFieldRVA) != 0)
            _md.AddFieldRelativeVirtualAddress(fieldDef, 0);

        _fieldDefs[g] = fieldDef;
        _globalFieldsByName[g.Name] = fieldDef;
    }

    private void RegisterExternField(Obj g)
    {
        var fieldSig = new BlobBuilder();
        fieldSig.WriteByte(0x06); // FIELD
        EncodeType(fieldSig, g.Ty);

        FieldAttributes attrs = FieldAttributes.Assembly | FieldAttributes.Static;

        var fieldDef = _md.AddFieldDefinition(attrs,
            _md.GetOrAddString(g.Name), _md.GetOrAddBlob(fieldSig));
        _nextFieldRow++;

        _fieldDefs[g] = fieldDef;
        _globalFieldsByName[g.Name] = fieldDef;
    }

    // ─── Phase 5: Materialize struct/array TypeDefs ───────────────

    private void MaterializeStructTypeDefs()
    {
        foreach (var (typeId, type, name) in _pendingTypeDefs)
        {
            TypeDefinitionHandle handle;

            if (type.Kind == TypeKind.Array)
            {
                var predicted = _arrayTypeDefs[name];
                handle = _md.AddTypeDefinition(
                    TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
                    default, _md.GetOrAddString(name),
                    GetValueTypeRef(),
                    MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                    MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

                Debug.Assert(handle == predicted, $"Array TypeDef handle mismatch: predicted {predicted}, got {handle}");
                _md.AddTypeLayout(handle, 0, (uint)type.Size);
            }
            else
            {
                var predicted = _structTypeDefs[typeId];
                // Unions use ExplicitLayout (all members at offset 0);
                // structs use SequentialLayout
                var layoutAttr = type.Kind == TypeKind.Union
                    ? TypeAttributes.ExplicitLayout
                    : TypeAttributes.SequentialLayout;
                handle = _md.AddTypeDefinition(
                    layoutAttr | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
                    default, _md.GetOrAddString(name),
                    GetValueTypeRef(),
                    MetadataTokens.FieldDefinitionHandle(_nextFieldRow),
                    MetadataTokens.MethodDefinitionHandle(_nextMethodRow));

                Debug.Assert(handle == predicted, $"Struct TypeDef handle mismatch: predicted {predicted}, got {handle}");
                _md.AddTypeLayout(handle, 0, (uint)type.Size);
            }

            // NativeCppClassAttribute
            AddNativeCppClassAttribute(handle);

            // <alignment member> field (on 64-bit targets, structs/unions only — not arrays)
            if (!Is32 && type.Kind != TypeKind.Array)
            {
                var alignFieldSig = new BlobBuilder();
                alignFieldSig.WriteByte(0x06); // FIELD
                // Use int64 if any member needs 8-byte alignment, else int32
                bool needs8 = type.Align >= 8;
                alignFieldSig.WriteByte(needs8 ? (byte)SignatureTypeCode.Int64 : (byte)SignatureTypeCode.Int32);

                var alignField = _md.AddFieldDefinition(
                    FieldAttributes.Private,
                    _md.GetOrAddString("<alignment member>"),
                    _md.GetOrAddBlob(alignFieldSig));
                _nextFieldRow++;

                // For ExplicitLayout (unions), set field offset to 0
                // (MSVC /clr C++ uses offset 0; /clr /BC incorrectly uses 0xFFFFFFFF)
                if (type.Kind == TypeKind.Union)
                    _md.AddFieldLayout(alignField, 0);
            }
        }
    }

    private void AddNativeCppClassAttribute(TypeDefinitionHandle handle)
    {
        var attrRef = GetNativeCppClassAttrRef();

        // MemberRef for .ctor()
        var ctorSig = new BlobBuilder();
        ctorSig.WriteByte(0x20); // HASTHIS
        ctorSig.WriteCompressedInteger(0);
        ctorSig.WriteByte((byte)SignatureTypeCode.Void);

        var ctorRef = _md.AddMemberReference(attrRef, _md.GetOrAddString(".ctor"), _md.GetOrAddBlob(ctorSig));

        var attrBlob = new BlobBuilder();
        attrBlob.WriteUInt16(0x0001); // Prolog
        attrBlob.WriteUInt16(0x0000); // NumNamed

        _md.AddCustomAttribute(handle, ctorRef, _md.GetOrAddBlob(attrBlob));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pass 2: IL Emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitFunctions(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            try { EmitFunction(fn); }
            catch (Exception ex) when (ex is not ChibiException)
            {
                throw new InvalidOperationException(
                    $"codegen failed in function '{fn.Name}': {ex.Message}", ex);
            }
        }
    }

    private void EmitFunction(Obj fn)
    {
        _currentFn = fn;
        _enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
        _localSlots = new Dictionary<Obj, int>();
        _dbgScopeRanges = new Dictionary<int, (int, int)>();
        _paramSlots = new Dictionary<Obj, int>();
        _scratchLocals = new List<(CType, int)>();
        _vaArgPApLocal = -1;
        _vaStartStructLocal = -1;
        _vaCopyStructLocal = -1;
        _setjmpWrap = false;
        _setjmpRetvalLocal = -1;
        _setjmpBufLocal = -1;
        _setjmpSites = null;
        _setjmpTryOpen = false;
        _setjmpDeferStart = false;
        _vaArgApLocal = -1;
        _maxStack = 0;
        _stackDepth = 0;
        _labels = new Dictionary<string, LabelHandle>();
        _labelCount = 0;

        // Assign parameter slots
        int argIdx = 0;
        for (Obj param = fn.Params; param != null; param = param.Next)
            _paramSlots[param] = argIdx++;

        // Assign local slots for user locals
        int localIdx = 0;
        for (Obj local = fn.Locals; local != null; local = local.Next)
        {
            if (local.IsLocal && !_paramSlots.ContainsKey(local))
            {
                if (local == fn.AllocaBottom) continue; // skip alloca bottom
                _localSlots[local] = localIdx++;
            }
        }
        _scratchLocalBase = localIdx;

        // Emit function body. A function that calls setjmp is wrapped in a
        // try/filter/handler so a longjmp targeting it resumes (see the field block).
        _setjmpWrap = NodeContainsSetjmp(fn.Body);
        if (_setjmpWrap)
        {
            EmitSetjmpWrappedBody(fn);
        }
        else
        {
            GenStmt(fn.Body);

            // Epilogue — fallthrough return
            if (_labels.TryGetValue($".L.return.{fn.Name}", out var retLabel))
                _enc.MarkLabel(retLabel);

            if (fn.Ty.ReturnTy.Kind != TypeKind.Void)
            {
                EmitDefaultValue(fn.Ty.ReturnTy);
            }
            _enc.OpCode(ILOpCode.Ret);
        }

        // Build locals signature
        int totalLocals = _scratchLocalBase + _scratchLocals.Count;
        StandaloneSignatureHandle localsSig = default;
        if (totalLocals > 0)
        {
            var localsSigBlob = new BlobBuilder();
            var enc = new BlobEncoder(localsSigBlob).LocalVariableSignature(totalLocals);

            // User locals
            for (Obj local = fn.Locals; local != null; local = local.Next)
            {
                if (_localSlots.ContainsKey(local))
                    EncodeLocalType(enc.AddVariable().Type(), local.Ty);
            }

            // Scratch locals
            foreach (var (ty, _) in _scratchLocals)
                EncodeLocalType(enc.AddVariable().Type(), ty);

            localsSig = _md.AddStandaloneSignature(_md.GetOrAddBlob(localsSigBlob));
        }
        _localsSigHandle = localsSig;

        // Build CodeView local slot info
        var localSlotList = new List<CodeViewManSlot>();
        foreach (var (local, slot) in _localSlots)
        {
            if (local.Name != null && localsSig != default)
            {
                localSlotList.Add(new CodeViewManSlot(slot,
                    MetadataTokens.GetToken(localsSig), local.Name));
            }
        }

        // Finalize method body
        var methodDef = _methodDefs[fn];
        string mangledName = MangleFunctionName(fn);

        int bodyOffset = _bodyEncoder.AddMethodBody(methodDef, mangledName, _enc,
            maxStack: _maxStack, localVariablesSignature: localsSig, attributes: MethodBodyAttributes.InitLocals,
            debugName: fn.Name,
            localSlots: localSlotList.Count > 0 ? localSlotList.ToArray() : null);
        _methodBodyOffsets[fn] = bodyOffset;

        // Collect line points + named locals for the managed-PDB side-stream
        // (chibil-link dedupes/sorts and builds the Portable PDB).
        var pts = new List<(int Il, int Line, int StartCol, int EndCol)>();
        foreach (var (_, off, line, sc, ec) in _enc.LineNumberBuilder.Entries())
            pts.Add((off, line, sc, ec));

        // Group named locals by their declaring lexical scope, attaching each scope's
        // measured IL range (or the whole method when unmeasured, e.g. the function
        // scope). chibil-link emits one nested LocalScope per group so shadowed names
        // resolve to the innermost block.
        int ilSize = _enc.CodeBuilder.Count;
        var byScope = new Dictionary<int, List<(int Slot, string Name)>>();
        if (localsSig != default)
            foreach (var (local, slot) in _localSlots)
            {
                if (local.Name == null) continue;
                if (!byScope.TryGetValue(local.ScopeId, out var list))
                    byScope[local.ScopeId] = list = new List<(int, string)>();
                list.Add((slot, local.Name));
            }
        var scopes = new List<(int Start, int Len, List<(int Slot, string Name)> Locals)>();
        foreach (var (scopeId, locals) in byScope)
        {
            var (start, len) = _dbgScopeRanges.TryGetValue(scopeId, out var r) ? r : (0, ilSize);
            scopes.Add((start, len, locals));
        }
        if (pts.Count > 0 || scopes.Count > 0)
            _dbgMethods.Add((MetadataTokens.GetRowNumber(methodDef), ilSize, pts, scopes));

        _currentFn = null;
    }

    private void EncodeLocalType(SignatureTypeEncoder enc, CType ty)
    {
        // Encode using the builder directly
        EncodeType(enc.Builder, ty);
    }

    private void EmitDefaultValue(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push(); break;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push(); break;
            case TypeKind.LLong:
                _enc.LoadConstantI8(0); Push(); break;
            case TypeKind.Struct:
            case TypeKind.Union:
                // For struct return, push a zeroed struct
                int scratch = GetOrAddScratchLocal(ty);
                _enc.LoadLocalAddress(scratch); Push();
                _enc.OpCode(ILOpCode.Initobj); _enc.Token(GetStructTypeHandle(ty)); Pop();
                _enc.LoadLocal(scratch); Push();
                break;
            default:
                _enc.OpCode(ILOpCode.Ldc_i4_0); Push(); break;
        }
    }

    private EntityHandle GetStructTypeHandle(CType ty)
    {
        int typeId = GetTypeId(ty);
        if (_structTypeDefs.TryGetValue(typeId, out var handle))
            return handle;
        // Forward-declared
        string name = GetStructName(ty);
        if (_forwardDeclTypeRefs.TryGetValue(name, out var typeRef))
            return typeRef;
        return default;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scratch locals
    // ═══════════════════════════════════════════════════════════════

    private int GetOrAddScratchLocal(CType ty)
    {
        // Reuse existing scratch local of same type.
        // For struct/union/array, require exact type identity (TypeDef handle match)
        // since IL verifier requires assignment-compatible value types.
        foreach (var (existingTy, slot) in _scratchLocals)
        {
            if (existingTy.Kind == ty.Kind && existingTy.Size == ty.Size &&
                existingTy.IsUnsigned == ty.IsUnsigned)
            {
                // Struct/union/array: only reuse if same canonical type
                if (ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union || ty.Kind == TypeKind.Array)
                {
                    if (GetTypeId(existingTy) == GetTypeId(ty))
                        return slot;
                    continue; // different struct type, keep looking
                }
                return slot;
            }
        }
        return AddFreshScratchLocal(ty);
    }

    private int AddFreshScratchLocal(CType ty)
    {
        int newSlot = _scratchLocalBase + _scratchLocals.Count;
        _scratchLocals.Add((ty, newSlot));
        return newSlot;
    }

    // ═══════════════════════════════════════════════════════════════
    //  setjmp / longjmp (MUSL-3) — managed-exception resumption
    // ═══════════════════════════════════════════════════════════════

    private static bool IsSetjmpName(string n) =>
        n is "setjmp" or "_setjmp" or "__setjmp" or "sigsetjmp" or "__sigsetjmp";

    /// <summary>True if the statement/expression tree calls setjmp anywhere — that
    /// function must be wrapped so a longjmp can resume it.</summary>
    private static bool NodeContainsSetjmp(Node n)
    {
        for (; n != null; n = n.Next)
        {
            if (n.Kind == NodeKind.FunCall && n.Lhs != null && n.Lhs.Kind == NodeKind.Var
                && n.Lhs.Var != null && n.Lhs.Var.IsFunction && IsSetjmpName(n.Lhs.Var.Name))
                return true;
            if (NodeContainsSetjmp(n.Lhs) || NodeContainsSetjmp(n.Rhs) || NodeContainsSetjmp(n.Cond)
                || NodeContainsSetjmp(n.Then) || NodeContainsSetjmp(n.Els) || NodeContainsSetjmp(n.Init)
                || NodeContainsSetjmp(n.Inc) || NodeContainsSetjmp(n.Body) || NodeContainsSetjmp(n.Args))
                return true;
        }
        return false;
    }

    /// <summary>Count setjmp call sites in a tree and whether any is lexically inside a
    /// <em>cond loop</em> — a `for`/`while` with a condition. Such a loop emits a forward
    /// exit (`brfalse brkLabel`) from before the body to a label after the loop; with the
    /// resume-at-site try starting inside the loop body, that label is inside the try and
    /// the exit would be an illegal branch INTO the protected region. `for(;;)` (no cond)
    /// and `do/while` (cond at the back-edge) have no such forward exit and are safe for
    /// resume-at-site (their back-edges become `leave` via trampolines).</summary>
    private static (int count, bool insideCondLoop) CountSetjmp(Node n, bool inCondLoop = false)
    {
        int count = 0;
        bool condHit = false;
        for (; n != null; n = n.Next)
        {
            if (n.Kind == NodeKind.FunCall && n.Lhs != null && n.Lhs.Kind == NodeKind.Var
                && n.Lhs.Var != null && n.Lhs.Var.IsFunction && IsSetjmpName(n.Lhs.Var.Name))
            {
                count++;
                if (inCondLoop) condHit = true;
            }
            bool childInCondLoop = inCondLoop || (n.Kind == NodeKind.For && n.Cond != null);
            foreach (var c in new[] { n.Lhs, n.Rhs, n.Cond, n.Then, n.Els, n.Init, n.Inc, n.Body, n.Args })
            {
                var (cc, cl) = CountSetjmp(c, childInCondLoop);
                count += cc;
                condHit |= cl;
            }
        }
        return (count, condHit);
    }

    // Default-convention MemberRef signatures for the linker-synthesized helpers.
    private static readonly byte[] RtLongjmp = { 0x00, 0x02, 0x01, 0x18, 0x08 }; // void(native int, int32)
    private static readonly byte[] RtMatch   = { 0x00, 0x01, 0x08, 0x18 };       // int32(native int)
    private static readonly byte[] RtBuf     = { 0x00, 0x00, 0x18 };             // native int()
    private static readonly byte[] RtVal     = { 0x00, 0x00, 0x08 };             // int32()
    private static readonly byte[] RtClear   = { 0x00, 0x00, 0x01 };             // void()

    private MemberReferenceHandle RuntimeHelperRef(string name, byte[] sig)
    {
        if (!_runtimeHelperRefs.TryGetValue(name, out var mr))
        {
            mr = _md.AddMemberReference(_moduleTypeDef, _md.GetOrAddString(name), _md.GetOrAddBlob(sig));
            _runtimeHelperRefs[name] = mr;
        }
        return mr;
    }

    private void EmitRuntimeCall(string name, byte[] sig, int nArgs, bool hasRet)
    {
        _enc.Call(RuntimeHelperRef(name, sig));
        if (nArgs > 0) Pop(nArgs);
        if (hasRet) Push();
    }

    /// <summary>
    /// Branch to <paramref name="target"/>, converting a branch that exits the
    /// resume-at-site setjmp try region (target marked before tryStart) into a jump to
    /// a trampoline that <c>leave</c>s — a plain <c>br</c> out of a protected region is
    /// illegal IL. Conditional branches are handled uniformly: the conditional branch
    /// goes to the trampoline, which does the unconditional leave. The caller still
    /// performs its own stack <c>Pop()</c> for conditional opcodes, as before.
    /// </summary>
    private void SjBranch(ILOpCode op, LabelHandle target)
    {
        if (_setjmpDeferStart && _setjmpTryOpen && _setjmpOuterLabels.Contains(target))
        {
            var tramp = _enc.DefineLabel();
            _enc.Branch(op, tramp);
            _setjmpLeaveTrampolines.Add((tramp, target));
        }
        else
        {
            _enc.Branch(op, target);
        }
    }

    /// <summary>Emit `Lhead: .try { body } filter { ours? } handler { resume }` plus
    /// the epilogue outside the try. Body returns funnel through `leave` (see GenStmt
    /// Return). Site list is populated by setjmp lowering DURING GenStmt.</summary>
    private void EmitSetjmpWrappedBody(Obj fn)
    {
        _setjmpSites = new List<(Node, int)>();
        _setjmpRetvalLocal = fn.Ty.ReturnTy.Kind != TypeKind.Void ? AddFreshScratchLocal(fn.Ty.ReturnTy) : -1;
        _setjmpBufLocal = AddFreshScratchLocal(_types.TyVaList);   // native-int sized

        _setjmpEpiLabel = _enc.DefineLabel();
        _setjmpLhead = _enc.DefineLabel();
        _setjmpTryStartLabel = _enc.DefineLabel();
        var tryStart = _setjmpTryStartLabel;
        var tryEnd = _enc.DefineLabel();
        var filterStart = _enc.DefineLabel();
        var handlerStart = _enc.DefineLabel();
        var handlerEnd = _enc.DefineLabel();

        // Resume-at-site mode: a single setjmp not inside a loop lets us start the try
        // region AT the setjmp call, so code sequenced before it runs exactly once
        // (outside the try) and a longjmp resumes after the call — correct setjmp
        // semantics. Otherwise wrap the whole body (re-from-top), the prior behavior.
        // Resume-at-site for a single setjmp, including inside for(;;)/do-while loops
        // (back-edges become `leave`). Excludes cond loops (for/while with a cond), whose
        // forward exit would branch into the try; those keep the re-from-top strategy.
        var (sjCount, sjInCondLoop) = CountSetjmp(fn.Body);
        _setjmpDeferStart = sjCount == 1 && !sjInCondLoop;
        _setjmpOuterLabels.Clear();
        _setjmpLeaveTrampolines.Clear();

        if (!_setjmpDeferStart)
        {
            // Lhead must sit OUTSIDE the try: the handler `leave Lhead`s to resume, and
            // leaving INTO a try is illegal. A nop separates Lhead from tryStart so the
            // leave lands before the try and falls into it.
            _enc.MarkLabel(_setjmpLhead);
            _enc.OpCode(ILOpCode.Nop);
            _enc.MarkLabel(tryStart);
            _setjmpTryOpen = true;
        }
        // In defer mode, the (single) setjmp lowering marks Lhead/tryStart at its site.
        GenStmt(fn.Body);
        // Normal fall-through end of the try -> leave to the epilogue.
        _enc.Branch(ILOpCode.Leave, _setjmpEpiLabel);
        // Leave-trampolines for branches that exit the try to a label marked before
        // tryStart (loop back-edges, gotos to a pre-setjmp label). Reachable only via
        // the redirected branches; each ends in an unconditional leave (no fall-through).
        foreach (var (tramp, target) in _setjmpLeaveTrampolines)
        {
            _enc.MarkLabel(tramp);
            _enc.Branch(ILOpCode.Leave, target);
        }
        _enc.MarkLabel(tryEnd);

        _enc.MarkLabel(filterStart);
        EmitSetjmpFilter();
        _enc.MarkLabel(handlerStart);
        EmitSetjmpHandler();
        _enc.MarkLabel(handlerEnd);
        _enc.ControlFlowBuilder.AddFilterRegion(tryStart, tryEnd, handlerStart, handlerEnd, filterStart);

        // Epilogue (outside the try): return the value funnelled into the retval local.
        _enc.MarkLabel(_setjmpEpiLabel);
        _stackDepth = 0;
        if (_setjmpRetvalLocal >= 0) { _enc.LoadLocal(_setjmpRetvalLocal); Push(); }
        _enc.OpCode(ILOpCode.Ret);
        if (_setjmpRetvalLocal >= 0) Pop();
        _setjmpWrap = false;
    }

    // filter: runtime pushes the exception. Return 1 iff an active longjmp targets one
    // of this function's setjmp buffers (so a real exception / outer-target longjmp is
    // not caught here).
    private void EmitSetjmpFilter()
    {
        _stackDepth = 1; if (_stackDepth > _maxStack) _maxStack = _stackDepth;
        _enc.OpCode(ILOpCode.Pop); Pop();                 // discard the exception object
        var Lyes = _enc.DefineLabel();
        var Lend = _enc.DefineLabel();
        foreach (var (jb, _) in _setjmpSites)
        {
            GenExpr(jb);                                  // &jb (decayed pointer)
            EmitRuntimeCall("__chibil_longjmp_match", RtMatch, nArgs: 1, hasRet: true);
            _enc.Branch(ILOpCode.Brtrue, Lyes); Pop();
        }
        EmitConstI4(0);
        _enc.Branch(ILOpCode.Br, Lend);
        _enc.MarkLabel(Lyes);
        EmitConstI4(1);
        _enc.MarkLabel(Lend);
        _enc.OpCode(ILOpCode.Endfilter); Pop();
    }

    // handler: dispatch the longjmp value into the matching setjmp's result local and
    // re-enter the try at Lhead (setjmp then re-reads that local, returning the value).
    private void EmitSetjmpHandler()
    {
        _stackDepth = 1; if (_stackDepth > _maxStack) _maxStack = _stackDepth;
        _enc.OpCode(ILOpCode.Pop); Pop();                 // discard the exception object
        EmitRuntimeCall("__chibil_longjmp_buf", RtBuf, nArgs: 0, hasRet: true);
        _enc.StoreLocal(_setjmpBufLocal); Pop();
        foreach (var (jb, sjval) in _setjmpSites)
        {
            var Lnext = _enc.DefineLabel();
            _enc.LoadLocal(_setjmpBufLocal); Push();
            GenExpr(jb);
            _enc.Branch(ILOpCode.Bne_un, Lnext); Pop(2);
            EmitRuntimeCall("__chibil_longjmp_val", RtVal, nArgs: 0, hasRet: true);
            _enc.StoreLocal(sjval); Pop();
            EmitRuntimeCall("__chibil_longjmp_clear", RtClear, nArgs: 0, hasRet: false);
            _enc.Branch(ILOpCode.Leave, _setjmpLhead);
            _enc.MarkLabel(Lnext);
        }
        _enc.OpCode(ILOpCode.Rethrow);                    // unreachable: the filter matched a site
    }

    // ═══════════════════════════════════════════════════════════════
    //  Address generation (GenAddr)
    // ═══════════════════════════════════════════════════════════════

    private void GenAddr(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Var:
                if (node.Var.Ty.Kind == TypeKind.Vla)
                {
                    // VLA pointer — load the stored pointer
                    LoadLocalOrParam(node.Var);
                    return;
                }
                if (node.Var.IsFunction || node.Var.Ty.Kind == TypeKind.Func)
                {
                    // &func — emit function address (same as GenExpr Var for functions)
                    EmitFunctionAddress(node.Var, node.Tok);
                    return;
                }
                if (node.Var.IsLocal)
                {
                    if (_paramSlots.TryGetValue(node.Var, out int argIdx))
                    {
                        _enc.LoadArgumentAddress(argIdx); Push();
                    }
                    else if (_localSlots.TryGetValue(node.Var, out int localIdx))
                    {
                        _enc.LoadLocalAddress(localIdx); Push();
                    }
                    return;
                }
                // Global variable — register its extern field on first reference.
                _enc.OpCode(ILOpCode.Ldsflda); _enc.Token(GetOrRegisterGlobalField(node.Var)); Push();
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                return;

            case NodeKind.Comma:
                GenExpr(node.Lhs); Pop(); // discard LHS value
                GenAddr(node.Rhs);
                return;

            case NodeKind.Member:
                GenAddr(node.Lhs);
                if (node.Member.Offset != 0)
                {
                    EmitConstI4(node.Member.Offset);
                    _enc.OpCode(ILOpCode.Add); Pop();
                }
                return;

            case NodeKind.FunCall:
                // Struct-returning call — evaluate, spill to scratch, return address
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                {
                    GenExpr(node);
                    var fHandle = GetStructTypeHandle(node.Ty);
                    if (fHandle.IsNil)
                    {
                        // Nested/flattened struct — GenExpr already returned an address.
                        // Spill to void* scratch, then return address.
                        int scratch = GetOrAddScratchLocal(_types.PointerTo(_types.TyVoid));
                        _enc.StoreLocal(scratch); Pop();
                        _enc.LoadLocal(scratch); Push();
                        return;
                    }
                    int fScratch = GetOrAddScratchLocal(node.Ty);
                    _enc.StoreLocal(fScratch); Pop();
                    _enc.LoadLocalAddress(fScratch); Push();
                    return;
                }
                break;

            case NodeKind.Assign:
            case NodeKind.Cond:
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                {
                    GenExpr(node);
                    var acHandle = GetStructTypeHandle(node.Ty);
                    if (acHandle.IsNil)
                    {
                        // Nested/flattened struct — GenExpr returned an address.
                        // Spill to void* scratch, then return that address.
                        int scratch = GetOrAddScratchLocal(_types.PointerTo(_types.TyVoid));
                        _enc.StoreLocal(scratch); Pop();
                        _enc.LoadLocal(scratch); Push();
                        return;
                    }
                    // Normal struct — spill value to scratch, return address of scratch.
                    int acScratch = GetOrAddScratchLocal(node.Ty);
                    _enc.StoreLocal(acScratch); Pop();
                    _enc.LoadLocalAddress(acScratch); Push();
                    return;
                }
                break;

            case NodeKind.VlaPtr:
                if (_localSlots.TryGetValue(node.Var, out int vlaSlot))
                {
                    _enc.LoadLocalAddress(vlaSlot); Push();
                }
                return;
        }
        Util.ErrorTok(node.Tok, "not an lvalue");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Load and Store
    // ═══════════════════════════════════════════════════════════════

    private void Load(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Array:
            case TypeKind.Func:
            case TypeKind.Vla:
                // Array decays to a pointer to its first element; the address (a
                // managed pointer to the array value-type) IS that value. The JIT
                // accepts &$ArrayType$ where a native int is required.
                return;
            case TypeKind.Struct:
            case TypeKind.Union:
            {
                var handle = GetStructTypeHandle(ty);
                if (handle.IsNil)
                {
                    // No TypeDef (nested/flattened struct) — address stays on stack.
                    // Caller accesses individual members via offset arithmetic.
                    return;
                }
                _enc.OpCode(ILOpCode.Ldobj); _enc.Token(handle);
                return;
            }
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Ldind_r4); return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Ldind_r8); return;
            case TypeKind.Ptr:
                // Pointers are native-int sized; use ldind.i so the stack type is a
                // native int (a valid address), not int64.
                _enc.OpCode(ILOpCode.Ldind_i); return;
        }

        // Integer types
        if (ty.Size == 1)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u1 : ILOpCode.Ldind_i1);
        else if (ty.Size == 2)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u2 : ILOpCode.Ldind_i2);
        else if (ty.Size == 4)
            _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u4 : ILOpCode.Ldind_i4);
        else
            _enc.OpCode(ILOpCode.Ldind_i8);
    }

    private void Store(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
            {
                var handle = GetStructTypeHandle(ty);
                if (handle.IsNil)
                {
                    // No TypeDef (nested/flattened struct) — use cpblk with the safest
                    // unaligned prefix because member addresses may be only byte-aligned.
                    // Stack: dest_addr, src_addr → unaligned. cpblk(dest, src, size)
                    EmitConstI4(ty.Size);
                    _enc.OpCode(ILOpCode.Unaligned); _enc.CodeBuilder.WriteByte(1);
                    _enc.OpCode(ILOpCode.Cpblk); Pop(3);
                    return;
                }
                _enc.OpCode(ILOpCode.Stobj); _enc.Token(handle);
                Pop(2);
                return;
            }
            case TypeKind.Float:
                _enc.OpCode(ILOpCode.Stind_r4); Pop(2); return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Stind_r8); Pop(2); return;
            case TypeKind.Ptr:
                // Pointers are native-int sized; store with stind.i.
                _enc.OpCode(ILOpCode.Stind_i); Pop(2); return;
        }

        if (ty.Size == 1) _enc.OpCode(ILOpCode.Stind_i1);
        else if (ty.Size == 2) _enc.OpCode(ILOpCode.Stind_i2);
        else if (ty.Size == 4) _enc.OpCode(ILOpCode.Stind_i4);
        else _enc.OpCode(ILOpCode.Stind_i8);
        Pop(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helper: Load local or parameter
    // ═══════════════════════════════════════════════════════════════

    private void LoadLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.LoadArgument(argIdx); Push();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.LoadLocal(localIdx); Push();
        }
    }

    private void StoreLocalOrParam(Obj var)
    {
        if (_paramSlots.TryGetValue(var, out int argIdx))
        {
            _enc.StoreArgument(argIdx); Pop();
        }
        else if (_localSlots.TryGetValue(var, out int localIdx))
        {
            _enc.StoreLocal(localIdx); Pop();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Constant loading helpers
    // ═══════════════════════════════════════════════════════════════

    private void EmitConstI4(int value)
    {
        _enc.LoadConstantI4(value); Push();
    }

    private void EmitConstI4(long value)
    {
        _enc.LoadConstantI4((int)value); Push();
    }

    private void EmitConstI8(long value)
    {
        _enc.LoadConstantI8(value); Push();
    }

    /// <summary>Emit conv.i8 for pointer arithmetic widening on 64-bit.</summary>
    private void ConvI8IfNeeded()
    {
        if (!Is32) _enc.OpCode(ILOpCode.Conv_i8);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch normalization helpers
    // ═══════════════════════════════════════════════════════════════

    private void NormalizeToBranchable(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
            case TypeKind.Double:
            case TypeKind.LDouble:
            case TypeKind.LLong:
            case TypeKind.Long when _dm.LongSize == 8:
                EmitTypedZero(ty);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                break;
        }
    }

    private void EmitTypedZero(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                return;
            case TypeKind.Double:
            case TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                return;
            case TypeKind.LLong:
                EmitConstI8(0);
                return;
            case TypeKind.Long:
                // LP64: long is 8 bytes = int64
                if (_dm.LongSize == 8) { EmitConstI8(0); return; }
                EmitConstI4(0);
                return;
            default:
                EmitConstI4(0);
                return;
        }
    }

    private static bool IsAggregateType(CType ty) =>
        ty.Kind == TypeKind.Struct || ty.Kind == TypeKind.Union || ty.Kind == TypeKind.Array;

    /// <summary>Push a callable function address onto the evaluation stack.</summary>
    private void EmitFunctionAddress(Obj fn, Token tok = null)
    {
        CType funcTy = fn.Ty;
        if (_options.Target == TargetProfile.CoreClr)
        {
            // Pure-MSIL: every C function is a managed method. Take its address with
            // ldftn regardless of (cdecl/stdcall/clrcall) calling convention — there
            // are no native __unep@ slots in this target.
            if (_methodDefs.TryGetValue(fn, out var mdef))
            {
                _enc.OpCode(ILOpCode.Ldftn); _enc.Token(mdef); Push(); return;
            }
            // External function: ldftn its member reference.
            if (!_externalFuncRefs.TryGetValue(fn.Name, out var extRef))
            {
                RegisterExternalFunction(fn);
                extRef = _externalFuncRefs[fn.Name];
            }
            _enc.OpCode(ILOpCode.Ldftn); _enc.Token(extRef); Push(); return;
        }
        if (funcTy.CallConv == CallConv.Clrcall)
        {
            if (_methodDefs.TryGetValue(fn, out var md))
            {
                _enc.OpCode(ILOpCode.Ldftn); _enc.Token(md); Push();
            }
            else
            {
                Util.ErrorTok(tok ?? fn.Tok, "cannot take address of external __clrcall function");
            }
        }
        else
        {
            // cdecl: load the native function pointer from __unep@ field
            if (_unepFields.TryGetValue(fn.Name, out var unepField))
            {
                _enc.OpCode(ILOpCode.Ldsfld); _enc.Token(unepField); Push();
            }
            else
            {
                Util.ErrorTok(tok ?? fn.Tok, $"cannot take address of cdecl function '{fn.Name}' — __unep@ field not registered");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Expression code generation (GenExpr)
    // ═══════════════════════════════════════════════════════════════

    // 1-based (startColumn, endColumn) of a token within its source line, derived
    // from the byte offset back to the previous newline. Gives Portable PDB sequence
    // points sub-line precision so the three clauses of for(init; cond; incr) — all on
    // one line but at different columns — are distinguishable in the debugger.
    private static (int Start, int End) ColumnSpan(Token t)
    {
        if (t?.Buf == null) return (0, 0);
        int i = t.Loc, col = 1;
        while (i > 0 && i <= t.Buf.Length && t.Buf[i - 1] != (byte)'\n') { i--; col++; }
        int len = t.Len > 0 ? t.Len : 1;
        return (col, col + len);
    }

    // Record a lexical scope's measured IL range [start, current) under its parser
    // scope index, for nested LocalScope emission in the Portable PDB.
    private void RecordScopeRange(int scopeId, int start)
    {
        if (scopeId < 0) return;
        _dbgScopeRanges[scopeId] = (start, _enc.CodeBuilder.Count - start);
    }

    private void GenExpr(Node node)
    {
        // Mark line number for debug info
        if (node.Tok?.File != null)
        {
            var (sc, ec) = ColumnSpan(node.Tok);
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo, sc, ec);
        }

        switch (node.Kind)
        {
            case NodeKind.NullExpr: return;

            case NodeKind.Num:
                switch (node.Ty.Kind)
                {
                    case TypeKind.Float:
                        _enc.LoadConstantR4((float)node.FVal); Push(); return;
                    case TypeKind.Double:
                    case TypeKind.LDouble:
                        _enc.LoadConstantR8(node.FVal); Push(); return;
                    case TypeKind.LLong:
                        EmitConstI8(node.Val); return;
                    default:
                        if (node.Ty.Kind == TypeKind.Long && _dm.LongSize == 8)
                            EmitConstI8(node.Val);
                        else
                            EmitConstI4(node.Val);
                        return;
                }

            case NodeKind.Neg:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Neg);
                return;

            case NodeKind.Var:
                if (node.Ty == null) throw new InvalidOperationException($"Var node '{node.Var?.Name}' has null Ty (AddType not run)");
                if (node.Ty.Kind == TypeKind.Func || node.Var.IsFunction)
                {
                    EmitFunctionAddress(node.Var, node.Tok);
                    return;
                }
                if (node.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    // Simple scalar local/param — use direct load
                    LoadLocalOrParam(node.Var);
                    return;
                }
                GenAddr(node);
                Load(node.Ty);
                return;

            case NodeKind.Member:
                GenAddr(node);
                Load(node.Ty);
                if (node.Member.IsBitfield)
                {
                    // The shift-extract sign/zero-fills from the MSB of the loaded VALUE,
                    // not the storage unit: `Load` widens a sub-word storage type to a
                    // 32-bit int (u8/u16/u32 -> i4), 8 bytes to i8. Sizing the shifts by
                    // the storage width (Ty.Size*8) instead leaves the storage unit's other
                    // bits in the result for u8/u16 bitfields. Use the loaded container's
                    // width: 32 for Size<=4, 64 for Size==8.
                    int containerBits = node.Member.Ty.Size <= 4 ? 32 : 64;
                    int shift = containerBits - node.Member.BitWidth - node.Member.BitOffset;
                    if (shift > 0)
                    {
                        EmitConstI4(shift);
                        _enc.OpCode(ILOpCode.Shl); Pop();
                    }
                    int rightShift = containerBits - node.Member.BitWidth;
                    if (rightShift > 0)
                    {
                        EmitConstI4(rightShift);
                        _enc.OpCode(node.Member.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop();
                    }
                }
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                Load(node.Ty);
                return;

            case NodeKind.Addr:
                GenAddr(node.Lhs);
                return;

            case NodeKind.Assign:
                // Handle bitfield assignment
                if (node.Lhs.Kind == NodeKind.Member && node.Lhs.Member.IsBitfield)
                {
                    GenBitfieldAssign(node);
                    return;
                }
                // Optimize: direct store to local/param for simple scalars
                if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    GenExpr(node.Rhs);
                    _enc.OpCode(ILOpCode.Dup); Push();
                    StoreLocalOrParam(node.Lhs.Var);
                    return;
                }
                // If the RHS emits a `localloc` (alloca / Layer-1 variadic call),
                // it must run with an empty evaluation stack — so it cannot be
                // generated AFTER the destination address is pushed. Evaluate the
                // RHS into a scratch FIRST (stack empty), then take the lvalue
                // address and store. C leaves assignment operand evaluation order
                // unspecified, so this reordering is conforming.
                if (ProducesLocalloc(node.Rhs))
                {
                    GenExpr(node.Rhs);
                    int rhsScratch = AddFreshScratchLocal(node.Ty);
                    _enc.StoreLocal(rhsScratch); Pop();
                    GenAddr(node.Lhs);
                    _enc.LoadLocal(rhsScratch); Push();
                    Store(node.Ty);
                    _enc.LoadLocal(rhsScratch); Push();
                    return;
                }

                GenAddr(node.Lhs);
                if ((node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union) &&
                    GetStructTypeHandle(node.Ty).IsNil)
                {
                    // Nested/flattened struct: GenExpr(rhs) returns an address.
                    // Save dest address before generating rhs so the assignment
                    // expression result refers to the destination, not the source.
                    // Use a fresh scratch to avoid clobber by inner chain assignments.
                    var destScratch = AddFreshScratchLocal(_types.PointerTo(_types.TyVoid));
                    _enc.OpCode(ILOpCode.Dup); Push();
                    _enc.StoreLocal(destScratch); Pop();
                    GenExpr(node.Rhs);
                    Store(node.Ty);
                    _enc.LoadLocal(destScratch); Push();
                }
                else
                {
                    GenExpr(node.Rhs);
                    int assignScratch = GetOrAddScratchLocal(node.Ty);
                    _enc.OpCode(ILOpCode.Dup); Push();
                    _enc.StoreLocal(assignScratch); Pop();
                    Store(node.Ty);
                    _enc.LoadLocal(assignScratch); Push();
                }
                return;

            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                {
                    if (n.Next == null && n.Kind == NodeKind.ExprStmt)
                    {
                        // Last expression in statement expression — its value IS the result.
                        GenExpr(n.Lhs);
                    }
                    else
                    {
                        GenStmt(n);
                    }
                }
                return;

            case NodeKind.Comma:
            {
                int depthBeforeComma = _stackDepth;
                GenExpr(node.Lhs);
                // Discard LHS result if it pushed anything
                while (_stackDepth > depthBeforeComma)
                {
                    _enc.OpCode(ILOpCode.Pop); Pop();
                }
                GenExpr(node.Rhs);
                return;
            }

            case NodeKind.Cast:
                GenExpr(node.Lhs);
                EmitCast(node.Lhs.Ty, node.Ty);
                return;

            case NodeKind.MemZero:
                if (_localSlots.TryGetValue(node.Var, out int mzSlot))
                {
                    _enc.LoadLocalAddress(mzSlot); Push();
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                else
                {
                    GenAddr(new Node { Kind = NodeKind.Var, Var = node.Var, Tok = node.Tok, Ty = node.Var.Ty });
                    EmitConstI4(0);
                    EmitConstI4(node.Var.Ty.Size);
                    _enc.OpCode(ILOpCode.Initblk); Pop(3);
                }
                return;

            case NodeKind.Cond:
            {
                int savedDepth = _stackDepth;
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(elseLabel);
                GenExpr(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.Not:
                GenExpr(node.Lhs);
                EmitTypedZero(node.Lhs.Ty);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;

            case NodeKind.BitNot:
                GenExpr(node.Lhs);
                _enc.OpCode(ILOpCode.Not);
                return;

            case NodeKind.LogAnd:
            {
                int savedDepth = _stackDepth;
                var falseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                NormalizeToBranchable(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                NormalizeToBranchable(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brfalse, falseLabel); Pop();
                _stackDepth = savedDepth;
                EmitConstI4(1);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(falseLabel);
                EmitConstI4(0);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.LogOr:
            {
                int savedDepth = _stackDepth;
                var trueLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Lhs);
                NormalizeToBranchable(node.Lhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueLabel); Pop();
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                NormalizeToBranchable(node.Rhs.Ty);
                _enc.Branch(ILOpCode.Brtrue, trueLabel); Pop();
                _stackDepth = savedDepth;
                EmitConstI4(0);
                _enc.Branch(ILOpCode.Br, endLabel);
                _stackDepth = savedDepth;
                _enc.MarkLabel(trueLabel);
                EmitConstI4(1);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.FunCall:
                GenFunCall(node);
                return;

            case NodeKind.LabelVal:
                Util.ErrorTok(node.Tok, "labels-as-values not supported in MSIL");
                return;

            case NodeKind.Cas:
                GenCas(node);
                return;

            case NodeKind.Exch:
                GenExch(node);
                return;

            case NodeKind.VaStart:
            {
                // ap points to a SysV __va_list_tag {uint gp_offset; uint fp_offset;
                // void* overflow_arg_area; void* reg_save_area} (24 bytes). We saturate
                // gp/fp_offset (48/176) so every va_arg — and libc, when ap is forwarded
                // to a v*printf/v*scanf — reads sequentially from overflow_arg_area, which
                // is the Layer-1 va-buffer (the hidden __va pointer). This makes chibil's
                // va_list ABI-compatible with libc's; the buffer packing is unchanged.
                if (_vaStartStructLocal < 0)
                    _vaStartStructLocal = AddFreshScratchLocal(_types.TyVaList);
                int sLoc = _vaStartStructLocal;
                int vaIdx = _paramSlots[_currentFn.VaPtr];

                // sLoc = localloc(24)  (zeroed: method body has InitLocals)
                EmitConstI4(24);
                _enc.OpCode(ILOpCode.Conv_u);
                _enc.OpCode(ILOpCode.Localloc);          // size -> ptr (net 0)
                _enc.StoreLocal(sLoc); Pop();

                // sLoc->gp_offset (off 0) = 48  (all integer registers "consumed")
                _enc.LoadLocal(sLoc); Push();
                EmitConstI4(48);
                _enc.OpCode(ILOpCode.Stind_i4); Pop(2);

                // sLoc->fp_offset (off 4) = 176  (all SSE registers "consumed")
                _enc.LoadLocal(sLoc); Push();
                EmitConstI4(4); _enc.OpCode(ILOpCode.Conv_i); _enc.OpCode(ILOpCode.Add); Pop();
                EmitConstI4(176);
                _enc.OpCode(ILOpCode.Stind_i4); Pop(2);

                // sLoc->overflow_arg_area (off 8) = __va  (the va-buffer)
                _enc.LoadLocal(sLoc); Push();
                EmitConstI4(8); _enc.OpCode(ILOpCode.Conv_i); _enc.OpCode(ILOpCode.Add); Pop();
                _enc.LoadArgument(vaIdx); Push();
                _enc.OpCode(ILOpCode.Stind_i); Pop(2);
                // reg_save_area (off 16) stays 0 (localloc-zeroed); never read (regs saturated)

                // ap = sLoc
                GenAddr(node.Lhs);                       // &ap
                _enc.LoadLocal(sLoc); Push();            // sLoc
                Store(_types.TyVaList);                  // *(&ap) = sLoc  (Pop x2)
                return;
            }

            case NodeKind.VaArg:
            {
                if (node.Ty.Kind == TypeKind.Struct || node.Ty.Kind == TypeKind.Union)
                    Util.ErrorTok(node.Tok, "va_arg of struct type not supported");
                // result = *(Ty*)ap ; ap += 8   (each slot is 8 bytes)
                // Two distinct pointer-typed locals are required; GetOrAddScratchLocal
                // would alias them (same Ptr kind/size), so allocate a fresh pair once.
                if (_vaArgPApLocal < 0)
                {
                    _vaArgPApLocal = AddFreshScratchLocal(_types.PointerTo(_types.TyVaList));
                    _vaArgApLocal = AddFreshScratchLocal(_types.TyVaList);
                }
                int apLocal = _vaArgApLocal;             // the __va_list_tag*
                int ovLocal = _vaArgPApLocal;            // its overflow_arg_area cursor

                // apLocal = ap  (the __va_list_tag pointer)
                GenAddr(node.Lhs);                       // &ap
                Load(_types.TyVaList);                   // ap (ldind.i)
                _enc.StoreLocal(apLocal); Pop();

                // ovLocal = *(ap + 8)   (current overflow_arg_area)
                _enc.LoadLocal(apLocal); Push();
                EmitConstI4(8); _enc.OpCode(ILOpCode.Conv_i); _enc.OpCode(ILOpCode.Add); Pop();
                Load(_types.TyVaList);                   // overflow ptr (ldind.i)
                _enc.StoreLocal(ovLocal); Pop();

                // result = *(Ty*)overflow  (left on the stack as the node's value)
                _enc.LoadLocal(ovLocal); Push();
                Load(node.Ty);

                // *(ap + 8) = overflow + 8   (advance one 8-byte slot)
                _enc.LoadLocal(apLocal); Push();
                EmitConstI4(8); _enc.OpCode(ILOpCode.Conv_i); _enc.OpCode(ILOpCode.Add); Pop();
                _enc.LoadLocal(ovLocal); Push();
                EmitConstI4(8); _enc.OpCode(ILOpCode.Conv_i); _enc.OpCode(ILOpCode.Add); Pop();
                Store(_types.TyVaList);                  // (Pop x2)
                return;
            }

            case NodeKind.VaEnd:
                // no-op (void); evaluate nothing, push nothing
                return;

            case NodeKind.VaCopy:
            {
                // dst must be an INDEPENDENT __va_list_tag so advancing dst does not
                // disturb src: allocate a fresh 24-byte tag, copy src's into it, point
                // dst at it.
                if (_vaCopyStructLocal < 0)
                    _vaCopyStructLocal = AddFreshScratchLocal(_types.TyVaList);
                int nLoc = _vaCopyStructLocal;

                // nLoc = localloc(24)
                EmitConstI4(24);
                _enc.OpCode(ILOpCode.Conv_u);
                _enc.OpCode(ILOpCode.Localloc);          // size -> ptr (net 0)
                _enc.StoreLocal(nLoc); Pop();

                // cpblk(nLoc, src, 24)
                _enc.LoadLocal(nLoc); Push();            // dest
                GenExpr(node.Rhs);                       // src (the __va_list_tag*)
                EmitConstI4(24);                         // size
                _enc.OpCode(ILOpCode.Cpblk); Pop(3);

                // dst = nLoc
                GenAddr(node.Lhs);                       // &dst
                _enc.LoadLocal(nLoc); Push();            // nLoc
                Store(_types.TyVaList);                  // (Pop x2)
                return;
            }
        }

        // Binary operations
        GenExpr(node.Lhs);
        GenExpr(node.Rhs);

        switch (node.Kind)
        {
            case NodeKind.Add: _enc.OpCode(ILOpCode.Add); Pop(); return;
            case NodeKind.Sub: _enc.OpCode(ILOpCode.Sub); Pop(); return;
            case NodeKind.Mul: _enc.OpCode(ILOpCode.Mul); Pop(); return;
            case NodeKind.Div:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Div_un : ILOpCode.Div); Pop(); return;
            case NodeKind.Mod:
                _enc.OpCode(node.Ty.IsUnsigned ? ILOpCode.Rem_un : ILOpCode.Rem); Pop(); return;
            case NodeKind.BitAnd: _enc.OpCode(ILOpCode.And); Pop(); return;
            case NodeKind.BitOr: _enc.OpCode(ILOpCode.Or); Pop(); return;
            case NodeKind.BitXor: _enc.OpCode(ILOpCode.Xor); Pop(); return;
            case NodeKind.Shl: _enc.OpCode(ILOpCode.Shl); Pop(); return;
            case NodeKind.Shr:
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop(); return;
            case NodeKind.Eq: _enc.OpCode(ILOpCode.Ceq); Pop(); return;
            case NodeKind.Ne:
                _enc.OpCode(ILOpCode.Ceq); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
            case NodeKind.Lt:
                // clt already returns 0 for NaN (unordered), which is correct for C's
                // "NaN < x is false". Only Le needs the _un variant (via inverted cgt.un).
                _enc.OpCode(node.Lhs.Ty.IsUnsigned ? ILOpCode.Clt_un : ILOpCode.Clt); Pop(); return;
            case NodeKind.Le:
                // a <= b  ≡  !(a > b)  ≡  (cgt_un == 0) for unsigned/float
                // For floats, must use Cgt_un so NaN comparisons return unordered=1→false
                _enc.OpCode((node.Lhs.Ty.IsUnsigned || TypeSystem.IsFlonum(node.Lhs.Ty))
                    ? ILOpCode.Cgt_un : ILOpCode.Cgt); Pop();
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Ceq); Pop();
                return;
        }
        Util.ErrorTok(node.Tok, "invalid expression");
    }

    // ─── Function call ───────────────────────────────────────────

    /// <summary>True if evaluating <paramref name="node"/>'s VALUE emits a
    /// <c>localloc</c> (alloca, or a Layer-1 variadic call that packs a stack
    /// va-buffer). ECMA-335 requires the evaluation stack be empty (apart from the
    /// size) when <c>localloc</c> runs, so such an expression cannot be generated
    /// while a destination lvalue address is already on the stack — the caller must
    /// evaluate it into a scratch local first. Only walks value-producing children
    /// of the immediate expression (not nested full statements / calls' own bodies),
    /// which is sufficient: the localloc, if any, is emitted directly by THIS
    /// expression's lowering before the surrounding store.</summary>
    private bool ProducesLocalloc(Node node)
    {
        if (node == null) return false;
        switch (node.Kind)
        {
            case NodeKind.FunCall:
            {
                bool isIndirect = node.Lhs.Kind != NodeKind.Var || !node.Lhs.Var.IsFunction;
                if (!isIndirect && node.Lhs.Var.Name == "alloca") return true;
                // Layer-1 variadic call packs its va-buffer with localloc when it
                // has at least one variadic argument.
                var ft = node.FuncTy;
                if (ft != null && ft.IsVariadic && ft.Params != null)
                {
                    int nFixed = 0;
                    for (CType p = ft.Params; p != null; p = p.Next) nFixed++;
                    int nArgs = 0;
                    for (Node a = node.Args; a != null; a = a.Next) nArgs++;
                    // Layer 2 (extern concrete) emits no localloc; only the Layer-1
                    // (locally-defined) path does. Be conservative: treat any
                    // variadic-with-extra-args call as localloc-producing.
                    if (nArgs > nFixed) return true;
                }
                return false;
            }
            // Transparent wrappers whose value is their child's.
            case NodeKind.Comma:
                return ProducesLocalloc(node.Rhs);
            case NodeKind.Cast:
                return ProducesLocalloc(node.Lhs);
            case NodeKind.Cond:
                return ProducesLocalloc(node.Then) || ProducesLocalloc(node.Els);
            default:
                return false;
        }
    }

    // When a call argument is pre-spilled (see GenFunCall), maps that argument
    // Node to the scratch local holding its already-evaluated value. GenArg loads
    // the scratch instead of re-evaluating.
    private Dictionary<Node, int> _argSpill;

    /// <summary>Evaluate (or, if pre-spilled, reload) a call argument's value onto
    /// the stack. See the pre-spill logic in <see cref="GenFunCall"/>.</summary>
    private void GenArg(Node arg)
    {
        if (_argSpill != null && _argSpill.TryGetValue(arg, out int slot))
        {
            _enc.LoadLocal(slot); Push();
            return;
        }
        GenExpr(arg);
    }

    private void GenFunCall(Node node)
    {
        CType funcTy = node.FuncTy;
        bool isIndirect = node.Lhs.Kind != NodeKind.Var || !node.Lhs.Var.IsFunction;

        // Check for alloca
        if (!isIndirect && node.Lhs.Var.Name == "alloca")
        {
            GenExpr(node.Args);
            _enc.OpCode(ILOpCode.Localloc);
            // Stack: size → ptr (net 0)
            return;
        }

        // ── setjmp / longjmp (MUSL-3, partial) ────────────────────────────────
        // CoreCLR can't save/restore native registers, and passing the jmp_buf
        // array (an opaque __jmp_buf_tag[1]) is rejected by the JIT. Lower setjmp to
        // a constant 0 (the value of a DIRECT setjmp call) WITHOUT evaluating the
        // jmp_buf — bypassing the array-decay InvalidProgram. The longjmp→setjmp
        // RESUMPTION (setjmp establishing a catch) is not implemented yet, so longjmp
        // throws instead of unwinding to its setjmp. This runs the no-longjmp path.
        if (!isIndirect)
        {
            string fn = node.Lhs.Var.Name;
            if (IsSetjmpName(fn))
            {
                // The containing function is wrapped (EmitSetjmpWrappedBody). Record
                // this site's jmp_buf + a fresh result local; setjmp's value IS that
                // local — 0 on the direct call (zero-init), the longjmp value on resume
                // (the wrap's handler stores it then re-enters the try).
                int sjval = AddFreshScratchLocal(_types.TyInt);
                _setjmpSites.Add((node.Args, sjval));
                // Resume-at-site: place the try-region boundary HERE so code sequenced
                // before this setjmp call runs exactly once and is not re-executed on a
                // longjmp resume. The eval stack is empty at a setjmp call in every
                // supported idiom ((push_tail, setjmp), if(setjmp()==0), v=setjmp()).
                if (_setjmpDeferStart && !_setjmpTryOpen)
                {
                    System.Diagnostics.Debug.Assert(_stackDepth == 0,
                        "setjmp call site must have an empty eval stack for the try boundary");
                    _enc.MarkLabel(_setjmpLhead);
                    _enc.OpCode(ILOpCode.Nop);
                    _enc.MarkLabel(_setjmpTryStartLabel);
                    _setjmpTryOpen = true;
                }
                _enc.LoadLocal(sjval); Push();
                return;
            }
            if (fn is "longjmp" or "_longjmp" or "siglongjmp")
            {
                // __chibil_longjmp(&jb, val): stash the carrier (buf,val,active) and
                // throw a stock Exception, unwinding to the matching setjmp's filter.
                GenExpr(node.Args);            // &jb (decayed pointer)
                GenExpr(node.Args.Next);       // val
                EmitRuntimeCall("__chibil_longjmp", RtLongjmp, nArgs: 2, hasRet: false);
                return;
            }
        }

        // ── localloc-producing argument: pre-spill ALL args ──────────────────
        // If any argument's value emits a `localloc` (a nested alloca or Layer-1
        // variadic call), it must run with an empty evaluation stack. But the
        // argument-push loops below accumulate earlier args on the stack first, so
        // a sibling localloc would run with them underneath -> InvalidProgramException
        // (e.g. SQLite's `sqlite3VdbeAddOp4(v, .., sqlite3MPrintf(..), ..)`).
        // Evaluate every argument into a fresh scratch local up front — each runs
        // with an empty stack — then the loops below just reload the scratch via
        // GenArg. C leaves argument evaluation order unspecified, so left-to-right
        // pre-evaluation is conforming. The spill map is scoped to THIS call and
        // saved/restored to support nested calls.
        var savedSpill = _argSpill;
        bool anyLocalloc = false;
        for (Node a = node.Args; a != null; a = a.Next)
            if (ProducesLocalloc(a)) { anyLocalloc = true; break; }
        if (anyLocalloc)
        {
            var spill = new Dictionary<Node, int>();
            for (Node a = node.Args; a != null; a = a.Next)
            {
                GenExpr(a);                               // stack empty at each localloc
                int slot = AddFreshScratchLocal(a.Ty);
                _enc.StoreLocal(slot); Pop();
                spill[a] = slot;
            }
            _argSpill = spill;
        }
        try
        {

        // Push arguments
        int argCount = 0;

        // ── Indirect variadic calls: lowered via the Layer-1 va-buffer ABI ──
        // A function pointer to a variadic function (e.g. bash's `(*pfunc)(fmt, …)`
        // where pfunc points at a chibil-defined `cprintf(const char*, …)`) is
        // lowered like a direct Layer-1 call: the va-buffer packing block below
        // pushes the fixed args + a hidden __va pointer, and the calli standalone
        // signature (further down) appends the matching trailing void* param so the
        // stack and signature agree. This matches the lowered MethodDef of any
        // chibil-compiled variadic (fixed params + void* __va, Default conv) — the
        // only kind of variadic a managed function pointer can target.

        // ── Layer 2: native __cdecl variadic call (e.g. printf) ──────────
        // A variadic callee that is an EXTERNAL declaration (not defined in this
        // TU) is assumed to be a native libc-style variadic. Its concrete arg
        // types are known at the call site, so emit a normal external call whose
        // MemberRef signature is fixed params + the concrete (default-promoted)
        // variadic arg types — NO hidden va-buffer pointer, NO localloc packing.
        // (chibil-link turns the unresolved external into a P/Invoke — task B2.)
        // Locally-DEFINED variadics keep the va-buffer path (Layer 1) so the
        // call matches the MethodDef's hidden __va param.
        // NOTE: external-declared variadics are assumed to be native cdecl; a
        // chibil variadic defined in a DIFFERENT translation unit and called as
        // extern is not supported (single-TU programs like the SQLite amalgamation
        // are unaffected).
        if (funcTy.IsVariadic && funcTy.Params != null && !isIndirect
            && !node.Lhs.Var.IsDefinition)
        {
            int nFixed2 = 0;
            for (CType p = funcTy.Params; p != null; p = p.Next) nFixed2++;

            // Push every argument directly. Variadic args (those past the fixed
            // prototype) get default argument promotions applied.
            int idx = 0;
            for (Node arg = node.Args; arg != null; arg = arg.Next, idx++)
            {
                GenArg(arg);
                if (idx >= nFixed2)
                {
                    CType promoted = VaPromote(arg.Ty);
                    if (promoted != arg.Ty)
                        EmitCast(arg.Ty, promoted);
                }
                argCount++;
            }

            var concreteRef = RegisterConcreteVarargCall(node.Lhs.Var, funcTy, node.Args);
            _enc.Call(concreteRef);
            Pop(argCount);

            if (funcTy.ReturnTy.Kind != TypeKind.Void)
                Push();
            return;
        }

        // Only REAL variadic callees (explicit prototype + ...) use the hidden
        // va-buffer pointer. K&R unprototyped functions (Params==null) also set
        // IsVariadic but are emitted/called as plain functions.
        if (funcTy.IsVariadic && funcTy.Params != null)
        {
            // Variadic callee: pass fixed args directly, then a hidden trailing
            // pointer to a caller-packed va-buffer (8-byte slots, one per vararg).
            // No CLR vararg calling convention is used (unsupported on CoreCLR).
            int nFixed = 0;
            for (CType p = funcTy.Params; p != null; p = p.Next) nFixed++;

            // Skip to the variadic args (those past the fixed prototype).
            Node arg = node.Args;
            for (int i = 0; i < nFixed; i++) arg = arg.Next;

            // Collect the remaining (variadic) args.
            var varArgs = new List<Node>();
            for (Node v = arg; v != null; v = v.Next) varArgs.Add(v);
            int nVar = varArgs.Count;

            // IMPORTANT: `localloc` requires the evaluation stack to be empty
            // except for the size operand. Therefore the va-buffer must be built
            // BEFORE the fixed args are pushed. We pack into a scratch local, then
            // push the fixed args, then push the buffer pointer last.
            // NOTE: this means variadic args are evaluated before the fixed args;
            // C leaves argument evaluation order unspecified, so this is permitted.
            int baseLocal = -1;
            if (nVar != 0)
            {
                // localloc 8*nVar bytes, keep base pointer in a scratch local.
                EmitConstI4(8 * nVar);
                _enc.OpCode(ILOpCode.Localloc); // size -> ptr (net 0)
                baseLocal = GetOrAddScratchLocal(_types.TyVaList);
                _enc.StoreLocal(baseLocal); Pop();

                for (int i = 0; i < nVar; i++)
                {
                    Node va = varArgs[i];
                    CType promoted = VaPromote(va.Ty);

                    // slot address = base + i*8
                    _enc.LoadLocal(baseLocal); Push();
                    if (i != 0)
                    {
                        EmitConstI4(i * 8);
                        _enc.OpCode(ILOpCode.Conv_i);
                        _enc.OpCode(ILOpCode.Add); Pop();
                    }
                    // value (promoted)
                    GenArg(va);
                    if (promoted != va.Ty)
                        EmitCast(va.Ty, promoted);
                    Store(promoted); // stind into slot, pops addr+value
                }
            }

            // Push the fixed args (after the buffer is fully built).
            arg = node.Args;
            for (int i = 0; i < nFixed; i++)
            {
                GenArg(arg);
                argCount++;
                arg = arg.Next;
            }

            // Push the hidden va-buffer pointer last.
            if (nVar == 0)
            {
                // No variadic args — pass a null va-buffer pointer.
                _enc.OpCode(ILOpCode.Ldc_i4_0);
                _enc.OpCode(ILOpCode.Conv_u);
                Push();
            }
            else
            {
                _enc.LoadLocal(baseLocal); Push();
            }
            argCount++; // the hidden va-buffer pointer
        }
        else
        {
            for (Node arg = node.Args; arg != null; arg = arg.Next)
            {
                GenArg(arg);
                argCount++;
            }
        }

        if (isIndirect)
        {
            // Indirect call — push function pointer, then calli
            GenExpr(node.Lhs);

            // Build standalone signature for calli
            var calliSig = new BlobBuilder();
            // In CoreCLR/pure-MSIL the callee is a managed method (Default conv) and
            // its address was taken via ldftn; the calli sig must match (Default),
            // not cdecl/stdcall — otherwise InvalidProgramException at runtime.
            byte calliConv = _options.Target == TargetProfile.CoreClr
                ? (byte)SignatureCallingConvention.Default
                : (funcTy.CallConv switch
                {
                    CallConv.Clrcall => (byte)SignatureCallingConvention.Default,
                    CallConv.Stdcall => (byte)SignatureCallingConvention.StdCall,
                    _ => (byte)SignatureCallingConvention.CDecl,
                });
            calliSig.WriteByte(calliConv);

            // A variadic function pointer carries a hidden trailing va-buffer
            // pointer (Layer-1 ABI), matching the callee's lowered MethodDef — count
            // and encode it so the calli sig agrees with the packed stack above.
            bool hasVaPtr = funcTy.IsVariadic && funcTy.Params != null;
            int paramCount = 0;
            for (CType p = funcTy.Params; p != null; p = p.Next) paramCount++;
            if (hasVaPtr) paramCount++;
            calliSig.WriteCompressedInteger(paramCount);

            // Return type (with modopt for calling convention)
            EncodeReturnType(calliSig, funcTy);

            // Params
            for (CType p = funcTy.Params; p != null; p = p.Next)
                EncodeType(calliSig, p);
            if (hasVaPtr)
                EncodeType(calliSig, _types.TyVaList); // hidden __va pointer

            var calliSigHandle = _md.AddStandaloneSignature(_md.GetOrAddBlob(calliSig));
            _enc.CallIndirect(calliSigHandle);
            Pop(argCount + 1); // pop args + function pointer
        }
        else
        {
            // Direct call
            string targetName = node.Lhs.Var.Name;
            if (_methodDefs.TryGetValue(node.Lhs.Var, out var methodDef))
            {
                _enc.Call(methodDef);
            }
            else if (_externalFuncRefs.TryGetValue(targetName, out var memberRef))
            {
                _enc.Call(memberRef);
            }
            else
            {
                // Register on the fly (might be a forward reference)
                RegisterExternalFunction(node.Lhs.Var);
                _enc.Call(_externalFuncRefs[targetName]);
            }
            Pop(argCount);
        }

        // Push return value if non-void
        if (funcTy.ReturnTy.Kind != TypeKind.Void)
            Push();

        }
        finally
        {
            _argSpill = savedSpill;
        }
    }

    // ─── Atomic operations ───────────────────────────────────────

    private void GenCas(Node node)
    {
        // __atomic_compare_exchange_n → Interlocked.CompareExchange(ref, value, comparand)
        // Returns bool: true if exchange happened
        GenExpr(node.CasAddr);  // address
        GenExpr(node.CasNew);   // desired value
        GenExpr(node.CasOld);   // Load old value from *old_ptr
        Load(node.CasOld.Ty.Base);

        // Call Interlocked.CompareExchange(ref int, int, int)
        var interlocked = GetInterlockedRef();
        var sig = new BlobBuilder();
        sig.WriteByte(0x00); // DEFAULT
        sig.WriteCompressedInteger(3);
        sig.WriteByte((byte)SignatureTypeCode.Int32); // return
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Int32); // ref param
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        var cxchgRef = _md.AddMemberReference(interlocked,
            _md.GetOrAddString("CompareExchange"), _md.GetOrAddBlob(sig));
        _enc.Call(cxchgRef);
        Pop(2); // 3 args → 1 result

        // Compare result with comparand to get bool
        GenExpr(node.CasOld);
        Load(node.CasOld.Ty.Base);
        _enc.OpCode(ILOpCode.Ceq); Pop();
    }

    private void GenExch(Node node)
    {
        // __atomic_exchange_n → Interlocked.Exchange(ref int, int)
        GenExpr(node.Lhs); // address
        GenExpr(node.Rhs); // new value

        var interlocked = GetInterlockedRef();
        var sig = new BlobBuilder();
        sig.WriteByte(0x00);
        sig.WriteCompressedInteger(2);
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Pointer);
        sig.WriteByte((byte)SignatureTypeCode.Int32);
        sig.WriteByte((byte)SignatureTypeCode.Int32);

        var xchgRef = _md.AddMemberReference(interlocked,
            _md.GetOrAddString("Exchange"), _md.GetOrAddBlob(sig));
        _enc.Call(xchgRef);
        Pop(); // 2 args → 1 result
    }

    // ─── Bitfield assignment ─────────────────────────────────────

    private void GenBitfieldAssign(Node node)
    {
        Member mem = node.Lhs.Member;
        // The storage unit may be wider than 32 bits (e.g. a `size_t`/`long long`
        // bitfield). The mask/shift/merge MUST be done in that width: otherwise a
        // field at a high bit offset is lost — `value << 56` masks the shift count to
        // 32-bit (`<< 24`), the scratch truncates to 32 bits, and the 64-bit clear
        // mask is cut to 32 bits, so the high bits read back 0.
        bool wide = mem.Ty.Size > 4;
        GenAddr(node.Lhs);

        // Save address for later store
        _enc.OpCode(ILOpCode.Dup); Push();

        GenExpr(node.Rhs);

        // Save the truncated value for the expression result
        long mask = mem.BitWidth >= 64 ? -1L : (1L << mem.BitWidth) - 1;
        int assignScratch = GetOrAddScratchLocal(node.Ty);
        _enc.OpCode(ILOpCode.Dup); Push();
        _enc.StoreLocal(assignScratch); Pop();

        // Widen the new value to the storage width before masking/shifting.
        if (wide) _enc.OpCode(mem.Ty.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);

        // Mask and shift new value into position
        if (wide) EmitConstI8(mask); else EmitConstI4(mask);
        _enc.OpCode(ILOpCode.And); Pop();
        if (mem.BitOffset > 0)
        {
            EmitConstI4(mem.BitOffset);
            _enc.OpCode(ILOpCode.Shl); Pop();
        }

        // Load old value, mask out old bits, OR in new bits
        // Stack: addr, shifted_new
        // We need: addr, (old & ~field_mask) | shifted_new
        // Duplicate addr, load old value
        // This requires reordering; use scratch (in the storage width when wide).
        int newValScratch = GetOrAddScratchLocal(wide ? mem.Ty : _types.TyInt);
        _enc.StoreLocal(newValScratch); Pop();
        _enc.OpCode(ILOpCode.Dup); Push(); // dup addr
        Load(mem.Ty); // load old value

        long clearMask = ~(mask << mem.BitOffset);
        if (wide) EmitConstI8(clearMask); else EmitConstI4(clearMask);
        _enc.OpCode(ILOpCode.And); Pop();
        _enc.LoadLocal(newValScratch); Push();
        _enc.OpCode(ILOpCode.Or); Pop();

        Store(node.Ty);

        _enc.LoadLocal(assignScratch); Push();
    }

    // ─── Type cast ───────────────────────────────────────────────

    // Default argument promotions for an argument passed through `...`:
    // integer types of rank < int are promoted to int; float is promoted to
    // double (the parser already applies float->double, but be defensive).
    private CType VaPromote(CType ty)
    {
        // Array / function args decay to pointer when passed (incl. through
        // varargs): a string literal "%s" arg is an array lvalue but the IL
        // pushes its address (char*), so the concrete vararg signature must
        // encode a pointer, not the array value type.
        if (ty.Kind == TypeKind.Array) return _types.PointerTo(ty.Base);
        if (ty.Kind == TypeKind.Func) return _types.PointerTo(ty);
        if (ty.Kind == TypeKind.Float) return _types.TyDouble;
        if (TypeSystem.IsInteger(ty) && ty.Size < _types.TyInt.Size)
            return ty.IsUnsigned ? _types.TyUint : _types.TyInt;
        return ty;
    }

    private void EmitCast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) { if (from.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); } return; }
        if (to.Kind == TypeKind.Bool)
        {
            // Non-zero → 1, zero → 0
            switch (from.Kind)
            {
                case TypeKind.Float:
                case TypeKind.Double:
                case TypeKind.LDouble:
                    if (from.Kind == TypeKind.Float)
                        _enc.LoadConstantR4(0.0f);
                    else
                        _enc.LoadConstantR8(0.0);
                    Push();
                    _enc.OpCode(ILOpCode.Ceq); Pop();
                    EmitConstI4(0);
                    _enc.OpCode(ILOpCode.Ceq); Pop();
                    break;
                default:
                    EmitConstI4(0);
                    if (from.Kind == TypeKind.Ptr) _enc.OpCode(ILOpCode.Conv_i);
                    else if (from.Size == 8) _enc.OpCode(ILOpCode.Conv_i8);
                    _enc.OpCode(ILOpCode.Cgt_un); Pop();
                    break;
            }
            return;
        }

        // From float/double
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsInteger(to))
        {
            if (to.Size <= 4)
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u4 : ILOpCode.Conv_i4);
            else
                _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (TypeSystem.IsInteger(from) && TypeSystem.IsFlonum(to))
        {
            if (from.IsUnsigned)
            {
                // conv.r.un interprets the stack value as unsigned for all integer sizes
                _enc.OpCode(ILOpCode.Conv_r_un);
                if (to.Kind == TypeKind.Float)
                    _enc.OpCode(ILOpCode.Conv_r4); // conv.r.un produces float64, narrow to float32
            }
            else if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }
        if (TypeSystem.IsFlonum(from) && TypeSystem.IsFlonum(to))
        {
            if (to.Kind == TypeKind.Float)
                _enc.OpCode(ILOpCode.Conv_r4);
            else
                _enc.OpCode(ILOpCode.Conv_r8);
            return;
        }

        // Integer → integer
        if (to.Kind == TypeKind.Ptr)
        {
            // Pointer: use conv.i/conv.u (native int) — correct for both 32-bit and 64-bit
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u : ILOpCode.Conv_i);
            return;
        }
        if ((to.Kind == TypeKind.Long && _dm.LongSize == 8) || to.Kind == TypeKind.LLong)
        {
            if (from.Size <= 4)
                _enc.OpCode(from.IsUnsigned ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            return;
        }
        if (to.Size == 1)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u1 : ILOpCode.Conv_i1);
        else if (to.Size == 2)
            _enc.OpCode(to.IsUnsigned ? ILOpCode.Conv_u2 : ILOpCode.Conv_i2);
        else if (to.Size == 4 && from.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i4);
        // If same size, no conv needed
    }

    // ═══════════════════════════════════════════════════════════════
    //  Statement code generation (GenStmt)
    // ═══════════════════════════════════════════════════════════════

    private void GenStmt(Node node)
    {
        if (node.Tok?.File != null)
        {
            var (sc, ec) = ColumnSpan(node.Tok);
            _enc.MarkLineNumber(_cvFile, node.Tok.LineNo, sc, ec);
        }

        switch (node.Kind)
        {
            case NodeKind.If:
            {
                var elseLabel = _enc.DefineLabel();
                var endLabel = _enc.DefineLabel();
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                _enc.Branch(ILOpCode.Brfalse, elseLabel); Pop();
                GenStmt(node.Then);
                _enc.Branch(ILOpCode.Br, endLabel);
                _enc.MarkLabel(elseLabel);
                if (node.Els != null) GenStmt(node.Els);
                _enc.MarkLabel(endLabel);
                return;
            }

            case NodeKind.For:
            {
                int forScopeStart = _enc.CodeBuilder.Count;
                var beginLabel = _enc.DefineLabel();
                var contLabel = _enc.DefineLabel();
                var brkLabel = _enc.DefineLabel();
                if (node.ContLabel != null) _labels[node.ContLabel] = contLabel;
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
                if (node.Cond != null)
                {
                    GenExpr(node.Cond);
                    NormalizeToBranchable(node.Cond.Ty);
                    _enc.Branch(ILOpCode.Brfalse, brkLabel); Pop();
                }
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                if (node.Inc != null)
                {
                    int incDepth = _stackDepth;
                    GenExpr(node.Inc);
                    while (_stackDepth > incDepth) { _enc.OpCode(ILOpCode.Pop); Pop(); }
                }
                SjBranch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
                RecordScopeRange(node.ScopeId, forScopeStart);
                return;
            }

            case NodeKind.Do:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = _enc.DefineLabel();
                var brkLabel = _enc.DefineLabel();
                if (node.ContLabel != null) _labels[node.ContLabel] = contLabel;
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                GenExpr(node.Cond);
                NormalizeToBranchable(node.Cond.Ty);
                SjBranch(ILOpCode.Brtrue, beginLabel); Pop();
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Switch:
            {
                var brkLabel = _enc.DefineLabel();
                if (node.BrkLabel != null) _labels[node.BrkLabel] = brkLabel;

                // x64: always if/else chain (no IL switch)
                GenExpr(node.Cond);
                int condScratch = GetOrAddScratchLocal(node.Cond.Ty);
                _enc.StoreLocal(condScratch); Pop();

                for (Node c = node.CaseNext; c != null; c = c.CaseNext)
                {
                    var caseLabel = _enc.DefineLabel();
                    _labels[c.Label] = caseLabel;
                    bool is64 = node.Cond.Ty.Size == 8;

                    if (c.Begin == c.End)
                    {
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.Branch(ILOpCode.Beq, caseLabel); Pop(2);
                    }
                    else
                    {
                        // Range case: val - begin <= (end - begin)
                        _enc.LoadLocal(condScratch); Push();
                        if (is64) EmitConstI8(c.Begin); else EmitConstI4(c.Begin);
                        _enc.OpCode(ILOpCode.Sub); Pop();
                        if (is64) EmitConstI8(c.End - c.Begin); else EmitConstI4(c.End - c.Begin);
                        _enc.Branch(ILOpCode.Ble_un, caseLabel); Pop(2);
                    }
                }

                if (node.DefaultCase != null)
                {
                    var defaultLabel = _enc.DefineLabel();
                    _labels[node.DefaultCase.Label] = defaultLabel;
                    _enc.Branch(ILOpCode.Br, defaultLabel);
                }
                else
                {
                    _enc.Branch(ILOpCode.Br, brkLabel);
                }

                GenStmt(node.Then);
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Case:
                if (_labels.TryGetValue(node.Label, out var cLabel))
                    _enc.MarkLabel(cLabel);
                GenStmt(node.Lhs);
                return;

            case NodeKind.Block:
            {
                int blockScopeStart = _enc.CodeBuilder.Count;
                for (Node n = node.Body; n != null; n = n.Next)
                    GenStmt(n);
                RecordScopeRange(node.ScopeId, blockScopeStart);
                return;
            }

            case NodeKind.Goto:
                if (!_labels.TryGetValue(node.UniqueLabel, out var gotoTarget))
                {
                    gotoTarget = _enc.DefineLabel();
                    _labels[node.UniqueLabel] = gotoTarget;
                }
                SjBranch(ILOpCode.Br, gotoTarget);
                return;

            case NodeKind.GotoExpr:
                Util.ErrorTok(node.Tok, "computed goto not supported in MSIL");
                return;

            case NodeKind.Label:
                if (!_labels.TryGetValue(node.UniqueLabel, out var labelTarget))
                {
                    labelTarget = _enc.DefineLabel();
                    _labels[node.UniqueLabel] = labelTarget;
                }
                _enc.MarkLabel(labelTarget);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(labelTarget);
                GenStmt(node.Lhs);
                return;

            case NodeKind.Return:
                if (_setjmpWrap && _setjmpTryOpen)
                {
                    // Inside the setjmp try: `ret` is illegal — funnel the value into
                    // the retval local and `leave` to the epilogue (which rets).
                    if (node.Lhs != null) { GenExpr(node.Lhs); _enc.StoreLocal(_setjmpRetvalLocal); Pop(); }
                    _enc.Branch(ILOpCode.Leave, _setjmpEpiLabel);
                    return;
                }
                // Pre-setjmp returns (defer mode, try not yet open) and non-setjmp
                // functions: a normal ret (these are outside any protected region).
                if (node.Lhs != null)
                {
                    GenExpr(node.Lhs);
                    Pop(); // ret consumes
                }
                _enc.OpCode(ILOpCode.Ret);
                return;

            case NodeKind.ExprStmt:
            {
                int depthBefore = _stackDepth;
                GenExpr(node.Lhs);
                // Pop any leftover value to maintain stack neutrality
                while (_stackDepth > depthBefore)
                {
                    _enc.OpCode(ILOpCode.Pop); Pop();
                }
                return;
            }

            case NodeKind.Asm:
                Util.ErrorTok(node.Tok, "inline assembly not supported in MSIL");
                return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }

    // ═══════════════════════════════════════════════════════════════
    //  __CxxPureMSILEntry body emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitCxxPureMSILEntry()
    {
        if (!_hasMain) return;

        var enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());

        // Count main's parameters
        int mainParamCount = 0;
        for (CType p = _mainObj.Ty.Params; p != null; p = p.Next)
            mainParamCount++;

        // Load argc, argv, and envp up to what main declares
        if (mainParamCount >= 1)
        {
            enc.OpCode(ILOpCode.Ldarg_0); // argc
        }
        if (mainParamCount >= 2)
        {
            enc.OpCode(ILOpCode.Ldarg_1); // argv
        }
        if (mainParamCount >= 3)
        {
            enc.OpCode(ILOpCode.Ldarg_2); // envp
        }

        enc.Call(_mainMethod);

        // If main returns void, push 0
        if (_mainObj.Ty.ReturnTy.Kind == TypeKind.Void)
            enc.OpCode(ILOpCode.Ldc_i4_0);

        enc.OpCode(ILOpCode.Ret);

        string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
        _bodyEncoder.AddMethodBody(_cxxPureMsilEntry, mangledName, enc,
            maxStack: Math.Max(mainParamCount, 1), localVariablesSignature: default, attributes: MethodBodyAttributes.InitLocals,
            debugName: "__CxxPureMSILEntry");
    }

    // ═══════════════════════════════════════════════════════════════
    //  NEP machinery emission
    // ═══════════════════════════════════════════════════════════════

    private void EmitNepMachinery(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;

            var methodDef = _methodDefs[fn];
            string mangledName = MangleFunctionName(fn);

            // Static functions use TU-hash-scoped bare names to avoid cross-TU collisions
            string bareName = fn.IsStatic ? $"{fn.Name}_?A0x{_tuHash}" : fn.Name;

            var bareSym = EmitNepForMethod(
                MetadataTokens.GetToken(methodDef), bareName, mangledName);

            // Also store under original name for __unep@ relocation lookup
            if (fn.IsStatic && !_nepBareNameSymbols.ContainsKey(fn.Name))
                _nepBareNameSymbols[fn.Name] = bareSym;

            if (fn.Ty.CallConv != CallConv.Clrcall && _addressTakenFuncs.Contains(fn.Name))
            {
                EmitUnepSlot(fn, bareSym);
            }
        }

        // NEP for __CxxPureMSILEntry
        if (_hasMain)
        {
            string mangledName = $"?__CxxPureMSILEntry@@$$J0YMHH{(Is32 ? "PAPA" : "PEAPEA")}D0@Z";
            EmitNepForMethod(
                MetadataTokens.GetToken(_cxxPureMsilEntry), "__CxxPureMSILEntry", mangledName);
        }

        // Emit ADDR relocs for extern __unep@ fields (not defined in this TU)
        foreach (var (funcName, _) in _unepFields)
        {
            if (_nepBareNameSymbols.ContainsKey(funcName)) continue; // already handled by local NEP
            if (!_unepSlotOffsets.TryGetValue(funcName, out int slotOffset)) continue;

            // Create an undefined external bare-name symbol — linker resolves from defining TU
            var externBareSym = _symtab.AddUndefinedExternalSymbol(SymPrefix + funcName);
            new CoffRelocationEncoder(_coffHeader, _dataRelocs)
                .AddAddressRelocation(slotOffset, externBareSym);
        }
    }

    /// <summary>
    /// CoreCLR target mode: emit a plain bare-name external COFF symbol for each
    /// live function definition, aliasing its managed method body in <c>.text$mn</c>.
    /// This replaces the IJW <see cref="EmitNepMachinery"/> path — no <c>.nep</c>
    /// thunk, no <c>__mep@</c> slot, no <c>.rdata$ilfixup</c> entry — while still
    /// giving other translation units a bare name (e.g. <c>fib</c> / <c>_fib</c>)
    /// to resolve C functions against.
    /// </summary>
    private void EmitPureMsilFunctionSymbols(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            if (!_methodBodyOffsets.TryGetValue(fn, out int bodyOffset)) continue;

            // Static functions use TU-hash-scoped bare names to avoid cross-TU collisions
            string bareName = fn.IsStatic ? $"{fn.Name}_?A0x{_tuHash}" : fn.Name;
            var bareSym = _symtab.AddExternalDataSymbol(
                SymPrefix + bareName, LogicalSection.Text, bodyOffset);
            _nepBareNameSymbols[bareName] = bareSym;
            if (fn.IsStatic && !_nepBareNameSymbols.ContainsKey(fn.Name))
                _nepBareNameSymbols[fn.Name] = bareSym;
        }
    }

    /// <summary>
    /// Emit NEP machinery for a single method: __mep@ slot, thunk, bare-name alias, ilfixup.
    /// </summary>
    private CoffSymbolHandle EmitNepForMethod(int methodToken, string bareName, string mangledSuffix)
    {
        var bareSym = ClrIjw.EmitNepMachinery(
            TargetMachine, Is32, PtrSize, SymPrefix,
            _coffHeader, _symtab,
            _dataStream, _dataRelocs,
            _nepStream, _nepRelocs,
            _ilFixupStream, _ilFixupRelocs,
            methodToken, bareName, mangledSuffix);
        _nepBareNameSymbols[bareName] = bareSym;
        return bareSym;
    }

    private void EmitUnepSlot(Obj fn, CoffSymbolHandle bareSym)
    {
        if (!_unepSlotOffsets.TryGetValue(fn.Name, out int slotOffset)) return;

        // ADDR relocation to the bare-name NEP thunk symbol
        new CoffRelocationEncoder(_coffHeader, _dataRelocs)
            .AddAddressRelocation(slotOffset, bareSym);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Global data emission
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Maps __unep@ field name → pre-allocated offset in .data for the slot.</summary>
    private readonly Dictionary<string, int> _unepSlotOffsets = new();

    /// <summary>Maps global Obj name → COFF data symbol handle for relocation targeting.</summary>
    private readonly Dictionary<string, CoffSymbolHandle> _dataCoffSymbols = new();

    /// <summary>Phase A: Write data bytes and register COFF data token symbols.
    /// Must run before IL emission so token ordering is correct.</summary>
    private void EmitGlobalDataBytesAndTokens(Obj prog)
    {
        // Register all global data symbols
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.TryGetValue(g, out var fieldDef)) continue;

            if (g.InitData != null)
            {
                bool isReadOnly = IsReadOnlyData(g);
                var stream = isReadOnly ? _rdataStream : _dataStream;
                var section = isReadOnly ? LogicalSection.RData : LogicalSection.Data;

                // Pad to required alignment
                int aligned = Util.AlignTo(stream.Count, g.Align);
                while (stream.Count < aligned) stream.WriteByte(0);

                int offset = stream.Count;

                // Copy InitData, writing addends at relocation offsets
                byte[] data = (byte[])g.InitData.Clone();
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    if (rel.Addend != 0)
                        Util.WriteBuf(data, rel.Offset, rel.Addend, PtrSize);
                }
                stream.WriteBytes(data);

                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, section, offset, out _,
                    isExternal: !g.IsStatic && !g.IsLocal);
                _dataCoffSymbols[g.Name] = coffSym;
            }
            else if (g.IsTentative)
            {
                if (g.IsStatic)
                {
                    // Static tentative → BSS with Static storage class (internal linkage)
                    int bssOffset = _bssSize;
                    _bssSize = Util.AlignTo(_bssSize + g.Ty.Size, g.Align);
                    var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Bss, bssOffset, out _,
                        isExternal: false);
                    _dataCoffSymbols[g.Name] = coffSym;
                }
                else
                {
                    // External tentative → common symbol (linker allocates)
                    var coffSym = _symtab.AddCommonDataClrToken(g.Name, fieldDef, g.Ty.Size, out _);
                    _dataCoffSymbols[g.Name] = coffSym;
                }
            }
            else
            {
                int bssOffset = _bssSize;
                _bssSize = Util.AlignTo(_bssSize + g.Ty.Size, g.Align);
                var coffSym = _symtab.AddDataClrToken(g.Name, fieldDef, LogicalSection.Bss, bssOffset, out _,
                    isExternal: !g.IsStatic && !g.IsLocal);
                _dataCoffSymbols[g.Name] = coffSym;
            }
        }

        // Pre-allocate __unep@ data slots (IJW-only).
        if (_options.Target == TargetProfile.Ijw)
        {
            foreach (var (funcName, unepField) in _unepFields)
            {
                Obj fn = null;
                for (Obj f = prog; f != null; f = f.Next)
                    if (f.IsFunction && f.Name == funcName) { fn = f; break; }
                if (fn == null) continue;

                string mangledName = MangleFunctionName(fn);
                string unepName = $"__unep@{mangledName}";

                int slotOffset = _dataStream.Count;
                for (int i = 0; i < PtrSize; i++) _dataStream.WriteByte(0);
                _unepSlotOffsets[funcName] = slotOffset;

                _symtab.AddDataClrToken(unepName, unepField, LogicalSection.Data, slotOffset, out _);
            }
        }
    }

    private static bool IsReadOnlyData(Obj g) => g.IsStringLiteral;

    /// <summary>Phase B: Write data relocations. Runs after NEP emission so
    /// bare-name symbols are available as relocation targets.</summary>
    private void EmitGlobalDataRelocations(Obj prog)
    {
        // Track cumulative offset through .data to match what Phase A wrote.
        // Read-only data (string literals) went to .rdata and must be skipped.
        int dataOffset = 0;
        for (Obj g = prog; g != null; g = g.Next)
        {
            if (g.IsFunction) continue;
            if (!g.IsDefinition) continue;
            if (!_fieldDefs.ContainsKey(g)) continue;
            if (g.InitData == null) continue;
            if (IsReadOnlyData(g)) continue;

            int offset = Util.AlignTo(dataOffset, g.Align);
            dataOffset = offset + g.InitData.Length;

            for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
            {
                string targetName = rel.Label();
                CoffSymbolHandle targetSym;

                if (_dataCoffSymbols.TryGetValue(targetName, out targetSym))
                {
                    // Data-to-data relocation (e.g., char* e = &hello[1])
                }
                else if (_nepBareNameSymbols.TryGetValue(targetName, out targetSym))
                {
                    // Function pointer relocation (e.g., int (*m)() = &get)
                }
                else
                {
                    // Unknown target — create as undefined external data symbol.
                    targetSym = _symtab.AddExternalDataSymbol(
                        SymPrefix + targetName, LogicalSection.Data, 0);
                    // If it is actually an undefined external FUNCTION whose address is
                    // baked into static data (a function-pointer table, e.g. QuickJS's
                    // js_math_funcs[]), also emit a MemberRef carrying its signature so
                    // chibil-link can bind a P/Invoke stub and ldftn it into the slot.
                    if (_options.Target == TargetProfile.CoreClr
                        && !_externalFuncRefs.ContainsKey(targetName))
                    {
                        for (Obj f = prog; f != null; f = f.Next)
                        {
                            if (f.IsFunction && !f.IsDefinition && f.Name == targetName
                                && f.Ty.CallConv != CallConv.Clrcall)
                            {
                                RegisterExternalFunction(f);
                                break;
                            }
                        }
                    }
                }

                new CoffRelocationEncoder(_coffHeader, _dataRelocs)
                    .AddAddressRelocation(offset + rel.Offset, targetSym);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scan for address-taken functions
    // ═══════════════════════════════════════════════════════════════

    private void ScanAddressTaken(Obj prog)
    {
        for (Obj fn = prog; fn != null; fn = fn.Next)
        {
            if (!fn.IsFunction || !fn.IsDefinition || !fn.IsLive) continue;
            ScanAddressTakenNode(fn.Body);

            // Also check global initializers that reference functions
            for (Obj g = prog; g != null; g = g.Next)
            {
                if (g.IsFunction) continue;
                for (Relocation rel = g.Rel; rel != null; rel = rel.Next)
                {
                    string label = rel.Label();
                    _addressTakenFuncs.Add(label);
                }
            }
        }
    }

    private void ScanAddressTakenNode(Node node)
    {
        if (node == null) return;
        // Explicit address-of: &func
        if (node.Kind == NodeKind.Addr && node.Lhs?.Kind == NodeKind.Var &&
            node.Lhs.Var.IsFunction)
        {
            _addressTakenFuncs.Add(node.Lhs.Var.Name);
        }
        // Implicit function-to-pointer: using function name as a value
        // (e.g., `fp = add;` without `&`)
        if (node.Kind == NodeKind.Var && node.Var != null && node.Var.IsFunction &&
            node.Var.Ty.CallConv != CallConv.Clrcall)
        {
            _addressTakenFuncs.Add(node.Var.Name);
        }
        // Function passed as argument to another function (e.g., `apply(add, 1, 2)`)
        if (node.Kind == NodeKind.FunCall)
        {
            for (Node arg = node.Args; arg != null; arg = arg.Next)
            {
                if (arg.Kind == NodeKind.Var && arg.Var != null && arg.Var.IsFunction &&
                    arg.Var.Ty.CallConv != CallConv.Clrcall)
                    _addressTakenFuncs.Add(arg.Var.Name);
            }
        }
        ScanAddressTakenNode(node.Lhs);
        ScanAddressTakenNode(node.Rhs);
        ScanAddressTakenNode(node.Cond);
        ScanAddressTakenNode(node.Then);
        ScanAddressTakenNode(node.Els);
        ScanAddressTakenNode(node.Init);
        ScanAddressTakenNode(node.Inc);
        ScanAddressTakenNode(node.Body);
        ScanAddressTakenNode(node.Next);
        for (Node arg = node.Args; arg != null; arg = arg.Next)
            ScanAddressTakenNode(arg);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Entry point
    // ═══════════════════════════════════════════════════════════════

    public byte[] Generate(Obj prog, string objName, string sourceFile)
    {
        _md = new MetadataBuilder();
        _coffHeader = new CoffHeaderBuilder(TargetMachine, 0);
        _symtab = new ManagedCoffSymbolTableBuilder(ObjectFeatures.None);

        _ilStreamBuilder = new BlobBuilder();
        _ilRelocBuilder = new BlobBuilder();
        _dataStream = new BlobBuilder();
        _dataRelocs = new BlobBuilder();
        _rdataStream = new BlobBuilder();
        _nepStream = new BlobBuilder();
        _nepRelocs = new BlobBuilder();
        _ilFixupStream = new BlobBuilder();
        _ilFixupRelocs = new BlobBuilder();
        _bssSize = 0;

        // AssemblyRef: mscorlib
        _mscorlibRef = _md.AddAssemblyReference(
            _md.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            _md.GetOrAddBlob(MscorlibPkt),
            default,
            _md.GetOrAddBlob(MscorlibHash));

        // CodeView debug info
        _codeviewSymbols = new CodeViewSymbolBuilder(_coffHeader);
        _codeviewSymbols.AddObjNameAndCompile3(objName,
            language: CodeViewLanguage.C,
            machine: CvMachine,
            feMajor: 19, feMinor: 50, feBuild: 35730,
            beMajor: 19, beMinor: 50, beBuild: 35730,
            "chibil C compiler",
            compileFlags: CodeViewCompileFlags.ManagedPresent | CodeViewCompileFlags.SecurityChecks);

        // Source file registration
        if (File.Exists(sourceFile))
        {
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourceFile));
            _cvFile = _codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.SHA256, sourceHash);
            _dbgSourceFile = sourceFile;
            _dbgSourceHash = sourceHash;
        }
        else
        {
            _cvFile = _codeviewSymbols.GetOrAddFile(sourceFile, CodeViewChecksumType.None, Array.Empty<byte>());
            _dbgSourceFile = sourceFile;
        }

        _bodyEncoder = new RelocatableMethodBodyStreamEncoder(
            _ilStreamBuilder, _ilRelocBuilder, _symtab, _coffHeader, _codeviewSymbols);

        // TU hash (from source path, matching MSVC behavior)
        byte[] pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceFile));
        _tuHash = BitConverter.ToString(pathHash, 0, 4).Replace("-", "").ToLowerInvariant();

        // In CoreCLR target mode, skip the /clr mixed-mode IJW machinery
        // (.nep section, .rdata$ilfixup, __unep@/__mep@ symbols, the
        // __CxxPureMSILEntry IJW shim body). The default Ijw path is unchanged.
        bool ijw = _options.Target == TargetProfile.Ijw;

        // Scan for address-taken functions before metadata registration
        ScanAddressTaken(prog);

        // Pass 1: Metadata
        RegisterMetadata(prog, objName);

        // Global data bytes + COFF token registration — BEFORE IL emission
        EmitGlobalDataBytesAndTokens(prog);

        // Pass 2: IL Emission
        EmitFunctions(prog);

        // Post-pass: __CxxPureMSILEntry (IJW shim body)
        if (ijw) EmitCxxPureMSILEntry();

        // NEP machinery (creates bare-name symbols for functions).
        // In CoreCLR mode, emit plain bare-name aliases instead (no thunk/mep/ilfixup).
        if (ijw) EmitNepMachinery(prog);
        else EmitPureMsilFunctionSymbols(prog);

        // Global data relocations — AFTER NEP so bare-name symbols exist
        EmitGlobalDataRelocations(prog);

        // Build COFF and serialize
        var coffBuilder = new ManagedCoffBuilder(_coffHeader, new MetadataRootBuilder(_md), _symtab, _codeviewSymbols,
            _ilStreamBuilder, _ilRelocBuilder,
            dataStream: _dataStream, dataRelocs: _dataRelocs,
            rdataStream: _rdataStream,
            ilFixupStream: _ilFixupStream, ilFixupRelocs: _ilFixupRelocs,
            nepStream: _nepStream, nepRelocs: _nepRelocs,
            bssSize: _bssSize);

        var dbg = BuildChibilDebugBlob();
        if (dbg != null)
            coffBuilder.SetChibilDebug(dbg);

        var output = new BlobBuilder();
        coffBuilder.Serialize(output);

        return output.ToArray();
    }

    // Serialize the .chidbg side-stream: magic 'CDBG', version 4, the primary
    // source file + SHA-256, then per-method (RID, IL size,
    // [(IL offset, line, startCol, endCol)], [scope: (start, len, [(slot, name)])]).
    // chibil-link transcodes this to the Portable PDB.
    private BlobBuilder BuildChibilDebugBlob()
    {
        if (_dbgMethods.Count == 0 || _dbgSourceFile == null)
            return null;

        var b = new BlobBuilder();
        b.WriteByte((byte)'C'); b.WriteByte((byte)'D'); b.WriteByte((byte)'B'); b.WriteByte((byte)'G');
        b.WriteByte(4);                                         // version

        byte[] path = System.Text.Encoding.UTF8.GetBytes(_dbgSourceFile);
        b.WriteUInt16((ushort)path.Length); b.WriteBytes(path);
        byte[] hash = _dbgSourceHash ?? Array.Empty<byte>();
        b.WriteByte((byte)hash.Length); b.WriteBytes(hash);

        b.WriteInt32(_dbgMethods.Count);
        foreach (var (rid, ilSize, pts, scopes) in _dbgMethods)
        {
            b.WriteInt32(rid);
            b.WriteInt32(ilSize);
            b.WriteInt32(pts.Count);
            foreach (var (il, line, sc, ec) in pts)
            {
                b.WriteInt32(il);
                b.WriteInt32(line);
                b.WriteInt32(sc);
                b.WriteInt32(ec);
            }
            b.WriteInt32(scopes.Count);
            foreach (var (start, len, locals) in scopes)
            {
                b.WriteInt32(start);
                b.WriteInt32(len);
                b.WriteInt32(locals.Count);
                foreach (var (slot, name) in locals)
                {
                    b.WriteInt32(slot);
                    byte[] nm = System.Text.Encoding.UTF8.GetBytes(name ?? "");
                    b.WriteUInt16((ushort)nm.Length); b.WriteBytes(nm);
                }
            }
        }
        return b;
    }
}
