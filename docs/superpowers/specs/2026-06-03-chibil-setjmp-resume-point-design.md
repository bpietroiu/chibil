# chibil: resume `longjmp` after the `setjmp` call, not from the function top

**Status:** design approved (pending spec review)
**Date:** 2026-06-03
**Component:** `chibil/CodeGen.cs` (`EmitSetjmpWrappedBody`, setjmp `GenExpr` lowering, `Return` lowering)

## Problem

chibil lowers `setjmp`/`longjmp` to managed-exception resumption: a function
containing `setjmp` is wrapped in `Lhead: .try { body } filter { ours? } handler
{ resume }`. A `longjmp` throws; the filter matches the target buffer; the handler
stores the longjmp value into the matching `setjmp`'s result local and does
`leave _setjmpLhead`. **`_setjmpLhead` sits at the top of the function body**
(`EmitSetjmpWrappedBody`), so resume **re-executes the entire body from the top** —
including any side effects sequenced *before* the `setjmp` call.

This breaks MicroPython's core nlr idiom (`shared/runtime/pyexec.c:73`):

```c
nlr_buf_t nlr;
nlr.ret_val = NULL;          // re-runs on resume → wipes the exception
if (nlr_push(&nlr) == 0) { ... }   // nlr_push = (nlr_push_tail(&nlr), setjmp(&nlr.jmpbuf))
else { /* dereferences nlr.ret_val */ }
```

`nlr_jump` stores the exception object into `nlr.ret_val` before throwing; chibil's
resume re-runs `nlr.ret_val = NULL`, clobbering it back to NULL. Confirmed by spike:
`SPIKE nlr.ret_val=0` at the `else` branch → `((mp_obj_base_t *)nlr.ret_val)->type`
dereferences NULL → `NullReferenceException` in `parse_compile_execute`
(`MICROPY_PYEXEC_ENABLE_VM_ABORT = 0`, so the `nlr.ret_val == NULL` guard above is
compiled out). It also re-runs `nlr_push_tail`, double-pushing the nlr chain.

Real `setjmp` semantics resume *after* the `setjmp` call and never re-run prior code.

## Fix: move the resume point to the `setjmp` call site

Emit code sequenced before `setjmp` **outside** the try (runs once); have the
handler re-enter the try **at the `setjmp` call**. Legal because at the `setjmp`
lowering point the eval stack is empty in every real idiom — `(nlr_push_tail(buf),
setjmp(...))` (comma discards the left result), `if (setjmp(...) == 0)`,
`v = setjmp(...)` — so a try-region boundary can be placed there.

Alternatives rejected: a hidden "resumed" flag can't identify which body code is the
pre-`setjmp` prologue; snapshotting the buffer's owning object needs chibil to
understand nlr semantics it cannot.

### Gating

Only **single-`setjmp`** functions whose `setjmp` is **not lexically inside a
`for`/`while`/`do`** get the new resume point. Add `CountSetjmp(fn.Body)` (mirrors
`NodeContainsSetjmp`) returning `(int count, bool insideLoop)` from one AST walk;
the gate is `count == 1 && !insideLoop`. Otherwise keep today's re-from-top
behavior exactly.

Rationale:
- **Multi-`setjmp`:** the resume target among several sites is ambiguous; re-from-top
  is the existing (no-worse) behavior.
- **`setjmp` in a loop:** a loop back-edge crossing `tryStart` would require a
  `leave` (illegal as a plain branch) → malformed protected region. Fall back.
- MicroPython's `nlr_push`, bash's `test`/`[`, and the existing cross-frame test all
  pass the gate.

### Emission changes (`EmitSetjmpWrappedBody`)

- Compute `bool deferStart = (count == 1 && !insideLoop)`.
- If `!deferStart` (unchanged path): mark `Lhead → nop → tryStart` before
  `GenStmt(fn.Body)`; set `_setjmpTryOpen = true`.
- If `deferStart`: do **not** mark them yet; set `_setjmpTryOpen = false`. During the
  single `setjmp` `GenExpr` lowering, just before loading the result local, assert
  `_stackDepth == 0`, then mark `Lhead → nop → tryStart` and set
  `_setjmpTryOpen = true`. Body code emitted earlier is outside the try.
- `tryEnd`, `EmitSetjmpFilter`, `EmitSetjmpHandler`,
  `AddFilterRegion(tryStart, tryEnd, …)`, and the epilogue stay after `GenStmt` as
  today. The handler still `leave`s to `Lhead` — now at the site.

### Return lowering

Add field `bool _setjmpTryOpen`. `Return` lowering:
`if (_setjmpWrap && _setjmpTryOpen) { funnel value into _setjmpRetvalLocal; leave
epilogue; }` else emit a normal `ret`. Non-gated/multi functions have the try open
from the top (all returns `leave`, unchanged). In the gated case, returns before the
site (rare) use normal `ret`; returns after `leave`.

### New state / reset

- `_setjmpTryOpen` added to the per-function field block and reset (to `false`)
  alongside `_setjmpWrap` etc. in the function-prologue reset.
- The `setjmp` `GenExpr` lowering needs the `Lhead`/`tryStart` `LabelHandle`s and the
  `deferStart`/already-marked state; thread via existing `_setjmp*` fields (add
  `_setjmpTryStartLabel`, and reuse `_setjmpLhead`).

## Test (runtime, `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs`)

Primary red/green (mirrors `Setjmp_longjmp_resumes_across_frames`, uses `LinkRun`):

```c
typedef long jmp_buf[16];
extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);
jmp_buf jb;
int count;
int main(void){
  count++;                 // pre-setjmp side effect — must run EXACTLY once
  int v = setjmp(jb);
  if (v == 0) longjmp(jb, 1);
  return count;            // fixed: 1 ; bug (re-runs count++): 2
}
```

`Assert.True(exit == 1, ...)`. Before fix → `2`; after → `1`.

nlr-mirror test (the exact MicroPython shape — a value written right before
`longjmp` survives resume):

```c
typedef long jmp_buf[16];
extern int setjmp(jmp_buf); extern void longjmp(jmp_buf, int);
jmp_buf jb;
void *slot;
int main(void){
  slot = 0;                          // pre-setjmp init — must run once
  int v = setjmp(jb);
  if (v == 0) { slot = (void*)0x55; longjmp(jb, 1); }
  return slot == (void*)0x55 ? 1 : 0; // fixed: 1 ; bug (slot re-zeroed): 0
}
```

`Assert.True(exit == 1, ...)`.

Regression guard:
- The existing `Setjmp_longjmp_resumes_across_frames` (exit 42) stays green — the
  resume *value* path is unchanged.
- The full bash CoreClr suite stays green (bash relies on this codepath; run inside
  an MSVC dev shell).
- MicroPython re-verification: the REPL evaluates `print(2+3)` (prints `5`) instead
  of faulting in `parse_compile_execute`.

## Risk / blast radius

- Non-gated functions (multi-`setjmp`, `setjmp`-in-loop) are byte-for-byte unchanged.
- Gated functions only differ in *where* the try begins and that pre-`setjmp` code is
  no longer in the try — which is the correct `setjmp` semantics.
- The `_stackDepth == 0` assert at the site fails loudly rather than emitting bad IL
  if an exotic expression form is ever hit.

## Out of scope

- Multi-`setjmp` correct resume (ambiguous target; rare).
- `setjmp` nested in a loop (back-edge vs region boundary; rare).
- The next MicroPython blocker after this (whatever surfaces once the REPL evaluates).
