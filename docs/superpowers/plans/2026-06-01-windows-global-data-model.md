# Windows Global-Data Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make mutable C globals writable on Windows CoreCLR by emitting them as non-FieldRVA CLR static fields initialized at runtime by the module `.cctor`, so the chibil-compiled SQLite `:memory:` harness exits 55 on Windows *and* Linux from one `app.dll`.

**Architecture:** Linker-only change in `tools/chibil-link/`. The `.obj` format and chibil codegen are untouched (objects stay `HasFieldRVA` / MSVC-compatible). When emitting the final PE, the linker splits each global field by its COFF source section: `.data` (mutable) and BSS become **plain CLR static fields**; the `.cctor` `cpblk`s `.data` init bytes from a read-only **source** field into the mutable target, then applies the existing pointer relocations. `.rdata` (string literals) stay `HasFieldRVA`. Access sites (`ldsflda`) are unchanged.

**Tech Stack:** C# / .NET 10, `System.Reflection.Metadata`, `System.Reflection.PortableExecutable`; `tools/chibil-link/` (`MetadataMerger`, `PeWriter`, `FieldDataRelocator`); CoreCLR in-process + `DotnetHostRunner` (Windows host) + `WslRunner` (Linux) test harness.

**Reference docs:** Spec `docs/superpowers/specs/2026-06-01-windows-global-data-model-design.md`. Branch: `windows-globals` (off `master`).

**Key existing code (verified):**
- `tools/chibil-link/MetadataMerger.cs` — `CopyDataFieldsAndTypeDefs` (field copy loop, ~`:368-432`); `CopiedField` class (`:115-131`, has `SourceObj`/`SourceSection`/`SourceOffset`/`Size`); BSS detection (`:404-411`, `isBss`); `_outFieldRow` row prediction.
- `tools/chibil-link/PeWriter.cs` — Step 5a0 field emission (`:152-171`): `mappedFieldData` blob, `AddFieldDefinition(cf.Attributes,…)` + `AddFieldRelativeVirtualAddress`. The `.cctor` synthesis (`:59-83`). `fieldDataOffsets` + `FieldRvaRebaser`/`WritableDataPEBuilder` (`:300-320`).
- `tools/chibil-link/FieldDataRelocator.cs` — `Collect` (pointer relocs), `BuildCctorIl` (`:206-238`, emits `ldsflda owner; ldftn/ldsflda target; stind.i`).
- Field access is `ldsflda <field>` + `ldind`/`stind` (`CodeGen.cs:1656-1694`) — unchanged by this work.

**Test runners (exist):** `DotnetHostRunner.RunPeViaDotnetHost(pe, out output)` runs `dotnet app.dll` as a Windows subprocess and returns the exit code (so an AccessViolation crashes the *subprocess*, not the test host). `WslRunner.Run(pe, cfg)` runs on Linux. `TestCompiler.CompileToObj(src, target)` / `CompileFileToObj(path, target, defs, incs)`; `ObjectFile.Load`; `LinkPipeline.LinkToBytes(objs, libs)`.

---

## Task 1: Failing regression — mutable scalar global on Windows

**Files:** Create `tests/Chibil.Tests/CoreClr/GlobalDataTests.cs`

> Run the produced PE through the **dotnet host subprocess** (`DotnetHostRunner`), NOT in-process `Assembly.Load`: a write to read-only FieldRVA data raises an AccessViolation, which is an uncatchable corrupted-state exception that would crash the test runner. A subprocess just gets a non-55 exit code.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class GlobalDataTests
{
    static int RunViaHost(string src, out string output)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Mutable_scalar_global_writable_via_dotnet_host()
    {
        // Writing an initialized global must work. On Windows this AV'd before
        // the fix (FieldRVA data is read-only there); on Linux it already worked.
        int exit = RunViaHost("static int g = 10; int main(void){ g = 55; return g; }", out string outp);
        Assert.True(exit == 55, $"expected 55, got {exit}. {outp}");
    }
}
```

- [ ] **Step 2: Run it — expect FAIL on Windows**

Run (Windows): `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Mutable_scalar_global_writable_via_dotnet_host`
Expected: **FAIL** — the subprocess faults writing `g` (exit code is an AV/non-zero, not 55). (On Linux this would already pass — the bug is Windows-specific.)

- [ ] **Step 3: Commit the failing test**

```bash
git add tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
git commit -m "test: failing mutable-global-on-Windows regression"
```

---

## Task 2: Core transform — mutable globals as CLR static fields initialized by the .cctor

This is one cohesive change across `MetadataMerger`, `PeWriter`, and `FieldDataRelocator` (they're coupled through field-row prediction). The runtime oracle is Task 1's test going green on Windows *and* staying green on Linux.

**Files:** Modify `tools/chibil-link/MetadataMerger.cs`, `tools/chibil-link/PeWriter.cs`, `tools/chibil-link/FieldDataRelocator.cs`.

- [ ] **Step 1: Classify each `CopiedField` by section kind** (`MetadataMerger.cs`)

Add to the `CopiedField` class (`:115-131`):
```csharp
public enum FieldKind { ReadOnly, Mutable, Bss }   // .rdata literal / .data / zero-init
public FieldKind Kind;
public int SourceFieldRow;   // for Mutable: the synthesized read-only source field's row (0 if none)
```
In `CopyDataFieldsAndTypeDefs`, when building each `CopiedField` (`:418-431`), set `Kind` from the COFF section:
```csharp
var section = of.Coff.GetSection(loc.SectionNumber);   // already fetched as `sec`
string secName = sec.Name;          // ".data" / ".rdata" / ".bss" / ".data$..." etc.
CopiedField.FieldKind kind =
    isBss ? CopiedField.FieldKind.Bss
    : secName.StartsWith(".rdata") ? CopiedField.FieldKind.ReadOnly
    : CopiedField.FieldKind.Mutable;
```
(set `Kind = kind` in the `CopiedField` initializer). `isBss` is computed at `:404-411`.

> Confirm the section-name strings via a quick probe: `coffobjdumper`/`ObjectFile` exposes `CoffSectionHeader.Name`. chibil emits string literals to `.rdata` (`CodeGen.cs:3265`), initialized globals to `.data`, and zero-init/`g_heap` as BSS (`PointerToRawData==0`). If a `.data$xx` COMDAT suffix appears, treat any `.data`-prefixed section as Mutable and any `.rdata`-prefixed as ReadOnly.

- [ ] **Step 2: Reserve a source-field row for each Mutable field** (`MetadataMerger.cs`)

After the field-copy loop predicts all `CopiedField` rows (the `_outFieldRow++` assignments), reserve one extra **source** field row per Mutable field so the `.cctor` can reference it and PeWriter's row assertions hold. Add a method called from `MergeAndPredict` AFTER all objects' `CopyDataFieldsAndTypeDefs` have run:
```csharp
private void ReserveMutableSourceFields()
{
    foreach (var cf in CopiedFields)
        if (cf.Kind == CopiedField.FieldKind.Mutable)
            cf.SourceFieldRow = ++_outFieldRow;   // appended after all target rows
}
```
Call it once, after the per-object field copy, before method-row prediction continues. (The targets keep rows `1..N`; sources get `N+1..N+M`. Access tokens map to the target rows — unchanged.)

- [ ] **Step 2.5: Apply addends into the source data** — the existing code writes reloc addends into `cf.Data` for the FieldRVA path. Keep that; the `cpblk` copies those bytes into the mutable target, and pointer relocs then overwrite the pointer slots. No change needed beyond ensuring `cf.Data` is the source bytes.

- [ ] **Step 3: Emit fields by kind in PeWriter** (`PeWriter.cs`, Step 5a0, `:157-171`)

Replace the single `foreach (var cf in merger.CopiedFields)` field-emission with kind-aware emission, in two passes so row order matches prediction (targets `1..N`, then sources `N+1..M`):
```csharp
var mappedFieldData = new BlobBuilder();
var fieldDataOffsets = new Dictionary<int, int>();   // outputRow -> blob offset (FieldRVA fields only)

// Pass 1: target fields, rows 1..N (one per CopiedField, in PredictedRow order).
foreach (var cf in merger.CopiedFields)
{
    System.Reflection.Metadata.FieldDefinitionHandle fh;
    if (cf.Kind == MetadataMerger.CopiedField.FieldKind.ReadOnly)
    {
        // unchanged: HasFieldRVA read-only data field
        int align = cf.Alignment <= 0 ? 1 : cf.Alignment;
        while ((mappedFieldData.Count % align) != 0) mappedFieldData.WriteByte(0);
        int off = mappedFieldData.Count;
        mappedFieldData.WriteBytes(cf.Data);
        fh = mdBuilder.AddFieldDefinition(cf.Attributes, mdBuilder.GetOrAddString(cf.Name), cf.SignatureBlob);
        mdBuilder.AddFieldRelativeVirtualAddress(fh, off);
        fieldDataOffsets[MetadataTokens.GetRowNumber(fh)] = off;
    }
    else
    {
        // Mutable (.data) or Bss: plain CLR static field — NO HasFieldRVA, NO FieldRVA row.
        var attrs = cf.Attributes & ~System.Reflection.FieldAttributes.HasFieldRVA;
        fh = mdBuilder.AddFieldDefinition(attrs, mdBuilder.GetOrAddString(cf.Name), cf.SignatureBlob);
    }
    AssertRow(cf.PredictedRow, MetadataTokens.GetRowNumber(fh), $"Field '{cf.Name}'");
}

// Pass 2: read-only SOURCE fields for Mutable targets, rows N+1.. (in SourceFieldRow order).
foreach (var cf in merger.CopiedFields)
{
    if (cf.Kind != MetadataMerger.CopiedField.FieldKind.Mutable) continue;
    int align = cf.Alignment <= 0 ? 1 : cf.Alignment;
    while ((mappedFieldData.Count % align) != 0) mappedFieldData.WriteByte(0);
    int off = mappedFieldData.Count;
    mappedFieldData.WriteBytes(cf.Data);
    var srcFh = mdBuilder.AddFieldDefinition(
        System.Reflection.FieldAttributes.Assembly | System.Reflection.FieldAttributes.Static
            | System.Reflection.FieldAttributes.HasFieldRVA,
        mdBuilder.GetOrAddString(cf.Name + "$init"),
        cf.SignatureBlob);
    AssertRow(cf.SourceFieldRow, MetadataTokens.GetRowNumber(srcFh), $"Field '{cf.Name}$init'");
    mdBuilder.AddFieldRelativeVirtualAddress(srcFh, off);
    fieldDataOffsets[MetadataTokens.GetRowNumber(srcFh)] = off;
}
```
> `cf.SignatureBlob` is reused for the source field (same value type / size). The `<Module>` field-list count must now include the source fields — update `int totalFields = merger.CopiedFields.Count` (`:186`) to `merger.CopiedFields.Count + <number of Mutable fields>` (i.e. the final `_outFieldRow`). Expose the final field count from the merger (e.g. `merger.TotalFieldRows`) and use it for the value-type TypeDefs' field-list upper bound.

- [ ] **Step 4: Extend the `.cctor` with the init phase** (`FieldDataRelocator.cs` `BuildCctorIl`, and its call site `PeWriter.cs:59-83`)

`BuildCctorIl` currently takes `List<Reloc>`. Add an init phase that runs first. Change its signature to also take the mutable fields' `(targetRow, sourceRow, size)`:
```csharp
public static byte[] BuildCctorIl(List<Reloc> relocs,
    IReadOnlyList<(int targetRow, int sourceRow, int size)> inits)
{
    var il = new BlobBuilder();
    // Init phase: copy each .data global's bytes from its read-only source into
    // the (writable) CLR static target, before any pointer relocations.
    foreach (var (targetRow, sourceRow, size) in inits)
    {
        il.WriteByte(0x7F); il.WriteInt32(0x04000000 | targetRow);   // ldsflda target
        il.WriteByte(0x7F); il.WriteInt32(0x04000000 | sourceRow);   // ldsflda source
        EmitLdcI4(il, size);                                          // ldc.i4 size
        il.WriteByte(0xFE); il.WriteByte(0x17);                       // cpblk
    }
    // Reloc phase: existing pointer-patching (writes into the now-writable targets).
    foreach (var r in relocs) { /* unchanged body */ }
    il.WriteByte(0x2A);                                              // ret
    return il.ToArray();
}
```
At the call site (`PeWriter.cs:59-83`): build the `inits` list from `merger.CopiedFields.Where(Kind==Mutable)` → `(cf.PredictedRow, cf.SourceFieldRow, cf.Size)`. Synthesize the `.cctor` if **either** `relocs` OR `inits` is non-empty (today it's only `relocs`). Raise the `.cctor` `MaxStack` to at least 3 (cpblk needs 3 stack slots).

> `cpblk` (`0xFE 0x17`) pops (dest, src, size). `ldsflda` of a CLR static field yields a writable managed pointer; `ldsflda` of the read-only source yields a readable pointer — `cpblk` reads src, writes dest: correct.

- [ ] **Step 5: Build + run Task 1's test — expect PASS on Windows AND Linux**

Run (Windows): `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Mutable_scalar_global_writable_via_dotnet_host`
Expected: **PASS** (exit 55). Then add a Linux assertion to the same test file (via `WslRunner`) and confirm it stays green on Linux:
```csharp
[Fact]
public void Mutable_scalar_global_writable_on_linux()
{
    if (!WslRunner.Available()) return;
    byte[] obj = TestCompiler.CompileToObj("static int g=10; int main(void){ g=55; return g; }", Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>());
    var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
    Assert.True(exit == 55, $"linux exit {exit}: {outp}");
}
```

- [ ] **Step 6: Confirm existing CoreCLR tests still green**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: all green (the fib/puts/cross-object/snprintf tests exercise the linker; they must still link+run). If a read-only-literal test regressed, the `.rdata` path lost data — verify ReadOnly fields still get `HasFieldRVA` + bytes.

- [ ] **Step 7: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
git commit -m "feat(linker): mutable globals as CLR static fields initialized by the .cctor"
```

---

## Task 3: Cover mutable arrays, BSS, and pointer-init structs

**Files:** Modify `tests/Chibil.Tests/CoreClr/GlobalDataTests.cs` (+ any kind-specific fixes the tests surface).

- [ ] **Step 1: Add the three regressions** (each runs via the dotnet host on Windows; add Linux variants via `WslRunner` for at least the array case)

```csharp
[Fact]
public void Mutable_array_global() // .data aggregate
{
    int exit = RunViaHost("static int a[3]={20,22,13}; int main(void){ a[0]+=0; return a[0]+a[1]+a[2]; }", out var o);
    Assert.True(exit == 55, o);
}

[Fact]
public void Bss_global_writable() // zero-init, no FieldRVA, no source — CLR auto-zeroes
{
    int exit = RunViaHost("static int b[100]; int main(void){ b[7]=55; return b[7]; }", out var o);
    Assert.True(exit == 55, o);
}

[Fact]
public void Pointer_init_struct_global() // g_vfs shape: function-pointer field, reassigned, called
{
    string src = @"
static int f0(void){ return 0; }
static int f55(void){ return 55; }
static struct { int (*fn)(void); } s = { f0 };
int main(void){ s.fn = f55; return s.fn(); }";
    int exit = RunViaHost(src, out var o);
    Assert.True(exit == 55, o);
}
```

- [ ] **Step 2: Run — expect PASS** (the Task 2 transform should already handle all three: `.data` array via cpblk; BSS via CLR auto-zero; pointer-init struct via cpblk + the existing pointer-reloc phase writing `f0`'s pointer, then the program reassigns to `f55`).

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "Mutable_array_global|Bss_global_writable|Pointer_init_struct_global"`
Expected: PASS. If BSS fails (it was a previously-skipped path — fields with no data location were dropped entirely): ensure BSS globals now produce a CLR static field. NOTE: the merger currently only creates `CopiedField`s for fields whose data is in a *section* (`loc.SectionNumber > 0`); a truly location-less common symbol is skipped and gets **no** field at all. If `Bss_global_writable` shows a missing-field/`MapToken` error, the BSS-in-section path (`isBss` true, `SectionNumber>0`, e.g. `g_heap`) is what the new model handles; a location-less common symbol is a separate pre-existing gap — if the test hits it, switch the test's `b` to a definition that lands in a BSS *section* (chibil's default for file-scope zero-init statics) and note the common-symbol gap as out of scope.

- [ ] **Step 3: Fix** any kind-specific issue; re-run to green.

- [ ] **Step 4: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
git commit -m "test(linker): mutable array/BSS/pointer-struct globals on Windows+Linux"
```

---

## Task 4: SQLite Windows parity + retire the writable-`.sdata` workaround

**Files:** Modify `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs`; possibly remove `tools/chibil-link/WritableDataPEBuilder.cs` + `FieldRvaRebaser.cs` and their use in `PeWriter.cs`.

- [ ] **Step 1: Add a Windows variant of the SQLite smoke test** (the existing one is WSL-gated)

```csharp
// add to SqliteSmokeTests.cs
[Fact]
public void Memory_db_crud_returns_55_on_windows()
{
    if (!DotnetHostRunner.DotnetAvailable()) return;
    // (reuse the same 3-source compile+link as Memory_db_crud_returns_55_on_linux;
    //  factor the build into a private helper returning the linked PE bytes.)
    byte[] pe = BuildSqliteAppDll();   // extract the compile+link from the existing test
    int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string output);
    Assert.True(exit == 55, $"windows exit {exit}: {output}");
}
```
Refactor the existing Linux test's compile+link into a shared `static byte[] BuildSqliteAppDll()` and call it from both.

- [ ] **Step 2: Run — expect PASS on Windows** (and the Linux variant still passes)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "Memory_db_crud_returns_55"`
Expected: BOTH pass (exit 55). This is the headline success criterion: one `app.dll`, 55 on Windows and Linux. (~40s for the sqlite3.c compile.)

- [ ] **Step 3: Retire the writable-`.sdata` workaround** (now that nothing mutable is FieldRVA)

In `PeWriter.cs`, switch from `WritableDataPEBuilder` back to a plain `ManagedPEBuilder` with `mappedFieldData` (FieldRVA data is now all read-only: sources + literals). Remove the `SDataRva`/`FieldRvaRebaser.SetAbsolute` post-pass if `ManagedPEBuilder`'s own FieldRVA placement loads correctly on both OSes. Then delete `WritableDataPEBuilder.cs` and `FieldRvaRebaser.cs` (and the test-project `<Compile Include>` for them).

- [ ] **Step 4: Re-run all CoreCLR + both SQLite tests after the removal**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "CoreClr|Memory_db_crud"`
Expected: all green on Windows; the WSL SQLite test green on Linux.
> If the plain-`ManagedPEBuilder` FieldRVA placement does NOT load on one OS, KEEP `WritableDataPEBuilder`/`FieldRvaRebaser` (they now hold only read-only data — harmless) and skip the deletion; note it. The retirement is a cleanup, not a requirement.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/
git commit -m "feat(linker): SQLite :memory: runs on Windows + Linux (one app.dll); simplify FieldRVA"
```

---

## Task 5: Full regression gate

- [ ] **Step 1: Full MSVC suite** (the `.obj` format is unchanged, so the IJW/MSVC path must be untouched)

Run: `run-tests.cmd x64`
Expected: all green (the previous baseline was 164 passed / 3 skipped). Confirm 0 failures.

- [ ] **Step 2: Linux SQLite still green**

Run the WSL SQLite smoke test (it's part of the CoreClr filter): confirm `Memory_db_crud_returns_55_on_linux` passes.

- [ ] **Step 3: Update `samples/sqlite/README.md`** — change the Windows status from ⚠️ to ✅ (the FieldRVA caveat is resolved); one line on the model (mutable globals are CLR static fields initialized by the `.cctor`).

- [ ] **Step 4: Commit**

```bash
git add samples/sqlite/README.md
git commit -m "docs(sqlite): Windows now runs (mutable globals via .cctor-initialized statics)"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** §3 model (per-section split) ↔ Task 2 Step 1 (Kind) + Step 3 (kind-aware emission); §4.1 source fields ↔ Task 2 Steps 2–3; §4.2 `.cctor` init phase ↔ Task 2 Step 4; §4.3 retire writable-`.sdata` ↔ Task 4 Step 3; §5 tests 1–4 ↔ Tasks 1+3; §5 capstone ↔ Task 4; non-regression ↔ Task 5.
- **The cohesion caveat:** Task 2 is one interdependent change (field classification ↔ row reservation ↔ PeWriter two-pass emission ↔ `.cctor` init) tested by the runtime oracle (the scalar global running on Windows). It can't be cleanly sub-divided into independently-green steps; the steps are ordered build-up, with Step 5 as the green gate.
- **Row-prediction invariant:** target fields keep rows `1..N` (so IL access tokens resolve unchanged); source fields are appended `N+1..M`. The `AssertRow` checks in PeWriter are the guardrail — if they fire, the merger's `_outFieldRow` reservation and PeWriter's emission order disagree.
- **Empirical points (oracle = the runtime test):** the exact `.data`/`.rdata`/`.bss` section-name strings chibil emits (Task 2 Step 1 — probe with `ObjectFile`/`CoffSectionHeader.Name`); whether plain `ManagedPEBuilder` FieldRVA placement loads on both OSes after retiring the writable-`.sdata` hack (Task 4 Step 3 — keep the hack if not). BSS location-less common symbols are a pre-existing gap, out of scope (Task 3 Step 2 note).
- **Non-regression:** chibil codegen and the `.obj` are untouched → the MSVC/IJW suite is the gate (Task 5).
```
