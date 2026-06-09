# Managed Sandbox Runtime — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up Layer 1 of the managed sandbox runtime (spec: `docs/superpowers/specs/2026-06-08-managed-sandbox-runtime-design.md`), starting by proving the load-bearing technical risk — that a chibil-compiled C program can run as concurrent **ALC-isolated green-processes** in one .NET process while sharing a single PAL kernel.

**Architecture:** One OS process per sandbox hosts bash + curated tools as MSIL. A green-process runs a tool's `Main` in its own `AssemblyLoadContext` (isolating the tool's C statics — which chibil emits as .NET static fields), while a singleton `SandboxPal` kernel (the bind target of `__chibil_syscall`) is shared in the default load context. Per-green-process state (current pid, later: fds/errno/cwd) is resolved through a `[ThreadStatic]` "current process" set before the green-process runs.

**Tech Stack:** .NET 10 / C#; chibil (C→MSIL) + chibil-link (in-house linker) via the in-process test harness (`Chibil.Tests` `TestCompiler` + `LinkPipeline`); `System.Runtime.Loader.AssemblyLoadContext`; xUnit.

---

## Scope: this plan delivers Milestone 1; M2–M6 become their own plans

Layer 1 is large. It is built as a sequence of independently-testable milestones; **this plan fully details Milestone 1** and roadmaps the rest. Each later milestone gets its own spec-grade plan when it is reached (per the writing-plans "break large work into separate plans" guidance). M1 is chosen first because it proves-or-kills decision **D6** (the green-process/ALC model); if D6 fails, the architecture falls back to Approach B (OS-process-per-command) and the rest of the design changes.

### Milestone roadmap

- **M1 — Green-process spike (THIS PLAN).** Prove ALC-isolated C statics + shared PAL kernel; leave behind `Chibil.Sandbox` with `SandboxPal` (kernel + per-process context), the green-process/ALC loader, a minimal process table, and `spawn`/`wait`. *Testable artifact:* concurrent green-processes report isolated static values; one green-process spawns and waits on another.
- **M2 — In-memory pipes + fd table.** `pipe2`/`dup2`/`read`/`write`/`close` over per-green-process fd tables and bounded ring buffers; EOF + `EPIPE`/backpressure. *Testable artifact:* a two-green-process producer→consumer pipe with correct byte stream + flow control.
- **M3 — Virtual FS.** In-memory tree (RO base + RW workdir); `open(at)`/`read`/`write`/`stat`/`getdents64`/`mkdir`/`unlink`/`chdir`/`getcwd`. *Testable artifact:* a tool reads/writes files; control-side inject/extract.
- **M4 — bash on the managed PAL.** Re-link the existing `bash.dll` against `SandboxPal` (instead of native libc); grow the syscall surface to the bash set; route `posix_spawn`→green-process. *Testable artifact:* the bash 57/57 feature matrix on the managed PAL (the headline acceptance gate).
- **M5 — Curated external tool + control protocol.** Port ≥1 coreutils tool to managed-musl; framed-JSON control handler (`exec`/`read_file`/`write_file`/`signal`/`kill`) over a unix-socket/named-pipe. *Testable artifact:* `exec("ls | wc -l")` end to end with streamed output + exit code; vfs round-trip over the socket.
- **M6 — Supervisor + isolation hardening.** Host-side process launch + OS resource caps (Linux cgroup v2/rlimits/seccomp; Windows Job Object) + per-exec deadline + hard-kill; crash-isolation + capability-policy tests. *Testable artifact:* runaway killed within deadline (host unaffected); faulting tool doesn't kill the sandbox; network/path-escape → `EPERM`.

---

## File structure (Milestone 1)

- Create: `targets/sandbox/Chibil.Sandbox/Chibil.Sandbox.csproj` — the sandbox runtime library (kernel + green-process host). One responsibility: the in-process micro-kernel + green-process lifecycle. Tools bind `__chibil_syscall` to types in this assembly; it is loaded **once** in the default context (shared kernel).
- Create: `targets/sandbox/Chibil.Sandbox/SandboxPal.cs` — the syscall kernel: shared state + `[ThreadStatic]` current-process context + `Syscall`/`GetTp` bind targets. Spike syscalls only (report/exit); grows in M2+.
- Create: `targets/sandbox/Chibil.Sandbox/ProcessTable.cs` — `pid → GreenProcess` registry; `Spawn`/`Wait`/allocation of pids.
- Create: `targets/sandbox/Chibil.Sandbox/GreenProcess.cs` — one green-process: owns its `ToolLoadContext`, the loaded tool assembly, runs `Main` with the current-process context set.
- Create: `targets/sandbox/Chibil.Sandbox/ToolLoadContext.cs` — the per-green-process `AssemblyLoadContext` (collectible; isolates the tool, falls back to default for `Chibil.Sandbox` + framework).
- Create: `targets/sandbox/tools/counter.c` — the freestanding test tool (a mutable static + a `report` syscall). No libc needed.
- Create: `targets/sandbox/tools/spawner.c` — a tool that spawns `counter` and waits (for the spawn/wait task).
- Create: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs` — the M1 xUnit tests.
- Create: `tests/Chibil.Tests/Sandbox/SandboxToolBuilder.cs` — test helper: chibil-compile + link a tool `.c` to a `.dll` (in-process), bound to `SandboxPal`.
- Modify: `tests/Chibil.Tests/Chibil.Tests.csproj` — add a `ProjectReference` to `Chibil.Sandbox.csproj` so tests can load/share it.

---

## Task 1: Scaffold `Chibil.Sandbox` and wire the test project

**Files:**
- Create: `targets/sandbox/Chibil.Sandbox/Chibil.Sandbox.csproj`
- Create: `targets/sandbox/Chibil.Sandbox/SandboxPal.cs`
- Modify: `tests/Chibil.Tests/Chibil.Tests.csproj`
- Test: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs`

- [ ] **Step 1: Write the failing test** (`tests/Chibil.Tests/Sandbox/GreenProcessTests.cs`)

```csharp
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

public class GreenProcessTests
{
    [Fact]
    public void Pal_reports_and_reads_back_per_pid()
    {
        SandboxPal.Reset();
        SandboxPal.EnterProcess(7);
        // syscall 0x1000 = report a1 as this pid's value
        long rc = SandboxPal.Syscall(0x1000, 42, 0, 0, 0, 0, 0);
        Assert.Equal(0, rc);
        Assert.Equal(42, SandboxPal.GetReport(7));
        Assert.Equal(1, SandboxPal.ReportCount);
    }
}
```

- [ ] **Step 2: Run it; verify it fails to compile** (`Chibil.Sandbox` / `SandboxPal` do not exist)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~GreenProcessTests" -v q`
Expected: build error — `The type or namespace name 'Sandbox' does not exist`.

- [ ] **Step 3: Create the project** (`targets/sandbox/Chibil.Sandbox/Chibil.Sandbox.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create the minimal kernel** (`targets/sandbox/Chibil.Sandbox/SandboxPal.cs`)

```csharp
using System;
using System.Collections.Concurrent;

namespace Chibil.Sandbox
{
    /// <summary>
    /// The in-process sandbox micro-kernel. Loaded ONCE in the default load context and
    /// shared by every green-process (which runs in its own AssemblyLoadContext). Tools
    /// bind <c>__chibil_syscall</c> to <see cref="Syscall"/>. Shared kernel state lives in
    /// static fields here; per-green-process state is resolved via the [ThreadStatic]
    /// current pid set by <see cref="EnterProcess"/> before a green-process runs.
    /// </summary>
    public static class SandboxPal
    {
        // Shared kernel state (singleton across all green-processes).
        static readonly ConcurrentDictionary<int, long> _reports = new();

        // Per-green-process context: which pid the calling thread is running as.
        [ThreadStatic] static int _currentPid;

        const long SYS_report = 0x1000;
        const long ENOSYS = 38;

        /// <summary>Bind target for __chibil_get_tp (no TLS pointer needed in the spike).</summary>
        public static ulong GetTp() => 0;

        /// <summary>Set the current green-process for the calling thread. Call before running a tool's Main.</summary>
        public static void EnterProcess(int pid) => _currentPid = pid;

        /// <summary>Bind target for __chibil_syscall. Linux x86-64 syscall ABI.</summary>
        public static long Syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            switch (n)
            {
                case SYS_report:
                    _reports[_currentPid] = a1;
                    return 0;
            }
            return -ENOSYS;
        }

        // Test/inspection surface (kernel-side).
        public static long GetReport(int pid) => _reports[pid];
        public static int ReportCount => _reports.Count;
        public static void Reset() => _reports.Clear();
    }
}
```

- [ ] **Step 5: Reference the project from the test project** (`tests/Chibil.Tests/Chibil.Tests.csproj`, in the existing `<ItemGroup>` of `ProjectReference`s)

```xml
    <ProjectReference Include="..\..\targets\sandbox\Chibil.Sandbox\Chibil.Sandbox.csproj" />
```

- [ ] **Step 6: Run the test; verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~GreenProcessTests" -v q`
Expected: PASS (1 passed).

- [ ] **Step 7: Commit**

```bash
git add targets/sandbox/Chibil.Sandbox tests/Chibil.Tests/Sandbox/GreenProcessTests.cs tests/Chibil.Tests/Chibil.Tests.csproj
git commit -m "sandbox(m1): scaffold Chibil.Sandbox + SandboxPal report syscall"
```

---

## Task 2: Build a chibil-compiled tool in-process (the test tool builder)

The green-process loads a real chibil-compiled `.dll`. This task builds one in-process via the existing test harness (`TestCompiler.CompileToObj` + `LinkPipeline.LinkToBytes`), bound to `SandboxPal`.

**Files:**
- Create: `targets/sandbox/tools/counter.c`
- Create: `tests/Chibil.Tests/Sandbox/SandboxToolBuilder.cs`
- Test: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs` (add a test)

- [ ] **Step 1: Write the freestanding tool** (`targets/sandbox/tools/counter.c`)

```c
/* Freestanding test tool: increment a file-scope static N times, then report it via the
 * sandbox kernel. No libc — only the __chibil_syscall bind. The static `counter` is what
 * chibil emits as a .NET static field; the green-process ALC test proves it is isolated. */
extern long __chibil_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6);

static long counter;            /* the static under test */

int main(int argc, char **argv)
{
    long n = 1000000;           /* large enough that shared (racy) increments would diverge */
    for (long i = 0; i < n; i++) counter++;
    __chibil_syscall(0x1000 /* report */, counter, 0, 0, 0, 0, 0);
    return 0;
}
```

- [ ] **Step 2: Write the failing test** (append to `GreenProcessTests.cs`)

```csharp
[Fact]
public void Counter_tool_builds_and_is_a_loadable_assembly()
{
    string dll = SandboxToolBuilder.Build("counter");
    Assert.True(System.IO.File.Exists(dll));
    Assert.True(new System.IO.FileInfo(dll).Length > 0);
}
```

- [ ] **Step 3: Run it; verify it fails** (`SandboxToolBuilder` does not exist)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~Counter_tool_builds" -v q`
Expected: build error — `SandboxToolBuilder` not found.

- [ ] **Step 4: Implement the builder** (`tests/Chibil.Tests/Sandbox/SandboxToolBuilder.cs`)

```csharp
using System.Collections.Generic;
using System.IO;
using Chibil;            // TestCompiler is in this test assembly's namespace
using ChibilLink;

namespace Chibil.Tests.Sandbox;

/// <summary>Compiles a freestanding tool .c under targets/sandbox/tools into a runnable
/// CoreCLR .dll, with __chibil_syscall bound to Chibil.Sandbox.SandboxPal.Syscall, using
/// the in-process compiler + linker (same path the CoreClr unit tests use).</summary>
public static class SandboxToolBuilder
{
    public static string Build(string toolName)
    {
        string repo = TestPaths.RepoRoot;   // existing helper; resolves the chibil checkout root
        string src = Path.Combine(repo, "targets", "sandbox", "tools", toolName + ".c");
        string sandboxDll = typeof(global::Chibil.Sandbox.SandboxPal).Assembly.Location;

        byte[] obj = TestCompiler.CompileFileToObj(src, TargetProfile.CoreClr,
            defines: new string[0], includes: new[] { Path.GetDirectoryName(src) });

        var bindMap = new Dictionary<string, string>
        {
            ["__chibil_syscall"] = "Chibil.Sandbox.SandboxPal.Syscall",
            ["__chibil_get_tp"]  = "Chibil.Sandbox.SandboxPal.GetTp",
        };

        byte[] dllBytes = LinkPipeline.LinkToBytes(
            new[] { ObjectFile.Load(obj, toolName + ".obj") },
            new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: false,
            assemblyName: toolName, entrySymbol: "main",
            libSearchPaths: null, importsOut: null,
            bindMap: bindMap,
            references: new[] { sandboxDll });

        string outDir = Path.Combine(Path.GetTempPath(), "chibil-sandbox-tools");
        Directory.CreateDirectory(outDir);
        string dll = Path.Combine(outDir, toolName + ".dll");
        File.WriteAllBytes(dll, dllBytes);
        return dll;
    }
}
```

> Note for the implementer: confirm the exact `TestCompiler.CompileFileToObj` and `TestPaths.RepoRoot` member names against `tests/Chibil.Tests/CoreClr/TestCompiler.cs` and adjust the call to match the existing helpers (the SQLite tests in `tests/Chibil.Tests/CoreClr/Sqlite*.cs` use the same compile+`LinkPipeline.LinkToBytes` path — copy their exact usage). The `LinkToBytes` parameter names above match its signature in `tools/chibil-link/PeWriter.cs`.

- [ ] **Step 5: Run the test; verify it passes**

Run (from a vcvars64 shell is NOT required — CoreClr target, no MSVC): `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~Counter_tool_builds" -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add targets/sandbox/tools/counter.c tests/Chibil.Tests/Sandbox/SandboxToolBuilder.cs tests/Chibil.Tests/Sandbox/GreenProcessTests.cs
git commit -m "sandbox(m1): in-process builder for chibil-compiled tools bound to SandboxPal"
```

---

## Task 3: The green-process loader (ALC isolation) + the D6 proof

**Files:**
- Create: `targets/sandbox/Chibil.Sandbox/ToolLoadContext.cs`
- Create: `targets/sandbox/Chibil.Sandbox/GreenProcess.cs`
- Test: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs`

- [ ] **Step 1: Write the failing test — the make-or-break D6 assertion** (append to `GreenProcessTests.cs`)

```csharp
using System.Threading.Tasks;   // ensure present at top of file

[Fact]
public void Concurrent_green_processes_isolate_statics_and_share_kernel()
{
    SandboxPal.Reset();
    string dll = SandboxToolBuilder.Build("counter");

    var p1 = new GreenProcess(pid: 1, toolDllPath: dll);
    var p2 = new GreenProcess(pid: 2, toolDllPath: dll);

    // Run both at once. If the tool's static `counter` were SHARED (D6 false), the two
    // 1,000,000-increment loops would race on one variable and neither would report
    // exactly 1,000,000. With ALC isolation, each instance has its own `counter`.
    Task t1 = Task.Run(() => p1.Run(new[] { "counter" }));
    Task t2 = Task.Run(() => p2.Run(new[] { "counter" }));
    Task.WaitAll(t1, t2);

    Assert.Equal(1_000_000, SandboxPal.GetReport(1));   // isolation
    Assert.Equal(1_000_000, SandboxPal.GetReport(2));   // isolation
    Assert.Equal(2, SandboxPal.ReportCount);            // both hit the SAME kernel dict
}
```

- [ ] **Step 2: Run it; verify it fails** (`GreenProcess`/`ToolLoadContext` do not exist)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~Concurrent_green_processes" -v q`
Expected: build error — `GreenProcess` not found.

- [ ] **Step 3: Implement the ALC** (`targets/sandbox/Chibil.Sandbox/ToolLoadContext.cs`)

```csharp
using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Chibil.Sandbox
{
    /// <summary>
    /// Per-green-process load context. The tool assembly is loaded into THIS context
    /// (so its .NET static fields — the C globals — are private to this green-process).
    /// Everything the tool references but does not define (Chibil.Sandbox, the framework)
    /// is left to fall back to the Default context, so the SandboxPal kernel is a single
    /// shared instance across all green-processes.
    /// </summary>
    internal sealed class ToolLoadContext : AssemblyLoadContext
    {
        public ToolLoadContext(string name) : base(name, isCollectible: true) { }

        // Return null for everything: the runtime then resolves via the Default context,
        // which shares Chibil.Sandbox + framework. The tool itself is loaded explicitly
        // via LoadFromAssemblyPath (below), which places it in THIS context regardless.
        protected override Assembly Load(AssemblyName assemblyName) => null;
    }
}
```

- [ ] **Step 4: Implement the green-process** (`targets/sandbox/Chibil.Sandbox/GreenProcess.cs`)

```csharp
using System;
using System.Reflection;

namespace Chibil.Sandbox
{
    /// <summary>One green-process: a tool's Main running in an isolated ToolLoadContext,
    /// with the SandboxPal current-process context set on the executing thread so the
    /// shared static Syscall resolves to this pid.</summary>
    public sealed class GreenProcess
    {
        readonly ToolLoadContext _alc;
        readonly MethodInfo _main;

        public int Pid { get; }

        public GreenProcess(int pid, string toolDllPath)
        {
            Pid = pid;
            _alc = new ToolLoadContext($"green-{pid}");
            Assembly asm = _alc.LoadFromAssemblyPath(toolDllPath);
            _main = asm.EntryPoint
                ?? throw new InvalidOperationException($"tool '{toolDllPath}' has no entry point");
        }

        /// <summary>Run the tool to completion on the CALLING thread (so EnterProcess binds
        /// this thread to this pid). Returns the process exit code.</summary>
        public int Run(string[] args)
        {
            SandboxPal.EnterProcess(Pid);
            object ret = _main.Invoke(null, new object[] { args });
            return ret is int code ? code : 0;
        }
    }
}
```

- [ ] **Step 5: Run the test; verify it passes — D6 PROVEN**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~Concurrent_green_processes" -v q`
Expected: PASS. (If it FAILS with reported values ≠ 1,000,000, D6 is false — STOP and escalate: the architecture must fall back to Approach B. This is the decision gate.)

- [ ] **Step 6: Commit**

```bash
git add targets/sandbox/Chibil.Sandbox/ToolLoadContext.cs targets/sandbox/Chibil.Sandbox/GreenProcess.cs tests/Chibil.Tests/Sandbox/GreenProcessTests.cs
git commit -m "sandbox(m1): ALC green-processes — isolated C statics + shared kernel (D6 proven)"
```

---

## Task 4: Process table + `spawn`/`wait` via the kernel

A green-process must be able to spawn another and wait for it — the primitive bash's `posix_spawn`/`waitpid` will route to. The tool calls `spawn(name)`/`wait(pid)` syscalls; the kernel owns the process table.

**Files:**
- Create: `targets/sandbox/Chibil.Sandbox/ProcessTable.cs`
- Modify: `targets/sandbox/Chibil.Sandbox/SandboxPal.cs` (add `SYS_spawn`/`SYS_wait`, a tool resolver)
- Create: `targets/sandbox/tools/spawner.c`
- Test: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs`

- [ ] **Step 1: Write the spawner tool** (`targets/sandbox/tools/spawner.c`)

```c
/* Spawns the `counter` tool (id 1 in the kernel's tool registry), waits for it, then
 * reports the spawned child's pid so the test can confirm spawn+wait worked. */
extern long __chibil_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6);

int main(int argc, char **argv)
{
    long child = __chibil_syscall(0x1001 /* spawn */, 1 /* tool id: counter */, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1002 /* wait  */, child, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1000 /* report*/, child, 0, 0, 0, 0, 0);
    return 0;
}
```

- [ ] **Step 2: Write the failing test** (append to `GreenProcessTests.cs`)

```csharp
[Fact]
public void Green_process_can_spawn_and_wait_a_child()
{
    SandboxPal.Reset();
    var table = new ProcessTable();
    // Register tool id 1 = counter (built in-process).
    table.RegisterTool(id: 1, toolDllPath: SandboxToolBuilder.Build("counter"));
    SandboxPal.AttachProcessTable(table);

    string spawnerDll = SandboxToolBuilder.Build("spawner");
    var spawner = table.CreateRoot(spawnerDll);   // gets pid, registers in table
    int rc = spawner.Run(new[] { "spawner" });

    Assert.Equal(0, rc);
    // counter (the child) reported 1,000,000 under its own pid; spawner reported the child's pid.
    int childPid = (int)SandboxPal.GetReport(spawner.Pid);
    Assert.Equal(1_000_000, SandboxPal.GetReport(childPid));
    Assert.NotEqual(spawner.Pid, childPid);
}
```

- [ ] **Step 3: Run it; verify it fails** (`ProcessTable`, `AttachProcessTable`, `CreateRoot`, `RegisterTool` missing)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~spawn_and_wait" -v q`
Expected: build error.

- [ ] **Step 4: Implement the process table** (`targets/sandbox/Chibil.Sandbox/ProcessTable.cs`)

```csharp
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Chibil.Sandbox
{
    /// <summary>pid → green-process registry + tool registry. Owns pid allocation and the
    /// spawn/wait primitives the kernel exposes as syscalls.</summary>
    public sealed class ProcessTable
    {
        readonly ConcurrentDictionary<int, GreenProcess> _procs = new();
        readonly ConcurrentDictionary<int, string> _tools = new();   // tool id → dll path
        readonly ConcurrentDictionary<int, Task<int>> _running = new();
        int _nextPid;

        public void RegisterTool(int id, string toolDllPath) => _tools[id] = toolDllPath;

        public GreenProcess CreateRoot(string toolDllPath)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var gp = new GreenProcess(pid, toolDllPath);
            _procs[pid] = gp;
            return gp;
        }

        /// <summary>Spawn tool `toolId` as a new green-process running on a background thread.
        /// Returns the child's pid.</summary>
        public int Spawn(int toolId)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var gp = new GreenProcess(pid, _tools[toolId]);
            _procs[pid] = gp;
            _running[pid] = Task.Run(() => gp.Run(new[] { "child" }));
            return pid;
        }

        /// <summary>Block until `pid` exits; returns its exit code.</summary>
        public int Wait(int pid) => _running.TryGetValue(pid, out var t) ? t.GetAwaiter().GetResult() : 0;
    }
}
```

- [ ] **Step 5: Wire spawn/wait into the kernel** (`targets/sandbox/Chibil.Sandbox/SandboxPal.cs` — add the constants, field, and cases)

```csharp
        // add near the other syscall constants:
        const long SYS_spawn = 0x1001;
        const long SYS_wait  = 0x1002;

        // add to the shared kernel state:
        static ProcessTable _table;
        public static void AttachProcessTable(ProcessTable t) => _table = t;

        // add cases inside Syscall's switch:
                case SYS_spawn:
                    return _table.Spawn((int)a1);
                case SYS_wait:
                    return _table.Wait((int)a1);
```

- [ ] **Step 6: Run the test; verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~spawn_and_wait" -v q`
Expected: PASS.

- [ ] **Step 7: Run the whole M1 suite + commit**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~GreenProcessTests" -v q`
Expected: PASS (all M1 tests).

```bash
git add targets/sandbox/Chibil.Sandbox/ProcessTable.cs targets/sandbox/Chibil.Sandbox/SandboxPal.cs targets/sandbox/tools/spawner.c tests/Chibil.Tests/Sandbox/GreenProcessTests.cs
git commit -m "sandbox(m1): process table + spawn/wait syscalls (one green-process spawns/waits another)"
```

---

## Task 5: ALC unload / lifetime check (the residual D6 risk)

The risk ledger flags ALC spawn cost + unload leaks. Prove a green-process's ALC actually unloads (no static-state or memory leak across many spawns), so M2+ can pool with confidence.

**Files:**
- Modify: `targets/sandbox/Chibil.Sandbox/GreenProcess.cs` (add `Unload()`)
- Test: `tests/Chibil.Tests/Sandbox/GreenProcessTests.cs`

- [ ] **Step 1: Write the failing test** (append to `GreenProcessTests.cs`)

```csharp
[Fact]
public void Green_process_alc_unloads_after_run()
{
    SandboxPal.Reset();
    string dll = SandboxToolBuilder.Build("counter");

    System.WeakReference weak = RunAndGetAlcWeakRef(dll);   // separate method so locals can be GC'd

    for (int i = 0; i < 10 && weak.IsAlive; i++)
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
    }
    Assert.False(weak.IsAlive, "the tool's AssemblyLoadContext did not unload");
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static System.WeakReference RunAndGetAlcWeakRef(string dll)
{
    var gp = new GreenProcess(pid: 99, toolDllPath: dll);
    gp.Run(new[] { "counter" });
    System.WeakReference w = gp.Unload();   // returns a weak ref to the ALC, then releases it
    return w;
}
```

- [ ] **Step 2: Run it; verify it fails** (`Unload` missing)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~alc_unloads" -v q`
Expected: build error — no `Unload`.

- [ ] **Step 3: Implement `Unload`** (`targets/sandbox/Chibil.Sandbox/GreenProcess.cs` — add the method; `_main` must be dropped so nothing roots the ALC)

```csharp
        MethodInfo _main;   // change from readonly to allow null-out on unload

        /// <summary>Initiate collectible unload of this green-process's load context and
        /// return a weak reference to it (for tests / pool reclamation). After this call the
        /// green-process is dead.</summary>
        public System.WeakReference Unload()
        {
            var weak = new System.WeakReference(_alc);
            _main = null;
            _alc.Unload();
            return weak;
        }
```

- [ ] **Step 4: Run the test; verify it passes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release --filter "FullyQualifiedName~alc_unloads" -v q`
Expected: PASS. (If the ALC stays alive, capture what roots it — most often a lingering `MethodInfo`/`Assembly` reference or a static in `Chibil.Sandbox` holding the tool type — and fix the root before proceeding; pooling in M2+ depends on clean unload.)

- [ ] **Step 5: Run the full test suite + commit**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj -c Release -v q`
Expected: PASS (M1 suite green; no regression elsewhere).

```bash
git add targets/sandbox/Chibil.Sandbox/GreenProcess.cs tests/Chibil.Tests/Sandbox/GreenProcessTests.cs
git commit -m "sandbox(m1): collectible ALC unload verified (clean green-process teardown)"
```

---

## Milestone 1 — Definition of done

- `Chibil.Sandbox` exists: `SandboxPal` (kernel + `[ThreadStatic]` per-process context), `GreenProcess` + `ToolLoadContext`, `ProcessTable`.
- A chibil-compiled tool runs as a green-process bound to `SandboxPal`.
- **D6 proven:** concurrent green-processes have isolated C statics and a shared kernel.
- One green-process spawns and waits on another via kernel syscalls.
- Green-process ALCs unload cleanly (pooling is safe to add in M2).
- All M1 tests pass; no regression in the existing suite.

The next plan (M2 — pipes + fd table) builds on `SandboxPal`'s per-process context and `ProcessTable`, replacing the spike `report` syscall with real `read`/`write`/`pipe2`/`dup2` over per-green-process fd tables.

---

## Self-review

**1. Spec coverage (M1 scope).** The spec's D5 (green processes) and D6 (ALC static isolation) are the M1 target — Tasks 3–4 implement and prove them; the kernel/per-process-context split (spec §4, §5.1) is Tasks 1 & 4; green-process host (spec §5.2) is Task 3; ALC unload risk (spec §11.1) is Task 5. Pipes/vfs/control/supervisor/bash-gate (spec §5.1 sub-units, §5.3, §5.4, §9 gate) are explicitly **deferred to M2–M6** per the milestone roadmap — not gaps, but later plans.

**2. Placeholder scan.** No "TBD/handle edge cases/similar to". The one forward-looking note (Task 2, Step 4) tells the implementer to confirm exact helper member names against named existing files (`TestCompiler.cs`, the SQLite tests, `PeWriter.cs`) — a verification instruction, not a missing implementation; the code shown is complete.

**3. Type/name consistency.** `SandboxPal` (`Syscall`/`GetTp`/`EnterProcess`/`GetReport`/`ReportCount`/`Reset`/`AttachProcessTable`), `GreenProcess(pid,toolDllPath)`/`Run`/`Pid`/`Unload`, `ToolLoadContext(name)`, `ProcessTable`/`RegisterTool`/`CreateRoot`/`Spawn`/`Wait`, syscall numbers `0x1000` report / `0x1001` spawn / `0x1002` wait — all used consistently across tasks. `SandboxToolBuilder.Build` returns a dll path used identically in Tasks 2–5.
