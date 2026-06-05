# Managed libc/PAL design — running chibil-compiled C with no native libc

> Sequel to `2026-06-05-posix-libc-abstraction-design.md`. That doc chose the
> architecture (syscall-seam PAL beneath a managed musl) and validated it with the
> musl-compile spike (~98–99% of musl source compiles to MSIL; blockers were the
> three arch asm seams + two now-fixed chibil bugs). **This doc designs the PAL
> itself** — the managed implementation of the seam — scoped *toward bash*.

**Goal.** Make a chibil-compiled C program (qjs, then bash) run as a single
managed program with **zero native libc** (`chibil-link --print-imports` → 0):
musl compiled to MSIL on top, a managed PAL implementing ~100 Linux syscalls
below, joined by one seam.

**Scope.** Designed for bash, not just `JS_Eval` — the hard POSIX surface (fork,
pipes, signals, dup) is in, tiered honestly between the managed and native
backends. Acceptance ladder: `JS_Eval` → `qjs script.js` → the 57 bash feature
tests.

---

## 1. Architecture & the memory model

Everything above the seam is RID-neutral managed musl; everything below is the PAL.

```
  chibil-compiled program (qjs / bash)        ┐
  musl  (MSIL; linked as objs into the module) │  "internal libc": musl fns are
     libc fns → __chibil_syscall(n, a1..a6)     │  in-module calls; only the seam
     __errno_location, __get_tp, a_cas, …        ┘  externs bind out
  ───────────────────────────  the seam  ───────────────────────────
  Chibil.Pal  (managed C#, AnyCPU)
     long Syscall(long n, a1..a6)  ──switch──►  handlers
     fd/OFD table   process table   signal state   per-thread TCB   (errno lives in TCB)
     ▼ backend seam (only for tiered syscalls)
     pal.managed (BCL, v1)    pal.linux (native, later)    pal.windows (later)
```

**The foundational decision — native memory.** A C pointer passed to a syscall
(`write(fd, buf, count)`) must be dereferenceable by the managed PAL. This works
cleanly because **all C memory is real unmanaged memory**: musl's `malloc`/`mmap`/
`brk` resolve (through the PAL) to `NativeMemory`, and globals are FieldRVA/native.
Every C pointer is a genuine machine address, so the PAL reads/writes buffers with
`new Span<byte>((void*)addr, len)` — no marshalling, exactly as the native
`libc.so` does today. **The PAL is the only `unsafe` code**; musl and the program
stay pointer-correct. (Rejected alternative: a managed heap with translated
addresses — far slower, and needless given chibil's native-pointer model.)

---

## 2. The core PAL (file I/O, fd table, memory, time)

**Dispatch & the errno trick.** One entry `long Syscall(long n, a1..a6)`, a
`switch (n)` over Linux x86-64 numbers (jump-table fast), split across
`partial class` files by category (`FileSyscalls.cs`, `MemSyscalls.cs`, …). The
PAL returns the **raw Linux ABI** — `≥0` success, `-errno` on error — and musl's
*existing* `__syscall_ret` + per-thread `errno` machinery do the rest. **The PAL
never touches `errno` directly**; `__errno_location` just reads the errno slot in
the per-thread TCB.

**Binding the seam (detailed in §4).** `__chibil_syscall` / `__chibil_get_tp` are
unresolved externs in musl's objs; chibil-link binds them to managed methods in a
referenced `Chibil.Pal.dll` — not P/Invoke. Result: `--print-imports` → 0.

**The fd table — two levels, mirroring the kernel** (required for bash redirections):

```
fd (int)  ─►  OpenFileDescription { offset, flags, Backend }  ─►  Backend
  0,1,2,3…       shared by dup/dup2 (shared offset, per POSIX)     SafeFileHandle | Socket | PipeEnd | DirEnum | StdStream
```

- `dup`/`dup2`/`fcntl(F_DUPFD)` copy fd→OFD (sharing offset/flags); `close` drops
  a ref to the OFD. Lowest-free-fd allocation.
- fds 0/1/2 preseed to stdin/stdout/stderr (Console, or redirected streams).

**File backend = `SafeFileHandle` + `System.IO.RandomAccess`.** `RandomAccess.
Read/Write(handle, buffer, offset)` is offset-addressed — a near-perfect match for
`pread`/`pwrite` and for `lseek`+`read` (the OFD owns the offset). Syscalls:
`openat, close, read, write, readv, writev, pread64, pwrite64, lseek, fstat/statx,
getdents64, fcntl, ioctl(TCGETS→isatty), faccessat, unlinkat, renameat, mkdirat,
getcwd, chdir, readlinkat`. Buffers accessed via `Span<byte>` over real pointers.

**Memory.** `mmap` anon → `NativeMemory.AlignedAlloc` (track region→size for
`munmap`); file-backed → `MemoryMappedFile`. `brk` → return `-ENOMEM` so musl's
malloc uses its mmap path (a real brk arena is a later optimization). `madvise`
→ no-op.

**Time / random / exit.** `clock_gettime` (REALTIME→`DateTimeOffset.UtcNow`,
MONOTONIC→a process `Stopwatch`), `nanosleep`→`Thread.Sleep`, `getrandom`→
`RandomNumberGenerator.Fill`, `exit_group`→`Environment.Exit`; sane-value stubs for
`getpid/getuid/getgid/uname/sysinfo`.

**The thread pointer.** `__chibil_get_tp()` lazily allocates one native **TCB** per
managed thread (`[ThreadStatic]`), laid out to satisfy musl's `pthread_impl.h`
(self-ptr, `errno_val`, `tid`, `locale`, cancel state, dtv). Fiddly but bounded;
single TCB for qjs.

---

## 3. Process & signals (the tiered layer)

Managed and native fidelity genuinely diverge here, so each capability names its tier.

**Process table.** `pid → ChildProcess { Process handle, exit-status slot }`; the
PAL allocates pids (virtual, mapped to OS pids where a real child exists).
`wait4`/`waitpid` → `Process.WaitForExit` + reaped-status bookkeeping.

**The fork split — the decision that defines this layer.** `fork` (musl →
`SYS_clone`) divides:

| case | what it is | managed PAL | linux-native PAL |
|---|---|---|---|
| **fork + immediate `execve`** | spawn an external (the shell's 90% case) | **real** — `posix_spawn`/`Process.Start` a fresh host, wire fds | real |
| **fork *without* `execve`** | subshell `(…)`, `$(…)`, background `&` (shell code in a child) | **re-exec + state snapshot** (reuse the bash port's proven funcs/vars/positional transfer) | **real fork** (clone passthrough) |

v1 implements fork+exec (universal) first; fork-without-exec is real on
`pal.linux` and state-transfer on `pal.managed`. Exotic fork degrades. This keeps
"toward bash" honest.

**`execve`.** A managed process image can't be replaced in place. Two backends: a
chibil-compiled **managed** target → in-process `AssemblyLoadContext` load+run (no
new process); a **native** target → spawn it; the host becomes a wait-and-exit
proxy (exit code preserved; pid approximated).

**Signals — cooperative delivery.** Per-process `{ rt_sigaction handler table,
rt_sigprocmask blocked mask, pending queue }`. musl already checks EINTR/
cancellation **at syscall boundaries**, so the PAL delivers queued signals there
(cooperative, not preemptive) — covering what shells need:
- `SIGINT` ← `Console.CancelKeyPress`; `SIGCHLD` ← a tracked child exits (drives
  `wait`); `SIGPIPE` ← broken-pipe write; `kill`/`tkill` → enqueue to target.
- Preemptive delivery to an arbitrary instruction (e.g. `SIGALRM` mid-loop) is the
  one thing the managed tier can't do faithfully — **native-only**, documented.

**Pipes.** `pipe2` → an in-process byte channel (paired `Stream`/`System.IO.
Pipelines`) as two fds; across a spawned child, an inherited OS anonymous pipe.

---

## 4. Link rewiring, prerequisites, backends, testing

**Internal-libc rewiring (load-bearing chibil-link change).** Today an unresolved
extern → a P/Invoke stub. New: **bind an extern to a managed method** in a
referenced assembly. Mirror the existing `--export-api` facade in reverse — resolve
the target in `Chibil.Pal.dll`'s metadata, add `AssemblyRef` + `MemberRef`,
override the call token to a direct managed `call`; signatures must line up
(`long __chibil_syscall(long×7)` ↔ `static long Pal.Syscall(long×7)`). Tiny config:

```
chibil-link program.obj musl/*.obj \
   --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp \
   -r Chibil.Pal.dll
```

Program objs + musl objs link into one module (as the QuickJS build already does)
+ a reference to `Chibil.Pal`. Atomics stay as the compat header's plain-C
(single-threaded-correct) for v1, so the only bindings are `__chibil_syscall` and
`__chibil_get_tp`.

**Prerequisite the spike deferred — real `weak_alias`.** The spike *dropped* it;
real musl needs `weak_alias(old,new)` to define `new` as an alias of `old` (how
`strcasecmp`, `_init`, hundreds exist). chibil-link needs **symbol aliasing** —
emit `new` as a thin forwarder MethodDef to `old`. Small, but a hard dependency and
the **first work item**.

**v1 ships managed-only.** `Chibil.Pal` is one AnyCPU managed assembly (BCL
backend). Handlers call a thin backend seam only for the *tiered* syscalls
(fork-without-exec, preemptive signals); native `pal.linux`/`pal.windows` plug in
there later (the fidelity tier, per the abstraction doc's D6).

**Testing — driven like the spike.**
1. **Unit**: each handler tested in C# against crafted native buffers
   (`Pal.Syscall(SYS_write, …)` over a `NativeMemory` buffer).
2. **Coverage readout**: `Pal.Syscall` returns `-ENOSYS` + logs unhandled numbers —
   the running target *tells you* the next syscall to implement (this phase's
   `--print-imports`).
3. **Acceptance ladder**: (a) `quickjs.Api.JS_Eval("40+2")==42` on managed musl +
   PAL with `--print-imports == 0`; (b) `qjs script.js` (file I/O); (c) the 57 bash
   feature tests.

**Work-item order.** real `weak_alias` → bind-extern-to-managed in chibil-link →
`Chibil.Pal` core (dispatch + errno-return + TCB + fd/OFD table + file/mem/time) →
QuickJS acceptance → process/signal layer → bash acceptance.

---

## Component summary (each unit: one purpose, clear interface)

| unit | purpose | depends on |
|---|---|---|
| compat layer (`musl-compat/*.h`, `musl-chibil-compat.h`) | shadow the 3 arch asm seams; route to externs | (built in the spike) |
| `weak_alias` aliasing | define alias symbols at link | chibil-link |
| bind-extern-to-managed | resolve seam externs → `Chibil.Pal` methods | chibil-link, `--export-api` machinery |
| `Chibil.Pal` dispatch | `Syscall(n,…)` switch; `-errno` return | native-memory model |
| TCB / `__chibil_get_tp` | per-thread musl control block + errno slot | musl pthread_impl layout |
| fd/OFD table | POSIX fd semantics, dup sharing | `SafeFileHandle`, `RandomAccess` |
| mem/time/random handlers | mmap/brk/clock/getrandom | `NativeMemory`, BCL |
| process layer | fork/execve/wait, process table | backend seam |
| signal layer | handler table, cooperative delivery | syscall-boundary hooks |
| backend seam | per-RID escape for tiered syscalls | (native backends later) |
