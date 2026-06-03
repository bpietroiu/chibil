# chibil/chibil-link: static-data function pointers to undefined external functions

**Status:** design approved (pending spec review)
**Date:** 2026-06-03
**Components:** `chibil/CodeGen.cs` (`EmitGlobalDataRelocations`), `tools/chibil-link/SymbolResolver.cs`, `tools/chibil-link/MetadataMerger.cs`, `tools/chibil-link/FieldDataRelocator.cs`

## Problem

QuickJS's `js_math_funcs[]` is a static table holding libc function pointers
(`JS_CFUNC_SPECIAL_DEF("abs", 1, f_f, fabs)` etc.). chibil emits each `&fabs` as a
COFF `ADDR64` data relocation to the undefined external symbol `fabs`; chibil-link
then fails:

```
chibil-link: quickjs.obj: external data relocation target 'fabs' in .data+0x2580
is not defined in any object.
```

A spike confirmed the mechanism is otherwise sound: a **local** function pointer to a
libc function (`int (*fp)(int) = abs; fp(-7)`) links and runs (returns 7). chibil
emits `ldftn <abs P/Invoke stub>` and a `calli` (Default managed convention) through
it; the stub is a managed method, so the call reaches native `abs`. The only gap is
that a function pointer in **static data** emits a bare data symbol with no signature,
whereas the local case emits a `MemberRef` (with signature) that `SymbolResolver`
turns into the stub.

## Fix (two sides, connected by name)

### chibil — emit a `MemberRef` for address-taken external functions in static data

In `EmitGlobalDataRelocations`, the relocation loop currently classifies a target as
data (`_dataCoffSymbols`), a defined-function NEP symbol (`_nepBareNameSymbols`), or
falls through to an undefined external **data** symbol. Add: when the target name
resolves to a **function** `Obj` that is an undefined external (declared, not defined
here), also call `RegisterExternalFunction(fn)`. That emits a `<Module>` `MemberRef`
named `fn.Name` with the function's signature (from `fn.Ty`). The COFF data
relocation still references the symbol by name; the `MemberRef` exists only to convey
the signature to chibil-link.

- `RegisterExternalFunction` caches by name (`_externalFuncRefs`), so a function that
  is also **called** (its call site already created the `MemberRef`) is a no-op here —
  the existing reference is reused.
- The target is identified as a function by locating its `Obj` in the program list
  (`IsFunction`, not a local definition). Non-function externals are left as today's
  undefined external **data** symbol.

### chibil-link — route the data relocation to the stub

`SymbolResolver` already iterates **every** `MemberRef` row and synthesizes a P/Invoke
stub for each external function reference (keyed by `(name, sig)`). Two pieces:

1. **Expose a name → stub map.** Add `MetadataMerger.PInvokeStubByName`
   (`Dictionary<string, int>`); in `SymbolResolver`, after synthesizing/reusing a stub,
   record `PInvokeStubByName[name] = pinvokeToken` (first write wins — libc names have
   one signature). This makes the stub token findable by name alone, which is all a
   data relocation has.

2. **Resolve in `FieldDataRelocator`.** In the branch that currently throws
   `external data relocation target '{nm}' ... is not defined in any object`
   (line ~170), first check `merger.PInvokeStubByName`. If found, emit the relocation
   as a **method** relocation:
   `relocs.Add(new Reloc(owner.PredictedRow, intra, /*isMethod*/ true, stubToken, 0));`
   and `continue`. Otherwise keep the existing behavior (error / data-import path).

The existing `.cctor` machinery already turns a method relocation into
`ldftn <token>; store into the slot at field+intra` (the same code path used for a
data relocation to a *defined* function). No new `.cctor` codegen is required.

### Why it is correct

The stub is a managed method with the function's real signature; `ldftn` yields a
managed function pointer; QuickJS's call through the slot lowers to `calli` with the
same signature (from the C function-pointer type). This is exactly the path the spike
proved with a local pointer.

## Edge cases

- **Called + tabled** (`fabs`): one `MemberRef` (from the call), one stub; the data
  path reuses it.
- **Table-only** (`abs`, some Math funcs): the new data-path `RegisterExternalFunction`
  supplies the `MemberRef`/signature from the `extern` declaration.
- **Same name, two signatures:** stubs are keyed by `(name, sig)`; `PInvokeStubByName`
  keeps the first. A function pointer's declared type matches its call type for libc,
  so picking the first is correct. (If a future case truly needs per-signature
  resolution from a data relocation — which carries no signature — that is out of
  scope.)
- **Undefined external *data*** (pointer to an external variable, not a function): no
  stub exists for the name, so the lookup misses and the existing data-import/error
  path runs unchanged.
- **Defined functions / data-to-data relocations:** resolved by earlier branches;
  never reach the changed code.

## Test

1. **Deterministic red/green (`tests/Chibil.Tests/CoreClr`):** compile
   `static int (*fp)(int) = abs; int main(void){ return fp(-9); }`, load the object,
   `LinkPipeline.LinkToBytes(..., new List<string>{ "c" })`, assert the PE is produced.
   Today it throws `external data relocation target 'abs' ... not defined`; after the
   fix it links. (A link-success assertion — the linker no longer errors on a static
   external-function pointer.)
2. **Runtime correctness (WSL):** the same program, linked `-lc`, runs and returns
   **9** — proving the `.cctor` `ldftn`'d the right stub and the `calli` reaches native
   `abs`.
3. **Integration (WSL):** `qjs.dll` links (the `js_math_funcs` table resolves) and runs
   a script; surfaces the next QuickJS blocker if any.

Regression guard: the full CoreClr suite (defined-function data relocations,
data-to-data relocations, the bash/micropython link paths) stays green; the bash suite
in an MSVC dev shell.

## Out of scope

- Per-signature resolution of a data relocation (it carries no signature).
- Any further QuickJS runtime blockers beyond the link.
