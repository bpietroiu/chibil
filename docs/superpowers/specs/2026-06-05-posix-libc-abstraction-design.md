# POSIX/libc abstraction for multi-RID targeting — design notes

> Status: **design exploration** (not a locked spec). Captures the architecture
> chat of 2026-06-05. The linchpin assumption (chibil can compile musl source to
> MSIL) is being validated by a separate **musl-compile spike** before committing.

## Goal

Let a single C codebase compiled by chibil to MSIL run on multiple .NET RIDs
(linux-x64, win-x64, …) — ideally as one RID-agnostic assembly *and*, where
fidelity demands it, as per-RID builds — without forking the C source or the libc.

## The problem is five problems

A chibil-compiled program is RID-locked at five distinct seams, with very
different costs:

1. **Module routing** — which native DLL exports a symbol (`libc.so.6` vs
   `msvcrt.dll`). chibil-link already abstracts this (`MapLib` + `-l` + probe).
2. **Symbol-name divergence** — same concept, different spelling
   (`__errno_location` vs `_errno`, `environ` vs `_environ`). A per-RID alias
   table fixes it.
3. **Missing functions** — `fork`, `pipe`, `mmap`, `opendir` don't exist on the
   Windows CRT; `stdin/stdout/stderr` are *data* on glibc but *functions*
   (`__acrt_iob_func`) on msvcrt. Needs shim code.
4. **ABI / struct layout** — `struct stat`, `FILE`, `time_t`, `off_t`, errno
   values, `O_*` flags differ binary-wise. **This is the deep one** — names remap,
   layouts don't.
5. **Selection** — build-time per-RID vs one assembly dispatching at runtime.

The msvcrt probe (earlier) hit #2/#3/#4: 42 of 140 QuickJS imports mismatched.
The whole game is *where you put the abstraction boundary*, because that decides
how much of #3 and #4 you fight.

## Three places to draw the boundary

- **A — boundary at the libc function** (per-RID native binding + shim). Keep
  musl headers; per RID, bind to that platform's libc with a name-alias map plus
  a shim for missing/mismatched functions. Never escapes #4 (every struct-taking
  call needs layout translation). This is literally what Cygwin *is*.
- **B — boundary at nothing** (managed libc in C#). Ship a managed `libc`
  implemented over the .NET BCL. One RID-agnostic ILOnly assembly, zero native
  dep. Cost: you write a libc; `fork`/`mmap(SHARED)`/signals are hard in managed.
- **C — boundary at the syscall** (PAL beneath a managed musl). Compile musl's
  *source* to MSIL; musl bottoms out in ~100 `__syscall(n, …)` calls — re-target
  only that backend as a managed PAL. Names and layouts become musl's
  *everywhere* (#2, #4 vanish by construction); missing functions don't exist (#3
  — musl supplies them atop syscalls); only ~100 syscalls differ per RID.

**Precedent for C:** Emscripten and WASI-libc are exactly "musl + a thin syscall
shim"; WSL1 emulated Linux syscalls over NT. Chosen direction: **C, with the PAL
implemented over the .NET BCL** — which fuses B and C: a managed musl whose
syscalls call portable .NET APIs. B's "one portable assembly" with C's tiny,
well-defined boundary and consistent ABI. Where the BCL can't express a syscall
portably, the PAL branches internally or P/Invokes the native syscall on that RID.

## "All" — one musl, one seam, swappable backends

This mirrors .NET's own shape (managed BCL + per-RID native PAL, selected by RID
assets). One codebase produces every tier because the seam is the syscall:

- **`musl.dll`** — musl source compiled by chibil to MSIL, **RID-neutral,
  compiled once**. Names/layouts are musl-x86_64 everywhere.
- **The seam** — `long __syscall(long n, long a1…a6)`. The whole platform
  difference lives behind this one method.
- **Backends:**
  - `pal.managed` (AnyCPU, pure BCL) — `open`→`File.Open`, `write`→stream,
    `mmap`→`MemoryMappedFile`/`NativeMemory`, `clock_gettime`→`Stopwatch`.
  - `pal.linux` — forwards `__syscall` to the real Linux syscall (full fidelity).
  - `pal.windows` — maps syscalls to Win32/NT.
  - hybrid — managed default, per-syscall escape to native.

**Distribution** as a `Chibil.Runtime` nupkg using .NET RID-asset resolution:
`lib/<tfm>/musl.dll` + `pal.managed.dll` for portable; `runtimes/<rid>/…/pal.<rid>.dll`
overlaid on publish. Runtime self-select (branch on `RuntimeInformation`) also
possible.

### In chibil-link terms

The resolver gains an **internal-libc** notion: symbols defined by `musl.dll`
resolve cross-assembly via the existing `DefinedMethodToken` path — *not* P/Invoke.
Only `__syscall` (and rare escapes) become P/Invoke, and only in native PALs. The
144 QuickJS imports collapse to the PAL set; on a pure-managed build, to **zero**.
`--print-imports` becomes the dial: a managed build prints `imports: 0`; a PAL
build prints only the syscall shims + a capability manifest.

## Capability tiers (be honest)

Not every backend honors every syscall. A syscall a backend can't do returns
`-ENOSYS` and musl/the program degrades normally.

| syscall class | managed | linux-native | windows-native |
|---|---|---|---|
| file/stream I/O, time, alloc, `mmap` (`MemoryMappedFile`) | ✅ | ✅ | ✅ |
| `fork` | ⚠️ emulated (`posix_spawn` + re-exec + state transfer) | ✅ real | ⚠️ `CreateProcess` emul |
| signals | ⚠️ partial (`SIGINT`→`CancelKeyPress`) | ✅ | ⚠️ |
| raw fd, `epoll`, ptrace | ⚠️/❌ | ✅ | varies |

Two new infra pieces the managed backend requires:
- **A PAL-owned fd table** (`int → SafeHandle/Stream`) — .NET doesn't expose
  process fds as ints portably. Load-bearing for `open/read/write/dup/select`.
- **Arch decoupling** — managed PAL is arch-free (musl-x86_64 layouts run on
  arm64 .NET unchanged); native PAL re-couples to host syscall numbers per arch.

## Backends & licensing

A Cygwin/**msys** backend fits as the *windows-fidelity* PAL at the syscall seam:
Cygwin already implements real `fork`/fds/signals/`mmap` on Windows, so the PAL is
just a syscall→Cygwin-POSIX-function translator (+ musl↔newlib struct ABI
translation at the boundary). **But `cygwin1.dll` / `msys-2.0.dll` are GPLv3** —
shipping them drags the program into GPL. So msys is an *optional* plug-in only.

**Permissive (MIT-ish) alternatives:**

| option | license | covers | catch |
|---|---|---|---|
| **.NET BCL** | MIT | files, sockets, `mmap`, process, timers, threads | managed; no real `fork` — *this is `pal.managed`* |
| **Cosmopolitan libc** | ISC | full POSIX-on-Windows incl. fork emulation | idiosyncratic; borrow code, don't link |
| **libuv** | MIT | cross-platform I/O/process/pipe/poll/fs/timer | `uv_spawn`, not `fork` |
| **APR** | Apache-2.0 | files, mmap, shm, process, network, threads | process create, not `fork` |
| **musl + newlib** | MIT / BSD | the libc itself | no Windows syscall backend |

There is **no drop-in MIT "Cygwin with real `fork`"** — real address-space-copy
fork is exactly what earns Cygwin its GPL. Permissive ⇒ *emulated* fork. The
practical kicker: **neither current target needs real fork** — QuickJS has none,
and bash already runs on CoreCLR via `posix_spawn` + re-exec. So a fully MIT stack
(musl + .NET BCL, both MIT) covers today's needs; Cygwin stays a future opt-in.

## Decisions

**Settled by steering:**
- **D2 — tiers:** support *all* (portable-managed, native-fidelity, opt-in GPL).
- **D3 — license:** MIT default (musl + BCL); Cygwin/GPL optional plug-in only.

**Recommended (to confirm):**
- **D1 — boundary:** **syscall seam** (one managed musl + PAL). *Lock first.*
- **D4 — `fork`:** **emulate everywhere** (`posix_spawn` + re-exec). Keeps the
  whole stack MIT; both targets already live with it.
- **D5 — musl:** **compile from source** to MSIL, hand-write the managed subset
  per-function only where chibil chokes. *(← the spike validates this.)*
- **D6 — native PAL:** **managed-BCL only for v1**; native (libuv/APR) later.
- **D7 — first target:** **QuickJS** (no fork/signals → proves managed PAL + the
  chibil-link internal-libc rewiring fastest).

## Next step — musl-compile spike

Validate D1/D5: compile a representative breadth of `targets/musl-1.2.6/src/*.c`
with `chibil --target=coreclr` and measure the completeness rate + failure
categories. This tells us whether "managed musl" is reachable now or needs N
chibil fixes first.

Harness: `targets/build/musl-spike.sh` (compile + count) and
`musl-spike-split.sh` (categorize failures by root cause).

## Spike results (2026-06-05) — D1/D5 VALIDATED

Stratified sample of **308 musl TUs**, compiled with
`chibil --target=coreclr -nostdinc -mlp64` + musl's own include set:

- **178 ok / 130 fail — 58% raw.** But the raw number is misleading; the
  decisive finding is the failure *distribution*.

**Every one of the 130 failures is a platform seam or a known shim — not libc
logic:**

| root cause | count | resolution |
|---|---|---|
| `arch/x86_64/syscall_arch.h` inline-asm `__syscall` | **68** | the seam — PAL provides `__syscall` (by design) |
| `arch/x86_64/atomic_arch.h` inline-asm atomics | 11 | map to managed `System.Threading.Interlocked` |
| `weak_alias(...)` attribute aliasing | 46 | forwarding shim, or chibil alias support |
| `internal/dynlink.h` `hidden`/tlsdesc parse gap | 4 | compat shim / small parser fix |
| `explicit_bzero` inline-asm barrier | 1 | trivial shim |

So **80 failures are inline-asm seams** (syscall + atomics + barrier — exactly the
boundary the PAL replaces) and **50 are attribute/aliasing macros** (`weak_alias`,
`hidden` — the same class of compat shim QuickJS and MicroPython already use).
**Zero failures are in actual libc computation.**

Subsystems that don't touch the seam already compile cleanly: `math` 15/15,
`prng` 11/11, `string` 65/74 (the 9 fails are all `weak_alias`), `stdlib` 17/22.

**Conclusion.** Managed musl is reachable *now*. The work is not "make chibil
compile C" (it already does) — it's a **`musl-chibil-compat.h`** that:
1. stubs `syscall_arch.h` so `__syscall` is an `extern` the PAL supplies,
2. redirects `atomic_arch.h` to managed atomics,
3. forwards `weak_alias` as a real alias/forwarder,
4. handles `hidden`/tlsdesc decls.

That single shim should lift this sample from 58% toward ~99% (only genuinely
platform-specific TUs remain — which the PAL owns anyway). This directly confirms
**D1** (the syscall seam is the right boundary — it's literally where musl breaks)
and **D5** (compile musl from source, shim the seam).

## Shim built + re-measured (2026-06-05) — lift PROVEN, one lever left

Built the compat layer:
- `targets/build/musl-compat/syscall_arch.h` — shadows the inline-asm
  `__syscall0..6` with forwarders to one extern `__chibil_syscall` (the PAL seam,
  made concrete). Shadowed via `-I…/musl-compat` first on the path.
- `targets/build/musl-compat/atomic_arch.h` — plain-C (single-threaded) `a_*`
  primitives replacing the `lock`-asm ones (real musl backs these with
  `Interlocked` externs).
- `targets/build/musl-chibil-compat.h` (force-included) — pulls musl's real
  `features.h`, then neutralizes `weak_alias` (chibil has no `__alias__`; real
  aliasing is a link-time concern).

Re-ran the same 308 TUs with `SHIM=1`:

| build | ok | fail | rate |
|---|---|---|---|
| baseline | 178 | 130 | 58% |
| **+ compat shim** | **228** | **80** | **74%** |

The shim **eliminated the entire seam**: `syscall_arch.h` 68→**0**,
`atomic_arch.h` 11→**0**, `weak_alias` 46→**0**. `ctype` 7/37→36/37, `string`
65/74→72/74.

**The 80 residuals collapse to a single chibil parser gap** — comma-separated
*function* declarations: `T f(…), g(…);`. It hits `src/internal/syscall.h:26`
(`hidden long __syscall_ret(...), __syscall_cp(...);` — pulled in by every
syscall-using TU → 75 failures) and `dynlink.h:114` (4). Confirmed in isolation:
`long a(int), b(long);` → *"parameter name omitted"*, while separate decls
compile. `hidden` is not involved. The only non-parser residual is 1 asm barrier
(`explicit_bzero`).

**So the next lever is one small chibil parser feature** — support multiple
function declarators in one declaration.

## Parser fixed + pthread shadow (2026-06-05) — 98%

- **chibil parser fix** (`Parser.cs` + `ParserDeclListTests.cs`, TDD): a top-level
  declaration whose first declarator is a function prototype followed by `,` now
  parses the remaining declarators as prototypes/globals (`FunctionDeclaratorTail`)
  instead of mis-routing them into function-DEFINITION handling. Full suite green
  (+2 tests, no regressions). Lift: **74% → 88%**.
- Clearing `syscall.h:26` exposed a **third seam asm header** I'd missed:
  `arch/x86_64/pthread_arch.h` reads the thread pointer via `mov %fs:0` asm
  (`__get_tp`). Added `musl-compat/pthread_arch.h` forwarding to a PAL extern
  `__chibil_get_tp`. Lift: **88% → 98%**.

Full progression on the 308-TU sample:

| stage | ok / fail | rate |
|---|---|---|
| baseline | 178 / 130 | 58% |
| + compat shim (syscall/atomic/weak_alias) | 228 / 80 | 74% |
| + parser fix (comma declarators) | 272 / 36 | 88% |
| + pthread_arch shadow | **302 / 6** | **98%** |

Subsystems now fully clean: `ctype` 37/37, `multibyte` 20/20, `stdlib`/`malloc`/
`math`/`time`/`locale`/`network`/`signal` all N/N.

**The 6 residuals are diverse and small** (no longer a single lever):
- 2 chibil internal crashes (a `NullReferenceException`, an internal assertion) —
  real chibil bugs, worth separate `systematic-debugging`;
- 1 `explicit_bzero` asm memory barrier (`__asm__("":::"memory")`);
- 1 `__scc` cast edge (`((long)(X))` in a syscall macro) + a couple misc.

## Gap covered: offsetof const-eval crash fixed (2026-06-05)

Root-caused the `NullReferenceException`: it was the **offsetof idiom**
`(char*)&((T*)0)->m - (char*)0` (musl's `offsetof` when `__GNUC__` is undefined,
used by `errno/strerror.c`). Pointer subtraction lowers to `Div(Sub(a,b), size)`;
`Eval2(Div)` evaluated operands through `Eval()` — a **null-ref label sink** — and
the address operand reached `EvalRval`, whose unconditional `label = null` wrote
through the null-ref → crash. Even though the value is a pure constant (null base,
no relocation).

Fix (`Parser.cs` `EvalRval` + `ParserConstEvalTests`, TDD): guard the label
writes against a null-ref sink — the pure-constant path needs no label and now
evaluates correctly (verified end-to-end: the idiom returns the real offset `8`,
not a silent `0`); a genuine symbol address under `Eval` is correctly reported
"not a compile-time constant" instead of crashing. Full suite green (306 pass).
`strerror.c` now compiles → 303/308.

**Final residual (5), all non-blocking:**
- `__libc_start_main.c`, `exit.c` (`_init`/`_fini`) — these are crt/**startup**
  TUs (the managed PAL provides entry + atexit), and the failures are anyway an
  artifact of the spike neutralizing `weak_alias` (which is what declares
  `_init`/`_fini`); real `weak_alias` support resolves them. Not a chibil gap.
- `explicit_bzero.c` — empty `__asm__("":::"memory")` compiler barrier (a no-op
  for chibil; supportable as an empty-asm no-op, or shimmed).
- `__stdio_seek.c` — a `__scc((long)(X))` cast edge (1 niche TU).
- `abort.c` — internal assertion on `&(struct k_sigaction){…}` (address of a
  compound literal as a syscall arg) — a real but niche chibil codegen edge.

So the two **real, general chibil bugs** the spike surfaced (comma-declarator
parse, offsetof const-eval crash) are both fixed with tests; the rest is PAL/crt
territory or niche edges. Managed musl compiles ~99% once `weak_alias` is real.

**Verdict: managed musl is ~98% compilable with chibil today.** The blockers were
exactly the platform seam (three arch asm headers — `syscall_arch`, `atomic_arch`,
`pthread_arch` — all PAL territory) plus one real parser gap (now fixed). Next:
implement the managed `__chibil_syscall` PAL + fd table + `__chibil_get_tp`, and
rewire chibil-link's resolver to treat `musl.dll` as internal libc (imports → ~0).
