# Managed SQLite via chibil — SP1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compile the SQLite amalgamation with chibil and run a `:memory:` CREATE/INSERT/SELECT harness from C, on Windows CoreCLR and Linux/WSL, matching a native MSVC reference build (exit code 55) — with no native `sqlite3` binary.

**Architecture:** Reference-build-first. Assemble one set of inputs (vendored `sqlite3.c`, a minimal chibil-parseable libc header set, `sqlite_cfg.h`, a C platform shim, a C harness); build them natively with `cl.exe` to validate config+semantics; then compile the identical sources with `chibil --target=coreclr` and link with `chibil-link`, fixing each chibil gap (with a `scenarios/` regression per gap) until the managed `app.dll` matches the reference.

**Tech Stack:** chibil (CoreCLR target), `chibil-link`, `System.Reflection.Metadata`, the SQLite C amalgamation, MSVC `cl.exe` (reference oracle, via `run-tests.cmd`/vcvars), xUnit, WSL + `dotnet`.

**Reference docs:** Spec at `docs/superpowers/specs/2026-06-01-sqlite-managed-sp1-design.md` (read first). Depends on the CoreCLR target + `chibil-link` from the `linux-selfcontained-builds` branch (this branch is stacked on it).

**Critical risk acknowledged up front:** chibil does **not** implement `__builtin_va_start`/`__builtin_va_arg` (verified: only `__builtin_types_compatible_p`/`reg_class`/`compare_and_swap`/`atomic_exchange` exist; `__va_area__` at `Parser.cs:1843` is vestigial). SQLite *defines* variadic functions (`sqlite3_mprintf`, `sqlite3VXPrintf`). **Part B is therefore a likely-blocking prerequisite**, not a quick check. A go/no-go checkpoint follows it.

---

## File Structure

**New files (all under `samples/sqlite/` unless noted):**
- `vendor/sqlite3.c`, `vendor/sqlite3.h` — pinned SQLite amalgamation (fetched by a script; not hand-edited).
- `fetch-amalgamation.ps1` / `fetch-amalgamation.sh` — download + verify the pinned amalgamation.
- `include/stddef.h`, `include/stdint.h`, `include/stdarg.h` — initial minimal chibil-parseable libc headers. More added on demand by the gap loop.
- `sqlite_cfg.h` — SQLite compile-time config.
- `sqlite_shim.c` — platform provider in C: imported mem/str funcs, a `sqlite3_vfs`, `platform_init()`.
- `main.c` — the `:memory:` CRUD harness (exit-code oracle).
- `build-ref.cmd` — native MSVC reference build → `ref.exe`.
- `build-chibil.cmd` / `build-chibil.sh` — chibil compile + `chibil-link` → `app.dll`.
- `README.md` — how to build/run; pinned version; status.
- `tests/Chibil.Tests/CoreClr/VarargsTests.cs` — varargs spike (Part B).
- `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs` — final SP1 regression (Part C).
- `scenarios/*.c` — one minimal repro per compiler gap fixed (Part C loop).

**Modified files (only as the gap loop demands):** `chibil/*.cs` (per-gap fixes), `chibil/Tokenizer.cs`/`Parser.cs`/`CodeGen.cs` (varargs, Part B).

---

## PART A — Setup, headers, and the native reference build

### Task A1: Vendor the SQLite amalgamation (pinned)

**Files:**
- Create: `samples/sqlite/fetch-amalgamation.ps1`, `samples/sqlite/fetch-amalgamation.sh`
- Create (by running the script): `samples/sqlite/vendor/sqlite3.c`, `vendor/sqlite3.h`
- Create: `samples/sqlite/.gitignore` (optional — decide whether to commit the ~9MB `sqlite3.c` or fetch it; default: **commit it** for reproducibility)

- [ ] **Step 1: Write the fetch script (PowerShell)**

```powershell
# samples/sqlite/fetch-amalgamation.ps1
# Pinned SQLite amalgamation. If this URL 404s, bump to a current release
# from https://sqlite.org/download.html and update VERSION + the expected files.
$ErrorActionPreference = 'Stop'
$VERSION = '3470200'   # SQLite 3.47.2
$url = "https://www.sqlite.org/2024/sqlite-amalgamation-$VERSION.zip"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$vendor = Join-Path $here 'vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
$zip = Join-Path $env:TEMP "sqlite-$VERSION.zip"
Invoke-WebRequest -Uri $url -OutFile $zip
$tmp = Join-Path $env:TEMP "sqlite-$VERSION"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
Expand-Archive $zip -DestinationPath $tmp
$src = Get-ChildItem -Recurse $tmp -Filter sqlite3.c | Select-Object -First 1
$hdr = Get-ChildItem -Recurse $tmp -Filter sqlite3.h | Select-Object -First 1
Copy-Item $src.FullName (Join-Path $vendor 'sqlite3.c')
Copy-Item $hdr.FullName (Join-Path $vendor 'sqlite3.h')
Write-Output "vendored sqlite $VERSION -> $vendor"
```

- [ ] **Step 2: Run it; verify the files exist**

Run: `pwsh samples/sqlite/fetch-amalgamation.ps1`
Expected: prints `vendored sqlite 3470200`; `samples/sqlite/vendor/sqlite3.c` is ~9 MB, `sqlite3.h` present.

> If there is no network access during execution, obtain the amalgamation out-of-band and place the two files at those paths; record the version in `README.md`. Write the equivalent `fetch-amalgamation.sh` (curl + unzip) for Linux parity.

- [ ] **Step 3: Commit**

```bash
git add samples/sqlite/fetch-amalgamation.ps1 samples/sqlite/fetch-amalgamation.sh samples/sqlite/vendor/sqlite3.c samples/sqlite/vendor/sqlite3.h
git commit -m "chore(sqlite): vendor SQLite 3.47.2 amalgamation"
```

---

### Task A2: SQLite config, platform shim, and harness

**Files:**
- Create: `samples/sqlite/sqlite_cfg.h`, `samples/sqlite/sqlite_shim.c`, `samples/sqlite/main.c`

- [ ] **Step 1: Write `sqlite_cfg.h`**

```c
/* samples/sqlite/sqlite_cfg.h — force-included before sqlite3.c */
#define SQLITE_OS_OTHER 1            /* no built-in unix/win VFS; we register one */
#define SQLITE_THREADSAFE 0         /* single-threaded; no mutex/atomics */
#define SQLITE_TEMP_STORE 3         /* temp tables/indices always in RAM */
#define SQLITE_ENABLE_MEMSYS5 1     /* built-in allocator over a static heap */
#define SQLITE_ZERO_MALLOC 1        /* never call system malloc */
#define SQLITE_OMIT_LOADEXTENSION 1
#define SQLITE_OMIT_AUTOINIT 1      /* we call sqlite3_initialize() ourselves */
/* Do NOT define SQLITE_ENABLE_COMPUTED_GOTO -> VM uses ordinary switch. */
```

- [ ] **Step 2: Write `sqlite_shim.c`** (platform provider — see spec §3.2)

```c
/* samples/sqlite/sqlite_shim.c */
#include "sqlite3.h"

/* --- imported mem/str functions SQLite expects from libc.
   Start with these; the link step will report any others to add. --- */
void *memcpy(void *d, const void *s, unsigned long n){
    char *dd=d; const char *ss=s; while(n--) *dd++=*ss++; return d; }
void *memset(void *d, int c, unsigned long n){
    char *dd=d; while(n--) *dd++=(char)c; return d; }
void *memmove(void *d, const void *s, unsigned long n){
    char *dd=d; const char *ss=s;
    if(dd<ss){ while(n--) *dd++=*ss++; }
    else { dd+=n; ss+=n; while(n--) *--dd=*--ss; } return d; }
int memcmp(const void *a, const void *b, unsigned long n){
    const unsigned char *x=a,*y=b; while(n--){ if(*x!=*y) return *x-*y; x++; y++; } return 0; }
unsigned long strlen(const char *s){ const char *p=s; while(*p) p++; return p-s; }
int strcmp(const char *a, const char *b){ while(*a&&*a==*b){a++;b++;} return (unsigned char)*a-(unsigned char)*b; }
int strncmp(const char *a, const char *b, unsigned long n){
    while(n&&*a&&*a==*b){a++;b++;n--;} return n? (unsigned char)*a-(unsigned char)*b : 0; }

/* --- a static heap for memsys5 --- */
static char g_heap[8*1024*1024];

/* --- minimal VFS (OS_OTHER requires one; file methods unused for :memory:) --- */
static unsigned int g_rng = 0x12345678u;
static int vfsRandomness(sqlite3_vfs *v, int n, char *out){
    (void)v; for(int i=0;i<n;i++){ g_rng = g_rng*1103515245u + 12345u; out[i]=(char)(g_rng>>16); } return n; }
static int vfsSleep(sqlite3_vfs *v, int micros){ (void)v; (void)micros; return 0; }
static int vfsCurrentTime(sqlite3_vfs *v, double *p){ (void)v; *p = 2440587.5; return SQLITE_OK; } /* unix epoch as Julian day */
static int vfsGetLastError(sqlite3_vfs *v, int n, char *b){ (void)v;(void)n;(void)b; return 0; }
static int vfsOpen(sqlite3_vfs *v, const char *z, sqlite3_file *f, int flags, int *out){
    (void)v;(void)z;(void)f;(void)flags; if(out)*out=0; return SQLITE_IOERR; }
static int vfsDelete(sqlite3_vfs *v, const char *z, int s){ (void)v;(void)z;(void)s; return SQLITE_IOERR; }
static int vfsAccess(sqlite3_vfs *v, const char *z, int f, int *out){ (void)v;(void)z;(void)f; if(out)*out=0; return SQLITE_OK; }
static int vfsFullPathname(sqlite3_vfs *v, const char *z, int n, char *out){
    (void)v; int i=0; while(z[i]&&i<n-1){ out[i]=z[i]; i++; } out[i]=0; return SQLITE_OK; }

static sqlite3_vfs g_vfs = {
    3, sizeof(sqlite3_file), 512, 0, "chibil-mem", 0,
    vfsOpen, vfsDelete, vfsAccess, vfsFullPathname,
    0,0,0,0,                       /* dlOpen/Error/Sym/Close (load ext omitted) */
    vfsRandomness, vfsSleep, vfsCurrentTime, vfsGetLastError,
    0,0,0,0,0,0                    /* currentTimeInt64 + later version methods */
};

void platform_init(void){
    sqlite3_config(SQLITE_CONFIG_HEAP, g_heap, (int)sizeof(g_heap), 16);
    sqlite3_vfs_register(&g_vfs, 1);
    sqlite3_initialize();
}
```

> The `sqlite3_vfs` struct field order/count differs slightly by SQLite version; copy the exact struct shape from the vendored `sqlite3.h` and fill the methods named above. The reference build (Task A4) will fail loudly if the initializer is wrong — fix it there before touching chibil.

- [ ] **Step 3: Write `main.c`** (exit-code oracle; uses prepare/step to avoid `atoi`)

```c
/* samples/sqlite/main.c */
#include "sqlite3.h"
void platform_init(void);

int main(void){
    platform_init();
    sqlite3 *db = 0;
    if (sqlite3_open(":memory:", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db,
            "CREATE TABLE t(a INTEGER);"
            "INSERT INTO t VALUES(20),(22),(13);", 0,0,0) != SQLITE_OK) return 102;
    sqlite3_stmt *st = 0;
    if (sqlite3_prepare_v2(db, "SELECT sum(a) FROM t", -1, &st, 0) != SQLITE_OK) return 103;
    int sum = 0;
    if (sqlite3_step(st) == SQLITE_ROW) sum = sqlite3_column_int(st, 0);
    sqlite3_finalize(st);
    sqlite3_close(db);
    return sum;   /* expect 55 */
}
```

- [ ] **Step 4: Commit**

```bash
git add samples/sqlite/sqlite_cfg.h samples/sqlite/sqlite_shim.c samples/sqlite/main.c
git commit -m "feat(sqlite): SP1 config, platform shim, and :memory: harness"
```

---

### Task A3: Initial chibil-parseable libc headers

chibil ships **no** standard headers. Provide the minimal ones SQLite needs to *parse*. Start with three; the gap loop (Part C) adds more (`string.h`, `stdlib.h`, `limits.h`, `assert.h`, etc.) as `#include`/unknown-type errors surface.

**Files:**
- Create: `samples/sqlite/include/stddef.h`, `include/stdint.h`, `include/stdarg.h`

- [ ] **Step 1: Write `stddef.h` and `stdint.h`** (types only)

```c
/* samples/sqlite/include/stddef.h */
#ifndef _CHIBIL_STDDEF_H
#define _CHIBIL_STDDEF_H
typedef unsigned long size_t;
typedef long ptrdiff_t;
#define NULL ((void*)0)
#define offsetof(t,m) ((size_t)&(((t*)0)->m))
#endif
```
```c
/* samples/sqlite/include/stdint.h */
#ifndef _CHIBIL_STDINT_H
#define _CHIBIL_STDINT_H
typedef signed char int8_t;     typedef unsigned char uint8_t;
typedef short int16_t;          typedef unsigned short uint16_t;
typedef int int32_t;            typedef unsigned int uint32_t;
typedef long long int64_t;      typedef unsigned long long uint64_t;
typedef long intptr_t;          typedef unsigned long uintptr_t;
#define INT8_MAX 127
#define INT16_MAX 32767
#define INT32_MAX 2147483647
#define INT64_MAX 9223372036854775807LL
#define UINT8_MAX 255
#define UINT16_MAX 65535
#define UINT32_MAX 4294967295u
#define UINT64_MAX 18446744073709551615ULL
#endif
```

- [ ] **Step 2: Write a *placeholder* `stdarg.h`**

```c
/* samples/sqlite/include/stdarg.h
   NOTE: va_start/va_arg map to compiler intrinsics that chibil does NOT yet
   implement (see Part B). This header is finalized in Part B once the
   varargs lowering exists. For now it lets includes resolve. */
#ifndef _CHIBIL_STDARG_H
#define _CHIBIL_STDARG_H
typedef __builtin_va_list va_list;
#define va_start(ap, last) __builtin_va_start(ap, last)
#define va_arg(ap, type)   __builtin_va_arg(ap, type)
#define va_end(ap)         __builtin_va_end(ap)
#define va_copy(d, s)      __builtin_va_copy(d, s)
#endif
```

- [ ] **Step 3: Commit**

```bash
git add samples/sqlite/include/
git commit -m "feat(sqlite): initial minimal chibil-parseable libc headers"
```

---

### Task A4: Native MSVC reference build → `ref.exe` (the oracle)

**Files:**
- Create: `samples/sqlite/build-ref.cmd`

- [ ] **Step 1: Write `build-ref.cmd`**

```bat
@echo off
REM Native MSVC reference build (run inside a VS dev environment, e.g. via run-tests.cmd's vcvars).
setlocal
set HERE=%~dp0
cl /nologo /Fe:"%HERE%ref.exe" /I"%HERE%vendor" /FI"%HERE%sqlite_cfg.h" ^
   "%HERE%vendor\sqlite3.c" "%HERE%sqlite_shim.c" "%HERE%main.c"
if errorlevel 1 ( echo REFERENCE BUILD FAILED & exit /b 1 )
echo built %HERE%ref.exe
```

- [ ] **Step 2: Build + run inside vcvars; assert exit 55**

Run (from repo root, in a VS dev shell — reuse the vcvars setup from `run-tests.cmd`):
```
call .github\workflows\vcvars.cmd x64
call samples\sqlite\build-ref.cmd
samples\sqlite\ref.exe & echo EXIT=%ERRORLEVEL%
```
Expected: `built ...ref.exe` then `EXIT=55`.

> This validates the **config + shim + harness** against real SQLite, independent of chibil. If `cl.exe` reports a bad `sqlite3_vfs` initializer or a missing function, fix `sqlite_shim.c`/`sqlite_cfg.h` here — *before* any chibil work. `ref.exe` is a build artifact; add `samples/sqlite/ref.exe` and `*.obj` to `samples/sqlite/.gitignore`.

- [ ] **Step 3: Commit**

```bash
git add samples/sqlite/build-ref.cmd samples/sqlite/.gitignore
git commit -m "test(sqlite): native MSVC reference build (exit 55 oracle)"
```

---

## PART B — Varargs definition on the MSIL target (the linchpin)

> **Read this first.** chibil cannot currently compile a C function that *defines* and consumes varargs via `va_start`/`va_arg` on the MSIL target. SQLite needs this. Task B1 proves the gap; Task B2 is the (likely substantial) fix. If B2 balloons, **stop and split it into its own spec** — it is a chibil compiler feature, not SQLite glue.

### Task B1: Varargs spike — prove the gap

**Files:**
- Create: `tests/Chibil.Tests/CoreClr/VarargsTests.cs`

- [ ] **Step 1: Write the spike test** (define + call a variadic function; verify via return value)

```csharp
// tests/Chibil.Tests/CoreClr/VarargsTests.cs
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class VarargsTests
{
    [Fact]
    public void Variadic_function_definition_sums_args()
    {
        // sum_n consumes `count` int args via va_list; main calls it.
        string src = @"
typedef __builtin_va_list va_list;
#define va_start(ap,last) __builtin_va_start(ap,last)
#define va_arg(ap,t)      __builtin_va_arg(ap,t)
#define va_end(ap)        __builtin_va_end(ap)
int sum_n(int count, ...){
    va_list ap; va_start(ap, count);
    int s = 0;
    for (int i = 0; i < count; i++) s += va_arg(ap, int);
    va_end(ap);
    return s;
}
int main(void){ return sum_n(3, 20, 22, 13); }   // expect 55
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
        var asm = System.Reflection.Assembly.Load(pe);
        object r = asm.EntryPoint.Invoke(null, new object[] { new string[0] });
        Assert.Equal(55, (int)r);
    }
}
```

- [ ] **Step 2: Run it — observe the failure mode**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Variadic_function_definition_sums_args`
Expected: **FAIL** — either a parse error on `__builtin_va_start`/`__builtin_va_arg` (not implemented), a codegen error, or a wrong/garbage result. **Record the exact failure** in the commit message and in `samples/sqlite/README.md`.

- [ ] **Step 3: Commit the failing spike (xfail marker)**

Mark the test `[Fact(Skip = "Part B: varargs definition not yet implemented on MSIL target")]` temporarily so the suite stays green, and commit:
```bash
git add tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "test(varargs): failing spike — va_start/va_arg unimplemented on MSIL"
```

### Task B2: Implement varargs definition (design + build)

> **This is a compiler feature with real design choices and is the highest-risk item in SP1.** Do NOT guess at an implementation inline. Approach it as a focused design problem first.

- [ ] **Step 1: Investigate the target ABI for variadic calls.** Determine how chibil currently lowers a *call* to a variadic function (it emits VARARG call-site signatures per the lcc.net report). The definition side must agree with the call side. Read `chibil/CodeGen.cs` (vararg call sites), `chibil/Parser.cs:1843` (`__va_area__`), and how arguments arrive in a called method. Document the actual IL calling convention chibil uses for `...`.

- [ ] **Step 2: Choose the lowering.** The proven MSIL approach (Hanson, lcc.NET) is the CLR `arglist`/`System.ArgIterator` model: a variadic C function compiles to an IL method using the `vararg` calling convention; `va_start` → `arglist` capture; `va_arg(ap,T)` → `ArgIterator.GetNextArg` + typed read. Confirm this composes with chibil's existing vararg *call* emission and the CoreCLR target. If chibil's call side is incompatible, reconcile both sides.

- [ ] **Step 3: STOP-and-split check.** If Step 2 reveals this needs more than a few focused changes (new IL opcodes, ArgIterator typerefs, call-side rework), **write a dedicated mini-spec** `docs/superpowers/specs/2026-06-01-chibil-varargs-msil-design.md` and brainstorm/plan it separately, then return here. Varargs is a prerequisite, not part of SQLite proper.

- [ ] **Step 4: Implement `__builtin_va_start` / `__builtin_va_arg` / `__builtin_va_end` / `__builtin_va_copy`** in the parser + codegen, with the spike test (un-skipped) as the regression. Implement the smallest thing that makes the spike pass.

- [ ] **Step 5: Run the spike, un-skipped**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Variadic_function_definition_sums_args`
Expected: **PASS** (returns 55). Add 2-3 more varargs `scenarios`/in-process cases (mixed types: `int`+`long long`+`double`; zero variadic args) and make them pass.

- [ ] **Step 6: Finalize `samples/sqlite/include/stdarg.h`** so its macros match the implemented builtins, then commit.

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs samples/sqlite/include/stdarg.h
git commit -m "feat(codegen): implement varargs definition (va_start/va_arg) on MSIL target"
```

> **Checkpoint (go/no-go):** SQLite is not attemptable until B1 passes. If B2 is split into its own spec, pause SP1 here and report.

---

## PART C — chibil compile of SQLite + the gap loop

### Task C1: chibil build script + first compile attempt

**Files:**
- Create: `samples/sqlite/build-chibil.cmd`, `samples/sqlite/build-chibil.sh`

- [ ] **Step 1: Write `build-chibil.sh`** (Linux/WSL; the `.cmd` mirrors it for Windows)

```bash
#!/usr/bin/env bash
# samples/sqlite/build-chibil.sh — compile SQLite + shim + harness with chibil, link with chibil-link.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$HERE/app.dll"
DEFS=(-DSQLITE_OS_OTHER=1 -DSQLITE_THREADSAFE=0 -DSQLITE_TEMP_STORE=3
      -DSQLITE_ENABLE_MEMSYS5=1 -DSQLITE_ZERO_MALLOC=1
      -DSQLITE_OMIT_LOADEXTENSION=1 -DSQLITE_OMIT_AUTOINIT=1)
INCS=(-I"$HERE/include" -I"$HERE/vendor")
OBJS=()
for src in "$HERE/vendor/sqlite3.c" "$HERE/sqlite_shim.c" "$HERE/main.c"; do
  o="$(mktemp --suffix=.obj)"; OBJS+=("$o")
  echo "[chibil] $src"
  dotnet run -c Release --project "$ROOT/chibil" -- \
    --target=coreclr "${DEFS[@]}" "${INCS[@]}" -cc1 -cc1-input "$src" -cc1-output "$o"
done
echo "[chibil-link] -> $OUT"
dotnet run -c Release --project "$ROOT/tools/chibil-link" -- -o "$OUT" "${OBJS[@]}"
rm -f "${OBJS[@]}"
echo "built $OUT"
```

- [ ] **Step 2: Run the first compile attempt; capture the first gap**

Run: `bash samples/sqlite/build-chibil.sh` (or the `.cmd` on Windows)
Expected: **FAIL** at the first chibil gap (a `#include` it can't find, an unknown type, a parse error, a codegen assert, or — later — a link-time unresolved symbol). **This is expected** — C1's deliverable is the script plus the first captured gap, not a passing build.

- [ ] **Step 3: Commit the build scripts**

```bash
git add samples/sqlite/build-chibil.sh samples/sqlite/build-chibil.cmd
git commit -m "feat(sqlite): chibil build/link script for SQLite (compile loop entry)"
```

### Task C2 (REPEATING): diagnose → reduce → fix → regress

> This task **repeats** until `build-chibil.sh` produces `app.dll`. Each iteration fixes exactly one gap and lands one regression test. There is no fixed count — the plan is the *procedure*, run until the build is clean.

For each iteration:

- [ ] **Step 1: Capture the first error** from `build-chibil.sh`. Classify it:
  - **(a) Missing header / unknown type** → add the minimal declaration to a header under `samples/sqlite/include/` (e.g. create `string.h`/`stdlib.h`/`limits.h`/`assert.h` with just the prototypes/types SQLite uses). No chibil change.
  - **(b) Missing libc function at link** (`chibil-link: unresolved symbol 'X'`) → add a minimal C implementation of `X` to `sqlite_shim.c`. No chibil change.
  - **(c) chibil parse/codegen gap** → go to Step 2.
  - **(d) Runtime mismatch** (`app.dll` builds but exit ≠ 55) → treat as a codegen bug; go to Step 2.

- [ ] **Step 2: Reduce to a minimal repro.** Create `scenarios/<gap-name>.c` — the smallest C that triggers the gap. (Worked example for a hypothetical "compound literal in initializer" gap:)

```c
/* scenarios/sqlite-gap-compound-literal.c — minimal repro */
struct P { int x, y; };
int main(void){ struct P a = (struct P){.x=20,.y=35}; return a.x + a.y; } /* expect 55 */
```

- [ ] **Step 3: Write a failing in-process regression** in a per-gap test (or extend `SqliteSmokeTests`'s gap-collection), compile the repro via `TestCompiler.CompileToObj(..., CoreClr)` → `LinkPipeline` → `Assembly.Load` → invoke, asserting the expected value. Run it; confirm it FAILS for the same reason SQLite does.

- [ ] **Step 4: Fix chibil** (smallest change in `chibil/*.cs`) until the repro test passes.

- [ ] **Step 5: Re-run `build-chibil.sh`.** It should advance past this gap to the next one (or succeed).

- [ ] **Step 6: Commit this single gap fix**

```bash
git add chibil/ scenarios/ tests/ samples/sqlite/include/ samples/sqlite/sqlite_shim.c
git commit -m "fix(codegen): <gap> surfaced by SQLite (+ scenarios regression)"
```

> **Discipline:** one gap per commit, each with a repro. Do NOT batch unrelated fixes. If a single gap has no clean MSIL mapping, either guard it with an `SQLITE_OMIT_*` in `sqlite_cfg.h` (document why) or escalate.

### Task C3: Final SP1 regression — SQLite `:memory:` runs, exit 55

Reached once `build-chibil.sh` produces `app.dll`.

**Files:**
- Create: `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs`

- [ ] **Step 1: Write the regression test** (compile the 3 sources in-process, link, run via dotnet host, assert 55)

```csharp
// tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs
using ChibilLink;
using Xunit;
using System.Collections.Generic;
using System.IO;

namespace Chibil.Tests.CoreClr;

public class SqliteSmokeTests
{
    static string SqliteDir() =>
        Path.Combine(/* repo root via AppContext.BaseDirectory walk-up */ FindRepoRoot(), "samples", "sqlite");

    [Fact]
    public void Memory_db_crud_returns_55()
    {
        Assert.True(DotnetHostRunner.DotnetAvailable());
        string dir = SqliteDir();
        string[] defs = {
            "-DSQLITE_OS_OTHER=1","-DSQLITE_THREADSAFE=0","-DSQLITE_TEMP_STORE=3",
            "-DSQLITE_ENABLE_MEMSYS5=1","-DSQLITE_ZERO_MALLOC=1",
            "-DSQLITE_OMIT_LOADEXTENSION=1","-DSQLITE_OMIT_AUTOINIT=1",
            "-I" + Path.Combine(dir,"include"), "-I" + Path.Combine(dir,"vendor") };

        var objs = new List<ObjectFile>();
        foreach (var src in new[]{ Path.Combine(dir,"vendor","sqlite3.c"),
                                   Path.Combine(dir,"sqlite_shim.c"),
                                   Path.Combine(dir,"main.c") })
            objs.Add(ObjectFile.Load(TestCompiler.CompileFileToObj(src, Chibil.TargetProfile.CoreClr, defs), src));

        byte[] pe = LinkPipeline.LinkToBytes(objs, new List<string>());
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string output);
        Assert.True(exit == 55, $"expected 55, got {exit}. {output}");
    }

    static string FindRepoRoot() { /* walk up from AppContext.BaseDirectory until samples/sqlite exists */ return RepoRootFinder.Find(); }
}
```

> You will need a `TestCompiler.CompileFileToObj(path, target, extraArgs)` overload (the existing helper takes a source string; add a file+args overload that runs the Driver `-cc1` pipeline with the `-D`/`-I` args). And a small `RepoRootFinder`/`DotnetHostRunner` reuse — `DotnetHostRunner` already exists from the linker work. Keep these helpers minimal.

- [ ] **Step 2: Run it; expect PASS (exit 55)**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Memory_db_crud_returns_55`
Expected: PASS.

- [ ] **Step 3: Validate cross-platform on Linux/WSL**

Run: `wsl -u root -- bash /mnt/d/sandbox/chibil/samples/sqlite/build-chibil.sh && wsl -u root -- bash -lc 'dotnet /mnt/d/sandbox/chibil/samples/sqlite/app.dll; echo EXIT=$?'`
Expected: `EXIT=55`. (WSL has the .NET 10 SDK from the prior project.)

- [ ] **Step 4: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs tests/Chibil.Tests/CoreClr/TestCompiler.cs
git commit -m "test(sqlite): :memory: CRUD runs on CoreCLR (Windows + WSL), exit 55"
```

---

## PART D — Wrap

### Task D1: Document SP1 result

**Files:**
- Create: `samples/sqlite/README.md`

- [ ] **Step 1: Write `README.md`** — pinned SQLite version + source URL; the config rationale; how to run `build-ref`/`build-chibil`; the proven result (exit 55 on Windows + WSL); the list of compiler gaps fixed (one line each, linking the `scenarios/` repro); and the explicit limitations carried to SP2/SP3 (no C# yet, C-side shim, `:memory:` only, varargs scope). Note the full self-contained property (no native `sqlite3` binary).

- [ ] **Step 2: Commit**

```bash
git add samples/sqlite/README.md
git commit -m "docs(sqlite): SP1 result, config rationale, and gap log"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** reference-build-first (§2) ↔ Tasks A4 + C1; config/shim/harness (§3) ↔ A2; pluggable-interface principle (§1) ↔ honored (the shim *is* a `sqlite3_vfs` + `SQLITE_CONFIG_HEAP`/`mem_methods` impl, swappable in SP3); compiler-gap loop (§4) ↔ Part C; varargs front-load (§4 risk #1) ↔ Part B; success criteria (§5) ↔ C3; cross-platform (§6) ↔ C3 Step 3; deferrals (§8) ↔ documented in D1.
- **Header set is a spec addition:** the spec said "mem/str functions"; in practice chibil ships *no* headers, so Task A3 + the gap loop's class-(a) branch add a minimal libc *header* set. This is necessary and folded into the loop.
- **Open-endedness is real:** Part C2 is a procedure, not an enumerated task list — by necessity (the gaps are unknowable in advance). Part B may split into its own spec; that is an expected, sanctioned outcome, not a failure.
- **Empirical points (will need iteration):** exact `sqlite3_vfs` struct shape for the pinned version (A2/A4 oracle); whether `SQLITE_TEMP_STORE=3` fully avoids file methods; the varargs lowering choice (B2). Each has a concrete oracle (ref.exe parity, the spike, the smoke test).
```
