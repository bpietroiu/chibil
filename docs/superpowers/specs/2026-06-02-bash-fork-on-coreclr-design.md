# fork() for bash-on-CoreCLR — Design

**Goal:** Make GNU bash 5.3 (compiled to IL, linked to glibc) run external
commands, command substitution, subshells, pipelines, background jobs and full
POSIX job control on CoreCLR — where `fork()` cannot be used.

**Status:** Brainstormed + investigation-validated 2026-06-02. Architecture **②**
(re-exec + managed job layer) chosen by the user, with **full POSIX job control**
as the target scope. One prerequisite bug already found + fixed (globalization,
commit #21). Implementation not yet started.

---

## The core problem

`fork()` returns **twice** — once in the parent (child pid) and once in the child
(0) — both into the *same* compiled code. On CoreCLR this is impossible to honor:

1. chibil compiles bash's `fork()` to a real call to glibc `fork()` (a P/Invoke to
   `libc.so.6`). So IL-bash *already* really-forks the .NET process today.
2. **Forking a CoreCLR process corrupts even the *parent*.** Empirically (this
   investigation): every path that actually forks-and-waits crashes
   non-deterministically — `find_pipeline`/`find_job`/`waitchld` fault with
   `AccessViolation`, `StackOverflow`, or a bare core dump, and the signature
   *changes between identical runs*. Killing background threads
   (`DOTNET_gcConcurrent=0`, `gcServer=0`, `TieredCompilation=0`, `ReadyToRun=0`)
   does **not** help — 5/5 fail in every configuration. The CLR's GC/JIT/finalizer
   threads do not survive `fork()`, leaving locks held and the heap inconsistent;
   the surviving thread then faults on the next nontrivial heap/managed activity.
3. The only paths that "work" today are bash's **exec-without-fork optimization**
   (a lone last command — `bash -c '/bin/echo hi'` — which `execve`s directly and
   never forks/waits). That masked the problem; it is not a working wait path.

**Conclusion:** the CLR must **never be forked**. Every `fork()` site must become a
real OS process created without cloning the CLR (`posix_spawn`, or re-exec of a
fresh `bash.dll`). This is mandatory even for external commands, not just for
children that run bash code.

## Why full job control forces "real processes everywhere"

The user chose full POSIX job control (process groups, `$!`, `wait`, `^Z`/`fg`/`bg`,
terminal handoff). In-process emulation cannot provide real pids, pgroups, or
concurrency, so **every forked child must be a real OS process with a real pid**.
The kernel then does job control for us.

## The single chokepoint and the semantic sites

`make_child()` (jobs.c:2305, `JOB_CONTROL=1`) is the one place bash calls `fork()`.
Everything funnels through it: external commands, `$(...)`, `(...)`, `<(...)`,
pipelines, and `&`. But `make_child` returns twice, so we **cannot** intercept it
transparently with re-exec. We intercept the ~5 **semantic callers**, where the
"child body" is a runnable unit:

| Site (file) | Child body | Replacement |
|---|---|---|
| `execute_disk_command` (execute_cmd.c) | redirs + `execve` | `posix_spawn` (file_actions + attrs) |
| `command_substitute` (subst.c) | `parse_and_execute(string)` | re-exec `bash.dll --fork-child`; **already has the text**; capture stdout pipe |
| `execute_in_subshell` (execute_cmd.c) | `execute_command(ast)` | **deparse** AST via `make_command_string()` → re-exec |
| pipeline stages (execute_cmd.c) | per-stage AST | spawn each stage; wire pipes |
| `process_substitute` (subst.c) | string | re-exec + `/dev/fd` |

## Architecture ② — components

1. **Spawn substrate.** One `posix_spawn`-based `spawn_child(argv, file_actions,
   attrs) -> pid`, used by external + re-exec paths. `posix_spawn_file_actions`
   carries dup2/close redirections; `posix_spawnattr` carries process group
   (`POSIX_SPAWN_SETPGROUP`) and signal defaults. Raw `posix_spawn` (not
   `System.Diagnostics.Process`) for exact fd/pgroup/POSIX control via the libc
   P/Invoke chibil already does. `posix_spawn` uses clone(CLONE_VM|CLONE_VFORK)
   internally — the child runs only async-signal-safe glibc code then execs, so the
   CLR is never cloned and the parent is not corrupted.

2. **Fork-site interceptions** (bash source, surgical, fenced under
   `#ifdef CHIBIL_REEXEC` so the upstream diff stays legible).

3. **Child rehydration entry** — new `--fork-child <fd>` mode in `main()`: read
   serialized state from the inherited FD, rebuild, run the command, exit with its
   status. Clean CLR per child.

4. **State transfer** — lean on bash's own re-parseable dumps over an inherited
   pipe FD (not env, to stay binary-safe and avoid leaking into child environs):
   `declare -p` (all vars incl. non-exported), `declare -f` (functions),
   `set`/`shopt` options, positional params, traps. Subtleties: `$$` stays the
   *parent's* logical pid (POSIX); `$BASHPID` = child's real pid; cwd/umask/fds
   inherited naturally (real child).

5. **Managed job/wait layer** — rework `wait_for`/`find_pipeline`/`waitchld` off
   **real pids + `waitpid`** instead of fork-twice/job-array assumptions. This is
   the layer crashing today.

6. **Signals (SIGCHLD)** — staged: synchronous `waitpid` reaping first (fixes
   `find_pipeline` for synchronous cases without async signals); async SIGCHLD later
   for interactive job-status. Pulls the deferred "signals" work into this milestone.

## Decisions (made, not asked)

- **Deparse-to-text + re-exec** for bash-code children (reuse `make_command_string`
  + `declare -p`), not a binary state snapshot — far less code, leans on machinery
  bash already trusts to round-trip.
- **Modify bash source** — unavoidable (interceptions live in
  `execute_cmd.c`/`subst.c`); keep surgical + macro-fenced.

## Staging (each shippable + testable)

- **M-fork-0 (DONE, #21):** invariant-globalization runtimeconfig. Prereq: without
  it, any BCL exception message fatally stack-overflows the single-file image. Now
  multi-command builtin lines run (`echo a; echo b`, `for`, `if [ ]`, `$(( ))`).
- **M-fork-1a (DONE, in targets/ — untracked):** spawn substrate
  (`make_child_posix_spawn` in jobs.c) + external `execute_disk_command` via
  `posix_spawn` for the no-redirect case → `ls`, `cmd; cmd`, exit status (X0/X1),
  `&&`/`||` chains, `for` loops spawning externals. **Thesis validated:** CLR
  never forked → `wait_for`/`find_pipeline`/`find_job` stop crashing (parent
  stays sane). 15/15 regress.sh PASS. Double-free gotcha fixed:
  `strvec_from_word_list(...,alloc=0,...)` aliases words' strings → `free(array)`,
  not `strvec_dispose`. `make_child_posix_spawn` already builds pipe/fds_to_close
  file_actions, but real pipelines fork earlier (see M-fork-3 note) so that path
  is not yet exercised. Redirects (`redirects != 0`) still fall through to
  make_child (crashes — deferred).
- **M-fork-2:** `$(...)` via re-exec (already has text) — unblocks `$(echo nested)`.
- **M-fork-3:** subshells `( )` (deparse) + pipelines (pipe wiring). NOTE
  (investigation): pipeline stages do **not** fork in `execute_disk_command` —
  `execute_simple_command` forks *early* at execute_cmd.c:4550 (`dofork = pipe_in
  || pipe_out || async`) **before** word expansion, then runs expansion + the
  command in the forked child. So pipelines need parent-side word expansion or
  re-exec, not just `posix_spawn` file_actions. `make_child_posix_spawn` already
  builds the pipe/`fds_to_close` file_actions (ready), but the early-fork site is
  the real interception point. This is why pipelines are M-fork-3, not M-fork-1.
- **M-fork-4:** background `&`, `$!`, `wait`, job table, async SIGCHLD, pgroups +
  `tcsetpgrp` (interactive job control).
- **M-fork-5:** process substitution `<()`.

## Risks

- `make_command_string` deparse round-trip fidelity for subshells/pipelines
  (comsub avoids it by having the text).
- State-transfer completeness (which globals a child must inherit).
- Per-child CLR startup latency — later optimizable (in-process fast path for
  pid-less synchronous `$()`/`()`, or AOT).
- SIGCHLD timing / reaping correctness.

## Investigation log (2026-06-02)

- Probed each fork path: externals "work" only via exec-no-fork optimization;
  everything that truly forks+waits crashes.
- Root-caused two *distinct* conflated crashes:
  1. **Globalization/resource recursion** → fatal StackOverflow whenever the BCL
     formats an exception message in a single-file image. Fixed (#21):
     invariant globalization + system resource keys in the emitted runtimeconfig.
     Independently valuable for *all* chibil images.
  2. **CLR-fork corrupts the parent** → non-deterministic AV/SO in the job/reaping
     layer. Not fixable by GC/JIT knobs. Confirms ② (never fork the CLR).
- Tooling note: bash replaces `getenv` with `sh_getenv` (lib/sh/getenv.c) that walks
  bash's variable tables — unreliable inside post-fork/reaping code; instrument with
  raw `write(2,...)`, not getenv-guarded prints. bash also shuffles fd 2 around
  command execution; capture traces via `2>&1 | …`, not `2>file`.
