# SQLite SP3b — Faithful Multi-Process Locking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the SP3a cross-OS disk VFS honor SQLite's byte-range lock protocol so two processes correctly serialize on one database file — a writer holding a lock makes a second writer get `SQLITE_BUSY`, then succeed after release — on Windows and Linux/WSL.

**Architecture:** Port SQLite's `os_unix.c`/`os_win.c` lock protocol into `sqlite_vfs_disk.c`: a `DiskFile.eLock` state machine (`NONE→SHARED→RESERVED→PENDING→EXCLUSIVE`) taking byte-range locks at SQLite's canonical lock bytes via `fcntl(F_SETLK)`+`struct flock` (Linux) / `LockFileEx`/`UnlockFileEx` (Windows), dispatched at runtime on the existing `g_win` flag. Proven by a two-process contention test (holder + contender) on both OSes; the Linux legs run on a native ext4 path (`/tmp`), not the `/mnt` drvfs mount (which may not honor `fcntl` locks). All changes are in `samples/sqlite/` + tests — the linker and chibil codegen are untouched.

**Tech Stack:** C (the VFS + harnesses); C# / xUnit; `DiskRunner`/`WslRunner` (extended for background processes); the SP3a `--pinvoke` routing + `OVERLAPPED` decl. WSL (Ubuntu + dotnet) is available on this machine.

**Reference docs:** Spec `docs/superpowers/specs/2026-06-01-sqlite-managed-sp3b-design.md`. Builds on SP3a (`sqlite_vfs_disk.c`, `chibil_os.h`, `main_disk.c`, `DiskRunner.cs`, `PositionedIoTests.PinvokeMap()`).

**Key existing code (verified):**
- `samples/sqlite/sqlite_vfs_disk.c` — `DiskFile { sqlite3_file base; long long h; }` (`:10`); `zero(p,n)` helper (`:12`); the no-op `dfLock`/`dfUnlock`/`dfCheckLock` (`:51-53`); `g_io` io-methods table (`:58-61`); `vOpen` sets `df->h`+`pMethods` (`:82`); `g_win` runtime flag (`:8`). The SP3a file I/O methods (dfRead/dfWrite/…) are unchanged by SP3b.
- `samples/sqlite/include/chibil_os.h` — has `OVERLAPPED`, `open`/`O_*`/`SEEK_SET`/`F_OK`, the kernel32 file decls, `__chibil_os_is_windows()`, `INVALID_HANDLE_VALUE`, `GENERIC_*`/`FILE_SHARE_*`/`OPEN_ALWAYS`/`FILE_ATTRIBUTE_NORMAL`.
- `tests/Chibil.Tests/CoreClr/PositionedIoTests.cs` — `internal static Dictionary<string,string> PinvokeMap()` (the shared 20-symbol map).
- `tests/Chibil.Tests/CoreClr/SqliteDiskTests.cs` — `BuildDiskAppDll()` compiles {sqlite3.c, sqlite_shim.c, sqlite_vfs_disk.c, main_disk.c} + links with `PinvokeMap()`; the Windows leg uses `DiskRunner.RunWindows(pe, out o, out size, "sp3.db")`, Linux uses `WslRunner.Run(pe, NetCoreRuntimeConfig)`.
- `tests/Chibil.Tests/CoreClr/DiskRunner.cs` — `RunWindows(byte[] pe, out string stdout, out long probeSize, string probeFile=null)` runs `dotnet app.dll` with `WorkingDirectory`=temp dir. `DotnetHostRunner.RuntimeConfigJson` is the runtimeconfig content. `WslRunner.Run`/`Available`, `SqliteSmokeTests.RepoRootDir()`.
- `SQLITE_LOCK_NONE`=0, `SHARED`=1, `RESERVED`=2, `PENDING`=3, `EXCLUSIVE`=4; `SQLITE_BUSY`, `SQLITE_OK` — from `sqlite3.h`.

---

## Task 1: Lock primitives — `chibil_os.h` decls + `fcntl`/`LockFileEx` round-trip

Adds the native lock decls (incl. the risky `struct flock` x64 ABI) and proves a byte-range lock round-trips on both OSes, single-process — isolating the ABI before the protocol.

**Files:** Modify `samples/sqlite/include/chibil_os.h`, `tests/Chibil.Tests/CoreClr/PositionedIoTests.cs`; Create `tests/Chibil.Tests/CoreClr/LockPrimitiveTests.cs`.

- [ ] **Step 1: Add the lock + sleep decls to `chibil_os.h`**

Insert after the existing POSIX block (after the `lseek` decl) and the Win32 block respectively:
```c
/* ---- POSIX file locking (libc fcntl) ---- */
#define F_RDLCK 0
#define F_WRLCK 1
#define F_UNLCK 2
#define F_SETLK 6
/* struct flock, Linux x86-64 layout (32 bytes): l_type@0, l_whence@2, [pad@4],
   l_start@8, l_len@16, l_pid@24, [pad@28]. The natural 4-byte pad after l_whence
   aligns the 8-byte l_start — declared in field order, the compiler inserts it. */
struct flock {
    short l_type;
    short l_whence;
    long long l_start;
    long long l_len;
    int l_pid;
};
extern int fcntl(int fd, int cmd, void *arg);   /* non-variadic: arg is always &flock here */
extern int usleep(unsigned int usec);           /* libc */

/* ---- Win32 file locking + sleep (kernel32) ---- */
#define LOCKFILE_FAIL_IMMEDIATELY 0x00000001u
#define LOCKFILE_EXCLUSIVE_LOCK   0x00000002u
extern int LockFileEx(void *h, unsigned int flags, unsigned int reserved,
                      unsigned int nLow, unsigned int nHigh, OVERLAPPED *ov);
extern int UnlockFileEx(void *h, unsigned int reserved,
                        unsigned int nLow, unsigned int nHigh, OVERLAPPED *ov);
extern void Sleep(unsigned int millis);          /* kernel32 */
```
> Place the `LockFileEx`/`UnlockFileEx`/`Sleep` decls inside/after the existing Win32 section (they need `OVERLAPPED`, already declared). `F_SETLK` is 6 on Linux x86-64.

- [ ] **Step 2: Add the new symbols to the shared `PinvokeMap()`** (`PositionedIoTests.cs`)

Append to the `Map` array in `PositionedIoTests`:
```csharp
        "fcntl=c","usleep=c",
        "LockFileEx=kernel32","UnlockFileEx=kernel32","Sleep=kernel32",
```
(So every disk build — `SqliteDiskTests`, and SP3b's — links these.)

- [ ] **Step 3: Write the failing primitive test** (`tests/Chibil.Tests/CoreClr/LockPrimitiveTests.cs`)

A standalone C program that opens a file, takes an exclusive byte-range lock, releases it, and re-takes it (proving release worked) → exit 55. OS-dispatched, single-process — proves the `struct flock` ABI + `fcntl`/`LockFileEx` plumbing.
```csharp
using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LockPrimitiveTests
{
    // Open "lk.tmp", exclusive-lock [1000,10), unlock, re-lock (must succeed again),
    // unlock. Returns 55 iff both locks succeeded. Proves the struct flock ABI + the
    // fcntl/LockFileEx P/Invokes on both OSes (single process).
    const string Src = @"
#include ""chibil_os.h""
static long w_open(const char* p){
    if (__chibil_os_is_windows())
        return (long long)(void*)CreateFileA(p, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    return open(p, O_RDWR|O_CREAT, 420);
}
static void zero(void* p,int n){ char* z=(char*)p; for(int i=0;i<n;i++) z[i]=0; }
static int lock_ex(long h, long long off, long long len){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        return LockFileEx((void*)h, LOCKFILE_FAIL_IMMEDIATELY|LOCKFILE_EXCLUSIVE_LOCK, 0, (unsigned int)len, (unsigned int)(len>>32), &ov) != 0; }
    struct flock fl; zero(&fl,sizeof fl); fl.l_type=(short)F_WRLCK; fl.l_whence=(short)SEEK_SET; fl.l_start=off; fl.l_len=len;
    return fcntl((int)h, F_SETLK, &fl) == 0;
}
static void unlock(long h, long long off, long long len){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        UnlockFileEx((void*)h, 0, (unsigned int)len, (unsigned int)(len>>32), &ov); return; }
    struct flock fl; zero(&fl,sizeof fl); fl.l_type=(short)F_UNLCK; fl.l_whence=(short)SEEK_SET; fl.l_start=off; fl.l_len=len;
    fcntl((int)h, F_SETLK, &fl);
}
int main(void){
    long h = w_open(""lk.tmp"");
    if (!lock_ex(h, 1000, 10)) return 1;
    unlock(h, 1000, 10);
    if (!lock_ex(h, 1000, 10)) return 2;   /* re-lock proves the first unlock worked */
    unlock(h, 1000, 10);
    return 55;
}";

    static byte[] Link()
    {
        string inc = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite", "include");
        string dir = Path.Combine(Path.GetTempPath(), "chibil_lk_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string c = Path.Combine(dir, "lk.c");
            File.WriteAllText(c, Src);
            byte[] obj = TestCompiler.CompileFileToObj(c, Chibil.TargetProfile.CoreClr, null, new[] { inc });
            return LinkPipeline.LinkToBytes(new[] { ObjectFile.Load(obj, "lk.obj") }, new List<string>(), null, PositionedIoTests.PinvokeMap());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Lock_primitive_roundtrip_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(Link(), out string o, out _);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Lock_primitive_roundtrip_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
```

- [ ] **Step 4: Run it — expect FAIL then PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter LockPrimitiveTests`
First it fails to compile/link until Steps 1-2 are in (the decls + map). With Steps 1-2 applied, expect both → 55. Triage: Linux exit 1 → `fcntl`/`struct flock` ABI wrong (the lock was rejected — check the 32-byte layout, `F_SETLK=6`, `l_type` values); Windows exit 1 → `LockFileEx` args/`OVERLAPPED` (check `LOCKFILE_*` flags, the len low/high split); exit 2 → unlock didn't release.

> NOTE on `WslRunner.Run` and `/mnt`: this primitive test runs in the `/mnt` staged dir. A *single-process* lock round-trip generally works on drvfs (it's contention/cross-process that drvfs may not honor). If Linux exit 1 happens ONLY here and you suspect drvfs, note it — Task 3 already moves the contention test to native ext4. But the single-process round-trip should pass on `/mnt`.

- [ ] **Step 5: Commit**

```bash
git add samples/sqlite/include/chibil_os.h tests/Chibil.Tests/CoreClr/PositionedIoTests.cs tests/Chibil.Tests/CoreClr/LockPrimitiveTests.cs
git commit -m "feat(sqlite): byte-range lock primitives (fcntl/struct flock + LockFileEx) round-trip both OSes"
```

---

## Task 2: The lock state machine in the VFS

Replaces the no-op lock methods with SQLite's faithful protocol. Gated by the existing single-process `SqliteDiskTests` staying at 55 (the write transaction now drives the full state machine — no self-deadlock).

**Files:** Modify `samples/sqlite/sqlite_vfs_disk.c`.

- [ ] **Step 1: Add `eLock` to `DiskFile` and initialize it**

Change the struct (`:10`):
```c
typedef struct DiskFile { sqlite3_file base; long long h; int eLock; } DiskFile;
```
In `vOpen`, after `df->h = h; df->base.pMethods = &g_io;` (`:82`), add:
```c
    df->eLock = SQLITE_LOCK_NONE;
```

- [ ] **Step 2: Add the lock-byte constants + `lock_byte`/`unlock_byte` helpers**

Insert above the lock methods (before `dfLock`, after `dfClose`):
```c
/* SQLite canonical lock bytes (must match os_unix.c / os_win.c for interop). */
#define PENDING_BYTE  0x40000000LL
#define RESERVED_BYTE (PENDING_BYTE + 1)
#define SHARED_FIRST  (PENDING_BYTE + 2)
#define SHARED_SIZE   510

/* Take a byte-range lock: 1 on success, 0 if contended/failed. Non-blocking. */
static int lock_byte(DiskFile *df, long long off, long long len, int exclusive){
    if (g_win){
        OVERLAPPED ov; zero(&ov, sizeof ov);
        ov.Offset = (unsigned int)off; ov.OffsetHigh = (unsigned int)(off>>32);
        unsigned int flags = LOCKFILE_FAIL_IMMEDIATELY | (exclusive ? LOCKFILE_EXCLUSIVE_LOCK : 0u);
        return LockFileEx((void*)df->h, flags, 0, (unsigned int)len, (unsigned int)(len>>32), &ov) != 0;
    }
    struct flock fl; zero(&fl, sizeof fl);
    fl.l_type = (short)(exclusive ? F_WRLCK : F_RDLCK);
    fl.l_whence = (short)SEEK_SET; fl.l_start = off; fl.l_len = len;
    return fcntl((int)df->h, F_SETLK, &fl) == 0;
}
/* Release a byte-range lock (harmless if not held). */
static void unlock_byte(DiskFile *df, long long off, long long len){
    if (g_win){
        OVERLAPPED ov; zero(&ov, sizeof ov);
        ov.Offset = (unsigned int)off; ov.OffsetHigh = (unsigned int)(off>>32);
        UnlockFileEx((void*)df->h, 0, (unsigned int)len, (unsigned int)(len>>32), &ov);
        return;
    }
    struct flock fl; zero(&fl, sizeof fl);
    fl.l_type = (short)F_UNLCK; fl.l_whence = (short)SEEK_SET; fl.l_start = off; fl.l_len = len;
    fcntl((int)df->h, F_SETLK, &fl);
}
```

- [ ] **Step 3: Replace the no-op lock methods with the real protocol**

Replace the three stub lines (`:51-53`) with:
```c
/* Faithful SQLite lock protocol (single connection per process), mirroring
   unixLock/unixUnlock/unixCheckReservedLock. Windows can't upgrade a held range
   lock in place, so the EXCLUSIVE/downgrade steps unlock-then-relock the shared
   range there; Linux fcntl converts in place. */
static int dfLock(sqlite3_file *f, int eTarget){
    DiskFile *df=(DiskFile*)f;
    if (df->eLock >= eTarget) return SQLITE_OK;

    /* PENDING gate: read-lock when acquiring SHARED, write-lock when jumping to EXCLUSIVE. */
    int gotPending = 0;
    if (eTarget == SQLITE_LOCK_SHARED
        || (eTarget == SQLITE_LOCK_EXCLUSIVE && df->eLock < SQLITE_LOCK_PENDING)){
        if (!lock_byte(df, PENDING_BYTE, 1, eTarget == SQLITE_LOCK_EXCLUSIVE)) return SQLITE_BUSY;
        gotPending = 1;
    }

    if (eTarget == SQLITE_LOCK_SHARED){
        int ok = lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0 /*read*/);
        if (gotPending) unlock_byte(df, PENDING_BYTE, 1);   /* PENDING was only a gate */
        if (!ok) return SQLITE_BUSY;
        df->eLock = SQLITE_LOCK_SHARED;
        return SQLITE_OK;
    }
    if (eTarget == SQLITE_LOCK_RESERVED){
        if (!lock_byte(df, RESERVED_BYTE, 1, 1 /*write*/)) return SQLITE_BUSY;
        df->eLock = SQLITE_LOCK_RESERVED;
        return SQLITE_OK;
    }
    /* EXCLUSIVE: hold the PENDING write lock (just taken, or we were already PENDING),
       then take the shared range exclusively. */
    if (eTarget == SQLITE_LOCK_EXCLUSIVE){
        if (g_win) unlock_byte(df, SHARED_FIRST, SHARED_SIZE);   /* Win: drop shared read-lock first */
        int ok = lock_byte(df, SHARED_FIRST, SHARED_SIZE, 1 /*write*/);
        if (!ok){
            if (g_win) lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0); /* restore shared read-lock */
            df->eLock = SQLITE_LOCK_PENDING;     /* we do hold PENDING */
            return SQLITE_BUSY;
        }
        df->eLock = SQLITE_LOCK_EXCLUSIVE;
        return SQLITE_OK;
    }
    return SQLITE_OK;
}
static int dfUnlock(sqlite3_file *f, int eTarget){
    DiskFile *df=(DiskFile*)f;
    if (df->eLock <= eTarget) return SQLITE_OK;
    if (eTarget == SQLITE_LOCK_SHARED){
        if (df->eLock == SQLITE_LOCK_EXCLUSIVE){
            /* downgrade the shared range from write back to read */
            if (g_win){ unlock_byte(df, SHARED_FIRST, SHARED_SIZE); lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0); }
            else lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0);     /* fcntl converts in place */
        }
        unlock_byte(df, PENDING_BYTE, 1);
        unlock_byte(df, RESERVED_BYTE, 1);
        df->eLock = SQLITE_LOCK_SHARED;
        return SQLITE_OK;
    }
    /* eTarget == NONE: release everything. */
    unlock_byte(df, SHARED_FIRST, SHARED_SIZE);
    unlock_byte(df, PENDING_BYTE, 1);
    unlock_byte(df, RESERVED_BYTE, 1);
    df->eLock = SQLITE_LOCK_NONE;
    return SQLITE_OK;
}
static int dfCheckLock(sqlite3_file *f, int *pOut){
    DiskFile *df=(DiskFile*)f;
    if (df->eLock >= SQLITE_LOCK_RESERVED){ *pOut = 1; return SQLITE_OK; }
    if (lock_byte(df, RESERVED_BYTE, 1, 1)){     /* trial write-lock */
        unlock_byte(df, RESERVED_BYTE, 1);
        *pOut = 0;
    } else {
        *pOut = 1;                                /* someone else holds RESERVED */
    }
    return SQLITE_OK;
}
```
> `g_io` (`:58`) already lists `dfLock, dfUnlock, dfCheckLock` — no table change. `SQLITE_LOCK_*` come from `sqlite3.h`.

- [ ] **Step 4: Run the single-process regression — expect 55 on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter SqliteDiskTests`
Expected: both legs still **55** (Windows also sp3.db>0). This now exercises the FULL lock state machine (the write transaction goes NONE→SHARED→RESERVED→PENDING→EXCLUSIVE→SHARED→NONE) within one process. If it deadlocks or returns non-55, the protocol self-conflicts — most likely the Windows EXCLUSIVE unlock-then-relock or the downgrade. Report the exact exit/output.

- [ ] **Step 5: Confirm `:memory:` + lock-primitive tests still green**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "SqliteSmokeTests|LockPrimitiveTests"`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add samples/sqlite/sqlite_vfs_disk.c
git commit -m "feat(sqlite): faithful SQLite lock state machine in the cross-OS disk VFS"
```

---

## Task 3: Two-process contention (the headline)

Two harnesses + an orchestration helper prove real cross-process locking on both OSes (Linux on native ext4).

**Files:** Create `samples/sqlite/main_lock_holder.c`, `samples/sqlite/main_lock_contender.c`, `tests/Chibil.Tests/CoreClr/LockContentionRunner.cs`, `tests/Chibil.Tests/CoreClr/SqliteLockTests.cs`.

- [ ] **Step 1: Create the holder harness** (`samples/sqlite/main_lock_holder.c`)

```c
/* main_lock_holder.c — acquires a write lock (BEGIN IMMEDIATE), signals via
   held.marker, waits for release.marker, then commits. */
#include "sqlite3.h"
#include "chibil_os.h"
void platform_init(void);
void register_disk_vfs(void);

static void touch(const char *p){
    if (__chibil_os_is_windows()){
        void *h = CreateFileA(p, GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
        if ((void*)h != INVALID_HANDLE_VALUE) CloseHandle(h);
    } else { int fd = open(p, O_RDWR|O_CREAT, 420); if (fd >= 0) close(fd); }
}
static int exists(const char *p){
    if (__chibil_os_is_windows()) return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
    return access(p, F_OK) == 0;
}
static void sleep_ms(unsigned int ms){ if (__chibil_os_is_windows()) Sleep(ms); else usleep(ms*1000u); }

int main(void){
    platform_init(); register_disk_vfs();
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    if (sqlite3_exec(db, "CREATE TABLE IF NOT EXISTS t(a INTEGER);", 0,0,0) != SQLITE_OK) return 102;
    if (sqlite3_exec(db, "BEGIN IMMEDIATE; INSERT INTO t VALUES(1);", 0,0,0) != SQLITE_OK) return 103; /* holds RESERVED */
    touch("held.marker");
    for (int i = 0; i < 3000 && !exists("release.marker"); i++) sleep_ms(10);   /* up to ~30s */
    sqlite3_exec(db, "COMMIT;", 0,0,0);
    sqlite3_close(db);
    return 0;
}
```

- [ ] **Step 2: Create the contender harness** (`samples/sqlite/main_lock_contender.c`)

```c
/* main_lock_contender.c — tries to write; 55 if blocked (SQLITE_BUSY), 0 if it
   acquires (and commits), 44 otherwise. busy_timeout=0 → fail immediately. */
#include "sqlite3.h"
void platform_init(void);
void register_disk_vfs(void);

int main(void){
    platform_init(); register_disk_vfs();
    sqlite3 *db = 0;
    if (sqlite3_open("sp3.db", &db) != SQLITE_OK) return 101;
    sqlite3_busy_timeout(db, 0);
    int rc = sqlite3_exec(db, "BEGIN IMMEDIATE; INSERT INTO t VALUES(2);", 0,0,0);
    if (rc == SQLITE_BUSY){ sqlite3_close(db); return 55; }                 /* blocked by holder */
    if (rc == SQLITE_OK){ sqlite3_exec(db, "COMMIT;", 0,0,0); sqlite3_close(db); return 0; } /* acquired */
    sqlite3_close(db); return 44;                                          /* unexpected */
}
```

- [ ] **Step 3: Create the orchestration helper** (`tests/Chibil.Tests/CoreClr/LockContentionRunner.cs`)

Runs the holder in the background, polls `held.marker`, runs the contender (contention leg), releases, joins the holder, runs the contender again (recovery leg). Returns `(contended, recovered)` — expected `(55, 0)`. Windows uses a temp dir; Linux uses a **native ext4 path** (`/tmp`) staged via `wsl`.
```csharp
using System;
using System.Diagnostics;
using System.IO;

namespace Chibil.Tests.CoreClr;

internal static class LockContentionRunner
{
    static void StageWin(string dir, string name, byte[] pe)
    {
        File.WriteAllBytes(Path.Combine(dir, name), pe);
        File.WriteAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".runtimeconfig.json"),
            DotnetHostRunner.RuntimeConfigJson);
    }
    static int RunWin(string dir, string dll)
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("contender timed out"); }
        return p.ExitCode;
    }

    public static (int contended, int recovered) RunWindows(byte[] holder, byte[] contender)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_lock_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Process bg = null;
        try
        {
            StageWin(dir, "holder.dll", holder);
            StageWin(dir, "contender.dll", contender);
            bg = Process.Start(new ProcessStartInfo("dotnet", "\"holder.dll\"")
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string held = Path.Combine(dir, "held.marker");
            if (!WaitFile(() => File.Exists(held), 30000)) throw new Exception("holder never acquired the lock");
            int contended = RunWin(dir, "contender.dll");
            File.WriteAllText(Path.Combine(dir, "release.marker"), "");
            bg.WaitForExit(30000);
            int recovered = RunWin(dir, "contender.dll");
            return (contended, recovered);
        }
        finally { try { if (bg != null && !bg.HasExited) bg.Kill(true); } catch { } try { Directory.Delete(dir, true); } catch { } }
    }

    public static (int contended, int recovered) RunLinux(byte[] holder, byte[] contender)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string ltmp = "/tmp/chibil_lock_" + id;
        // Stage into a Windows temp dir, then copy into native ext4 /tmp via wsl.
        string win = Path.Combine(Path.GetTempPath(), "chibil_lockstage_" + id);
        Directory.CreateDirectory(win);
        StageWin(win, "holder.dll", holder);
        StageWin(win, "contender.dll", contender);
        string winWsl = "/mnt/" + char.ToLower(win[0]) + win[2..].Replace('\\', '/');
        Process bg = null;
        try
        {
            Wsl($"mkdir -p {ltmp} && cp '{winWsl}'/* {ltmp}/");
            bg = StartWslBg($"cd {ltmp} && dotnet holder.dll");
            if (!WaitFile(() => WslTest($"test -f {ltmp}/held.marker"), 30000)) throw new Exception("holder never acquired the lock (linux)");
            int contended = WslExit($"cd {ltmp} && dotnet contender.dll");
            Wsl($"touch {ltmp}/release.marker");
            bg.WaitForExit(30000);
            int recovered = WslExit($"cd {ltmp} && dotnet contender.dll");
            return (contended, recovered);
        }
        finally
        {
            try { if (bg != null && !bg.HasExited) bg.Kill(true); } catch { }
            try { Wsl($"rm -rf {ltmp}"); } catch { }
            try { Directory.Delete(win, true); } catch { }
        }
    }

    static bool WaitFile(Func<bool> ready, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 100)
        { if (ready()) return true; System.Threading.Thread.Sleep(100); }
        return ready();
    }

    // --- WSL helpers ---
    static Process WslStart(string bashCmd, bool background)
    {
        var psi = new ProcessStartInfo("wsl", $"-u root -- bash -lc \"{bashCmd}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var p = Process.Start(psi);
        if (!background) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
        return p;
    }
    static void Wsl(string cmd) { var p = WslStart(cmd, false); p.WaitForExit(30000); }
    static bool WslTest(string cmd) { var p = WslStart(cmd, false); p.WaitForExit(15000); return p.ExitCode == 0; }
    static int WslExit(string cmd) { var p = WslStart(cmd, false); if (!p.WaitForExit(30000)) { p.Kill(true); } return p.ExitCode; }
    static Process StartWslBg(string cmd) => WslStart(cmd, true);
}
```
> The holder's `held.marker`/`release.marker` live in the run dir (cwd); the holder creates/polls them via its own `chibil_os.h` calls, and the orchestrator polls/creates them on its side (Windows: `File.Exists`; Linux: `wsl test -f` / `touch`). `dotnet holder.dll` resolves the chibil-emitted assembly name (`a.dll`?) — NO: each linked PE's assembly name is `a`, but the FILE is named `holder.dll`/`contender.dll`; the dotnet host loads the file by path, so the on-disk name is what matters. (SP2's consumer test already runs a renamed file this way.)

- [ ] **Step 4: Write the contention test** (`tests/Chibil.Tests/CoreClr/SqliteLockTests.cs`)

```csharp
using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteLockTests
{
    // Build a disk app from {sqlite3.c, sqlite_shim.c, sqlite_vfs_disk.c, <harness>}.
    static byte[] Build(string harness)
    {
        string sq = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite");
        string[] defs = { "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1", "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1" };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };
        var objs = new List<ObjectFile>();
        foreach (var src in new[] { "vendor/sqlite3.c", "sqlite_shim.c", "sqlite_vfs_disk.c", harness })
        {
            byte[] obj = TestCompiler.CompileFileToObj(Path.Combine(sq, src.Replace('/', Path.DirectorySeparatorChar)),
                Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }
        return LinkPipeline.LinkToBytes(objs, new List<string>(), null, PositionedIoTests.PinvokeMap());
    }

    [Fact]
    public void Two_process_contention_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        var (contended, recovered) = LockContentionRunner.RunWindows(Build("main_lock_holder.c"), Build("main_lock_contender.c"));
        Assert.True(contended == 55, $"windows contender expected 55 (BUSY while held), got {contended}");
        Assert.True(recovered == 0, $"windows contender expected 0 (acquired after release), got {recovered}");
    }

    [Fact]
    public void Two_process_contention_linux()
    {
        if (!WslRunner.Available()) return;
        var (contended, recovered) = LockContentionRunner.RunLinux(Build("main_lock_holder.c"), Build("main_lock_contender.c"));
        Assert.True(contended == 55, $"linux contender expected 55 (BUSY while held), got {contended}");
        Assert.True(recovered == 0, $"linux contender expected 0 (acquired after release), got {recovered}");
    }
}
```

- [ ] **Step 5: Run the contention test — expect (55, 0) on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter SqliteLockTests`
Expected: both → contended 55, recovered 0. Triage (report exact values + any host output, do NOT mask):
- contended == 0 (not 55): the lock did NOT block the contender → the holder's RESERVED lock isn't being seen cross-process. On Linux, if it's 0 only on `/mnt`, that's the drvfs-doesn't-lock issue — confirm the test is running in `/tmp` (it should via `RunLinux`). On Windows, check `LockFileEx` flags.
- contended == 101/102/103: holder/contender setup failed (open/create/begin) — inspect.
- "holder never acquired the lock": the holder didn't create `held.marker` — its `BEGIN IMMEDIATE` may have failed (103) or the marker path/cwd is wrong (the holder writes a relative `held.marker` in its cwd = the run dir; the orchestrator polls the same dir).
- recovered != 0: locks weren't released on holder exit/commit — check `dfUnlock` to NONE on `sqlite3_close`.
- Linux flakiness: increase the marker timeout; ensure `wsl cp` staged the DLLs (a missing-file error means the copy/glob failed).

- [ ] **Step 6: Commit**

```bash
git add samples/sqlite/main_lock_holder.c samples/sqlite/main_lock_contender.c tests/Chibil.Tests/CoreClr/LockContentionRunner.cs tests/Chibil.Tests/CoreClr/SqliteLockTests.cs
git commit -m "test(sqlite): two-process lock contention (SQLITE_BUSY + recovery) on Windows + Linux"
```

---

## Task 4: Build script + regression gate + docs

**Files:** Modify `samples/sqlite/build-chibil-disk.sh`, `samples/sqlite/README.md`.

- [ ] **Step 1: Extend the disk build script's `--pinvoke` map**

In `samples/sqlite/build-chibil-disk.sh`, the `PINVOKE=` string drives the disk VFS link. The VFS now references `fcntl`/`LockFileEx`/`UnlockFileEx` (and the harnesses use `usleep`/`Sleep`). Append to the `PINVOKE` value:
```
,fcntl=c,usleep=c,LockFileEx=kernel32,UnlockFileEx=kernel32,Sleep=kernel32
```
(So `build-chibil-disk.sh app-disk.dll` — which builds `main_disk.c` — still links, since the VFS now references `fcntl`/`LockFileEx`. The map is a superset; unused entries are harmless.)

- [ ] **Step 2: Full MSVC regression suite**

Run: `run-tests.cmd x64`
Expected: 0 failures (chibil codegen/`.obj` + the linker are untouched → IJW unaffected; the total grows by the SP3b CoreCLR tests). Report the exact final `Passed!/Failed:` line.

- [ ] **Step 3: Full CoreCLR suite incl. all SP3b legs**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: 0 failures; `LockPrimitiveTests`, `SqliteLockTests` (both OSes), and the unchanged `SqliteDiskTests`/`SqliteSmokeTests` all green.

- [ ] **Step 4: Update `samples/sqlite/README.md`**

In the "On-disk persistence (SP3a)" section, change the trailing "Single connection, no locking yet; … is SP3b." to reflect SP3b done, and add a short note:
```markdown
Multi-process locking (SP3b) is implemented: the VFS takes SQLite's byte-range
locks at the canonical lock bytes via `fcntl(F_SETLK)` (Linux) / `LockFileEx`
(Windows), so a second writer gets `SQLITE_BUSY` while one is held and succeeds
after release — proven by a two-process contention test on Windows and Linux/WSL
(`tests/Chibil.Tests/CoreClr/SqliteLockTests.cs`; the Linux legs run on a native
ext4 path, since the WSL `/mnt` drvfs mount may not honor `fcntl` locks). WAL mode
is out of scope.
```

- [ ] **Step 5: Commit**

```bash
git add samples/sqlite/build-chibil-disk.sh samples/sqlite/README.md
git commit -m "docs(sqlite): multi-process locking (SP3b) + build-script pinvoke map"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** §3 state machine ↔ Task 2 Step 3; §4 primitives + decls ↔ Task 1 + Task 2 Step 2; §5 two-process proof + harnesses ↔ Task 3; §5 native-ext4 Linux staging ↔ `LockContentionRunner.RunLinux`; §5 single-process regression ↔ Task 2 Step 4; §6 components ↔ all; §7 testing ↔ Tasks 1-3; non-regression ↔ Task 4.
- **De-risking order:** Task 1 isolates the `struct flock` ABI + lock P/Invokes (single-process round-trip) before the protocol; Task 2 gates the protocol on the single-process CRUD staying green (no self-deadlock); Task 3 is the cross-process headline; the `/mnt`-drvfs risk is contained to `RunLinux` (native `/tmp`).
- **The Windows upgrade divergence** (Task 2 Step 3): Windows can't convert a held range lock, so EXCLUSIVE/downgrade unlock-then-relock the shared range there; Linux `fcntl` converts in place. This is the subtlest correctness point — the single-process `SqliteDiskTests` (which drives SHARED→EXCLUSIVE→SHARED) is its oracle.
- **Type/name consistency:** `lock_byte(df, off, len, exclusive)` / `unlock_byte(df, off, len)` and the lock-byte macros are defined in Task 2 Step 2 and used in Step 3; `DiskFile.eLock` added in Step 1; `PositionedIoTests.PinvokeMap()` gains the 5 lock/sleep symbols in Task 1 Step 2 (used by every disk/lock build); `LockContentionRunner.RunWindows/RunLinux` defined in Task 3 Step 3, used in Step 4; harness filenames `main_lock_holder.c`/`main_lock_contender.c` consistent across Task 3.
- **Untouched:** the linker, chibil codegen, the `.obj` format, `sqlite_shim.c`, `main.c`, `main_disk.c`, and the SP3a file-I/O methods — so `:memory:`, SP2, SP3a single-process, and the MSVC/IJW suite are the regression gates (Task 4).
