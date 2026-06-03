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

## What is NOT yet pinned

The exact reservation-vs-emission desync for the 380 common/mutable-source rows (they are
counted into `_outFieldRow` but the member block ends up physically occupying their row
range). The member-field `AssertRow` (`PeWriter.cs:385`) passes, which means the member
*first-row* prediction matches its emission — so the inconsistency is in how the
commons/`$init`-source rows are predicted vs emitted relative to the member block. A fix
needs to make the **emitted** global-field count and `TotalGlobalFieldRows` agree (so the
export class `FieldList` and `<Module>`'s range end exactly where members begin), OR set
the export class `FieldList` / `<Module>` bound from the *first member field row* rather
than `TotalGlobalFieldRows + 1`.

## Repro

```
wsl bash targets/build/quickjs-api.sh                 # build qjs.dll with the facade
wsl bash -lc 'cd targets/quickjs-api-consumer && dotnet run'   # → TypeLoadException
```
The surface oracle (`targets/build/quickjs-api-surface.sh`) passes — only the behavioral
consumer (which `Assembly.Load`s qjs.dll) trips the bug.

## Status

The behavioral-oracle harness (`targets/quickjs-api-consumer/`) is committed and correct;
it will pass once this `<Module>`-field-range bug is fixed. Recommended follow-on: a
focused plan to reconcile `TotalGlobalFieldRows` with the emitted global boundary (most
likely: bound `<Module>` / the export class by the first member-field row, and ensure
common/`$init`-source rows are emitted contiguously within the global block).
