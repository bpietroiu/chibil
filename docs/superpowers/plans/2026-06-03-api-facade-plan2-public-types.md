# API Facade — Plan 2: Public types (structs/unions + opaque handles)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make struct/union and opaque types declared in a `--export-api` header appear as real **public** C# types in the facade namespace (`mylib.MlPoint`, `mylib.MlCtx`), not in the global namespace.

**Architecture:** chibil tags struct/union tags declared in the public header and records them in the `.chiapi` manifest (format bumped to v2 with a types section). chibil-link, after its existing TypeDef merge, runs a pass that **re-namespaces the already-canonical `CopiedTypeDef`** for each manifest type into the facade namespace and marks it public — reusing the linker's pre-existing cross-TU TypeDef dedup (so the forwarders and `<Module>` methods already share that one type). Opaque handles (forward-declared, synthesized as empty TypeDefs) are re-namespaced by the same pass since they are also `CopiedTypeDef`s.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-06-03-api-facade-design.md`. Builds on Plan 1 (merged). Enums are **Plan 3** — not here.

---

## File Structure

- Modify `chibil/ChibiTypes.cs` — add `CompilerOptions.PublicApiTypes` (HashSet<string>).
- Modify `chibil/Parser.cs` — record struct/union tags from `--export-api` headers in `StructUnionDecl` (~line 670).
- Modify `chibil/CodeGen.cs` — `BuildChiapiBlob`: bump manifest to v2, emit a types section; relax the null-guard.
- Modify `tools/chibil-link/ChibilApi.cs` — add `Types` set; parse the v2 types section.
- Modify `tools/chibil-link/PeWriter.cs` — collect manifest types + facade namespace; thread to `MetadataMerger`; union the api-type rows into the public set.
- Modify `tools/chibil-link/MetadataMerger.cs` — store the api type set + namespace; add `ApplyApiTypeNamespacing()` + `ApiPublicTypeRows`.
- Modify `tests/Chibil.Tests/CoreClr/fixtures/mylib/{include/mylib.h,src/mylib.c}` — grow with a public struct + opaque handle.
- Modify `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` — tests.

The fixture types used through Tasks 1-4 (inline consts in the test):

```c
// header (public surface)
struct MlPoint { int x; int y; };          // public struct, by value
struct MlCtx;                              // opaque (never defined)
int ml_add(int a, int b);
int ml_sum(struct MlPoint p);
struct MlCtx *ml_ctx_new(void);
int ml_ctx_id(struct MlCtx *c);
```
```c
// src (private impl)
#include "mylib.h"
static int secret(int x){ return x * 2; }
int ml_add(int a, int b){ return a + b + secret(0); }
int ml_sum(struct MlPoint p){ return p.x + p.y; }
struct MlCtx *ml_ctx_new(void){ return (struct MlCtx*)0; }
int ml_ctx_id(struct MlCtx *c){ return c ? 1 : 0; }
```

---

## Task 1: Tag public struct/union types during parse

**Files:**
- Modify: `chibil/ChibiTypes.cs:425` (`CompilerOptions`, near `PublicApiFunctions`)
- Modify: `chibil/Parser.cs:670` (`StructUnionDecl`)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests` (the class already has `LibSrc`/`LibHdr`; add the new type-bearing consts and the test):

```csharp
    const string TyHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "struct MlPoint { int x; int y; };\n" +
        "struct MlCtx;\n" +
        "int ml_sum(struct MlPoint p);\n" +
        "struct MlCtx *ml_ctx_new(void);\n" +
        "int ml_ctx_id(struct MlCtx *c);\n" +
        "#endif\n";
    const string TySrc =
        "#include \"mylib.h\"\n" +
        "int ml_sum(struct MlPoint p){ return p.x + p.y; }\n" +
        "struct MlCtx *ml_ctx_new(void){ return (struct MlCtx*)0; }\n" +
        "int ml_ctx_id(struct MlCtx *c){ return c ? 1 : 0; }\n";

    [Fact]
    public void Public_struct_and_opaque_tag_are_recorded()
    {
        var opts = TestCompiler.CompileAndReturnOptions(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        Assert.Contains("MlPoint", opts.PublicApiTypes);  // defined in the header
        Assert.Contains("MlCtx", opts.PublicApiTypes);    // forward-declared (opaque) in the header
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_and_opaque"`
Expected: FAIL — `PublicApiTypes` does not exist (compile error).

- [ ] **Step 3: Add the options field**

In `chibil/ChibiTypes.cs`, in `CompilerOptions` right after `PublicApiFunctions`:

```csharp
    // Collected during parse: tag names of struct/union types declared in an
    // ExportApiHeaders header. Consumed by CodeGen for the .chiapi manifest.
    public HashSet<string> PublicApiTypes = new();
```

- [ ] **Step 4: Record struct/union tags in the parser**

In `chibil/Parser.cs`, in `StructUnionDecl` (~line 670), right after the tag token is captured (`if (tok.Kind == TokenKind.Ident) { tag = tok; tok = tok.Next; }`), add:

```csharp
        // Record public-API struct/union tags declared (or forward-declared) in an
        // --export-api header. Fires for definitions and `struct Foo;` forward decls.
        if (tag != null && IsFromExportApiHeader(tag))
            _options.PublicApiTypes.Add(Util.GetTokenText(tag));
```

`IsFromExportApiHeader(Token)` already exists on `Parser` (added in Plan 1). `Util.GetTokenText(tag)` is the same call `StructUnionDecl` uses to set `ty.TagName` (confirm by reading line ~681). `_options` is the Parser's options field (confirmed in Plan 1).

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_and_opaque"`
Expected: PASS — `MlPoint` and `MlCtx` recorded.

- [ ] **Step 6: Commit**

```bash
git add chibil/ChibiTypes.cs chibil/Parser.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: tag public struct/union types declared in --export-api headers"
```

---

## Task 2: Bump the `.chiapi` manifest to v2 (emit + parse the types section, atomically)

**Files:**
- Modify: `chibil/CodeGen.cs` (`BuildChiapiBlob`, ~line 4105)
- Modify: `tools/chibil-link/ChibilApi.cs`
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

**Why one task:** bumping the version byte v1→v2 makes the existing v1 parser reject the
section. If emit and parse were separate commits, the in-between commit would break every
Plan-1 facade test (`of.Api` would be null). So emit + parse change together.

Manifest v2 = v1 layout with the version byte bumped to **2** and, after the function
list, a types section: Int32 type count, then per type UInt16 length + UTF-8 name
(sorted Ordinal). The types section is always present (count may be 0).

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests`:

```csharp
    [Fact]
    public void Chiapi_v2_round_trips_public_types()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);
        Assert.Contains("ml_sum", of.Api.Functions);
        Assert.Contains("MlPoint", of.Api.Types);
        Assert.Contains("MlCtx", of.Api.Types);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Chiapi_v2"`
Expected: FAIL — `ChibilApi.Types` does not exist (compile error).

- [ ] **Step 3: Emit v2 in `BuildChiapiBlob`**

In `chibil/CodeGen.cs`, change `BuildChiapiBlob` (read the current Plan-1 version first):
- Relax the null-guard so it emits when EITHER functions OR types are present:
  ```csharp
      if (_options.ExportApiHeaders.Count == 0 ||
          (_options.PublicApiFunctions.Count == 0 && _options.PublicApiTypes.Count == 0))
          return null;
  ```
- Change the version byte from `1` to `2`.
- After the existing function-name loop, append the types section:
  ```csharp
      var tys = new System.Collections.Generic.List<string>(_options.PublicApiTypes);
      tys.Sort(System.StringComparer.Ordinal);
      b.WriteInt32(tys.Count);
      foreach (string t in tys)
      {
          byte[] nm = System.Text.Encoding.UTF8.GetBytes(t);
          b.WriteUInt16((ushort)nm.Length); b.WriteBytes(nm);
      }
  ```
  (Confirm the options field name is `_options` — match the real BuildChiapiBlob from Plan 1.)

- [ ] **Step 4: Parse v2 in `ChibilApi`**

In `tools/chibil-link/ChibilApi.cs`, add a `Types` set and parse v2:

```csharp
    public string Group = "";
    public readonly HashSet<string> Functions = new();
    public readonly HashSet<string> Types = new();   // public struct/union/opaque tags

    public static ChibilApi Parse(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'A' || br.ReadByte() != 'P' || br.ReadByte() != 'I')
            return null;
        int version = br.ReadByte();
        if (version != 2) return null;               // current format
        var api = new ChibilApi();
        int glen = br.ReadUInt16();
        api.Group = System.Text.Encoding.UTF8.GetString(br.ReadBytes(glen));
        int nf = br.ReadInt32();
        for (int i = 0; i < nf; i++)
        {
            int len = br.ReadUInt16();
            api.Functions.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        int nt = br.ReadInt32();
        for (int i = 0; i < nt; i++)
        {
            int len = br.ReadUInt16();
            api.Types.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        return api;
    }
```

(chibil and chibil-link are built/shipped together, so requiring v2 is safe — every object
is freshly compiled by the same chibil. No v1 back-compat.)

- [ ] **Step 5: Run to verify it passes (round-trip + Plan-1 unbroken)**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"`
Expected: ALL pass — `Chiapi_v2_round_trips_public_types` is green, AND the Plan-1 tests
(`Chiapi_section_lists_public_functions_and_group_name`, `Facade_Api_...`, `Fixture_...`)
still pass because the `ml_add`-only fixtures now emit v2 with an empty types section that
the v2 parser reads back identically.

- [ ] **Step 6: Commit**

```bash
git add chibil/CodeGen.cs tools/chibil-link/ChibilApi.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: bump .chiapi to v2 with public-types section (emit + parse)"
```

---

## Task 3: Re-namespace + publicize the public types

**Files:**
- Modify: `tools/chibil-link/PeWriter.cs` (`LinkToBytes` manifest block; `PeWriter` ctor; the TypeDef-emit promotion union)
- Modify: `tools/chibil-link/MetadataMerger.cs` (api-type fields; `ApplyApiTypeNamespacing`; `ApiPublicTypeRows`)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests` (uses the same `LinkLib`-style helper; define a local link helper for the type fixture):

```csharp
    static System.Reflection.Assembly LinkTyLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true);
        return System.Reflection.Assembly.Load(pe);
    }

    [Fact]
    public void Public_struct_is_public_and_in_facade_namespace()
    {
        var asm = LinkTyLib();
        Type pt = asm.GetType("mylib.MlPoint");
        Assert.NotNull(pt);                         // re-namespaced into mylib, not global
        Assert.True(pt.IsPublic, "public struct must be public");
        Assert.True(pt.IsValueType, "struct must be a value type");
        Assert.Null(asm.GetType("MlPoint"));        // no longer in the global namespace
    }

    [Fact]
    public void Opaque_handle_is_public_and_in_facade_namespace()
    {
        var asm = LinkTyLib();
        Type ctx = asm.GetType("mylib.MlCtx");
        Assert.NotNull(ctx);                        // synthesized opaque handle, re-namespaced
        Assert.True(ctx.IsPublic && ctx.IsValueType);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_is_public|FullyQualifiedName~ApiFacadeTests.Opaque_handle"`
Expected: FAIL — `mylib.MlPoint`/`mylib.MlCtx` are null (types still in global namespace).

- [ ] **Step 3: Collect manifest types + namespace in `LinkToBytes`**

In `tools/chibil-link/PeWriter.cs`, in the existing manifest block in `LinkToBytes` (the one that sets `effectiveExportClass`/`apiFns`), also collect the types and the namespace:

```csharp
        HashSet<string> apiFns = null;
        HashSet<string> apiTypes = null;
        string apiNs = null;
        string effectiveExportClass = exportClass;
        if (effectiveExportClass == null)
        {
            string group = null;
            var fns = new HashSet<string>();
            var tys = new HashSet<string>();
            foreach (var o in objs)
                if (o.Api != null)
                {
                    group ??= o.Api.Group;
                    fns.UnionWith(o.Api.Functions);
                    tys.UnionWith(o.Api.Types);
                }
            if (group != null)
            {
                apiNs = SanitizeNs(group);
                effectiveExportClass = apiNs + ".Api";
                apiFns = fns;
                apiTypes = tys;
            }
        }
```

Pass `apiTypes` and `apiNs` to the `PeWriter` ctor (alongside `apiFns`). Add the trailing
optional ctor params on `PeWriter` and store them; pass them through to the
`MetadataMerger` ctor (parallel to `apiFunctionNames`).

- [ ] **Step 4: Add the re-namespacing pass to `MetadataMerger`**

In `tools/chibil-link/MetadataMerger.cs`, add fields + the pass:

```csharp
    private readonly HashSet<string> _apiTypeNames;  // null = no re-namespacing
    private readonly string _apiNamespace;
    // Output TypeDef rows of public-API types re-namespaced into the facade namespace.
    public readonly HashSet<int> ApiPublicTypeRows = new();

    // After the merge, move each public-API type's canonical TypeDef into the facade
    // namespace and mark it for public visibility. The linker already deduped same-named
    // value types across TUs into one CopiedTypeDef, so this is a one-row metadata edit;
    // the forwarders and <Module> methods reference it by token and are unaffected.
    public void ApplyApiTypeNamespacing()
    {
        if (_apiTypeNames == null || _apiNamespace == null) return;
        foreach (var ct in CopiedTypeDefs)
            if (ct.Namespace.Length == 0 && _apiTypeNames.Contains(ct.Name))
            {
                ct.Namespace = _apiNamespace;
                ApiPublicTypeRows.Add(ct.PredictedRow);
            }
    }
```

Store the two new ctor params into `_apiTypeNames`/`_apiNamespace` (add them as trailing
optional params on the `MetadataMerger` ctor, defaulting null).

- [ ] **Step 5: Call the pass + union the public rows in `PeWriter.Write`**

In `tools/chibil-link/PeWriter.cs` `Write()`, after `MergeAndPredict()` is called (~line 82-83) and before the TypeDef emit loop, call:

```csharp
        merger.ApplyApiTypeNamespacing();
```

Then where `promotedTypeDefRows` is built (~line 340):

```csharp
        var promotedTypeDefRows = merger.ExportTypeDefRow != 0
            ? merger.BuildExportReferencedTypeRows()
            : new System.Collections.Generic.HashSet<int>();
        promotedTypeDefRows.UnionWith(merger.ApiPublicTypeRows); // manifest-declared public types
```

(Read the actual lines; keep the existing logic and just add the `UnionWith`.)

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_is_public|FullyQualifiedName~ApiFacadeTests.Opaque_handle"`
Expected: PASS — `mylib.MlPoint` and `mylib.MlCtx` are public value types in the `mylib` namespace; no global `MlPoint`.

Regression: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests|FullyQualifiedName~ExportClassTests"`
Expected: ALL pass (the re-namespacing only runs when `_apiTypeNames != null`).

- [ ] **Step 7: Commit**

```bash
git add tools/chibil-link/PeWriter.cs tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: re-namespace + publicize public types into the facade namespace"
```

---

## Task 4: Grow the fixture + behavioral keystone

**Files:**
- Modify: `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h`
- Modify: `tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c`
- Modify: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

Grow the on-disk `mylib` to carry the struct + opaque (so Plan 3 extends the same lib),
and add a behavioral keystone that constructs a public struct value and passes it through
a forwarder — proving the re-namespaced struct is the SAME nominal type the forwarder
signature uses (a value-type mismatch would throw at invoke).

- [ ] **Step 1: Grow the fixture header**

Replace `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h` with:

```c
#ifndef MYLIB_H
#define MYLIB_H
struct MlPoint { int x; int y; };
struct MlCtx;
int ml_add(int a, int b);
int ml_sum(struct MlPoint p);
struct MlCtx *ml_ctx_new(void);
int ml_ctx_id(struct MlCtx *c);
#endif
```

- [ ] **Step 2: Grow the fixture source**

Replace `tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c` with:

```c
#include "mylib.h"
static int secret(int x){ return x * 2; }
int ml_add(int a, int b){ return a + b + secret(0); }
int ml_sum(struct MlPoint p){ return p.x + p.y; }
struct MlCtx *ml_ctx_new(void){ return (struct MlCtx*)0; }
int ml_ctx_id(struct MlCtx *c){ return c ? 1 : 0; }
```

- [ ] **Step 3: Add the behavioral keystone**

Add to `ApiFacadeTests` (uses the on-disk fixture via the existing `FixtureDir()` helper
from Plan 1; if the existing `Fixture_mylib_links_and_exposes_Api` test only checks
`ml_add`, keep it and ADD this one):

```csharp
    [Fact]
    public void Fixture_struct_value_passes_through_forwarder()
    {
        string lib = FixtureDir();
        string src = File.ReadAllText(Path.Combine(lib, "src", "mylib.c"));
        string hdr = File.ReadAllText(Path.Combine(lib, "include", "mylib.h"));
        byte[] obj = TestCompiler.CompileToObjWithApi(src, hdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));

        Type api = asm.GetType("mylib.Api");
        Type pt = asm.GetType("mylib.MlPoint");
        MethodInfo sum = api.GetMethod("ml_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(sum);
        // The forwarder's parameter must be the re-namespaced public struct, not a stray copy.
        Assert.Equal(pt, sum.GetParameters()[0].ParameterType);

        object p = Activator.CreateInstance(pt);       // boxed MlPoint
        pt.GetField("x").SetValue(p, 3);
        pt.GetField("y").SetValue(p, 4);
        Assert.Equal(7, (int)sum.Invoke(null, new object[] { p }));  // 3+4, struct passed by value
    }
```

(If the C struct fields `x`/`y` are emitted with different reflected names, adjust
`GetField("x")`/`GetField("y")` after inspecting `pt.GetFields()`; they should be public
instance fields named `x` and `y`.)

- [ ] **Step 4: Run the full sweep**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"`
Expected: ALL pass (Plan-1 tests + the new Plan-2 tests, incl. the struct-value keystone).

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ExportClassTests|FullyQualifiedName~MuslLinkTests"`
Expected: ALL pass — no regression to the explicit `--export-class` engine or the link path.

- [ ] **Step 5: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/fixtures/mylib tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: grow mylib fixture with public struct + opaque; Plan 2 complete"
```

---

## Notes for the implementer

- The manifest is bumped to v2 and `ChibilApi.Parse` now REQUIRES v2. Both chibil and chibil-link are built together, so there is no mixed-version concern; do not add v1 back-compat.
- The re-namespacing pass (`ApplyApiTypeNamespacing`) only touches a `CopiedTypeDef` whose current namespace is empty AND whose name is in the manifest type set — so it never disturbs `<Module>`, the export `Api` class, or unrelated private types. When `_apiTypeNames` is null (explicit `--export-class` or no manifest), the pass is a no-op and behavior is identical to before — guarded by the `ExportClassTests` regression.
- A public struct used by NO public function and referenced nowhere may have no `CopiedTypeDef` (chibil emits a value-type TypeDef only when the type is used). Such a type simply won't appear in the facade. Acceptable for Plan 2; do not add synthesis for unused types.
- Opaque handles (`MlCtx`) are synthesized as empty `CopiedTypeDef`s by the existing `MaybeReserveOpaque`; because they are in `CopiedTypeDefs`, the single re-namespacing pass handles them too — no separate opaque code path is needed.
- Enums are Plan 3 — do NOT record/emit/synthesize enums here.
