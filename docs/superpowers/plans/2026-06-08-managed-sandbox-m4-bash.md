# Managed Sandbox Runtime — Milestone 4: bash on the PAL

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox tracking. This is the heavy milestone — a real-software bring-up, decomposed into sub-steps M4.1–M4.6, each independently testable.

**Goal:** Run real GNU bash (already chibil-compiled to MSIL) on `SandboxPal` instead of native libc, passing the bash 57/57 feature matrix — the Layer-1 acceptance gate. Spec: `2026-06-08-managed-sandbox-runtime-design.md` §9.

**Why hard:** M1–M3 ran freestanding toy tools that call `__chibil_syscall` directly. bash needs the *full managed musl libc* (malloc/stdio/string/…) and a large syscall tail (stat/getdents/signals/fcntl), plus `posix_spawn`→green-process. This is where the toy kernel meets a real program.

---

## Sub-milestones

### M4.1 — Host-capable kernel  ✅ DONE (commit 8490a91)
`SandboxPal` handles `writev` (fd-table), `ioctl`→`ENOTTY`, `exit`/`exit_group`→green-process termination (via `GreenProcessExit`), and delegates the pure memory/random syscalls (`mmap`/`mprotect`/`munmap`/`madvise`/`getrandom`) to the proven `Chibil.Pal`. `BufferSinkHandle` captures stdout. *Test: `HostCapTests` — writev→sink, mmap-delegates, exit_group code.* Because `SandboxPal` now ⊇ `Chibil.Pal`'s surface, a program that runs on `Chibil.Pal` should run on `SandboxPal` modulo fd-table stdio and the fs/process overrides.

### M4.2 — A musl-linked tool runs on SandboxPal  ◀ NEXT (the M4 analog of D6)
Prove the *full managed musl* hosts on `SandboxPal`.
- **Build vehicle:** a `.proj` (mirror `targets/quickjs/QuickJsManaged.proj`), NOT in-process — musl needs its 1029-object set (`build/managed-musl/*.obj`) + `pal-shim.c` + `ForceInclude targets/build/musl-chibil-compat.h` + the musl include set, linked with `--bind=__chibil_syscall=Chibil.Sandbox.SandboxPal.Syscall,__chibil_get_tp=Chibil.Sandbox.SandboxPal.GetTp -r Chibil.Sandbox.dll`. Produces e.g. `hello.dll`.
- **Run vehicle:** an xUnit test loads `hello.dll` as a `GreenProcess` (seed fd 1 = `BufferSinkHandle`), runs it, asserts captured stdout.
- **First tool:** `int main(){ printf("hello, sandbox\n"); return 0; }` — exercises malloc + stdio + the `_start`/`__libc_start_main` path.
- **Expect gaps:** musl stdio/startup will hit syscalls `SandboxPal` doesn't yet handle and that `Chibil.Pal` lacks (likely `fstat`/`newfstatat` on fd 1 for buffering mode, `set_tid_address`, `rt_sigprocmask`, `brk`). Add each to `SandboxPal` (stat-family needs the x86-64 `struct stat` layout: 144 bytes; fill `st_mode`/`st_size`). Iterate until `hello` prints. *This is the make-or-break step; if musl won't host, M4 is blocked.*

### M4.3 — Filesystem metadata syscalls
`stat`/`fstat`/`lstat`/`newfstatat` (fill `struct stat`), `getdents64` (fill `struct linux_dirent64`), `access`, `readlink`, `chdir`/`getcwd`, `unlink(at)`, `rename(at)`, `mkdir(at)` syscall (vfs `Mkdir` exists). *Test: a musl tool that `opendir`/`readdir`s the vfs and `stat`s files (the things `ls` and bash globbing need).*

### M4.4 — posix_spawn → green-process + signals
Route bash's no-fork spawn (`posix_spawn` + the port's re-exec/state-transfer) onto `ProcessTable.Spawn`, wiring the child's fds (stdin/stdout/pipe ends) into its `FdTable` before run. Minimal signal emulation: `rt_sigaction`/`rt_sigprocmask` (record, deliver at syscall boundaries), `SIGCHLD` on child exit, `SIGPIPE`, `SIGINT`. *Test: a tool spawns a child with a piped stdout and waits — the green-process analog of a shell pipeline stage.*

### M4.5 — Build bash against SandboxPal
Re-point the existing bash 5.3 managed build (`CompileBash.md`) from native libc to the managed musl + `SandboxPal` bind. Resolve the link (bash references many libc symbols; ensure the musl set + shim cover them). Get `bash -c 'echo hi'` to run as a green-process with stdout captured. *Test: `exec("echo hi")` → "hi\n".*

### M4.6 — The 57/57 gate
Run the bash feature matrix (variables, arithmetic, conditionals, loops, functions, arrays, command substitution + state transfer, externals, redirections, heredocs, pipelines) against `SandboxPal`. Port the first external (`true`/`echo` builtins cover much; a real external needs M5's coreutils port). Fill remaining syscall/termios-stub gaps. *Acceptance: 57/57 on the managed PAL, zero native deps — the headline result.*

---

## Risks specific to M4
1. **musl startup syscall tail** (M4.2) — unknown until run; the iterative gap-fill is the bulk of the work. Mitigated by `SandboxPal` ⊇ `Chibil.Pal` (musl already runs on `Chibil.Pal` for QuickJS/MicroPython).
2. **`struct stat`/`dirent` layouts** (M4.3) — must match x86-64 Linux byte-for-byte or musl misreads them.
3. **tty/termios** — bash checks `isatty`; non-interactive mode needs only `ioctl`→`ENOTTY` (done) + stubs. Interactive/job-control is out of scope (Layer-1 = non-interactive).
4. **bash's own fork-emulation** — the existing port already solves no-fork via posix_spawn+re-exec+state-transfer; M4.4 re-points it at green-processes rather than reinventing it.

## Definition of done (M4)
`exec("…bash script…")` over the control interface runs real bash on `SandboxPal` with zero native libc, passing 57/57, on Linux and Windows.
