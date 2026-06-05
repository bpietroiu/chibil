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
