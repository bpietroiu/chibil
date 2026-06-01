# Managed SQLite via chibil — cross-OS disk persistence — design (Sub-Project 3a)

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** SP3a only — give chibil-compiled SQLite a **real on-disk database file** on Windows *and* Linux/WSL, backed by native file I/O (libc on Linux, kernel32 on Windows) through chibil's P/Invoke. Single connection, **no locking yet** (the SQLite lock protocol is SP3b). `:memory:` keeps working unchanged.

## 1. Where this sits

```
┌ Ergonomic C# API (ADO.NET-ish)                         ┐  SP4
├ Sqlite3.Native raw export surface                      ┤  SP2 (done)
├ Platform: cross-OS disk VFS                            ┤  SP3a (THIS)  →  + lock protocol  SP3b
└ sqlite3 core compiled to MSIL                          ┘  SP1 (done)
```

SP1 compiles `sqlite3.c` to MSIL and runs `:memory:` CRUD → 55. SP2 exposes `Sqlite3.Native` to C#. SP3a makes SQLite read/write a **real file** cross-platform; SP3b adds faithful multi-process locking on top.

**Decomposition rationale (locked):** real cross-OS locking (the eventual goal) needs the file-I/O infrastructure first — per-OS P/Invoke routing, runtime OS dispatch, native struct ABI, positioned I/O. SP3a builds exactly that and is independently shippable (a real on-disk db, single connection). SP3b layers the byte-range lock protocol on it.

## 2. Decisions (locked during brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Backend | **Native libc/Win32 via chibil P/Invoke** (not managed System.IO) | Real OS files; reuses the proven self-contained-Linux libc-P/Invoke machinery; faithful POSIX/Win32 semantics for SP3b's locking. |
| OS coverage | **Both** Windows + Linux/WSL from one `app.dll` | Consistent with the Windows-parity theme; the runtime target is cross-platform. |
| VFS strategy | **One custom `sqlite3_vfs` (keep `SQLITE_OS_OTHER=1`) that dispatches to a Linux/Windows backend at runtime** | SQLite's own `os_unix.c`/`os_win.c` are selected by the preprocessor at compile time and cannot both live in one cross-OS binary. |
| Concurrency (SP3a) | **Single connection, no locking** (`xLock`/`xUnlock`/`xCheckReservedLock` no-op) | The lock protocol is the hard cross-OS piece → deferred to SP3b. Persistence (write→close→reopen→read) needs no locking. |
| Path input | **Fixed relative filename** (`sp3.db`) run in a fresh dir | chibil's entry supports only `int main(void)` (no argv); avoids env/argv. The test inspects the file in the run dir. |
| Positioned I/O | **Atomic positioned reads/writes** (`pread`/`pwrite` on Linux; `ReadFile`/`WriteFile` + `OVERLAPPED` offset on Windows) | Forward-compatible with SP3b; correct regardless of a shared file pointer. |

## 3. The cross-OS disk VFS

`SQLITE_OS_OTHER=1` stays. A new VFS replaces the stubbed file methods with real I/O, each method branching on a runtime OS flag.

### 3.1 Runtime OS dispatch
A single boolean, read once at VFS registration from a synthesized intrinsic:
```c
extern int __chibil_os_is_windows(void);   /* 1 on Windows, 0 elsewhere */
static int g_is_win;
```
The **linker synthesizes** `__chibil_os_is_windows` (see §4.2) — it is NOT a P/Invoke. The VFS calls it once in `register_disk_vfs()`.

### 3.2 `sqlite3_io_methods` (per open file)
A `sqlite3_file` subclass carries the OS handle:
```c
typedef struct DiskFile { sqlite3_file base; long long h; } DiskFile;  /* fd (Linux) or HANDLE (Windows) */
```
Methods (iVersion 1 is sufficient):
- `xRead(file, buf, n, off)` → Linux `pread(fd, buf, n, off)`; Windows `ReadFile` with `OVERLAPPED{Offset=off}`. Short read → zero-fill the tail and return `SQLITE_IOERR_SHORT_READ` (SQLite contract).
- `xWrite(file, buf, n, off)` → Linux `pwrite`; Windows `WriteFile` + `OVERLAPPED`.
- `xTruncate(file, size)` → Linux `ftruncate`; Windows `SetFilePointerEx`+`SetEndOfFile`.
- `xSync(file, flags)` → Linux `fsync`; Windows `FlushFileBuffers`.
- `xFileSize(file, *pSize)` → Linux `lseek(fd,0,SEEK_END)` (avoids `struct stat`); Windows `GetFileSizeEx`.
- `xClose(file)` → Linux `close`; Windows `CloseHandle`.
- `xLock/xUnlock(file, level)` → **no-op**, return `SQLITE_OK`. `xCheckReservedLock(file, *pOut)` → `*pOut=0; SQLITE_OK`.
- `xFileControl` → `SQLITE_NOTFOUND`. `xSectorSize` → 512. `xDeviceCharacteristics` → 0.

### 3.3 `sqlite3_vfs`
- `xOpen(vfs, zName, file, flags, *pOut)` — translate SQLite open flags to the backend:
  - Linux `open(zName, oflags, 0644)` where `oflags` = `O_RDWR|O_CREAT` for main-db/`READWRITE|CREATE`, `O_RDONLY` for readonly; returns fd.
  - Windows `CreateFileA(zName, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL)`.
  - Store the handle in `DiskFile`, set `pFile->pMethods`, set `*pOut` (output open flags). On failure return `SQLITE_CANTOPEN`.
- `xDelete(vfs, zName, syncDir)` → Linux `unlink`; Windows `DeleteFileA`.
- `xAccess(vfs, zName, flags, *pResOut)` → Linux `access(zName, F_OK)`==0; Windows `GetFileAttributesA(zName) != INVALID_FILE_ATTRIBUTES`.
- `xFullPathname(vfs, zName, n, zOut)` → copy through (the harness uses a simple relative name; absolutization is unnecessary for SP3a and avoids `realpath`/`GetFullPathName` ABI work). Document the simplification.
- `xRandomness/xSleep/xCurrentTime/xGetLastError` → the disk VFS carries its **own** small copies (seeded RNG, fixed Julian day, no-op sleep). It can't share `sqlite_shim.c`'s versions — those are `static` there, and the shim stays untouched — so a few lines are duplicated by design.
- `register_disk_vfs()` sets `g_is_win = __chibil_os_is_windows()`, fills the `sqlite3_vfs` struct, and `sqlite3_vfs_register(&g_disk_vfs, /*makeDflt=*/1)`.

> The function-pointer-table struct initializers (`sqlite3_io_methods`, `sqlite3_vfs`) exercise chibil's FieldRVA pointer relocations — already proven at SQLite scale.

## 4. The three new chibil/linker capabilities

### 4.1 Per-OS P/Invoke library routing (central new feature)
One `app.dll` calls **both** libc (`open`, `pread`, …) and kernel32 (`CreateFileA`, `ReadFile`, …). The linker must emit each P/Invoke against the correct native module. Today `SymbolResolver` routes all synthesized P/Invokes to a single default library.

**Mechanism:** a build-time **name→library map** passed to the linker — e.g. `--pinvoke open=c,pread=c,…,CreateFileA=kernel32,ReadFile=kernel32,…` (or a small mapping file). `SymbolResolver` consults it when synthesizing each P/Invoke stub, choosing the ModuleRef per symbol; unmapped externals fall back to the existing default. The C stays clean (plain `extern` decls). Linker-only — chibil codegen and the `.obj` are untouched (consistent with SP2).

**Lazy resolution is load-bearing:** .NET resolves a `DllImport` on first call. On Linux the `kernel32` stubs are never called (the VFS dispatches to the libc branch), so they never attempt to load `kernel32.dll`; symmetrically on Windows for `libc`. This is what lets both stub sets coexist in one binary.

> `--pinvoke` library tokens map to the platform DLL the existing P/Invoke machinery already uses (`c` → `libc.so.6` on Linux; `kernel32` → `kernel32.dll`). Confirm the existing default-library naming and reuse it.

### 4.2 The `__chibil_os_is_windows` intrinsic
When `SymbolResolver` finds an unresolved external named `__chibil_os_is_windows`, it synthesizes a `MethodDef` (a `SynthMethod`, like the `.cctor`/entry/forwarders) instead of a P/Invoke stub. Its IL:
```
call bool [mscorlib]System.OperatingSystem::IsWindows()
ret                     ; bool is returned as i4 (1/0) — matches `int` C ABI
```
Signature: `int32 __chibil_os_is_windows()`, default calling convention. The C's `extern int __chibil_os_is_windows(void)` call binds to it via the normal cross-reference resolution. (`System.OperatingSystem` is in the core lib; add the TypeRef/MemberRef via the merger's existing helpers.)

### 4.3 Native struct ABI for `OVERLAPPED`
Windows positioned I/O needs `OVERLAPPED` (x64 layout: `ULONG_PTR Internal; ULONG_PTR InternalHigh; union { struct { DWORD Offset; DWORD OffsetHigh; }; PVOID Pointer; }; HANDLE hEvent;` = 32 bytes). Declared in a chibil-parseable header; the VFS sets `Offset`/`OffsetHigh` from the 64-bit file offset. chibil already compiles structs with explicit layout at SQLite scale; the oracle is the positioned-I/O round-trip test.

## 5. Components

| File | Change |
|---|---|
| `samples/sqlite/sqlite_vfs_disk.c` (new) | The entire cross-OS disk VFS: `DiskFile`, `sqlite3_io_methods`, `sqlite3_vfs`, libc + kernel32 `extern` decls, OS dispatch, `register_disk_vfs()`. |
| `samples/sqlite/main_disk.c` (new) | Harness: `platform_init(); register_disk_vfs(); open "sp3.db" → CREATE/INSERT → close → reopen → SELECT sum(a) → return 55`. |
| `samples/sqlite/include/` (additions) | Minimal chibil-parseable decls/constants: `open`/`O_RDWR`/`O_CREAT`/`O_RDONLY`/`SEEK_END`/`F_OK`; Win32 `CreateFileA`/`ReadFile`/`WriteFile`/`SetEndOfFile`/`SetFilePointerEx`/`FlushFileBuffers`/`CloseHandle`/`DeleteFileA`/`GetFileSizeEx`/`GetFileAttributesA`, `OVERLAPPED`, and the `GENERIC_*`/`FILE_SHARE_*`/`OPEN_ALWAYS`/`FILE_ATTRIBUTE_NORMAL`/`INVALID_*` constants. |
| `samples/sqlite/sqlite_shim.c` | **Untouched** — the disk VFS is additive and self-registers; `:memory:` is unaffected. |
| `tools/chibil-link/` (`Program.cs`/`LinkOptions`, `SymbolResolver.cs`) | `--pinvoke name=lib` map → per-symbol ModuleRef routing; the `__chibil_os_is_windows` synthesized intrinsic. |
| `tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs` (new) | The capstone + focused infra tests. |
| Runner helper(s) | Expose the run dir (or the produced file) before temp-dir cleanup so the test can assert `sp3.db` exists on disk. |

## 6. Testing

- **Capstone (headline):** compile+link {`sqlite3.c`, `sqlite_shim.c`, `sqlite_vfs_disk.c`, `main_disk.c`} with the `--pinvoke` map; run via `DotnetHostRunner` (Windows) and `WslRunner` (Linux); assert exit **55** AND that `sp3.db` exists with size > 0 in the run dir (proves on-disk, not memory). Both OSes (WSL available here).
- **Focused infra tests (fast):**
  - `__chibil_os_is_windows()` returns 1 on Windows, 0 on Linux (tiny program: `int main(void){ return __chibil_os_is_windows() ? 55 : 44; }` run on each OS).
  - Per-OS P/Invoke routing: a tiny program that calls one libc function on Linux / one kernel32 function on Windows (guarded by the OS flag) and returns a known value — proves routing + lazy resolution (the wrong-OS stub is present but never loaded).
  - Positioned-I/O round-trip: write bytes at an offset, read them back at that offset, compare — the `OVERLAPPED`/`pwrite` oracle.
- **Non-regression:** `:memory:` `SqliteSmokeTests` + SP2 `SqliteExportTests`/`ExportClassTests` unchanged (shim untouched, export path untouched); full MSVC suite green (codegen/`.obj` unchanged → IJW untouched); linking without `--pinvoke` unchanged.

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Per-OS P/Invoke routing / lazy resolution (wrong-OS stub must not load) | Medium | Focused routing test; rely on .NET's first-call `DllImport` resolution — uncalled stubs never bind |
| `OVERLAPPED`/Win32 decls compiled + called correctly by chibil | Medium | Positioned-I/O round-trip test is the oracle; struct layout pinned to the documented x64 ABI |
| WSL `/mnt` (drvfs) `fsync`/IO quirks | Low | SP3a has no locking; basic read/write/fsync on `/mnt` works; revisit for SP3b |
| Short-read / open-flag translation bugs | Medium | Reopen-and-read-back is end-to-end; positioned-I/O test covers the read path |
| Linker intrinsic (`__chibil_os_is_windows`) row prediction / synthesis | Low | Reuses the proven `SynthMethod` reservation + `AssertRow` discipline (same as SP2 forwarders) |

## 8. Out of scope
- **SP3b:** the SQLite lock protocol (`fcntl` `F_SETLK` + `struct flock` on Linux, `LockFileEx`/`UnlockFileEx` on Windows), `xCheckReservedLock`, multi-process concurrency, WAL.
- **SP4:** the ergonomic C# API.
- Absolute-path resolution (`realpath`/`GetFullPathName`), temp-file VFS spill (kept in memory via `SQLITE_TEMP_STORE`), directory sync, `getenv`/argv-driven paths.

## 9. Success criteria
1. The linker, given a `--pinvoke` map, routes each native symbol to its module; calling-the-wrong-OS stubs are never loaded.
2. `__chibil_os_is_windows()` is synthesized and returns the correct value on each OS.
3. One `app.dll`: a chibil-compiled SQLite opens `sp3.db`, runs CREATE/INSERT, closes, **reopens**, and `SELECT sum(a)` returns **55** on Windows **and** Linux/WSL, with a real file on disk.
4. `:memory:`, SP2, and the MSVC/IJW suites stay green.
