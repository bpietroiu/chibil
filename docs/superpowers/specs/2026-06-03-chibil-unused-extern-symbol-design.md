# chibil: don't emit symbols for unused `extern` globals — design

**Status:** approved to implement (TDD).
**Author:** brainstormed 2026-06-03.

## Problem

Linking the MicroPython minimal port fails with:

```
chibil-link: field 'mp_const_notimplemented_obj' RVA data references a non-TypeDef value type.
```

Minimal reproduction (4 lines, no MicroPython):

```c
struct S;                  /* incomplete, never completed */
extern const struct S g;   /* declared, NEVER used */
int main(void) { return 0; }
```
`chibil -c` then `chibil-link -lc` → the same error.

## Root cause

`mp_const_notimplemented_obj`'s **definition** and its only **use** are both behind
`#if MICROPY_PY_BUILTINS_NOTIMPLEMENTED`, which is
`(MICROPY_CONFIG_ROM_LEVEL >= EXTRA_FEATURES) = (10 >= 30) = 0` in the minimal port.
chibil evaluates that correctly and excludes both. What remains in the referencing
TUs is only an **unused `extern` declaration** of an incomplete-struct-typed global.

A real linker (`gcc`/`ld`) emits **no symbol** for an unused `extern` declaration —
it's a pure declaration with no storage and no reference. chibil instead emits a
`Field` for **every** declared global (`CodeGen.RegisterGlobalFields`, "Pass B:
externs"), used or not. chibil-link then sweeps every unmapped field into
`SynthesizeDataImports` as a native data import (bound to a `-l` library); sizing
the unused extern's incomplete-struct type (a forward-declared `TypeRef` with no
layout) throws.

So this is a **general chibil bug**, independent of MicroPython: an unused `extern`
global becomes a phantom undefined symbol.

## Fix

**Match `ld` semantics: chibil must not emit a `Field`/symbol for an `extern`
global that is never referenced by emitted code.**

chibil already pre-walks function bodies (e.g. `PreAllocateStructTypeDefs`). The fix
collects the set of globals actually referenced by emitted code (value loaded or
address taken) and, in `RegisterGlobalFields` Pass B, emits an extern global's
`Field` only when it is in that set. Definitions (Pass A) are unaffected — a defined
global always emits its field+`FieldRVA`. An extern that *is* used still emits its
field and resolves normally.

Edge cases to preserve:
- An extern that is referenced → still emitted (unchanged behavior, regression-tested).
- A defined global that is unused → still emitted (definitions are always emitted;
  C allows taking their address from other TUs).
- Function externs are out of scope (this is data-global Pass B only).

### Why not "fix it in chibil-link"

A chibil-link-side fix (skip unreferenced extern fields in `SynthesizeDataImports`)
would work but leaves chibil emitting phantom symbols that other consumers (ilasm,
verifiers, future tooling) still see. The object-level fix is the real-linker-correct
behavior and shrinks every object's metadata. Kept as a possible belt-and-suspenders
only if the chibil change hits an awkward edge.

## Test plan (TDD)

1. **RED — unit (emission/link level):** the 4-line reproduction above, linked with
   `-lc`, currently errors. After the fix it links clean. Add as a chibil-link/CoreClr
   test that compiles the snippet and asserts the link succeeds (and, more precisely,
   that the object emits **no** `Field` named `g`).
2. **Regression — used extern still works:** the same snippet but with `g` referenced
   (`return &g != 0;`) must still compile, emit the field, and resolve (links with a
   definition in a second TU).
3. **GREEN — integration:** the full MicroPython minimal-port link advances past this
   error (documented in `CompileMicroPython.md`).
4. Full CoreClr suite stays green (no regression from dropping unused-extern symbols —
   bash and existing tests must be unaffected).

## Risk

Low–medium. The change is additive gating in one emission pass. The only risk is
mis-classifying a *used* extern as unused (would break a real reference) — covered by
regression test #2 and the full suite. The usage scan must count all reference forms
(load, store, address-of, initializer relocations referencing the global).
