# Managed SQLite via chibil

The full **SQLite amalgamation** (`sqlite3.c`, ~250k lines, v3.47.2) compiled to
**pure MSIL** by chibil's CoreCLR target, linked into a single managed assembly
by the in-house linker (`chibil-link`), running a real `:memory:` query on
CoreCLR — **with no native `sqlite3` binary**.

## Status

✅ **Linux / CoreCLR:** compiles, links, and runs. The `:memory:` harness
(`main.c`) does `CREATE TABLE` / `INSERT` / `SELECT sum(a)` and returns the sum
as its exit code (**55** = 20+22+13).

```sh
# requires the .NET 10 SDK on PATH
bash samples/sqlite/build-chibil.sh app.dll
dotnet app.dll ; echo "exit=$?"     # -> exit=55
```

✅ **Windows / CoreCLR:** compiles, links, and runs — the same `app.dll` returns
**55** on Windows too. Mutable C globals are emitted as runtime-initialized CLR
static fields (the module `.cctor` copies their init bytes from a read-only source
field, then applies pointer relocations), instead of `FieldRVA`-mapped data —
which Windows CoreCLR maps read-only regardless of PE section flags. String
literals stay read-only `FieldRVA`.

```sh
# requires the .NET 10 SDK on PATH
bash samples/sqlite/build-chibil.sh app.dll   # same app.dll as Linux
dotnet app.dll ; echo "exit=$?"     # -> exit=55
```

## Consuming from C# (SP2)

Linking with `--export-class=Sqlite3.Native` emits a `public static class
Sqlite3.Native` whose static methods forward (raw passthrough) to the
extern-linkage `sqlite3_*` functions. Signatures stay raw — `byte*`/`sbyte*`,
`sqlite3*`, `sqlite3_stmt*`, … — and the opaque handle structs are emitted as
public value types so external C# can name the pointer targets (forward-declared-
only handles like `sqlite3_stmt` get a synthesized empty public TypeDef).

A hand-written `unsafe` C# program, compiled against the generated `app.dll`,
runs the same `:memory:` CRUD and exits 55 on Windows and Linux/WSL — proven by
`tests/Chibil.Tests/CoreClr/SqliteExportTests.cs` (Roslyn-compiles an `unsafe`
consumer against the image, runs it via the dotnet host / WSL). IntPtr/string/
`out`-param ergonomics and an ADO.NET-style API are SP4; on-disk persistence is SP3.

## Pipeline

```
sqlite3.c ─┐ chibil --target=coreclr -cc1   → sqlite3.obj  (pure-MSIL, ~7 MB)
shim.c    ─┤                                  shim.obj
main.c    ─┘                                  main.obj
   ─► chibil-link -o app.dll  → app.dll (~9.8 MB) + app.runtimeconfig.json
        └─ merges metadata, resolves cross-object calls, synthesizes a
           module .cctor that initializes mutable globals (copying their init
           bytes into CLR static fields) and applies pointer relocations (the
           VFS / static method tables), emits a pure-MSIL PE
   ─► dotnet app.dll   (CoreCLR; no native sqlite3)
```

## Configuration (`sqlite_cfg.h` / build defines)

No-OS, single-threaded, self-contained: `SQLITE_OS_OTHER`, `SQLITE_THREADSAFE=0`,
`SQLITE_TEMP_STORE=3` (temp in RAM), `SQLITE_ENABLE_MEMSYS5` + `SQLITE_ZERO_MALLOC`
(built-in allocator over a static heap — no system `malloc`),
`SQLITE_OMIT_LOADEXTENSION`, `SQLITE_OMIT_AUTOINIT`.

## Files

| Path | Role |
|------|------|
| `vendor/sqlite3.c`, `sqlite3.h` | Vendored amalgamation (3.47.2); `fetch-amalgamation.{ps1,sh}` refresh it. |
| `include/` | Minimal chibil-parseable libc headers (`string.h`, `stdlib.h`, `stdio.h`, `math.h`, `ctype.h`, `assert.h`, …). |
| `sqlite_cfg.h` | Compile-time config. |
| `sqlite_shim.c` | C platform provider: imported mem/str funcs, the `sqlite3_vfs` (randomness/time; file methods stubbed — never hit with `:memory:`), `memsys5` heap config, `sqlite3_os_init`/`platform_init`. |
| `main.c` | `:memory:` CRUD harness (exit code = `SELECT sum(a)` = 55). |
| `build-chibil.sh` | Orchestrates compile + link → `app.dll`. |
| `build-ref.cmd` | Native MSVC reference build (`ref.exe`, exits 55) — the parity oracle. |

The end-to-end run is covered by `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs`
(`Memory_db_crud_returns_55_on_linux` is WSL-gated; `Memory_db_crud_returns_55_on_windows`
runs via the dotnet host — both compile + link + run and assert exit 55).

## What this exercised in chibil

Compiling and running SQLite drove out a series of real compiler/linker
capabilities (each with a regression test): function pointers in pure-MSIL
(`ldftn` + managed `calli`), variadic functions (the va-buffer ABI + native
cdecl P/Invoke), `const`-qualified forward-declared structs, anonymous
nested-struct compound assignment, VLA/`alloca`, long-form `ldloca`/`ldarga`
for ≥256 locals, and SQLite-scale multi-object metadata merging with FieldRVA
pointer relocations.

## Limitations

`:memory:` only (the VFS file methods are stubbed); single-threaded; SQLite's
variadic `printf` works on the Layer-2 cdecl path but `printf`-style **float**
varargs to native libc on Linux x64 carry the SysV-`AL` caveat (chibil's own
variadics are unaffected). The raw `Sqlite3.Native` export surface is in place
(SP2, above); the ergonomic C# API (IntPtr/string marshalling, `out` params,
`IDisposable` handles, ADO.NET-style) is SP4, and on-disk persistence (a real VFS
over `System.IO`) is SP3.
