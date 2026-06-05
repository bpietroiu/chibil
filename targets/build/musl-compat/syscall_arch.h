/* chibil shim — shadows musl's arch/x86_64/syscall_arch.h (no include guard, so
 * shadowed by putting this dir first on the -I path).
 *
 * musl's version implements __syscall0..6 with inline `syscall` asm, which chibil
 * can't emit. Here they forward to ONE extern, __chibil_syscall — the managed PAL
 * entry that implements Linux syscall semantics over the .NET BCL (or native per
 * RID). This is the syscall seam made concrete: musl compiles unchanged above it,
 * the platform lives below it. */
#define __SYSCALL_LL_E(x) (x)
#define __SYSCALL_LL_O(x) (x)

extern long __chibil_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6);

static __inline long __syscall0(long n)
{ return __chibil_syscall(n, 0, 0, 0, 0, 0, 0); }
static __inline long __syscall1(long n, long a1)
{ return __chibil_syscall(n, a1, 0, 0, 0, 0, 0); }
static __inline long __syscall2(long n, long a1, long a2)
{ return __chibil_syscall(n, a1, a2, 0, 0, 0, 0); }
static __inline long __syscall3(long n, long a1, long a2, long a3)
{ return __chibil_syscall(n, a1, a2, a3, 0, 0, 0); }
static __inline long __syscall4(long n, long a1, long a2, long a3, long a4)
{ return __chibil_syscall(n, a1, a2, a3, a4, 0, 0); }
static __inline long __syscall5(long n, long a1, long a2, long a3, long a4, long a5)
{ return __chibil_syscall(n, a1, a2, a3, a4, a5, 0); }
static __inline long __syscall6(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{ return __chibil_syscall(n, a1, a2, a3, a4, a5, a6); }

/* No VDSO_USEFUL: clock_gettime etc. take the plain __syscall path (the PAL
 * handles it) rather than musl's vDSO symbol lookup. */
#define IPC_64 0
