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
