# Mutable FAM Field Storage Sizing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Size a Mutable FAM (flexible-array-member) global's CLR static-field storage to its data extent, so the `<Module>.cctor` initial-value copy no longer overflows into the adjacent static field (the MicroPython `find_qstr` crash).

**Architecture:** In `MetadataMerger.CopyDataFieldsAndTypeDefs`, where the data extent (`size`) and struct layout (`typeSize`) are already computed, reroute the field's signature: when `Kind == Mutable && size > typeSize`, give the field a synthesized `valuetype` whose `ClassLayout == size` (via a new cached helper) instead of the struct signature. The `.cctor` copy then lands in a field whose storage exactly matches the bytes copied. ReadOnly/BSS/exactly-sized Mutable fields are untouched.

**Tech Stack:** C# / .NET, `System.Reflection.Metadata` (ECMA-335 emit + read-back), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-03-chibil-link-mutable-fam-field-storage-design.md`

---

### Task 1: Size Mutable FAM field storage to its data extent

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs` (call site ~line 1494-1515; new helper + cache field nearby)
- Test: `tests/Chibil.Tests/CoreClr/FlexibleArrayGlobalTests.cs` (add one `[Fact]`)

Background the implementer needs:
- A `CopiedField` carries `Kind` (`ReadOnly`/`Mutable`/`Bss`), `Size` (data extent), and `SignatureBlob` (the field's type signature, used for BOTH the target field and its `<name>$init` source field).
- `CopiedTypeDef { Name, Namespace, BaseType, LayoutSize, LayoutPack, PredictedRow }` (defined ~line 131) is emitted by PeWriter with `mdBuilder.AddTypeLayout(...)` — so a `CopiedTypeDef` with `LayoutSize = N` becomes a value type of exactly `N` bytes.
- `GetOrAddCoreValueTypeRef()` (~line 266) returns the `System.ValueType` TypeRef for the base type.
- `_outTypeDefRow` is the running output TypeDef-row counter; it is already incremented during this same field-copy pass (via `EnsureFieldTypeDefs`), so minting one more TypeDef here keeps predicted == emitted.
- `Builder` is the shared `MetadataBuilder`; `Builder.GetOrAddBlob(BlobBuilder)` interns a signature blob.

- [ ] **Step 1: Write the failing test**

Add this method to `tests/Chibil.Tests/CoreClr/FlexibleArrayGlobalTests.cs` (inside the existing `FlexibleArrayGlobalTests` class). It also needs these `using`s at the top of the file — add any that are missing: `using System.Collections.Immutable;`, `using System.Reflection.Metadata;`, `using System.Reflection.PortableExecutable;` (the file already has `using System.Collections.Generic;`, `using ChibilLink;`, `using Xunit;`).

```csharp
    // A NON-const global with a flexible array member filled past sizeof(struct),
    // holding a pointer relocation (&sentinel), lands in .data -> chibil-link emits
    // it as a Mutable CLR static field. Its data extent (0x30) exceeds the struct's
    // fixed ClassLayout (0x10). The Mutable field's STORAGE must be sized to the
    // extent: the <Module>.cctor initialises it by copying `extent` bytes from a
    // $init source field, so a struct-sized field would overflow into the adjacent
    // static field. (Same shape as MicroPython's qstr pools, whose overflow
    // corrupted mp_qstr_const_pool_static -> find_qstr crash.)
    [Fact]
    public void Mutable_flexible_array_global_storage_type_is_sized_to_its_data_extent()
    {
        const string src =
            "struct b { const void *t; };\n" +
            "typedef struct { struct b base; long len; const void *items[]; } tup_t;\n" +
            "extern const int sentinel;\n" +
            "tup_t g = { {0}, 4, { &sentinel, &sentinel, &sentinel, &sentinel } };\n" +
            "const int sentinel = 42;\n" +
            "int main(void){ return g.items[3] != &sentinel; }\n";

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fam.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });

        using var pr = new PEReader(ImmutableArray.Create(pe));
        var md = pr.GetMetadataReader();

        // Find the target field named exactly "g" (NOT the "g$init" source field).
        FieldDefinition field = default;
        bool found = false;
        foreach (var fh in md.FieldDefinitions)
        {
            var fd = md.GetFieldDefinition(fh);
            if (md.GetString(fd.Name) == "g") { field = fd; found = true; break; }
        }
        Assert.True(found, "target field 'g' not found in linked PE");

        // Decode the field signature to its value-type TypeDef, read its ClassLayout.
        // (Mirrors MetadataMerger.GetFieldDataSize: ELEMENT_TYPE_VALUETYPE is reported
        // as SignatureTypeCode.TypeHandle; step back over the byte, then ReadTypeHandle.)
        var sr = md.GetBlobReader(field.Signature);
        sr.ReadSignatureHeader();
        SignatureTypeCode tc = sr.ReadSignatureTypeCode();
        Assert.Equal(SignatureTypeCode.TypeHandle, tc);
        sr.Offset -= 1; sr.ReadByte();
        EntityHandle th = sr.ReadTypeHandle();
        Assert.Equal(HandleKind.TypeDefinition, th.Kind);
        var layout = md.GetTypeDefinition((TypeDefinitionHandle)th).GetLayout();

        // extent = 16 (struct b + long len) + 4*8 (items[4]) = 0x30; struct sizeof = 0x10.
        Assert.False(layout.IsDefault, "storage value-type has no ClassLayout");
        Assert.True(layout.Size >= 0x30,
            $"Mutable FAM field 'g' storage ClassLayout 0x{layout.Size:X} < data extent 0x30");
    }
```

- [ ] **Step 2: Run the test to verify it fails (RED)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Mutable_flexible_array_global_storage_type_is_sized_to_its_data_extent"
```
Expected: FAIL on the final assertion — `... storage ClassLayout 0x10 < data extent 0x30` (the field currently uses the struct type, ClassLayout `0x10`).

If instead it fails at `Assert.Equal(SignatureTypeCode.TypeHandle, tc)` or "field 'g' not found", the global was not emitted as a value-type field / not named `g` — STOP and inspect the linked metadata before proceeding (do not weaken the assertion).

- [ ] **Step 3: Add the cache field and the helper to `MetadataMerger.cs`**

Add the cache field next to the other field declarations (e.g. directly after the `_typeDefByKey` dictionary, ~line 182):

```csharp
    // Synthesized fixed-size storage value types (ClassLayout == size) for Mutable
    // fields whose initialized data extent exceeds their declared struct size
    // (flexible array members / trailing padding). Cached by size so identically
    // sized fields share one type. Keyed value is the `valuetype <T>` field sig.
    private readonly Dictionary<int, BlobHandle> _sizedStorageSig = new();
```

Add the helper method (place it just before `EnsureTypeDefCopied`, ~line 1996):

```csharp
    /// <summary>
    /// Field signature <c>valuetype &lt;T&gt;</c> where T is a synthesized value type
    /// with <c>ClassLayout == size</c>. Gives a Mutable field whose initialized data
    /// extent exceeds its declared struct size (flexible array member / trailing
    /// padding) storage that matches the bytes the <c>&lt;Module&gt;.cctor</c> copies
    /// into it — otherwise the copy overflows into the adjacent static field.
    /// </summary>
    private BlobHandle GetOrAddSizedStorageFieldSig(int size)
    {
        if (_sizedStorageSig.TryGetValue(size, out var cached)) return cached;

        _outTypeDefRow++;
        var td = new CopiedTypeDef
        {
            Name = $"$FieldStorage${size}",
            Namespace = "",
            BaseType = GetOrAddCoreValueTypeRef(),
            LayoutSize = size,
            LayoutPack = 1,
            PredictedRow = _outTypeDefRow,
        };
        CopiedTypeDefs.Add(td);

        var b = new BlobBuilder();
        new BlobEncoder(b).FieldSignature()
            .Type(MetadataTokens.TypeDefinitionHandle(td.PredictedRow), isValueType: true);
        var sig = Builder.GetOrAddBlob(b);
        _sizedStorageSig[size] = sig;
        return sig;
    }
```

- [ ] **Step 4: Reroute the Mutable oversized field's signature at the call site**

In `CopyDataFieldsAndTypeDefs`, the field is currently added (~line 1500-1515) with `SignatureBlob = Builder.GetOrAddBlob(sigB)`. Replace that inline expression with a local computed just before the `CopiedFields.Add`. Change:

```csharp
            _outFieldRow++;
            map.SetField(fh, _outFieldRow);
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd.Attributes,
                Name = md.GetString(fd.Name),
                SignatureBlob = Builder.GetOrAddBlob(sigB),
                Data = data,
```

to:

```csharp
            // A Mutable field whose initialized data extent exceeds its declared
            // struct size (flexible array member / trailing padding) needs storage
            // sized to the extent: the .cctor copies `size` bytes into it, so a
            // struct-sized field would overflow the copy into the next static field.
            BlobHandle sigBlob = Builder.GetOrAddBlob(sigB);
            if (kind == CopiedField.FieldKind.Mutable && size > typeSize)
                sigBlob = GetOrAddSizedStorageFieldSig(size);

            _outFieldRow++;
            map.SetField(fh, _outFieldRow);
            CopiedFields.Add(new CopiedField
            {
                Attributes = fd.Attributes,
                Name = md.GetString(fd.Name),
                SignatureBlob = sigBlob,
                Data = data,
```

(Leave the rest of the object initializer — `Alignment`, `PredictedRow`, `Kind`, `SourceObj`, `SourceSection`, `SourceOffset`, `Size` — exactly as-is.)

- [ ] **Step 5: Run the test to verify it passes (GREEN)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Mutable_flexible_array_global_storage_type_is_sized_to_its_data_extent"
```
Expected: PASS (the field now references `$FieldStorage$48`, ClassLayout `0x30`).

- [ ] **Step 6: Run the full CoreClr regression suite**

The repo's full `dotnet test` includes ~120 tests that shell out to `cl.exe`/`link.exe` and FAIL in a plain shell (environmental, not regressions) — they must be run inside an MSVC dev shell. From a Developer PowerShell (or after importing `vcvars64`):
```
dotnet test tests/Chibil.Tests
```
Expected: the previously-green set stays green (the 130 CoreClr tests pass; the `cl.exe`/`link.exe` failures, if any, are the known environmental ones — confirm the count/identity matches the pre-change baseline, i.e. no NEW failures). In particular `FlexibleArrayGlobalTests` (both methods) and `LinkerOutputTests` pass.

- [ ] **Step 7: Commit**

```
git add tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/FlexibleArrayGlobalTests.cs
git commit -m "Size Mutable FAM field storage to its data extent

A const-with-relocations flexible-array-member global is emitted as a Mutable
CLR static field initialised by the <Module>.cctor copying its data extent from
a \$init source field. The target field's storage was the struct's fixed
ClassLayout, so the copy overflowed into the adjacent static field (MicroPython's
qstr pools corrupted mp_qstr_const_pool_static -> find_qstr crash). Give such
fields a synthesized value-type sized to the extent.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Verify MicroPython `find_qstr` no longer crashes, then clean up

This is the integration proof. The MicroPython tree under `targets/micropython/` is git-ignored (third-party; never committed). The harness is `targets/build/micropython-chibil.sh` (run under WSL). Task 1 must be committed first.

**Files:**
- Modify (then revert): `targets/micropython/ports/minimal/main.c` — remove the CHIBIL probe block (gitignored; not committed)
- Modify: `CompileMicroPython.md` (§5d) — record the outcome

- [ ] **Step 1: Rebuild + relink MicroPython with the fixed chibil-link**

Run (under WSL, from repo root):
```
wsl bash targets/build/micropython-chibil.sh
```
Expected: `micropython.dll` (~3.4 MB) + `micropython.runtimeconfig.json` produced, no link error.

- [ ] **Step 2: Run it and observe the probe output**

Run (under WSL, in the build output dir — see the harness header for the exact path, e.g. `/tmp/mpy_obj` or the port dir):
```
wsl bash -lc 'cd <build-out-dir> && dotnet micropython.dll </dev/null'
```
Expected: the two `PROBE ...` lines now show `const_pool` and `static_pool` at NON-overlapping addresses (the gap between them is >= `0x120`, not `0x68`), and execution proceeds PAST `find_qstr` / `mp_init` (no `AccessViolationException` at `<Module>.find_qstr`). It may stop later for a different reason (next runtime blocker) — that is success for THIS fix.

If it still crashes in `find_qstr` with the same overlap, STOP: the fix did not take — re-inspect whether the pools are actually Mutable and whether `size > typeSize` held for them (add a temporary diagnostic in `GetOrAddSizedStorageFieldSig`).

- [ ] **Step 3: Remove the probe from `main.c`**

Delete the probe block in `targets/micropython/ports/minimal/main.c` — the two `extern const qstr_pool_t ...;` declarations and the two `printf("PROBE ...")` calls added for debugging (restore `main` to start with `stack_top = ...;` then go straight to the `#if MICROPY_ENABLE_GC` / `gc_init` / `mp_init` sequence). This file is gitignored; the edit is just to keep the scratch tree clean.

- [ ] **Step 4: Update `CompileMicroPython.md` §5d to record the outcome**

Edit the §5d heading and body so it reads as RESOLVED. Replace the heading line `## 5d. Runtime blocker — DEBUGGED (Mutable FAM field storage overflow)` with `## 5d. Runtime blocker — FIXED (Mutable FAM field storage overflow)`, and append a short paragraph after the existing "**Fix (next):**" paragraph stating the fix landed and what the run now reaches. Concretely, change the final paragraph from beginning "**Fix (next):**" to "**Fix (committed):**" and append one sentence describing the observed result from Step 2 (pools no longer overlap; execution proceeds past `find_qstr`/`mp_init`; note the next blocker if one appeared).

- [ ] **Step 5: Commit the doc update**

```
git add CompileMicroPython.md
git commit -m "MicroPython: find_qstr crash fixed (Mutable FAM field storage)

The Mutable FAM field storage fix resolves the qstr-pool overlap; mp_init now
runs past find_qstr. Records the integration result.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the executor

- **Do not** commit anything under `targets/` (third-party / gitignored).
- Commit messages end with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer (already in the templates above).
- If on the default branch (`master`), create a feature branch first. (This work is already on branch `mpy-find-qstr-crash`.)
- The `cl.exe`/`link.exe` test failures in a plain shell are environmental (missing MSVC in PATH), never regressions — run the full suite inside an MSVC dev shell.
