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

### M4.5 — Build bash against SandboxPal  ✅ DONE
Re-pointed the existing bash 5.3 managed build from native libc to managed musl + `SandboxPal`. `bash --norc --noprofile -c 'echo hi'` runs as a green-process → `"hi\n"`, rc 0, **zero native libc**; var-expansion + `$((6*7))` also pass. *Tests: `BashTests` (2 facts); full Sandbox suite 24/24.*
- **Build vehicle:** `targets/sandbox/SandboxBash.proj` links the prebuilt bash image (`targets/bash-5.3/_il/*.obj`, 215 TUs from the WSL build) + managed-musl objs + `pal-shim.c` + new `targets/sandbox/bash-shim.c`, bound to `SandboxPal`, refs `Chibil.Sandbox.dll`+`Chibil.Pal.dll`. No native `-l`.
- **Link gap-fill (`bash-shim.c`):** `chibil-link … -lc --print-imports` over the image enumerated ALL 38 otherwise-unresolved symbols at once (33 func + 5 data): termcap, passwd/group DB, netdb, dlopen, regex, fnmatch, temp-files, termios, conf — none on the `-c` happy path → stubbed "feature absent". Data: bare `errno`/`current_command_first_line_comment`/`_rl_executing_macro`/`in6addr_*`.
- **Runtime gap-fill (4 fixes):** (1) `SandboxPal.GetTp` returned 0 → delegate to `Chibil.Pal.GetTp` (real per-thread TCB) so musl's thread-pointer-relative errno doesn't null-fault on the first failing syscall. (2) Synthesized entry calls `main` directly, bypassing musl `__libc_start_main` → added `__chibil_rt_init` (in bash-shim.c; pal-shim can't `#include pthread_impl.h` — `__wake`/`__wait` clash) that sets `__pthread_self()->{locale=&__libc.global_locale, tid=1}` before main (locale: `__ctype_get_mb_cur_max` deref; tid: musl `exit()` a_crash on tid==0). `GreenProcess` invokes it before main. (3) The synth `Main` marshals argv/envp from the shared **host** `Environment` — wrong in-process; `GreenProcess` now invokes the C `main` directly with its OWN argv/envp marshalled to native char** (new `Environ` prop). (4) Added `SYS_getcwd` (returns Cwd).

### M4.6 — The 57/57 gate  ◀ IN PROGRESS
Run the bash feature matrix against `SandboxPal`. **Measured baseline (BashTests batch harness): 28/31 pure-bash features ALREADY pass in-process** — variables, arithmetic, all conditionals/loops/functions/arrays/param-expansion/brace-expansion. The 3 gaps all need a child execution context: command substitution `$()`, nested comsub, subshell `( )`.

**Foundation DONE (managed side, tested 25/25):** the green-process analog of bash's "re-exec self":
- `SYS_pipe` (22) — musl x86-64 `pipe()` uses it (only `pipe2`/293 existed → comsub's `pipe()` ENOSYS'd).
- `GreenProcess.ToolDllPath` + `ProcessTable.SpawnImage(dll,args,parent,fdMap)` (runs `Run(args)`, inherits Cwd+fds) + `SYS_spawn_self` (0x1003: argv char** + fdMap pairs) → spawns the SAME bash image with `-c <body>` and stdout→pipe. Test `Bash_on_bash_spawn_writes_to_pipe` proves it end-to-end.

**comsub DONE (functionally) — re-pointed onto green-processes:**
1. ✅ `chibil_spawn_comsub` (jobs.c) under `#if CHIBIL_SANDBOX` calls `__chibil_syscall(SYS_spawn_self=0x1003, argv[bash,--norc,--noprofile,-c,BODY], fdmap{1,fildes[1]}, 1)`. Build: SandboxBash.proj recompiles ONLY jobs.c on Windows with `-DCHIBIL_SANDBOX`, swaps it for `_il/jobs.obj`. NO WSL rebuild needed.
2. ✅ Reap path: `SYS_wait4`(61) over `ProcessTable.WaitPid` (pid>0 / pid==-1 / WNOHANG; status=code<<8; parent+reaped tracking). bash's existing `wait_for(pid)`→waitpid reaps cleanly.
3. ✅ **VERIFIED:** `echo $(echo x)`, nested, `a$(echo b)c`, comsub+redirect, **recursion `fact 5`→120**, var+func transfer — all correct output. (Also added per-process musl TCB: green-processes share pooled threads, a per-thread TCB bleeds TLS.)

**Bug #1+#2 (ONE root cause) — ROOT-CAUSED + FIXED: collectible-ALC unload during concurrent green-process execution.** PROVEN via windbg/cdb crash dump: the faulting green thread raised `System.ExecutionEngineException` (CLR's own state corrupted) while the **Background GC thread was active**; `isCollectible:false` makes ALL cases (shallow + deep) clean → it's the collectible-ALC UNLOAD (driven by background GC) racing another green-process's chibil-JITted execution. **FIX** (`GreenProcess`): active-count + retire-when-idle — keep every green-process REFERENCED in a static `_retired` list until `_executing==0` (a true safe point), then unload them all + GC. ALCs stay collectible (memory reclaimed), never unload mid-execution. Plus `ProcessTable.RunOnDedicatedThread` (dedicated threads, not ThreadPool — avoids pool starvation under deep nested blocking waits) + idempotent `Unload`. Verified: `fact 5`→120, `fact 8`, linear-6 ×30; diverse 30-comsub×6; `cases3` 7/7; suite **28/28 stable** (`Bash_command_substitution_sequence`, `Bash_deeply_nested_comsub`). **comsub (incl deep recursion) is now reliable.**

**Bug #3 — FIXED (chibil CodeGen setjmp-lowering fix, reviewed):** `exit N`/`set -e` crashed because whole-body setjmp resumption re-ran `parse_and_execute`'s pre-loop `begin_unwind_frame` → double-free. Fix: `EmitSetjmpWrappedBody` hoists the branch-free pre-setjmp prefix OUT of the try (conservative `PrefixBranchFree` guard → never invalid IL; falls back to whole-body otherwise). Recompiled `evalstring.c`. Verified: exit/errexit work, comsub preserved. **GREEN-GATE PASSED** — full chibil unit suite 369/0/3 (vcvars64) + QuickJS oracle (JS_Eval==42) + MicroPython oracle (print(40+2)=42, sorted, SystemExit) + sandbox 29/29 (`Bash_exit_builtin_and_errexit`). The compiler change (`chibil/CodeGen.cs`) is fully validated and ready to land.

**Still TODO after the bugs:** subshell `( )` (intercept `execute_in_subshell`, deparse via `make_command_string`, spawn-self `-c`); builtin/external pipelines; externals need **M5 coreutils**. Full 57/57 awaits M5; M4.6's reachable target = the bash-CODE matrix.
*Acceptance: the bash-code feature matrix on the managed PAL, zero native deps; full 57/57 once M5 lands externals.*

---

## Risks specific to M4
1. **musl startup syscall tail** (M4.2) — unknown until run; the iterative gap-fill is the bulk of the work. Mitigated by `SandboxPal` ⊇ `Chibil.Pal` (musl already runs on `Chibil.Pal` for QuickJS/MicroPython).
2. **`struct stat`/`dirent` layouts** (M4.3) — must match x86-64 Linux byte-for-byte or musl misreads them.
3. **tty/termios** — bash checks `isatty`; non-interactive mode needs only `ioctl`→`ENOTTY` (done) + stubs. Interactive/job-control is out of scope (Layer-1 = non-interactive).
4. **bash's own fork-emulation** — the existing port already solves no-fork via posix_spawn+re-exec+state-transfer; M4.4 re-points it at green-processes rather than reinventing it.

## Definition of done (M4)
`exec("…bash script…")` over the control interface runs real bash on `SandboxPal` with zero native libc, passing 57/57, on Linux and Windows.
