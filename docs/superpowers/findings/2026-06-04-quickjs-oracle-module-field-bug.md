# Finding: facade member fields leak into `<Module>` at QuickJS scale

**Context:** Plan 4 (QuickJS oracle), behavioral half. The surface oracle passes (the
facade exposes 188 functions, 21 public types, 3 enums from `quickjs.h`). The behavioral
oracle — a C# program that evaluates `40+2` through `quickjs.Api` — surfaced a real bug:
`Assembly.Load(qjs.dll)` throws `System.TypeLoadException: Non-Static Global Field`.

**This is the oracle doing its job:** the bug does **not** reproduce in the `mylib`
fixture and was invisible to every prior test (which use `Assembly.Load` on tiny inputs).

## Diagnosis (what is confirmed)

Inspecting the built `qjs.dll` metadata:

- The global type `<Module>` (TypeDef row 1) owns Field rows **1..7727** (`cnt=7727`).
- Within that range, **167 fields are non-static** (instance) — struct *member* fields
  (`name`, `prop_flags`, `def_type`, `magic`, `u`, `tag`, `header`, `rt`, …). A `<Module>`
  field must be static; an instance field there is what the runtime rejects.
- The boundary is exact: rows **1..7347** are static globals (the last is
  `?A0x…unnamed-global-229`, a `.rdata` literal); rows **7348..7943** are the non-static
  member fields. So the *real* global fields end at **7347**, and member fields begin at
  **7348**.
- But `TotalGlobalFieldRows` (used to set the export class `quickjs.Api`'s `FieldList`,
  `PeWriter.cs:364` → `TotalGlobalFieldRows + 1 = 7728`) reports **7727**, so `<Module>`'s
  field range runs to 7727 and **swallows the member fields at 7348..7727**.

Tracing `_outFieldRow` (the field-row counter behind `TotalGlobalFieldRows`):

- After every object's `CopyDataFieldsAndTypeDefs`, `_outFieldRow = 7347` (the real
  globals).
- Between the copy loop and `ReserveMemberFields`, it jumps **+380 → 7727** (from
  `AllocateCommonSymbols` / `ReserveMutableSourceFields` — the tentative-definition
  *commons* and `.data` `$init` source rows).
- `ReserveMemberFields` then reserves members at 7728..7943.

So the **reserved** layout is `[globals 1..7727][members 7728..7943]`, but the **emitted**
layout is `[globals 1..7347][members 7348..7943]` — a ~380-row divergence. The member
fields physically occupy rows 7348.., while the export class's `FieldList` (7728) leaves
`<Module>` claiming 1..7727, so 380 member fields fall inside `<Module>`'s range.

**Why only QuickJS:** the +380 comes from common symbols / mutable-data globals, of which
QuickJS has hundreds and `mylib` has **zero**. With no such globals, `mylib`'s
`TotalGlobalFieldRows` equals the real global count and the layout is consistent — which
is why every `mylib` test (incl. `Assembly.Load`) passes.

## Root cause (RESOLVED)

The `<Module>` non-static fields are not a field-range arithmetic problem — they are
**extra fields wrongly synthesized** by `MetadataMerger.SynthesizeDataImports`
(`MetadataMerger.cs:1397`). That pass — gated on `-l` (libraries), which is why **only
libc-linked programs hit it** — resolves still-unmapped data references to native imports
(`stdout`, `errno`, …) by iterating *every* field of *every* object and treating any
unmapped one as an unresolved extern global.

A public struct's named **member fields** (emitted by the facade's named-fields work, owned
by a struct TypeDef, not `<Module>`) are never mapped by that pass, so member names like
`u`/`tag`/`name` were collected as "unresolved data" and emitted as **non-static `<Module>`
Bss globals** — exactly the 167 the loader rejects.

**Fix (one guard):** `SynthesizeDataImports` now skips **instance** fields — a real data
import is always an extern *global* (static); an instance field is a struct member. (`<Module>`
member-field leak gone; `TotalGlobalFieldRows` now equals the real global count.)

**Why it hid:** `mylib` and the earlier unit tests linked with **no libraries**, so the
`-l`-gated resolver never ran. The regression test
`Public_struct_member_fields_not_mistaken_for_data_imports_when_linking_libs` links with
`-lc` and a real `extern` and inspects the PE metadata directly (no `Assembly.Load`, which
would run the data-import `.cctor` off-target).

## Repro

```
wsl bash targets/build/quickjs-api.sh                 # build qjs.dll with the facade
wsl bash -lc 'cd targets/quickjs-api-consumer && dotnet run'   # → TypeLoadException
```
The surface oracle (`targets/build/quickjs-api-surface.sh`) passes — only the behavioral
consumer (which `Assembly.Load`s qjs.dll) trips the bug.

## Status: RESOLVED

Fixed (data-import resolver skips instance fields). The behavioral oracle now passes:
`quickjs.Api.JS_Eval("40+2") == 42` (and `JSValue.u.int32 == 42` read directly). Full
regression green (ApiFacade + ExportClass + MuslLink + AppHost), so libc-linked programs
are unaffected.
