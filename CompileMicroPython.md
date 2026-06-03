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

## 5. First link blocker — FIXED (unused `extern` symbols)

```
chibil-link: field 'mp_const_notimplemented_obj' RVA data references a non-TypeDef value type.
```

This was **misleading** — not a TypeDef, struct, or linker problem. Spike + TDD
delta-reduction found a 4-line root cause:

```c
struct S;                  /* incomplete, never completed */
extern const struct S g;   /* declared, NEVER used */
int main(void){ return 0; }   /* chibil -c ; chibil-link -lc -> same error */
```

`mp_const_notimplemented_obj`'s definition **and** its only use are both behind
`#if MICROPY_PY_BUILTINS_NOTIMPLEMENTED` = `(ROM_LEVEL 10 >= EXTRA 30) = 0` in the
minimal port. chibil evaluates that correctly and excludes both — leaving only an
**unused `extern` declaration**. A real linker emits no symbol for an unused
`extern`; chibil eagerly emitted a metadata `Field` for *every* declared global, so
the unused declaration became a phantom undefined symbol that `chibil-link` swept
into `SynthesizeDataImports` and failed to size.

**Fix (committed):** register an extern global's field **lazily, on first IL
reference** — a used extern self-registers, an unused one never does. Matches `ld`.
Red/green test + 129 CoreClr tests green.

## 5b. Second link blocker — FIXED (flexible array members)

```
chibil-link: modsys.obj: data relocation at .data+0xF8 is not inside any
FieldRVA field (uninitialized/unknown global?).
```

`mp_sys_implementation_obj` is a `mp_rom_obj_tuple_t` — a struct with a **flexible
array member** `items[]`. chibil-link sized the global by the struct's fixed
`ClassLayout` (0x10), but the initializer fills the FAM (→ 0x28 bytes), so a pointer
relocation in the FAM (0xF8) fell outside the undersized field.

**Fix (committed):** size an initialized FieldRVA global by its **data extent** (to
the next symbol in the section), not the struct's fixed size. Red/green test; 130
CoreClr tests green.

## 5c. **MicroPython links and starts running** 🎉

With both link blockers fixed, the minimal port **links to a single 3.4 MB MSIL
assembly** (`micropython.dll` + `micropython.runtimeconfig.json`) and **begins
executing**: `dotnet micropython.dll` reaches `main → mp_init`, runs real IL through
`mp_obj_dict_store → mp_map_lookup → qstr_hash`, and then hits a **runtime**
`AccessViolationException` in `<Module>.find_qstr(UInt64*)`.

## 5d. Runtime blocker — FIXED (Mutable FAM field storage overflow)

`find_qstr` faults during `mp_init`. Root cause, found by probing the pools from
`main` before `mp_init`:

- `mp_qstr_const_pool` reads correctly (`prev`, `total_prev_len=183`, `len=31`).
- `mp_qstr_const_pool_static` (which `prev` points to) reads **garbage** — it sits at
  `const_pool + 0x68`, but `const_pool` (len 31) needs `0x28 + 31*8 = 0x120` bytes,
  so its FAM **overlaps** the static pool. `find_qstr` walks into garbage → fault.

Why: the qstr pools are `const` but have pointer relocations (`prev`, `lengths`,
`qstrs[]`), so chibil-link emits them as **Mutable** fields — plain CLR static fields
of the struct type, initialised by the `.cctor` copying from a `$init` source field.
The **FieldRVA extent fix (5b) correctly sizes the `$init` data to 0x120**, but the
*target* Mutable field's storage is the struct's `ClassLayout` (**0x28**). The
`.cctor` copies 0x120 bytes into a 0x28 field → **overflow into the adjacent static
field**.

**Fix (committed):** size the Mutable target field's storage to its data extent —
emit it with a synthesized value-type whose `ClassLayout` = `cf.Size` (`chibil-link`
already emits `AddTypeLayout` for opaque value-types), minted during the merge's
field pass so row prediction holds. The `.cctor` copy now fits and the FAM globals
(qstr pools, attrtuples) are laid out correctly. (`MetadataMerger.GetOrAddSizedStorageFieldSig`;
red/green metadata-assertion test in `FlexibleArrayGlobalTests`.)

**Result:** with the fix, the pools no longer overlap (gap `0x160` ≥ the `0x120`
`const_pool` needs; `const_pool.prev == &static_pool`, `total_prev_len == static_pool.len`),
and MicroPython boots clean past `find_qstr`/`mp_init` to the **REPL prompt**:

```
MicroPython 44a569b637 on 2026-06-03; minimal with unknown-cpu
>>>
```

The GC initialises and allocates (`gc_init` → `print(2+3)` triggers a collection
that reports live blocks), so the qstr/object machinery is sound. The next runtime
blocker was a `NullReferenceException` in `parse_compile_execute` — fixed in §5e.

## 5e. Runtime blocker — FIXED (setjmp resume point)

Once the REPL evaluated a line, it faulted with a `NullReferenceException` in
`parse_compile_execute` (`shared/runtime/pyexec.c:166`). Root cause (confirmed by
spike: `SPIKE nlr.ret_val=0`): chibil lowered `setjmp`/`longjmp` to managed-exception
resumption that **re-ran the function body from the top** on resume. MicroPython's
nlr idiom is

```c
nlr_buf_t nlr;
nlr.ret_val = NULL;                  // re-runs on resume → wipes the exception
if (nlr_push(&nlr) == 0) { ... } else { /* derefs nlr.ret_val */ }
```

`nlr_jump` stores the exception into `nlr.ret_val` before throwing; chibil's resume
re-executed `nlr.ret_val = NULL`, clobbering it back to NULL → null deref (with
`MICROPY_PYEXEC_ENABLE_VM_ABORT = 0`, the `nlr.ret_val == NULL` guard is compiled
out, so line 166 ran unconditionally).

**Fix (committed):** for a function with a single `setjmp` not inside a loop, start
the try region **at the `setjmp` call site** rather than the function top, so code
sequenced before `setjmp` runs exactly once and a `longjmp` resumes *after* the call
— correct `setjmp` semantics. Multi-`setjmp` / `setjmp`-in-loop keep the prior
re-from-top behavior. (`chibil/CodeGen.cs`: `CountSetjmp` gate, deferred
`tryStart`/`Lhead`, `_setjmpTryOpen`; red/green runtime tests in `MuslLinkTests`.)

**Result:** the nlr exception path now works — the REPL parses, compiles, executes,
and **raises/catches/prints exceptions** correctly (no NullReference). The next
blocker is a `MemoryError` on every REPL line: evaluating `print(2+3)` tries to
allocate ≈ the entire heap. Bumping `MICROPY_HEAP_SIZE` 25 KB → 1 MB grows the failed
request in lock-step (`23808` → `1038848` bytes), so this is **not** "heap too small"
but a **size-computation bug** — something allocates a quantity derived from the total
heap size. A distinct next blocker (likely a `size_t`/pointer-arithmetic miscompile in
the GC or allocator), to debug separately.

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
