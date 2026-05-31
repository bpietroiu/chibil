# Managed SQLite via chibil — design (Sub-Project 1)

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope of this spec:** Sub-Project 1 (SP1) only — compile `sqlite3.c` with chibil and run a `:memory:` CREATE/INSERT/SELECT harness from C, cross-platform. No C#, no export feature, no managed provider yet (those are SP2/SP3).

## 1. Goal & vision

Make SQLite a **fully-managed product for .NET**: SQLite-as-IL with a clean C# API and **no native `sqlite3` binary**, cross-platform (one artifact runs anywhere CoreCLR runs). This builds directly on chibil's CoreCLR/Linux target and the in-house linker, and closes the README's "consuming C from .NET is not complete yet" gap.

### Product layering (end state)
```
┌ Ergonomic C# API (pure C#, ADO.NET-ish)              ┐  SP4
├ Sqlite3.Native — raw export surface (static class)   ┤  SP2  ← new chibil "export" feature
├ Managed platform provider (VFS + mem via SQLite's    │  SP3
│   pluggable sqlite3_vfs / sqlite3_mem_methods)       │
└ sqlite3 core — chibil-compiled amalgamation (IL)     ┘  SP1
```

### Decomposition (each its own spec → plan → build)
- **SP1 (this spec):** compile + run `:memory:` from C. Prove chibil compiles `sqlite3.c` and a `:memory:` CRUD harness runs, using a **C-side** platform shim. De-risks the compiler and the SQLite configuration.
- **SP2:** expose to C# — build the chibil **export feature** (C functions → public static methods on a named type) and consume a small `sqlite3_*` surface from a C# test doing the same CRUD.
- **SP3:** managed pluggable provider — re-implement the platform layer behind SQLite's native interfaces (`sqlite3_vfs`, `sqlite3_mem_methods`, `sqlite3_mutex_methods`) as swappable backends (managed default; libc/custom optional); add disk persistence via `System.IO`.
- **SP4:** ergonomic C# API + packaging as a .NET library.

### Key decisions (locked during brainstorming)
| Decision | Choice |
|---|---|
| Primary benefit | A managed SQLite **product** (expose/consume is the point) |
| Dependency stance | **Abstract / pluggable** platform layer built on SQLite's own `sqlite3_vfs` / `sqlite3_mem_methods` / `sqlite3_mutex_methods`; default **managed** provider; libc/custom swappable |
| C# binding mechanism (SP2) | A **new chibil export feature**: emit chosen functions as public static methods on a named type (no reflection, no P/Invoke) |
| First milestone (SP1) | **Compile-and-run spike**: `sqlite3.c` → link → run a `:memory:` CRUD harness from a C `main` |
| SP1 strategy | **Reference-build-first** (build natively with MSVC to validate config/provider, then point chibil at identical sources) |

## 2. SP1 strategy — reference-build-first

Assemble the exact inputs once and feed them to two builds:
1. **Native reference build** with `cl.exe` → `ref.exe`. Confirms the SQLite **configuration**, the **platform shim**, and the **harness** are correct against real SQLite (exit code 55).
2. **chibil build** of the *identical* sources → `app.dll`. Run it; it must match `ref.exe` (exit 55).

Because the only variable between (1) and (2) is the compiler, any divergence is provably a **chibil gap**, isolated by construction. This reuses the MSVC toolchain already wired via `run-tests.cmd`/`vcvars`.

Rejected alternatives: compiling straight at chibil (every failure ambiguous against 250k lines); hand-subsetting SQLite (the amalgamation is monolithic — "subsetting" is just `SQLITE_OMIT_*` config).

## 3. SP1 components

| File | Role |
|---|---|
| `samples/sqlite/vendor/sqlite3.c`, `sqlite3.h` | Official **amalgamation**, vendored at a pinned version (record the version + source URL). |
| `samples/sqlite/sqlite_cfg.h` | Compile-time config (see §4). |
| `samples/sqlite/sqlite_shim.c` | SP1 **platform provider** in C: imported mem/str functions, a `sqlite3_vfs`, and `platform_init()`. |
| `samples/sqlite/main.c` | The `:memory:` CRUD harness (exit-code oracle). |
| `samples/sqlite/build-ref.cmd` | Native MSVC reference build → `ref.exe`. |
| `samples/sqlite/build-chibil.sh` / `.cmd` | chibil compile + link → `app.dll` (+ `runtimeconfig.json`). |

### 3.1 The harness (`main.c`) — exit-code oracle (no printf/IO)
Verifies CRUD through the process exit code (the DOOM-checksum trick), so it needs **no** formatting/output function:
```c
static int add_cb(void *p, int argc, char **argv, char **col){
    if (argc > 0 && argv[0]) *(int*)p += atoi_simple(argv[0]); /* or sqlite3-provided */
    return 0;
}
int main(void){
    platform_init();
    sqlite3 *db;
    if (sqlite3_open(":memory:", &db) != SQLITE_OK) return 100;
    sqlite3_exec(db, "CREATE TABLE t(a INTEGER); "
                     "INSERT INTO t VALUES(20),(22),(13);", 0,0,0);
    int sum = 0;
    sqlite3_exec(db, "SELECT sum(a) FROM t", add_cb, &sum, 0);
    sqlite3_close(db);
    return sum;   /* expect 55 */
}
```
This exercises open → SQL parse → bytecode VM (`sqlite3VdbeExec`) → aggregate → `sqlite3_exec` callback → close. `atoi_simple` is a tiny shim (or use `sqlite3`'s own conversion) to avoid an `atoi` import. Result path uses no time/randomness, so reference parity is exact and deterministic.

### 3.2 The platform shim (`sqlite_shim.c`)
- **mem/str functions** SQLite imports: `memcpy`, `memset`, `memmove`, `memcmp`, `strlen`, `strcmp`, `strncmp`, and whatever else the link step reports unresolved. Tiny, self-contained C. (These compile via chibil too — no native dependency.)
- **VFS** (`sqlite3_vfs`, registered as default because `SQLITE_OS_OTHER=1`): real `xRandomness` (seeded counter is fine for SP1), `xCurrentTime`/`xCurrentTimeInt64` (fixed/epoch or simple counter), `xSleep` (no-op), `xGetLastError`, `xFullPathname`, `xAccess`. File methods (`xOpen`/`xRead`/`xWrite`/…) are stubbed to return `SQLITE_IOERR` and are **never invoked** with `:memory:` + `SQLITE_TEMP_STORE=3`.
- **Allocator:** no `malloc` shim — `SQLITE_ENABLE_MEMSYS5` + `SQLITE_ZERO_MALLOC`, and `platform_init()` calls `sqlite3_config(SQLITE_CONFIG_HEAP, staticBuf, sizeof(staticBuf), 16)` to hand SQLite a static buffer it suballocates from. `staticBuf` sized generously (e.g. a few MB) for the harness.
- **`platform_init()`:** configures the heap, registers + defaults the VFS, calls `sqlite3_initialize()` (because `SQLITE_OMIT_AUTOINIT`).

### 3.3 Config (`sqlite_cfg.h`)
```
SQLITE_OS_OTHER=1            # no built-in unix/win VFS; we register our own
SQLITE_THREADSAFE=0         # single-threaded; no mutex/atomics
SQLITE_TEMP_STORE=3         # temp tables/indices always in RAM (no temp files)
SQLITE_ENABLE_MEMSYS5       # built-in allocator over a static heap
SQLITE_ZERO_MALLOC          # never call system malloc
SQLITE_OMIT_LOADEXTENSION   # no dlopen
SQLITE_OMIT_AUTOINIT        # we call sqlite3_initialize() ourselves
# Computed goto stays OFF (do NOT define SQLITE_ENABLE_COMPUTED_GOTO) → ordinary switch in the VM
```
OMITs kept modest so core SQL (the CRUD path) stays intact. Additional `SQLITE_OMIT_*` may be added only if a specific feature surfaces an unsupportable construct (documented per case).

## 4. Compiler-gap strategy (the unknown-size work)

A 250k-line file will surface chibil gaps that cannot be enumerated in advance. The work is a **diagnostic loop**, not a fixed task list.

### The loop (one gap at a time)
1. Compile → capture the **first** failure (parse error / codegen assert / unresolved link symbol / runtime mismatch vs `ref.exe`).
2. **Classify:** (a) C feature not parsed/emitted; (b) semantic/runtime bug (compiles but `app.dll` ≠ `ref.exe`); (c) missing libc symbol (add to `sqlite_shim.c`).
3. **Reduce** to a minimal `scenarios/`-style repro.
4. **Fix** chibil with the repro as a regression test; resume.

The `autoresearch:debug` / `autoresearch:fix` skills fit this loop; every fix lands a `scenarios/` regression so SQLite permanently hardens chibil.

### Front-loaded risks (the plan tackles these first)
- **Varargs definition (risk #1):** SQLite defines `sqlite3_mprintf`/`sqlite3VXPrintf` with `va_list`. chibil allocates a `__va_area__` (chibicc SysV model, `Parser.cs:1843`) but whether `va_start`/`va_arg` actually work on the **MSIL** target is **unverified**. A focused spike (`scenarios/vararg-define.c` — define and call a variadic function, verify via exit code) gates whether SQLite is attemptable. If broken, fixing varargs becomes its own tracked task and **blocks** SQLite.
- **Computed goto:** kept off via config → ordinary `switch`. Guarded, not fixed.
- **`setjmp`/`longjmp`:** not used by SQLite (good — unsupportable on MSIL).
- **Scale stress:** huge functions, deep initializers, `long double`, bitfields, unions, function-pointer tables (VFS/vtable structs). chibil has scenarios for most; SQLite stresses them at scale — fix per-gap.
- **Static init order:** sidestepped via runtime `sqlite3_initialize()` + `SQLITE_OMIT_AUTOINIT`.

## 5. Success criteria (SP1)
1. `chibil --target=coreclr -c` compiles `sqlite3.c` + `sqlite_shim.c` + `main.c` with **zero errors**.
2. `chibil-link` produces `app.dll`; it runs on **Windows** (in-process `Assembly.Load` + `dotnet` host) **and Linux/WSL**, exit code **55**, matching native `ref.exe`.
3. Every chibil gap fixed en route lands a minimal `scenarios/` regression test.

## 6. Testing
- **Reference parity:** `ref.exe` (MSVC native) exit 55 is the oracle; `app.dll` must match. Deterministic result path → exact parity.
- **Per-gap regressions:** each `scenarios/*.c` repro, run by the existing xUnit harness (`run-tests.cmd`).
- **Varargs spike** up front, before the big compile (go/no-go checkpoint).
- **Cross-platform:** the same `app.dll` under Windows CoreCLR and WSL — proves the no-native-binary claim.

## 7. Risks
| Risk | Likelihood | Mitigation |
|---|---|---|
| Varargs definition broken on MSIL | High | Front-loaded spike; own task if it fails — **blocks SQLite** |
| Long tail of small compiler gaps | High | Open-ended diagnostic loop; SP1 is bounded by gap count, not a fixed task list |
| Gap with no clean MSIL mapping | Medium | Reduce, document; `SQLITE_OMIT_*` around it or escalate |
| Amalgamation size stresses codegen | Medium | Surfaces early; fix per-gap |

## 8. Explicitly out of scope (SP1)
- C# export feature and any C# consumption → SP2.
- Managed VFS/allocator (SP1 uses C shims); disk persistence → SP3.
- Ergonomic API, packaging, threading, full feature set, `printf`-style output → SP3/SP4.
- Performance tuning.

## 9. Note on open-endedness
Unlike the bounded Linux-self-contained project, SP1's compiler-gap work is **genuinely unpredictable in size**. The implementation plan will be structured as *front-loaded spikes (varargs first) + a repeating diagnose→reduce→fix→regress loop*, with a **go/no-go checkpoint after the varargs spike**, rather than a fixed task count.
