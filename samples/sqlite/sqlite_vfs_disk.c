/* samples/sqlite/sqlite_vfs_disk.c — a real on-disk sqlite3_vfs, cross-OS.
 * File I/O goes to libc (Linux) or kernel32 (Windows), picked at runtime via
 * __chibil_os_is_windows(). Single connection per process; SQLite's byte-range
 * lock protocol (fcntl / LockFileEx) is implemented below. Register with
 * register_disk_vfs(). */
#include "sqlite3.h"
#include "chibil_os.h"

static int g_win;   /* set ONCE in register_disk_vfs() before any open; read-only thereafter (THREADSAFE=0) */

typedef struct DiskFile { sqlite3_file base; long long h; int eLock; } DiskFile;

static void zero(void *p, int n){ char *z=(char*)p; for(int i=0;i<n;i++) z[i]=0; }

static int dfRead(sqlite3_file *f, void *buf, int n, sqlite3_int64 off){
    DiskFile *df=(DiskFile*)f; long long got;
    if (g_win){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        unsigned int rd=0;
        int ok = ReadFile((void*)df->h,buf,(unsigned int)n,&rd,&ov);
        /* A positioned ReadFile that hits EOF returns FALSE with ERROR_HANDLE_EOF and rd<n;
           that is a legitimate short read (SQLite zero-fills). Only a non-EOF FALSE is a
           hard I/O error. (Mirrors SQLite's own winRead.) */
        if (!ok && GetLastError() != ERROR_HANDLE_EOF) return SQLITE_IOERR_READ;
        got=(long long)rd; }
    else got = pread((int)df->h, buf, (unsigned long long)n, off);
    if (got == n) return SQLITE_OK;
    if (got < 0) return SQLITE_IOERR_READ;   /* live on Linux (pread -1); Windows errors handled above via GetLastError */
    char *z=(char*)buf; for (long long i=got;i<n;i++) z[i]=0;   /* zero-fill tail */
    return SQLITE_IOERR_SHORT_READ;
}
static int dfWrite(sqlite3_file *f, const void *buf, int n, sqlite3_int64 off){
    DiskFile *df=(DiskFile*)f; long long put;
    if (g_win){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        unsigned int wr=0; WriteFile((void*)df->h,buf,(unsigned int)n,&wr,&ov); put=(long long)wr; }
    else put = pwrite((int)df->h, buf, (unsigned long long)n, off);
    return put == n ? SQLITE_OK : SQLITE_IOERR_WRITE;
}
static int dfTruncate(sqlite3_file *f, sqlite3_int64 size){
    DiskFile *df=(DiskFile*)f;
    if (g_win){ long long np; if(!SetFilePointerEx((void*)df->h,size,&np,FILE_BEGIN)) return SQLITE_IOERR_TRUNCATE;
        return SetEndOfFile((void*)df->h) ? SQLITE_OK : SQLITE_IOERR_TRUNCATE; }
    return ftruncate((int)df->h, size)==0 ? SQLITE_OK : SQLITE_IOERR_TRUNCATE;
}
static int dfSync(sqlite3_file *f, int flags){ (void)flags; DiskFile *df=(DiskFile*)f;
    if (g_win) return FlushFileBuffers((void*)df->h) ? SQLITE_OK : SQLITE_IOERR_FSYNC;
    return fsync((int)df->h)==0 ? SQLITE_OK : SQLITE_IOERR_FSYNC; }
static int dfFileSize(sqlite3_file *f, sqlite3_int64 *pSize){ DiskFile *df=(DiskFile*)f;
    if (g_win){ long long s=0; if(!GetFileSizeEx((void*)df->h,&s)) return SQLITE_IOERR_FSTAT; *pSize=s; return SQLITE_OK; }
    long long s = lseek((int)df->h, 0, SEEK_END); if (s<0) return SQLITE_IOERR_FSTAT; *pSize=s; return SQLITE_OK; }
static int dfClose(sqlite3_file *f){ DiskFile *df=(DiskFile*)f;
    if (g_win) CloseHandle((void*)df->h); else close((int)df->h); return SQLITE_OK; }
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
            if (g_win) lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0); /* restore shared read-lock; a kernel-level failure here (not contention) would desync eLock — unhandled, as in os_win.c */
            df->eLock = SQLITE_LOCK_PENDING;     /* we do hold PENDING */
            return SQLITE_BUSY;
        }
        df->eLock = SQLITE_LOCK_EXCLUSIVE;
        return SQLITE_OK;
    }
    /* unreachable: SQLite never requests PENDING directly (it's an internal gate). */
    return SQLITE_OK;
}
static int dfUnlock(sqlite3_file *f, int eTarget){
    DiskFile *df=(DiskFile*)f;
    if (df->eLock <= eTarget) return SQLITE_OK;
    if (eTarget == SQLITE_LOCK_SHARED){
        if (df->eLock == SQLITE_LOCK_EXCLUSIVE){
            /* downgrade the shared range from write back to read */
            if (g_win){ unlock_byte(df, SHARED_FIRST, SHARED_SIZE); lock_byte(df, SHARED_FIRST, SHARED_SIZE, 0); } /* brief no-lock window between unlock and relock — same as os_win.c; a relock kernel-failure is unhandled */
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
        *pOut = 1;                                /* contended (or a rare lock I/O error, treated conservatively as held) */
    }
    return SQLITE_OK;
}
static int dfControl(sqlite3_file *f, int op, void *a){ (void)f;(void)op;(void)a; return SQLITE_NOTFOUND; }
static int dfSectorSize(sqlite3_file *f){ (void)f; return 512; }
static int dfDevChar(sqlite3_file *f){ (void)f; return 0; }

static sqlite3_io_methods g_io = {
    1, dfClose, dfRead, dfWrite, dfTruncate, dfSync, dfFileSize,
    dfLock, dfUnlock, dfCheckLock, dfControl, dfSectorSize, dfDevChar
};

static int vOpen(sqlite3_vfs *v, const char *z, sqlite3_file *f, int flags, int *pOut){
    (void)v; DiskFile *df=(DiskFile*)f; df->base.pMethods=0;
    if (!z) return SQLITE_CANTOPEN;   /* temp files unsupported (SQLITE_TEMP_STORE=3 keeps temp in RAM) */
    /* SP3a: always opened RDWR; SQLITE_OPEN_READONLY is not specialized (WAL / read-only DBs are out of scope). */
    long long h;
    if (g_win){
        unsigned int disp = (flags & SQLITE_OPEN_EXCLUSIVE) ? CREATE_NEW
                          : (flags & SQLITE_OPEN_CREATE)    ? OPEN_ALWAYS
                          :                                   OPEN_EXISTING;
        h = (long long)(void*)CreateFileA(z, GENERIC_READ|GENERIC_WRITE,
                FILE_SHARE_READ|FILE_SHARE_WRITE, 0, disp, FILE_ATTRIBUTE_NORMAL, 0);
        if ((void*)h == INVALID_HANDLE_VALUE) return SQLITE_CANTOPEN;
    } else {
        int of = O_RDWR
               | ((flags & SQLITE_OPEN_CREATE)    ? O_CREAT : 0)
               | ((flags & SQLITE_OPEN_EXCLUSIVE) ? O_EXCL  : 0);
        h = open(z, of, 420);
        if (h < 0) return SQLITE_CANTOPEN;
    }
    df->h = h; df->base.pMethods = &g_io;
    df->eLock = SQLITE_LOCK_NONE;
    if (pOut) *pOut = flags & (SQLITE_OPEN_READONLY|SQLITE_OPEN_READWRITE|SQLITE_OPEN_CREATE);
    return SQLITE_OK;
}
static int vDelete(sqlite3_vfs *v, const char *z, int s){ (void)v;(void)s;
    if (g_win) return DeleteFileA(z) ? SQLITE_OK : SQLITE_IOERR_DELETE;
    return unlink(z)==0 ? SQLITE_OK : SQLITE_IOERR_DELETE; }
static int vAccess(sqlite3_vfs *v, const char *z, int flags, int *pOut){ (void)v;(void)flags;
    /* Only existence is needed here (the pager's hot-journal check); READWRITE/READ
       permission modes are not exercised under this config — SP3b can extend. */
    if (g_win) *pOut = (GetFileAttributesA(z) != INVALID_FILE_ATTRIBUTES);
    else *pOut = (access(z, F_OK) == 0);
    return SQLITE_OK; }
static int vFullPath(sqlite3_vfs *v, const char *z, int n, char *out){ (void)v;
    if (n <= 0) return SQLITE_OK;
    int i=0; while(z[i] && i<n-1){ out[i]=z[i]; i++; } out[i]=0; return SQLITE_OK; }

/* VFS-level housekeeping (own copies; sqlite_shim.c's are static there). */
static unsigned int g_rng = 0xC0FFEEu;
static int vRand(sqlite3_vfs *v,int n,char *o){ (void)v; for(int i=0;i<n;i++){ g_rng=g_rng*1103515245u+12345u; o[i]=(char)(g_rng>>16);} return n; }
static int vSleep(sqlite3_vfs *v,int us){ (void)v;(void)us; return 0; }
static int vCurTime(sqlite3_vfs *v,double *p){ (void)v; *p=2440587.5; return SQLITE_OK; }
static int vLastErr(sqlite3_vfs *v,int n,char *b){ (void)v;(void)n;(void)b; return 0; }

/* sqlite3_vfs (iVersion 3): iVersion, szOsFile, mxPathname, pNext, zName, pAppData,
 * xOpen, xDelete, xAccess, xFullPathname, [4 v1 xDl* = 0],
 * xRandomness, xSleep, xCurrentTime, xGetLastError, [xCurrentTimeInt64=0], [3 v3 syscall=0]. */
static sqlite3_vfs g_disk_vfs = {
    3, sizeof(DiskFile), 1024, 0, "chibil-disk", 0,
    vOpen, vDelete, vAccess, vFullPath,
    0,0,0,0,
    vRand, vSleep, vCurTime, vLastErr,
    0, 0,0,0
};

void register_disk_vfs(void){
    g_win = __chibil_os_is_windows();
    sqlite3_vfs_register(&g_disk_vfs, 1);   /* makeDflt = 1 */
}
