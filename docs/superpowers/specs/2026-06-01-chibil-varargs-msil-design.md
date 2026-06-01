# Varargs on the MSIL target (chibil) — design

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** Implement C variadic functions for chibil's CoreCLR/MSIL target — both *defining/consuming* chibil-internal varargs (Layer 1) and *calling native* variadic functions (Layer 2) — portably on Windows **and** Linux. Prerequisite that unblocks compiling SQLite (`docs/superpowers/specs/2026-06-01-sqlite-managed-sp1-design.md`).

## 1. Problem & current state

chibil cannot compile C functions that *define* and consume varargs, and its variadic *call* emission is incomplete. Verified state:
- `Parser.cs:1842` explicitly errors: *"variadic function definitions are not supported in MSIL mode."*
- `__builtin_va_list` is not a recognized type; none of `__builtin_va_start`/`va_arg`/`va_end`/`va_copy` exist (only `__builtin_types_compatible_p`/`reg_class`/`compare_and_swap`/`atomic_exchange` are implemented, `Parser.cs:1153`).
- `fn.VaArea` / `__va_area__` (`Parser.cs:1843`, a 136-byte char array; skipped at `CodeGen.cs:1423`) is vestigial — a chibicc x86-64 SysV register-save-area with no meaning on the CLR; never consumed.
- Variadic call sites never emit the CIL `vararg` convention or a sentinel + actual arg types; `CodeGen.cs:2227`/`1027`/`1139` hardcode calling-convention `0x00` and encode only fixed params. It works today only because the one large sample (DOOM) avoids variadic calls.

SQLite *defines* variadic functions (`sqlite3_mprintf`, `sqlite3VXPrintf`) and forwards a `va_list` (`mprintf → VXPrintf`), so all of: define, consume, `va_list`-as-parameter, and forwarding are required.

## 2. Why not the standard MSIL approach (ArgIterator)

The canonical MSIL lowering — David Hanson's lcc.NET (`msil.c`) — uses the **CLR `vararg` calling convention + `System.ArgIterator`**: `va_list = valuetype [mscorlib]System.ArgIterator`; `va_start` emits the `arglist` opcode + `ArgIterator::.ctor(RuntimeArgumentHandle)`; `va_arg` calls `ArgIterator::GetNextArg()` → `typedref` → `refanyval`. This was full-featured on **.NET Framework**.

**Empirically ruled out for our target.** A direct test on .NET 10:
- Windows: a C# `__arglist`/`ArgIterator` method returns the correct result.
- **Linux/WSL: `System.InvalidProgramException: Vararg calling convention not supported.`**

CoreCLR dropped the managed vararg calling convention on Unix. Since portable Linux is the entire point (managed SQLite), ArgIterator is a dead end for chibil's CoreCLR target.

## 3. Decision: two portable mechanisms (Approach B), one spec

Neither mechanism uses the managed vararg convention; both are portable (Windows/Linux/WASM/AOT identical).

| Call direction | Mechanism | Portable on Linux |
|---|---|---|
| chibil C → **its own** variadic fn | **Layer 1: va-buffer** | ✅ |
| chibil C → **native** variadic fn (`printf`) | **Layer 2: monomorphized cdecl call** | ✅ for int/ptr/string; float caveat (§7) |
| native / C# → chibil `...` fn via native vararg ABI | (out of scope) | ⚠️ needs a non-variadic / `va_list` wrapper |

The only genuinely Linux-hard direction is the third (a chibil `...` function invoked *as* a native variadic) — niche and always wrappable. It is **not** a goal here; it is explicitly *not* a permanent wall on calling native varargs.

## 4. Layer 1 — the va-buffer ABI (chibil-internal varargs)

By example, `T f(int a, ...)` called as `f(1, 20, 3.5, ptr)`:

- **Definition lowering:** `f` compiles to IL method `T f(int a, void* __va)` — the `...` becomes one hidden trailing pointer parameter (calling convention stays `0x00`; *no* vararg convention). The vestigial `__va_area__` is removed; in its place the parser appends the `__va` parameter.
- **Call lowering:** the caller applies **C default argument promotions** to each variadic arg (`char/short/bool→int`, `float→double`, array/function→pointer), `localloc`s a buffer of `8 × nVarArgs` bytes (pushes `null` when `nVarArgs == 0`), `stind`/`stobj`s each promoted value into its **8-byte slot** (natural size ≤ 8, little-endian), and calls `f(1, &buf)`. The buffer lives on the caller's frame for the whole call and any forwarding within it.
- **`__builtin_va_list`** is a real chibil type (`TyVaList`), a pointer-width type aliased to a byte pointer.
- **`va_start(ap, last)`** → `ap = __va` (load the hidden param into the `ap` lvalue). `last` is ignored — `__va` already points at slot 0.
- **`va_arg(ap, U)`** → load `ap`; read `*(U*)ap` (`ldobj U` / appropriate `ldind`); `ap += 8`; store `ap` back; yield the value. Uniform 8-byte slots make `int`/`long long`/`double`/pointer all read correctly on the 64-bit target.
- **`va_end(ap)`** → no-op. **`va_copy(d, s)`** → pointer assign `d = s`.
- **`va_list` as a parameter** (`g(int n, va_list ap)`) → an ordinary pointer parameter. **Forwarding** (`f` does `va_start(ap,a); g(n, ap);`) passes the same pointer. This is the SQLite `mprintf → VXPrintf` shape.

## 5. Layer 2 — native variadic calls (monomorphized cdecl)

**Callee classification by calling convention** (reuses chibil's existing `CType.CallConv`):
- variadic + **default/managed** convention → Layer 1 (va-buffer);
- variadic + **`__cdecl`** (how libc headers declare `printf`) → Layer 2.

Explicit and unambiguous — no link-time guessing. The libc header declares `int __cdecl printf(const char*, ...)`; chibil's own variadics are default convention.

**Codegen:** at a `__cdecl`-variadic call site the actual arg types are known, so emit an ordinary external call whose signature is **fixed params + the concrete promoted variadic types** (`printf("%d",x)` → `printf(byte*, int)`; `printf("%s",p)` → `printf(byte*, byte*)`), marked cdecl. No buffer packing.

**Linker:** `chibil-link` resolves the unresolved external into a **monomorphized `pinvokeimpl`** keyed by `(name, concrete-signature)` — a small extension of F1's P/Invoke synthesis (dedup changes from by-name to **by-name+signature**, so `printf(byte*,int)` and `printf(byte*,byte*)` coexist, both bound to the same native entry). CoreCLR permits multiple managed signatures for one native export.

## 6. Components & files

**`chibil/TypeSystem.cs`** — add `TyVaList` (pointer-width); add a `NodeKind`-free helper to recognize it.
**`chibil/Tokenizer.cs` / `Parser.cs`** — recognize `__builtin_va_list` as a type specifier (declarations + parameter types); add `__builtin_va_start`/`va_arg`/`va_end`/`va_copy` to the `Primary()` dispatch as new `NodeKind`s (`VaStart`/`VaArg`/`VaEnd`/`VaCopy`); remove the `:1842` block; for variadic *definitions* append the synthetic `__va` parameter instead of `__va_area__`.
**`chibil/ChibiTypes.cs`** — remove `Obj.VaArea`; add the `NodeKind`s and any node fields (`VaArg.Ty`, the `ap`/`dst`/`src` operands).
**`chibil/CodeGen.cs`** — definition signature gains the hidden pointer param (`RegisterFunction`); call-site Layer-1 packing (`GenFunCall`); call-site Layer-2 concrete-cdecl signature emission; lower the four va nodes; delete the `:1423` `VaArea` skip.
**`tools/chibil-link/SymbolResolver.cs`** — Layer-2 monomorphized P/Invoke (dedup by name+signature).
**Tests** — `tests/Chibil.Tests/CoreClr/VarargsTests.cs` (un-skip + extend); `scenarios/*.c` regressions.

## 7. Scope, edge cases & caveats

**In scope:** scalar variadic args — `int`/`unsigned`/`long`/`long long`, `double` (with `float→double`, `char/short/bool→int`), and pointers — on the **64-bit CoreCLR target** (x64/arm64). `long double` rides along (chibil maps it to `double`). Define, consume, `va_list` parameter, forwarding (Layer 1); native cdecl calls (Layer 2).

**Out of scope (clear errors, not silent miscompiles):**
- **Struct-by-value as a variadic argument** (`va_arg(ap, struct S)`) — SQLite doesn't use it; emit "not supported."
- **32-bit target** — the 8-byte-slot model assumes 64-bit pointers; future work.
- **Exposing a chibil `...` function via the native vararg ABI** (direction 3) — use a `va_list`-taking or non-variadic wrapper.
- **Indirect variadic calls** (call through a function pointer to a variadic callee) — the va-buffer hidden param cannot be represented in a `calli` standalone signature; emits a compile error.
- **Cross-TU chibil variadic functions** (a chibil `...` function defined in one object and called from another) — external variadics are assumed native cdecl; a chibil variadic defined in a different TU and declared as `extern` will be wrongly routed to the Layer-2 native path (single-TU programs like the SQLite amalgamation are unaffected).

**The SysV-AL caveat (Layer 2, must be validated in implementation):** the Linux x64 SysV ABI requires register **`AL` = number of SSE/vector args** when calling a variadic function; a concrete non-variadic cdecl `calli`/`pinvokeimpl` does not set it. Consequences:
- integer/pointer/string varargs to native functions (the bulk of `printf` usage) → work portably;
- **floating-point** varargs to native functions on **Linux x64** may misbehave because `AL` is unset. This is a SysV reality, not a chibil bug. Documented sharp edge; worst case native-float-varargs is Windows-only (no AL requirement on Win x64). **Layer 1 (chibil's own variadics) is unaffected — floats work everywhere.**

## 8. Testing

All CoreCLR in-process tests run on **Windows and WSL/Linux** — the cross-platform run is the core validation (Approach B exists precisely so Linux does not hit the vararg-convention wall).

**Layer 1:**
1. Spike (un-skip): `int sum_n(int count, ...)` → `sum_n(3,20,22,13) == 55`.
2. Mixed types: sum of `int` + `long long` + `double`; pointer args dereferenced.
3. Zero variadic args.
4. **Forwarding** (`vprintf`-style): `f(n,...)` builds a `va_list` and calls `g(int n, va_list)` which consumes it.
5. `va_copy` then independent re-iteration.

**Layer 2:**
6. `snprintf(buf, n, "%d", 42)` against real libc (Linux `-lc`) / msvcrt (Windows) → `buf == "42"`; plus a `"%s"` pointer case.
7. A float case documenting the §7 caveat's actual behavior on each platform.

Each lands a `scenarios/` regression. **Downstream proof (not a success criterion here):** SQLite SP1 Part C should get past SQLite's `va_list` functions.

**Success criterion for this spec:** Layer-1 tests 1–5 pass on Windows and Linux; Layer-2 tests 6 pass for int/ptr/string on both; the float case (7) behaves as documented.

## 9. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| 8-byte slot / promotion mismatch (sign-extension, read width) | Medium | Spike + mixed-type tests are exact oracles; little-endian slot read by `va_arg` type |
| Hidden-param shift breaks parameter indexing in codegen | Medium | Define-side tests; the `__va` param is appended last |
| Layer-2 monomorphized pinvoke dedup collision (F1 was by-name) | Medium | Change dedup key to name+signature; test two arities of one fn |
| SysV-AL for native float varargs on Linux x64 | High (known) | Documented; int/ptr/string covered; float-native may stay Windows-only |
| `localloc` buffer lifetime across forwarding | Low | Caller-frame `localloc` spans the whole call tree; C forbids escaping `va_list` |

## 10. Out of scope (this spec)
SQLite itself (separate); 32-bit target; struct-by-value varargs; native-vararg *callee* (chibil `...` exposed via native ABI); performance tuning.
