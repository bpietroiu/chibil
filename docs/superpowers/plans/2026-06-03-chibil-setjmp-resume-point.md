# Setjmp Resume-Point Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a `longjmp` resume *at* the `setjmp` call instead of re-running the function body from the top, so side effects sequenced before `setjmp` (MicroPython's `nlr.ret_val = NULL`) are not re-executed and clobber the longjmp'd value.

**Architecture:** For a function with exactly one `setjmp` call not lexically inside a loop, place the `setjmp`-wrap's try-region start (`Lhead`/`tryStart`) at the `setjmp` call site rather than the function top — so code before `setjmp` runs once (outside the try) and the handler re-enters at the call. Multi-`setjmp` and `setjmp`-in-loop functions keep today's re-from-top behavior. All in `chibil/CodeGen.cs`.

**Tech Stack:** C# / .NET, `System.Reflection.Metadata` IL emission, xUnit, `DotnetHostRunner` (runs a linked PE via the dotnet host).

**Spec:** `docs/superpowers/specs/2026-06-03-chibil-setjmp-resume-point-design.md`

---

### Task 1: Red tests — pre-`setjmp` code must run exactly once

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs` (add two `[Fact]`s near the existing `Setjmp_longjmp_resumes_across_frames`, ~line 162)

Background: `LinkRun(string[] sources, out string output)` (defined ~line 198 in the same file) compiles each source with chibil, links to a PE, runs it via the dotnet host, and returns the process exit code. `DotnetHostRunner.DotnetAvailable()` guards on the host being present (mirror the existing setjmp test).

- [ ] **Step 1: Add the two failing tests**

Insert after the `Setjmp_longjmp_resumes_across_frames` method (after its closing `}`, ~line 162):

```csharp
    [Fact]
    public void Setjmp_does_not_rerun_code_before_the_setjmp_call_on_resume()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // chibil resumes a longjmp by re-entering the function. Code sequenced BEFORE
        // the setjmp call must run EXACTLY ONCE, else it re-executes on resume. Here
        // `count++` runs once on the first pass; the longjmp resumes after setjmp, so
        // count must still be 1. The old re-from-top model re-ran count++ -> 2.
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "jmp_buf jb;\n" +
            "int count;\n" +
            "int main(void){\n" +
            "  count++;\n" +                            // pre-setjmp: must run once
            "  int v = setjmp(jb);\n" +
            "  if (v == 0) longjmp(jb, 1);\n" +
            "  return count;\n" +                       // fixed: 1 ; bug: 2
            "}\n",
        }, out string o);
        Assert.True(exit == 1, $"expected 1 (count++ ran once), got {exit}. {o}");
    }

    [Fact]
    public void Setjmp_preserves_value_written_before_longjmp_on_resume()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // The exact MicroPython nlr shape: a slot is initialised before setjmp, then a
        // value is stored into it right before longjmp. On resume the pre-setjmp init
        // must NOT re-run and wipe that value (nlr.ret_val = NULL re-running clobbered
        // the exception object -> NullReferenceException in parse_compile_execute).
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "jmp_buf jb;\n" +
            "void *slot;\n" +
            "int main(void){\n" +
            "  slot = 0;\n" +                           // pre-setjmp init: must run once
            "  int v = setjmp(jb);\n" +
            "  if (v == 0) { slot = (void*)0x55; longjmp(jb, 1); }\n" +
            "  return slot == (void*)0x55 ? 1 : 0;\n" + // fixed: 1 ; bug: 0
            "}\n",
        }, out string o);
        Assert.True(exit == 1, $"expected 1 (slot preserved across resume), got {exit}. {o}");
    }
```

- [ ] **Step 2: Run the tests to verify they fail (RED)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp_does_not_rerun_code_before_the_setjmp_call_on_resume|FullyQualifiedName~Setjmp_preserves_value_written_before_longjmp_on_resume"
```
Expected: BOTH FAIL — the first with `expected 1 ..., got 2`, the second with `expected 1 ..., got 0`. (If `DotnetHostRunner.DotnetAvailable()` is false they'd pass vacuously; the dotnet host is present in this environment, so they must run and fail.)

- [ ] **Step 3: Commit the red tests**

```
git add tests/Chibil.Tests/CoreClr/MuslLinkTests.cs
git commit -m "Add red tests: setjmp must not re-run pre-setjmp code on resume

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Move the resume point to the `setjmp` call site

**Files:**
- Modify: `chibil/CodeGen.cs` — field block (~137-147), per-function reset (~1535), `CountSetjmp` helper (new, near `NodeContainsSetjmp` ~1758), `EmitSetjmpWrappedBody` (1787-1826), setjmp `GenExpr` lowering (2772-2782), `Return` lowering (3414-3422)

Background:
- Loop `NodeKind`s are `NodeKind.For` (also represents `while`) and `NodeKind.Do`.
- `Node` has the child fields `Lhs, Rhs, Cond, Then, Els, Init, Inc, Body, Args` (as used by `NodeContainsSetjmp`).
- The handler (`EmitSetjmpHandler`) already does `leave _setjmpLhead`; this plan only changes *where* `_setjmpLhead`/`tryStart` are marked.

- [ ] **Step 1: Add the two state fields**

In the setjmp field block (after `private List<(Node Jb, int Sjval)> _setjmpSites;`, ~line 147), add:

```csharp
    private bool _setjmpTryOpen;            // true once the try region has started (governs Return)
    private bool _setjmpDeferStart;         // resume-at-site mode: single setjmp, not in a loop
    private LabelHandle _setjmpTryStartLabel;
```

- [ ] **Step 2: Reset the new flags per function**

In the per-function reset block (the run of `_setjmp* = ...` near line 1535), after `_setjmpSites = null;` add:

```csharp
        _setjmpTryOpen = false;
        _setjmpDeferStart = false;
```

- [ ] **Step 3: Add the `CountSetjmp` analysis helper**

Immediately after `NodeContainsSetjmp` (after its closing `}`, ~line 1758) add:

```csharp
    /// <summary>Count setjmp call sites in a tree and whether any is lexically inside
    /// a loop (for/while/do). Gates the resume-at-site lowering: only a single setjmp
    /// not under a loop can safely place the try-region boundary at the call (a loop
    /// back-edge crossing that boundary would need an illegal plain branch out of the
    /// try).</summary>
    private static (int count, bool insideLoop) CountSetjmp(Node n, bool inLoop = false)
    {
        int count = 0;
        bool loopHit = false;
        for (; n != null; n = n.Next)
        {
            if (n.Kind == NodeKind.FunCall && n.Lhs != null && n.Lhs.Kind == NodeKind.Var
                && n.Lhs.Var != null && n.Lhs.Var.IsFunction && IsSetjmpName(n.Lhs.Var.Name))
            {
                count++;
                if (inLoop) loopHit = true;
            }
            bool childInLoop = inLoop || n.Kind == NodeKind.For || n.Kind == NodeKind.Do;
            foreach (var c in new[] { n.Lhs, n.Rhs, n.Cond, n.Then, n.Els, n.Init, n.Inc, n.Body, n.Args })
            {
                var (cc, cl) = CountSetjmp(c, childInLoop);
                count += cc;
                loopHit |= cl;
            }
        }
        return (count, loopHit);
    }
```

- [ ] **Step 4: Defer the region start in `EmitSetjmpWrappedBody`**

Replace the body of `EmitSetjmpWrappedBody` (lines 1787-1826) with:

```csharp
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
        var (sjCount, sjInLoop) = CountSetjmp(fn.Body);
        _setjmpDeferStart = sjCount == 1 && !sjInLoop;

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
```

- [ ] **Step 5: Mark the region at the `setjmp` site in the `GenExpr` lowering**

In the setjmp lowering (the `if (IsSetjmpName(fn))` block, lines 2772-2782), replace:

```csharp
                int sjval = AddFreshScratchLocal(_types.TyInt);
                _setjmpSites.Add((node.Args, sjval));
                _enc.LoadLocal(sjval); Push();
                return;
```

with:

```csharp
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
```

- [ ] **Step 6: Gate the `Return` funneling on `_setjmpTryOpen`**

In the `NodeKind.Return` case (lines 3414-3422), change the condition from `if (_setjmpWrap)` to `if (_setjmpWrap && _setjmpTryOpen)`:

```csharp
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
```

- [ ] **Step 7: Build chibil**

Run:
```
dotnet build chibil -c Debug
```
Expected: `0 Error(s)`.

- [ ] **Step 8: Run the Task 1 tests — now GREEN**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp_does_not_rerun_code_before_the_setjmp_call_on_resume|FullyQualifiedName~Setjmp_preserves_value_written_before_longjmp_on_resume"
```
Expected: BOTH PASS (exit 1 each).

- [ ] **Step 9: Run the existing setjmp value-path test — still GREEN**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp_longjmp_resumes_across_frames"
```
Expected: PASS (exit 42) — confirms the resume *value* path is unchanged.

- [ ] **Step 10: Commit the fix**

```
git add chibil/CodeGen.cs
git commit -m "Resume longjmp at the setjmp call, not from the function top

chibil resumed a longjmp by re-running the whole function body, re-executing
side effects sequenced before the setjmp call (MicroPython's nlr.ret_val = NULL),
which clobbered the longjmp'd exception object. For a single setjmp not inside a
loop, place the try-region start at the setjmp call site so prior code runs once
and resume re-enters at the call. Multi-setjmp / setjmp-in-loop keep the prior
re-from-top behavior.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Regression — bash suite + full CoreClr, then MicroPython re-verify

**Files:**
- Modify: `CompileMicroPython.md` (add the next-blocker resolution / new state)

The bash port relies on this codepath; the full CoreClr suite is the regression guard. The ~120 `cl.exe`/`link.exe` tests need an MSVC dev shell (environmental, not regressions — confirm no NEW failures vs the known baseline: the single pre-existing `Data_MsvcDefine_ChibiConsume` environmental failure).

- [ ] **Step 1: Run the CoreClr suite (no MSVC needed)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Chibil.Tests.CoreClr"
```
Expected: all pass (the prior 131 + the 2 new = 133 passing, 1 skipped). In particular the bash/setjmp/MuslLink tests pass.

- [ ] **Step 2: Run the full suite in an MSVC dev shell**

Create `D:\sandbox\chibil\runtests.bat` (untracked — delete after) with:
```
@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
dotnet test D:\sandbox\chibil\tests\Chibil.Tests
```
Run it via PowerShell: `& cmd.exe /c "D:\sandbox\chibil\runtests.bat"`.
Expected: only the known pre-existing `Data_MsvcDefine_ChibiConsume` environmental failure (links via MSVC `link.exe`, not chibil-link) — no NEW failures. Then delete `runtests.bat`.

- [ ] **Step 3: Rebuild + relink MicroPython with the fixed chibil**

Build chibil-link is unaffected, but chibil changed — the harness invokes `dotnet .../chibil/bin/Debug/net10.0/chibil.dll`, already rebuilt by Step 1. Run the harness (under WSL via PowerShell to avoid Git-Bash path mangling):
```
wsl bash /mnt/d/sandbox/chibil/targets/build/micropython-chibil.sh
```
Expected: `compiled OK: 138 FAILED: 0`, `link exit: 0`, `micropython.dll` (~3.4 MB).

- [ ] **Step 4: Verify the REPL now EVALUATES instead of faulting**

Run:
```
wsl bash -c "cd /mnt/d/sandbox/chibil/targets/micropython/ports/minimal && printf 'print(2+3)\n' | timeout 30 dotnet micropython.dll 2>&1 | head -20"
```
Expected: the banner, `>>> print(2+3)`, then **`5`** printed — NOT a `NullReferenceException` in `parse_compile_execute`. (It may surface a *different* later blocker; reaching/printing `5` is success for THIS fix.)

If it still faults at `parse_compile_execute` with `nlr.ret_val` NULL, STOP: re-check that `parse_compile_execute` qualified for the gate (single setjmp, not in a loop) — add a temporary diagnostic to confirm `_setjmpDeferStart` was true for it.

- [ ] **Step 5: Update `CompileMicroPython.md`**

Add a new subsection after §5d recording: the REPL boot fix (§5d) exposed a second runtime blocker — chibil's `setjmp`/`longjmp` resumed from the function top, re-running `nlr.ret_val = NULL` and clobbering the exception (NullReference in `parse_compile_execute`); fixed by resuming at the `setjmp` call site (single-setjmp, non-loop). State the observed result from Step 4 (REPL evaluates `print(2+3)` → `5`) and note the next blocker if one appeared. Use the same heading style as §5d (`## 5e. Runtime blocker — FIXED (setjmp resume point)`).

- [ ] **Step 6: Commit the doc update**

```
git add CompileMicroPython.md
git commit -m "MicroPython: REPL evaluates after setjmp resume-point fix

Records the second runtime blocker (setjmp resumed from the function top,
clobbering nlr.ret_val) and its fix; the REPL now evaluates expressions.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the executor

- Do NOT commit anything under `targets/` (third-party / gitignored).
- Commit messages end with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.
- This work is on branch `chibil-setjmp-resume-point` (already created).
- The `cl.exe`/`link.exe` failures in a plain shell are environmental — run the full suite inside an MSVC dev shell.
- WSL invocations must go through PowerShell (`wsl bash ...`), not Git-Bash, which mangles `/mnt/...` paths.
