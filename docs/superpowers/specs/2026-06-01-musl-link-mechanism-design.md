# chibil → musl link mechanism — design (MUSL Sub-Project 1)

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** MUSL-1 only — prove a chibil-compiled-to-MSIL C program can call into the **native musl `libc.so`** (`targets/musl-1.2.6/lib/libc.so`) via P/Invoke, on CoreCLR in WSL. This is a de-risking spike, not a product. Bash is MUSL-2; setjmp→exceptions is MUSL-3.

## 1. Context & the bigger roadmap

The user has natively built **musl 1.2.6** (`./configure && make` with gcc → `lib/libc.a`, `lib/libc.so`, x86_64) and **bash 5.3** (configured + gcc-built). The stated end goal is "compile bash to IL and link it to musl." Exploration established two hard facts:

1. **chibil emits pure MSIL** — you cannot statically link native x86-64 `libc.a`/`.o` into a managed PE. "Link to musl" can therefore only mean **P/Invoke into musl's `libc.so`** at runtime (the SP3a libc-P/Invoke mechanism, pointed at musl).
2. **Bash compiled to IL hits three walls:** `setjmp`/`longjmp` (123 call sites, drives the REPL/error-recovery), `fork()` (subshells/`$()` need address-space duplication a managed runtime can't provide), and async signals. A *fully running* IL bash is infeasible without major chibil work (and full subshell semantics may be impossible on CoreCLR).

The agreed roadmap (1→2→3): **MUSL-1** (this spec — the musl link mechanism), then **MUSL-2** (a bash compilation spike / gap report), then **MUSL-3** (a chibil codegen feature lowering setjmp/longjmp → managed try/catch). Each is its own spec→plan→build cycle.

MUSL-1 de-risks the *foundation* the later two would rely on: that musl's `libc.so` is even callable from a chibil-IL program running on a glibc CoreCLR.

## 2. The genuine risk this spike answers

A CoreCLR process on Ubuntu/WSL is itself **glibc**-based. Loading musl's `libc.so` alongside it puts **two C runtimes in one process** — separate `errno`, separate TLS, separate malloc heaps — and musl's `libc.so` is *also* musl's dynamic linker (`ld-musl-x86_64.so.1` is a symlink to it), whose internal state (`__libc`, TLS setup) may not initialize correctly when the file is merely `dlopen`'d (as a P/Invoke target) rather than used as the program interpreter.

Consequently the test is **graded**: pure (stateless) musl functions are expected to work; stateful ones (malloc/stdio/errno) may not, and the exact break point is a deliverable.

## 3. Mechanism

Reuses SP3a's per-symbol P/Invoke routing (`--pinvoke name=lib` / the `pinvokeMap` arg to `LinkPipeline.LinkToBytes`, and `SymbolResolver`'s `MapLib`, which passes a dotted/path-bearing name through as-is).

- chibil compiles the program to an `.obj` (CoreCLR target).
- chibil-link routes the program's libc symbols to musl by emitting each P/Invoke's ModuleRef as a **musl `.so` name**.
- **Staging:** copy `targets/musl-1.2.6/lib/libc.so` next to `app.dll` in the run directory under a distinct name (`libc_musl.so` — distinct so it can't be confused with the host glibc `libc.so`). Route `--pinvoke <fn>=libc_musl.so` for each libc symbol the program uses.
- A **relative** ModuleRef name (`libc_musl.so`) is resolved by the CoreCLR native-library loader against the app base directory (the dir holding `app.dll`), so the musl `.so` travels with the app — no absolute paths baked into the image.

> If the relative name fails to resolve from the app dir, the fallback is an absolute path in the ModuleRef (route `<fn>=<abs path to libc.so>`) or `LD_LIBRARY_PATH` pointing at the staged dir. The relative-name approach is tried first.

## 4. The proof programs (graded)

Two tiny C programs under `samples/musl/`:

- **Floor — `musl_pure.c`** (the link proof): `extern unsigned long strlen(const char*); int main(void){ return (int)strlen("hello world"); }` → exit **11**. `strlen` is pure (no global state). Routing it to `libc_musl.so` makes chibil-link emit a P/Invoke (not a local definition), so a passing run *is* musl's `strlen` executing. (Declared `extern` so chibil doesn't supply its own.)
- **Stretch — `musl_state.c`** (state): exercises a stateful path — e.g. `char *p = malloc(16); strcpy(p, "abc"); int n = (int)strlen(p); free(p); return n;` → exit **3**, and/or `puts("musl")` (musl stdio). Routed to `libc_musl.so`. Expected to either work (great) or fail in a documented way (the musl-in-glibc boundary).

Exit codes are the oracle (the DOOM-checksum trick used throughout this repo), so no output capture is required for the floor case.

## 5. Components

| Path | Role |
|---|---|
| `samples/musl/musl_pure.c` (new) | The floor proof: `strlen` → exit 11. |
| `samples/musl/musl_state.c` (new) | The stretch proof: malloc/strcpy/strlen (+ maybe puts) → exit 3. |
| `samples/musl/build-musl-link.sh` (new) | Compile each with chibil, link with `--pinvoke <syms>=libc_musl.so`, stage `libc_musl.so` from `targets/musl-1.2.6/lib/libc.so` beside the output, print the run command. |
| `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs` (new) | Compile→link (musl pinvoke map)→stage `libc_musl.so` in the WSL run dir→run via `WslRunner`→assert exit code. Floor asserts 11; stretch asserts 3 (or is marked documenting-the-break if it faults). WSL-gated. |
| `tools/chibil-link/` | **No change expected** — `--pinvoke`/`MapLib` already pass a `.so` name through. Only if relative-`.so` resolution needs a tweak. |
| `tests/.../WslRunner.cs` (maybe) | `WslRunner.Run` stages `app.dll`; MUSL-1 also needs the staged `libc_musl.so` in the same dir. If `Run` can't stage an extra file, add a small variant that drops a sidecar `.so` next to `app.dll` before running. |

## 6. Testing

- **Floor (headline):** `musl_pure` → exit **11** in WSL, with libc routed to the staged musl `libc.so`. Proves the IL→musl-symbol P/Invoke path resolves and executes. This is the must-pass criterion.
- **Stretch:** `musl_state` → exit **3** (and/or `puts` output). If it works, the musl-in-glibc-process boundary is wider than feared (good for MUSL-2/3); if it faults, the test documents *where* (malloc heap, stdio, errno) — recorded as a finding, and the floor test still defines success.
- **Non-regression:** none needed for chibil itself if the linker is unchanged; if a `MapLib`/staging tweak is made, run the CoreCLR suite to confirm no regression.
- **Windows:** N/A — musl is Linux-only; MUSL-1 is WSL-only.

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| musl `libc.so` won't `dlopen` cleanly in a glibc CoreCLR process (symbol/TLS/linker-state conflict) → even `strlen` faults | Medium | The whole point of the spike. If the floor faults, document it; the conclusion (musl-as-dlopen'd-lib is unviable in a glibc host; "link to musl" would need NativeAOT-static-musl instead) is itself the deliverable and reshapes MUSL-2/3. |
| Relative `.so` ModuleRef not found from the app dir | Medium | Absolute-path ModuleRef or `LD_LIBRARY_PATH` fallback (§3). |
| Stateful musl functions misbehave (uninitialized musl runtime) | Medium-High | Graded test: floor is pure; stretch documents the break — expected, not a failure. |
| `WslRunner` can't stage a sidecar `.so` | Low | Small runner variant to drop `libc_musl.so` beside `app.dll`. |

## 8. Out of scope
- **Bash** → MUSL-2. **setjmp/longjmp → exceptions** → MUSL-3.
- A fully-musl **static** native binary (chibil IL → NativeAOT → static-link musl, the Alpine model) — a different pipeline, not CoreCLR P/Invoke.
- Replacing glibc as the CoreCLR host libc; making the CLR itself run on musl.
- Windows; the full musl symbol surface (only the few functions the proof programs call are routed).

## 9. Success criteria
1. A chibil-compiled `musl_pure` exits **11** in WSL with `strlen` routed via P/Invoke to the staged musl `libc.so` — proving chibil-IL → musl-`libc.so` works for at least pure functions.
2. The stretch (`musl_state`) either passes (exit 3) or its failure mode is documented — establishing the musl-in-glibc-process boundary for the later sub-projects.
3. If any linker tweak was needed, the existing CoreCLR suite stays green.
