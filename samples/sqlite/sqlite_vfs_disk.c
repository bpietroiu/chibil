/* samples/sqlite/sqlite_vfs_disk.c — a real on-disk sqlite3_vfs, cross-OS.
 * File I/O goes to libc (Linux) or kernel32 (Windows), picked at runtime via
 * __chibil_os_is_windows(). Single connection, NO locking (xLock/xUnlock no-op);
 * the SQLite lock protocol is SP3b. Register with register_disk_vfs(). */
#include "sqlite3.h"
#include "chibil_os.h"

static int g_win;   /* set ONCE in register_disk_vfs() before any open; read-only thereafter (THREADSAFE=0) */

typedef struct DiskFile { sqlite3_file base; long long h; } DiskFile;

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
    if (got < 0) return SQLITE_IOERR_READ;
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
static int dfLock(sqlite3_file *f, int e){ (void)f;(void)e; return SQLITE_OK; }      /* SP3b */
static int dfUnlock(sqlite3_file *f, int e){ (void)f;(void)e; return SQLITE_OK; }
static int dfCheckLock(sqlite3_file *f, int *p){ (void)f; *p=0; return SQLITE_OK; }
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
