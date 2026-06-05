# Build-system reorg + sh→MSBuild migration (Phase 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize all .NET build output under `./build/` and convert the primary chibil pipeline (managed-musl objects → QuickJS-on-musl → consumer oracle) from WSL bash scripts into cross-platform MSBuild tasks/`.proj` that build and run in Visual Studio on Windows with no WSL.

**Architecture:** A root `Directory.Build.props` switches every project to the .NET artifacts output layout (`./build/`). A new task assembly `Chibil.Build.Tasks` exposes `ChibilCompile`/`ChibilLink` MSBuild tasks that shell out to `dotnet chibil.dll`/`dotnet chibil-link.dll` (via `ProcessStartInfo.ArgumentList`, incremental + parallel). Two `.proj` files plus a root `build.proj` orchestrator drive the managed-musl + QuickJS build; an `env.sh` keeps the not-yet-converted secondary scripts working under the new paths.

**Tech Stack:** .NET 10 SDK, MSBuild custom tasks (`Microsoft.Build.Utilities.Core`), the existing `chibil`/`chibil-link` CLIs, the managed PAL (`Chibil.Pal`).

**Reference spec:** `docs/superpowers/specs/2026-06-06-build-system-msbuild-migration-design.md`

**Standing constraints (from the session):**
- Commit ONLY at the commit steps below (the user authorized this plan's execution).
- Do NOT modify any upstream file except the single `/build/` line in `.gitignore`. Upstream = `chibil/`, `crt/`, `tests/`, `scenarios/`, `tools/asm2obj/`, `.github/`, `samples/`, `README.md`, `THIRD-PARTY-NOTICES.md`.
- Do NOT commit anything under `targets/musl-1.2.6/`, `targets/quickjs-2025-09-13/`, `targets/micropython/` (git-ignored third-party).
- Commit messages end with: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Current branch is `master` (already ahead of origin). Per the session, work proceeds on `master`; do NOT push.
- Verifications that build the chibil toolchain run on Windows (PowerShell). The one WSL check (Task 7) uses `wsl bash` via PowerShell to avoid Git-Bash path mangling.

---

## File Structure

**New files:**
- `Directory.Build.props` — root; turns on artifacts output → `./build/`.
- `tools/chibil-build/Chibil.Build.Tasks.csproj` — the task assembly.
- `tools/chibil-build/ChibilCompile.cs` — `ChibilCompile` task.
- `tools/chibil-build/ChibilLink.cs` — `ChibilLink` task.
- `tools/chibil-build/build/Chibil.Build.props` — `$(ChibilDll)` / `$(ChibilLinkDll)` / `$(ChibilPalDll)` / `$(ChibilBuildTasksDll)` path properties.
- `tools/chibil-build/build/Chibil.Build.targets` — `UsingTask` registrations.
- `tools/chibil-build/test/smoke.proj` + `tools/chibil-build/test/smoke.c` — task smoke fixture.
- `targets/managed-musl/ManagedMusl.proj` — replaces `build-managed-musl.sh`.
- `targets/quickjs/QuickJsManaged.proj` — replaces `qjs-on-musl-api.sh` (+ `Oracle` target).
- `build.proj` — root orchestrator (`Tools` → `ManagedMusl` → `QuickJs`).
- `targets/build/env.sh` — single source of truth for tool paths used by secondary scripts.

**Modified files:**
- `.gitignore` — add `/build/`.
- `targets/build/*.sh` (secondary scripts) — `source` `env.sh` instead of redefining `CH`/`LINK`/`PAL`.
- `CompileQuickJS.md`, `CompileBash.md`, `CompileMicroPython.md`, `DebugAndConsume.md`, `targets/quickjs-api-consumer/README.md` — new commands/paths.

---

## Task 1: Centralize build output via the artifacts layout

**Files:**
- Create: `Directory.Build.props`
- Modify: `.gitignore`

- [ ] **Step 1: Create the root `Directory.Build.props`**

Create `Directory.Build.props`:

```xml
<Project>
  <!--
    Centralized output: every project writes to ./build (the .NET 8+ artifacts
    layout): build/bin/<Project>/<config>/, build/obj/..., build/publish/...
    This file is auto-imported by every project under the repo, so NO .csproj is
    edited. The upstream scenarios/Directory.Build.props shadows this for that one
    project (CoffEmitterTests keeps its local bin/obj) - accepted exception.
  -->
  <PropertyGroup>
    <UseArtifactsOutput>true</UseArtifactsOutput>
    <ArtifactsPath>$(MSBuildThisFileDirectory)build</ArtifactsPath>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Add `/build/` to `.gitignore`**

Append `/build/` to `.gitignore` (the only upstream-file edit). The file becomes:

```
references/
[Bb]in/
[Oo]bj/
tmp/
/*.obj
.vs/
myapp.dll
/build/
```

- [ ] **Step 3: Verify output redirects, and existing build/test still works**

Run (Windows):
```
cd D:\sandbox\chibil
dotnet build chibil/Chibil.csproj -c Debug -v q --nologo
dir build\bin\Chibil\debug\chibil.dll
```
Expected: `Build succeeded`, and `build\bin\Chibil\debug\chibil.dll` exists.

Then verify the test project still builds under the new layout:
```
dotnet build tests/Chibil.Tests/Chibil.Tests.csproj -c Debug -v q --nologo
dir build\bin\Chibil.Tests\debug\Chibil.Tests.dll
```
Expected: `Build succeeded`, the test dll exists under `build\bin`.

And confirm the shadowed `scenarios` project still builds (it keeps its own output):
```
dotnet build scenarios/CoffEmitterTests.csproj -c Debug -v q --nologo
```
Expected: `Build succeeded` (output stays under `scenarios/bin`, the documented exception).

- [ ] **Step 4: Commit**

```
git add Directory.Build.props .gitignore
git commit -m "build: centralize output to ./build via .NET artifacts layout

Add a root Directory.Build.props enabling UseArtifactsOutput (ArtifactsPath=./build)
so every project's bin/obj collapse under ./build with no .csproj edits. Add /build/
to .gitignore. scenarios/Directory.Build.props (upstream) shadows the root for
CoffEmitterTests, which keeps its local bin/obj - accepted exception.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `Chibil.Build.Tasks` assembly with the `ChibilCompile` and `ChibilLink` tasks

**Files:**
- Create: `tools/chibil-build/Chibil.Build.Tasks.csproj`
- Create: `tools/chibil-build/ChibilCompile.cs`
- Create: `tools/chibil-build/ChibilLink.cs`

- [ ] **Step 1: Create the task project**

Create `tools/chibil-build/Chibil.Build.Tasks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>Chibil.Build.Tasks</AssemblyName>
    <!-- A task assembly: don't copy the MSBuild reference assemblies (the host
         provides them at runtime). -->
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Build.Utilities.Core" Version="17.11.4">
      <PrivateAssets>all</PrivateAssets>
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Implement `ChibilCompile`**

Create `tools/chibil-build/ChibilCompile.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Chibil.Build
{
    /// <summary>Compiles C translation units to .obj by invoking `dotnet chibil.dll`,
    /// in parallel and incrementally. Mirrors the bash build loops (build-managed-musl.sh,
    /// qjs-on-musl-api.sh). Args are passed via ArgumentList so paths/quotes never need
    /// shell escaping (e.g. -DCONFIG_VERSION="2025-09-13").</summary>
    public sealed class ChibilCompile : Task
    {
        [Required] public string ChibilDll { get; set; }       // path to chibil.dll
        [Required] public ITaskItem[] Sources { get; set; }    // .c files
        [Required] public string OutputDir { get; set; }
        public string SourceRoot { get; set; }                 // for flat obj naming
        public string[] IncludeDirs { get; set; } = Array.Empty<string>();
        public string[] Defines { get; set; } = Array.Empty<string>();
        public string ForceInclude { get; set; }
        public string ExtraArgs { get; set; } = "";            // e.g. "--target=coreclr -nostdinc -mlp64"
        public string[] ExtraInputs { get; set; } = Array.Empty<string>(); // extra incremental deps (headers)
        public bool ContinueOnError { get; set; } = false;
        public int MaxParallel { get; set; } = 0;              // 0 => CPU count
        [Output] public ITaskItem[] Objects { get; set; }

        public override bool Execute()
        {
            Directory.CreateDirectory(OutputDir);
            string root = SourceRoot != null ? Path.GetFullPath(SourceRoot) : null;
            string[] deps = BuildDeps();
            var produced = new ConcurrentBag<string>();
            var failures = new ConcurrentBag<string>();
            int par = MaxParallel > 0 ? MaxParallel : Environment.ProcessorCount;

            Parallel.ForEach(Sources, new ParallelOptions { MaxDegreeOfParallelism = par }, item =>
            {
                string src = item.GetMetadata("FullPath");
                string obj = ObjPath(src, root);
                if (UpToDate(src, obj, deps)) { produced.Add(obj); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(obj));
                var (code, output) = Run(BuildArgs(src, obj));
                if (code == 0) { produced.Add(obj); return; }
                failures.Add(src);
                if (ContinueOnError) Log.LogWarning($"chibil skipped {src} (exit {code})");
                else Log.LogError($"chibil failed ({code}) for {src}:\n{output}");
            });

            Objects = produced.OrderBy(o => o).Select(o => (ITaskItem)new TaskItem(o)).ToArray();
            Log.LogMessage(MessageImportance.High,
                $"ChibilCompile: {produced.Count} ok, {failures.Count} failed" +
                (ContinueOnError && !failures.IsEmpty ? " (skipped)" : "") + $" -> {OutputDir}");
            return ContinueOnError || failures.IsEmpty;
        }

        string ObjPath(string src, string root)
        {
            string rel = root != null && src.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? src.Substring(root.Length).TrimStart('/', '\\')
                : Path.GetFileName(src);
            string flat = rel.Replace('\\', '_').Replace('/', '_');
            if (flat.EndsWith(".c", StringComparison.Ordinal))
                flat = flat.Substring(0, flat.Length - 2) + ".obj";
            return Path.Combine(OutputDir, flat);
        }

        string[] BuildDeps()
        {
            var d = new List<string> { Path.GetFullPath(ChibilDll) };
            if (!string.IsNullOrEmpty(ForceInclude)) d.Add(Path.GetFullPath(ForceInclude));
            foreach (var x in ExtraInputs) d.Add(Path.GetFullPath(x));
            return d.ToArray();
        }

        bool UpToDate(string src, string obj, string[] deps)
        {
            if (!File.Exists(obj)) return false;
            DateTime ot = File.GetLastWriteTimeUtc(obj);
            if (File.GetLastWriteTimeUtc(src) > ot) return false;
            foreach (var d in deps)
                if (File.Exists(d) && File.GetLastWriteTimeUtc(d) > ot) return false;
            return true;
        }

        List<string> BuildArgs(string src, string obj)
        {
            var a = new List<string> { ChibilDll, "-c" };
            foreach (var e in ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) a.Add(e);
            foreach (var i in IncludeDirs) a.Add("-I" + i);
            foreach (var df in Defines) a.Add("-D" + df);
            if (!string.IsNullOrEmpty(ForceInclude)) { a.Add("-include"); a.Add(ForceInclude); }
            a.Add(src);
            a.Add("-o"); a.Add(obj);
            return a;
        }

        static (int, string) Run(List<string> args)
        {
            var psi = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
    }
}
```

- [ ] **Step 3: Implement `ChibilLink`**

Create `tools/chibil-build/ChibilLink.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Chibil.Build
{
    /// <summary>Links .obj files into a managed .dll by invoking `dotnet chibil-link.dll`.
    /// Binds are joined into one `--bind=a=X,b=Y` (the CLI's form). Args via ArgumentList.</summary>
    public sealed class ChibilLink : Task
    {
        [Required] public string ChibilLinkDll { get; set; }
        [Required] public ITaskItem[] Objects { get; set; }
        [Required] public string Output { get; set; }
        public bool Shared { get; set; }
        public bool Debug { get; set; }
        public string[] Binds { get; set; } = Array.Empty<string>();      // "sym=Ns.Type.Method"
        public string[] References { get; set; } = Array.Empty<string>(); // -r asm.dll
        public string ExtraArgs { get; set; } = "";                       // e.g. "--print-imports" / "-l c"

        public override bool Execute()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Output)));
            var a = new List<string> { ChibilLinkDll };
            if (Debug) a.Add("-g");
            if (Shared) a.Add("-shared");
            a.Add("-o"); a.Add(Output);
            foreach (var o in Objects) a.Add(o.GetMetadata("FullPath"));
            if (Binds.Length > 0) a.Add("--bind=" + string.Join(",", Binds));
            foreach (var r in References) { a.Add("-r"); a.Add(r); }
            foreach (var e in ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) a.Add(e);

            var psi = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in a) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (outp.Length > 0) Log.LogMessage(MessageImportance.High, outp.TrimEnd());
            if (err.Length > 0) Log.LogMessage(MessageImportance.High, err.TrimEnd());
            if (p.ExitCode != 0) { Log.LogError($"chibil-link failed ({p.ExitCode}) -> {Output}"); return false; }
            Log.LogMessage(MessageImportance.High, $"ChibilLink: -> {Output}");
            return true;
        }
    }
}
```

- [ ] **Step 4: Build the task assembly**

Run (Windows):
```
cd D:\sandbox\chibil
dotnet build tools/chibil-build/Chibil.Build.Tasks.csproj -c Debug -v q --nologo
dir build\bin\Chibil.Build.Tasks\debug\Chibil.Build.Tasks.dll
```
Expected: `Build succeeded`, the dll exists under `build\bin\Chibil.Build.Tasks\debug\`.

- [ ] **Step 5: Commit**

```
git add tools/chibil-build/Chibil.Build.Tasks.csproj tools/chibil-build/ChibilCompile.cs tools/chibil-build/ChibilLink.cs
git commit -m "build: add Chibil.Build.Tasks (ChibilCompile/ChibilLink MSBuild tasks)

Parallel + incremental ChibilCompile and a ChibilLink task, each shelling out to
dotnet chibil(.dll)/chibil-link(.dll) via ProcessStartInfo.ArgumentList (no shell
quoting). ContinueOnError lets ChibilCompile skip the known musl residual TUs.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Task import props/targets + a smoke fixture

**Files:**
- Create: `tools/chibil-build/build/Chibil.Build.props`
- Create: `tools/chibil-build/build/Chibil.Build.targets`
- Create: `tools/chibil-build/test/smoke.c`
- Create: `tools/chibil-build/test/smoke.proj`

- [ ] **Step 1: Create the path props**

Create `tools/chibil-build/build/Chibil.Build.props`:

```xml
<Project>
  <!-- Tool/output locations under the artifacts root (./build). Override any of
       these from the command line if your config differs. -->
  <PropertyGroup>
    <ChibilToolConfig Condition="'$(ChibilToolConfig)' == ''">debug</ChibilToolConfig>
    <ChibilArtifacts Condition="'$(ChibilArtifacts)' == ''">$(MSBuildThisFileDirectory)..\..\..\build</ChibilArtifacts>
    <ChibilDll Condition="'$(ChibilDll)' == ''">$(ChibilArtifacts)\bin\Chibil\$(ChibilToolConfig)\chibil.dll</ChibilDll>
    <ChibilLinkDll Condition="'$(ChibilLinkDll)' == ''">$(ChibilArtifacts)\bin\ChibilLink\$(ChibilToolConfig)\chibil-link.dll</ChibilLinkDll>
    <ChibilPalDll Condition="'$(ChibilPalDll)' == ''">$(ChibilArtifacts)\bin\Chibil.Pal\release\Chibil.Pal.dll</ChibilPalDll>
    <ChibilBuildTasksDll Condition="'$(ChibilBuildTasksDll)' == ''">$(ChibilArtifacts)\bin\Chibil.Build.Tasks\$(ChibilToolConfig)\Chibil.Build.Tasks.dll</ChibilBuildTasksDll>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create the targets (UsingTask registrations)**

Create `tools/chibil-build/build/Chibil.Build.targets`:

```xml
<Project>
  <UsingTask TaskName="Chibil.Build.ChibilCompile" AssemblyFile="$(ChibilBuildTasksDll)" />
  <UsingTask TaskName="Chibil.Build.ChibilLink" AssemblyFile="$(ChibilBuildTasksDll)" />
</Project>
```

- [ ] **Step 3: Create the smoke source**

Create `tools/chibil-build/test/smoke.c`:

```c
int main(void) { return 7; }
```

- [ ] **Step 4: Create the smoke fixture proj**

Create `tools/chibil-build/test/smoke.proj`:

```xml
<Project DefaultTargets="Run">
  <Import Project="$(MSBuildThisFileDirectory)..\build\Chibil.Build.props" />
  <Import Project="$(MSBuildThisFileDirectory)..\build\Chibil.Build.targets" />
  <PropertyGroup>
    <SmokeOut>$(ChibilArtifacts)\smoke</SmokeOut>
  </PropertyGroup>
  <Target Name="Build">
    <ChibilCompile ChibilDll="$(ChibilDll)"
                   Sources="$(MSBuildThisFileDirectory)smoke.c"
                   OutputDir="$(SmokeOut)"
                   ExtraArgs="--target=coreclr -mlp64">
      <Output TaskParameter="Objects" ItemName="SmokeObj" />
    </ChibilCompile>
    <ChibilLink ChibilLinkDll="$(ChibilLinkDll)" Objects="@(SmokeObj)"
                Output="$(SmokeOut)\smoke.dll" />
  </Target>
  <Target Name="Run" DependsOnTargets="Build">
    <Exec Command="dotnet &quot;$(SmokeOut)\smoke.dll&quot;" IgnoreExitCode="true">
      <Output TaskParameter="ExitCode" PropertyName="SmokeExit" />
    </Exec>
    <Error Condition="'$(SmokeExit)' != '7'" Text="smoke expected exit 7, got $(SmokeExit)" />
    <Message Importance="high" Text="smoke OK: exit 7" />
  </Target>
</Project>
```

- [ ] **Step 5: Verify the fixture fails before the tools exist, then passes**

First prove the tasks are actually exercised: ensure tools are built, then run the fixture.

Run (Windows):
```
cd D:\sandbox\chibil
dotnet build chibil/Chibil.csproj -c Debug -v q --nologo
dotnet build tools/chibil-link/ChibilLink.csproj -c Debug -p:Platform=AnyCPU -v q --nologo
dotnet build tools/chibil-build/Chibil.Build.Tasks.csproj -c Debug -v q --nologo
dotnet build tools/chibil-build/test/smoke.proj -v m --nologo
```
Expected: ends with `smoke OK: exit 7` (chibil compiled smoke.c, chibil-link linked it, it ran returning 7).

Negative check (proves the incremental/early-eval wiring is real): temporarily rename the tasks dll and rebuild the fixture — it must fail to load the task:
```
ren build\bin\Chibil.Build.Tasks\debug\Chibil.Build.Tasks.dll _x.dll
dotnet build tools/chibil-build/test/smoke.proj -v m --nologo
ren build\bin\Chibil.Build.Tasks\debug\_x.dll Chibil.Build.Tasks.dll
```
Expected: the middle build FAILS with a "task could not be loaded / not found" error; the final rename restores it.

- [ ] **Step 6: Commit**

```
git add tools/chibil-build/build tools/chibil-build/test
git commit -m "build: Chibil.Build props/targets + task smoke fixture

Chibil.Build.props resolves \$(ChibilDll)/\$(ChibilLinkDll)/\$(ChibilPalDll)/
\$(ChibilBuildTasksDll) from the artifacts root; Chibil.Build.targets registers the
tasks. smoke.proj compiles+links+runs a trivial TU (exit 7) end-to-end on Windows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `ManagedMusl.proj` (replaces `build-managed-musl.sh`)

**Files:**
- Create: `targets/managed-musl/ManagedMusl.proj`

- [ ] **Step 1: Create the project**

Create `targets/managed-musl/ManagedMusl.proj`. The `<MuslSrc>` Include lists exactly the subsystem dirs from `build-managed-musl.sh`'s `DIRS`. Output objects land flat (underscore-flattened paths) under `build/managed-musl/`.

```xml
<Project DefaultTargets="Build">
  <Import Project="$(MSBuildThisFileDirectory)..\..\tools\chibil-build\build\Chibil.Build.props" />
  <Import Project="$(MSBuildThisFileDirectory)..\..\tools\chibil-build\build\Chibil.Build.targets" />

  <PropertyGroup>
    <MuslRoot>$(MSBuildThisFileDirectory)..\musl-1.2.6</MuslRoot>
    <BuildInfra>$(MSBuildThisFileDirectory)..\build</BuildInfra>
    <MuslObjDir>$(ChibilArtifacts)\managed-musl</MuslObjDir>
  </PropertyGroup>

  <Target Name="Build">
    <ItemGroup>
      <!-- The subsystem set from build-managed-musl.sh DIRS. -->
      <MuslSrc Include="$(MuslRoot)\src\string\**\*.c;$(MuslRoot)\src\ctype\**\*.c;$(MuslRoot)\src\errno\**\*.c;$(MuslRoot)\src\stdlib\**\*.c;$(MuslRoot)\src\stdio\**\*.c;$(MuslRoot)\src\math\**\*.c;$(MuslRoot)\src\malloc\**\*.c;$(MuslRoot)\src\mman\**\*.c;$(MuslRoot)\src\internal\**\*.c;$(MuslRoot)\src\locale\**\*.c;$(MuslRoot)\src\multibyte\**\*.c;$(MuslRoot)\src\prng\**\*.c;$(MuslRoot)\src\exit\**\*.c;$(MuslRoot)\src\env\**\*.c;$(MuslRoot)\src\misc\**\*.c;$(MuslRoot)\src\time\**\*.c;$(MuslRoot)\src\fenv\**\*.c;$(MuslRoot)\src\complex\**\*.c;$(MuslRoot)\src\fcntl\**\*.c;$(MuslRoot)\src\unistd\**\*.c;$(MuslRoot)\src\stat\**\*.c;$(MuslRoot)\src\dirent\**\*.c;$(MuslRoot)\src\select\**\*.c;$(MuslRoot)\src\signal\**\*.c;$(MuslRoot)\src\process\**\*.c;$(MuslRoot)\src\random\**\*.c;$(MuslRoot)\src\linux\**\*.c" />
      <MuslInc Include="$(BuildInfra)\musl-compat;$(MuslRoot)\arch\x86_64;$(MuslRoot)\arch\generic;$(MuslRoot)\src\include;$(MuslRoot)\src\internal;$(MuslRoot)\include;$(MuslRoot)\obj\include" />
    </ItemGroup>

    <ChibilCompile ChibilDll="$(ChibilDll)"
                   Sources="@(MuslSrc)"
                   SourceRoot="$(MuslRoot)"
                   OutputDir="$(MuslObjDir)"
                   IncludeDirs="@(MuslInc)"
                   Defines="_GNU_SOURCE;_XOPEN_SOURCE=700"
                   ForceInclude="$(BuildInfra)\musl-chibil-compat.h"
                   ExtraInputs="$(BuildInfra)\musl-compat\syscall_arch.h"
                   ExtraArgs="--target=coreclr -nostdinc -mlp64"
                   ContinueOnError="true">
      <Output TaskParameter="Objects" ItemName="ManagedMuslObj" />
    </ChibilCompile>

    <Message Importance="high" Text="managed-musl objects: @(ManagedMuslObj->Count()) -> $(MuslObjDir)" />
  </Target>
</Project>
```

- [ ] **Step 2: Build it and check the object count**

Run (Windows; assumes Task 3's verify already built chibil + tasks dll):
```
cd D:\sandbox\chibil
dotnet build targets/managed-musl/ManagedMusl.proj -v m --nologo
(Get-ChildItem build\managed-musl -Filter *.obj).Count
```
Expected: `Build succeeded`; the object count is in the high hundreds / ~1000 (the script produced ~1026; some known TUs fail and are skipped via `ContinueOnError`). Confirm a known object exists:
```
dir build\managed-musl\src_malloc_mallocng_malloc.obj
dir build\managed-musl\src_mman_mmap.obj
```
Expected: both exist.

- [ ] **Step 3: Verify incremental rebuild is a no-op**

Run:
```
dotnet build targets/managed-musl/ManagedMusl.proj -v m --nologo
```
Expected: `Build succeeded` quickly; the `ChibilCompile:` line reports `0 ok` newly compiled is NOT required, but the run should be markedly faster (all objects up-to-date, skipped). (Implementation detail: every source is skipped by the timestamp check.)

- [ ] **Step 4: Commit**

```
git add targets/managed-musl/ManagedMusl.proj
git commit -m "build: ManagedMusl.proj replaces build-managed-musl.sh

Compiles the musl subsystem set (string/stdio/malloc/mman/... per the script's DIRS)
to build/managed-musl/*.obj via ChibilCompile (parallel, incremental, residual TUs
skipped). Runs on Windows - no WSL, no /tmp.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `QuickJsManaged.proj` + `build.proj` orchestrator (replaces `qjs-on-musl-api.sh`)

**Files:**
- Create: `targets/quickjs/QuickJsManaged.proj`
- Create: `build.proj`

- [ ] **Step 1: Create `QuickJsManaged.proj`**

Create `targets/quickjs/QuickJsManaged.proj`. Flags mirror `qjs-on-musl-api.sh` exactly (facade `--export-api=quickjs.h`, the qjs+musl include set, `EMSCRIPTEN`/`_GNU_SOURCE`/`CONFIG_VERSION`, `pal-shim.c`, exclude `*oldmalloc*`, `--bind` to the PAL, `-g -shared`). The linked `qjs.dll` is copied to the consumer's stable HintPath.

```xml
<Project DefaultTargets="Build">
  <Import Project="$(MSBuildThisFileDirectory)..\..\tools\chibil-build\build\Chibil.Build.props" />
  <Import Project="$(MSBuildThisFileDirectory)..\..\tools\chibil-build\build\Chibil.Build.targets" />

  <PropertyGroup>
    <QjsRoot>$(MSBuildThisFileDirectory)..\quickjs-2025-09-13</QjsRoot>
    <MuslRoot>$(MSBuildThisFileDirectory)..\musl-1.2.6</MuslRoot>
    <BuildInfra>$(MSBuildThisFileDirectory)..\build</BuildInfra>
    <MuslObjDir>$(ChibilArtifacts)\managed-musl</MuslObjDir>
    <QjsOut>$(ChibilArtifacts)\bin\qjs</QjsOut>
    <QjsDll>$(QjsOut)\qjs.dll</QjsDll>
    <Consumer>$(MSBuildThisFileDirectory)..\quickjs-api-consumer\qjsconsumer.csproj</Consumer>
  </PropertyGroup>

  <Target Name="Build">
    <ItemGroup>
      <MuslInc Include="$(BuildInfra)\musl-compat;$(MuslRoot)\arch\x86_64;$(MuslRoot)\arch\generic;$(MuslRoot)\src\include;$(MuslRoot)\src\internal;$(MuslRoot)\include;$(MuslRoot)\obj\include" />
      <QjsInc Include="$(BuildInfra)\qjs-compat;$(QjsRoot);@(MuslInc)" />
      <EngineSrc Include="$(QjsRoot)\cutils.c;$(QjsRoot)\dtoa.c;$(QjsRoot)\libregexp.c;$(QjsRoot)\libunicode.c;$(QjsRoot)\quickjs.c" />
    </ItemGroup>

    <!-- Engine TUs: facade (--export-api) + managed-musl headers. -->
    <ChibilCompile ChibilDll="$(ChibilDll)"
                   Sources="@(EngineSrc)"
                   SourceRoot="$(QjsRoot)"
                   OutputDir="$(QjsOut)\obj"
                   IncludeDirs="@(QjsInc)"
                   Defines="EMSCRIPTEN;_GNU_SOURCE;CONFIG_VERSION=&quot;2025-09-13&quot;"
                   ForceInclude="$(BuildInfra)\quickjs-chibil-compat.h"
                   ExtraArgs="--target=coreclr -nostdinc -mlp64 --export-api=quickjs.h">
      <Output TaskParameter="Objects" ItemName="EngineObj" />
    </ChibilCompile>

    <!-- The PAL shim (musl arch shadows). -->
    <ChibilCompile ChibilDll="$(ChibilDll)"
                   Sources="$(BuildInfra)\musl-compat\pal-shim.c"
                   OutputDir="$(QjsOut)\obj"
                   IncludeDirs="@(MuslInc)"
                   ForceInclude="$(BuildInfra)\musl-chibil-compat.h"
                   ExtraArgs="--target=coreclr -nostdinc -mlp64">
      <Output TaskParameter="Objects" ItemName="ShimObj" />
    </ChibilCompile>

    <ItemGroup>
      <!-- Managed-musl objects, minus oldmalloc (mallocng is the proven allocator). -->
      <MuslObj Include="$(MuslObjDir)\*.obj" Exclude="$(MuslObjDir)\*oldmalloc*" />
    </ItemGroup>

    <ChibilLink ChibilLinkDll="$(ChibilLinkDll)"
                Output="$(QjsDll)"
                Shared="true" Debug="true"
                Objects="@(EngineObj);@(MuslObj);@(ShimObj)"
                Binds="__chibil_syscall=Chibil.Pal.Syscall;__chibil_get_tp=Chibil.Pal.GetTp"
                References="$(ChibilPalDll)" />

    <!-- The consumer references ..\quickjs-2025-09-13\qjs.dll (stable HintPath). -->
    <Copy SourceFiles="$(QjsDll)" DestinationFiles="$(QjsRoot)\qjs.dll" />
    <Message Importance="high" Text="qjs.dll -> $(QjsDll) (copied to consumer HintPath)" />
  </Target>

  <!-- Behavioral oracle: build + run the C# consumer, assert exit 0 (JS_Eval==42). -->
  <Target Name="Oracle" DependsOnTargets="Build">
    <MSBuild Projects="$(Consumer)" Properties="Configuration=Debug" Targets="Build" />
    <Exec Command="dotnet &quot;$(ChibilArtifacts)\bin\qjsconsumer\debug\qjsconsumer.dll&quot;" IgnoreExitCode="true">
      <Output TaskParameter="ExitCode" PropertyName="OracleExit" />
    </Exec>
    <Error Condition="'$(OracleExit)' != '0'" Text="oracle FAILED: qjsconsumer exit $(OracleExit) (expected 0 / JS_Eval==42)" />
    <Message Importance="high" Text="ORACLE OK: JS_Eval(&quot;40+2&quot;) == 42" />
  </Target>
</Project>
```

- [ ] **Step 2: Create the root `build.proj` orchestrator**

The orchestrator builds the toolchain (chibil, chibil-link, Chibil.Pal, the tasks dll) FIRST, then invokes the port `.proj` files via nested `<MSBuild>` so the task assembly exists by the time those projects load (their `UsingTask` is resolved at load time).

Create `build.proj`:

```xml
<Project DefaultTargets="QuickJs">
  <!--
    One-command driver for the managed pipeline. Tools are built first (the port
    .proj UsingTask the freshly-built Chibil.Build.Tasks.dll, so it must exist before
    those projects load - a separate MSBuild invocation guarantees that).
      dotnet build build.proj -t:QuickJs     (default; builds qjs.dll + runs oracle)
      dotnet build build.proj -t:ManagedMusl (just the musl object set)
      dotnet build build.proj -t:Tools       (just the toolchain)
  -->
  <PropertyGroup>
    <ToolConfig Condition="'$(ToolConfig)' == ''">Debug</ToolConfig>
  </PropertyGroup>

  <Target Name="Tools">
    <MSBuild Projects="chibil\Chibil.csproj;tools\chibil-link\ChibilLink.csproj;tools\chibil-build\Chibil.Build.Tasks.csproj"
             Properties="Configuration=$(ToolConfig)" />
    <MSBuild Projects="targets\build\Chibil.Pal\Chibil.Pal.csproj" Properties="Configuration=Release" />
  </Target>

  <Target Name="ManagedMusl" DependsOnTargets="Tools">
    <MSBuild Projects="targets\managed-musl\ManagedMusl.proj" />
  </Target>

  <Target Name="QuickJs" DependsOnTargets="ManagedMusl">
    <MSBuild Projects="targets\quickjs\QuickJsManaged.proj" Targets="Oracle" />
  </Target>
</Project>
```

- [ ] **Step 3: Verify the full one-command build + oracle on Windows**

Run (Windows), from a state where `build/` may or may not exist:
```
cd D:\sandbox\chibil
dotnet build build.proj -t:QuickJs -v m --nologo
```
Expected: ends with `ORACLE OK: JS_Eval("40+2") == 42`. (Tools build, managed-musl compiles, qjs.dll links, the consumer runs and exits 0.)

Independently confirm `qjs.dll` reached the consumer HintPath and VS-style run works:
```
dir targets\quickjs-2025-09-13\qjs.dll
dotnet build targets/quickjs-api-consumer/qjsconsumer.slnx -c Debug -v q --nologo
dotnet build\bin\qjsconsumer\debug\qjsconsumer.dll
```
Expected: `qjs.dll` exists; the consumer prints `BEHAVIORAL_ORACLE_OK: quickjs.Api.JS_Eval("40+2") == 42` and exits 0.

- [ ] **Step 4: Commit**

```
git add targets/quickjs/QuickJsManaged.proj build.proj
git commit -m "build: QuickJsManaged.proj + build.proj replace qjs-on-musl-api.sh

QuickJsManaged.proj compiles the QuickJS engine TUs + pal-shim, links qjs.dll
against the managed-musl objects (minus oldmalloc) + Chibil.Pal, copies it to the
consumer HintPath, and its Oracle target runs the consumer asserting JS_Eval==42.
build.proj orchestrates Tools -> ManagedMusl -> QuickJs in one command, on Windows,
no WSL.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `env.sh` so the secondary WSL scripts keep working

**Files:**
- Create: `targets/build/env.sh`
- Modify: every `targets/**/*.sh` and `targets/bash-5.3/*.sh` that defines `CH`/`LINK`/`PAL`/objimports paths EXCEPT the three already converted (`build-managed-musl.sh`, `qjs-on-musl-api.sh`, `qjs-on-musl.sh` — these are superseded; leave them but they may stay as-is since they're not used by the new flow).

The scripts to update (they hardcode `bin/Debug/net10.0` / `bin/Release/net10.0`): `targets/build/musl-layer1.sh`, `musl-layer2-seam.sh`, `musl-layer3-write.sh`, `musl-layer3b-malloc.sh`, `musl-layer3c-printf.sh`, `musl-spike.sh`, `probe-imports.sh`, `quickjs-api.sh`, `quickjs-chibil.sh`, `quickjs-chibil-inc.sh`, `quickjs-test262.sh`, `qjs-gen-repl.sh`, `micropython-chibil.sh`, `micropython-chibil-inc.sh`, and `targets/bash-5.3/anal.sh`, `targets/bash-5.3/run-sj2.sh`.

- [ ] **Step 1: Create `targets/build/env.sh`**

Create `targets/build/env.sh`:

```bash
#!/bin/bash
# Single source of truth for the chibil toolchain DLL locations under the
# centralized artifacts output (build/bin/<Project>/<config>/). Source this AFTER
# setting ROOT (defaults to the WSL repo mount). Phase 2 converts these scripts to
# .proj and deletes this file.
ROOT="${ROOT:-/mnt/d/sandbox/chibil}"
CH="dotnet $ROOT/build/bin/Chibil/debug/chibil.dll"
LINK="dotnet $ROOT/build/bin/ChibilLink/debug/chibil-link.dll"
PAL="$ROOT/build/bin/Chibil.Pal/release/Chibil.Pal.dll"
OBJIMPORTS="dotnet $ROOT/build/bin/objimports/release/objimports.dll"
```

- [ ] **Step 2: Point one representative script at `env.sh` (musl-layer3b-malloc.sh)**

In `targets/build/musl-layer3b-malloc.sh`, replace the two tool-path lines (currently):
```bash
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
```
with:
```bash
source "$ROOT/targets/build/env.sh"
```
And replace the PAL discovery line (currently):
```bash
PAL=$(ls "$ROOT/targets/build/Chibil.Pal/bin/Release/net10.0/Chibil.Pal.dll")
```
with nothing (PAL now comes from `env.sh`) — delete that line. Keep the `dotnet build` of Chibil.Pal line above it (it still builds the PAL; the artifacts layout puts it at the `env.sh` path).

> Note: `musl-layer3b-malloc.sh` also has a line `dotnet build "$ROOT/targets/build/Chibil.Pal/Chibil.Pal.csproj" -c Release ...` — leave it; under the artifacts layout its output lands exactly at `$PAL`.

- [ ] **Step 3: Verify the representative script runs under WSL**

Run (Windows PowerShell, invoking WSL to avoid Git-Bash path mangling). First make sure the tools exist at the artifacts paths (Task 5 already built them):
```
wsl bash /mnt/d/sandbox/chibil/targets/build/musl-layer3b-malloc.sh 2>&1 | Select-Object -Last 6
```
Expected: ends with `exit=42 (expect 42)` — proving `env.sh` resolves the tools at the new locations.

- [ ] **Step 4: Apply the same `env.sh` swap to the remaining scripts**

For EACH of these scripts, replace their `CH=`/`LINK=` definitions with `source "$ROOT/targets/build/env.sh"` (placed right after `ROOT=` is set), and replace any `PAL=$(ls .../bin/Release/net10.0/Chibil.Pal.dll)` with reliance on `$PAL` from env.sh (delete the `ls` line). For `targets/bash-5.3/anal.sh` (which uses `T=.../objimports/bin/Release/net10.0/objimports.dll`) set `T="$OBJIMPORTS"` after sourcing. For `targets/bash-5.3/run-sj2.sh` (which uses `CH=`/`LK=`) source env.sh and add `LK="$LINK"`.

Scripts to edit: `musl-layer1.sh`, `musl-layer2-seam.sh`, `musl-layer3-write.sh`, `musl-layer3c-printf.sh`, `musl-spike.sh`, `probe-imports.sh`, `quickjs-api.sh`, `quickjs-chibil.sh`, `quickjs-chibil-inc.sh`, `quickjs-test262.sh`, `qjs-gen-repl.sh`, `micropython-chibil.sh`, `micropython-chibil-inc.sh`, `targets/bash-5.3/anal.sh`, `targets/bash-5.3/run-sj2.sh`.

Each script sets `ROOT=/mnt/d/sandbox/chibil` already; the `source` line must come after it. Example transformation for `targets/build/musl-layer1.sh`:

Before:
```bash
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
```
After:
```bash
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
```

- [ ] **Step 5: Verify no stale `bin/Debug/net10.0` tool paths remain in the still-shell scripts**

Run:
```
cd /d/sandbox/chibil
grep -rln "bin/Debug/net10.0/chibil\|bin/Release/net10.0/Chibil.Pal\|bin/Release/net10.0/objimports" --include="*.sh" targets/ | grep -v "build-managed-musl.sh\|qjs-on-musl-api.sh\|qjs-on-musl.sh"
```
Expected: NO output (every still-shell script now sources `env.sh`). The three superseded scripts may still match; that is fine.

- [ ] **Step 6: Verify a second script runs under WSL (layer3c printf)**

```
wsl bash /mnt/d/sandbox/chibil/targets/build/musl-layer3c-printf.sh 2>&1 | Select-Object -Last 6
```
Expected: the script's success line (printf layer prints and exits cleanly — same as before the reorg).

- [ ] **Step 7: Commit**

```
git add targets/build/env.sh targets/build/musl-layer1.sh targets/build/musl-layer2-seam.sh targets/build/musl-layer3-write.sh targets/build/musl-layer3b-malloc.sh targets/build/musl-layer3c-printf.sh targets/build/musl-spike.sh targets/build/probe-imports.sh targets/build/quickjs-api.sh targets/build/quickjs-chibil.sh targets/build/quickjs-chibil-inc.sh targets/build/quickjs-test262.sh targets/build/qjs-gen-repl.sh targets/build/micropython-chibil.sh targets/build/micropython-chibil-inc.sh targets/bash-5.3/anal.sh targets/bash-5.3/run-sj2.sh
git commit -m "build: secondary WSL scripts source env.sh for new artifacts paths

Add targets/build/env.sh as the single source of truth for the toolchain DLL
locations under ./build, and point the not-yet-converted layer/quickjs/micropython/
bash scripts at it. Phase 2 converts these to .proj and removes env.sh.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Clean up stale output + update docs

**Files:**
- Modify: `CompileQuickJS.md`, `DebugAndConsume.md`, `CompileBash.md`, `CompileMicroPython.md`, `targets/quickjs-api-consumer/README.md`

- [ ] **Step 1: Delete scattered stale `bin/`/`obj/`/stray `.vs/`**

These are all git-ignored, so deleting them only cleans the working tree. Run (Windows):
```
cd D:\sandbox\chibil
Get-ChildItem -Recurse -Directory -Force -Include bin,obj |
  Where-Object { $_.FullName -notmatch '\\build\\' -and $_.FullName -notmatch 'musl-1\.2\.6|quickjs-2025-09-13|micropython|PureDOOM' } |
  ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item targets\quickjs-api-consumer\.vs -Recurse -Force -ErrorAction SilentlyContinue
```
Expected: no errors. (Do NOT touch `build/`, third-party trees, or the submodule.)

- [ ] **Step 2: Re-verify the build still works after cleanup**

```
dotnet build build.proj -t:QuickJs -v m --nologo
```
Expected: `ORACLE OK: JS_Eval("40+2") == 42` (everything rebuilds cleanly into `./build`).

- [ ] **Step 3: Update `CompileQuickJS.md`**

Find the section documenting the build/run commands and replace the WSL `qjs-on-musl-api.sh` invocation + `chibil/bin/Debug/net10.0` references with the new one-command flow. Add (near the top of the build instructions):

```markdown
## Build & run (Windows or Linux, no WSL required)

```
dotnet build build.proj -t:QuickJs
```

This builds the toolchain, compiles the managed-musl object set, links `qjs.dll`
against it + the managed PAL, and runs the C# consumer asserting
`JS_Eval("40+2") == 42`. Output lands under `./build/`. To rebuild just `qjs.dll`
(then F5 the consumer in VS): `dotnet build targets/quickjs/QuickJsManaged.proj`.
```

Update the two lines that read:
```
- `chibil/bin/Debug/net10.0/chibil.dll` — the C→MSIL compiler ...
- `tools/chibil-link/bin/Debug/net10.0/chibil-link.dll` — the linker ...
```
to:
```
- `build/bin/Chibil/debug/chibil.dll` — the C→MSIL compiler (`--target=coreclr`).
- `build/bin/ChibilLink/debug/chibil-link.dll` — the linker that merges objects.
```

- [ ] **Step 4: Update `CompileBash.md`**

Replace the two reference lines (`CompileBash.md:50-51`):
```
- `chibil/bin/Debug/net10.0/chibil.dll` — the C→MSIL compiler (`--target=coreclr`).
- `tools/chibil-link/bin/Debug/net10.0/chibil-link.dll` — the linker that merges
```
with:
```
- `build/bin/Chibil/debug/chibil.dll` — the C→MSIL compiler (`--target=coreclr`).
- `build/bin/ChibilLink/debug/chibil-link.dll` — the linker that merges
```

- [ ] **Step 5: Update `CompileMicroPython.md` and `DebugAndConsume.md`**

In each file, replace every literal `chibil/bin/Debug/net10.0/chibil.dll` with `build/bin/Chibil/debug/chibil.dll` and every `tools/chibil-link/bin/Debug/net10.0/chibil-link.dll` with `build/bin/ChibilLink/debug/chibil-link.dll`. (Use a search to find them first:)
```
grep -n "bin/Debug/net10.0\|bin/Release/net10.0" CompileMicroPython.md DebugAndConsume.md
```
Edit each match accordingly. If a file has no matches, leave it unchanged.

- [ ] **Step 6: Update the consumer README**

In `targets/quickjs-api-consumer/README.md`, update the "Rebuilding `qjs.dll`" section to prefer the MSBuild flow. Replace the `wsl bash .../qjs-on-musl-api.sh` command block with:

```markdown
Regenerate it after changing QuickJS, the managed musl, the PAL, or the linker —
cross-platform, no WSL:

```
dotnet build targets/quickjs/QuickJsManaged.proj
```

This compiles the engine TUs against the managed-musl headers, links them with the
managed-musl objects + `Chibil.Pal`, and copies the fresh `qjs.dll` to
`..\quickjs-2025-09-13\qjs.dll` (this project's `HintPath`). The managed-musl object
set is built by `targets/managed-musl/ManagedMusl.proj` (run automatically by
`dotnet build build.proj -t:QuickJs`).
```

- [ ] **Step 7: Final verification — VS path + tests**

Confirm the consumer still runs (the VS F5 path) and a test slice is green:
```
cd D:\sandbox\chibil
dotnet build targets/quickjs/QuickJsManaged.proj -v q --nologo
dotnet build targets/quickjs-api-consumer/qjsconsumer.slnx -c Debug -v q --nologo
dotnet build\bin\qjsconsumer\debug\qjsconsumer.dll
dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "FullyQualifiedName~MuslLink|FullyQualifiedName~WeakAlias" --nologo
```
Expected: consumer prints `BEHAVIORAL_ORACLE_OK ... == 42`; the test slice passes (the cl.exe-dependent tests are environmental and out of this filter).

- [ ] **Step 8: Commit**

```
git add CompileQuickJS.md CompileBash.md CompileMicroPython.md DebugAndConsume.md targets/quickjs-api-consumer/README.md
git commit -m "docs: document the ./build artifacts layout and MSBuild pipeline

Point CompileQuickJS/CompileBash/CompileMicroPython/DebugAndConsume and the consumer
README at the new one-command build (dotnet build build.proj -t:QuickJs) and the
build/bin/<Project>/<config> tool paths.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Fold in the pending VS-enablement files

The previous session left `targets/quickjs-api-consumer/qjsconsumer.csproj` (modified), `README.md`, and `qjsconsumer.slnx` uncommitted. Task 7 already re-touched the README; this task commits the remaining consumer wiring together so the tree is clean.

**Files:**
- Commit: `targets/quickjs-api-consumer/qjsconsumer.csproj`, `targets/quickjs-api-consumer/qjsconsumer.slnx`

- [ ] **Step 1: Confirm the consumer still builds/runs (already verified in Task 7) and stage the VS files**

```
cd D:\sandbox\chibil
git status --short targets/quickjs-api-consumer
```
Expected: shows `qjsconsumer.csproj` (modified) and `qjsconsumer.slnx` (untracked) — README was committed in Task 7.

- [ ] **Step 2: Commit**

```
git add targets/quickjs-api-consumer/qjsconsumer.csproj targets/quickjs-api-consumer/qjsconsumer.slnx
git commit -m "consumer: ProjectReference Chibil.Pal + VS solution for Windows F5

qjsconsumer.csproj references Chibil.Pal as a ProjectReference (so it lands in
deps.json - the loader needs it for qjs.dll's <Module> .cctor -> __chibil_pal_init)
and qjs.dll CopyLocal. qjsconsumer.slnx opens both projects in VS. Enables building
and debugging the consumer on Windows.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 3: Commit the design spec + this plan (documentation)**

```
git add docs/superpowers/specs/2026-06-06-build-system-msbuild-migration-design.md docs/superpowers/plans/2026-06-06-build-system-msbuild-migration.md
git commit -m "docs: build-system msbuild migration spec + Phase 1 plan

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Final verification (whole plan)

- [ ] `dotnet build build.proj -t:QuickJs -v m --nologo` → `ORACLE OK: JS_Eval("40+2") == 42` (Windows, no WSL).
- [ ] `git status --short` → clean (only `build/` ignored).
- [ ] `dotnet build targets/quickjs-api-consumer/qjsconsumer.slnx -c Debug` then run `build\bin\qjsconsumer\debug\qjsconsumer.dll` → `BEHAVIORAL_ORACLE_OK ... == 42` (VS F5 path).
- [ ] One secondary WSL script (`musl-layer3b-malloc.sh`) → `exit=42` (env.sh path).
- [ ] No scattered `bin/Debug/net10.0` tool paths remain in still-shell scripts (Task 6 Step 5 grep empty).

## Notes for the executor

- **Bootstrapping order matters.** The port `.proj` files `UsingTask` the freshly-built `Chibil.Build.Tasks.dll`, which is resolved when the project LOADS. You cannot build the tasks dll and use it in the same MSBuild invocation. `build.proj` solves this by building tools in one nested `<MSBuild>` call, then invoking the port projects in later nested calls. When running a port `.proj` directly (not via `build.proj`), ensure the tools (chibil, chibil-link, Chibil.Pal, Chibil.Build.Tasks) are already built into `./build`.
- **`chibil-link` Platform.** Per the session's known gotcha, build `ChibilLink.csproj` with `-p:Platform=AnyCPU` when building it directly so it lands at `build/bin/ChibilLink/debug/`. The artifacts pivot does not include Platform, so AnyCPU and x64 builds share that path — verify during Task 3 that the smoke fixture finds `chibil-link.dll` there (if not, pass `-p:Platform=AnyCPU` in `build.proj`'s `Tools` target).
- **MSBuild package version.** `Microsoft.Build.Utilities.Core` `17.11.4` is a known-good version; if NuGet restore complains on this SDK, bump to the latest `17.*` and keep `ExcludeAssets=runtime`.
- **`dotnet test` rebuilds the tools to x64.** If you run `dotnet test` between toolchain builds, re-run the `Tools` target (or `dotnet build build.proj -t:Tools`) before a port `.proj` so the artifacts paths are repopulated.
- Phase 2 (out of scope): convert the remaining secondary scripts to `.proj` and delete `env.sh`; the Linux-only native-libc oracle stays a thin script.
```
