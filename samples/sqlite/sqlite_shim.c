/* samples/sqlite/sqlite_shim.c
 *
 * Platform provider for the SQLite :memory: harness.
 *
 *  - Configures a fixed static heap for memsys5 (SQLITE_CONFIG_HEAP).
 *  - Registers a minimal VFS that only supports the operations a :memory:
 *    database needs (randomness, sleep, current time, get-last-error). All
 *    real file I/O methods return SQLITE_IOERR — a :memory: db never touches
 *    them, and SQLITE_TEMP_STORE=3 forces temp storage into memory too.
 *  - Provides freestanding mem/str implementations for the LATER chibil
 *    build, which has no C runtime.
 *
 * mem/str CRT collision:
 *   For the native MSVC reference build the CRT already provides memcpy,
 *   memset, strlen, etc. Defining our own would collide at link time, so they
 *   are guarded under `#ifndef SQLITE_REF_BUILD`. The reference build is
 *   compiled with /DSQLITE_REF_BUILD (see build-ref.cmd), excluding them and
 *   letting the CRT versions satisfy sqlite3.c. The chibil build (no CRT)
 *   compiles WITHOUT that define so these freestanding versions are used.
 */
#include "sqlite3.h"

/* mem/str shims — needed by the chibil build (no CRT). For the native MSVC
   reference build they collide with the CRT, so guard them out there. */
#ifndef SQLITE_REF_BUILD
void *memcpy(void *d, const void *s, unsigned long n){ char*dd=d;const char*ss=s;while(n--)*dd++=*ss++;return d; }
void *memset(void *d, int c, unsigned long n){ char*dd=d;while(n--)*dd++=(char)c;return d; }
void *memmove(void *d, const void *s, unsigned long n){ char*dd=d;const char*ss=s; if(dd<ss){while(n--)*dd++=*ss++;} else {dd+=n;ss+=n;while(n--)*--dd=*--ss;} return d; }
int memcmp(const void *a, const void *b, unsigned long n){ const unsigned char*x=a,*y=b;while(n--){if(*x!=*y)return *x-*y;x++;y++;}return 0; }
unsigned long strlen(const char *s){ const char*p=s;while(*p)p++;return p-s; }
int strcmp(const char *a, const char *b){ while(*a&&*a==*b){a++;b++;}return (unsigned char)*a-(unsigned char)*b; }
int strncmp(const char *a, const char *b, unsigned long n){ while(n&&*a&&*a==*b){a++;b++;n--;}return n?(unsigned char)*a-(unsigned char)*b:0; }
#endif

static char g_heap[8*1024*1024];

static unsigned int g_rng = 0x12345678u;
static int vfsRandomness(sqlite3_vfs *v, int n, char *out){ (void)v; for(int i=0;i<n;i++){ g_rng=g_rng*1103515245u+12345u; out[i]=(char)(g_rng>>16);} return n; }
static int vfsSleep(sqlite3_vfs *v, int micros){ (void)v;(void)micros; return 0; }
static int vfsCurrentTime(sqlite3_vfs *v, double *p){ (void)v; *p=2440587.5; return SQLITE_OK; }
static int vfsGetLastError(sqlite3_vfs *v, int n, char *b){ (void)v;(void)n;(void)b; return 0; }
static int vfsOpen(sqlite3_vfs *v, const char *z, sqlite3_file *f, int flags, int *out){ (void)v;(void)z;(void)f;(void)flags; if(out)*out=0; return SQLITE_IOERR; }
static int vfsDelete(sqlite3_vfs *v, const char *z, int s){ (void)v;(void)z;(void)s; return SQLITE_IOERR; }
static int vfsAccess(sqlite3_vfs *v, const char *z, int f, int *out){ (void)v;(void)z;(void)f; if(out)*out=0; return SQLITE_OK; }
static int vfsFullPathname(sqlite3_vfs *v, const char *z, int n, char *out){ (void)v; int i=0; while(z[i]&&i<n-1){out[i]=z[i];i++;} out[i]=0; return SQLITE_OK; }

/* sqlite3_vfs initializer matching vendor/sqlite3.h (SQLite 3.47.2, iVersion 3).
 * Field order:
 *   iVersion, szOsFile, mxPathname, pNext, zName, pAppData,
 *   xOpen, xDelete, xAccess, xFullPathname,
 *   xDlOpen, xDlError, xDlSym, xDlClose,            (v1, unused -> 0)
 *   xRandomness, xSleep, xCurrentTime, xGetLastError,
 *   xCurrentTimeInt64,                              (v2, unused -> 0)
 *   xSetSystemCall, xGetSystemCall, xNextSystemCall (v3, unused -> 0)
 * => 4 trailing zero fields after xGetLastError (1 for v2 + 3 for v3). */
static sqlite3_vfs g_vfs = {
    3, sizeof(sqlite3_file), 512, 0, "chibil-mem", 0,
    vfsOpen, vfsDelete, vfsAccess, vfsFullPathname,
    0,0,0,0,
    vfsRandomness, vfsSleep, vfsCurrentTime, vfsGetLastError,
    0,
    0,0,0
};

/* With SQLITE_OS_OTHER=1 the application must provide sqlite3_os_init /
   sqlite3_os_end. sqlite3_initialize() calls sqlite3_os_init() internally;
   we register our VFS there so it is available regardless of call order. */
int sqlite3_os_init(void){
    return sqlite3_vfs_register(&g_vfs, 1);
}
int sqlite3_os_end(void){
    return SQLITE_OK;
}

void platform_init(void){
    sqlite3_config(SQLITE_CONFIG_HEAP, g_heap, (int)sizeof(g_heap), 16);
    sqlite3_initialize();
}
