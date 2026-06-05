/* chibil shim — shadows musl's arch/x86_64/pthread_arch.h (no include guard).
 *
 * __get_tp reads the thread-pointer (the TLS base) via `mov %fs:0` asm, which
 * chibil can't emit. Forward to a managed PAL extern that returns the per-thread
 * control block pointer (a thread-local on the .NET side). Third seam header
 * alongside syscall_arch.h / atomic_arch.h. */
extern uintptr_t __chibil_get_tp(void);
static inline uintptr_t __get_tp(void) { return __chibil_get_tp(); }

#define MC_PC gregs[REG_RIP]
