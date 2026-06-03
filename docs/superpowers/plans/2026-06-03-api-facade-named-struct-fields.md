# API Facade — Named struct fields (public structs)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the members of a public API struct readable/writable **by name** from C# (`p.x`, `p.y`) instead of an opaque blob — closing the field-less-struct deviation found in Plan 2.

**Architecture:** For a struct in `--export-api` (i.e. in `CompilerOptions.PublicApiTypes`), chibil emits the struct TypeDef as **ExplicitLayout** with one named `FieldDef` per eligible member (name + type signature + `FieldOffset = Member.Offset`). Because chibil's IL accesses members by raw byte offset (never by field token, `CodeGen.cs:2010`), the named fields are purely additive — the explicit offsets make a C# field read hit the exact bytes chibil's IL writes. chibil-link, which today drops member fields, is extended to **predict, carry, and emit** those member `FieldDef`s (with `AddFieldLayout`) on the canonical `CopiedTypeDef`.

**Scope (first cut):** public structs only; **scalar and pointer** members only. **Bitfield** members and **fixed-array** members get no named field (documented limitation). Private structs are unchanged.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-06-03-api-facade-design.md`. Branch: `feature/api-facade-plan2` (extends the public-types work).

---

## File Structure

- Modify `chibil/CodeGen.cs` — `MaterializeStructTypeDefs` (~1450): for public structs, emit ExplicitLayout + named member FieldDefs with FieldOffset.
- Modify `tools/chibil-link/MetadataMerger.cs` — `CopiedTypeDef` gains a member-field list; `EnsureTypeDefCopied` extracts member fields; predict their output field rows; expose for emission. Mark ExplicitLayout.
- Modify `tools/chibil-link/PeWriter.cs` — emit the carried member FieldDefs (+ `AddFieldLayout`) and set each struct TypeDef's field range + layout kind.
- Modify `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` — COFF-metadata test (T1), reflection test (T2), fixture keystone (T3).

---

## Task 1: chibil emits named member fields for public structs

**Files:**
- Modify: `chibil/CodeGen.cs` (`MaterializeStructTypeDefs`, ~line 1450-1491)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

A COFF object's metadata is readable in tests via `ObjectFile.Load(obj).Md` (a
`System.Reflection.Metadata.MetadataReader`), so chibil's emission is testable directly,
before the linker work.

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests` (it has `TyHdr`/`TySrc` with `struct MlPoint { int x; int y; };`):

```csharp
    [Fact]
    public void Public_struct_typedef_in_object_has_named_member_fields()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        var md = of.Md;
        System.Reflection.Metadata.TypeDefinitionHandle mlPoint = default;
        foreach (var h in md.TypeDefinitions)
            if (md.GetString(md.GetTypeDefinition(h).Name) == "MlPoint") { mlPoint = h; break; }
        Assert.False(mlPoint.IsNil, "MlPoint TypeDef must exist in the object");

        var td = md.GetTypeDefinition(mlPoint);
        Assert.True((td.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) != 0,
            "public struct must be ExplicitLayout so member offsets are exact");

        var names = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var fh in td.GetFields())
        {
            var fd = md.GetFieldDefinition(fh);
            string n = md.GetString(fd.Name);
            if (n == "<alignment member>") continue;       // chibil's alignment filler
            names[n] = fd.GetOffset();                      // FieldLayout offset
        }
        Assert.True(names.ContainsKey("x") && names.ContainsKey("y"), "members x and y must be named fields");
        Assert.Equal(0, names["x"]);                        // int x at offset 0
        Assert.Equal(4, names["y"]);                        // int y at offset 4
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_typedef_in_object"`
Expected: FAIL — MlPoint has no named member fields / is SequentialLayout.

- [ ] **Step 3: Emit named member fields in chibil**

In `chibil/CodeGen.cs`, in `MaterializeStructTypeDefs` (read lines ~1450-1491 first), for a
struct/union `type` whose tag name is in `_options.PublicApiTypes`:
- Choose `TypeAttributes.ExplicitLayout` for the layout attr (instead of SequentialLayout)
  so each field can carry an offset. (Unions are already ExplicitLayout.)
- After creating the TypeDef and BEFORE/around the existing `<alignment member>` emission,
  emit one named `FieldDef` per eligible member, in member order, each followed by
  `AddFieldLayout(fieldHandle, member.Offset)`:

```csharp
        bool isPublicApi = type.TagName != null && _options.PublicApiTypes.Contains(type.TagName);
        // ... existing AddTypeDefinition with layoutAttr (ExplicitLayout when isPublicApi || union) ...
        if (isPublicApi)
        {
            for (Member m = type.Members; m != null; m = m.Next)
            {
                if (m.IsBitfield) continue;                         // not separately addressable
                if (m.Ty.Kind == TypeKind.Array) continue;          // fixed arrays: first-cut skip
                if (m.Name == null) continue;                       // anonymous member
                var msig = new BlobBuilder();
                msig.WriteByte(0x06);                               // FIELD
                EncodeType(msig, m.Ty);                             // existing type-sig encoder
                var fh = _md.AddFieldDefinition(
                    FieldAttributes.Public,
                    _md.GetOrAddString(Util.GetTokenText(m.Name)),
                    _md.GetOrAddBlob(msig));
                _nextFieldRow++;
                _md.AddFieldLayout(fh, m.Offset);
            }
        }
```

Place this so the member fields are part of the struct's contiguous field range (the
TypeDef's `FieldList` start was set to `_nextFieldRow` at AddTypeDefinition time — the
member fields and the alignment member must be the fields that follow, in row order).
READ the existing alignment-member block (~1472-1491) and emit the member fields in the
same contiguous region (member fields first, then the alignment member, OR alignment
member first then members — whichever keeps the field range contiguous and the row
counter consistent; the test only checks names/offsets, not order). For ExplicitLayout the
alignment member also needs an offset — give it `AddFieldLayout(alignField, 0)` (as unions
already do).

Confirm: `_options` is CodeGen's options field; `EncodeType`, `Util.GetTokenText`,
`_nextFieldRow`, `AddFieldLayout` all exist (see `RegisterGlobalField` ~1373 and the
alignment-member code for the exact call shapes).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_typedef_in_object"`
Expected: PASS — MlPoint is ExplicitLayout with public fields x@0, y@4.

Also: `dotnet build chibil/chibil.csproj -c Debug` → succeeds; and
`dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.CompileToObj_accepts"` → still PASS (codegen not broken).

- [ ] **Step 5: Commit**

```bash
git add chibil/CodeGen.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: emit named member fields (ExplicitLayout) for public API structs"
```

---

## Task 2: chibil-link carries the member fields into the assembly

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs` (`CopiedTypeDef` ~132; `EnsureTypeDefCopied` ~2084; field-row prediction)
- Modify: `tools/chibil-link/PeWriter.cs` (field emit + TypeDef field-range/layout, ~234-379)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

The linker today copies only (name, ns, size) for struct TypeDefs and emits them with an
empty field range. Extend it to extract, predict rows for, and emit the member fields.

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests`:

```csharp
    [Fact]
    public void Public_struct_fields_are_named_and_readable_in_the_assembly()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        Type pt = asm.GetType("mylib.MlPoint");
        Assert.NotNull(pt);
        FieldInfo fx = pt.GetField("x", BindingFlags.Public | BindingFlags.Instance);
        FieldInfo fy = pt.GetField("y", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(fx);
        Assert.NotNull(fy);
        Assert.Equal(typeof(int), fx.FieldType);

        // Construct via named fields (no Marshal) and pass through the forwarder.
        object p = Activator.CreateInstance(pt);
        fx.SetValue(p, 3);
        fy.SetValue(p, 4);
        MethodInfo sum = asm.GetType("mylib.Api").GetMethod("ml_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(7, (int)sum.Invoke(null, new object[] { p }));   // proves field offsets match the IL
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_fields_are_named"`
Expected: FAIL — `mylib.MlPoint` has no `x`/`y` fields (the linker dropped them).

- [ ] **Step 3: Carry member fields on `CopiedTypeDef`**

In `tools/chibil-link/MetadataMerger.cs`, extend `CopiedTypeDef` (~line 132):

```csharp
    public sealed class CopiedTypeDef
    {
        public string Name;
        public string Namespace;
        public EntityHandle BaseType;
        public int LayoutSize;
        public int LayoutPack;
        public int PredictedRow;
        public bool ExplicitLayout;                 // true → emit ExplicitLayout + per-field offsets
        public List<MemberField> Members = new();   // named member fields (may be empty)
        public int FirstFieldRow;                   // predicted output field row of Members[0], or 0
    }

    public sealed class MemberField
    {
        public string Name;
        public BlobHandle Signature;   // output-blob field signature (tokens already mapped)
        public int Offset;             // FieldLayout offset
    }
```

- [ ] **Step 4: Extract member fields in `EnsureTypeDefCopied`**

In `EnsureTypeDefCopied` (~line 2084), after building the `CopiedTypeDef`, read the source
TypeDef's fields and copy the named members (skip `<alignment member>`). Re-emit each field
signature through the existing signature rewriter so any type tokens are remapped (scalars
have none, but this is future-proof). Read how `RewriteMethodSignature`/`EcmaSignatureRewriter`
map tokens and mirror it for a FIELD signature:

```csharp
        var srcTd = md.GetTypeDefinition(inH);
        if ((srcTd.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) != 0)
        {
            copied.ExplicitLayout = true;
            foreach (var fh in srcTd.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                string fn = md.GetString(fd.Name);
                if (fn == "<alignment member>") continue;
                var sr = md.GetBlobReader(fd.Signature);
                var ob = new BlobBuilder();
                EcmaSignatureRewriter.RewriteFieldSignature(sr, _maps[of], ob);   // see note below
                copied.Members.Add(new MemberField {
                    Name = fn,
                    Signature = Builder.GetOrAddBlob(ob),
                    Offset = fd.GetOffset(),
                });
            }
        }
```

NOTE on the field-signature rewriter: `EcmaSignatureRewriter` already rewrites method
signatures (used by `RewriteMethodSignature`). If it lacks a `RewriteFieldSignature`, add a
minimal one: a FIELD sig is `0x06` then a single Type — read the `0x06` header byte, write
it, then call the same per-Type rewrite the method rewriter uses for one type. (For scalar
members the Type is a primitive element-type byte with no token, so even a verbatim blob
copy works; but route through the rewriter so nested-struct members work later.)

- [ ] **Step 5: Predict member field rows**

The output field table is `[<Module> global/RVA fields ...][struct member fields ...]`.
After the existing global-field prediction completes (find where `TotalFieldRows` / the
global field rows are predicted in `MergeAndPredict`), append a pass that walks
`CopiedTypeDefs` in `PredictedRow` order and assigns each `MemberField` a consecutive output
field row, recording `copied.FirstFieldRow` = the first member's row (0 if no members). The
struct member fields thus occupy a contiguous block after the global fields, in TypeDef-row
order — exactly what TypeDef field-range semantics require. Expose the new total field count
(globals + all members) for PeWriter's row assertions.

- [ ] **Step 6: Emit member fields + set field range/layout in PeWriter**

In `tools/chibil-link/PeWriter.cs`:
- In the field-emit phase (~234-269), AFTER emitting the global/RVA fields, emit each
  `CopiedTypeDef`'s member fields in TypeDef-row order:
  ```csharp
          foreach (var ct in merger.CopiedTypeDefs)
              foreach (var mf in ct.Members)
              {
                  var fh = mdBuilder.AddFieldDefinition(
                      System.Reflection.FieldAttributes.Public,
                      mdBuilder.GetOrAddString(mf.Name), mf.Signature);
                  mdBuilder.AddFieldLayout(fh, mf.Offset);
              }
  ```
- In the TypeDef-emit loop (~362), set the layout attribute and the field-range start:
  ```csharp
          var tdH = mdBuilder.AddTypeDefinition(
              (ct.ExplicitLayout ? System.Reflection.TypeAttributes.ExplicitLayout
                                 : System.Reflection.TypeAttributes.SequentialLayout)
                  | System.Reflection.TypeAttributes.Sealed | System.Reflection.TypeAttributes.AnsiClass
                  | (promotedTypeDefRows.Contains(ct.PredictedRow) ? System.Reflection.TypeAttributes.Public : 0),
              ct.Namespace.Length == 0 ? default : mdBuilder.GetOrAddString(ct.Namespace),
              mdBuilder.GetOrAddString(ct.Name),
              ct.BaseType,
              MetadataTokens.FieldDefinitionHandle(ct.Members.Count > 0 ? ct.FirstFieldRow : totalGlobalFields + 1),
              MetadataTokens.MethodDefinitionHandle(totalMethods + 1));
          if (ct.LayoutSize >= 0) mdBuilder.AddTypeLayout(tdH, (ushort)ct.LayoutPack, (uint)ct.LayoutSize);
  ```
  (Match the real variable names for the field/method totals; the key change is the
  field-range start and the layout attribute.)

IMPORTANT: TypeDef field ranges are derived from each TypeDef's `FieldList` and the next
TypeDef's `FieldList`. Ensure `<Module>` (row 1) still starts at field row 1, the global
fields occupy rows `1..G`, and struct member fields occupy `G+1..` in TypeDef order, so each
struct's `FirstFieldRow` is correct and the ranges are contiguous and non-overlapping. Verify
the `AssertRow`/row-prediction assertions still hold.

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_struct_fields_are_named"`
Expected: PASS — `mylib.MlPoint.x/.y` are public int fields; setting them and invoking
`ml_sum` returns 7 (proving the field offsets match chibil's IL offsets).

Regression: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests|FullyQualifiedName~ExportClassTests"`
Expected: ALL pass (private structs unchanged; only ExplicitLayout public structs carry fields).

- [ ] **Step 8: Commit**

```bash
git add tools/chibil-link/MetadataMerger.cs tools/chibil-link/PeWriter.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: carry named member fields for public structs into the assembly"
```

---

## Task 3: Fixture keystone (no Marshal) + full regression

**Files:**
- Modify: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

The on-disk `mylib` fixture already has `struct MlPoint { int x; int y; }` (from Plan 2).
Replace the `Marshal`-based struct-value keystone with a clean named-field version, and run
the full sweep including the real-program suites to confirm ExplicitLayout public structs
don't break anything downstream.

- [ ] **Step 1: Replace the keystone body**

Find `Fixture_struct_value_passes_through_forwarder` in `ApiFacadeTests.cs` (Plan 2 wrote it
with a `GCHandle`/`Marshal` workaround). Replace its struct-construction with named fields:

```csharp
        Type pt = asm.GetType("mylib.MlPoint");
        object p = Activator.CreateInstance(pt);
        pt.GetField("x", BindingFlags.Public | BindingFlags.Instance).SetValue(p, 3);
        pt.GetField("y", BindingFlags.Public | BindingFlags.Instance).SetValue(p, 4);
        MethodInfo sum = api.GetMethod("ml_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(7, (int)sum.Invoke(null, new object[] { p }));
```

(Keep the `Assert.Equal(pt, sum.GetParameters()[0].ParameterType)` nominal-identity check.)

- [ ] **Step 2: Run the full sweep**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ExportClassTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~MuslLinkTests"` → pass (some may early-return without WSL).

- [ ] **Step 3: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: read public struct fields by name in keystone (no Marshal)"
```

---

## Notes for the implementer

- The single highest-risk property: a C# read of `p.x` must hit the SAME byte the chibil IL wrote. That is guaranteed only if the named field's `FieldOffset` equals `Member.Offset` (chibil's parser-computed offset) AND the struct is ExplicitLayout (so the JIT honors the offset rather than auto-packing). The `ml_sum → 7` assertion is the end-to-end guard; do not weaken it.
- ExplicitLayout value types require EVERY field to have a `FieldLayout` offset — including chibil's `<alignment member>` filler. Give it offset 0 (the union code already does this). If a public struct has bitfield/array members that you skip, the struct may be smaller-in-fields than its `ClassLayout` size — that is fine: `ClassLayout(size)` pads the type to the correct size regardless of which members are named.
- Field-row prediction is the core linker change: the merger must allocate output field rows for struct members AFTER the global fields, in TypeDef-row order, so the contiguous-range invariant holds. If the existing code has a `TotalFieldRows`/field-count used in `AddTypeDefinition` field-range starts and in row assertions, update it to include member fields.
- Do NOT touch private structs: gate emission on `PublicApiTypes` (chibil) and on the source TypeDef being ExplicitLayout-with-named-members (linker). Existing SequentialLayout opaque structs must round-trip exactly as before (guarded by ExportClassTests + MuslLinkTests).
- Bitfields and fixed arrays are explicitly out of this first cut — skip those members; they simply won't be named. Note it; don't half-emit them.
