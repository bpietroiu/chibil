# Chibil Self-Contained Linux Builds — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compile a `.c` file to a runnable pure-MSIL .NET assembly on Linux with zero Windows tooling — `chibil --target=coreclr a.c -lc -o app.dll` then `dotnet app.dll`.

**Architecture:** A new CoreCLR target in `CodeGen` emits managed-COFF `.obj` files *without* `/clr` IJW machinery (no NEP thunks, no `__unep@`, `ILOnly` intent). A new `tools/chibil-link` tool parses those objects (reusing `coffobjdumper`'s `CoffFile`), merges their ECMA metadata into one `MetadataBuilder` (reusing `asm2obj`'s `TokenMap` + `EcmaSignatureRewriter`), resolves cross-object symbols, synthesizes P/Invoke for unresolved externs against `-l` libraries, fixes up CLR-token relocations in the IL, synthesizes a managed entry point, and emits a pure-MSIL PE via `ManagedPEBuilder` (`CorFlags.ILOnly`) plus a `runtimeconfig.json`.

**Tech Stack:** C# / .NET 10, `System.Reflection.Metadata`, `System.Reflection.PortableExecutable` (`MetadataReader`, `MetadataBuilder`, `ManagedPEBuilder`, `MethodBodyStreamEncoder`), xUnit, WSL + `dotnet` for end-to-end runs.

**Reference docs:** Design spec at `docs/superpowers/specs/2026-05-31-chibil-linux-selfcontained-design.md`. Read it before starting.

**Milestones:** Part A–E deliver **Phase 0** (freestanding `fib`, exit code 55). Part F delivers **Phase 1** (two objects, `puts` via libc P/Invoke). Each Part ends in a green test.

**Key reused APIs (verified in tree):**
- `CoffFile.Parse(byte[])`, `.FindSection`, `.GetSectionData`, `.GetRelocations`, `.BuildMethodBodyLocationMap()`, `.BuildFieldDataLocationMap()`, `.BuildTokenRelocationMap(section)`, `.GetPatchedSectionData(section)` — `tools/coffobjdumper.cs`.
- `TokenMap(MetadataReader, MetadataBuilder)` with `MapToken(int)`, `MapEntity(EntityHandle)`, `MapUserStringToken(int)` — `tools/asm2obj/TokenMap.cs`.
- `EcmaSignatureRewriter.RewriteMethodSignature/RewriteFieldSignature/RewriteStandaloneSignatureBlob/RewriteMemberReferenceSignature(...)` — `tools/asm2obj/EcmaSignatureRewriter.cs`.
- `CodeGen.Generate(Obj, string, string)` and `CompilerOptions` in `chibil/ChibiTypes.cs:415`.

> **WSL prerequisite (verify once, before Part E):** Run `wsl -- dotnet --version` from PowerShell. It must print a .NET 10 SDK version. If it errors, install the SDK in WSL before Part E's integration tests. The unit tests in Parts A–D and F run on Windows under `dotnet test` and do **not** need WSL.

---

## File Structure

**New files:**
- `tools/chibil-link/ChibilLink.csproj` — linker project; `<Compile Include>`s shared sources from `chibil/` and `tools/asm2obj/`.
- `tools/chibil-link/Program.cs` — CLI entry: parse args (`-o`, `-l`, input objs), drive the pipeline.
- `tools/chibil-link/ObjectFile.cs` — `ObjectFile` wraps a parsed `CoffFile` + a `MetadataReader` over its `.cormeta`; exposes machine, methods, fields, token-reloc maps, section bytes.
- `tools/chibil-link/LinkSymbolTable.cs` — collects defined/undefined symbols across objects; COMDAT fold; duplicate detection.
- `tools/chibil-link/MetadataMerger.cs` — copies each object's metadata into a shared `MetadataBuilder` via a per-object `TokenMap`; builds the merged `<Module>`.
- `tools/chibil-link/SymbolResolver.cs` — resolves undefined externs to merged `MethodDef`/`Field`, or synthesizes P/Invoke `MethodDef`s for `-l` libraries.
- `tools/chibil-link/RelocationFixer.cs` — rewrites CLR-token operands in IL bytes from original→merged tokens.
- `tools/chibil-link/EntrySynthesizer.cs` — synthesizes the managed `Main(string[])` entry and the module `.cctor`.
- `tools/chibil-link/PeWriter.cs` — assembles the merged metadata + IL + field data into a PE via `ManagedPEBuilder`; writes `runtimeconfig.json`.
- `tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs` — unit tests for Part A (obj has no IJW sections).
- `tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs` — unit tests for Parts B–D, F (parse, merge, resolve, fixup).
- `tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs` — WSL integration tests (Phase 0 + Phase 1 acceptance).

**Modified files:**
- `chibil/ChibiTypes.cs` — add `TargetProfile` enum + field to `CompilerOptions`.
- `chibil/Driver.cs` — parse `--target=coreclr`/`-l`; route link to `chibil-link`; write `runtimeconfig.json`.
- `chibil/CodeGen.cs` — gate IJW emission behind `TargetProfile.Ijw`.

---

## PART A — Pure-MSIL emit mode in CodeGen

### Task A1: Add `TargetProfile` to `CompilerOptions` and a `--target` flag

**Files:**
- Modify: `chibil/ChibiTypes.cs:415-422`
- Modify: `chibil/Driver.cs` (`ParseArgs`, around `:163`)
- Test: `tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs
using Chibil;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PureMsilEmitTests
{
    [Fact]
    public void Default_target_is_ijw()
    {
        var opts = new CompilerOptions();
        Assert.Equal(TargetProfile.Ijw, opts.Target);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Default_target_is_ijw`
Expected: FAIL — `TargetProfile` / `CompilerOptions.Target` do not exist.

- [ ] **Step 3: Implement**

```csharp
// chibil/ChibiTypes.cs — add near CompilerOptions
public enum TargetProfile { Ijw, CoreClr }

public class CompilerOptions
{
    public List<string> IncludePaths = new();
    public bool OptFpic;
    public bool OptFcommon = true;
    public string BaseFile;
    public DataModel DataModel = DataModel.LLP64;
    public TargetProfile Target = TargetProfile.Ijw;   // NEW
}
```

```csharp
// chibil/Driver.cs — in ParseArgs, before the "Ignored options" block (~line 162)
if (arg == "--target=coreclr") { Options.Target = TargetProfile.CoreClr; continue; }
if (arg == "--target=ijw")     { Options.Target = TargetProfile.Ijw; continue; }
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Default_target_is_ijw`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add chibil/ChibiTypes.cs chibil/Driver.cs tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs
git commit -m "feat(codegen): add TargetProfile option and --target flag"
```

---

### Task A2: Gate IJW machinery behind `TargetProfile.Ijw`

In CoreCLR mode, skip NEP thunks, `__unep@` field registration/slots, and the `__CxxPureMSILEntry` IJW shim. The compiler still emits plain managed methods, struct TypeDefs, `.data`/`.rdata`/`.bss`, and the `mscorlib` ref (the linker remaps the core-lib ref later).

**Files:**
- Modify: `chibil/CodeGen.cs` — `Generate` (`:3024-3036`), `RegisterMetadata`/`RegisterUnepFields` (`:881`, `:1185`), `EmitCxxPureMSILEntry` (`:2644`), `EmitNepMachinery` (`:2689`)
- Test: `tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// add to PureMsilEmitTests.cs
using System.Linq;

[Fact]
public void CoreClr_obj_has_no_ijw_sections_or_symbols()
{
    byte[] obj = TestCompiler.CompileToObj(
        "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
        TargetProfile.CoreClr);

    var coff = CoffFile.Parse(obj);
    Assert.Null(coff.FindSection(".nep"));
    Assert.Null(coff.FindSection(".rdata$ilfixup"));
    Assert.DoesNotContain(coff.Symbols, s => s.Name.StartsWith("__unep@"));
    Assert.DoesNotContain(coff.Symbols, s => s.Name.Contains("__mep@"));
    // plain managed method for fib still present
    Assert.Contains(coff.Symbols, s => s.Name == "fib" || s.Name == "_fib");
}
```

> `TestCompiler.CompileToObj` is a tiny helper you add in Step 3 that runs the tokenize→preprocess→parse→`CodeGen.Generate` pipeline in-process. `CoffFile` is `public` in `tools/coffobjdumper.cs` and is compiled into the test project (see Task B1 note about sharing sources — for the test project add `<Compile Include="..\..\tools\coffobjdumper.cs" />`).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr_obj_has_no_ijw`
Expected: FAIL — `.nep`/`__unep@`/`__mep@` are still emitted (and `TestCompiler` doesn't exist yet).

- [ ] **Step 3: Implement — add the test helper, then gate the emission**

Add the in-process compile helper:

```csharp
// tests/Chibil.Tests/CoreClr/TestCompiler.cs
using Chibil;
namespace Chibil.Tests.CoreClr;

static class TestCompiler
{
    public static byte[] CompileToObj(string source, TargetProfile target, string name = "t.c")
    {
        var opts = new CompilerOptions { Target = target, BaseFile = name };
        var types = new TypeSystem(opts.DataModel);
        var tok = new Tokenizer(opts, types);
        var pp = new Preprocessor(tok, opts, types);
        pp.InitMacros();
        var parser = new Parser(tok, opts, types);
        pp.SetParser(parser);
        Token t = tok.TokenizeString(name, source);   // see note
        t = pp.Preprocess(t);
        Obj prog = parser.Parse(t);
        var cg = new CodeGen(opts, tok, types);
        return cg.Generate(prog, "t.obj", System.IO.Path.GetFullPath(name));
    }
}
```

> If `Tokenizer` has no `TokenizeString`, write the source to a temp file and call the existing `TokenizeFile(path)` instead. Verify the actual method name in `chibil/Tokenizer.cs` and use it; do not invent an API.

Gate the IJW steps in `Generate` (`chibil/CodeGen.cs:3024-3036`):

```csharp
bool ijw = _options.Target == TargetProfile.Ijw;

ScanAddressTaken(prog);
RegisterMetadata(prog, objName);           // keep — but see RegisterUnepFields gate below
EmitGlobalDataBytesAndTokens(prog);
EmitFunctions(prog);
if (ijw) EmitCxxPureMSILEntry();           // IJW entry shim — CoreCLR uses linker-synthesized entry
if (ijw) EmitNepMachinery(prog);           // NEP thunks + ilfixup + __mep@
EmitGlobalDataRelocations(prog);
```

Inside `RegisterMetadata` (around the `RegisterUnepFields(prog)` call near `:881`), guard it:

```csharp
if (_options.Target == TargetProfile.Ijw)
    RegisterUnepFields(prog);
```

Inside `EmitGlobalDataBytesAndTokens` (the `__unep@` slot loop near `:2834-2850`), wrap the `__unep@` slot emission in `if (_options.Target == TargetProfile.Ijw) { ... }`.

> Leave `RegisterCxxPureMSILEntry` (registration) as-is; it only sets `_hasMain`/`_mainMethod`. The IJW *body+NEP* are what we skip. The linker reads `main` directly.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: PASS — both PureMsil tests green. Also run the full suite to confirm **no IJW regression**: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj`. Expected: all existing tests still PASS (IJW path unchanged because `Target` defaults to `Ijw`).

- [ ] **Step 5: Commit**

```bash
git add chibil/CodeGen.cs tests/Chibil.Tests/CoreClr/
git commit -m "feat(codegen): skip IJW machinery in CoreCLR target mode"
```

---

## PART B — Linker skeleton + object parsing

### Task B1: Create `tools/chibil-link` project that builds and runs

**Files:**
- Create: `tools/chibil-link/ChibilLink.csproj`
- Create: `tools/chibil-link/Program.cs`

- [ ] **Step 1: Write the failing test (smoke via build+run)**

There is no unit test here; the acceptance is that the project builds and prints usage. Create both files in Step 3, then verify in Step 4.

- [ ] **Step 2: (n/a — build is the check)**

- [ ] **Step 3: Implement**

```xml
<!-- tools/chibil-link/ChibilLink.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>chibil-link</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- Reuse parsing + metadata-merge machinery without duplicating it -->
    <Compile Include="..\coffobjdumper.cs" />
    <Compile Include="..\asm2obj\TokenMap.cs" />
    <Compile Include="..\asm2obj\EcmaSignatureRewriter.cs" />
  </ItemGroup>
</Project>
```

> `tools/coffobjdumper.cs` may declare its own `Main`/top-level statements. If it does, the linker exe will have two entry points. Resolve by guarding the dumper's entry behind `#if !CHIBIL_LINK` or by extracting `CoffFile` into the build without the dumper's `Main`. Check `coffobjdumper.cs` first; if it uses top-level statements, instead `<Compile Include>` only after refactoring its `Main` into a `class Dumper { public static int Run(...) }`. Pick the minimal change and note it in the commit.

```csharp
// tools/chibil-link/Program.cs
namespace ChibilLink;

public static class Program
{
    public static int Main(string[] args)
    {
        var opts = LinkOptions.Parse(args);
        if (opts == null || opts.Inputs.Count == 0)
        {
            Console.Error.WriteLine("usage: chibil-link [-o out.dll] [-l<lib>] <obj>...");
            return 1;
        }
        try
        {
            Linker.Run(opts);
            return 0;
        }
        catch (LinkException ex)
        {
            Console.Error.WriteLine($"chibil-link: {ex.Message}");
            return 1;
        }
    }
}

public sealed class LinkException : Exception
{
    public LinkException(string m) : base(m) { }
}

public sealed class LinkOptions
{
    public List<string> Inputs = new();
    public List<string> Libraries = new();   // from -l (e.g. "c" → libc.so.6)
    public string Output = "a.dll";

    public static LinkOptions Parse(string[] args)
    {
        var o = new LinkOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") { o.Output = args[++i]; continue; }
            if (a.StartsWith("-l")) { o.Libraries.Add(a[2..]); continue; }
            if (a.StartsWith("-")) { Console.Error.WriteLine($"unknown flag: {a}"); return null; }
            o.Inputs.Add(a);
        }
        return o;
    }
}

public static class Linker
{
    public static void Run(LinkOptions opts)
    {
        // Filled in across Parts B–F.
        throw new LinkException("not implemented yet");
    }
}
```

- [ ] **Step 4: Run it to verify build + usage**

Run: `dotnet run --project tools/chibil-link`
Expected: prints the `usage:` line and exits 1 (no inputs).

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/
git commit -m "feat(linker): scaffold chibil-link project and CLI"
```

---

### Task B2: `ObjectFile` — parse one `.obj` into a usable shape

**Files:**
- Create: `tools/chibil-link/ObjectFile.cs`
- Test: `tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs`

> The test project must compile the linker sources too. Add to `tests/Chibil.Tests/Chibil.Tests.csproj`:
> ```xml
> <ItemGroup>
>   <Compile Include="..\..\tools\chibil-link\ObjectFile.cs" />
>   <Compile Include="..\..\tools\chibil-link\LinkSymbolTable.cs" />
>   <Compile Include="..\..\tools\chibil-link\MetadataMerger.cs" />
>   <Compile Include="..\..\tools\chibil-link\SymbolResolver.cs" />
>   <Compile Include="..\..\tools\chibil-link\RelocationFixer.cs" />
>   <Compile Include="..\..\tools\chibil-link\EntrySynthesizer.cs" />
>   <Compile Include="..\..\tools\chibil-link\PeWriter.cs" />
>   <Compile Include="..\..\tools\coffobjdumper.cs" />
>   <Compile Include="..\..\tools\asm2obj\TokenMap.cs" />
>   <Compile Include="..\..\tools\asm2obj\EcmaSignatureRewriter.cs" />
> </ItemGroup>
> ```
> Add these `<Compile Include>` lines **once**, here; later tasks just reference the types. Guard against duplicate `CoffFile`/entry points the same way as Task B1.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
using ChibilLink;
using Chibil.Tests.CoreClr;
using System.Linq;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LinkerUnitTests
{
    [Fact]
    public void ObjectFile_reads_methods_from_chibil_obj()
    {
        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            Chibil.TargetProfile.CoreClr);

        var of = ObjectFile.Load(obj, "t.obj");

        Assert.True(of.Methods.Count >= 2);                       // fib + main
        Assert.Contains(of.Methods, m => m.Name == "fib");
        Assert.Contains(of.Methods, m => m.Name == "main");
        // each method exposes its IL bytes + token-reloc map
        var fib = of.Methods.First(m => m.Name == "fib");
        Assert.NotEmpty(fib.Il);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter ObjectFile_reads_methods`
Expected: FAIL — `ObjectFile` does not exist.

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/ObjectFile.cs
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

public sealed class ObjMethod
{
    public string Name;             // metadata method name
    public int OriginalToken;       // 0x06xxxxxx in this object
    public byte[] Il;               // raw IL bytes (operands hold ORIGINAL tokens)
    public Dictionary<int, int> TokenRelocs; // IL offset -> original token to remap
    public int MaxStack;
    public bool InitLocals;
    public StandaloneSignatureHandle LocalSig;
    public MethodDefinitionHandle Handle;
}

public sealed unsafe class ObjectFile
{
    public string Path;
    public Machine Machine;
    public CoffFile Coff;
    public MetadataReader Md;       // reader over .cormeta
    public List<ObjMethod> Methods = new();

    private byte[] _metaBytes; // pinned-lifetime backing for Md

    public static ObjectFile Load(byte[] bytes, string path)
    {
        var of = new ObjectFile { Path = path };
        of.Coff = CoffFile.Parse(bytes);
        of.Machine = (Machine)of.Coff.Header.Machine;

        var meta = of.Coff.FindSection(".cormeta")
            ?? throw new LinkException($"{path}: no .cormeta section");
        of._metaBytes = of.Coff.GetSectionData(meta.Value).ToArray();

        fixed (byte* p = of._metaBytes)
            of.Md = new MetadataReader(p, of._metaBytes.Length);

        var bodyLoc = of.Coff.BuildMethodBodyLocationMap();   // token -> (section, offset)
        foreach (var mh in of.Md.MethodDefinitions)
        {
            var md = of.Md.GetMethodDefinition(mh);
            int token = MetadataTokens.GetToken(mh);
            if ((md.ImplAttributes & MethodImplAttributes.ForwardRef) != 0) continue; // extern decl
            if (!bodyLoc.TryGetValue(token, out var loc)) continue;                    // no body

            var sec = of.Coff.GetSection(loc.SectionNumber);
            byte[] secData = of.Coff.GetSectionData(sec).ToArray();
            var relocMap = of.Coff.BuildTokenRelocationMap(sec); // section-offset -> token

            MethodBodyBlock body;
            fixed (byte* sp = secData)
            {
                var br = new BlobReader(sp + loc.Offset, secData.Length - loc.Offset);
                body = MethodBodyBlock.Create(br);
            }
            byte[] il = body.GetILBytes();

            // Translate section-relative reloc offsets to IL-relative offsets.
            int ilStartInSection = loc.Offset + (body.Size - il.Length); // header precedes IL
            var ilRelocs = new Dictionary<int, int>();
            foreach (var (secOff, tok) in relocMap)
            {
                int ilOff = secOff - ilStartInSection;
                if (ilOff >= 0 && ilOff + 4 <= il.Length) ilRelocs[ilOff] = tok;
            }

            of.Methods.Add(new ObjMethod
            {
                Name = of.Md.GetString(md.Name),
                OriginalToken = token,
                Il = il,
                TokenRelocs = ilRelocs,
                MaxStack = body.MaxStack,
                InitLocals = body.LocalVariablesInitialized,
                LocalSig = body.LocalSignature,
                Handle = mh,
            });
        }
        return of;
    }
}
```

> The `ilStartInSection` computation assumes `MethodBodyBlock.Size` includes the header; verify against `BuildMethodBodyLocationMap`'s offset convention in `coffobjdumper.cs` (it already maps token→body start). If `coffobjdumper` exposes a helper that returns IL-relative reloc offsets directly, prefer it. The Part-D fixup test (Task D1) is the oracle that confirms offsets are right.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter ObjectFile_reads_methods`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/ObjectFile.cs tests/Chibil.Tests/Chibil.Tests.csproj tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): ObjectFile parses methods+IL+token-relocs from .obj"
```

---

## PART C — Metadata merge (single object)

### Task C1: `MetadataMerger` copies one object's metadata into a shared builder

For Phase 0 there is exactly one input object, but the merger is written to accept N (called once per object, accumulating into the same `MetadataBuilder`). Reuse `TokenMap` (one per object) and `EcmaSignatureRewriter`. Copy: `<Module>` (synthesize one, shared across all objects), struct/array TypeDefs, FieldDefs, MethodDefs (+Params), and the AssemblyRef (dedup by name). Record original→merged token mapping per object for Part D.

**Files:**
- Create: `tools/chibil-link/MetadataMerger.cs`
- Test: `tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

[Fact]
public void Merger_copies_methods_into_shared_builder()
{
    byte[] obj = TestCompiler.CompileToObj(
        "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
        Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");

    var merger = new MetadataMerger();
    merger.AddObject(of);
    merger.Finish();

    // fib's original token now maps to a merged MethodDef token
    var fib = of.Methods.First(m => m.Name == "fib");
    int mapped = merger.MapToken(of, fib.OriginalToken);
    Assert.Equal(TableIndex.MethodDef, (TableIndex)((mapped >> 24) == 0x06 ? TableIndex.MethodDef : (TableIndex)0xff));
    Assert.True((mapped & 0x00FFFFFF) >= 1);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Merger_copies_methods`
Expected: FAIL — `MetadataMerger` does not exist.

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/MetadataMerger.cs
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

public sealed class MetadataMerger
{
    public readonly MetadataBuilder Md = new();
    private readonly Dictionary<ObjectFile, TokenMap> _maps = new();
    private TypeDefinitionHandle _moduleType;
    private bool _moduleCreated;

    // original (per-object) method token -> merged MethodDefinitionHandle, for entry/resolve.
    public readonly Dictionary<(ObjectFile, int), int> MethodTokenByOriginal = new();

    public void AddObject(ObjectFile of)
    {
        var map = new TokenMap(of.Md, Md);
        _maps[of] = map;

        // 1) AssemblyRefs (dedup by name+version via GetOrAddString/blob is automatic in builder,
        //    but AddAssemblyReference always adds a row — dedup manually by (name,version)).
        CopyAssemblyRefs(of, map);

        // 2) TypeRefs (resolution scope remapped).
        CopyTypeRefs(of, map);

        // 3) TypeDefs + Fields + Methods. chibil already emits a <Module> typedef (row 1)
        //    plus struct/array typedefs. Merge all <Module> members into ONE shared <Module>.
        CopyTypesFieldsMethods(of, map);
    }

    public void Finish()
    {
        // Module + Assembly identity rows are added by PeWriter (needs entry point first).
    }

    public int MapToken(ObjectFile of, int originalToken) => _maps[of].MapToken(originalToken);
    public TokenMap MapFor(ObjectFile of) => _maps[of];

    // --- helpers (sketch; mirror asm2obj/MetadataCopier.PhaseC patterns) ---

    private readonly Dictionary<string, AssemblyReferenceHandle> _asmRefByName = new();

    private void CopyAssemblyRefs(ObjectFile of, TokenMap map)
    {
        foreach (var h in of.Md.AssemblyReferences)
        {
            var ar = of.Md.GetAssemblyReference(h);
            string name = of.Md.GetString(ar.Name);
            if (!_asmRefByName.TryGetValue(name, out var outH))
            {
                outH = Md.AddAssemblyReference(
                    Md.GetOrAddString(name), ar.Version, default,
                    ar.PublicKeyOrToken.IsNil ? default : Md.GetOrAddBlob(of.Md.GetBlobBytes(ar.PublicKeyOrToken)),
                    ar.Flags,
                    ar.HashValue.IsNil ? default : Md.GetOrAddBlob(of.Md.GetBlobBytes(ar.HashValue)));
                _asmRefByName[name] = outH;
            }
            map.SetAssemblyRef(h, MetadataTokens.GetRowNumber(outH));
        }
    }

    private void CopyTypeRefs(ObjectFile of, TokenMap map)
    {
        foreach (var h in of.Md.TypeReferences) // MetadataReader exposes TypeReferences? if not, iterate rows
        {
            var tr = of.Md.GetTypeReference(h);
            var scope = tr.ResolutionScope.IsNil ? default : map.MapEntity(tr.ResolutionScope);
            var outH = Md.AddTypeReference(scope,
                Md.GetOrAddString(of.Md.GetString(tr.Namespace)),
                Md.GetOrAddString(of.Md.GetString(tr.Name)));
            map.SetTypeRef(h, MetadataTokens.GetRowNumber(outH));
        }
    }

    private void CopyTypesFieldsMethods(ObjectFile of, TokenMap map)
    {
        // Ensure a single shared <Module> exists as output TypeDef row 1.
        if (!_moduleCreated)
        {
            _moduleType = Md.AddTypeDefinition(System.Reflection.TypeAttributes.Class, default,
                Md.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            _moduleCreated = true;
        }

        // Walk this object's TypeDefs. Map its <Module> (row 1) to the shared module.
        // For non-<Module> typedefs (structs/arrays), copy them; for <Module>, reparent members.
        // NOTE: this is the substantive copy loop — model it on
        //   tools/asm2obj/MetadataCopier.PhaseB.cs (row prediction) + PhaseC.cs (population),
        //   minus the Drop/Flatten classification (chibil objs are already flattened).
        // Use EcmaSignatureRewriter.RewriteMethodSignature/RewriteFieldSignature with `map`
        // for every signature blob, and map.SetField/SetMethodDef to record output rows.
        new SingleObjectCopier(of.Md, Md, map, _moduleType, MethodTokenByOriginal, of).Run();
    }
}
```

```csharp
// tools/chibil-link/MetadataMerger.cs (same file) — the copy loop
internal sealed class SingleObjectCopier
{
    private readonly MetadataReader _r;
    private readonly MetadataBuilder _o;
    private readonly TokenMap _map;
    private readonly TypeDefinitionHandle _sharedModule;
    private readonly Dictionary<(ObjectFile, int), int> _methodByOrig;
    private readonly ObjectFile _of;

    public SingleObjectCopier(MetadataReader r, MetadataBuilder o, TokenMap map,
        TypeDefinitionHandle sharedModule, Dictionary<(ObjectFile, int), int> methodByOrig, ObjectFile of)
    { _r = r; _o = o; _map = map; _sharedModule = sharedModule; _methodByOrig = methodByOrig; _of = of; }

    public void Run()
    {
        // Fields first (so MethodDef field-list ranges are valid), then methods.
        // For Phase 0 (fib/main) there are no globals/structs; this loop copies 2 MethodDefs.
        foreach (var mh in _r.MethodDefinitions)
        {
            var md = _r.GetMethodDefinition(mh);
            var sigR = _r.GetBlobReader(md.Signature);
            var sigB = new BlobBuilder();
            EcmaSignatureRewriter.RewriteMethodSignature(sigR, _map, sigB);

            var outH = _o.AddMethodDefinition(
                md.Attributes, md.ImplAttributes,
                _o.GetOrAddString(_r.GetString(md.Name)),
                _o.GetOrAddBlob(sigB),
                bodyOffset: -1,                       // patched by PeWriter
                MetadataTokens.ParameterHandle(_o.GetRowCount(TableIndex.Param) + 1));

            foreach (var ph in md.GetParameters())
            {
                var p = _r.GetParameter(ph);
                _o.AddParameter(p.Attributes, _o.GetOrAddString(_r.GetString(p.Name)), p.SequenceNumber);
            }

            _map.SetMethodDef(mh, MetadataTokens.GetRowNumber(outH));
            _methodByOrig[(_of, MetadataTokens.GetToken(mh))] = MetadataTokens.GetToken(outH);
        }
    }
}
```

> **This is the riskiest task.** Keep it minimal for Phase 0 (methods only). Structs/globals/local-sig copying is added in Part F / hardening. The `BlobBuilder` type is `System.Reflection.Metadata.BlobBuilder` — add the `using`. If `MetadataReader` lacks a `TypeReferences`/`MethodDefinitions` enumerator with the exact name, iterate rows via `MetadataTokens` like `MetadataCopier.PhaseC.cs` does (it uses `GetTableRowCount` + `GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(r))`). Match the existing code's idiom.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Merger_copies_methods`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): MetadataMerger copies method metadata with token remap"
```

---

## PART D — Relocation fixup, entry synthesis, PE emission

### Task D1: `RelocationFixer` rewrites IL token operands original→merged

**Files:**
- Create: `tools/chibil-link/RelocationFixer.cs`
- Test: `tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
[Fact]
public void Fixer_remaps_recursive_call_token()
{
    byte[] obj = TestCompiler.CompileToObj(
        "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
        Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    var merger = new MetadataMerger();
    merger.AddObject(of);
    merger.Finish();

    var fib = of.Methods.First(m => m.Name == "fib");
    byte[] fixedIl = RelocationFixer.Fix(fib, of, merger);

    // every reloc offset now contains the MERGED token, not the original
    foreach (var (off, origTok) in fib.TokenRelocs)
    {
        int merged = merger.MapToken(of, origTok);
        int inIl = System.BitConverter.ToInt32(fixedIl, off);
        Assert.Equal(merged, inIl);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Fixer_remaps`
Expected: FAIL — `RelocationFixer` does not exist.

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/RelocationFixer.cs
namespace ChibilLink;

public static class RelocationFixer
{
    public static byte[] Fix(ObjMethod m, ObjectFile of, MetadataMerger merger)
    {
        byte[] il = (byte[])m.Il.Clone();
        foreach (var (ilOffset, originalToken) in m.TokenRelocs)
        {
            int merged = merger.MapToken(of, originalToken);
            if (merged == 0)
                throw new LinkException($"unresolved token 0x{originalToken:X8} in method {m.Name}");
            BitConverterWrite(il, ilOffset, merged);
        }
        return il;
    }

    private static void BitConverterWrite(byte[] buf, int off, int value)
    {
        buf[off + 0] = (byte)value;
        buf[off + 1] = (byte)(value >> 8);
        buf[off + 2] = (byte)(value >> 16);
        buf[off + 3] = (byte)(value >> 24);
    }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Fixer_remaps`
Expected: PASS. (If it fails because the reloc offsets are off by the method-body-header size, fix `ObjectFile.Load`'s `ilStartInSection` — this test is its oracle.)

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/RelocationFixer.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): RelocationFixer remaps IL token operands to merged tokens"
```

---

### Task D2: `EntrySynthesizer` adds a managed `Main(string[])` entry that calls C `main`

**Files:**
- Create: `tools/chibil-link/EntrySynthesizer.cs`
- Test: covered by the Part E integration test (exit code). Add a unit check that the entry MethodDef exists.

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
[Fact]
public void Entry_is_added_and_calls_main()
{
    byte[] obj = TestCompiler.CompileToObj(
        "int main(){return 7;}", Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    var merger = new MetadataMerger();
    merger.AddObject(of);
    merger.Finish();

    var entry = EntrySynthesizer.AddEntry(merger, of);
    Assert.False(entry.IlBytes.Length == 0);
    Assert.NotEqual(0, entry.MainMergedToken);   // resolved C main token
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Entry_is_added`
Expected: FAIL — `EntrySynthesizer` does not exist.

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/EntrySynthesizer.cs
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

public sealed class SynthEntry
{
    public MethodDefinitionHandle Handle;
    public byte[] IlBytes;
    public int MainMergedToken;
}

public static class EntrySynthesizer
{
    // Phase 0/1 entry: C `int main(void)` or `int main(int,char**)`.
    // For the MVP the synthesized entry calls main() (ignoring argv) and returns its int.
    public static SynthEntry AddEntry(MetadataMerger merger, ObjectFile mainObj)
    {
        var mainMethod = mainObj.Methods.FirstOrDefault(m => m.Name == "main")
            ?? throw new LinkException("no 'main' function found");
        int mainTok = merger.MapToken(mainObj, mainMethod.OriginalToken);

        // signature: static int32 Main(string[])
        var sigB = new BlobBuilder();
        new BlobEncoder(sigB).MethodSignature().Parameters(1,
            r => r.Type().Int32(),
            p => p.AddParameter().Type().SZArray().String());

        // IL: call int32 main(); ret    (main token is a MethodDef → 0x06xxxxxx)
        var il = new BlobBuilder();
        il.WriteByte(0x28);                 // call
        il.WriteInt32(mainTok);
        il.WriteByte(0x2A);                 // ret
        byte[] ilBytes = il.ToArray();

        var entryH = merger.Md.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static,
            MethodImplAttributes.IL,
            merger.Md.GetOrAddString("Main"),
            merger.Md.GetOrAddBlob(sigB),
            bodyOffset: -1,
            MetadataTokens.ParameterHandle(merger.Md.GetRowCount(TableIndex.Param) + 1));
        merger.Md.AddParameter(ParameterAttributes.None, merger.Md.GetOrAddString("args"), 1);

        return new SynthEntry { Handle = entryH, IlBytes = ilBytes, MainMergedToken = mainTok };
    }
}
```

> The synthesized entry references `main` as a raw MethodDef token written directly into the IL — no reloc table needed because `PeWriter` writes this body with the already-final token. The `Main(string[])` signature is required for a CoreCLR managed entry. For `int main(int,char**)`, Phase 1+ marshals `args`→`char**`; the MVP `puts` program uses `int main(void)`.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Entry_is_added`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/EntrySynthesizer.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): synthesize managed Main entry that calls C main"
```

---

### Task D3: `PeWriter` emits a pure-MSIL PE via `ManagedPEBuilder`

This wires the merged metadata + fixed IL + synthesized entry into an `app.dll` with `CorFlags.ILOnly`, and writes `app.runtimeconfig.json`. **Core-lib reference is the empirical knob** (§7 of the spec): first attempt keeps the `mscorlib` ref from the obj; if CoreCLR refuses to load, the resolver step remaps `mscorlib`→`System.Runtime`. The Part E run decides.

**Files:**
- Create: `tools/chibil-link/PeWriter.cs`
- Modify: `tools/chibil-link/MetadataMerger.cs` (expose `Md`, method bodies list)
- Test: Part E integration test is the oracle; add a unit check that bytes start with `MZ`.

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
[Fact]
public void PeWriter_emits_loadable_pe_header()
{
    byte[] obj = TestCompiler.CompileToObj("int main(){return 7;}", Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
    Assert.Equal((byte)'M', pe[0]);
    Assert.Equal((byte)'Z', pe[1]);
    Assert.True(pe.Length > 256);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter PeWriter_emits`
Expected: FAIL — `LinkPipeline`/`PeWriter` do not exist.

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/PeWriter.cs
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

public static class LinkPipeline
{
    public static byte[] LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs)
    {
        var merger = new MetadataMerger();
        ObjectFile mainObj = null;
        foreach (var of in objs)
        {
            merger.AddObject(of);
            if (of.Methods.Any(m => m.Name == "main")) mainObj = of;
        }
        merger.Finish();

        // Resolve cross-object + P/Invoke (Part F populates this; Phase 0 is a no-op).
        SymbolResolver.Resolve(merger, objs, libs);

        var entry = EntrySynthesizer.AddEntry(merger, mainObj
            ?? throw new LinkException("no object defines main"));

        return PeWriter.Write(merger, objs, entry);
    }
}

public static class PeWriter
{
    public static byte[] Write(MetadataMerger merger, IReadOnlyList<ObjectFile> objs, SynthEntry entry)
    {
        var md = merger.Md;

        // Module + Assembly identity rows (added last, before serialize).
        md.AddModule(0, md.GetOrAddString("app.dll"),
            md.GetOrAddGuid(Guid.Empty), default, default);
        md.AddAssembly(md.GetOrAddString("app"), new Version(1, 0, 0, 0), default, default,
            0, AssemblyHashAlgorithm.Sha1);

        // Lay out all method bodies into one IL stream; record offsets to patch MethodDef.bodyOffset.
        var ilBuilder = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(ilBuilder);
        var bodyOffsets = new Dictionary<int, int>();   // merged MethodDef token -> RVA-ish body offset

        foreach (var of in objs)
            foreach (var m in of.Methods)
            {
                byte[] il = RelocationFixer.Fix(m, of, merger);
                int mergedTok = merger.MapToken(of, m.OriginalToken);
                int off = AddBody(bodyEncoder, il, m.MaxStack, m.InitLocals,
                    MapLocalSig(m, of, merger));
                bodyOffsets[mergedTok] = off;
            }

        // synthesized entry body (already-final tokens, no locals).
        int entryOff = AddBody(bodyEncoder, entry.IlBytes, 8, false, default);
        bodyOffsets[MetadataTokens.GetToken(entry.Handle)] = entryOff;

        // Patch each MethodDef row's body offset. MetadataBuilder requires bodies via
        // the RVA passed when the row was added; since we added with -1, we instead
        // re-emit using the standard pattern: build a fresh MetadataBuilder is NOT needed —
        // use MethodBodyStreamEncoder offsets by adding bodies BEFORE AddMethodDefinition.
        // >>> See note: ordering must be bodies-first. Adjust MetadataMerger accordingly.

        var rootBuilder = new MetadataRootBuilder(md);
        var peHeader = PEHeaderBuilder.CreateExecutableHeader();
        var peBuilder = new ManagedPEBuilder(
            peHeader,
            rootBuilder,
            ilBuilder,
            entryPoint: entry.Handle,
            flags: CorFlags.ILOnly);

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }

    private static int AddBody(MethodBodyStreamEncoder enc, byte[] il, int maxStack, bool initLocals,
        StandaloneSignatureHandle localSig)
    {
        var inst = new InstructionEncoder(new BlobBuilder());
        // We already have raw IL bytes; write them directly.
        var code = new BlobBuilder();
        code.WriteBytes(il);
        return enc.AddMethodBody(
            instructionEncoder: WrapRawIl(il, maxStack),
            maxStack: maxStack,
            localVariablesSignature: localSig,
            attributes: initLocals ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None);
    }

    private static InstructionEncoder WrapRawIl(byte[] il, int maxStack)
    {
        var cb = new BlobBuilder();
        cb.WriteBytes(il);
        return new InstructionEncoder(cb);
    }

    private static StandaloneSignatureHandle MapLocalSig(ObjMethod m, ObjectFile of, MetadataMerger merger)
        => default; // Phase 0 fib/main have no locals sig that needs remap; Part F handles locals.
}
```

> **Two real ordering constraints to resolve here (the engineer must handle, the integration test is the oracle):**
> 1. **`MethodBodyStreamEncoder.AddMethodBody` returns the body offset**, and `MetadataBuilder.AddMethodDefinition` needs that offset as `bodyOffset`. So bodies must be encoded **before** the MethodDef rows are finalized. Restructure so `MetadataMerger` defers `AddMethodDefinition` until `PeWriter` has body offsets — i.e., the merger records *intended* rows, and `PeWriter` adds bodies, then adds MethodDef rows with real offsets, preserving the predicted row order so tokens still match `TokenMap`. This is the standard `System.Reflection.Metadata` emit pattern (see any `ManagedPEBuilder` sample). Adjust Task C1's copier to emit bodies-first if the prediction approach proves fragile.
> 2. **`AddMethodBody` wants an `InstructionEncoder`, not raw bytes.** `MethodBodyStreamEncoder` also has an overload taking a code-size; the cleanest path is `enc.AddMethodBody(int codeSize, maxStack, ExceptionRegionEncoder, hasSmallExceptionRegions, localVariablesSignature, attributes)` then copy raw IL into the returned `Blob`. Use that overload to write pre-assembled IL bytes. Verify the exact overload in the installed `System.Reflection.Metadata` version and use it; do not force the `InstructionEncoder` path for raw bytes.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter PeWriter_emits`
Expected: PASS — emits a PE beginning with `MZ`. (Functional correctness is proven in Part E.)

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/PeWriter.cs tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): emit pure-MSIL PE via ManagedPEBuilder (ILOnly)"
```

---

### Task D4: Wire `Linker.Run` + write `runtimeconfig.json`

**Files:**
- Modify: `tools/chibil-link/Program.cs` (`Linker.Run`)

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
[Fact]
public void Linker_writes_dll_and_runtimeconfig(System.String _ = null)
{
    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    System.IO.Directory.CreateDirectory(dir);
    string objPath = System.IO.Path.Combine(dir, "t.obj");
    System.IO.File.WriteAllBytes(objPath,
        TestCompiler.CompileToObj("int main(){return 7;}", Chibil.TargetProfile.CoreClr));
    string outPath = System.IO.Path.Combine(dir, "app.dll");

    Linker.Run(new LinkOptions { Inputs = { objPath }, Output = outPath });

    Assert.True(System.IO.File.Exists(outPath));
    Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "app.runtimeconfig.json")));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Linker_writes_dll`
Expected: FAIL — `Linker.Run` throws "not implemented yet".

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/Program.cs — replace Linker.Run
public static class Linker
{
    public static void Run(LinkOptions opts)
    {
        var objs = new List<ObjectFile>();
        foreach (var path in opts.Inputs)
            objs.Add(ObjectFile.Load(File.ReadAllBytes(path), path));

        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries);
        File.WriteAllBytes(opts.Output, pe);

        string cfg = Path.ChangeExtension(opts.Output, ".runtimeconfig.json");
        File.WriteAllText(cfg,
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"rollForward\": \"Major\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" }\n  }\n}\n");
    }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Linker_writes_dll`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/Program.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): wire pipeline and emit runtimeconfig.json"
```

---

## PART E — Driver integration + Phase 0 end-to-end (WSL)

### Task E1: Driver routes CoreCLR link to `chibil-link`

**Files:**
- Modify: `chibil/Driver.cs` — `RunLinker` (`:336`), arg parsing for `-l`

- [ ] **Step 1: Write the failing test**

```csharp
// add to PureMsilEmitTests.cs
[Fact]
public void Driver_coreclr_link_command_uses_chibil_link()
{
    // -### prints the commands without running them
    string outp = DriverProbe.CaptureCommands(
        new[] { "--target=coreclr", "-###", "x.c", "-lc", "-o", "app.dll" });
    Assert.Contains("chibil-link", outp);
    Assert.DoesNotContain("link.exe", outp);
}
```

> `DriverProbe.CaptureCommands` redirects `Console.Error` and runs `Driver.ParseArgs`-equivalent in `-###` mode. If the existing `Driver` only prints via `RunSubprocess` when `_optHashHashHash`, expose a seam: add an internal `BuildLinkerCommand(...)` method returning `string[]` and test that directly instead of capturing stderr. Prefer the seam — it's deterministic.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Driver_coreclr_link`
Expected: FAIL — Driver still emits `link.exe` unconditionally.

- [ ] **Step 3: Implement**

In `chibil/Driver.cs` add `-l` passthrough in `ParseArgs` (collect into a `List<string> _libs`), and branch `RunLinker`:

```csharp
private void RunLinker(List<string> inputs, string output)
{
    if (Options.Target == TargetProfile.CoreClr)
    {
        RunChibilLink(inputs, output);
        return;
    }
    // existing MSVC path unchanged:
    var arr = new List<string> { "link.exe", "/DEBUG", "/subsystem:console" };
    arr.Add($"/out:{output}");
    arr.Add("mscoree.lib");
    arr.AddRange(LdExtraArgs);
    arr.AddRange(inputs);
    RunSubprocess(arr.ToArray());
}

private string[] BuildLinkerCommand(List<string> inputs, string output)
{
    var arr = new List<string> { "chibil-link", "-o", output };
    foreach (var l in _libs) arr.Add($"-l{l}");
    arr.AddRange(inputs);
    return arr.ToArray();
}

private void RunChibilLink(List<string> inputs, string output)
{
    var cmd = BuildLinkerCommand(inputs, output);
    // resolve chibil-link next to this assembly, or via `dotnet run --project`
    var psi = CreateChibilLinkProcessStartInfo(cmd);
    if (_optHashHashHash) Console.Error.WriteLine(string.Join(" ", cmd));
    using var proc = System.Diagnostics.Process.Start(psi);
    proc?.WaitForExit();
    if (proc?.ExitCode != 0) Environment.Exit(1);
}
```

> Implement `CreateChibilLinkProcessStartInfo` to locate the built `chibil-link` (publish it next to `chibil`, or invoke `dotnet exec path/to/chibil-link.dll`). Mirror the existing `CreateSelfInvokeProcessStartInfo` logic at `Driver.cs:280`. Also `Options.Target` must be set from `--target=coreclr` (Task A1) **and** auto-default to `CoreClr` when `!OperatingSystem.IsWindows()` — add that in `Run()`.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Driver_coreclr_link`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add chibil/Driver.cs tests/Chibil.Tests/CoreClr/PureMsilEmitTests.cs
git commit -m "feat(driver): route CoreCLR target to chibil-link"
```

---

### Task E2: Phase 0 acceptance — `fib` runs on Linux, exit code 55

**Files:**
- Create: `tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs`
- Create: `samples/linux/build.sh`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs
using System.Diagnostics;
using Xunit;

namespace Chibil.Tests.CoreClr;

[Trait("Category", "wsl")]
public class LinuxEndToEndTests
{
    static bool WslAvailable() =>
        RunWsl("dotnet --version", out _).ExitCode == 0;

    [SkippableFact]
    public void Fib_runs_on_linux_exit_55()
    {
        Skip.IfNot(WslAvailable(), "WSL + dotnet not available");

        // Arrange: a freestanding program. fib(10) == 55.
        string proj = SetupRepoInWsl();   // helper: returns repo path inside WSL
        string c = "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}";
        WriteWslFile($"{proj}/_e2e/fib.c", c);

        // Act: compile + link + run, entirely in WSL.
        var build = RunWsl($"cd {proj} && bash samples/linux/build.sh _e2e/fib.c _e2e/app.dll", out string blog);
        Assert.True(build.ExitCode == 0, blog);
        var run = RunWsl($"cd {proj}/_e2e && dotnet app.dll; echo EXIT=$?", out string rlog);

        // Assert
        Assert.Contains("EXIT=55", rlog);
    }

    // RunWsl/WriteWslFile/SetupRepoInWsl: thin wrappers around `wsl.exe -- bash -lc "..."`.
    static (int ExitCode, string Out) RunWslImpl(string cmd, out string output) { /* Process wsl.exe */ ... }
    static Process RunWsl(string cmd, out string output) { ... }
    // ... (implement helpers; see note)
}
```

> Use the `Xunit.SkippableFact` package (already common) or implement skip via `Assert.True(WslAvailable())` returning early. Implement `RunWsl` with `ProcessStartInfo("wsl.exe"){ ArgumentList = { "--","bash","-lc", cmd } }`, capturing stdout+stderr. `SetupRepoInWsl` can `wsl wslpath` the current repo dir (the repo is already on a drive WSL can see at `/mnt/d/sandbox/chibil`).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Fib_runs_on_linux`
Expected: FAIL — `build.sh` does not exist (or skips if WSL absent — if it skips, fix WSL first, since Phase 0 acceptance requires it).

- [ ] **Step 3: Implement `build.sh`**

```bash
#!/usr/bin/env bash
# samples/linux/build.sh  —  self-contained C -> runnable .NET dll on Linux
# usage: build.sh <input.c> <output.dll> [-l<lib>...]
set -euo pipefail
SRC="$1"; OUT="$2"; shift 2
LIBS=("$@")
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

OBJ="$(mktemp --suffix=.obj)"
# 1) compile C -> pure-MSIL managed COFF obj
dotnet run -c Release --project "$ROOT/chibil" -- \
    --target=coreclr -cc1 -cc1-input "$SRC" -cc1-output "$OBJ"
# 2) link obj -> pure-MSIL PE + runtimeconfig.json
dotnet run -c Release --project "$ROOT/tools/chibil-link" -- \
    -o "$OUT" "${LIBS[@]}" "$OBJ"
rm -f "$OBJ"
echo "built $OUT"
```

> If `-cc1` mode in the Driver doesn't yet honor `--target=coreclr` (it should, via Task A1 parsing + Task E1 auto-default on non-Windows), confirm the flag reaches `CompilerOptions.Target` in the `-cc1` subprocess path (`Driver.Cc1`). The obj must be CoreCLR-profile or the linker will choke on `.nep` sections.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Fib_runs_on_linux`
Expected: PASS — `EXIT=55`.

**If it fails to load with a core-lib error** (e.g. "Could not load type System.ValueType from assembly mscorlib"): apply the §7 core-lib remap — in `MetadataMerger.CopyAssemblyRefs`, rewrite an incoming `mscorlib` ref to `System.Runtime` (Version `10.0.0.0`, the CoreCLR PKT `b03f5f7f11d50a3a`). Re-run. This is the one expected iterate-against-the-runtime point.

- [ ] **Step 5: Commit**

```bash
git add samples/linux/build.sh tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs
git commit -m "test(linux): Phase 0 end-to-end — fib runs on CoreCLR, exit 55"
```

---

## PART F — Phase 1: cross-object linking + libc P/Invoke

### Task F1: `LinkSymbolTable` + cross-object symbol resolution

Resolve an undefined extern in object A (e.g. `greet`) to a defined `MethodDef` in object B. The merger already maps each object's defined methods; the resolver maps A's *undefined* reference token to B's *merged* token.

**Files:**
- Create: `tools/chibil-link/LinkSymbolTable.cs`
- Create: `tools/chibil-link/SymbolResolver.cs`
- Test: `tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinkerUnitTests.cs
[Fact]
public void Resolver_links_call_across_objects()
{
    byte[] a = TestCompiler.CompileToObj("void greet(void); int main(){greet();return 0;}",
        Chibil.TargetProfile.CoreClr, "main.c");
    byte[] b = TestCompiler.CompileToObj("int puts(const char*); void greet(void){puts(\"hi\");}",
        Chibil.TargetProfile.CoreClr, "greet.c");
    var ofa = ObjectFile.Load(a, "main.obj");
    var ofb = ObjectFile.Load(b, "greet.obj");

    var merger = new MetadataMerger();
    merger.AddObject(ofa); merger.AddObject(ofb);
    merger.Finish();
    SymbolResolver.Resolve(merger, new[] { ofa, ofb }, new System.Collections.Generic.List<string>());

    // main's call to greet now maps to greet's merged MethodDef (a real 0x06 token)
    var mainM = ofa.Methods.First(m => m.Name == "main");
    foreach (var (off, origTok) in mainM.TokenRelocs)
    {
        int mapped = merger.MapToken(ofa, origTok);
        Assert.NotEqual(0, mapped);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Resolver_links_call`
Expected: FAIL — `SymbolResolver`/`LinkSymbolTable` do not exist (and A's `greet` token currently maps to nothing).

- [ ] **Step 3: Implement**

```csharp
// tools/chibil-link/LinkSymbolTable.cs
namespace ChibilLink;

public sealed class LinkSymbolTable
{
    // name -> (object, merged MethodDef token) for every DEFINED function across all objects.
    public readonly Dictionary<string, (ObjectFile Obj, int MergedToken)> Defined = new();

    public void AddDefined(ObjectFile of, MetadataMerger merger)
    {
        foreach (var m in of.Methods)
        {
            int merged = merger.MapToken(of, m.OriginalToken);
            Defined[m.Name] = (of, merged);   // last-wins; COMDAT fold acceptable for MVP
        }
    }
}
```

```csharp
// tools/chibil-link/SymbolResolver.cs
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

public static class SymbolResolver
{
    public static void Resolve(MetadataMerger merger, IReadOnlyList<ObjectFile> objs, List<string> libs)
    {
        var table = new LinkSymbolTable();
        foreach (var of in objs) table.AddDefined(of, merger);

        // For each object, find undefined external method references (ForwardRef MemberRefs /
        // MemberRefs whose name matches no merged MethodDef) and point their merged token
        // at either (a) a defined function in another object, or (b) a synthesized P/Invoke.
        foreach (var of in objs)
        {
            foreach (var mrh in of.Md.MemberReferences)   // iterate rows if no enumerator
            {
                var mr = of.Md.GetMemberReference(mrh);
                string name = of.Md.GetString(mr.Name);
                int originalTok = MetadataTokens.GetToken(mrh);

                if (merger.MapFor(of).MapToken(originalTok) != 0) continue; // already mapped

                int target;
                if (table.Defined.TryGetValue(name, out var def))
                    target = def.MergedToken;                                  // cross-object
                else
                    target = SynthesizePInvoke(merger, of, mr, name, libs);    // native import

                merger.MapFor(of).RecordExternal(originalTok, target);         // see note
            }
        }
    }

    private static int SynthesizePInvoke(MetadataMerger merger, ObjectFile of,
        MemberReference mr, string name, List<string> libs)
    {
        if (libs.Count == 0)
            throw new LinkException($"unresolved symbol '{name}' and no -l libraries given");

        string lib = MapLib(libs[0]);   // "c" -> "libc.so.6"; MVP uses the first -l
        var md = merger.Md;
        var moduleRef = merger.GetOrAddModuleRef(lib);

        // signature: copy the call-site MemberRef signature, remapping tokens.
        var sigR = of.Md.GetBlobReader(mr.Signature);
        var sigB = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(sigR, merger.MapFor(of), sigB);

        var methodH = md.AddMethodDefinition(
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static
                | System.Reflection.MethodAttributes.PinvokeImpl,
            System.Reflection.MethodImplAttributes.PreserveSig,
            md.GetOrAddString(name),
            md.GetOrAddBlob(sigB),
            bodyOffset: -1,
            MetadataTokens.ParameterHandle(md.GetRowCount(TableIndex.Param) + 1));

        md.AddMethodImport(methodH,
            MethodImportAttributes.CallingConventionCDecl | MethodImportAttributes.ExactSpelling
                | MethodImportAttributes.CharSetAnsi,
            md.GetOrAddString(name), moduleRef);

        return MetadataTokens.GetToken(methodH);
    }

    private static string MapLib(string l) => l switch
    {
        "c" => "libc.so.6",
        "m" => "libm.so.6",
        _   => l.Contains('.') ? l : $"lib{l}.so",
    };
}
```

> Two small additions to `MetadataMerger` this needs (add them):
> - `RecordExternal(int originalToken, int mergedTarget)` on `TokenMap` — store the override so `MapToken` returns `mergedTarget` for that original token. `TokenMap` already maps MemberRef rows; add a `_externalOverride` dictionary checked first in `MapToken`. (Alternatively, store the override in `MetadataMerger` and have `MapToken` consult it.)
> - `GetOrAddModuleRef(string name)` on `MetadataMerger` — dedup `ModuleRef` rows by name; `Md.AddModuleReference(Md.GetOrAddString(name))`.
> A P/Invoke `MethodDef` must have **no body**. Ensure `PeWriter` skips body emission for methods carrying `PinvokeImpl` (check the attribute and pass `bodyOffset` ≈ 0 / omit). Verify against `ManagedPEBuilder` expectations.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Resolver_links_call`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/LinkSymbolTable.cs tools/chibil-link/SymbolResolver.cs tools/chibil-link/MetadataMerger.cs tests/Chibil.Tests/CoreClr/LinkerUnitTests.cs
git commit -m "feat(linker): cross-object resolution + libc P/Invoke synthesis"
```

---

### Task F2: Phase 1 acceptance — `puts` via libc runs on Linux

**Files:**
- Modify: `tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// add to LinuxEndToEndTests.cs
[SkippableFact]
public void Puts_via_libc_prints_line()
{
    Skip.IfNot(WslAvailable(), "WSL + dotnet not available");
    string proj = SetupRepoInWsl();
    WriteWslFile($"{proj}/_e2e/greet.c",
        "int puts(const char*); void greet(void){puts(\"hello from chibil on linux\");}");
    WriteWslFile($"{proj}/_e2e/main.c", "void greet(void); int main(){greet();return 0;}");

    var build = RunWsl(
        $"cd {proj} && bash samples/linux/build.sh _e2e/main.c _e2e/app.dll -lc _e2e/greet.c", out string blog);
    Assert.True(build.ExitCode == 0, blog);
    var run = RunWsl($"cd {proj}/_e2e && dotnet app.dll; echo EXIT=$?", out string rlog);

    Assert.Contains("hello from chibil on linux", rlog);
    Assert.Contains("EXIT=0", rlog);
}
```

> `build.sh` currently takes one `.c`. Extend it to accept multiple `.c` inputs and `-l` flags: compile each `.c` to its own obj, then pass all objs + `-l` libs to `chibil-link`. Update Step 3.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Puts_via_libc`
Expected: FAIL — `build.sh` handles only a single source / no multi-obj link yet.

- [ ] **Step 3: Implement — multi-source `build.sh`**

```bash
#!/usr/bin/env bash
# samples/linux/build.sh — usage: build.sh <out.dll> <src1.c> [src2.c ...] [-l<lib>...]
# (first arg is now the output; remaining are sources and -l libs, intermixed)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT=""; SRCS=(); LIBS=()
for a in "$@"; do
  if [[ "$a" == -l* ]]; then LIBS+=("$a")
  elif [[ "$a" == *.dll ]]; then OUT="$a"
  elif [[ "$a" == *.c ]]; then SRCS+=("$a")
  fi
done
[[ -n "$OUT" ]] || { echo "no output .dll given"; exit 1; }

OBJS=()
for s in "${SRCS[@]}"; do
  o="$(mktemp --suffix=.obj)"
  dotnet run -c Release --project "$ROOT/chibil" -- \
      --target=coreclr -cc1 -cc1-input "$s" -cc1-output "$o"
  OBJS+=("$o")
done
dotnet run -c Release --project "$ROOT/tools/chibil-link" -- -o "$OUT" "${LIBS[@]}" "${OBJS[@]}"
rm -f "${OBJS[@]}"
echo "built $OUT from ${SRCS[*]}"
```

> Update both E2E tests to the new `build.sh` arg order (`<out.dll> <src...> [-l..]`). Re-run the Phase 0 test too to confirm no regression.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "Puts_via_libc|Fib_runs_on_linux"`
Expected: PASS — prints the line, `EXIT=0`; fib still `EXIT=55`.

- [ ] **Step 5: Commit**

```bash
git add samples/linux/build.sh tests/Chibil.Tests/CoreClr/LinuxEndToEndTests.cs
git commit -m "test(linux): Phase 1 end-to-end — puts via libc P/Invoke prints + exits 0"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** Pure-MSIL emit (A2) ↔ spec §4; linker pipeline (B–D) ↔ §5; P/Invoke auto-resolve (F1) ↔ §2/§5.4; entry+`.cctor` (D2) ↔ §5.6 (`.cctor` synthesis is stubbed for MVP — fib/puts need no dynamic initializers; add `??__E` ordering in Phase 2 per §6); core-lib knob (E2 fallback) ↔ §7; WSL testing (E2,F2) ↔ §8; Windows non-regression checked after A2 ↔ spec non-regression principle.
- **Known empirical points (expected to need a runtime iterate, not a redesign):** (1) method-body IL-relative reloc offset in `ObjectFile.Load` — oracle is Task D1; (2) `MethodBodyStreamEncoder.AddMethodBody` overload + bodies-before-MethodDef ordering in `PeWriter`/`MetadataMerger` — oracle is Task D3/E2; (3) `mscorlib`→`System.Runtime` core-lib remap — oracle is Task E2.
- **Deferred to Phase 2 (out of this plan, per spec §6/§9):** `printf`/varargs P/Invoke, struct/global metadata copy at scale, local-variable signature remap (`MapLocalSig` returns `default` now — fine for fib/main/greet which have no locals; **a function with locals will need it**, so if an early real program has locals, implement `MapStandaloneSignatureBlob` remap there), DOOM, ubuntu CI, `.cctor` static-init ordering, managed↔unmanaged callback thunks.
```
