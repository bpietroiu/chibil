using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Chibil;

/// <summary>
/// MSIL code generator — emits COFF object files with CIL bytecode.
/// Targets MSVC /clr mixed-mode (IJW) compatible output.
/// </summary>
public class CodeGen
{
    private readonly TypeSystem _types;
    private readonly MsilObjectEmitter _emit;
    private readonly CodeViewFileHandle _cvFile;
    private RelocatableInstructionEncoder _enc;
    private readonly Obj _currentFn;
    private readonly Dictionary<Obj, int> _localSlots;
    private readonly Dictionary<Obj, int> _paramSlots;
    private readonly List<(CType ty, int slot)> _scratchLocals;
    private readonly int _scratchLocalBase;
    private int _maxStack, _stackDepth;
    private readonly LabelHandle[] _labels;

    // Per-function: measured IL range [start, start+len) of each lexical scope, keyed
    // by the parser's scope index. Drives nested LocalScope emission so block-scoped
    // and shadowed locals are visible only within their block.
    private readonly Dictionary<int, (int Start, int Len)> _dbgScopeRanges = new();

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

    private void Push() { _stackDepth++; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Push(int n) { _stackDepth += n; if (_stackDepth > _maxStack) _maxStack = _stackDepth; }
    private void Pop() { Debug.Assert(_stackDepth > 0, "stack underflow"); _stackDepth--; }
    private void Pop(int n) { Debug.Assert(_stackDepth >= n, "stack underflow"); _stackDepth -= n; }

    public CodeGen(TypeSystem types, MsilObjectEmitter emit, Obj fn, CodeViewFileHandle cvFile)
    {
        _types = types;
        _emit = emit;
        _currentFn = fn;
        _cvFile = cvFile;
        _enc = new RelocatableInstructionEncoder(
            new BlobBuilder(), new MethodRelocationBuilder(),
            new RelocatableControlFlowBuilder(), new CodeViewLineNumberBuilder());
        _localSlots = new Dictionary<Obj, int>();
        _paramSlots = new Dictionary<Obj, int>();
        _scratchLocals = new List<(CType, int)>();
        _labels = new LabelHandle[fn.LabelCount];
        for (int i = 0; i < _labels.Length; i++)
            _labels[i] = _enc.DefineLabel();

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
    }

    public static CompiledMethod EmitFunction(TypeSystem types, MsilObjectEmitter emit, Obj fn, CodeViewFileHandle cvFile)
    {
        return new CodeGen(types, emit, fn, cvFile).Emit();
    }

    private CompiledMethod Emit()
    {
        // Emit function body. A function that calls setjmp is wrapped in a
        // try/filter/handler so a longjmp targeting it resumes (see the field block).
        _setjmpWrap = NodeContainsSetjmp(_currentFn.Body);
        if (_setjmpWrap)
        {
            EmitSetjmpWrappedBody(_currentFn);
        }
        else
        {
            GenStmt(_currentFn.Body);

            CType returnTy = _currentFn.Ty.ReturnTy;
            if (returnTy.Kind != TypeKind.Void)
            {
                if (IsStructOrUnion(returnTy))
                {
                    // For struct return, push a zeroed struct.
                    int scratch = GetOrAddScratchLocal(returnTy);
                    _enc.LoadLocalAddress(scratch); Push();
                    _enc.OpCode(ILOpCode.Initobj); _enc.Token(_emit.GetStructTypeHandle(returnTy)); Pop();
                    _enc.LoadLocal(scratch); Push();
                }
                else
                {
                    EmitTypedZero(returnTy);
                }
            }
            _enc.OpCode(ILOpCode.Ret);
        }

        // Build locals signature
        int totalLocals = _scratchLocalBase + _scratchLocals.Count;
        StandaloneSignatureHandle localsSig = default;
        if (totalLocals > 0)
        {
            var localsSigBlob = new BlobBuilder();
            localsSigBlob.WriteByte(0x07); // LOCAL_SIG
            localsSigBlob.WriteCompressedInteger(totalLocals);

            // User locals
            for (Obj local = _currentFn.Locals; local != null; local = local.Next)
            {
                if (_localSlots.ContainsKey(local))
                    _emit.EncodeType(localsSigBlob, local.Ty);
            }

            // Scratch locals
            foreach (var (ty, _) in _scratchLocals)
                _emit.EncodeType(localsSigBlob, ty);

            localsSig = _emit.AddStandaloneSignature(localsSigBlob);
        }

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

        // Collect named locals (slot, scopeId, name) for the managed-PDB side-stream.
        // MsilObjectEmitter groups these by scope and attaches each scope's measured
        // IL range from _dbgScopeRanges.
        var namedLocals = new List<(int Slot, int ScopeId, string Name)>();
        if (localsSig != default)
            foreach (var (local, slot) in _localSlots)
            {
                if (local.Name == null) continue;
                namedLocals.Add((slot, local.ScopeId, local.Name));
            }

        return new CompiledMethod(_enc, _maxStack, localsSig,
            localSlotList.Count > 0 ? localSlotList.ToArray() : null,
            _dbgScopeRanges, namedLocals);
    }

    private LabelHandle GetLabel(int label) => _labels[label - 1];

    private void GenExprDiscard(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Assign:
                GenAssign(node, wantValue: false);
                return;
            case NodeKind.Comma:
                GenExprDiscard(node.Lhs);
                GenExprDiscard(node.Rhs);
                return;
            case NodeKind.StmtExpr:
                for (Node n = node.Body; n != null; n = n.Next)
                    GenStmt(n);
                return;
            case NodeKind.Cast when node.Ty.Kind == TypeKind.Void:
                GenExprDiscard(node.Lhs);
                return;
        }

        int depthBefore = _stackDepth;
        GenExpr(node);
        while (_stackDepth > depthBefore)
        {
            _enc.OpCode(ILOpCode.Pop);
            Pop();
        }
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
                if (IsAggregateType(ty))
                {
                    if (_types.GetTypeId(existingTy) == _types.GetTypeId(ty))
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
    /// <em>cond loop</em> or a <em>conditional branch</em>. Both make the resume-at-site
    /// try region (which runs from the setjmp site to body-end) unsafe:
    /// <list type="bullet">
    /// <item><b>cond loop</b> — a `for`/`while` with a condition emits a forward exit
    /// (`brfalse brkLabel`) from before the body to a label after the loop; that label is
    /// inside the try, so the exit is an illegal branch INTO the protected region.
    /// `for(;;)`/`do-while` have no such forward exit and stay resume-at-site (back-edges
    /// become `leave` via trampolines).</item>
    /// <item><b>conditional branch</b> — a setjmp inside the THEN/ELSE arm of an enclosing
    /// `if`/`switch` (mp_iternext nests it 2 deep). The enclosing test branches (emitted
    /// before the setjmp site, hence outside the try) target the sibling arm / merge, which
    /// the body-end try swallows — another illegal branch INTO the try. Inherited through
    /// branch arms, NOT through `if`-conditions or loop bodies, so the plain `if(setjmp())`
    /// idiom and `for(;;){ if(setjmp())… }` stay resume-at-site.</item>
    /// </list>
    /// When either holds, the caller falls back to whole-body re-from-top (everything
    /// inside the try → no branch crosses in).</summary>
    private static (int count, bool insideCondLoop, bool insideCondBranch) CountSetjmp(
        Node n, bool inCondLoop = false, bool inCondBranch = false)
    {
        int count = 0;
        bool condLoop = false, condBranch = false;
        for (; n != null; n = n.Next)
        {
            if (n.Kind == NodeKind.FunCall && n.Lhs != null && n.Lhs.Kind == NodeKind.Var
                && n.Lhs.Var != null && n.Lhs.Var.IsFunction && IsSetjmpName(n.Lhs.Var.Name))
            {
                count++;
                if (inCondLoop) condLoop = true;
                if (inCondBranch) condBranch = true;
            }
            bool childInCondLoop = inCondLoop || (n.Kind == NodeKind.For && n.Cond != null);
            // Then/Els are conditional ARMS for if/switch (not for loops, whose Then/Body
            // is the loop body); Cond and the other slots are not arms.
            bool armsAreBranches = n.Kind == NodeKind.If || n.Kind == NodeKind.Switch;
            void Walk(Node c, bool branch)
            {
                var (cc, cl, cb) = CountSetjmp(c, childInCondLoop, branch);
                count += cc; condLoop |= cl; condBranch |= cb;
            }
            Walk(n.Lhs, inCondBranch); Walk(n.Rhs, inCondBranch); Walk(n.Cond, inCondBranch);
            Walk(n.Init, inCondBranch); Walk(n.Inc, inCondBranch); Walk(n.Args, inCondBranch);
            Walk(n.Body, inCondBranch);
            Walk(n.Then, inCondBranch || armsAreBranches);
            Walk(n.Els, inCondBranch || armsAreBranches);
        }
        return (count, condLoop, condBranch);
    }

    // Default-convention MemberRef signatures for the linker-synthesized helpers.
    private static readonly byte[] RtLongjmp = { 0x00, 0x02, 0x01, 0x18, 0x08 }; // void(native int, int32)
    private static readonly byte[] RtMatch   = { 0x00, 0x01, 0x08, 0x18 };       // int32(native int)
    private static readonly byte[] RtBuf     = { 0x00, 0x00, 0x18 };             // native int()
    private static readonly byte[] RtVal     = { 0x00, 0x00, 0x08 };             // int32()
    private static readonly byte[] RtClear   = { 0x00, 0x00, 0x01 };             // void()

    private void EmitRuntimeCall(string name, byte[] sig, int nArgs, bool hasRet)
    {
        _enc.Call(_emit.RuntimeHelperRef(name, sig));
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
        var (sjCount, sjInCondLoop, sjInCondBranch) = CountSetjmp(fn.Body);
        // Resume-at-site only for a single setjmp that is NOT inside a cond loop or a
        // conditional branch arm — either would create an illegal branch INTO the
        // body-end try region. Otherwise wrap the whole body (re-from-top), where every
        // branch stays inside the try. (mp_iternext nests its setjmp in if-arms.)
        _setjmpDeferStart = sjCount == 1 && !sjInCondLoop && !sjInCondBranch;
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
                    EmitFunctionAddress(node.Var);
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
                _enc.OpCode(ILOpCode.Ldsflda); _enc.Token(_emit.GetOrRegisterGlobalField(node.Var)); Push();
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                return;

            case NodeKind.Comma:
                GenExprDiscard(node.Lhs);
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

            case NodeKind.FunCall when IsStructOrUnion(node.Ty):
            case NodeKind.Assign when IsStructOrUnion(node.Ty):
            case NodeKind.Cond when IsStructOrUnion(node.Ty):
                GenExpr(node);
                var handle = _emit.GetStructTypeHandle(node.Ty);
                if (handle.IsNil)
                {
                    // Nested/flattened struct — GenExpr returned an address.
                    int scratch = GetOrAddScratchLocal(_types.PointerTo(_types.TyVoid));
                    _enc.StoreLocal(scratch); Pop();
                    _enc.LoadLocal(scratch); Push();
                    return;
                }

                int valueScratch = GetOrAddScratchLocal(node.Ty);
                _enc.StoreLocal(valueScratch); Pop();
                _enc.LoadLocalAddress(valueScratch); Push();
                return;

            case NodeKind.FunCall:
            case NodeKind.Assign:
            case NodeKind.Cond:
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
            case TypeKind.Array or TypeKind.Func or TypeKind.Vla:
                // Array decays to a pointer to its first element; the address (a
                // managed pointer to the array value-type) IS that value. The JIT
                // accepts &$ArrayType$ where a native int is required.
                return;
            case TypeKind.Struct or TypeKind.Union:
            {
                var handle = _emit.GetStructTypeHandle(ty);
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
            case TypeKind.Double or TypeKind.LDouble:
                _enc.OpCode(ILOpCode.Ldind_r8); return;
            case TypeKind.Ptr:
                // Pointers are native-int sized; use ldind.i so the stack type is a
                // native int (a valid address), not int64.
                _enc.OpCode(ILOpCode.Ldind_i); return;
        }

        // Integer types
        if (ty.Size == 1) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u1 : ILOpCode.Ldind_i1);
        else if (ty.Size == 2) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u2 : ILOpCode.Ldind_i2);
        else if (ty.Size == 4) _enc.OpCode(ty.IsUnsigned ? ILOpCode.Ldind_u4 : ILOpCode.Ldind_i4);
        else _enc.OpCode(ILOpCode.Ldind_i8);
    }

    private void Store(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Struct or TypeKind.Union:
            {
                var handle = _emit.GetStructTypeHandle(ty);
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
            case TypeKind.Double or TypeKind.LDouble:
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

    // ═══════════════════════════════════════════════════════════════
    //  Branch normalization helpers
    // ═══════════════════════════════════════════════════════════════

    private void EmitBranch(ILOpCode opcode, LabelHandle label, CType conditionType)
    {
        if (TypeSystem.IsFlonum(conditionType))
            EmitNonZero(conditionType);
        _enc.Branch(opcode, label);
        Pop();
    }

    private void EmitNonZero(CType ty)
    {
        if (TypeSystem.IsFlonum(ty))
        {
            EmitTypedZero(ty);
            _enc.OpCode(ILOpCode.Ceq); Pop();
            EmitConstI4(0);
            _enc.OpCode(ILOpCode.Ceq); Pop();
            return;
        }

        EmitConstI4(0);
        if (ty.Kind is TypeKind.Ptr or TypeKind.Func or TypeKind.Array or TypeKind.Vla)
            _enc.OpCode(ILOpCode.Conv_i);
        else if (ty.Size == 8)
            _enc.OpCode(ILOpCode.Conv_i8);
        _enc.OpCode(ILOpCode.Cgt_un); Pop();
    }

    private void EmitTypedZero(CType ty)
    {
        switch (ty.Kind)
        {
            case TypeKind.Float:
                _enc.LoadConstantR4(0.0f); Push();
                return;
            case TypeKind.Double or TypeKind.LDouble:
                _enc.LoadConstantR8(0.0); Push();
                return;
            case TypeKind.LLong:
                EmitConstI8(0);
                return;
            case TypeKind.Long when _types.DataModel.LongSize == 8:
                // LP64: long is 8 bytes = int64
                EmitConstI8(0);
                return;
            case TypeKind.Ptr or TypeKind.Func or TypeKind.Array or TypeKind.Vla:
                EmitConstI4(0);
                _enc.OpCode(ILOpCode.Conv_i);
                return;
            default:
                EmitConstI4(0);
                return;
        }
    }

    private static bool IsStructOrUnion(CType ty) =>
        ty.Kind is TypeKind.Struct or TypeKind.Union;

    private static bool IsAggregateType(CType ty) =>
        ty.Kind is TypeKind.Struct or TypeKind.Union or TypeKind.Array;

    /// <summary>Push a callable function address onto the evaluation stack.</summary>
    private void EmitFunctionAddress(Obj fn)
    {
        CType funcTy = fn.Ty;
        if (_emit.Target == TargetProfile.CoreClr)
        {
            // Pure-MSIL: every C function is a managed method. Take its address with
            // ldftn regardless of (cdecl/stdcall/clrcall) calling convention — there
            // are no native __unep@ slots in this target. GetFunctionToken returns the
            // MethodDef for a local definition or registers/returns the external
            // MemberRef for a forward/extern declaration.
            EntityHandle ftn = _emit.GetFunctionToken(fn);
            _enc.OpCode(ILOpCode.Ldftn); _enc.Token(ftn); Push(); return;
        }
        if (funcTy.CallConv == CallConv.Clrcall)
        {
            EntityHandle md = _emit.GetFunctionToken(fn);
            _enc.OpCode(ILOpCode.Ldftn); _enc.Token(md); Push();
        }
        else
        {
            // unmanaged: load the native function pointer from __unep@ field
            FieldDefinitionHandle unepField = _emit.GetUnepFieldToken(fn);
            _enc.OpCode(ILOpCode.Ldsfld); _enc.Token(unepField); Push();
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
                        if (node.Ty.Kind == TypeKind.Long && _types.DataModel.LongSize == 8)
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
                    EmitFunctionAddress(node.Var);
                    return;
                }
                if (node.Var.IsLocal && !IsAggregateType(node.Ty))
                {
                    // Simple scalar local/param — use direct load
                    LoadLocalOrParam(node.Var);
                    return;
                }
                // Global scalars/aggregates: load via address (ldsflda + Load). Upstream's
                // #31 "direct scalar global field access" (ldsfld) is NOT adopted: our
                // global-field machinery (FieldRVA storage, $GlobalFields container
                // partitioning, cross-object resolution) is only sound through the
                // address-based path — a direct ldsfld miscompiles after linking
                // (InvalidProgramException, e.g. SQLite's disk VFS vRand). Load is
                // type-agnostic; GetOrRegisterGlobalField lazily registers the field.
                GenAddr(node);
                Load(node.Ty);
                return;

            case NodeKind.Member:
                GenAddr(node);
                Load(node.Ty);
                if (node.Member.IsBitfield)
                    ExtractBitfieldValue(node.Member);
                return;

            case NodeKind.Deref:
                GenExpr(node.Lhs);
                Load(node.Ty);
                return;

            case NodeKind.Addr:
                GenAddr(node.Lhs);
                return;

            case NodeKind.Assign:
                GenAssign(node, wantValue: true);
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
                GenExprDiscard(node.Lhs);
                Debug.Assert(_stackDepth == depthBeforeComma);
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
                EmitBranch(ILOpCode.Brfalse, elseLabel, node.Cond.Ty);
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
                EmitBranch(ILOpCode.Brfalse, falseLabel, node.Lhs.Ty);
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                EmitBranch(ILOpCode.Brfalse, falseLabel, node.Rhs.Ty);
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
                EmitBranch(ILOpCode.Brtrue, trueLabel, node.Lhs.Ty);
                _stackDepth = savedDepth;
                GenExpr(node.Rhs);
                EmitBranch(ILOpCode.Brtrue, trueLabel, node.Rhs.Ty);
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

    private void GenAssign(Node node, bool wantValue)
    {
        if (node.Lhs.Kind == NodeKind.Member && node.Lhs.Member.IsBitfield)
        {
            GenBitfieldAssign(node, wantValue);
            return;
        }

        // Fast path for simple scalar LOCAL/param stores only. Global scalars fall
        // through to the generic address-based store (GenAddr ldsflda + Store stind):
        // upstream's #31 direct `stsfld` to globals is NOT adopted — our global-field
        // machinery is only sound through the address path (a direct stsfld miscompiles
        // after linking; see the matching note in GenExpr's Var case).
        if (node.Lhs.Kind == NodeKind.Var && node.Lhs.Var.IsLocal && !IsAggregateType(node.Ty))
        {
            GenExpr(node.Rhs);
            if (wantValue)
            {
                _enc.OpCode(ILOpCode.Dup);
                Push();
            }
            StoreLocalOrParam(node.Lhs.Var);
            return;
        }

        // If the RHS emits a `localloc` (alloca / Layer-1 variadic call), it must run
        // with an empty evaluation stack — so it cannot be generated AFTER the
        // destination address is pushed (the generic path below). Evaluate the RHS
        // into a scratch FIRST (stack empty), then take the lvalue address and store.
        // C leaves assignment operand evaluation order unspecified, so this is conforming.
        if (ProducesLocalloc(node.Rhs))
        {
            GenExpr(node.Rhs);
            int rhsScratch = AddFreshScratchLocal(node.Ty);
            _enc.StoreLocal(rhsScratch); Pop();
            GenAddr(node.Lhs);
            _enc.LoadLocal(rhsScratch); Push();
            Store(node.Ty);
            if (wantValue) { _enc.LoadLocal(rhsScratch); Push(); }
            return;
        }

        GenAddr(node.Lhs);
        if (IsStructOrUnion(node.Ty) &&
            _emit.GetStructTypeHandle(node.Ty).IsNil)
        {
            if (wantValue)
            {
                // Nested/flattened struct: GenExpr(rhs) returns an address.
                // Save dest address before generating rhs so the assignment
                // expression result refers to the destination, not the source.
                // Use a fresh scratch to avoid clobber by inner chain assignments.
                var destScratch = AddFreshScratchLocal(_types.PointerTo(_types.TyVoid));
                _enc.OpCode(ILOpCode.Dup);
                Push();
                _enc.StoreLocal(destScratch);
                Pop();
                GenExpr(node.Rhs);
                Store(node.Ty);
                _enc.LoadLocal(destScratch);
                Push();
            }
            else
            {
                GenExpr(node.Rhs);
                Store(node.Ty);
            }
            return;
        }

        GenExpr(node.Rhs);
        if (wantValue)
        {
            int assignScratch = GetOrAddScratchLocal(node.Ty);
            _enc.OpCode(ILOpCode.Dup);
            Push();
            _enc.StoreLocal(assignScratch);
            Pop();
            Store(node.Ty);
            _enc.LoadLocal(assignScratch);
            Push();
        }
        else
        {
            Store(node.Ty);
        }
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
                    // Re-arm reset: when this setjmp is re-entered on a loop back-edge —
                    // e.g. mp_execute_bytecode's `for(;;){ if(setjmp()==0){…} else {…} }`,
                    // which catches an exception in the else and continues the loop — the
                    // re-armed setjmp must return 0 again. sjval still holds the PRIOR
                    // longjmp value (the handler stored it), so zero it here. This sits
                    // BEFORE Lhead, so a longjmp RESUME (which `leave`s to Lhead) skips the
                    // reset and keeps the stored value, while the direct call and every
                    // loop re-arm fall through it. (Without this, the re-armed setjmp keeps
                    // returning the stale nonzero value → the else branch loops forever.)
                    EmitConstI4(0);
                    _enc.StoreLocal(sjval); Pop();
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
                    CType promoted = _emit.VaPromote(arg.Ty);
                    if (promoted != arg.Ty)
                        EmitCast(arg.Ty, promoted);
                }
                argCount++;
            }

            var concreteRef = _emit.RegisterConcreteVarargCall(node.Lhs.Var, funcTy, node.Args);
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
                    CType promoted = _emit.VaPromote(va.Ty);

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
            byte calliConv = _emit.Target == TargetProfile.CoreClr
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
            _emit.EncodeReturnType(calliSig, funcTy);

            // Params
            for (CType p = funcTy.Params; p != null; p = p.Next)
                _emit.EncodeType(calliSig, p);
            if (hasVaPtr)
                _emit.EncodeType(calliSig, _types.TyVaList); // hidden __va pointer

            var calliSigHandle = _emit.AddStandaloneSignature(calliSig);
            _enc.CallIndirect(calliSigHandle);
            Pop(argCount + 1); // pop args + function pointer
        }
        else
        {
            // Direct call
            _enc.Call(_emit.GetFunctionToken(node.Lhs.Var));
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
        var interlocked = _emit.GetInterlockedRef();
        var cxchgRef = _emit.GetLazyMemberRef("Interlocked.CompareExchange", interlocked, "CompareExchange", () =>
        {
            var sig = new BlobBuilder();
            sig.WriteByte(0x00); // DEFAULT
            sig.WriteCompressedInteger(3);
            sig.WriteByte((byte)SignatureTypeCode.Int32); // return
            sig.WriteByte((byte)SignatureTypeCode.Pointer);
            sig.WriteByte((byte)SignatureTypeCode.Int32); // ref param
            sig.WriteByte((byte)SignatureTypeCode.Int32);
            sig.WriteByte((byte)SignatureTypeCode.Int32);
            return sig;
        });
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

        var interlocked = _emit.GetInterlockedRef();
        var xchgRef = _emit.GetLazyMemberRef("Interlocked.Exchange", interlocked, "Exchange", () =>
        {
            var sig = new BlobBuilder();
            sig.WriteByte(0x00);
            sig.WriteCompressedInteger(2);
            sig.WriteByte((byte)SignatureTypeCode.Int32);
            sig.WriteByte((byte)SignatureTypeCode.Pointer);
            sig.WriteByte((byte)SignatureTypeCode.Int32);
            sig.WriteByte((byte)SignatureTypeCode.Int32);
            return sig;
        });
        _enc.Call(xchgRef);
        Pop(); // 2 args → 1 result
    }

    // ─── Bitfield assignment ─────────────────────────────────────

    private void GenBitfieldAssign(Node node, bool wantValue)
    {
        Member mem = node.Lhs.Member;
        GenAddr(node.Lhs);

        // Save address for later store
        _enc.OpCode(ILOpCode.Dup); Push();

        GenExpr(node.Rhs);

        ulong mask = BitMask(mem.BitWidth);

        // Mask and shift new value into position
        EmitBitfieldStorageConst(mem, mask);
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
        int newValScratch = GetOrAddScratchLocal(mem.Ty.Size <= 4 ? _types.TyInt : mem.Ty);
        _enc.StoreLocal(newValScratch); Pop();
        _enc.OpCode(ILOpCode.Dup); Push(); // dup addr
        Load(mem.Ty); // load old value

        ulong clearMask = ~(mask << mem.BitOffset);
        EmitBitfieldStorageConst(mem, clearMask);
        _enc.OpCode(ILOpCode.And); Pop();
        _enc.LoadLocal(newValScratch); Push();
        _enc.OpCode(ILOpCode.Or); Pop();

        Store(node.Ty);

        if (wantValue)
        {
            _enc.OpCode(ILOpCode.Dup); Push();
            Load(mem.Ty);
            ExtractBitfieldValue(mem);
            int assignScratch = GetOrAddScratchLocal(node.Ty);
            _enc.StoreLocal(assignScratch); Pop();
            _enc.OpCode(ILOpCode.Pop); Pop(); // discard the saved destination address
            _enc.LoadLocal(assignScratch);
            Push();
        }
        else
        {
            _enc.OpCode(ILOpCode.Pop); Pop(); // discard the saved destination address
        }
    }

    private static ulong BitMask(int width) =>
        width >= 64 ? ulong.MaxValue : (1UL << width) - 1;

    private void EmitBitfieldStorageConst(Member mem, ulong value)
    {
        if (mem.Ty.Size <= 4)
            EmitConstI4(unchecked((int)value));
        else
            EmitConstI8(unchecked((long)value));
    }

    private void ExtractBitfieldValue(Member mem)
    {
        // Shift-extract sign/zero-fills from the MSB of the loaded VALUE, not the
        // storage unit: `Load` widens a sub-word storage type to a 32-bit stack int
        // (u8/u16/u32 -> i4), 8 bytes to i8. Sizing the shifts by the storage width
        // (Ty.Size*8) would leave the storage unit's other bits in the result and
        // mis-sign-extend signed u8/u16 bitfields. Use the loaded container's width:
        // 32 for Size<=4, 64 for Size==8.
        int containerBits = mem.Ty.Size <= 4 ? 32 : 64;
        int shift = containerBits - mem.BitWidth - mem.BitOffset;
        if (shift > 0)
        {
            EmitConstI4(shift);
            _enc.OpCode(ILOpCode.Shl); Pop();
        }

        int rightShift = containerBits - mem.BitWidth;
        if (rightShift > 0)
        {
            EmitConstI4(rightShift);
            _enc.OpCode(mem.Ty.IsUnsigned ? ILOpCode.Shr_un : ILOpCode.Shr); Pop();
        }
    }

    // ─── Type cast ───────────────────────────────────────────────

    private void EmitCast(CType from, CType to)
    {
        if (to.Kind == TypeKind.Void) { if (from.Kind != TypeKind.Void) { _enc.OpCode(ILOpCode.Pop); Pop(); } return; }
        if (to.Kind == TypeKind.Bool)
        {
            EmitNonZero(from);
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
        if ((to.Kind == TypeKind.Long && _types.DataModel.LongSize == 8) || to.Kind == TypeKind.LLong)
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

    /// <summary>True if control can never fall off the end of <paramref name="n"/> —
    /// it always returns or branches away (return, goto, and break/continue which the
    /// parser lowers to goto). Conservative: only the cases certain to transfer return
    /// true, so a needed branch is never suppressed. Used to drop dead merge branches
    /// (notably the if/else merge that would otherwise branch into the setjmp try).</summary>
    private static bool StmtAlwaysTransfers(Node n)
    {
        if (n == null) return false;
        switch (n.Kind)
        {
            case NodeKind.Return:
            case NodeKind.Goto:        // break/continue are lowered to goto
                return true;
            case NodeKind.Block:
            {
                Node last = null;
                for (Node s = n.Body; s != null; s = s.Next) last = s;
                return StmtAlwaysTransfers(last);
            }
            case NodeKind.If:
                return n.Els != null && StmtAlwaysTransfers(n.Then) && StmtAlwaysTransfers(n.Els);
            case NodeKind.Label:
                return StmtAlwaysTransfers(n.Lhs);
            default:
                return false;
        }
    }

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
                EmitBranch(ILOpCode.Brfalse, elseLabel, node.Cond.Ty);
                GenStmt(node.Then);
                // The merge branch to endLabel is dead when the then-branch can't fall
                // through (it ends in return/goto). Emitting it anyway is normally just
                // dead code, but under the setjmp wrap the then-branch can sit OUTSIDE the
                // try while endLabel sits INSIDE it (the setjmp lives in the else-branch),
                // making `br endLabel` an illegal branch-INTO-the-try -> the JIT rejects
                // the whole method (InvalidProgramException). Suppress it when unreachable.
                if (!StmtAlwaysTransfers(node.Then))
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
                var contLabel = GetLabel(node.ContLabelId);
                var brkLabel = GetLabel(node.BrkLabelId);

                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
                if (node.Cond != null)
                {
                    GenExpr(node.Cond);
                    EmitBranch(ILOpCode.Brfalse, brkLabel, node.Cond.Ty);
                }
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                if (node.Inc != null)
                {
                    int incDepth = _stackDepth;
                    GenExprDiscard(node.Inc);
                    Debug.Assert(_stackDepth == incDepth);
                }
                SjBranch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
                RecordScopeRange(node.ScopeId, forScopeStart);
                return;
            }

            case NodeKind.Do:
            {
                var beginLabel = _enc.DefineLabel();
                var contLabel = GetLabel(node.ContLabelId);
                var brkLabel = GetLabel(node.BrkLabelId);

                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
                GenStmt(node.Then);
                _enc.MarkLabel(contLabel);
                GenExpr(node.Cond);
                // Mirror EmitBranch's float normalization, but route through SjBranch so
                // the loop back-edge is trampolined when it exits a setjmp try region.
                if (TypeSystem.IsFlonum(node.Cond.Ty)) EmitNonZero(node.Cond.Ty);
                SjBranch(ILOpCode.Brtrue, beginLabel); Pop();
                _enc.MarkLabel(brkLabel);
                return;
            }

            case NodeKind.Switch:
            {
                var brkLabel = GetLabel(node.BrkLabelId);

                // x64: always if/else chain (no IL switch)
                GenExpr(node.Cond);
                int condScratch = GetOrAddScratchLocal(node.Cond.Ty);
                _enc.StoreLocal(condScratch); Pop();
                bool is64 = node.Cond.Ty.Size == 8;

                for (Node c = node.CaseNext; c != null; c = c.CaseNext)
                {
                    var caseLabel = GetLabel(c.LabelId);
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
                    var defaultLabel = GetLabel(node.DefaultCase.LabelId);
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
                _enc.MarkLabel(GetLabel(node.LabelId));
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
                // SjBranch redirects a branch that exits a setjmp try into a `leave`
                // via a trampoline (no-op when not in a setjmp-wrapped function).
                SjBranch(ILOpCode.Br, GetLabel(node.LabelId));
                return;

            case NodeKind.Label:
            {
                var labelTarget = GetLabel(node.LabelId);
                _enc.MarkLabel(labelTarget);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(labelTarget);
                GenStmt(node.Lhs);
                return;
            }

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
                GenExprDiscard(node.Lhs);
                Debug.Assert(_stackDepth == depthBefore);
                return;
            }

            case NodeKind.Asm:
                Util.ErrorTok(node.Tok, "inline assembly not supported in MSIL");
                return;
        }
        Util.ErrorTok(node.Tok, "invalid statement");
    }
}

public record struct CompiledMethod(
    RelocatableInstructionEncoder Instructions,
    int MaxStack,
    StandaloneSignatureHandle LocalVariables,
    CodeViewManSlot[] LocalDebugInfo,
    // Managed-PDB side-stream inputs (collected during IL gen):
    // measured IL range per parser scope index, and the named locals
    // (slot, scopeId, name). MsilObjectEmitter.EmitFunctions assembles the
    // per-method .chidbg record from these. See BuildChibilDebugBlob.
    Dictionary<int, (int Start, int Len)> DbgScopeRanges,
    List<(int Slot, int ScopeId, string Name)> DbgNamedLocals);