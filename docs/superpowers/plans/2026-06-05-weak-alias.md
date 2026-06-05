# weak_alias (symbol aliasing) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make chibil support `__attribute__((alias("target")))` (GCC symbol aliasing, the basis of musl's `weak_alias`) so a function declared as an alias compiles, links, and is callable — resolving to the target's definition.

**Architecture:** Three small changes along the existing pipeline. (1) chibil's parser learns to parse a **postfix** `__attribute__((...))` after a top-level declarator and capture an `alias("target")` into the `Obj`. (2) chibil emits alias pairs `(aliasName → targetName)` into a new COFF manifest section `.chialias`, mirroring how `--export-api` emits `.chiapi`. (3) chibil-link reads `.chialias` and registers `DefinedMethodToken[alias] = DefinedMethodToken[target]`, so every reference to the alias resolves to the target's merged MethodDef token (the existing `SymbolResolver` redirect path). Scope: **function aliases** (the dominant musl case); data aliases are out of scope (a follow-up).

**Tech Stack:** C# (.NET 10), chibil compiler (`chibil/`), chibil-link linker (`tools/chibil-link/`), xUnit (`tests/Chibil.Tests/`), the CoreCLR target. Run tests under `vcvars64` (MSVC-PATH-gated cl/link tests are environmental, not regressions).

---

### Task 1: Acceptance test — a function alias links and is callable

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/WeakAliasTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class WeakAliasTests
{
    // __attribute__((alias("target"))) — GCC symbol aliasing, the basis of musl's
    // weak_alias. `myalias` must resolve to `target`'s definition and be callable.
    [Fact]
    public void Function_alias_compiles_and_is_callable()
    {
        const string src =
            "int target(int x){ return x + 1; }\n" +
            "extern __typeof(target) myalias __attribute__((weak, alias(\"target\")));\n" +
            "int main(void){ return myalias(41); }\n";
        // CompileToObj throws on any compile error; reaching the assert means it parsed + emitted.
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "FullyQualifiedName~WeakAliasTests.Function_alias_compiles_and_is_callable"`
Expected: FAIL during compile — the postfix `__attribute__((...))` after the declarator `myalias` is unparsed (chibil errors, e.g. "expected ';'" / unexpected `__attribute__`). This is the same failure the musl spike hit on `weak_alias`.

- [ ] **Step 3: Commit the failing test**

```bash
git add tests/Chibil.Tests/CoreClr/WeakAliasTests.cs
git commit -m "test: failing acceptance for function __attribute__((alias))"
```

---

### Task 2: Parse postfix `__attribute__` after a top-level declarator

**Files:**
- Modify: `chibil/Parser.cs` — `Function()` (~line 1899) and `GlobalVariable()` (~line 1987); reuse `AttributeList`/`SkipBalancedParens` (~line 651).

**Context:** `AttributeList` (line 651) already parses an `__attribute__((...))` group, skipping unknown attributes. The gap is *position*: postfix attributes (after the declarator, before `;`) are never consumed. `weak_alias(old,new)` expands to `extern __typeof(old) new __attribute__((__weak__, __alias__(#old)))`, i.e. a postfix attribute on a top-level declaration.

- [ ] **Step 1: Write the failing test** (parser-only, no aliasing yet — postfix attribute on a function prototype must parse and be ignored)

Add to `tests/Chibil.Tests/CoreClr/WeakAliasTests.cs`:

```csharp
    [Fact]
    public void Postfix_attribute_on_prototype_parses()
    {
        const string src =
            "int g(int) __attribute__((weak));\n" +   // postfix attribute, no alias
            "int g(int x){ return x; }\n" +
            "int main(void){ return g(0); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test ... --filter "FullyQualifiedName~WeakAliasTests.Postfix_attribute_on_prototype_parses"`
Expected: FAIL — postfix `__attribute__` unparsed.

- [ ] **Step 3: Consume a postfix attribute in `Function()`**

In `chibil/Parser.cs`, in `Function()` (~line 1901, immediately after `CType ty = Declarator(...)`), consume any postfix attribute group before the existing `;`/`,`/`{` handling:

```csharp
        CType ty = Declarator(ref tok, tok, basety, attr.PendingCallConv);
        if (Util.Equal(tok, "__attribute__")) tok = AttributeList(tok, ty);   // postfix attrs
```

And in `GlobalVariable()` (~line 1993, after `CType ty = Declarator(...)`):

```csharp
            CType ty = Declarator(ref tok, tok, basety, attr.PendingCallConv);
            if (Util.Equal(tok, "__attribute__")) tok = AttributeList(tok, ty);   // postfix attrs
```

(`AttributeList` returns the token past the group and currently skips unknown attrs — `weak`, `alias`, etc. — so this makes them parse-and-ignore. Aliasing is added in Task 3.)

- [ ] **Step 4: Run both tests to verify the prototype test passes**

Run: `dotnet test ... --filter "FullyQualifiedName~WeakAliasTests.Postfix_attribute_on_prototype_parses"`
Expected: PASS. (The Task 1 acceptance test still fails — it needs the alias to actually resolve.)

- [ ] **Step 5: Commit**

```bash
git add chibil/Parser.cs tests/Chibil.Tests/CoreClr/WeakAliasTests.cs
git commit -m "parser: parse postfix __attribute__ after top-level declarators"
```

---

### Task 3: Capture the `alias("target")` attribute into the Obj

**Files:**
- Modify: `chibil/Parser.cs` — `AttributeList` (~line 651); the `Obj` type (search `class Obj`) to add an `AliasTarget` field.
- Modify: the `CType`/`VarAttr` carrier so the alias target reaches `Function()`/`GlobalVariable()` where the `Obj` is created.

**Context:** `AttributeList(Token, CType ty)` currently records `packed`/`aligned` onto `ty`. Add capture of `alias("name")` onto `ty` (new `CType.AliasTarget` string), then propagate to the created `Obj` in `Function()`/`GlobalVariable()`.

- [ ] **Step 1: Add the carrier fields**

In `chibil/TypeSystem.cs` (the `CType` class), add:

```csharp
    public string AliasTarget;   // __attribute__((alias("target"))) — GCC symbol alias
```

In the `Obj` class (find with `git grep "class Obj"`), add:

```csharp
    public string AliasTarget;   // non-null => this symbol is an alias of AliasTarget
```

- [ ] **Step 2: Capture `alias(...)` in `AttributeList`**

In `chibil/Parser.cs` `AttributeList` (~line 661, alongside the `packed`/`aligned` cases, before the unknown-attribute skip at line 666):

```csharp
                if (Util.Consume(ref tok, tok, "alias") || Util.Consume(ref tok, tok, "__alias__"))
                {
                    tok = Util.Skip(tok, "(");
                    if (tok.Kind != TokenKind.Str) Util.ErrorTok(tok, "alias attribute requires a string");
                    ty.AliasTarget = Util.GetStringLiteralValue(tok);   // the target symbol name
                    tok = tok.Next;
                    tok = Util.Skip(tok, ")");
                    continue;
                }
```

(Verify the helper that returns a string-literal token's value; `git grep "GetStringLiteralValue\|StrVal\|\.Str\b" chibil/` — use the existing accessor for a string token's decoded bytes/text. If none exists, read the token's literal value field directly.)

- [ ] **Step 3: Propagate the alias onto the Obj**

In `Function()` (~line 1957, where `fn = NewGvar(nameStr, ty);` creates the function Obj) add immediately after:

```csharp
            fn = NewGvar(nameStr, ty);
            fn.IsFunction = true; fn.IsDefinition = Util.Equal(tok, "{");
            if (ty.AliasTarget != null) { fn.AliasTarget = ty.AliasTarget; fn.IsDefinition = false; }
```

(An alias declares no body — `IsDefinition = false`.)

- [ ] **Step 4: Write a test that the alias is captured (unit, no link yet)**

This is observable only via emission, so defer the assertion to Task 4's emission test. For now, re-run the full suite to confirm no regressions:

Run (under vcvars64): `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj`
Expected: same pass count as before + the Task 2 prototype test; the Task 1 acceptance test still fails (no emission yet).

- [ ] **Step 5: Commit**

```bash
git add chibil/Parser.cs chibil/TypeSystem.cs chibil/<Obj-file>.cs
git commit -m "parser: capture __attribute__((alias)) target onto Obj"
```

---

### Task 4: Emit alias pairs into a `.chialias` COFF manifest

**Files:**
- Modify: `chibil/coffobjectemitter.cs` — mirror the `.chiapi` manifest emission (search `chiapi`).
- Modify: the codegen entry that walks globals and triggers manifest emission (search where `.chiapi` is written, e.g. `CodeGen.cs` / the emitter's finalize step).

**Context:** chibil already emits a custom manifest section (`.chiapi`) for `--export-api`. Reuse that exact mechanism for a new `.chialias` section: a length-prefixed list of `(aliasName, targetName)` UTF-8 string pairs, one entry per `Obj` with non-null `AliasTarget`.

- [ ] **Step 1: Write the failing emission test**

Add to `WeakAliasTests.cs`:

```csharp
    [Fact]
    public void Alias_pair_is_emitted_in_chialias_section()
    {
        const string src =
            "int target(int x){ return x; }\n" +
            "extern __typeof(target) myalias __attribute__((alias(\"target\")));\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        // The object must carry a .chialias section naming the alias + target.
        Assert.True(ObjectFile.TryReadAliasManifest(obj, out var aliases), ".chialias missing");
        Assert.Equal("target", Assert.Contains("myalias", aliases));
    }
```

(`ObjectFile.TryReadAliasManifest` is added in Task 5; for now this test fails to compile → that's the RED. If you prefer a pure-emitter RED first, assert the raw section name is present by scanning `obj` for the ASCII bytes `".chialias"`.)

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test ... --filter "FullyQualifiedName~WeakAliasTests.Alias_pair_is_emitted"`
Expected: FAIL — no `.chialias` section emitted.

- [ ] **Step 3: Emit the `.chialias` section**

Find the `.chiapi` emission in `chibil/coffobjectemitter.cs` (`git grep -n chiapi chibil/`). Add a sibling method that writes a `.chialias` section whose payload is:

```
uint32 count
repeat count: uint32 aliasLen, bytes aliasName(utf8), uint32 targetLen, bytes targetName(utf8)
```

Collect entries by iterating the program's globals for `obj.AliasTarget != null`:

```csharp
        var aliases = new List<(string alias, string target)>();
        for (Obj o = prog; o != null; o = o.Next)
            if (o.AliasTarget != null) aliases.Add((o.Name, o.AliasTarget));
        if (aliases.Count > 0) EmitAliasManifest(aliases);   // writes the .chialias section
```

Implement `EmitAliasManifest` by copying the `.chiapi` writer's section-creation call and writing the payload above with a `BlobBuilder`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test ... --filter "FullyQualifiedName~WeakAliasTests.Alias_pair_is_emitted"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add chibil/coffobjectemitter.cs chibil/CodeGen.cs tests/Chibil.Tests/CoreClr/WeakAliasTests.cs
git commit -m "codegen: emit __attribute__((alias)) pairs in a .chialias manifest"
```

---

### Task 5: chibil-link reads `.chialias` and binds the alias to the target's token

**Files:**
- Modify: `tools/chibil-link/ObjectFile.cs` — add `TryReadAliasManifest(byte[], out Dictionary<string,string>)` (mirror the `.chiapi` reader).
- Modify: `tools/chibil-link/LinkSymbolTable.cs` (~line 23, `AddDefined`) or `tools/chibil-link/SymbolResolver.cs` (~line 38, where `table.AddDefined` runs) to apply aliases after all defined symbols are known.

**Context:** `LinkSymbolTable.DefinedMethodToken` maps a symbol name → its merged MethodDef token. `SymbolResolver` (line 103) already redirects any reference to a name in `DefinedMethodToken` to that token. So binding `DefinedMethodToken[alias] = DefinedMethodToken[target]` makes every call to the alias resolve to the target — no new MethodDef needed.

- [ ] **Step 1: Write the failing test**

Add to `tests/Chibil.Tests/CoreClr/WeakAliasTests.cs`:

```csharp
    [Fact]
    public void Linker_binds_alias_to_target_token()
    {
        const string src =
            "int target(int x){ return x; }\n" +
            "extern __typeof(target) myalias __attribute__((alias(\"target\")));\n" +
            "int use(void){ return myalias(7); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var merger = LinkTestHarness.LinkOne(obj);     // links a single obj, exposes the symbol table
        Assert.Equal(merger.TokenOf("target"), merger.TokenOf("myalias"));
    }
```

(Use the existing single-object link harness if present — `git grep -n "LinkOne\|LinkToBytes\|new MetadataMerger" tools/chibil-link tests/`; ImportsTests already exercises `CollectImports` on a linked object, so follow that harness shape. If `TokenOf` does not exist, assert instead that `use()` links without an "unresolved symbol 'myalias'" error.)

- [ ] **Step 2: Run it to verify it fails**

Run (under vcvars64): `dotnet test ... --filter "FullyQualifiedName~WeakAliasTests.Linker_binds_alias_to_target_token"`
Expected: FAIL — `myalias` is unresolved (no `.chialias` consumption yet).

- [ ] **Step 3: Read `.chialias` in chibil-link**

In `tools/chibil-link/ObjectFile.cs`, add `TryReadAliasManifest` mirroring the existing `.chiapi` reader (find with `git grep -n "chiapi" tools/chibil-link/`): locate the section named `.chialias`, parse the `count` + `(aliasLen,alias,targetLen,target)` payload from Task 4, return them as a `Dictionary<string,string>` (alias → target).

- [ ] **Step 4: Apply aliases after defined symbols are collected**

In `tools/chibil-link/SymbolResolver.cs` `Resolve` (~line 38, right after the `foreach (var of in objs) table.AddDefined(of, merger);` loop), add:

```csharp
        // Apply __attribute__((alias)) bindings: an alias resolves to its target's
        // merged token, so every reference to the alias hits the target's MethodDef.
        foreach (var of in objs)
            if (ObjectFile.TryReadAliasManifest(of.Bytes, out var aliases))
                foreach (var (alias, target) in aliases)
                    if (table.DefinedMethodToken.TryGetValue(target, out int tok))
                        table.DefinedMethodToken[alias] = tok;
```

(Confirm the field that holds the object's raw bytes on `ObjectFile` — `git grep -n "public byte\[\]\|Bytes\|Image" tools/chibil-link/ObjectFile.cs` — and use it for the manifest read.)

- [ ] **Step 5: Run the linker test + the Task 1 acceptance test**

Run (under vcvars64):
`dotnet test ... --filter "FullyQualifiedName~WeakAliasTests"`
Expected: PASS for `Linker_binds_alias_to_target_token` AND the Task 1 `Function_alias_compiles_and_is_callable`.

- [ ] **Step 6: Commit**

```bash
git add tools/chibil-link/ObjectFile.cs tools/chibil-link/SymbolResolver.cs tests/Chibil.Tests/CoreClr/WeakAliasTests.cs
git commit -m "chibil-link: bind __attribute__((alias)) symbols to their target token"
```

---

### Task 6: Full-suite regression + musl-spike confirmation

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite under vcvars64**

```
cmd /c '"C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 && dotnet test D:\sandbox\chibil\tests\Chibil.Tests\Chibil.Tests.csproj --nologo'
```
Expected: prior pass count + the new `WeakAliasTests`; the only failure is the pre-existing `ManglingInteropTests.Data_MsvcDefine_ChibiConsume` (environmental, not a regression).

- [ ] **Step 2: Confirm musl now compiles `weak_alias` TUs *without* the neutralization shim**

Edit `targets/build/musl-chibil-compat.h` to REMOVE the `#define weak_alias(old, new)` override (so real `weak_alias` is used), then:

```
wsl bash -lc "INCREMENTAL=0 SHIM=1 bash /mnt/d/sandbox/chibil/targets/build/musl-spike.sh"
```
Expected: the `weak_alias`-bucket failures (was 46 in the spike) drop to ~0; the previously-`weak_alias`-failing TUs (`string/strcasecmp.c`, etc.) now compile. Overall rate rises above the 98% measured with the shim.

- [ ] **Step 3: Commit the compat-header change**

```bash
git add targets/build/musl-chibil-compat.h
git commit -m "musl-compat: drop weak_alias neutralization (real aliasing now supported)"
```

---

## Notes / out of scope
- **Data aliases** (`weak_alias` on a non-function, e.g. `__environ`/`environ`): `DefinedMethodToken` is function-only. Data aliasing needs an analogous binding in the data-import/field path — a separate, smaller follow-up; not required for the function-dominated musl core.
- **`weak` semantics** (a weak def overridden by a strong one): the `weak` attribute is parsed-and-ignored here; true weak-symbol precedence is a separate concern and not needed for the alias use-case.
