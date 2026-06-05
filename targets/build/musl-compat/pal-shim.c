/* PAL-side musl shim: replaces musl threading/asm internals that the managed PAL
 * handles differently. Compiled + linked into every managed-musl build.
 *
 * __syscall_cp is musl's CANCELLABLE syscall — natively a per-arch asm trampoline
 * (__syscall_cp_asm) that checks a thread cancel flag. The managed PAL has no
 * pthread cancellation, so a cancellation point is simply the syscall. */
extern long __chibil_syscall(long, long, long, long, long, long, long);

long __syscall_cp(long n, long a, long b, long c, long d, long e, long f)
{
    return __chibil_syscall(n, a, b, c, d, e, f);
}

/* musl's internal locks. The managed PAL is single-threaded for now, so locking
 * is a no-op (an uncontended lock never touches the futex path). When real
 * threading lands, back these with a managed Monitor. */
void __lock(volatile int *l)   { (void)l; }
void __unlock(volatile int *l) { (void)l; }

/* __toread/__towrite call this to register stdio for atexit flushing. The managed
 * crt doesn't run musl's atexit yet (stdout is line-buffered, which covers the
 * common case), so this is a no-op for now. */
void __stdio_exit_needed(void) { }

/* abort(): the real src/exit/abort.c hits a chibil compound-literal residual.
 * Terminate with the SIGABRT convention (128+6) via exit_group. */
#define SYS_exit_group 231
_Noreturn void abort(void)
{
    __chibil_syscall(SYS_exit_group, 134, 0, 0, 0, 0, 0);
    for (;;) { }
}

/* TLS register setup is arch asm natively; the managed PAL provides the thread
 * pointer via __chibil_get_tp, so the arch TLS install is a no-op. */
int __set_thread_area(void *p) { (void)p; return 0; }

/* Futex wait/wake — only reached on lock contention, which can't happen while the
 * managed PAL is single-threaded. No-ops for now (managed Monitor later). */
void __wait(volatile int *addr, volatile int *waiters, int val, int priv) { (void)addr;(void)waiters;(void)val;(void)priv; }
void __wake(volatile void *addr, int cnt, int priv) { (void)addr;(void)cnt;(void)priv; }

/* pthread cancellation — disabled in the single-threaded managed model. */
int pthread_setcancelstate(int state, int *old) { if (old) *old = 0; (void)state; return 0; }
int pthread_sigmask(int how, const void *set, void *old) { (void)how;(void)set;(void)old; return 0; }

/* clone() is arch asm and creates a thread/process — unsupported in the managed
 * model (fork etc. are referenced by the engine but not used by JS_Eval). */
int __clone(int (*fn)(void *), void *stack, int flags, void *arg, void *ptid, void *tls, void *ctid)
{ (void)fn;(void)stack;(void)flags;(void)arg;(void)ptid;(void)tls;(void)ctid; return -1; }

/* Semaphores / mutexes / condvars — uncontended no-ops while single-threaded.
 * (Engine references these for thread-safety; JS_Eval runs on one thread.) */
int sem_post(void *s) { (void)s; return 0; }
int sem_wait(void *s) { (void)s; return 0; }
int sem_trywait(void *s) { (void)s; return 0; }
int sem_init(void *s, int pshared, unsigned value) { (void)s;(void)pshared;(void)value; return 0; }
int sem_destroy(void *s) { (void)s; return 0; }
int sem_getvalue(void *s, int *v) { (void)s; if (v) *v = 0; return 0; }
int pthread_mutex_lock(void *m) { (void)m; return 0; }
int pthread_mutex_unlock(void *m) { (void)m; return 0; }
int pthread_mutex_trylock(void *m) { (void)m; return 0; }
int pthread_cond_wait(void *c, void *m) { (void)c;(void)m; return 0; }
int pthread_cond_signal(void *c) { (void)c; return 0; }
int pthread_cond_broadcast(void *c) { (void)c; return 0; }
/* broadcast a callback to all threads — single-threaded: run it once, here. */
void __synccall(void (*func)(void *), void *ctx) { func(ctx); }

/* Managed-crt startup init. Natively __init_libc sets __libc.auxv from the program
 * stack; the managed PAL has no auxv, so code that walks it (e.g. mallocng's
 * get_random_secret) would deref a null pointer. Build a minimal auxv with a real
 * AT_RANDOM (16 bytes from getrandom). Call this once before any libc use. */
#include "libc.h"
#define AT_RANDOM 25
#define SYS_getrandom 318
static unsigned long __chibil_auxv[3];
static unsigned char  __chibil_random16[16];
void __chibil_pal_init(void)
{
    __chibil_syscall(SYS_getrandom, (long)__chibil_random16, 16, 0, 0, 0, 0);
    __chibil_auxv[0] = AT_RANDOM;
    __chibil_auxv[1] = (unsigned long)__chibil_random16;
    __chibil_auxv[2] = 0;
    __libc.auxv = (size_t *)__chibil_auxv;
}

/* The real src/stdio/__stdio_seek.c hits a chibil __scc cast-parse bug (a known
 * residual). Console streams aren't seekable anyway, so stub it (ESPIPE) until
 * that parser bug is fixed. */
#include "stdio_impl.h"
off_t __stdio_seek(FILE *f, off_t off, int whence)
{
    (void)f; (void)off; (void)whence;
    return -1;
}

/* fstatat(): the real src/stat/fstatat.c uses a nested designated initializer
 * (.st_atim.tv_sec = ...) that hits a chibil parser residual. It backs
 * stat/fstat/lstat, which the engine references but JS_Eval never exercises (no
 * file I/O for an in-memory eval). Forward to the statx syscall and translate the
 * fields directly here so the wrappers link and behave when a path IS stat'd. */
#include <sys/stat.h>
#include <fcntl.h>
#include <errno.h>
#include <stdint.h>
#include <sys/sysmacros.h>
#define SYS_statx 332
#ifndef AT_NO_AUTOMOUNT
#define AT_NO_AUTOMOUNT 0x800
#endif
struct __chibil_statx {
    uint32_t stx_mask, stx_blksize;
    uint64_t stx_attributes;
    uint32_t stx_nlink, stx_uid, stx_gid;
    uint16_t stx_mode, pad1;
    uint64_t stx_ino, stx_size, stx_blocks, stx_attributes_mask;
    struct { int64_t tv_sec; uint32_t tv_nsec; int32_t pad; } stx_atime, stx_btime, stx_ctime, stx_mtime;
    uint32_t stx_rdev_major, stx_rdev_minor, stx_dev_major, stx_dev_minor;
    uint64_t spare[14];
};
int fstatat(int fd, const char *restrict path, struct stat *restrict st, int flag)
{
    struct __chibil_statx stx;
    long ret = __chibil_syscall(SYS_statx, fd, (long)path, flag | AT_NO_AUTOMOUNT, 0x7ff, (long)&stx, 0);
    if (ret) return (int)ret;
    st->st_dev = makedev(stx.stx_dev_major, stx.stx_dev_minor);
    st->st_ino = stx.stx_ino;
    st->st_mode = stx.stx_mode;
    st->st_nlink = stx.stx_nlink;
    st->st_uid = stx.stx_uid;
    st->st_gid = stx.stx_gid;
    st->st_rdev = makedev(stx.stx_rdev_major, stx.stx_rdev_minor);
    st->st_size = stx.stx_size;
    st->st_blksize = stx.stx_blksize;
    st->st_blocks = stx.stx_blocks;
    st->st_atim.tv_sec = stx.stx_atime.tv_sec;
    st->st_atim.tv_nsec = stx.stx_atime.tv_nsec;
    st->st_mtim.tv_sec = stx.stx_mtime.tv_sec;
    st->st_mtim.tv_nsec = stx.stx_mtime.tv_nsec;
    st->st_ctim.tv_sec = stx.stx_ctime.tv_sec;
    st->st_ctim.tv_nsec = stx.stx_ctime.tv_nsec;
    return 0;
}

/* settimeofday(): src/linux/settimeofday.c hits a chibil compound-literal codegen
 * residual (&(struct timespec){...} -> stack underflow). Setting the wall clock is
 * not supported by the managed PAL and JS_Eval never calls it; no-op success. */
struct timeval; struct timezone;
int settimeofday(const struct timeval *tv, const struct timezone *tz)
{ (void)tv; (void)tz; return 0; }

/* ─────────────────────────────────────────────────────────────────────────────
 * Link tail: thread / process / network / terminal primitives.
 *
 * The managed PAL is single-threaded and has no fork/exec/sockets. The engine TUs
 * and the managed-musl objects reference these symbols, but JS_Eval("40+2") never
 * exercises any of them. Each is a no-op (or failure-returning) stub whose only job
 * is to make the link self-contained. Owning subsystems (thread, network, process,
 * passwd, conf, termios) are deliberately NOT pulled into the managed-musl object
 * set because they would drag in further threading/socket machinery the managed PAL
 * cannot honour. Signatures match musl's public prototypes so the link binds cleanly.
 * ───────────────────────────────────────────────────────────────────────────── */

/* Threading (src/thread): no real threads in the managed model. */
void __inhibit_ptc(void) { }
void __release_ptc(void) { }
struct __ptcb;
void _pthread_cleanup_push(struct __ptcb *cb, void (*f)(void *), void *x) { (void)cb;(void)f;(void)x; }
void _pthread_cleanup_pop(struct __ptcb *cb, int run) { (void)cb;(void)run; }
void pthread_testcancel(void) { }
int pthread_attr_init(void *a) { (void)a; return 0; }
int pthread_attr_setdetachstate(void *a, int s) { (void)a;(void)s; return 0; }
#define EAGAIN_ 11
int pthread_create(void *t, const void *attr, void *(*fn)(void *), void *arg)
{ (void)t;(void)attr;(void)fn;(void)arg; return EAGAIN_; }   /* cannot spawn threads */
unsigned __default_stacksize = 128*1024;   /* DEFAULT_STACK_SIZE; backs pthread_attr_init */

/* Temp-name helper (src/temp): only the file-creation paths use it (not JS_Eval). */
char *__randname(char *t) { return t; }

/* Filesystem (src/stat internal): the kstat path of fstat.c calls __fstatat; the
 * statx-based fstatat is shimmed above. Forward __fstatat to it. */
int fstatat(int, const char *, void *, int);
int __fstatat(int fd, const char *path, void *st, int flag)
{ return fstatat(fd, path, st, flag); }

/* Terminal / conf (src/termios, src/conf): no tty/sysconf surface for JS_Eval. */
int tcsetattr(int fd, int act, const void *tio) { (void)fd;(void)act;(void)tio; return 0; }
long sysconf(int name) { (void)name; return -1; }

/* ioctl(): src/misc/ioctl.c hits a chibil offsetof-in-static-table residual (the
 * v4l2 time-conversion table). The variadic public wrapper just forwards a single
 * arg to SYS_ioctl; the managed PAL maps that syscall to 0. */
#define SYS_ioctl 16
int ioctl(int fd, int req, void *arg) { return (int)__chibil_syscall(SYS_ioctl, fd, req, (long)arg, 0, 0, 0); }

/* Process (src/process): no fork/exec in the managed model. */
typedef int __pid_t_shim;
int posix_spawn(int *pid, const char *path, const void *fa, const void *attr,
                char *const argv[], char *const envp[])
{ (void)pid;(void)path;(void)fa;(void)attr;(void)argv;(void)envp; return 38; } /* ENOSYS */

/* Group DB (src/passwd): pulled by misc/initgroups; never on the JS_Eval path. */
int getgrouplist(const char *user, unsigned gid, unsigned *groups, int *ngroups)
{ (void)user;(void)gid; if (ngroups) { if (groups && *ngroups>0) groups[0]=gid; *ngroups = 1; } return 0; }

/* Network (src/network): no sockets in the managed PAL. Fail with ENOSYS. */
int socket(int d, int t, int p) { (void)d;(void)t;(void)p; return -1; }
int connect(int fd, const void *addr, unsigned len) { (void)fd;(void)addr;(void)len; return -1; }
long send(int fd, const void *buf, unsigned long n, int flags) { (void)fd;(void)buf;(void)n;(void)flags; return -1; }

/* Dynamic-linking / init-array data symbols referenced at link time but unused in a
 * statically-merged managed image: provide zero-init storage so they resolve. */
void *__fini_array_start[1] = { 0 };
void *__fini_array_end[1]   = { 0 };
long  _DYNAMIC[1]           = { 0 };
