# Setjmp-in-Loop Resume Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a single-`setjmp` function whose `setjmp` is inside a loop resume *at* the `setjmp` call (not re-run from the function top), so a `longjmp` resume does not re-execute `nlr_push_tail` and corrupt the nlr chain — fixing MicroPython's `exit()` crash.

**Architecture:** Drop the `!insideLoop` restriction on resume-at-site (gate on `sjCount == 1`). With `tryStart` at the setjmp call, the loop header is outside the try and its back-edge is inside; convert any branch whose target was marked before `tryStart` into a `leave` via a trampoline (handles conditional back-edges uniformly). All in `chibil/CodeGen.cs`.

**Tech Stack:** C# / .NET, `System.Reflection.Metadata` IL emission, xUnit, `DotnetHostRunner`.

**Spec:** `docs/superpowers/specs/2026-06-03-chibil-setjmp-in-loop-resume-design.md`

---

### Task 1: Red test — nlr-chain re-raise through a setjmp-in-loop frame

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs` (add one `[Fact]` near the other setjmp tests, e.g. after `Setjmp_outer_catches_reraise_from_inner_that_popped`)

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public void Setjmp_in_loop_resume_does_not_corrupt_the_nlr_chain()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // Mirrors MicroPython's exit(): an nlr-style chain where the "VM" has its
        // setjmp INSIDE a for(;;) loop. It catches a longjmp, returns "exception"
        // (MP_VM_RETURN_EXCEPTION), and the caller re-raises through the chain to an
        // outer frame. With the buggy re-from-top resume the VM re-runs `push(&n)`,
        // re-pushing its own nlr, so the re-raise targets the returned-from VM frame
        // -> uncaught crash. Resume-at-site must not re-run `push`, so `top` stays
        // correct and the re-raise reaches main.
        int exit = LinkRun(new[]
        {
            "typedef long jmp_buf[16];\n" +
            "extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);\n" +
            "typedef struct nlr { struct nlr *prev; jmp_buf jb; } nlr_t;\n" +
            "static nlr_t *top;\n" +
            "static void push(nlr_t *n){ n->prev = top; top = n; }\n" +
            "static void jump(void){ nlr_t *t = top; top = t->prev; longjmp(t->jb, 1); }\n" +
            "static int vm(void){\n" +
            "  for (;;) {\n" +
            "    nlr_t n;\n" +
            "    if ((push(&n), setjmp(n.jb)) == 0) { jump(); return 0; }\n" +
            "    else { return 7; }\n" +
            "  }\n" +
            "}\n" +
            "static void caller(void){ if (vm() == 7) { jump(); } }\n" +
            "int main(void){\n" +
            "  nlr_t n;\n" +
            "  if ((push(&n), setjmp(n.jb)) == 0) { caller(); return 99; }\n" +
            "  else { return 42; }\n" +
            "}\n",
        }, out string o);
        Assert.True(exit == 42, $"expected 42 (re-raise reached main), got {exit}. {o}");
    }
```

- [ ] **Step 2: Run the test to verify it fails (RED)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp_in_loop_resume_does_not_corrupt_the_nlr_chain"
```
Expected: FAIL — `exit` is not 42 (the process crashes / aborts on the uncaught longjmp, so the exit code is some non-42 value).

- [ ] **Step 3: Commit the red test**

```
git add tests/Chibil.Tests/CoreClr/MuslLinkTests.cs
git commit -m "Add red test: setjmp-in-loop resume must not corrupt the nlr chain

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Resume-at-site for setjmp-in-loop via leave-trampolines

**Files:**
- Modify: `chibil/CodeGen.cs` — field block (~150), `EmitSetjmpWrappedBody` (gate ~1839; trampoline emit ~1855), new `SjBranch` helper, `For` (3349-3374), `Do` (3379-3392), `Goto` (3459-3466), `Label` (3472-3480)

Background:
- `break`/`continue` are lowered as `NodeKind.Goto` (their targets are `ContLabel`/`BrkLabel` mapped into `_labels`), so routing `Goto` through `SjBranch` covers them.
- `_setjmpTryOpen` becomes true when the setjmp lowering marks `tryStart` at the call site (CodeGen.cs ~2830-2838). A statement label marked while `_setjmpDeferStart && !_setjmpTryOpen` is therefore *before* the setjmp.
- The setjmp field block currently ends at `private LabelHandle _setjmpTryStartLabel;` (~line 150).

- [ ] **Step 1: Add the two collection fields**

After `private LabelHandle _setjmpTryStartLabel;` (~line 150), add:

```csharp
    // Statement labels marked BEFORE the setjmp site (loop headers, user labels). A
    // branch from inside the resume-at-site try to one of these exits the protected
    // region and must be a `leave`, not a `br`.
    private readonly HashSet<LabelHandle> _setjmpOuterLabels = new();
    // (trampoline, target) pairs: a redirected cross-out branch jumps to `trampoline`,
    // emitted inside the try as `trampoline: leave target`.
    private readonly List<(LabelHandle tramp, LabelHandle target)> _setjmpLeaveTrampolines = new();
```

- [ ] **Step 2: Change the gate and clear the collections**

In `EmitSetjmpWrappedBody`, replace:

```csharp
        var (sjCount, sjInLoop) = CountSetjmp(fn.Body);
        _setjmpDeferStart = sjCount == 1 && !sjInLoop;
```

with:

```csharp
        var (sjCount, _) = CountSetjmp(fn.Body);
        _setjmpDeferStart = sjCount == 1;   // resume-at-site, in or out of a loop
        _setjmpOuterLabels.Clear();
        _setjmpLeaveTrampolines.Clear();
```

- [ ] **Step 3: Add the `SjBranch` helper**

Add this method next to `EmitSetjmpWrappedBody` (e.g. just before it):

```csharp
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
```

- [ ] **Step 4: Emit the trampolines in `EmitSetjmpWrappedBody`**

In `EmitSetjmpWrappedBody`, find:

```csharp
        // In defer mode, the (single) setjmp lowering marks Lhead/tryStart at its site.
        GenStmt(fn.Body);
        // Normal fall-through end of the try -> leave to the epilogue.
        _enc.Branch(ILOpCode.Leave, _setjmpEpiLabel);
        _enc.MarkLabel(tryEnd);
```

and insert the trampoline loop between the end-of-body `Leave` and `MarkLabel(tryEnd)`:

```csharp
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
```

- [ ] **Step 5: Record outer labels at the `For` loop header and route its back-edge**

In `case NodeKind.For:` (3349-3374): after `_enc.MarkLabel(beginLabel);` (3356) record the header, and route the back-edge through `SjBranch`. Change:

```csharp
                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
```

to:

```csharp
                if (node.Init != null) GenStmt(node.Init);
                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
```

and change the back-edge:

```csharp
                _enc.Branch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
```

to:

```csharp
                SjBranch(ILOpCode.Br, beginLabel);
                _enc.MarkLabel(brkLabel);
```

- [ ] **Step 6: Record outer labels at the `Do` loop header and route its back-edge**

In `case NodeKind.Do:` (3379-3392). Change:

```csharp
                _enc.MarkLabel(beginLabel);
                GenStmt(node.Then);
```

to:

```csharp
                _enc.MarkLabel(beginLabel);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(beginLabel);
                GenStmt(node.Then);
```

and change the conditional back-edge:

```csharp
                _enc.Branch(ILOpCode.Brtrue, beginLabel); Pop();
```

to:

```csharp
                SjBranch(ILOpCode.Brtrue, beginLabel); Pop();
```

- [ ] **Step 7: Record user labels and route `Goto`**

In `case NodeKind.Label:` (3472-3480), after `_enc.MarkLabel(labelTarget);` (3478) record it:

```csharp
                _enc.MarkLabel(labelTarget);
                if (_setjmpDeferStart && !_setjmpTryOpen) _setjmpOuterLabels.Add(labelTarget);
                GenStmt(node.Lhs);
                return;
```

In `case NodeKind.Goto:` (3459-3466), route the branch through `SjBranch`:

```csharp
            case NodeKind.Goto:
                if (!_labels.TryGetValue(node.UniqueLabel, out var gotoTarget))
                {
                    gotoTarget = _enc.DefineLabel();
                    _labels[node.UniqueLabel] = gotoTarget;
                }
                SjBranch(ILOpCode.Br, gotoTarget);
                return;
```

- [ ] **Step 8: Build chibil**

Run:
```
dotnet build chibil -c Debug
```
Expected: `0 Error(s)`.

- [ ] **Step 9: Run the Task 1 test — now GREEN**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp_in_loop_resume_does_not_corrupt_the_nlr_chain"
```
Expected: PASS (exit 42).

- [ ] **Step 10: Run all setjmp tests — still GREEN**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Setjmp|FullyQualifiedName~Longjmp"
```
Expected: all pass (the existing cross-frame/local-buffer/nested-reraise/through-loop tests, the resume-point tests, and the new one).

- [ ] **Step 11: Commit the fix**

```
git add chibil/CodeGen.cs
git commit -m "Resume-at-site for setjmp-in-loop functions (fix nlr-chain corruption)

A single setjmp inside a loop was gated to the re-from-top resume strategy, which
re-runs nlr_push_tail on a longjmp resume and re-pushes the nlr -> the chain breaks
and a re-raise targets the returned-from frame (MicroPython exit() crashed uncaught).
Drop the !insideLoop gate so such functions resume AT the setjmp call; convert any
branch exiting the try to a label marked before tryStart (loop back-edges, gotos to a
pre-setjmp label) into a leave via a trampoline.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Regression (bash + full CoreClr) and MicroPython integration

**Files:**
- Modify: `CompileMicroPython.md` (§5g → FIXED)

- [ ] **Step 1: Run the CoreClr suite (no MSVC needed)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Chibil.Tests.CoreClr"
```
Expected: all pass (the prior 137 + the 1 new = 138 passing, 1 skipped).

- [ ] **Step 2: Run the full suite in an MSVC dev shell (bash regression guard)**

Create `D:\sandbox\chibil\runtests.bat` (untracked — delete after):
```
@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
dotnet test D:\sandbox\chibil\tests\Chibil.Tests
```
Run via PowerShell: `& cmd.exe /c "D:\sandbox\chibil\runtests.bat"`.
Expected: only the known pre-existing `Data_MsvcDefine_ChibiConsume` environmental failure — no NEW failures (the bash setjmp tests must stay green). Then delete `runtests.bat`.

- [ ] **Step 3: Rebuild MicroPython with the fixed chibil**

Run (under WSL via PowerShell):
```
wsl bash /mnt/d/sandbox/chibil/targets/build/micropython-chibil.sh
```
Expected: `compiled OK: 138 FAILED: 0`, `link exit: 0`, `micropython.dll` (~3.4 MB).

- [ ] **Step 4: Verify exit() exits cleanly and eval still works**

Run:
```
wsl bash -c "cd /mnt/d/sandbox/chibil/targets/micropython/ports/minimal && printf 'print(2+3)\nexit()\n' | timeout 30 dotnet micropython.dll 2>&1 | grep -vE '^GC:|blocks:' | head -10"
```
Expected: `>>> print(2+3)` → `5`, then `>>> exit()` ends the process **without** an `Unhandled exception … __chibil_longjmp` / core dump. (A clean exit, or at most a benign EOF/return — but NOT the uncaught-longjmp crash.)

If it still crashes uncaught in `__chibil_longjmp`, STOP: confirm `vm()`/`mp_execute_bytecode` qualified for the gate (single setjmp) — if `mp_execute_bytecode` has more than one `setjmp`/`nlr_push`, it stays multi-setjmp re-from-top and needs separate handling; add a temporary diagnostic to confirm `_setjmpDeferStart` was true for it.

- [ ] **Step 5: Update `CompileMicroPython.md` §5g to FIXED**

Change the §5g heading from `## 5g. Next blocker (open) — setjmp-in-loop resume corrupts the nlr chain` to `## 5g. Runtime blocker — FIXED (setjmp-in-loop resume)`. Replace the final paragraph (currently "...That is a focused but non-trivial codegen change, deferred.") with a statement that the fix landed: resume-at-site now applies to single-`setjmp`-in-loop functions (cross-out branches converted to `leave` via trampolines), so the VM no longer re-pushes its nlr and `exit()` exits cleanly. Reference `chibil/CodeGen.cs` (`SjBranch`, `_setjmpOuterLabels`) and the test `Setjmp_in_loop_resume_does_not_corrupt_the_nlr_chain`.

- [ ] **Step 6: Commit the doc update**

```
git add CompileMicroPython.md
git commit -m "MicroPython: exit() exits cleanly after setjmp-in-loop resume fix

The VM's in-loop setjmp now resumes at the call site, so it no longer re-pushes its
nlr on a longjmp resume; exit() no longer crashes uncaught.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the executor

- Do NOT commit anything under `targets/` (third-party / gitignored).
- Commit messages end with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.
- This work continues on branch `chibil-wide-bitfield-store` (which carries the bitfield fix needed for the MicroPython `exit()` integration check).
- The `cl.exe`/`link.exe` failures in a plain shell are environmental — run the full suite inside an MSVC dev shell.
- WSL invocations go through PowerShell (`wsl bash ...`), not Git-Bash (which mangles `/mnt/...` paths). Use `targets/build/micropython-chibil-inc.sh <file.c>` for fast incremental recompiles during iteration.
