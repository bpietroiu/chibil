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
void *memchr(const void *s, int c, unsigned long n){ const unsigned char*p=s; while(n--){ if(*p==(unsigned char)c) return (void*)p; p++; } return 0; }
char *strchr(const char *s, int c){ for(;;s++){ if(*s==(char)c) return (char*)s; if(!*s) return 0; } }
char *strrchr(const char *s, int c){ const char*last=0; for(;;s++){ if(*s==(char)c) last=s; if(!*s) return (char*)last; } }
unsigned long strspn(const char *s, const char *set){ const char*p=s; while(*p){ const char*q=set; while(*q&&*q!=*p)q++; if(!*q)break; p++; } return p-s; }
unsigned long strcspn(const char *s, const char *set){ const char*p=s; while(*p){ const char*q=set; while(*q){ if(*q==*p) return p-s; q++; } p++; } return p-s; }
#endif

/* math + time shims needed by the chibil build (no CRT). */
double fabs(double x){ return x<0 ? -x : x; }

/* localtime: a freestanding civil-calendar conversion (UTC; no DST/zones).
   The :memory: CREATE/INSERT/SELECT harness never invokes date functions, so
   this only needs to LINK and be safe if ever called. */
#include "time.h"
struct tm *localtime(const time_t *t){
    static struct tm tmv;
    long secs = t ? *t : 0;
    long days = secs / 86400; long rem = secs % 86400;
    if (rem < 0) { rem += 86400; days -= 1; }
    tmv.tm_hour = (int)(rem / 3600);
    tmv.tm_min  = (int)((rem % 3600) / 60);
    tmv.tm_sec  = (int)(rem % 60);
    tmv.tm_wday = (int)((days % 7 + 4) % 7); if (tmv.tm_wday < 0) tmv.tm_wday += 7; /* 1970-01-01 = Thu */
    /* Howard Hinnant's days-from-civil, inverted. */
    long z = days + 719468;
    long era = (z >= 0 ? z : z - 146096) / 146097;
    unsigned long doe = (unsigned long)(z - era * 146097);
    unsigned long yoe = (doe - doe/1460 + doe/36524 - doe/146096) / 365;
    long y = (long)yoe + era * 400;
    unsigned long doy = doe - (365*yoe + yoe/4 - yoe/100);
    unsigned long mp = (5*doy + 2)/153;
    unsigned long d = doy - (153*mp+2)/5 + 1;
    unsigned long m = mp < 10 ? mp+3 : mp-9;
    y += (m <= 2);
    tmv.tm_year = (int)(y - 1900);
    tmv.tm_mon  = (int)(m - 1);
    tmv.tm_mday = (int)d;
    tmv.tm_yday = 0;
    tmv.tm_isdst = 0;
    return &tmv;
}

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

/* Cross-TU variadic call bridge.
 *
 * sqlite3_config(int, ...) is a chibil-DEFINED *managed* variadic (it lives in
 * sqlite3.c). chibil lowers a managed variadic to a hidden trailing "va-buffer"
 * pointer ABI ("Layer 1"): the definition's real signature is
 *   int sqlite3_config(int op, void *va_buffer)
 * where va_buffer holds the variadic arguments in 8-byte slots, one per arg,
 * each value stored at the slot start (int in the low 4 bytes, pointer full 8).
 *
 * When sqlite3_config is called from THIS translation unit it is only an extern
 * declaration, so chibil would instead emit a native "Layer 2" concrete cdecl
 * call (4 separate args) — which mismatches the 2-param managed MethodDef the
 * linker resolves to and faults. A chibil variadic defined in a *different* TU
 * and called as extern is an unsupported codegen path. We therefore pack the
 * Layer-1 va-buffer by hand and call sqlite3_config through its real 2-param
 * managed signature, which the linker binds to the definition correctly.
 *
 * For SQLITE_CONFIG_HEAP (op 8) the varargs are: void* pHeap, int nByte, int min.
 *
 * We invoke sqlite3_config through a function pointer whose type is the concrete
 * Layer-1 signature `int (*)(int, void*)`. Taking &sqlite3_config yields its
 * managed MethodDef (ldftn); the indirect call's standalone signature then has
 * exactly the two params the definition expects. (We cannot simply redeclare
 * sqlite3_config non-variadically — that conflicts with sqlite3.h's
 * `int sqlite3_config(int, ...)`.)
 */
typedef int (*config_layer1_fn)(int op, void *va_buffer);

void platform_init(void){
    unsigned char va[24];                       /* 3 slots * 8 bytes */
    *(void **)(va + 0)        = g_heap;          /* slot 0: heap pointer */
    *(long long *)(va + 8)    = (long long)(int)sizeof(g_heap); /* slot 1: nByte */
    *(long long *)(va + 16)   = 16;              /* slot 2: min allocation */
    ((config_layer1_fn)sqlite3_config)(SQLITE_CONFIG_HEAP, va);
    sqlite3_initialize();
}
