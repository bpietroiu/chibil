# Windows global-data model (FieldRVA → runtime-initialized CLR static fields) — design

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** Make mutable C global variables work on **Windows** CoreCLR (they already work on Linux), so the chibil-compiled SQLite `:memory:` harness exits 55 on Windows *and* Linux from one `app.dll`. Linker-side change only; the `.obj` format and the IJW/MSVC path are untouched.

## 1. Problem

chibil emits every C global as a **`HasFieldRVA` static field** — its storage is PE-mapped initial-data. Globals are *accessed* by address (`ldsflda <field>` + `ldind`/`stind`), confirmed at `CodeGen.cs:1656-1694` (access) and `CodeGen.cs:1327-1378` (emission). The linker places that data in a writable `.sdata` section (`WritableDataPEBuilder`), which is why **Linux** CoreCLR allows writes.

But **Windows CoreCLR maps `FieldRVA` data read-only regardless of the PE section's `MEM_WRITE` flag.** SQLite mutates global state constantly (e.g. `sqlite3GlobalConfig`, schema state, the synthesized `.cctor`'s own pointer writes), so on Windows the first write to an initialized/BSS global faults (AccessViolation). Verified minimally: `static int g=10; int main(){ g=55; return g; }` exits 55 on Linux, AVs on Windows.

**Key enabler:** because access is `ldsflda`-based, a field's *storage* can change from FieldRVA-mapped to CLR-allocated **without touching any access site**. Non-FieldRVA CLR static fields are writable on every platform (that's how all C# statics behave); the read-only behavior is specific to FieldRVA-mapped data.

## 2. Decision

**Mutable globals become non-FieldRVA CLR static fields, initialized at runtime by the module `.cctor`.** Read-only string literals stay FieldRVA (read-only is fine on Windows — they're never written). The transformation lives entirely in the **linker** (`tools/chibil-link/`), extending the existing `.cctor` synthesis (`FieldDataRelocator`) that already applies pointer relocations.

Rejected: all-globals-CLR-static (Approach B — copies hundreds of KB of read-only literals at startup for no benefit); codegen-side change (Approach C — perturbs the `.obj`/MSVC-compatible representation; the linker owns final-image layout).

**Success criterion:** the SQLite `:memory:` harness (`samples/sqlite/main.c`) exits **55** on Windows *and* Linux CoreCLR from the same `app.dll`; the focused mutable-global regressions (§5) pass on both; no MSVC/IJW or existing-CoreCLR regression.

## 3. The model (per global, by COFF source section)

| Global kind | Today | New model |
|---|---|---|
| **`.data`** — initialized, mutable (`int g=5`, `char buf[]={…}`, `g_vfs={…}`) | `HasFieldRVA` (read-only on Windows) | **Non-FieldRVA CLR static field.** The `.cctor` `cpblk`s its init bytes from a read-only **source** field into its storage; then pointer relocations are applied to it. |
| **BSS** — zero-init (`static char heap[8MB]`, `int g;`) | `HasFieldRVA` RVA=0 (currently skipped by the data-field copy) | **Non-FieldRVA CLR static field** — the CLR zero-initializes statics automatically. No source, no init. |
| **`.rdata`** — string literals (read-only) | `HasFieldRVA` in read-only data | **Unchanged** (`HasFieldRVA`, read-only). Never written. |

`IsReadOnlyData(g) == g.IsStringLiteral` (`CodeGen.cs:3337`) is the existing `.data`/`.rdata` split; the linker already records each field's `SourceSection` (`MetadataMerger.CopiedField`).

## 4. Linker plumbing (`tools/chibil-link/`)

The `.obj` format and chibil codegen are **unchanged** (objects stay `HasFieldRVA` / MSVC-compatible). Only the final-PE emission changes.

### 4.1 `MetadataMerger` — field emission by source section
Today every copied global field is `HasFieldRVA` (`CopyDataFieldsAndTypeDefs`). Change the *output* representation per `CopiedField.SourceSection`:
- **`.data` field** → output field is a **plain CLR static field** (`FieldAttributes.Assembly | Static`, **no** `HasFieldRVA`). Synthesize an additional **read-only source field** (`HasFieldRVA`) holding the init bytes. Record `(targetFieldRow, sourceFieldRow, size)` for the `.cctor`.
- **BSS field** (currently dropped at `loc.SectionNumber <= 0`) → output a **plain CLR static field**, no source, no init.
- **`.rdata` field** (string literal) → unchanged (`HasFieldRVA`, read-only).

The IL access token maps to the **target** field row (unchanged), so `ldsflda`/`ldind`/`stind` sites are untouched. The source field is referenced only by the `.cctor`.

### 4.2 `FieldDataRelocator` — extend the `.cctor`
Prepend an **init phase** before the existing pointer-relocation phase (`BuildCctorIl`, currently `FieldDataRelocator.cs:206-238`):
```
; init phase — per .data global (copy base bytes into writable storage):
ldsflda <targetField> ; ldsflda <sourceField> ; ldc.i4 <size> ; cpblk
; reloc phase — per pointer reloc (existing), now writing into the CLR-static target:
ldsflda <ownerTargetField> [ ; ldc.i4 <intra> ; conv.i ; add ]
ldftn <method>  |  ldsflda <targetField> [ ; ldc.i4 <addend> ; conv.i ; add ]
stind.i
ret
```
Order is load-bearing: copy base bytes first, then patch pointers into the now-writable target. `maxstack` rises (cpblk needs 3 slots) — set conservatively.

### 4.3 Retire the writable-`.sdata` workaround
With mutable globals now CLR-static, **nothing mutable lives in `.sdata`** — all remaining `HasFieldRVA` data (read-only sources + string literals) is read-only. `WritableDataPEBuilder` / `FieldRvaRebaser` (the Linux-only writable-section + rebase hack) can be **removed** once the read-only placement is confirmed to load on both OSes (the `.cctor`-cpblk approach must keep working on Linux too — it will, it's portable CLR). Removing them shrinks the linker and unifies the model. (If a subtlety blocks full removal, leave them holding only read-only data; but the goal is removal.)

## 5. Testing

Each focused test runs **in-process on Windows** (`Assembly.Load` + invoke) **and on Linux** (`WslRunner`), in `tests/Chibil.Tests/CoreClr/`:
1. **Mutable scalar** — `static int g=10; int main(void){ g=55; return g; }` → 55 (the exact case that AV'd on Windows).
2. **Mutable array** — `static int a[3]={20,22,13}; int main(void){ a[0]+=0; return a[0]+a[1]+a[2]; }` → 55.
3. **BSS write** — `static int b[100]; int main(void){ b[7]=55; return b[7]; }` → 55.
4. **Pointer-init struct, written + called** (g_vfs shape) — a `static struct { int(*f)(void); } s = { g0 };` reassigned to another function then called → 55.

**Capstone:** `SqliteSmokeTests` gains a **Windows** variant via `DotnetHostRunner` (the `dotnet` host on Windows CoreCLR) asserting exit 55; the existing Linux/WSL assertion stays. Same `app.dll` runs on both.

**Non-regression:** full MSVC suite green (obj format unchanged → IJW untouched); existing CoreCLR + Linux SQLite tests still pass.

## 6. Risks
| Risk | Likelihood | Mitigation |
|---|---|---|
| `cpblk` into a static value-type field's `ldsflda` not writable on Windows | Low | Standard CLR (ldsflda of a non-FieldRVA static is a writable managed pointer); regression #1 is the oracle |
| BSS fields currently skipped — wiring them as CLR-static touches a new path | Medium | Regression #3; verify they appear as zero-initialized statics |
| Removing `WritableDataPEBuilder` breaks Linux | Low | Keep it until both-OS green; remove only after the SQLite smoke test passes on both |
| `.cctor` startup cost (cpblk of all `.data`) | Low | One-time at load; SQLite `.data` is a few hundred KB — acceptable |
| Pointer relocs must target the CLR-static field (not the read-only source) | Medium | The reloc owner is the target field; ordering (init then reloc) enforced in `BuildCctorIl` |

## 7. Out of scope
NativeAOT validation (the new model may help, but not a goal here); on-disk VFS; the C# binding surface; non-SQLite global edge cases beyond regressions §5 (the model is general, but the test bar is SQLite parity + the four focused cases).
