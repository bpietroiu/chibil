# Managed SQLite via chibil — faithful multi-process locking — design (Sub-Project 3b)

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** SP3b only — add SQLite's faithful byte-range **lock protocol** to the cross-OS disk VFS built in SP3a, so two processes correctly serialize access to one on-disk database (a writer holding a lock makes a second writer get `SQLITE_BUSY`), on Windows *and* Linux/WSL. Rollback-journal mode only; WAL is out of scope.

## 1. Where this sits

```
┌ Ergonomic C# API (ADO.NET-ish)                  ┐  SP4
├ Sqlite3.Native raw export surface               ┤  SP2 (done)
├ Cross-OS disk VFS — file I/O (SP3a, done)       │
│   + faithful lock protocol  ← SP3b (THIS)       ┤  SP3
└ sqlite3 core compiled to MSIL                   ┘  SP1 (done)
```

SP3a gave the disk VFS real cross-OS file I/O with **no-op** `xLock`/`xUnlock`/`xCheckReservedLock` (single connection). SP3b makes those methods real, porting SQLite's `os_unix.c`/`os_win.c` lock protocol into the runtime-dispatched VFS so multiple processes safely share a database file. SP3a was explicitly built as this foundation: the `DiskFile` handle, the `g_win` dispatch, and `OVERLAPPED` all extend directly.

## 2. Decisions (locked during brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| Fidelity | **Faithful SQLite protocol** — full `NONE→SHARED→RESERVED→PENDING→EXCLUSIVE` state machine at SQLite's canonical lock bytes | Gives multiple concurrent readers OR one writer, and is **interoperable** with stock sqlite3 on the same file. Using SQLite's exact lock-byte offsets costs nothing extra over a custom scheme. |
| Proof | **Two-process contention** test (holder + contender, `SQLITE_BUSY` then recovery) | POSIX `fcntl` locks are per-process — two fds in one process don't contend on Linux — so only two real processes prove cross-process locking. |
| Backend | `fcntl(F_SETLK)` + `struct flock` (Linux), `LockFileEx`/`UnlockFileEx` + `OVERLAPPED` (Windows), dispatched at runtime via `g_win` | Reuses SP3a's per-OS P/Invoke routing (`--pinvoke`) and the `OVERLAPPED` decl. |
| Journal mode | **Rollback journal only** (the default) | WAL needs `xShmMap`/`xShmLock` shared-memory — a separate large surface (SP3c/SP4 territory). |

## 3. The lock state machine (in `sqlite_vfs_disk.c`)

`DiskFile` gains `int eLock` — the connection's current lock level, one of `SQLITE_LOCK_NONE`(0) / `SHARED`(1) / `RESERVED`(2) / `PENDING`(3) / `EXCLUSIVE`(4).

**Lock bytes** (identical to `os_unix.c`/`os_win.c`, which is what makes it interoperable):
```
PENDING_BYTE  = 0x40000000        /* 1 GiB */
RESERVED_BYTE = PENDING_BYTE + 1
SHARED_FIRST  = PENDING_BYTE + 2
SHARED_SIZE   = 510
```

**`dfLock(file, eTarget)`** — raise the lock level, mirroring `unixLock`. The transitions, expressed as byte-range locks (read = shared, write = exclusive; all non-blocking — `F_SETLK` / `LOCKFILE_FAIL_IMMEDIATELY`):
- already at or above `eTarget` → `SQLITE_OK` (no-op).
- Acquire a **PENDING** lock on `PENDING_BYTE` first when going to SHARED (read-lock) or jumping toward EXCLUSIVE (write-lock). This is the gate that prevents writer starvation.
- **→ SHARED:** (hold PENDING read-lock) take a **read-lock** on the shared range `[SHARED_FIRST, SHARED_SIZE]`, then **release** the PENDING lock. Set `eLock=SHARED`.
- **→ RESERVED:** take a **write-lock** on `RESERVED_BYTE`. Set `eLock=RESERVED`.
- **→ EXCLUSIVE:** take a **write-lock** on `PENDING_BYTE` (this is the PENDING state), then a **write-lock** on the shared range `[SHARED_FIRST, SHARED_SIZE]`. Set `eLock=EXCLUSIVE`.
- Any required lock that is contended → leave `eLock` unchanged, return `SQLITE_BUSY`.

**`dfUnlock(file, eTarget)`** — lower the lock level, mirroring `unixUnlock`. `eTarget` is `SHARED` or `NONE`:
- **→ SHARED** (from RESERVED/PENDING/EXCLUSIVE): release the `RESERVED_BYTE`/`PENDING_BYTE` write-locks and re-assert a **read-lock** on the shared range (downgrade the exclusive shared-range lock to shared). Set `eLock=SHARED`.
- **→ NONE:** release **all** locks (the shared range + any RESERVED/PENDING). Set `eLock=NONE`.

**`dfCheckLock(file, *pOut)`** — mirror `unixCheckReservedLock`: if `eLock >= RESERVED` set `*pOut=1` (we hold it); else probe `RESERVED_BYTE` with a trial write-lock — if it can't be taken, another process holds RESERVED, set `*pOut=1`; otherwise release the trial lock and set `*pOut=0`. Return `SQLITE_OK`.

> Faithful but minimal: this is the single-connection-per-process port of `unixLock`. It does NOT replicate SQLite's per-process inode shared-lock table (which handles multiple connections to the same file in one process) — out of scope, and the VFS opens one fd per file per process.

## 4. Cross-OS lock primitives

Two helpers in `sqlite_vfs_disk.c`, dispatching on `g_win`, are the only place the OS lock APIs are called:

```c
/* take a byte-range lock; returns 1 on success, 0 if contended/failed. */
static int lock_byte(DiskFile *df, long long off, long long len, int exclusive);
/* release a byte-range lock. */
static void unlock_byte(DiskFile *df, long long off, long long len);
```

- **Linux** (`fcntl`, routed to libc): a `struct flock { short l_type; short l_whence; long long l_start; long long l_len; int l_pid; }` (x64 layout — the compiler's natural 4-byte pad after `l_whence` aligns `l_start` to offset 8, giving the correct 32-byte struct). `l_type` = `F_WRLCK`(1) for exclusive / `F_RDLCK`(0) for shared / `F_UNLCK`(2) for unlock; `l_whence=SEEK_SET`; `l_start=off`; `l_len=len`. Call `fcntl(fd, F_SETLK, &fl)` (non-blocking; returns −1 on contention). `fcntl` is declared **non-variadic** (`int fcntl(int,int,void*)`) to use the concrete cdecl P/Invoke path.
- **Windows** (`LockFileEx`/`UnlockFileEx`, routed to kernel32): `OVERLAPPED{Offset=(off), OffsetHigh=(off>>32)}`; `LockFileEx(h, LOCKFILE_FAIL_IMMEDIATELY | (exclusive?LOCKFILE_EXCLUSIVE_LOCK:0), 0, (len low), (len high), &ov)` returns 0 on contention; `UnlockFileEx(h, 0, (len low),(len high), &ov)`.

New `chibil_os.h` decls/constants: `struct flock`; `F_RDLCK 0`, `F_WRLCK 1`, `F_UNLCK 2`, `F_SETLK 6`; `int fcntl(int,int,void*)`; `LOCKFILE_FAIL_IMMEDIATELY 1`, `LOCKFILE_EXCLUSIVE_LOCK 2`; `int LockFileEx(void*,unsigned int,unsigned int,unsigned int,unsigned int,OVERLAPPED*)`; `int UnlockFileEx(void*,unsigned int,unsigned int,unsigned int,OVERLAPPED*)`; and the sleep primitive (`int usleep(unsigned int)` libc / `void Sleep(unsigned int)` kernel32) for the test harnesses' hold/poll windows.

## 5. The two-process contention proof

Two harnesses, both compiled+linked with `{sqlite3.c, sqlite_shim.c, sqlite_vfs_disk.c, <harness>.c}` and the (extended) `--pinvoke` map:

- **`samples/sqlite/main_lock_holder.c`:** `platform_init(); register_disk_vfs();` open `sp3.db`; `CREATE TABLE IF NOT EXISTS t(a INTEGER);`; `BEGIN IMMEDIATE; INSERT INTO t VALUES(1);` (acquires the RESERVED write lock and holds the transaction open); create a `held.marker` file; **wait** for a `release.marker` (poll with `usleep`/`Sleep`, bounded timeout); `COMMIT`; exit 0.
- **`samples/sqlite/main_lock_contender.c`:** `platform_init(); register_disk_vfs();` open `sp3.db`; `sqlite3_busy_timeout(db, 0);` (fail immediately, don't wait); `BEGIN IMMEDIATE; INSERT INTO t VALUES(2);`. Exit convention: **`SQLITE_BUSY` → exit 55** (the lock correctly blocked us); **`SQLITE_OK` (acquired) → exit 0** (`COMMIT` first); any other error → exit 44. This single convention serves both legs below: the contention leg asserts 55, the recovery leg asserts 0.

**`tests/Chibil.Tests/CoreClr/SqliteLockTests.cs`** orchestrates, per OS, in one shared run dir:
1. Build `holder.dll` and `contender.dll` (link each harness set; reuse the SQLite build helper, varying the harness source).
2. Stage both DLLs + runtimeconfig in the run dir; **start the holder in the background**.
3. Poll for `held.marker` (up to a timeout) → the holder now holds the RESERVED lock.
4. **Contention leg:** run the contender (blocking); assert it exits **55** (`SQLITE_BUSY` — blocked by the holder).
5. Write `release.marker`; join the holder (exit 0, after its `COMMIT`).
6. **Recovery leg:** with the holder gone, run the contender again; assert it now exits **0** (it acquires the lock and commits) — proving the holder's locks were released.

**Linux staging (the top risk):** WSL's `/mnt` (drvfs) may not honor `fcntl` advisory byte-range locks. The Linux legs therefore run in a **native ext4 path** (e.g. `/tmp/chibil_lock_<id>`), not the Windows `/mnt` mount: the orchestrator copies `holder.dll`/`contender.dll`/runtimeconfig into `/tmp` (via `wsl cp` or writing through `\\wsl$`), runs there, and reads `held.marker` via `wsl test -f` / `wsl cat`. Windows legs run in an ordinary temp dir (kernel32 `LockFileEx` on NTFS is reliable).

**Regression:** the existing single-process `SqliteDiskTests` must still exit 55 with real locking on — its write transaction now drives the full `dfLock` state machine (SHARED→RESERVED→PENDING→EXCLUSIVE→back), proving no self-deadlock. SP1/SP2/SP3a and the MSVC/IJW suites stay green (linker + codegen untouched).

## 6. Components

| File | Change |
|---|---|
| `samples/sqlite/include/chibil_os.h` | `struct flock`; `fcntl`/`F_*`; `LockFileEx`/`UnlockFileEx`/`LOCKFILE_*`; `usleep`/`Sleep`. |
| `samples/sqlite/sqlite_vfs_disk.c` | `DiskFile.eLock`; `lock_byte`/`unlock_byte`; real `dfLock`/`dfUnlock`/`dfCheckLock`; lock-byte constants. (Replaces the SP3a no-op stubs; the SP3a file I/O is unchanged.) |
| `samples/sqlite/main_lock_holder.c`, `main_lock_contender.c` | New contention harnesses. |
| `tests/Chibil.Tests/CoreClr/SqliteLockTests.cs` | The two-process orchestration (Windows + native-FS Linux). |
| `tests/Chibil.Tests/CoreClr/` helper | A background-process + marker-poll helper (Windows + WSL native-path variants). |
| `PinvokeMap()` (in `PositionedIoTests.cs`) | Add `fcntl=c, LockFileEx=kernel32, UnlockFileEx=kernel32, usleep=c, Sleep=kernel32`. |
| `samples/sqlite/build-chibil-disk.sh` | Extend its `--pinvoke` map with the new symbols (it builds the disk VFS which now references them). |
| **Linker (`tools/chibil-link/`)** | **No change** — the new natives are `--pinvoke`-routed like SP3a's; `struct flock` is ordinary struct codegen. |

## 7. Testing
- **Headline:** the two-process contention test — contender gets `SQLITE_BUSY` while the holder holds a write lock, then succeeds after release — on Windows AND Linux/WSL (Linux on a native ext4 path).
- **Regression:** `SqliteDiskTests` single-process CRUD still exits 55 with locking on (no self-deadlock); `:memory:` `SqliteSmokeTests`, SP2 export tests, and the full MSVC suite stay green.
- **Focused (optional):** a `dfCheckLock` unit-ish check folded into the contention harness (the contender could query reserved-lock state), if cheap.

## 8. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| WSL `/mnt` drvfs ignores `fcntl` locks → Linux test can't show contention | **High** | Run the Linux legs in a **native ext4 path** (`/tmp`), not `/mnt`; the orchestrator stages there. |
| `struct flock` x64 ABI (padding before `l_start`) wrong → locks wrong bytes | Med | The two-process test is the oracle; cross-check the 32-byte layout |
| `unixLock` PENDING dance / downgrade ported incorrectly | Med | Single-process CRUD (no self-deadlock) + the BUSY test both gate it |
| Two-process orchestration flakiness (timing) | Med | Marker-file coordination + generous timeouts; not bare sleeps |
| `fcntl` declared non-variadic vs the real variadic prototype | Low | Same approach proven for `open` in SP3a; the 3rd arg is always a pointer |
| Background-process management in the test harness (zombies/cleanup) | Low | Kill+join the holder in a `finally`; bounded timeouts |

## 9. Out of scope
- **WAL mode** (`xShmMap`/`xShmLock` shared-memory `-shm` file) — a separate large surface.
- Blocking lock waits beyond `sqlite3_busy_timeout`; custom `busy_handler`.
- The per-process multi-connection inode lock table (SQLite's `unixInodeInfo`) — single connection per process here.
- Databases ≥ `PENDING_BYTE` (1 GiB) where the lock bytes overlap real pages (SQLite's special-case) — test DBs are tiny.
- **SP4:** the ergonomic C# API.

## 10. Success criteria
1. `dfLock`/`dfUnlock`/`dfCheckLock` implement SQLite's protocol at the canonical lock bytes via `fcntl` (Linux) / `LockFileEx` (Windows).
2. A contender process gets `SQLITE_BUSY` while a holder process holds a write lock, and succeeds after release — on Windows **and** Linux/WSL (Linux on a native ext4 path).
3. Single-process `SqliteDiskTests` still exits 55 with locking on; `:memory:`, SP2, SP3a, and the MSVC/IJW suites stay green.
