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
