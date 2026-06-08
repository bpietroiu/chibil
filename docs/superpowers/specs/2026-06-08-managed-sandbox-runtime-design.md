# Managed Sandbox Runtime (Layer 1) — Design

**Date:** 2026-06-08
**Status:** Approved (brainstorm), pending implementation plan
**Scope:** The foundational sub-project of a larger "agent sandbox-as-a-service" vision. This spec covers **Layer 1 only** — a single isolated managed-bash sandbox. Higher layers (lifecycle/state, control plane, agent harness) are deferred to their own specs.

---

## 1. Goal

Run **one isolated, process-per-sandbox managed-bash environment** that executes a real shell pipeline (builtins + at least one ported external tool) over an **expanded capability PAL** (process-spawn + FD/pipe table + virtual FS), driven by a minimal exec/file control interface, with OS-level resource limits and clean kill — and with zero native libc dependencies, so the same Linux userland runs on Windows, Linux, and (eventually) WASM.

This is the engine the rest of the product sits on. It is also the highest-risk piece: today's managed PAL implements ~11 compute/stdio syscalls and **zero** process/FD syscalls.

## 2. Context & vision

The product is **agent sandbox-as-a-service**: isolated execution environments for AI coding agents, exposed over an API (`POST /sandboxes`, exec, fs, and eventually snapshot/fork/replay), competing with container/microVM sandboxes (E2B, Daytona, Firecracker).

**The moat — the PAL *is* the sandbox.** In the managed-musl model, every syscall a program makes is already funneled through managed C# (`__chibil_syscall`). So isolation stops being "spin up a kernel/container" and becomes "instantiate a managed process with a capability-scoped PAL." That buys: sub-50ms spin-up with no image/VM; per-syscall mediation (the boundary is a C# allowlist, not seccomp); deterministic + replayable runs (log the syscall stream); cheap snapshot/fork (state is managed memory + a virtual FS); and "runs anywhere .NET runs."

**The load-bearing constraint — MSIL, not ELF.** A managed-musl sandbox runs only what has been compiled to MSIL via chibil. There is no `apt install <arbitrary binary>`. The product is therefore a **curated managed toolset** (bash + a growing, vetted set of tools), not a Docker replacement. This is simultaneously the moat (total control) and the wall (a porting treadmill).

**Decomposition (built bottom-up, each its own spec):**

1. **Sandbox Runtime** ← *this spec.* One isolated managed-bash process: the capability PAL, the curated toolset, a drive interface, resource limits.
2. **Lifecycle & State.** Snapshot / fork / replay, virtual-FS persistence, image/template system.
3. **Control Plane (the "SaaS server").** Multi-tenant API, placement across hosts, auth, quotas, billing, observability.
4. **Agent Harness Integration.** The SDK / MCP / tools API agents use to drive sandboxes.

## 3. Key decisions (decision log)

| # | Decision | Choice | Why |
|---|----------|--------|-----|
| D1 | Product center of gravity | Sandbox/isolation tech (sell the environment + API) | The reusable, defensible core; harness/control-plane sit on top |
| D2 | Workload model | Curated managed toolset (all MSIL) | Honest given MSIL-not-ELF; max control/density/portability |
| D3 | Isolation model | Process-per-sandbox | OS boundary + PAL allowlist + rlimits; safe for hostile code; ~10–50ms spin-up |
| D4 | Layer 1 MVP | Pipeline (builtins + ≥1 external) + virtual FS, socket-driven, capped, killable | Proves the engine turns over end to end |
| D5 | Intra-sandbox exec model | In-process **green processes** (spawn = managed task, pipe = in-mem buffer, wait = join) | ms-spawn, reuses bash's no-fork model, portable, snapshot-friendly; isolation boundary is the sandbox |
| D6 | C-statics isolation | **ALC-per-green-process** | .NET statics are per-OS-process; an ALC gives each tool instance private C globals for free, PAL kept as a shared singleton |

## 4. Architecture

A sandbox is **one OS process**. Inside it, the PAL is a **micro-kernel** shared by all green-processes; each green-process runs a tool with **ALC-isolated C statics**.

```
┌────────────────────── sandbox = 1 OS process ───────────────────────┐
│  CONTROL HANDLER  ── framed-JSON over unix-socket / named-pipe       │
│     exec · read_file/write_file/stat/list · signal · kill           │
│                                                                     │
│  GREEN PROCESSES (tasks, ALC-isolated statics)                      │
│     bash ──spawn──▶ ls ──pipe──▶ wc      each: own fd table, errno   │
│        │                                                            │
│  ┌─────▼──────────── PAL MICRO-KERNEL (singleton, shared) ────────┐ │
│  │  syscall dispatch  (__chibil_syscall: 11 → bash set)           │ │
│  │  vfs (in-mem tree; RO base layer + RW workdir; serializable)   │ │
│  │  fd table (per green-proc) · pipes (in-mem ring buffers)       │ │
│  │  process table (pid→green-proc, spawn/wait/kill) · signals     │ │
│  │  capability policy  (allowlist: net denied, fs scoped, …)      │ │
│  └───────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
        ▲ launched & capped by ▼
   SUPERVISOR (host-side, outside the sandbox process):
   spawns `dotnet sandbox.dll`, applies OS limits
   (Linux cgroup v2 + rlimits + seccomp / Windows Job Object),
   owns the control socket, hard-kills on runaway or disconnect.
```

**The concurrent-globals problem and its resolution (D6).** chibil compiles C file-scope globals and `static`s — `errno`, `optind`, bash's entire interpreter state — to **.NET static fields, which are per-OS-process (shared across threads)**. Naively running `bash` and `ls` (or two `cat`s in a pipe, or a bash subshell) as tasks in one process would have them stomp each other's globals. **Resolution:** each green-process runs its tool's `Main` inside its own `AssemblyLoadContext`; .NET statics are per-ALC, so every tool instance gets private C globals automatically, while the PAL kernel is a singleton in the default load context (the vfs/fd-table/pipes/process-table *are* the shared kernel). Warm ALCs are pooled per tool to keep spawn in the ms range. Bash subshells reuse the port's existing state-transfer (serialize funcs/vars/positional, re-inject) rather than shared memory.

## 5. Components

### 5.1 PAL micro-kernel
**Responsibility:** the expanded managed syscall surface. Owns the virtual FS, the per-green-process FD table, in-memory pipes, the process table, signal emulation, and the capability policy. Routes `__chibil_syscall(num, args…)` to the right subsystem.
**Interface (in):** the Linux syscall ABI (`__chibil_syscall`), as the ported musl/tools already call it.
**Interface (out):** managed host facilities — memory, the host clock (virtualizable), the control streams for fd 0/1/2.
**Depends on:** nothing outside the sandbox process except the host runtime.
**Sub-units (each independently testable):**
- **vfs** — in-memory tree of dirs/files/symlinks; a read-only base layer (toolset + base rootfs) overlaid by a read-write workdir; backs open/openat/read/write/lseek/stat-family/getdents64/mkdir/unlink/rename/access/readlink/chdir/getcwd. Designed as a serializable structure (for Layer-2 snapshot), but persistence is out of scope here.
- **fd table** — per green-process map fd→open handle (vfs file, pipe end, or fd 0/1/2 bound to the control stream); dup/dup2/dup3/fcntl/close operate here.
- **pipes** — bounded in-memory byte ring buffers; `pipe2` returns (rfd, wfd); blocking read/write with backpressure; EOF when all write-ends close; `EPIPE`/`SIGPIPE` when writing to a closed read-end.
- **process table** — pid → green-process record {entrypoint, argv, env, cwd, fd table, ALC lease, Task, state, exit status}; backs spawn / wait4 / kill / get*pid; ids are emulated.
- **signals** — per-green-process pending mask + handlers; delivered at syscall boundaries; SIGCHLD on child exit, SIGINT (from control), SIGPIPE, SIGTERM/SIGKILL → Task cancellation.
- **capability policy** — every syscall checked against an allowlist before dispatch (network denied, FS confined to the vfs, clock/random mediated); violations return `EPERM`/`EACCES` and are audited.

### 5.2 Green-process / toolset host
**Responsibility:** the registry mapping `path → managed entrypoint` (`int Main(argc, argv, envp, fds)`), and the green-process lifecycle. `posix_spawn("/bin/ls")` resolves the path, leases a warm ALC, loads the tool assembly into it, and starts `Main` as a Task with the supplied fd table; records the pid in the process table.
**Interface:** `posix_spawn`/`clone`/`execve` (as the PAL routes them) in; a Task + pid out.
**Depends on:** PAL process table + fd table; the ALC pool.
**Constraint:** tools need not be reentrant — ALC isolation makes each green-process's statics private — but they must be *instance-clean* (no reliance on cross-invocation static state surviving). This holds for bash + coreutils.

### 5.3 Control handler
**Responsibility:** the drive protocol over a unix domain socket (Linux) / named pipe (Windows). Lets the host run commands and manipulate files without screen-scraping a tty.
**Protocol (MVP):** framed JSON request/response with a streaming channel for stdio.
- `exec{cmd, argv, env, cwd, stdin?}` → starts a top-level green-process (`bash -c …` or a tool), streams stdout/stderr, returns exit code.
- `write_file{path, bytes}`, `read_file{path}`, `stat{path}`, `list{path}` → direct vfs access (inject/extract files).
- `signal{pid, sig}`, `kill_sandbox`.
**Depends on:** PAL (vfs, process table), green-process host.

### 5.4 Supervisor (host-side, outside the sandbox process)
**Responsibility:** the *actual* security/resource enforcer. Launches `dotnet sandbox.dll`; applies OS resource limits (Linux: cgroup v2 cpu/mem + rlimits + seccomp as defense-in-depth; Windows: Job Object cpu/mem/process caps); owns the control socket endpoint; enforces a per-`exec` wall-clock deadline; hard-kills the sandbox process on runaway or control disconnect (no orphans).
**Why it exists:** the managed PAL mediates *I/O*, but a runaway or memory-unsafe tool is contained only by the OS process boundary + caps. The supervisor is what makes "handles hostile code" true at the sandbox granularity.

## 6. Data flow — `exec("ls /src | wc -l")`

```
control → exec{cmd,cwd,env,stdin}
  └ control handler starts  bash -c "ls /src | wc -l"  as top-level green-proc (fds 0/1/2 = control stream)
     └ bash: pipe2() → (rfd,wfd) in-mem buffer
        ├ posix_spawn ls : lease warm ALC, ls.Main as Task, fd1=wfd, proc-table pid
        ├ posix_spawn wc : fd0=rfd
        └ waitpid both
   ls: getdents(vfs /src) → write names to wfd → exit → SIGCHLD + status
   wc: read rfd to EOF (all writers closed) → "N\n" → fd1 (control stream) → exit
   bash: pipeline status = last → return; control streams stdout + exit code
```

Bounded ring buffers provide real backpressure (a writer blocks when the buffer is full), giving genuine pipeline flow control rather than unbounded buffering.

## 7. Error handling & isolation

- **Tool faults** (managed exception / chibil trap / caught unsafe access) → caught at the green-process Task boundary → mapped to a nonzero exit + synthetic `SIGSEGV` in the process table → bash's `waitpid` sees `WIFSIGNALED`. The sandbox process survives; only that green-process dies.
- **Runaway** (infinite loop / unbounded alloc) — a managed thread cannot be cleanly preempted, so backstops in depth: PAL allocation accounting (fail allocations past a cap → `ENOMEM`), the supervisor's cgroup/Job-Object cpu+mem caps, and a per-`exec` wall-clock deadline → `kill_sandbox`. Runaway kills the *sandbox*, not the host. Correct blast radius.
- **Policy violation** (network, path-escape) → `EPERM`/`EACCES`, audited. The vfs has no host-FS passthrough by construction, so path traversal cannot escape the sandbox.
- **Control disconnect** → the sandbox self-terminates; the supervisor reaps. No orphans.
- **Accepted caveat:** a memory-unsafe tool could corrupt the shared PAL heap before it faults. This is accepted *within* a sandbox (one user's session, vetted toolset); the OS-process boundary contains it. Tool-vs-tool isolation *inside* a sandbox is an explicit non-goal for Layer 1.

## 8. Capability / security model

Two boundaries, layered:
1. **The PAL allowlist** mediates every syscall — the fine-grained, auditable policy surface (deny network, confine FS to the vfs, mediate clock/random). This is the unique control the managed model gives.
2. **The OS process boundary + resource caps** (supervisor) contain blast radius and runaway resource use — the coarse, hard backstop the managed runtime cannot itself guarantee.

Neither alone is sufficient; together they make process-per-sandbox defensible for untrusted code. Managed code is explicitly *not* treated as a security boundary on its own.

## 9. Testing strategy

**Acceptance gate (headline):** re-run the existing **bash 57/57 feature matrix against the managed PAL** (not native libc). "Real bash, all features, zero native deps." Reuses the QuickJS/MicroPython managed-musl harness pattern (compile → link → run → assert).

**Behavioral tests:**
- pipeline of builtins (`echo hi | wc -l` → 1)
- builtin + ported external over the vfs (`ls | grep`)
- vfs round-trip (control `write_file` → bash reads it; bash writes → control `read_file`)
- **concurrent-globals isolation** — the same tool twice in one pipeline / concurrent green-processes → no cross-talk (the ALC test)
- **crash isolation** — a tool that faults → sandbox survives, bash sees signaled status
- **resource cap** — a runaway (alloc/CPU) → sandbox killed within the deadline, host unaffected
- **capability policy** — network / path-escape attempt → `EPERM`
- **control protocol** — exec streaming, exit codes, signal delivery
- **determinism probe** — record the PAL syscall stream for a run; replay → identical output (designed-for here; exercised in Layer 2)

## 10. MVP syscall surface

PAL grows from its current 11 to the minimum for the target scripts:
- **FS:** open/openat, close, read, write, lseek, stat/fstat/lstat/newfstatat, getdents64, mkdir, unlink, rename, access, readlink, dup/dup2/dup3, fcntl, ioctl (TCGETS → `ENOTTY`), chdir, getcwd
- **Process:** clone·fork → **spawn**, execve → **entrypoint**, posix_spawn, wait4, exit/exit_group, getpid/getppid/getpgid·setpgid (emulated ids), kill → signal-post
- **Pipes/poll:** pipe2, poll/ppoll, select (over vfs + pipe fds)
- **Signals:** rt_sigaction / rt_sigprocmask / rt_sigreturn (delivered at syscall boundaries) · SIGCHLD/INT/PIPE/TERM
- **Misc:** brk · mmap(anon) · munmap (already present), clock_gettime (virtualizable), getrandom (present), uname
- **Explicitly deferred:** termios/tty (interactive mode), networking (policy-denied), threads (tools are single green-thread), exotic ioctls

## 11. Risk ledger (carries into the plan)

1. **Concurrent C globals** — resolved by ALC-per-green-process (D6); residual risk is ALC spawn cost / memory and unload leaks → mitigate with pooling + bounded lifetimes; **must be proven under concurrency**. (Largest risk; resolved-by-design but unproven until tested.)
2. **Syscall tail** — bash and each new tool drag in more syscalls; grow the surface iteratively against a fixed target-script set rather than chasing completeness.
3. **No clean green-process preemption** — runaway relies on sandbox-level OS caps (documented, accepted).
4. **Unsafe tool ↔ shared PAL heap** — accepted within a sandbox; OS boundary contains it. Tool-vs-tool isolation is a future (Approach-C) concern.
5. **tty/termios deferred** — non-interactive only; interactive bash/readline is a later layer.

## 12. Deferred / out of scope for Layer 1

- **Snapshot / fork / replay** — *designed-for* (serializable vfs + state-transfer + syscall-stream recording) but built in Layer 2.
- **Persistence / images / templates** — Layer 2.
- **Multi-tenancy, placement, the HTTP API, auth, quotas, billing** — Layer 3 (control plane).
- **Agent SDK / MCP / tools contract** — Layer 4.
- **Interactive shell, job control, tty** — a later runtime increment.
- **Tool-vs-tool isolation within a sandbox** — Approach-C future, not a Layer-1 goal.

## 13. Success criteria

Layer 1 is done when, on the managed PAL with zero native libc dependencies, on both Linux and Windows:
- bash passes the 57/57 feature matrix;
- a control-plane client can `exec` a builtin+external pipeline and get correct streamed output + exit code;
- it can inject and extract files via the vfs;
- concurrent green-processes do not corrupt each other's globals;
- a faulting tool does not take down the sandbox, and a runaway is killed within its deadline without affecting the host.
