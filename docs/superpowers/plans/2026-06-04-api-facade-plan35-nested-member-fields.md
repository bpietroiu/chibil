# API Facade — Plan 3.5: Nested struct/union member fields

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a public struct expose members whose type is **another struct/union** as named C# fields (`JSValue.u` → `quickjs.JSValueUnion u`), and **gracefully skip** members whose type can't be cleanly encoded — instead of crashing the whole compile. This closes the one gap the QuickJS oracle spike surfaced.

**Background (from the spike):** Building `qjs.dll` with `--export-api=quickjs.h` crashed in `CodeGen.MaterializeStructTypeDefs` (`CodeGen.cs:1482`) because the named-fields emission (Plan 2.5) calls `EncodeType(m.Ty)` on every public-struct member, and `EncodeType` *throws* an internal-error guard (`CodeGen.cs:425`) when a struct/union member's canonical type is a no-TypeDef nested aggregate (`IsNestedMember`). The Plan-2.5 first cut only handled scalar/pointer members. Everything else about the facade works at scale (188 funcs, 21 public types, 3 enums emitted from real `quickjs.h`) — this is the lone blocker.

**Fix:** before emitting a named field, validate the member type. A member is named iff its type is encodable as a field — a scalar/enum, a pointer, or a struct/union **that has a real TypeDef**. Members whose type is a no-TypeDef nested aggregate (or a pointer/array to one) are skipped (the struct still gets its `ClassLayout` size and its other named fields; chibil's offset-based IL is unaffected). Struct/union members that DO have a TypeDef are emitted as `valuetype <that TypeDef>`, so nested public types like `JSValueUnion` become named fields.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-06-03-api-facade-design.md`. Branch: `feature/api-facade-plan35-nested-member-fields`. Builds on Plans 1-3 (merged).

---

## File Structure

- Modify `chibil/CodeGen.cs` — add `CanEncodeFieldType(CType)`; gate the named-field emission on it (`MaterializeStructTypeDefs` ~1473-1489).
- Modify `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` — tests for a named-struct member (emitted) and an anonymous-union member (skipped, no crash); plus the chibil-link round-trip.
- Possibly modify `tools/chibil-link/MetadataMerger.cs` — only if the round-trip test reveals the nested member type's TypeDef isn't carried (the existing `RewriteFieldSignature` should already remap the token; Task 2 verifies).

---

## Task 1: Encode struct/union member fields; skip un-encodable ones

**Files:**
- Modify: `chibil/CodeGen.cs` (`MaterializeStructTypeDefs` ~1473-1489; add `CanEncodeFieldType`)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ApiFacadeTests` (the COFF-metadata inspection pattern from Plan 2.5):

```csharp
    // A public struct with a NAMED-struct member (should become a named field of that
    // struct's type) and an ANONYMOUS-union member (should be skipped, not crash).
    const string NestHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "struct MlInner { int a; int b; };\n" +
        "struct MlOuter { struct MlInner inner; int tag; };\n" +
        "struct MlAnon { union { int i; float f; } v; int tag; };\n" +
        "int ml_outer_sum(struct MlOuter o);\n" +
        "int ml_anon_tag(struct MlAnon a);\n" +
        "#endif\n";
    const string NestSrc =
        "#include \"mylib.h\"\n" +
        "int ml_outer_sum(struct MlOuter o){ return o.inner.a + o.inner.b + o.tag; }\n" +
        "int ml_anon_tag(struct MlAnon a){ return a.tag; }\n";

    static System.Reflection.Metadata.TypeDefinitionHandle FindTd(System.Reflection.Metadata.MetadataReader md, string name)
    {
        foreach (var h in md.TypeDefinitions)
            if (md.GetString(md.GetTypeDefinition(h).Name) == name) return h;
        return default;
    }

    [Fact]
    public void Public_struct_named_aggregate_member_becomes_a_field()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(NestSrc, NestHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var md = ObjectFile.Load(obj, "mylib.obj").Md;
        var outer = FindTd(md, "MlOuter");
        Assert.False(outer.IsNil);
        var names = new System.Collections.Generic.List<string>();
        foreach (var fh in md.GetTypeDefinition(outer).GetFields())
        {
            string n = md.GetString(md.GetFieldDefinition(fh).Name);
            if (n != "<alignment member>") names.Add(n);
        }
        Assert.Contains("inner", names);   // named-struct member is emitted as a field
        Assert.Contains("tag", names);
    }

    [Fact]
    public void Public_struct_anonymous_aggregate_member_is_skipped_not_crash()
    {
        // Compiling this must NOT throw; the anonymous-union member `v` is skipped,
        // but the struct still compiles and its scalar `tag` is named.
        byte[] obj = TestCompiler.CompileToObjWithApi(NestSrc, NestHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var md = ObjectFile.Load(obj, "mylib.obj").Md;
        var anon = FindTd(md, "MlAnon");
        Assert.False(anon.IsNil);
        var names = new System.Collections.Generic.List<string>();
        foreach (var fh in md.GetTypeDefinition(anon).GetFields())
        {
            string n = md.GetString(md.GetFieldDefinition(fh).Name);
            if (n != "<alignment member>") names.Add(n);
        }
        Assert.Contains("tag", names);     // scalar member still named
        Assert.DoesNotContain("v", names); // anonymous-union member skipped (un-encodable)
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_named_aggregate|FullyQualifiedName~ApiFacadeTests.Public_struct_anonymous_aggregate"`
Expected: FAIL — the anonymous-union test throws `InvalidOperationException` (the crash) during compile; the named-aggregate test may also fail/throw.

- [ ] **Step 3: Add `CanEncodeFieldType` + gate the emission**

In `chibil/CodeGen.cs`, add a helper (place near `MaterializeStructTypeDefs` or the other type helpers):

```csharp
    // True if `ty` can be encoded as a struct member FIELD signature. Scalars/enums and
    // pointers/arrays-to-encodable are fine; a struct/union is fine only if it has a real
    // TypeDef (i.e. it is NOT a no-TypeDef nested/anonymous aggregate). Mirrors the
    // un-encodable cases EncodeType's internal guard would throw on.
    private bool CanEncodeFieldType(CType ty)
    {
        CType c = ty;
        while (c.Origin != null) c = c.Origin;
        switch (c.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Union:
                if (c.IsNestedMember) return false;                 // anonymous nested aggregate: no TypeDef
                return _structTypeDefs.ContainsKey(GetTypeId(c));    // must have a pre-allocated TypeDef
            case TypeKind.Ptr:
            case TypeKind.Array:
                return c.Base == null || CanEncodeFieldType(c.Base); // pointee/element must also encode
            default:
                return true;                                         // scalar / enum / etc.
        }
    }
```

(Confirm field/method names: `CType.Origin`, `CType.Kind`, `CType.IsNestedMember`, `CType.Base`, `_structTypeDefs`, `GetTypeId` — all exist per CodeGen.cs/ChibiTypes.cs. For `Ptr`/`Array` the field holding the pointee/element is `Base` — verify the actual name.)

In the named-field emission loop (`MaterializeStructTypeDefs` ~1475-1489), add the gate before encoding:

```csharp
                    for (Member m = type.Members; m != null; m = m.Next)
                    {
                        if (m.IsBitfield) continue;
                        if (m.Ty.Kind == TypeKind.Array) continue;
                        if (m.Name == null) continue;
                        if (!CanEncodeFieldType(m.Ty)) continue;     // skip un-encodable (anon nested) members
                        var msig = new BlobBuilder();
                        msig.WriteByte(0x06);
                        EncodeType(msig, m.Ty);
                        var mfh = _md.AddFieldDefinition(
                            FieldAttributes.Public,
                            _md.GetOrAddString(Util.GetTokenText(m.Name)),
                            _md.GetOrAddBlob(msig));
                        _nextFieldRow++;
                        _md.AddFieldLayout(mfh, m.Offset);
                    }
```

(The existing array-skip line is now redundant with `CanEncodeFieldType` returning false for arrays, but keep it — it's a fast early-out and explicit. `CanEncodeFieldType` makes the loop never reach `EncodeType` on a type that would throw.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_named_aggregate|FullyQualifiedName~ApiFacadeTests.Public_struct_anonymous_aggregate"`
Expected: PASS — `MlOuter.inner` is a named field, `MlAnon` compiles with `v` skipped but `tag` named.

Sanity: `dotnet build chibil/chibil.csproj -c Debug` → succeeds;
`dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"` → all prior facade tests still pass (scalar/pointer member emission unchanged).

- [ ] **Step 5: Commit**

```bash
git add chibil/CodeGen.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: name struct/union member fields with TypeDefs; skip un-encodable members"
```

---

## Task 2: chibil-link round-trips the nested-aggregate member field

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`
- Possibly modify: `tools/chibil-link/MetadataMerger.cs` (only if the test reveals the nested member type's TypeDef isn't carried)

The member field signature for `MlOuter.inner` is `FIELD valuetype <MlInner-TypeDef-token>`. chibil-link's `RewriteFieldSignature` (added in Plan 2.5) remaps tokens in field signatures, so this *should* already resolve to `mylib.MlInner` (or the private MlInner TypeDef) in the linked assembly. This task verifies it end-to-end and fixes the token mapping if needed.

- [ ] **Step 1: Write the failing/likely-passing test**

Add to `ApiFacadeTests`:

```csharp
    [Fact]
    public void Linked_public_struct_has_nested_aggregate_field()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(NestSrc, NestHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        Type outer = asm.GetType("mylib.MlOuter");
        Assert.NotNull(outer);
        FieldInfo inner = outer.GetField("inner", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(inner);                                   // nested-aggregate field survived the link
        Assert.Equal("MlInner", inner.FieldType.Name);          // typed as the inner struct
        // The whole struct still passes by value through the forwarder.
        MethodInfo sum = asm.GetType("mylib.Api").GetMethod("ml_outer_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(sum);
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Linked_public_struct_has_nested_aggregate"`
Expected: likely PASS (RewriteFieldSignature already remaps the token, and MlInner gets a CopiedTypeDef because it is referenced). If it FAILS (e.g. `Assembly.Load` throws because the field's TypeDef token is wrong/dangling, or the field type doesn't resolve), the nested member type's TypeDef isn't being carried/remapped — fix in `tools/chibil-link/MetadataMerger.cs`: ensure the TypeDef referenced by a member-field signature is copied (it should be, via the normal struct-TypeDef copy path, since MlInner is used by-value in `ml_outer_sum`'s signature too). Re-run until green. Do NOT weaken the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs tools/chibil-link/MetadataMerger.cs
git commit -m "chibil-link: round-trip nested-aggregate public-struct member fields"
```
(If no MetadataMerger change was needed, omit it from the add.)

---

## Task 3: QuickJS no-crash verification + fixture + regression

**Files:**
- Modify: `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h`, `.../src/mylib.c`
- Modify: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Grow the fixture with a nested-aggregate struct**

Append to `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h` (before `#endif`):

```c
struct MlInner { int a; int b; };
struct MlOuter { struct MlInner inner; int tag; };
int ml_outer_sum(struct MlOuter o);
```

Append to `tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c`:

```c
int ml_outer_sum(struct MlOuter o){ return o.inner.a + o.inner.b + o.tag; }
```

- [ ] **Step 2: Add a fixture test that exercises the nested field end-to-end**

Add to `ApiFacadeTests`:

```csharp
    [Fact]
    public void Fixture_nested_aggregate_field_round_trips()
    {
        string lib = FixtureDir();
        byte[] obj = TestCompiler.CompileToObjWithApi(
            File.ReadAllText(Path.Combine(lib, "src", "mylib.c")),
            File.ReadAllText(Path.Combine(lib, "include", "mylib.h")),
            "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        Type outer = asm.GetType("mylib.MlOuter");
        Assert.NotNull(outer.GetField("inner", BindingFlags.Public | BindingFlags.Instance));
    }
```

- [ ] **Step 3: Full regression sweep**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"` → ALL pass (the grown fixture now has functions + struct(named fields) + nested-aggregate + opaque + enum, all coexisting).
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ExportClassTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~MuslLinkTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests"` → ALL pass.

- [ ] **Step 4: QuickJS no-crash spot check (WSL, manual — optional but recommended)**

If WSL is available, confirm the original blocker is gone: compile `quickjs.c` with `--export-api=quickjs.h` and verify it no longer throws (it previously crashed at `MaterializeStructTypeDefs`). A one-off:
```
wsl bash -lc 'cd /mnt/d/sandbox/chibil/targets/quickjs-2025-09-13 && dotnet /mnt/d/sandbox/chibil/chibil/bin/Debug/net10.0/chibil.dll -c --target=coreclr -nostdinc -mlp64 --export-api=quickjs.h -include /mnt/d/sandbox/chibil/targets/build/quickjs-chibil-compat.h -I/mnt/d/sandbox/chibil/targets/build/qjs-compat -I. -I/mnt/d/sandbox/chibil/targets/musl-1.2.6/arch/x86_64 -I/mnt/d/sandbox/chibil/targets/musl-1.2.6/arch/generic -I/mnt/d/sandbox/chibil/targets/musl-1.2.6/obj/include -I/mnt/d/sandbox/chibil/targets/musl-1.2.6/include -DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION=\"2025-09-13\" quickjs.c -o /tmp/qjs_check.obj && echo QUICKJS_COMPILE_OK'
```
Expected: `QUICKJS_COMPILE_OK` (no `nested member` exception). This is a manual confidence check; the full qjs.dll oracle is Plan 4. Note the result in the commit message or report; do not gate the commit on WSL availability.

- [ ] **Step 5: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/fixtures/mylib tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: grow mylib fixture with nested aggregate; Plan 3.5 complete"
```

---

## Notes for the implementer

- The crash this fixes is `InvalidOperationException: nested member type '...' reached signature encoding` at `CodeGen.cs:425`, hit by the Plan-2.5 named-field emission at `CodeGen.cs:1482`. `CanEncodeFieldType` is a pre-check so `EncodeType` is never called on a type it would throw on.
- A struct/union member with a real TypeDef (named type) is now emitted as a `valuetype <TypeDef>` field. The member's type TypeDef is referenced by token; chibil-link's `RewriteFieldSignature` remaps it, and the type is copied via the normal struct-copy path (it is also used by-value in the function signatures, so it is always present).
- A truly anonymous nested member (no TypeDef) is skipped — the struct keeps its `ClassLayout` size and other named fields; chibil's offset-based IL is unaffected (members are accessed by raw offset, never by field token). This matches how bitfields/arrays are already skipped.
- Do NOT change `EncodeType`'s guard — it is a correct internal-error guard for other callers (function signatures should never encode a nested member). Only the named-field caller pre-checks and skips.
- This unblocks the QuickJS oracle (Plan 4): with this fix, `qjs.dll` builds with `--export-api=quickjs.h` (the spike confirmed 188 funcs / 21 types / 3 enums emit once the nested-member crash is handled).
