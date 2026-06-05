# `--bind` extern-to-managed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a chibil-link `--bind=<sym>=<Ns.Type.Method>` (+ `-r/--reference <assembly.dll>`) capability that resolves an unresolved C extern to a **managed method in a referenced assembly** — a direct `call` — instead of synthesizing a native P/Invoke stub. This is the "internal libc" rewiring that lets `__chibil_syscall` bind to `Chibil.Pal.Syscall` so a chibil program runs with zero native imports.

**Architecture:** chibil-link already calls external managed methods (`NativeLibrary.Load`, `Marshal`, …) by adding an `AssemblyRef` → `TypeRef` → `MemberRef` and emitting a `call`. The new path reuses exactly that: for a symbol in the `--bind` map, create (dedup) an `AssemblyRef` to the referenced assembly (identity read from its `AssemblyDefinition`), a `TypeRef` to the declaring type, and a `MemberRef` whose signature is the call-site's own (rewritten via the object map, like `SynthesizePInvoke`), then `RecordExternal(originalToken → memberRefToken)`. The bind branch sits *before* P/Invoke synthesis in `SymbolResolver`.

**Tech Stack:** C# (.NET 10), `tools/chibil-link/` (the linker), `System.Reflection.Metadata` (`MetadataBuilder`, `MetadataReader`), xUnit (`tests/Chibil.Tests/`). The `--print-imports` machinery (`MetadataMerger.CollectImports`, `ImportRecord`) is reused for assertions. Run tests under `vcvars64`.

---

## File structure

| File | Responsibility | Change |
|---|---|---|
| `tools/chibil-link/Program.cs` | `LinkOptions` parsing | add `BindMap` (`Dictionary<string,string>`) + `References` (`List<string>`); parse `--bind=` and `-r`/`--reference` |
| `tools/chibil-link/ManagedReference.cs` | read a referenced assembly's identity | **new**: `AssemblyIdentity` record + `Read(path)` (name, version, culture, publicKeyToken) |
| `tools/chibil-link/MetadataMerger.cs` | metadata emission | add `BindMap`/`References` fields; `ResolveManagedBind(name, sigReader, of)` → `MemberRef` token (mirrors `GetOrAddNativeLibraryTypeRef` + `ReservePInvokeRow`) |
| `tools/chibil-link/SymbolResolver.cs` | extern resolution | add the bind branch before `SynthesizePInvoke` |
| `tools/chibil-link/LinkPipeline.cs` (or wherever `LinkToBytes` lives) | thread `BindMap`/`References` into the merger | pass-through |
| `tests/Chibil.Tests/CoreClr/BindManagedTests.cs` | tests | **new** |

---

### Task 1: `LinkOptions` parses `--bind` and `-r`/`--reference`

**Files:**
- Modify: `tools/chibil-link/Program.cs` (the `LinkOptions` class + `Parse`)
- Test: `tests/Chibil.Tests/CoreClr/LinkOptionsParseTests.cs` (existing — add cases)

- [ ] **Step 1: Write the failing tests.** Append to `tests/Chibil.Tests/CoreClr/LinkOptionsParseTests.cs`:

```csharp
    [Fact]
    public void Bind_parses_comma_separated_pairs()
    {
        var o = LinkOptions.Parse(new[] { "--bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp", "a.obj" });
        Assert.Equal("Chibil.Pal.Syscall", o.BindMap["__chibil_syscall"]);
        Assert.Equal("Chibil.Pal.GetTp", o.BindMap["__chibil_get_tp"]);
    }

    [Theory]
    [InlineData("-r Chibil.Pal.dll a.obj")]
    [InlineData("--reference Chibil.Pal.dll a.obj")]
    [InlineData("--reference=Chibil.Pal.dll a.obj")]
    public void Reference_accepts_forms(string cmd)
    {
        var o = LinkOptions.Parse(cmd.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(new[] { "Chibil.Pal.dll" }, o.References);
    }
```

- [ ] **Step 2: Run; verify FAIL** (members don't exist):
```
cmd /c '"C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 && dotnet test D:\sandbox\chibil\tests\Chibil.Tests\Chibil.Tests.csproj --filter "FullyQualifiedName~LinkOptionsParseTests.Bind_parses|FullyQualifiedName~LinkOptionsParseTests.Reference_accepts" --nologo'
```

- [ ] **Step 3: Implement.** In `LinkOptions` (Program.cs), add fields near the existing `PinvokeMap`/`Libraries`:
```csharp
    public Dictionary<string, string> BindMap = new();      // --bind=sym=Ns.Type.Method,...
    public List<string> References = new();                 // -r/--reference managed assemblies
```
In `Parse`, add a long-option case beside `--pinvoke` (the `switch (name)` around the existing `case "--pinvoke": ParsePinvoke(o, Val()); break;`):
```csharp
                    case "--bind": ParseBind(o, Val()); break;
                    case "--reference": o.References.Add(Val()); break;
```
Add a short-option handler beside the existing `-l`/`-L` `TryValue` block:
```csharp
            if (TryValue(a, "-r", args, ref i, out var rv)) { o.References.Add(rv); continue; }
```
Add the `ParseBind` helper next to `ParsePinvoke` (mirror it — same `name=value` shape, but the value is a dotted method path):
```csharp
    // --bind=sym=Ns.Type.Method[,sym=Ns.Type.Method...]
    private static void ParseBind(LinkOptions o, string spec)
    {
        if (string.IsNullOrEmpty(spec)) throw new LinkException("--bind requires sym=Ns.Type.Method entries");
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1) throw new LinkException($"bad --bind entry: {pair}");
            o.BindMap[pair[..eq]] = pair[(eq + 1)..];
        }
    }
```
Add a help line in `Usage` beside `--pinvoke`:
```csharp
        "  --bind=<s=N.T.M,...>     resolve C symbol <s> to managed method N.T.M in a -r assembly\n" +
        "  -r, --reference <dll>    reference a managed assembly (for --bind targets)\n" +
```

- [ ] **Step 4: Run; verify PASS.** Same command as Step 2.

- [ ] **Step 5: Commit.**
```
git add tools/chibil-link/Program.cs tests/Chibil.Tests/CoreClr/LinkOptionsParseTests.cs
git commit -m "chibil-link: parse --bind and -r/--reference options"
```

---

### Task 2: Read a referenced assembly's identity

**Files:**
- Create: `tools/chibil-link/ManagedReference.cs`
- Test: `tests/Chibil.Tests/CoreClr/BindManagedTests.cs` (new)

- [ ] **Step 1: Write the failing test.** Create `tests/Chibil.Tests/CoreClr/BindManagedTests.cs`:
```csharp
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class BindManagedTests
{
    [Fact]
    public void Reads_assembly_identity_from_corelib()
    {
        // System.Private.CoreLib is always present; read its identity.
        string corelib = typeof(object).Assembly.Location;
        var id = ManagedReference.Read(corelib);
        Assert.Equal("System.Private.CoreLib", id.Name);
        Assert.True(id.Version.Major >= 8);
        Assert.NotEmpty(id.PublicKeyToken);   // corelib is strong-named
    }
}
```

- [ ] **Step 2: Run; verify FAIL** (`ManagedReference` doesn't exist).
```
cmd /c '"...vcvars64.bat" >nul 2>&1 && dotnet test D:\sandbox\chibil\tests\Chibil.Tests\Chibil.Tests.csproj --filter "FullyQualifiedName~BindManagedTests.Reads_assembly_identity" --nologo'
```

- [ ] **Step 3: Implement.** Create `tools/chibil-link/ManagedReference.cs`:
```csharp
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

/// <summary>Identity of a referenced managed assembly, read from its
/// AssemblyDefinition — enough to emit a matching AssemblyRef in the output.</summary>
public sealed record AssemblyIdentity(string Name, Version Version, string Culture, byte[] PublicKeyToken);

public static class ManagedReference
{
    public static AssemblyIdentity Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var asm = md.GetAssemblyDefinition();
        string name = md.GetString(asm.Name);
        string culture = asm.Culture.IsNil ? "" : md.GetString(asm.Culture);
        // A strong-named assembly stores a full public KEY; the AssemblyRef needs the
        // 8-byte TOKEN (low 8 bytes of SHA-1 of the key, reversed). Compute it.
        byte[] pk = asm.PublicKey.IsNil ? Array.Empty<byte>() : md.GetBlobBytes(asm.PublicKey);
        byte[] token = pk.Length == 0 ? Array.Empty<byte>() : PublicKeyToken(pk);
        return new AssemblyIdentity(name, asm.Version, culture, token);
    }

    private static byte[] PublicKeyToken(byte[] publicKey)
    {
        byte[] hash = System.Security.Cryptography.SHA1.HashData(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++) token[i] = hash[hash.Length - 1 - i];   // last 8 bytes, reversed
        return token;
    }
}
```

- [ ] **Step 4: Run; verify PASS.** Same filter as Step 2.

- [ ] **Step 5: Commit.**
```
git add tools/chibil-link/ManagedReference.cs tests/Chibil.Tests/CoreClr/BindManagedTests.cs
git commit -m "chibil-link: read referenced-assembly identity (AssemblyIdentity)"
```

---

### Task 3: Emit AssemblyRef/TypeRef/MemberRef for a bound symbol

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs`

**Context:** `GetOrAddNativeLibraryTypeRef()` (~line 1381) is the exact template: it deduplicates an `AssemblyRef` via `_assemblyRefByName`, then `Builder.AddTypeReference(asmRef, ns, name)`, then callers do `Builder.AddMemberReference(typeRef, methodName, sigBlob)`. `SynthesizePInvoke` (in `SymbolResolver.cs`) shows the signature rewrite: `EcmaSignatureRewriter.RewriteMethodSignature(sigReader, merger.MapFor(of), sigB)`.

- [ ] **Step 1: Add the bind inputs + resolver to `MetadataMerger`.** Near the other public link inputs, add:
```csharp
    public IReadOnlyDictionary<string, string> BindMap = new Dictionary<string, string>();
    public IReadOnlyList<AssemblyIdentity> ReferenceIdentities = new List<AssemblyIdentity>();
    private readonly Dictionary<(string asm, string ns, string type), EntityHandle> _externTypeRefs = new();
```

- [ ] **Step 2: Add `ResolveManagedBind`.** Add this method (model the AssemblyRef/TypeRef on `GetOrAddNativeLibraryTypeRef`, the signature on `SynthesizePInvoke`):
```csharp
    /// <summary>Bind C symbol <paramref name="name"/> to the managed method named by
    /// <paramref name="dotted"/> (e.g. "Chibil.Pal.Syscall") in one of the referenced
    /// assemblies. Returns the MemberRef token to redirect the extern's call to.</summary>
    public int ResolveManagedBind(string name, string dotted, BlobReader sigReader, ObjectFile of)
    {
        // Split "Ns.Sub.Type.Method" -> ("Ns.Sub", "Type", "Method"). The method is the
        // last segment; the type is the segment before it; the rest is the namespace.
        int lastDot = dotted.LastIndexOf('.');
        if (lastDot < 0) throw new LinkException($"--bind target '{dotted}' must be Namespace.Type.Method");
        string method = dotted[(lastDot + 1)..];
        string typeFull = dotted[..lastDot];
        int typeDot = typeFull.LastIndexOf('.');
        string ns = typeDot < 0 ? "" : typeFull[..typeDot];
        string typeName = typeDot < 0 ? typeFull : typeFull[(typeDot + 1)..];

        // The first referenced assembly is the bind target (v1: a single PAL assembly).
        if (ReferenceIdentities.Count == 0)
            throw new LinkException($"--bind needs a -r reference assembly for '{name}'");
        var id = ReferenceIdentities[0];

        if (!_assemblyRefByName.TryGetValue(id.Name, out var asmRef))
        {
            asmRef = Builder.AddAssemblyReference(
                Builder.GetOrAddString(id.Name), id.Version,
                string.IsNullOrEmpty(id.Culture) ? default : Builder.GetOrAddString(id.Culture),
                id.PublicKeyToken.Length == 0 ? default : Builder.GetOrAddBlob(id.PublicKeyToken),
                default, default);
            _assemblyRefByName[id.Name] = asmRef;
        }
        var typeKey = (id.Name, ns, typeName);
        if (!_externTypeRefs.TryGetValue(typeKey, out var typeRef))
        {
            typeRef = Builder.AddTypeReference(asmRef, Builder.GetOrAddString(ns), Builder.GetOrAddString(typeName));
            _externTypeRefs[typeKey] = typeRef;
        }
        // The MemberRef signature is the call-site's own signature, token-remapped.
        var sigB = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(sigReader, MapFor(of), sigB);
        var mr = Builder.AddMemberReference(typeRef, Builder.GetOrAddString(method), Builder.GetOrAddBlob(sigB));
        return MetadataTokens.GetToken(mr);
    }
```
(Confirm `MapFor`, `EcmaSignatureRewriter`, `Builder`, `_assemblyRefByName`, and `MetadataTokens` are all accessible here — they are used elsewhere in this file / in `SymbolResolver`.)

- [ ] **Step 3: No standalone test yet** (exercised end-to-end in Task 5). Build to confirm it compiles:
```
dotnet build tools/chibil-link/chibil-link.csproj -v q --nologo
```
Expected: Build succeeded.

- [ ] **Step 4: Commit.**
```
git add tools/chibil-link/MetadataMerger.cs
git commit -m "chibil-link: ResolveManagedBind — emit AssemblyRef/TypeRef/MemberRef for a bound symbol"
```

---

### Task 4: Bind matched externs in `SymbolResolver` (before P/Invoke)

**Files:**
- Modify: `tools/chibil-link/SymbolResolver.cs`
- Modify: `tools/chibil-link/MetadataMerger.cs` (thread `BindMap`/`ReferenceIdentities` from options — see Task 6 wiring)

**Context:** In `SymbolResolver.Resolve`, the per-MemberRef loop resolves each external function reference: first `table.DefinedMethodToken` (defined symbol → redirect), else `SynthesizePInvoke`. The bind branch goes between them.

- [ ] **Step 1: Add the bind branch.** In `SymbolResolver.cs`, find the block (after the `table.DefinedMethodToken.TryGetValue(name, out int definedToken)` redirect, before the "Native import: synthesize … P/Invoke" block). Insert:
```csharp
                // Managed bind (--bind): resolve this C symbol to a managed method in a
                // referenced assembly (a direct call), not a native P/Invoke stub.
                if (merger.BindMap.TryGetValue(name, out string dotted))
                {
                    var sr = md.GetBlobReader(mr.Signature);
                    int bindTok = merger.ResolveManagedBind(name, dotted, sr, of);
                    map.RecordExternal(originalToken, bindTok);
                    continue;
                }
```
(`name`, `mr`, `md`, `of`, `map`, `originalToken` are all in scope in that loop — they are used by the surrounding defined/P-Invoke branches.)

- [ ] **Step 2: Build.** `dotnet build tools/chibil-link/chibil-link.csproj -v q --nologo` → Build succeeded.

- [ ] **Step 3: Commit.**
```
git add tools/chibil-link/SymbolResolver.cs
git commit -m "chibil-link: bind --bind symbols to managed methods before P/Invoke synthesis"
```

---

### Task 5: Wire options → merger, and the acceptance test

**Files:**
- Modify: the link entry that builds the `MetadataMerger` from `LinkOptions` (`git grep -nE "new MetadataMerger|LinkToBytes|class LinkPipeline|class PeWriter" tools/chibil-link/` — set `merger.BindMap` and `merger.ReferenceIdentities`).
- Test: `tests/Chibil.Tests/CoreClr/BindManagedTests.cs`

- [ ] **Step 1: Write the failing acceptance test.** Append to `BindManagedTests.cs`:
```csharp
    [Fact]
    public void Bound_symbol_resolves_managed_and_is_not_a_native_import()
    {
        // A program that calls an unresolved extern `__chibil_echo`. With --bind, it
        // resolves to a managed method in a referenced assembly (no -l, no P/Invoke).
        const string src =
            "long __chibil_echo(long);\n" +
            "int main(void){ return (int)__chibil_echo(41); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");

        var opts = LinkOptions.Parse(new[] {
            "--bind=__chibil_echo=Chibil.PalTest.Echo.Run",
            "-r", typeof(object).Assembly.Location,   // any real assembly: identity is read, method name is trusted
        });

        var imports = new List<ImportRecord>();
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, opts.Libraries, opts.ExportClass,
            opts.PinvokeMap, opts.Debug, opts.Shared, "t", opts.Entry, opts.LibSearchPaths,
            imports, opts.BindMap, opts.References);   // <-- new trailing params (Task 5 wiring)

        Assert.NotEmpty(pe);                                          // linked (not "unresolved symbol")
        Assert.DoesNotContain(imports, i => i.Name == "__chibil_echo"); // not a native import
    }
```
(If `LinkToBytes`'s signature differs, adapt the call; the point is to pass `BindMap` + `References` through. If wiring through `LinkToBytes` is too invasive, set `merger.BindMap`/`merger.ReferenceIdentities` at the merger-construction site and have the test drive that path instead — keep the two assertions.)

- [ ] **Step 2: Run; verify FAIL** — currently `__chibil_echo` is unresolved → `LinkToBytes` throws "unresolved symbol '__chibil_echo'".

- [ ] **Step 3: Implement the wiring.** At the `MetadataMerger` construction site, set:
```csharp
        merger.BindMap = opts.BindMap;
        merger.ReferenceIdentities = opts.References.Select(ManagedReference.Read).ToList();
```
Thread `BindMap` + `References` through `LinkToBytes` (add two trailing optional params: `IReadOnlyDictionary<string,string> bindMap = null, IReadOnlyList<string> references = null`) down to the merger, mirroring how `importsOut` was threaded in the `--print-imports` work. Read each reference's identity with `ManagedReference.Read` once.

- [ ] **Step 4: Run; verify PASS.** Both assertions hold (links; `__chibil_echo` absent from imports).

- [ ] **Step 5: Run the full suite (vcvars64) for regressions.** Expected: prior pass count + the new Bind tests; only the pre-existing environmental `ManglingInteropTests.Data_MsvcDefine_ChibiConsume` fails.

- [ ] **Step 6: Commit.**
```
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/BindManagedTests.cs
git commit -m "chibil-link: wire --bind/-r into the linker; acceptance test (managed bind, no native import)"
```

---

### Task 6: End-to-end run — a bound call actually invokes managed code

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/BindManagedTests.cs`
- Fixture: a tiny managed helper assembly built by the test (or referenced from the test assembly itself).

**Context:** Task 5 proves the *metadata* is correct (bound, not imported). This task proves the bound call *runs*. The cleanest fixture: bind to a `public static long` method that already exists in the **test assembly itself** (so no separate build), then link to a dll and load+invoke `main` via reflection / the existing run harness (`git grep -n "DotnetHostRunner\|DiskRunner\|LoadFromAssemblyPath\|InvokeMember" tests/Chibil.Tests/CoreClr/`).

- [ ] **Step 1: Add the target method** to the test file (a `public static` so it's referenceable):
```csharp
    public static class BindTarget
    {
        public static long Plus1(long x) => x + 1;
    }
```

- [ ] **Step 2: Write the failing run test.**
```csharp
    [Fact]
    public void Bound_call_invokes_the_managed_method()
    {
        const string src =
            "long bt_plus1(long);\n" +
            "int main(void){ return (int)bt_plus1(41); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        string thisAsm = typeof(BindTarget).Assembly.Location;
        var opts = LinkOptions.Parse(new[] {
            "--bind=bt_plus1=Chibil.Tests.CoreClr.BindManagedTests+BindTarget.Plus1",
            "-r", thisAsm,
        });
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, opts.Libraries, opts.ExportClass,
            opts.PinvokeMap, opts.Debug, opts.Shared, "t", opts.Entry, opts.LibSearchPaths,
            null, opts.BindMap, opts.References);
        int exit = <load `pe` into an AssemblyLoadContext, run its entry, capture exit code — use the existing run harness>;
        Assert.Equal(42, exit);
    }
```
NOTE on the bound name: a nested type uses `+` in the reflection name but chibil-link's `ResolveManagedBind` splits on `.`. **Either** make `BindTarget` a top-level type in the test namespace (then the dotted name is `Chibil.Tests.CoreClr.BindTarget.Plus1`, splitting cleanly) — **prefer this** — or extend `ResolveManagedBind` to handle `+`. Use a top-level `BindTarget` type to keep the splitter simple.

- [ ] **Step 3: Run; verify FAIL** (no run harness wired / method not invoked).

- [ ] **Step 4: Implement** using the existing CoreCLR run harness (found in Step-2 grep — e.g. load the PE bytes via `AssemblyLoadContext.LoadFromStream`, find the entry point, invoke, read the returned int). Keep `BindTarget` top-level (`public static class BindTarget { public static long Plus1(long x) => x + 1; }` directly in the `Chibil.Tests.CoreClr` namespace) so the bind target is `Chibil.Tests.CoreClr.BindTarget.Plus1`.

- [ ] **Step 5: Run; verify PASS** (exit 42 — the bound call ran managed code).

- [ ] **Step 6: Run the full suite; confirm no regressions. Commit.**
```
git add tests/Chibil.Tests/CoreClr/BindManagedTests.cs
git commit -m "chibil-link: end-to-end test — a bound extern call invokes managed code"
```

---

## Notes / out of scope
- **Single reference assembly (v1):** `ResolveManagedBind` binds to `ReferenceIdentities[0]`. Multiple `-r` assemblies with per-symbol routing (which assembly owns which method) is a follow-up — add a `sym=Asm!Ns.Type.Method` form if needed.
- **No method-existence verification:** like P/Invoke entry points, `--bind` trusts the dotted name; a wrong name links fine and fails at run time with `MissingMethodException`. A verification pass (resolve the method in the referenced metadata, check the signature matches) is a hardening follow-up.
- **Signature compatibility:** the emitted MemberRef uses the C call-site signature (`long(long…)`). The managed method must have a matching blittable signature. Non-blittable params (string, structs) are out of scope for v1 — the PAL's seam is all `long`.
