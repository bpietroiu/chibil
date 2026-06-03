# Compiling MicroPython to MSIL with chibil

Status of bringing up **MicroPython** (the `minimal` port) on CoreCLR via chibil —
a sibling experiment to the bash port. This documents the recipe, the chibil gaps
it surfaced, and the one remaining link blocker.

**Headline result:** all **138/138** translation units of the minimal port compile
to MSIL with chibil. Linking is blocked on a single chibil-link FieldRVA issue
(below). The work used the same playbook as bash: native tools for codegen, chibil
for compilation, musl headers, `chibil-link` + `-lc`.

---

## 1. Layout & prerequisites

- Source: `git clone --depth 1 https://github.com/micropython/micropython` into
  `targets/micropython/` (git-ignored; not vendored).
- Port: **`ports/minimal`** with `CROSS=0` — the smallest host build. Its only
  libc surface is `read`/`write` (stdio via `mp_hal`) plus `string.h`. The arch
  emitters (`asmx64.c`, `emitn*.c`, …) compile to near-empty objects because
  native-code emission is off.
- Build under **WSL** (`dotnet`, plus host `gcc`/`make`/`python3` for codegen).
  musl 1.2.6 headers come from `targets/musl-1.2.6` (already `./configure`-d, so
  `obj/include/bits/alltypes.h` exists).

Tracked harness (in `targets/build/`):
`micropython-chibil.sh` (compile+link driver), `micropython-chibil-compat.h`
(`-include` shim for GCC builtins chibil lacks), `micropython-chibil-analyze.sh`.

## 2. Baseline (native gcc) — proves the source + generates headers

```bash
cd targets/micropython/ports/minimal
make -j4          # -> build/firmware.elf (148 KB text) and build/genhdr/*.h
```
This generates the codegen headers chibil reuses verbatim: `qstrdefs.generated.h`,
`moduledefs.h`, `root_pointers.h`, `mpversion.h`, `compressed.data.h`, and
`build/_frozen_mpy.c`. The QSTR pipeline only needs a C preprocessor, so the
native build is the cheapest way to produce them.

## 3. chibil compile recipe

Per TU (138 of them; see `micropython-chibil.sh`):

```
dotnet chibil.dll -c --target=coreclr -nostdinc -mlp64 \
  -include chibil-compat.h \
  -I. -I../.. -Ibuild \
  -I<musl>/arch/x86_64 -I<musl>/arch/generic -I<musl>/obj/include -I<musl>/include \
  -DMICROPY_ROM_TEXT_COMPRESSION=1 \
  -DMICROPY_NLR_SETJMP=1 \         # nlr via setjmp — no native asm in MSIL
  -DMICROPY_OPT_COMPUTED_GOTO=0 \  # switch dispatch — chibil can't do goto *ptr
  <src.c> -o <obj>
```

Key configuration choices:
- **`MICROPY_NLR_SETJMP=1`** — MicroPython's non-local return (exceptions) defaults
  to per-arch assembly (`nlrx64.c`). chibil targets MSIL, so force the
  `setjmp`/`longjmp` implementation (chibil supports those).
- **`MICROPY_OPT_COMPUTED_GOTO=0`** — the VM's opcode dispatch uses GCC computed
  goto (`goto *ptr`), which chibil cannot emit. The switch-based dispatch works.
- `-nostdinc -mlp64` + musl headers — identical to the bash port.

## 4. chibil gaps surfaced (and how each was handled)

| # | Gap | Status |
|---|---|---|
| 1 | **Prefix `__attribute__((noreturn))`** (MicroPython `MP_NORETURN`) — parser errored "variable name omitted" | **Fixed in chibil** (commit `2c95713`): `__attribute__` is now a typename so the declspec loop consumes/skips it; `AttributeList` skips unknown attributes |
| 2 | `__builtin_clz` / `clzl` / `clzll` / `ctz` / `popcount` — implicit declaration | Worked around via `-include` C fallbacks. **TODO:** implement in chibil via `System.Numerics.BitOperations.{LeadingZeroCount,TrailingZeroCount,PopCount}` |
| 3 | `__builtin_expect(x, c)` — implicit declaration | Worked around (`#define __builtin_expect(x,c) (x)`). Trivial to add natively |
| 4 | **`_Static_assert`** (C11) — not parsed | Worked around (empty macro). **TODO:** parse + evaluate the const-expr |
| 5 | **Computed goto** (`goto *ptr`) — unsupported in MSIL | Config off (`MICROPY_OPT_COMPUTED_GOTO=0`). Inherent to the MSIL model |
| 6 | Postfix `void f(void) __attribute__((...))` — "expected '{'" | Not needed by the minimal port; remains a chibil gap |
| 7 | `-DMACRO=` (empty value) did not suppress an `#ifndef`-guarded `#define` | Unverified; possible chibil `-D` bug — sidestepped by the native fix for #1 |

After the chibil fix + the `-include` shim, **all 138 TUs compile to MSIL.**

## 5. The remaining blocker — linking

```
chibil-link: field 'mp_const_notimplemented_obj' RVA data references a non-TypeDef value type.
```

`mp_const_notimplemented_obj` (and `mp_const_ellipsis_obj`) are `const` singleton
globals: `const mp_obj_singleton_t mp_const_notimplemented_obj = {{&mp_type_singleton}, …};`.
The struct `_mp_obj_singleton_t` is **complete only in `objsingleton.c`**; every
other TU that takes its address (e.g. `modbuiltins.c` via `obj.h`) sees it
**forward-declared (incomplete)**.

It has **two** layers, both rooted in how chibil emits a struct that is used only
**by-pointer and as a `const` global's type** (never as a value local), so chibil
never emits a sized `TypeDef` for it:

**(a) FieldRVA sizing.** `chibil-link`'s `GetFieldDataSize` sizes a `HasFieldRVA`
global from its field's value-type and requires a `TypeDef` with `ClassLayout`.
Diagnostic (added then reverted) showed the field type is a **same-module
`TypeRef`** — `TypeRef ''.'_mp_obj_singleton_t' scope=ModuleDefinition` — and that
**no object** emits `_mp_obj_singleton_t` as a `TypeDef` (the object that owns the
FieldRVA lists `_mp_obj_type_t`, `_mp_obj_dict_t`, … but not it). A prototype that
falls back to sizing from the **FieldRVA data extent** (distance to the next
FieldRVA symbol in the section) cleared this layer.

**(b) Symbol resolution.** With (a) bypassed, the link then treats
`mp_const_notimplemented_obj` as an **unresolved data import** (e.g. from
`argcheck.obj`) instead of binding the cross-TU references to the `objsingleton.c`
**definition**. So the `const` global's def↔ref matching across the
incomplete-type boundary also fails.

**Recommended fix — chibil side (fixes both layers at once):** make chibil emit a
real, sized `TypeDef` (with `ClassLayout`) for a struct that is the type of a
defined global, and emit the global's field type as that `TypeDef` token (not a
same-module `TypeRef`), with consistent symbol naming for def and refs. Then
`chibil-link`'s existing sizing + symbol resolution handle it unchanged.

The experimental `chibil-link` sizing fallback was **reverted** — it cleared (a)
but not (b), and modifying the merge core is risky without the full test pass. The
diagnosis above is the handoff. After the chibil-side fix: link with `-lc`, emit
the invariant-globalization `runtimeconfig.json` (chibil-link does this), and run
`dotnet micropython.dll` for the REPL.

## 6. Windows vs Linux

chibil output is platform-neutral MSIL; the platform split is purely which libc the
P/Invokes bind to at run time:
- **Linux:** `-lc` → `libc.so.6` (`read`/`write`/`mem*`/`str*`), as bash does.
- **Windows:** the same `.dll` runs under `dotnet`, but the libc P/Invokes must bind
  to the Windows CRT (`ucrtbase`/`msvcrt` — `_read`/`_write`) instead of
  `libc.so.6`. That mapping (and `mp_hal`'s stdin/stdout) is the Windows-specific
  follow-up once linking works.

## 7. Reproduce

```bash
# 1. clone + native baseline (generates QSTR headers)
cd targets/micropython/ports/minimal && make -j4
# 2. capture the source list once (from a verbose build) into /tmp/mpy_srcs.txt
#    (the harness expects it; see micropython-chibil.sh header)
# 3. chibil compile + link
wsl bash targets/build/micropython-chibil.sh
```
