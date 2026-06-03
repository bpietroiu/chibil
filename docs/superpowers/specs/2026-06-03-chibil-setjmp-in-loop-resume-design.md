# chibil: resume-at-site for `setjmp`-in-loop functions

**Status:** design approved (pending spec review)
**Date:** 2026-06-03
**Component:** `chibil/CodeGen.cs` (`EmitSetjmpWrappedBody`, setjmp `GenExpr` lowering, `For`/`Do`/`Goto`/`Switch` branch emission)

## Problem

MicroPython's `exit()` crashes with an **uncaught** `__chibil_longjmp` that propagates
past `parse_compile_execute` to `Main`. Tracing `nlr_jump` shows the longjmp firing
**twice against the same nlr buffer** before going uncaught.

Root cause: the VM's `mp_execute_bytecode` does `nlr_push(&nlr)` **inside** its
`for(;;)` dispatch loop (`py/vm.c:298-301`). chibil's `setjmp` resume-point fix (the
prior change) only applies resume-at-site to **single-setjmp, non-loop** functions;
`setjmp`-in-loop is gated out and uses the **re-from-top** strategy. On a longjmp the
VM re-runs its body from the top, re-executing `nlr_push_tail` and **re-pushing its
own nlr onto `nlr_top`**. So when the VM catches an exception, finds no handler, and
returns, the caller's re-raise reads the stale `nlr_top` (the VM's re-pushed buffer)
instead of the outer frame's — the longjmp loops back to the now-returned VM frame
and is uncaught.

This is not a regression: forcing re-from-top everywhere reproduces the identical
double-jump. It is the pre-existing `setjmp`-in-loop limitation, now reachable because
the parser works.

`nlr_push` expands to `(nlr_push_tail(&nlr), setjmp(&nlr.jmpbuf))`. The fix is to
resume **at the setjmp call** (after `nlr_push_tail`), so a longjmp resume does not
re-push the nlr. The obstacle is the loop back-edge: with `tryStart` at the setjmp
(inside the loop), the loop header is *before* `tryStart` (outside the try), so the
back-edge `br header` is an illegal branch out of a protected region — it must be a
`leave`.

## Fix

### Gate

Change `_setjmpDeferStart = (sjCount == 1 && !sjInLoop)` to **`_setjmpDeferStart =
(sjCount == 1)`**. Single-setjmp functions use resume-at-site whether or not the
setjmp is in a loop. Multi-setjmp (`sjCount > 1`) stays re-from-top (ambiguous resume
target among multiple sites). `CountSetjmp` still returns `insideLoop`; it is no longer
used for gating (keep it returning the tuple; the field is simply ignored, or simplify
to return only the count — implementer's choice, but keep `CountSetjmp` callable).

### Track outer labels

Add `private readonly HashSet<LabelHandle> _setjmpOuterLabels = new();` (cleared per
function alongside the other `_setjmp*` resets). When a **statement** label is marked
while `_setjmpDeferStart && !_setjmpTryOpen` (before the setjmp site), add it to the
set. The label sites that can be cross-out targets and must record:
- `For` `beginLabel` (the loop top).
- `Do` `beginLabel`.
- user `Label` (`NodeKind.Label`) — e.g. the VM's `outer_dispatch_loop`.

(Loop `contLabel`/`brkLabel` for the setjmp-containing loop are marked *after* the body
— after `tryStart` — so they are inside the try and never cross-out. Expression-level
labels (`?:`, `&&`, `||`) are transient and never branched-to across the try.)

### Redirect cross-out branches through leave-trampolines

Add a helper:

```csharp
private readonly List<(LabelHandle tramp, LabelHandle target)> _setjmpLeaveTrampolines = new();

/// Branch to `target`, converting a branch that exits the setjmp try region (target
/// marked before tryStart) into a jump to a trampoline that `leave`s — a plain `br`
/// out of a protected region is illegal IL. Conditional branches are handled
/// uniformly: the conditional branch goes to the trampoline, which does the
/// unconditional leave.
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

Route the loop/goto branch sites through `SjBranch` instead of `_enc.Branch`:
- `For` back-edge (`Br beginLabel`).
- `Do` back-edge (`Brtrue beginLabel`).
- `Goto` (`Br target`).
- `break`/`continue` (gotos to `brkLabel`/`contLabel`) and `Switch` (`Br` to
  default/break) — route them too for completeness; they only divert when their target
  is an outer label, which is the rare cross-out case.

`Pop()` bookkeeping for conditional branches is unchanged (the caller pops the
condition as today; `SjBranch` only chooses the branch target/opcode).

### Emit trampolines

In `EmitSetjmpWrappedBody`, after `GenStmt(fn.Body)` and the normal
`_enc.Branch(Leave, _setjmpEpiLabel)` end-of-body, before `MarkLabel(tryEnd)`:

```csharp
foreach (var (tramp, target) in _setjmpLeaveTrampolines)
{
    _enc.MarkLabel(tramp);
    _enc.Branch(ILOpCode.Leave, target);
}
```

The trampolines sit inside `[tryStart, tryEnd)`, reachable only via redirected
branches; each ends in an unconditional `leave` (no fall-through). `leave target`
exits the try to the outer label, which then falls through to re-arm `nlr_push_tail`
+ `setjmp` (a fresh loop iteration).

### Re-entry semantics (why it is correct)

- **Loop back-edge** → `leave header`: re-runs `nlr_push_tail` + `setjmp` → arms a
  fresh iteration. Correct.
- **Longjmp handler** → `leave Lhead` (the setjmp site): `setjmp` returns the longjmp
  value → else branch, **without** re-running `nlr_push_tail`. The nlr chain stays
  intact, so the re-raise reaches the outer frame.

Non-loop single-setjmp functions: no statement label precedes `tryStart` that is
branched-to from inside the try, so `_setjmpLeaveTrampolines` is empty and codegen is
byte-for-byte identical to the prior resume-at-site behavior.

## Test (runtime, `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs`)

Mirror the exact `exit()` flow: an nlr-style chain where the VM (`setjmp` in a loop)
catches a longjmp, returns "exception", and a caller re-raises through the chain to an
outer frame.

```c
typedef long jmp_buf[16];
extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);
typedef struct nlr { struct nlr *prev; jmp_buf jb; } nlr_t;
static nlr_t *top;
static void push(nlr_t *n){ n->prev = top; top = n; }
static void jump(void){ nlr_t *t = top; top = t->prev; longjmp(t->jb, 1); }
static int vm(void){
    for (;;) {
        nlr_t n;
        if ((push(&n), setjmp(n.jb)) == 0) { jump(); return 0; }
        else { return 7; }
    }
}
static void caller(void){ if (vm() == 7) { jump(); } }
int main(void){
    nlr_t n;
    if ((push(&n), setjmp(n.jb)) == 0) { caller(); return 99; }
    else { return 42; }
}
```

`Assert.True(exit == 42, …)`.
- **Before fix** (`vm` re-from-top): resume re-runs `push(&n)`, re-pushing the VM's
  nlr; `caller`'s `jump()` targets the returned-from `vm` frame → uncaught → exit ≠ 42.
- **After fix** (`vm` resume-at-site): `push` not re-run, `top` correct, re-raise
  reaches `main` → exit 42.

Regression guard:
- Existing setjmp tests (`Setjmp_longjmp_resumes_across_frames`,
  `Setjmp_does_not_rerun_code_before_the_setjmp_call_on_resume`,
  `Setjmp_preserves_value_written_before_longjmp_on_resume`,
  `Longjmp_passes_through_loop_setjmp_frame_to_outer`,
  `Setjmp_with_local_buffer_catches_cross_frame_longjmp`,
  `Setjmp_outer_catches_reraise_from_inner_that_popped`) stay green.
- Full bash CoreClr suite stays green (run inside an MSVC dev shell; the lone
  pre-existing `Data_MsvcDefine_ChibiConsume` environmental failure is expected).
- MicroPython integration: `exit()` exits cleanly (no uncaught `__chibil_longjmp`);
  `print(2+3)` still evaluates to `5`.

## Risk / blast radius

- Multi-setjmp functions: unchanged (still re-from-top).
- Non-loop single-setjmp functions: unchanged (no trampolines emitted).
- Single-setjmp-in-loop functions (newly resume-at-site): the loop back-edge and any
  goto to a pre-setjmp label become `leave` via trampolines. The bash suite + the new
  test guard this; the trampoline `leave`s are standard structured-exception exits.

## Out of scope

- Multi-setjmp correct resume (ambiguous target; rare).
- The `mp_iternext` `InvalidProgramException` (separate codegen issue).
