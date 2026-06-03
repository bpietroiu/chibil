# API Facade — Plan 3: Enums (real enum types + threaded into function signatures)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Expose C enums declared in `--export-api` headers as real C# `enum` types in the facade (named constants, correct underlying type), and thread the enum type into the public **function signatures** so consumers write `mylib.Api.ml_color_code(mylib.MlColor.ML_BLUE)`.

**Architecture:** C enums lower to `int` and have no TypeDef, so (a) chibil captures each public enum's tag/underlying/enumerators AND the enum usages on public function params/returns at parse time, recording them in the `.chiapi` manifest (bumped to v3); (b) chibil-link **synthesizes** an enum TypeDef (`: System.Enum`, a `value__` instance field, one static-literal const field per enumerator via `AddConstant`) reusing the `CopiedTypeDef`/member-field infra from the named-fields work; (c) the `Api` forwarder signatures substitute `int32`→the synthesized enum TypeDef at the annotated positions, relying on enum↔underlying interchangeability (the forwarder body still calls the `int`-typed `<Module>` method).

**Scope (this plan):** enum type synthesis + threading into **function** signatures + the keystone. **Struct-field enum threading is a noted follow-on** (the same substitution mechanism applied to member-field signatures) — NOT in this plan.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-06-03-api-facade-design.md`. Branch: `feature/api-facade-plan3-enums`. Builds on Plans 1+2 (merged).

The enum fixture used through the plan (inline consts in tests; on-disk in the last task):
```c
// header
enum MlColor { ML_RED, ML_GREEN = 5, ML_BLUE };
int ml_color_code(enum MlColor c);
enum MlColor ml_default_color(void);
```
```c
// src
#include "mylib.h"
int ml_color_code(enum MlColor c){ return (int)c + 100; }
enum MlColor ml_default_color(void){ return ML_GREEN; }
```

---

## File Structure

- Modify `chibil/ChibiTypes.cs` — `CompilerOptions` gains `PublicApiEnums` (list of enum defs) and `PublicApiEnumUsages` (function param/return enum annotations).
- Modify `chibil/Parser.cs` — `EnumSpecifier` records public enum defs; the function-prototype path records enum usages on params/returns.
- Modify `chibil/CodeGen.cs` — `BuildChiapiBlob`: bump to v3, emit the enum + usage sections.
- Modify `tools/chibil-link/ChibilApi.cs` — parse v3 (enum defs + usages).
- Modify `tools/chibil-link/MetadataMerger.cs` — `MemberField` gains literal/attribute/constant support; synthesize enum `CopiedTypeDef`s; reserve their rows; expose an enum name→TypeDef-row map.
- Modify `tools/chibil-link/PeWriter.cs` — emit literal const fields + `AddConstant`; emit `value__`; the enum TypeDef's base/layout.
- Modify `tools/asm2obj/EcmaSignatureRewriter.cs` — an injector (or a method-sig rewrite variant) that substitutes a type at given positions with a chosen TypeDef token.
- Modify `tools/chibil-link/ForwarderSynthesizer.cs` — build forwarder signatures with enum substitution at annotated positions.
- Modify `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` and the `mylib` fixture.

---

## Task 1: chibil captures public enums + function enum usages

**Files:**
- Modify: `chibil/ChibiTypes.cs` (`CompilerOptions`)
- Modify: `chibil/Parser.cs` (`EnumSpecifier` ~519; the function-prototype param/return path)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Add the options data structures**

In `chibil/ChibiTypes.cs`, add to `CompilerOptions` (after `PublicApiTypes`):

```csharp
    // Public enums declared in --export-api headers: tag, unsigned-ness, and enumerators.
    public List<ApiEnum> PublicApiEnums = new();
    // Enum usage on a public function: which param/return position is which enum tag.
    public List<ApiEnumUse> PublicApiEnumUsages = new();
```

And add these small record-like classes near `CompilerOptions` (top-level in the same file):

```csharp
public sealed class ApiEnum
{
    public string Tag;
    public bool IsUnsigned;
    public List<(string Name, int Value)> Members = new();
}
public sealed class ApiEnumUse
{
    public string Function;   // function name
    public int Position;      // 0 = return type, 1..N = parameter index
    public string EnumTag;    // the enum tag at that position
}
```

- [ ] **Step 2: Write the failing test**

Add to `ApiFacadeTests`:

```csharp
    const string EnHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "enum MlColor { ML_RED, ML_GREEN = 5, ML_BLUE };\n" +
        "int ml_color_code(enum MlColor c);\n" +
        "enum MlColor ml_default_color(void);\n" +
        "#endif\n";
    const string EnSrc =
        "#include \"mylib.h\"\n" +
        "int ml_color_code(enum MlColor c){ return (int)c + 100; }\n" +
        "enum MlColor ml_default_color(void){ return ML_GREEN; }\n";

    [Fact]
    public void Public_enum_and_usages_are_captured()
    {
        var opts = TestCompiler.CompileAndReturnOptions(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var e = opts.PublicApiEnums.Find(x => x.Tag == "MlColor");
        Assert.NotNull(e);
        Assert.Equal(0, e.Members.Find(m => m.Name == "ML_RED").Value);
        Assert.Equal(5, e.Members.Find(m => m.Name == "ML_GREEN").Value);
        Assert.Equal(6, e.Members.Find(m => m.Name == "ML_BLUE").Value);   // auto-increment after 5
        // ml_color_code: param 1 is MlColor; ml_default_color: return (pos 0) is MlColor
        Assert.Contains(opts.PublicApiEnumUsages, u => u.Function == "ml_color_code" && u.Position == 1 && u.EnumTag == "MlColor");
        Assert.Contains(opts.PublicApiEnumUsages, u => u.Function == "ml_default_color" && u.Position == 0 && u.EnumTag == "MlColor");
    }
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_enum_and_usages"`
Expected: FAIL — fields don't exist (compile error).

- [ ] **Step 4: Capture enum defs in EnumSpecifier**

In `chibil/Parser.cs` `EnumSpecifier` (~519), when the enum has a tag from an `--export-api`
header AND a body is being parsed (the definition form, not a bare reference), collect the
enumerators as they are parsed. Read lines 519-556: the enumerator loop computes
`name`/`val` at ~549-551. Accumulate into a local list, and after the loop, if
`IsFromExportApiHeader(tag)` and the enum was defined here (has a body), record:

```csharp
        // inside the body-parsing branch, build `var enumerators = new List<(string,int)>();`
        // and in the enumerator loop add: enumerators.Add((name, val));   // val BEFORE val++
        // after the loop:
        if (tag != null && IsFromExportApiHeader(tag))
            _options.PublicApiEnums.Add(new ApiEnum {
                Tag = Util.GetTokenText(tag), IsUnsigned = ty.IsUnsigned, Members = enumerators });
```

Match the real variable names (`name`, `val`, `ty`, `tag`, `_options`). Record the value
BEFORE the `val++` so ML_RED=0, ML_GREEN=5, ML_BLUE=6. Guard against double-recording a
re-opened tag (only record on the definition with `{ ... }`).

- [ ] **Step 5: Capture enum usages on public function prototypes**

In `chibil/Parser.cs`, find where a top-level function declarator's type (return + params)
is known together with the function name (near the public-FUNCTION tagging added in Plan 1,
in `Function()` ~1894 where `nameStr` and the function `CType ty` are in hand). When the
function is from an export-api header (the existing `IsFromExportApiHeader` check there),
inspect its return type and each parameter type; for any that is `TypeKind.Enum` with a tag,
record an `ApiEnumUse`:

```csharp
        if (IsFromExportApiHeader(ty.Name))   // (the existing public-function condition)
        {
            // return type = position 0
            if (ty.ReturnTy != null && ty.ReturnTy.Kind == TypeKind.Enum && ty.ReturnTy.TagName != null)
                _options.PublicApiEnumUsages.Add(new ApiEnumUse { Function = nameStr, Position = 0, EnumTag = ty.ReturnTy.TagName });
            int pi = 1;
            for (CType p = ty.ParamList; p != null; p = p.Next, pi++)   // match the real param-list field
                if (p.Kind == TypeKind.Enum && p.TagName != null)
                    _options.PublicApiEnumUsages.Add(new ApiEnumUse { Function = nameStr, Position = pi, EnumTag = p.TagName });
        }
```

READ the `Function()` method and the `CType` for a function: confirm the field names for the
return type (`ReturnTy`?) and the parameter list (`Params`/`ParamList`?), and how to iterate
params in order. Match them. (The plan's field names are illustrative.)

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_enum_and_usages"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add chibil/ChibiTypes.cs chibil/Parser.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: capture public enum definitions + function enum usages"
```

---

## Task 2: `.chiapi` v3 — emit + parse enums and usages (atomic)

**Files:**
- Modify: `chibil/CodeGen.cs` (`BuildChiapiBlob`)
- Modify: `tools/chibil-link/ChibilApi.cs`
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

Atomic version bump (v2→v3): the v2 parser rejects v3, so emit and parse change together.
v3 = v2 layout (version byte 3) + two new sections after the types section:
- **Enums:** Int32 count; per enum: UInt16 len+UTF-8 tag, 1 byte unsigned-flag, Int32 member-count, per member UInt16 len+UTF-8 name + Int32 value.
- **Usages:** Int32 count; per usage: UInt16 len+UTF-8 function name, Int32 position, UInt16 len+UTF-8 enum tag.

- [ ] **Step 1: Write the failing round-trip test**

```csharp
    [Fact]
    public void Chiapi_v3_round_trips_enums_and_usages()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);
        var e = of.Api.Enums.Find(x => x.Tag == "MlColor");
        Assert.NotNull(e);
        Assert.Equal(6, e.Members.Find(m => m.Name == "ML_BLUE").Value);
        Assert.Contains(of.Api.EnumUsages, u => u.Function == "ml_color_code" && u.Position == 1 && u.EnumTag == "MlColor");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Chiapi_v3"`
Expected: FAIL — `of.Api.Enums` doesn't exist.

- [ ] **Step 3: Emit v3 in `BuildChiapiBlob`**

In `chibil/CodeGen.cs` `BuildChiapiBlob`: relax the null-guard to also emit when
`PublicApiEnums.Count > 0`; change the version byte to `3`; after the existing types-section
loop, append:

```csharp
    var enums = _options.PublicApiEnums;   // keep insertion order (or sort by Tag for determinism)
    b.WriteInt32(enums.Count);
    foreach (var e in enums)
    {
        byte[] tg = System.Text.Encoding.UTF8.GetBytes(e.Tag);
        b.WriteUInt16((ushort)tg.Length); b.WriteBytes(tg);
        b.WriteByte(e.IsUnsigned ? (byte)1 : (byte)0);
        b.WriteInt32(e.Members.Count);
        foreach (var (mn, mv) in e.Members)
        {
            byte[] nm = System.Text.Encoding.UTF8.GetBytes(mn);
            b.WriteUInt16((ushort)nm.Length); b.WriteBytes(nm);
            b.WriteInt32(mv);
        }
    }
    var uses = _options.PublicApiEnumUsages;
    b.WriteInt32(uses.Count);
    foreach (var u in uses)
    {
        byte[] fn = System.Text.Encoding.UTF8.GetBytes(u.Function);
        b.WriteUInt16((ushort)fn.Length); b.WriteBytes(fn);
        b.WriteInt32(u.Position);
        byte[] et = System.Text.Encoding.UTF8.GetBytes(u.EnumTag);
        b.WriteUInt16((ushort)et.Length); b.WriteBytes(et);
    }
```

- [ ] **Step 4: Parse v3 in `ChibilApi`**

In `tools/chibil-link/ChibilApi.cs`: add types mirroring chibil's, change the version gate to
`!= 3`, and parse the two new sections after the types loop:

```csharp
    public sealed class ApiEnum { public string Tag = ""; public bool IsUnsigned;
        public readonly List<(string Name, int Value)> Members = new(); }
    public sealed class ApiEnumUse { public string Function = ""; public int Position; public string EnumTag = ""; }
    public readonly List<ApiEnum> Enums = new();
    public readonly List<ApiEnumUse> EnumUsages = new();
    // ... in Parse, after the types loop:
    int ne = br.ReadInt32();
    for (int i = 0; i < ne; i++)
    {
        var e = new ApiEnum();
        e.Tag = ReadStr(br); e.IsUnsigned = br.ReadByte() != 0;
        int nm = br.ReadInt32();
        for (int j = 0; j < nm; j++) { string n = ReadStr(br); int v = br.ReadInt32(); e.Members.Add((n, v)); }
        api.Enums.Add(e);
    }
    int nu = br.ReadInt32();
    for (int i = 0; i < nu; i++)
        api.EnumUsages.Add(new ApiEnumUse { Function = ReadStr(br), Position = br.ReadInt32(), EnumTag = ReadStr(br) });
```

Add a small `static string ReadStr(BinaryReader br)` helper (UInt16 len + UTF-8) and reuse it
for the existing group/function/type reads too if convenient.

- [ ] **Step 5: Run to verify PASS + Plan-1/2 unbroken**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"`
Expected: ALL pass — v3 round-trips enums/usages, and the function/type round-trips from
Plans 1-2 still pass (the `ml_add`/`MlPoint` fixtures now emit v3 with empty enum/usage
sections that the v3 parser reads back identically).

- [ ] **Step 6: Commit**

```bash
git add chibil/CodeGen.cs tools/chibil-link/ChibilApi.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: bump .chiapi to v3 with enum definitions + usages (emit + parse)"
```

---

## Task 3: chibil-link synthesizes enum TypeDefs (named constants)

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs` (`MemberField` extension; `GetOrAddCoreEnumRef`; enum synthesis; row reservation; enum name→row map)
- Modify: `tools/chibil-link/PeWriter.cs` (emit literal const fields + `AddConstant`; value__)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    static System.Reflection.Assembly LinkEnLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        return System.Reflection.Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
    }

    [Fact]
    public void Public_enum_is_synthesized_as_a_real_enum_type()
    {
        var asm = LinkEnLib();
        Type t = asm.GetType("mylib.MlColor");
        Assert.NotNull(t);
        Assert.True(t.IsEnum, "must be a real CLR enum");
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(t));
        Assert.Equal(5, (int)Enum.Parse(t, "ML_GREEN"));
        Assert.Equal(6, (int)Enum.Parse(t, "ML_BLUE"));
        Assert.True(t.IsPublic);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_enum_is_synthesized"`
Expected: FAIL — `mylib.MlColor` is null.

- [ ] **Step 3: Extend `MemberField` for literals**

In `tools/chibil-link/MetadataMerger.cs`, extend `MemberField`:

```csharp
    public sealed class MemberField
    {
        public string Name;
        public BlobHandle Signature;
        public int Offset;                                   // used only when HasLayout
        public bool HasLayout = true;                        // false for enum literal/value__ (no FieldLayout)
        public System.Reflection.FieldAttributes Attributes  // default Public for struct members
            = System.Reflection.FieldAttributes.Public;
        public bool IsLiteral;                               // emit an AddConstant row
        public int LiteralValue;
    }
```

The existing struct-member emission sets only `Name`/`Signature`/`Offset` (so `HasLayout`
defaults true, `Attributes` Public, `IsLiteral` false) — unchanged behavior.

- [ ] **Step 4: Add `GetOrAddCoreEnumRef` + synthesize enum CopiedTypeDefs**

Add `private EntityHandle GetOrAddCoreEnumRef() => GetOrAddCoreTypeRef("System", "Enum");`

Add a synthesis pass (call it from `MergeAndPredict` AFTER `ReserveOpaqueTypeDefs` and BEFORE
`ReserveMemberFields`, so the synthesized enum TypeDefs get predicted rows and their fields
get reserved with the others). For each manifest enum (aggregate `o.Api.Enums` across objects,
dedup by Tag), allocate a TypeDef row and a `CopiedTypeDef`:

```csharp
    // _apiEnums/_apiNamespace passed via ctor (see Task threading); build a name→row map.
    public readonly Dictionary<string,int> ApiEnumRow = new();   // enum tag -> output TypeDef row
    private void SynthesizeApiEnums()
    {
        if (_apiEnums == null) return;
        foreach (var e in _apiEnums)
        {
            _outTypeDefRow++;
            var ct = new CopiedTypeDef {
                Name = e.Tag, Namespace = _apiNamespace ?? "",
                BaseType = GetOrAddCoreEnumRef(),
                LayoutSize = -1, LayoutPack = 0, PredictedRow = _outTypeDefRow,
                ExplicitLayout = false,
            };
            // value__ : instance field of the underlying primitive (RTSpecialName|SpecialName).
            var vsig = new BlobBuilder(); vsig.WriteByte(0x06);
            vsig.WriteByte(e.IsUnsigned ? (byte)0x09 /*U4*/ : (byte)0x08 /*I4*/);
            ct.Members.Add(new MemberField {
                Name = "value__", Signature = Builder.GetOrAddBlob(vsig), HasLayout = false,
                Attributes = System.Reflection.FieldAttributes.Public
                    | System.Reflection.FieldAttributes.SpecialName
                    | System.Reflection.FieldAttributes.RTSpecialName });
            // one static-literal const field per enumerator, typed AS THE ENUM (valuetype self-token).
            var selfTok = MetadataTokens.TypeDefinitionHandle(_outTypeDefRow);
            foreach (var (mn, mv) in e.Members)
            {
                var fsig = new BlobBuilder(); fsig.WriteByte(0x06);
                fsig.WriteByte(0x11 /*ELEMENT_TYPE_VALUETYPE*/);
                fsig.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(selfTok));
                ct.Members.Add(new MemberField {
                    Name = mn, Signature = Builder.GetOrAddBlob(fsig), HasLayout = false, IsLiteral = true, LiteralValue = mv,
                    Attributes = System.Reflection.FieldAttributes.Public
                        | System.Reflection.FieldAttributes.Static
                        | System.Reflection.FieldAttributes.Literal
                        | System.Reflection.FieldAttributes.HasDefault });
            }
            CopiedTypeDefs.Add(ct);
            ApiEnumRow[e.Tag] = ct.PredictedRow;
            ApiPublicTypeRows.Add(ct.PredictedRow);   // make the enum public
        }
    }
```

(`CodedIndex.TypeDefOrRefOrSpec` + `WriteCompressedInteger` is how a value-type token is
encoded in a signature — confirm the helper names against `System.Reflection.Metadata.Ecma335`
and the existing signature-building code. `ReserveMemberFields` already allocates field rows
for all `CopiedTypeDef.Members`, so the enum's value__ + const fields get rows automatically.)

- [ ] **Step 5: Emit literal fields + value__ + AddConstant in PeWriter**

In `tools/chibil-link/PeWriter.cs`, in the member-field emit loop (~353), honor the new
`MemberField` fields:

```csharp
        foreach (var ct in merger.CopiedTypeDefs)
        {
            bool firstMember = true;
            foreach (var mf in ct.Members)
            {
                var mfh = mdBuilder.AddFieldDefinition(mf.Attributes, mdBuilder.GetOrAddString(mf.Name), mf.Signature);
                if (firstMember) { AssertRow(ct.FirstFieldRow, MetadataTokens.GetRowNumber(mfh), $"field '{ct.Name}.{mf.Name}'"); firstMember = false; }
                if (mf.HasLayout) mdBuilder.AddFieldLayout(mfh, mf.Offset);
                if (mf.IsLiteral) mdBuilder.AddConstant(mfh, mf.LiteralValue);
            }
        }
```

In the TypeDef-emit loop (~390), the enum `CopiedTypeDef` already has `BaseType = System.Enum`,
`ExplicitLayout = false` → SequentialLayout, `LayoutSize = -1` → no `AddTypeLayout`. It is in
`promotedTypeDefRows` (via `ApiPublicTypeRows`) → Public. No special-casing needed beyond
honoring `mf.Attributes`/`IsLiteral`/`HasLayout` above.

Thread `_apiEnums` (aggregated `o.Api.Enums`) and the existing `_apiNamespace` into the merger
(parallel to `_apiTypeNames`), and call `SynthesizeApiEnums()` at the right point in
`MergeAndPredict`.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_enum_is_synthesized"`
Expected: PASS — `mylib.MlColor` is a real public enum, underlying int, ML_GREEN=5, ML_BLUE=6.
If `Assembly.Load` throws, check the value__/literal field signatures, the Enum base TypeRef,
and the FirstFieldRow accounting for the synthesized fields.

Regression: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests|FullyQualifiedName~ExportClassTests"` → ALL pass.

- [ ] **Step 7: Commit**

```bash
git add tools/chibil-link/MetadataMerger.cs tools/chibil-link/PeWriter.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: synthesize public enum types with named constants"
```

---

## Task 4: Thread enums into function signatures + interchangeability keystone

**Files:**
- Modify: `tools/asm2obj/EcmaSignatureRewriter.cs` (position-aware enum substitution)
- Modify: `tools/chibil-link/ForwarderSynthesizer.cs` (use it for the annotated positions)
- Modify: `tools/chibil-link/MetadataMerger.cs` (pass the usage map + enum-row map to the forwarder builder)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

The forwarder body still calls the `int`-typed `<Module>` method; only the forwarder's
**signature** advertises the enum. Enum↔underlying interchangeability makes the IL
(`ldarg` enum → `call` int32 → `ret` enum) verify and run.

- [ ] **Step 1: Write the failing keystone test**

```csharp
    [Fact]
    public void Forwarder_signature_uses_enum_and_runs()
    {
        var asm = LinkEnLib();
        Type api = asm.GetType("mylib.Api");
        Type mc = asm.GetType("mylib.MlColor");
        MethodInfo code = api.GetMethod("ml_color_code", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(code);
        Assert.Equal(mc, code.GetParameters()[0].ParameterType);   // param threaded to the enum
        MethodInfo def = api.GetMethod("ml_default_color", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(mc, def.ReturnType);                          // return threaded to the enum

        object blue = Enum.Parse(mc, "ML_BLUE");                   // 6
        Assert.Equal(106, (int)code.Invoke(null, new object[] { blue }));  // enum arg → int callee → runs
        object dc = def.Invoke(null, null);
        Assert.Equal(5, (int)dc);                                  // ML_GREEN
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Forwarder_signature_uses_enum"`
Expected: FAIL — the param/return types are `int`, not `mylib.MlColor`.

- [ ] **Step 3: Add position-aware enum substitution to the rewriter**

In `tools/asm2obj/EcmaSignatureRewriter.cs`, add a method-signature rewrite that, given a map
`position → TypeDefHandle` (position 0 = return, 1..N = params), emits `VALUETYPE <enumtoken>`
at those positions instead of copying the source type. Read the existing
`RewriteMethodSignature(int count, ReturnTypeEncoder, ParametersEncoder)` (~291) and the
`_injector?.BeginParameter(i)` position hooks. Implement either:
- an `ISignatureInjector`-style object that overrides the type at a position, OR
- a new `RewriteMethodSignatureWithEnums(BlobReader, TokenMap, BlobBuilder, IReadOnlyDictionary<int,int> posToEnumRow)` that, at each position present in the map, SKIPS the source type (advance the reader past it) and writes `valuetype <row>` via the encoder; otherwise rewrites normally.

Provide a public entry the linker can call. Keep the existing `RewriteMethodSignature`
unchanged for non-enum forwarders.

- [ ] **Step 4: Use it in ForwarderSynthesizer**

In `tools/chibil-link/ForwarderSynthesizer.cs`, for each exported function being forwarded,
look up its enum usages (from a `Dictionary<string, Dictionary<int,int>>` = function name →
(position → enum TypeDef row) built by the merger from `EnumUsages` + `ApiEnumRow`). If the
function has usages, build its signature via the enum-aware rewriter; else use the existing
`RewriteMethodSignature`. The forwarder IL (`ldarg…; call <module fn>; ret`) is UNCHANGED —
only the advertised signature differs. Pass the usage/row maps from the merger.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Forwarder_signature_uses_enum"`
Expected: PASS — `ml_color_code(mylib.MlColor)` and `ml_default_color() -> mylib.MlColor`;
invoking with `ML_BLUE` returns 106; `ml_default_color()` returns 5. This is the
interchangeability keystone: the enum-typed forwarder JITs and runs against the int callee.
If it throws `InvalidProgramException`, the enum↔int interchange needs an explicit (no-op)
representation match — inspect the IL/signature and adjust (but first confirm the signatures
are well-formed).

Regression: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests|FullyQualifiedName~ExportClassTests"` → ALL pass.

- [ ] **Step 6: Commit**

```bash
git add tools/asm2obj/EcmaSignatureRewriter.cs tools/chibil-link/ForwarderSynthesizer.cs tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: thread public enums into Api forwarder signatures"
```

---

## Task 5: Grow the fixture + full regression

**Files:**
- Modify: `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h`, `.../src/mylib.c`
- Modify: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Grow the fixture**

Append to `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h` (before `#endif`):

```c
enum MlColor { ML_RED, ML_GREEN = 5, ML_BLUE };
int ml_color_code(enum MlColor c);
enum MlColor ml_default_color(void);
```

Append to `tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c`:

```c
int ml_color_code(enum MlColor c){ return (int)c + 100; }
enum MlColor ml_default_color(void){ return ML_GREEN; }
```

- [ ] **Step 2: Add a fixture keystone**

```csharp
    [Fact]
    public void Fixture_enum_threads_through_facade()
    {
        string lib = FixtureDir();
        byte[] obj = TestCompiler.CompileToObjWithApi(
            File.ReadAllText(Path.Combine(lib, "src", "mylib.c")),
            File.ReadAllText(Path.Combine(lib, "include", "mylib.h")),
            "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        Type mc = asm.GetType("mylib.MlColor");
        Assert.True(mc.IsEnum);
        MethodInfo code = asm.GetType("mylib.Api").GetMethod("ml_color_code", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(mc, code.GetParameters()[0].ParameterType);
        Assert.Equal(105, (int)code.Invoke(null, new object[] { Enum.Parse(mc, "ML_GREEN") }));  // 5+100
    }
```

- [ ] **Step 3: Full regression sweep**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ExportClassTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~MuslLinkTests"` → ALL pass.
Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests"` → ALL pass.

- [ ] **Step 4: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/fixtures/mylib tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: grow mylib fixture with enum; Plan 3 complete"
```

---

## Notes for the implementer

- **Capture at parse time, not from signatures.** chibil collapses enums to int32 in ECMA signatures (`CodeGen.cs:349`), so the enum identity for params/returns/members MUST come from the parse-time `.chiapi` annotations — do NOT try to recover it from the merged signatures.
- **Synthesized enums reuse `CopiedTypeDef`.** Base = `System.Enum` TypeRef; `value__` instance field of the underlying primitive with `RTSpecialName|SpecialName`; one `Static|Literal|HasDefault` field per enumerator typed as the enum itself, each with an `AddConstant` row. `ReserveMemberFields` already reserves their field rows — make sure `SynthesizeApiEnums` runs before it and after the last other TypeDef-adding pass (`ReserveOpaqueTypeDefs`), mirroring the ordering fix from the named-fields work.
- **Interchangeability is the one novel IL claim.** The forwarder advertises the enum but calls the int-typed `<Module>` method. The `Forwarder_signature_uses_enum_and_runs` keystone is the guard — if it throws `InvalidProgramException`, the claim needs adjustment (a no-op conv or matched representation); do not weaken the assertion.
- **Opt-out unchanged.** No `--export-api` → no enums captured → `.chiapi` enum/usage sections empty (or no section) → no synthesis, no signature substitution. The v3 empty-section round-trip + ExportClass/Musl regression guard this.
- **Struct-field enum threading is OUT of this plan** — a follow-on applies the same position substitution to member-field signatures using the `EnumUsages`-style annotation for struct fields. Do not attempt it here.
- **Unsigned enums:** `value__` and the literal-field underlying type follow `IsUnsigned` (U4 vs I4). The fixture uses signed; keep the unsigned path wired even if untested here.
