# API Facade — Plan 1: Walking Skeleton (functions end-to-end)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the whole pipeline on one symbol kind — chibil tags functions declared in a `--export-api` public header and emits a `.chiapi` manifest; chibil-link reads it and builds a per-header `static class Api` of forwarders in a header-derived namespace, callable from C#.

**Architecture:** chibil parses the public header (it already parses everything), records public function names + the header name into a new `.chiapi` COFF section (modeled on `.chidbg`). chibil-link parses `.chiapi` into `ObjectFile.Api`, and when a manifest is present **reuses the existing `--export-class` forwarder engine** (`ForwarderSynthesizer` + struct/opaque promotion), sourcing the class name from the header (`mylib.h` → `mylib.Api`) and **restricting** the forwarded set to the manifest's functions.

**Tech Stack:** C# / .NET 10, xUnit. Spec: `docs/superpowers/specs/2026-06-03-api-facade-design.md`.

**This is Plan 1 of 4.** Later plans (separate docs): **2** — re-namespace/publicize public struct & opaque types into the header namespace; **3** — enum TypeDef synthesis + thread enums into signatures/fields; **4** — QuickJS oracle (surface + behavioral). Plan 1 deliberately stops at functions (public structs already become public via the reused export engine, just in the global namespace — Plan 2 namespaces them).

---

## File Structure

- Modify `chibil/ChibiTypes.cs` — add `CompilerOptions.ExportApiHeaders` (List<string>) and a parse-time collector `CompilerOptions.PublicApiFunctions` (HashSet<string>).
- Modify `chibil/Driver.cs` — parse the repeatable `--export-api=<path>` flag.
- Modify `chibil/Parser.cs` — at the top-level declaration loop, record a function's name into `PublicApiFunctions` when its declarator token's source file matches an `--export-api` header.
- Modify `chibil/CodeGen.cs` — `BuildChiapiBlob()` and wire it into `Generate()` next to `BuildChibilDebugBlob()`.
- Modify `chibil/coffobjectemitter.cs` — add the `.chiapi` section (mirror `.chidbg`).
- Create `tools/chibil-link/ChibilApi.cs` — the parsed manifest type + `Parse` (mirror `ParseChibilDebug`).
- Modify `tools/chibil-link/ObjectFile.cs` — parse `.chiapi` into `ObjectFile.Api`.
- Modify `tools/chibil-link/Program.cs` (`LinkPipeline.LinkToBytes`) + `tools/chibil-link/MetadataMerger.cs` — derive the export class from the manifest and restrict forwarders to its function set.
- Modify `tests/Chibil.Tests/CoreClr/TestCompiler.cs` — overload `CompileToObj` to pass export-api headers.
- Create `tests/Chibil.Tests/CoreClr/fixtures/mylib/{include/mylib.h,src/mylib.c}` — the grown fixture (Plan 1 seed).
- Create `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` — the tests.

---

## Task 1: `--export-api` flag and options plumbing

**Files:**
- Modify: `chibil/ChibiTypes.cs:423` (`CompilerOptions`)
- Modify: `chibil/Driver.cs` (`ParseArgs`, ~line 151 where `-I` is handled)
- Modify: `tests/Chibil.Tests/CoreClr/TestCompiler.cs:42`
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ApiFacadeTests
{
    // A function declared in a public header and defined in a .c, plus a private
    // static helper that must never reach the facade.
    const string LibSrc =
        "#include \"mylib.h\"\n" +
        "static int secret(int x){ return x * 2; }\n" +
        "int ml_add(int a, int b){ return a + b + secret(0); }\n";

    const string LibHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "int ml_add(int a, int b);\n" +
        "#endif\n";

    [Fact]
    public void CompileToObj_accepts_export_api_headers()
    {
        // The new overload must compile without throwing and produce a non-empty object.
        byte[] obj = TestCompiler.CompileToObjWithApi(
            LibSrc, LibHdr, headerName: "mylib.h",
            target: Chibil.TargetProfile.CoreClr);
        Assert.NotNull(obj);
        Assert.True(obj.Length > 0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.CompileToObj_accepts"`
Expected: compile failure — `TestCompiler.CompileToObjWithApi` does not exist.

- [ ] **Step 3: Add the options fields**

In `chibil/ChibiTypes.cs`, inside `class CompilerOptions` (after the `IncludePaths` field at line 425), add:

```csharp
    // --export-api=<header>: headers whose top-level declarations form the public API
    // surface (resolved paths or as-written include spellings, matched by file name).
    public List<string> ExportApiHeaders = new();
    // Collected during parse: names of functions declared at top level inside an
    // ExportApiHeaders header. Consumed by CodeGen to emit the .chiapi manifest.
    public HashSet<string> PublicApiFunctions = new();
```

- [ ] **Step 4: Parse the flag in the Driver**

In `chibil/Driver.cs`, in `ParseArgs`, next to the `-I` handling (~line 151), add:

```csharp
            if (arg.StartsWith("--export-api=")) { Options.ExportApiHeaders.Add(arg["--export-api=".Length..]); continue; }
```

- [ ] **Step 5: Add the test-compiler overload**

In `tests/Chibil.Tests/CoreClr/TestCompiler.cs`, add a sibling of `CompileToObj` that writes a header next to the source and sets `ExportApiHeaders`. Mirror the existing method body (lines 42-74), adding the header file and the option:

```csharp
    public static byte[] CompileToObjWithApi(string source, string header, string headerName,
        Chibil.TargetProfile target, string name = "t.c")
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string srcPath = Path.Combine(dir, name);
        string hdrPath = Path.Combine(dir, headerName);
        File.WriteAllText(srcPath, source);
        File.WriteAllText(hdrPath, header);
        try
        {
            var opts = new Chibil.CompilerOptions { Target = target, BaseFile = srcPath };
            opts.IncludePaths.Add(dir);                 // so #include "mylib.h" resolves
            opts.ExportApiHeaders.Add(hdrPath);          // the public header
            var types = new Chibil.TypeSystem(opts.DataModel);
            var tokenizer = new Chibil.Tokenizer(opts, types);
            var preprocessor = new Chibil.Preprocessor(tokenizer, opts, types);
            preprocessor.InitMacros();
            var parser = new Chibil.Parser(tokenizer, opts, types);
            preprocessor.SetParser(parser);
            Chibil.Token tok = tokenizer.TokenizeFile(srcPath)
                ?? throw new InvalidOperationException($"Failed to tokenize {srcPath}");
            tok = preprocessor.Preprocess(tok);
            Chibil.Obj prog = parser.Parse(tok);
            var codegen = new Chibil.CodeGen(opts, tokenizer, types);
            return codegen.Generate(prog, "t.obj", Path.GetFullPath(srcPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
```

(Match the exact namespaces/types the existing `CompileToObj` uses — open it at line 42 and copy its `using`s / type references; the snippet above assumes the `Chibil.` prefix, adjust to the file's convention.)

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.CompileToObj_accepts"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add chibil/ChibiTypes.cs chibil/Driver.cs tests/Chibil.Tests/CoreClr/TestCompiler.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: --export-api flag + options plumbing + test-compiler overload"
```

---

## Task 2: Tag public functions during parse

**Files:**
- Modify: `chibil/Parser.cs` (top-level declaration loop, ~line 2015; function declarator name token available there)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

The signal: a function is public iff a top-level declaration of it appears in an
`--export-api` header. chibil parses the header (it is `#include`d into the TU), so the
declarator's name token carries `File` = the header. Record the name then.

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests`:

```csharp
    [Fact]
    public void Public_function_in_header_is_tagged_private_static_is_not()
    {
        // Re-run the compile but capture the options to inspect PublicApiFunctions.
        var opts = TestCompiler.CompileAndReturnOptions(LibSrc, LibHdr, "mylib.h",
            Chibil.TargetProfile.CoreClr);
        Assert.Contains("ml_add", opts.PublicApiFunctions);   // declared in mylib.h
        Assert.DoesNotContain("secret", opts.PublicApiFunctions); // static, src-only
    }
```

Add `CompileAndReturnOptions` to `TestCompiler.cs` — identical to `CompileToObjWithApi`
but returning `opts` after `parser.Parse(tok)` (skip codegen):

```csharp
    public static Chibil.CompilerOptions CompileAndReturnOptions(string source, string header,
        string headerName, Chibil.TargetProfile target, string name = "t.c")
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string srcPath = Path.Combine(dir, name);
            File.WriteAllText(srcPath, source);
            File.WriteAllText(Path.Combine(dir, headerName), header);
            var opts = new Chibil.CompilerOptions { Target = target, BaseFile = srcPath };
            opts.IncludePaths.Add(dir);
            opts.ExportApiHeaders.Add(Path.Combine(dir, headerName));
            var types = new Chibil.TypeSystem(opts.DataModel);
            var tokenizer = new Chibil.Tokenizer(opts, types);
            var preprocessor = new Chibil.Preprocessor(tokenizer, opts, types);
            preprocessor.InitMacros();
            var parser = new Chibil.Parser(tokenizer, opts, types);
            preprocessor.SetParser(parser);
            Chibil.Token tok = preprocessor.Preprocess(tokenizer.TokenizeFile(srcPath));
            parser.Parse(tok);
            return opts;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_function_in_header"`
Expected: FAIL — `PublicApiFunctions` is empty (no tagging yet).

- [ ] **Step 3: Implement the tagging helper + call**

In `chibil/Parser.cs`, add a private helper (place near the other top-level helpers):

```csharp
    // True if `tok` originates from one of the --export-api public headers (matched by
    // file name, so an as-written include spelling and a resolved path both match).
    private bool IsFromExportApiHeader(Token tok)
    {
        if (tok?.File == null || _opts.ExportApiHeaders.Count == 0) return false;
        string tf = System.IO.Path.GetFileName(tok.File.DisplayName ?? tok.File.Name ?? "");
        foreach (string h in _opts.ExportApiHeaders)
            if (string.Equals(System.IO.Path.GetFileName(h), tf, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
```

In the top-level declaration loop in `Parser.Parse` (~line 2015), where a function
declarator is recognized and its name token is in hand (the same token used to create
the function `Obj`), add — guarded so only functions are recorded:

```csharp
            // record public-API functions: a top-level function declarator (prototype or
            // definition) whose name token is in an --export-api header.
            if (isFunctionDeclarator && IsFromExportApiHeader(nameTok))
                _opts.PublicApiFunctions.Add(nameTokText);
```

Use the loop's existing function-vs-variable discriminator and the existing name-token
variable (read the surrounding code at line 2015 — the Explore notes `Function()` vs
`GlobalVariable()` are dispatched there; record at the function branch using that
branch's name token). `_opts` is the `CompilerOptions` the `Parser` was constructed with
(confirm the field name in the `Parser` ctor and match it).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Public_function_in_header"`
Expected: PASS — `ml_add` tagged, `secret` not.

- [ ] **Step 5: Commit**

```bash
git add chibil/Parser.cs tests/Chibil.Tests/CoreClr/TestCompiler.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil: tag public-API functions declared in --export-api headers"
```

---

## Task 3: Emit the `.chiapi` manifest section

**Files:**
- Modify: `chibil/CodeGen.cs` (add `BuildChiapiBlob`; wire near `BuildChibilDebugBlob` at line 4039)
- Modify: `chibil/coffobjectemitter.cs` (add `.chiapi` section; mirror `.chidbg` at lines 2394/2524/2563)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

Manifest format (`.chiapi`): magic `CAPI`, version 1, header-group name (the public
header's base name without extension, used for the facade namespace), then the count and
UTF-8 names of public functions.

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests` (reads the COFF section directly via the linker's object model):

```csharp
    [Fact]
    public void Chiapi_section_lists_public_functions_and_group_name()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(LibSrc, LibHdr, "mylib.h",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);                          // .chiapi parsed
        Assert.Equal("mylib", of.Api.Group);             // header base name → group/namespace
        Assert.Contains("ml_add", of.Api.Functions);
        Assert.DoesNotContain("secret", of.Api.Functions);
    }
```

(This depends on Task 4's `ObjectFile.Api`/`ChibilApi`. Implement Task 3 then Task 4;
the test goes green after Task 4. If executing strictly task-by-task, write this test in
Task 4 instead — it asserts both the emitted bytes and the parse.)

- [ ] **Step 2: Add the `.chiapi` section to the COFF emitter**

In `chibil/coffobjectemitter.cs`, mirror the `.chidbg` plumbing:

Near line 2394:
```csharp
    private const string ChiapiSectionName = ".chiapi";
    private BlobBuilder _chiapi;
    public void SetChiapiData(BlobBuilder data) => _chiapi = data;
```

In `CreateSections()` (near the `.chidbg` block, ~line 2555):
```csharp
        if (_chiapi != null && _chiapi.Count > 0)
            builder.Add(new Section(ChiapiSectionName, SectionCharacteristics.ContainsInitializedData
                | SectionCharacteristics.MemRead | SectionCharacteristics.Align1Bytes));
```

In `SerializeSection()` (near the `.chidbg` case, ~line 2570):
```csharp
            ChiapiSectionName => _chiapi,
```

- [ ] **Step 3: Build and wire the blob in CodeGen**

In `chibil/CodeGen.cs`, add after `BuildChibilDebugBlob` (line 4094):

```csharp
    // Serialize the .chiapi manifest: magic 'CAPI', version 1, the facade group name
    // (the first --export-api header's base name, no extension), then the public
    // function names. chibil-link consumes this to build the per-header Api facade.
    private BlobBuilder BuildChiapiBlob()
    {
        if (_opts.ExportApiHeaders.Count == 0 || _opts.PublicApiFunctions.Count == 0)
            return null;
        string group = System.IO.Path.GetFileNameWithoutExtension(_opts.ExportApiHeaders[0]);
        var b = new BlobBuilder();
        b.WriteByte((byte)'C'); b.WriteByte((byte)'A'); b.WriteByte((byte)'P'); b.WriteByte((byte)'I');
        b.WriteByte(1); // version
        byte[] g = System.Text.Encoding.UTF8.GetBytes(group);
        b.WriteUInt16((ushort)g.Length); b.WriteBytes(g);
        var fns = new System.Collections.Generic.List<string>(_opts.PublicApiFunctions);
        fns.Sort(System.StringComparer.Ordinal); // deterministic output
        b.WriteInt32(fns.Count);
        foreach (string fn in fns)
        {
            byte[] nm = System.Text.Encoding.UTF8.GetBytes(fn);
            b.WriteUInt16((ushort)nm.Length); b.WriteBytes(nm);
        }
        return b;
    }
```

Wire it next to the debug blob at line 4039:

```csharp
        var api = BuildChiapiBlob();
        if (api != null)
            coffBuilder.SetChiapiData(api);
```

Confirm `_opts` is the `CodeGen`'s `CompilerOptions` field (match the actual field name).

- [ ] **Step 4: Run to verify (after Task 4)**

The assertion test runs in Task 4 (it needs the parser). Build now to confirm chibil
compiles: `dotnet build chibil/chibil.csproj -c Debug` → Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add chibil/CodeGen.cs chibil/coffobjectemitter.cs
git commit -m "chibil: emit .chiapi manifest section (group name + public functions)"
```

---

## Task 4: Parse `.chiapi` in chibil-link

**Files:**
- Create: `tools/chibil-link/ChibilApi.cs`
- Modify: `tools/chibil-link/ObjectFile.cs:37` (add `Api` field) and `:148-152` (parse it)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` (the Task-3 test goes green here)

- [ ] **Step 1: Create the manifest type + parser**

Create `tools/chibil-link/ChibilApi.cs`:

```csharp
using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// The public-API manifest chibil emits in the <c>.chiapi</c> COFF section: a facade
/// group name (the public header's base name, used for the namespace) and the names of
/// the functions declared in that header. See CodeGen.BuildChiapiBlob.
/// </summary>
public sealed class ChibilApi
{
    public string Group = "";
    public readonly HashSet<string> Functions = new();

    public static ChibilApi Parse(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'A' || br.ReadByte() != 'P' || br.ReadByte() != 'I')
            return null;
        if (br.ReadByte() != 1) return null; // version
        var api = new ChibilApi();
        int glen = br.ReadUInt16();
        api.Group = System.Text.Encoding.UTF8.GetString(br.ReadBytes(glen));
        int n = br.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int len = br.ReadUInt16();
            api.Functions.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        return api;
    }
}
```

- [ ] **Step 2: Wire parsing into ObjectFile**

In `tools/chibil-link/ObjectFile.cs`, add a field near line 37:

```csharp
    public ChibilApi Api;       // parsed .chiapi public-API manifest, or null
```

After the `.chidbg` parse (line 150), add:

```csharp
        var apiSec = of.Coff.FindSection(".chiapi");
        if (apiSec != null)
            of.Api = ChibilApi.Parse(of.Coff.GetSectionData(apiSec.Value).ToArray());
```

- [ ] **Step 3: Run the Task-3 manifest test**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Chiapi_section"`
Expected: PASS — group `mylib`, `ml_add` present, `secret` absent.

- [ ] **Step 4: Commit**

```bash
git add tools/chibil-link/ChibilApi.cs tools/chibil-link/ObjectFile.cs
git commit -m "chibil-link: parse .chiapi public-API manifest into ObjectFile.Api"
```

---

## Task 5: Build the per-header `Api` facade from the manifest

**Files:**
- Modify: `tools/chibil-link/Program.cs` (`LinkPipeline.LinkToBytes` ~line 221; derive export class from manifests)
- Modify: `tools/chibil-link/MetadataMerger.cs` (`IsExportForwarder` ~line 114; restrict to manifest functions)
- Test: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs`

Reuse the existing `--export-class` engine: when objects carry a `.chiapi` manifest and
no explicit `--export-class` was given, set the export class to `<group>.Api` and
restrict forwarders to the manifest's function names.

- [ ] **Step 1: Write the failing test**

Add to `ApiFacadeTests` — links the fixture object and inspects the produced assembly:

```csharp
    static Assembly LinkLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(LibSrc, LibHdr, "mylib.h",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>()); // no explicit export-class
        return Assembly.Load(pe);
    }

    [Fact]
    public void Facade_Api_class_in_header_namespace_forwards_public_functions()
    {
        Assembly asm = LinkLib();
        Type api = asm.GetType("mylib.Api");
        Assert.NotNull(api);                                   // namespace from header base name
        Assert.True(api.IsPublic && api.IsAbstract && api.IsSealed);
        MethodInfo add = api.GetMethod("ml_add", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(add);
        Assert.Null(api.GetMethod("secret", BindingFlags.Public | BindingFlags.Static)); // private hidden
        Assert.Equal(7, (int)add.Invoke(null, new object[] { 3, 4 })); // forwarder runs: 3+4+secret(0)=7
    }

    [Fact]
    public void No_export_api_means_no_facade()
    {
        byte[] obj = TestCompiler.CompileToObj("int ml_add(int a,int b){return a+b;} int main(void){return 0;}",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>()));
        Assert.Null(asm.GetType("mylib.Api"));
        Assert.DoesNotContain(asm.GetTypes(), t => t.IsPublic); // unchanged: no public facade
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Facade_Api|FullyQualifiedName~ApiFacadeTests.No_export_api"`
Expected: FAIL — `mylib.Api` not produced (the manifest isn't driving the facade yet).

- [ ] **Step 3: Derive the export class from the manifest in LinkToBytes**

In `tools/chibil-link/Program.cs`, in `LinkPipeline.LinkToBytes`, before constructing the
merger/PE (where `exportClass` is currently consumed, ~line 221), compute the facade
inputs from the loaded objects' manifests:

```csharp
        // API facade: when objects carry a .chiapi manifest and no explicit
        // --export-class was given, build a `<group>.Api` facade restricted to the
        // manifest's public functions (Plan 1: functions only).
        HashSet<string> apiFns = null;
        if (exportClass == null)
        {
            string group = null;
            apiFns = new HashSet<string>();
            foreach (var o in objs)
                if (o.Api != null)
                {
                    group ??= o.Api.Group;
                    apiFns.UnionWith(o.Api.Functions);
                }
            if (group != null) exportClass = SanitizeNs(group) + ".Api";
            else apiFns = null;
        }
```

Add a small sanitizer (kept local to `LinkPipeline`):

```csharp
    // Turn a header base name into a valid namespace segment (letters/digits/underscore;
    // a leading digit is prefixed with '_'). e.g. "quickjs-libc" -> "quickjs_libc".
    private static string SanitizeNs(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
```

Thread `apiFns` into the merger so it can restrict forwarders (pass it to the
`MetadataMerger` ctor / `MergeAndPredict`, storing it in a field `_apiFunctionNames`).
Follow the existing `_exportClass` threading from `LinkToBytes` → `MetadataMerger`
(grep `exportClass`/`_exportClass` for the exact path) and add the parallel parameter.

- [ ] **Step 4: Restrict forwarders to the manifest set**

In `tools/chibil-link/MetadataMerger.cs`, in `IsExportForwarder` (line 114), add the
manifest filter (when a set is present, the function must be in it):

```csharp
    private bool IsExportForwarder(ObjectFile of, ObjMethod m)
    {
        if (m.Name == _entrySymbol) return false;
        if (_apiFunctionNames != null && !_apiFunctionNames.Contains(m.Name)) return false; // manifest-restricted
        var mdef = of.Md.GetMethodDefinition(m.Handle);
        return (mdef.Attributes & UnmanagedExportFlag) != 0;
    }
```

Add the field and constructor parameter:

```csharp
    private readonly HashSet<string> _apiFunctionNames; // null = no manifest restriction
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests.Facade_Api|FullyQualifiedName~ApiFacadeTests.No_export_api"`
Expected: PASS — `mylib.Api.ml_add` present and returns 7; `secret` absent; no facade without `--export-api`.

- [ ] **Step 6: Commit**

```bash
git add tools/chibil-link/Program.cs tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: build per-header Api facade from .chiapi manifest"
```

---

## Task 6: Promote the fixture to real files + regression sweep

**Files:**
- Create: `tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h`
- Create: `tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c`
- Modify: `tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs` (point one test at the on-disk fixture)

The inline strings proved the mechanics; now seed the **grown fixture** on disk so Plans
2-4 extend the same `include/` + `src/` library.

- [ ] **Step 1: Create the fixture files**

`tests/Chibil.Tests/CoreClr/fixtures/mylib/include/mylib.h`:

```c
#ifndef MYLIB_H
#define MYLIB_H
int ml_add(int a, int b);
#endif
```

`tests/Chibil.Tests/CoreClr/fixtures/mylib/src/mylib.c`:

```c
#include "mylib.h"
static int secret(int x){ return x * 2; }
int ml_add(int a, int b){ return a + b + secret(0); }
```

- [ ] **Step 2: Add a fixture-driven test**

Add to `ApiFacadeTests` a helper that locates the fixture relative to the test assembly
and compiles `src/mylib.c` with `--export-api=include/mylib.h`, then asserts the same
facade as Task 5 (this is the seed Plans 2-4 grow):

```csharp
    static string FixtureDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "fixtures", "mylib");

    [Fact]
    public void Fixture_mylib_links_and_exposes_Api()
    {
        string lib = FixtureDir();
        string src = File.ReadAllText(Path.Combine(lib, "src", "mylib.c"));
        string hdr = File.ReadAllText(Path.Combine(lib, "include", "mylib.h"));
        byte[] obj = TestCompiler.CompileToObjWithApi(src, hdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>()));
        MethodInfo add = asm.GetType("mylib.Api").GetMethod("ml_add", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(11, (int)add.Invoke(null, new object[] { 5, 6 }));
    }
```

- [ ] **Step 3: Run the full ApiFacade suite + the existing export-class suite**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ApiFacadeTests"`
Expected: PASS (all Plan-1 tests).

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~ExportClassTests"`
Expected: PASS — the manifest path must not regress the explicit `--export-class` engine
(the new filter only applies when `_apiFunctionNames != null`, i.e. only when a manifest
drove the export class).

- [ ] **Step 4: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/fixtures/mylib tests/Chibil.Tests/CoreClr/ApiFacadeTests.cs
git commit -m "chibil-link: seed grown mylib fixture; Plan 1 walking skeleton complete"
```

---

## Notes for the implementer

- The two `TestCompiler` helpers duplicate the existing `CompileToObj` body (TestCompiler.cs:42-74) with two additions: a header file and `opts.ExportApiHeaders`/`opts.IncludePaths`. Read the real method first and match its exact type references and `using`s (the `Chibil.` prefixes above are illustrative).
- Task 2's exact insertion point is the top-level declaration dispatch in `Parser.Parse` (~line 2015, where `Function()` vs `GlobalVariable()` is chosen). Read that loop; record on the **function** branch using that branch's declarator name token and text. The `IsFromExportApiHeader` helper is complete; only the one-line call placement is local to that loop.
- Do NOT change behavior when `--export-api` is absent: `BuildChiapiBlob` returns null (no section), `ObjectFile.Api` stays null, `_apiFunctionNames` stays null, and `IsExportForwarder` is unchanged — guarded by the `No_export_api_means_no_facade` test.
- Plan 1 leaves public structs in the global namespace (they already become *public* via the reused export engine — see `ExportClassTests.Referenced_struct_is_public_for_export`). Re-namespacing them into `mylib` is Plan 2; do not attempt it here.
- `LinkPipeline.LinkToBytes` has several overloads; use the one the tests call (`(objs, libraries)` and `(objs, libraries, exportClass)` per `ExportClassTests.cs:16`). The manifest logic lives in the shared implementation that all overloads funnel into.
