/* chibil shim — shadows musl's arch/x86_64/atomic_arch.h (no include guard).
 *
 * musl's version implements the atomic primitives with `lock`-prefixed inline
 * asm. chibil can't emit asm, so these are plain-C, single-threaded-correct
 * stand-ins (the EMSCRIPTEN-build assumption chibil already relies on for
 * QuickJS). The real managed musl backs these with externs mapped to
 * System.Threading.Interlocked; for the compile spike, non-atomic is sufficient. */
#include <stdint.h>

#define a_cas a_cas
static inline int a_cas(volatile int *p, int t, int s)
{ int old = *p; if (old == t) *p = s; return old; }

#define a_cas_p a_cas_p
static inline void *a_cas_p(volatile void *p, void *t, void *s)
{ void *old = *(void *volatile *)p; if (old == t) *(void *volatile *)p = s; return old; }

#define a_swap a_swap
static inline int a_swap(volatile int *p, int v)
{ int old = *p; *p = v; return old; }

#define a_fetch_add a_fetch_add
static inline int a_fetch_add(volatile int *p, int v)
{ int old = *p; *p = old + v; return old; }

#define a_and a_and
static inline void a_and(volatile int *p, int v) { *p &= v; }

#define a_or a_or
static inline void a_or(volatile int *p, int v) { *p |= v; }

#define a_and_64 a_and_64
static inline void a_and_64(volatile uint64_t *p, uint64_t v) { *p &= v; }

#define a_or_64 a_or_64
static inline void a_or_64(volatile uint64_t *p, uint64_t v) { *p |= v; }

#define a_inc a_inc
static inline void a_inc(volatile int *p) { ++*p; }

#define a_dec a_dec
static inline void a_dec(volatile int *p) { --*p; }

#define a_store a_store
static inline void a_store(volatile int *p, int x) { *p = x; }

#define a_barrier a_barrier
static inline void a_barrier(void) { }

#define a_spin a_spin
static inline void a_spin(void) { }

#define a_crash a_crash
static inline void a_crash(void) { *(volatile int *)0 = 0; }

#define a_ctz_64 a_ctz_64
static inline int a_ctz_64(uint64_t x)
{ int n = 0; if (!x) return 64; while (!(x & 1)) { x >>= 1; n++; } return n; }

#define a_clz_64 a_clz_64
static inline int a_clz_64(uint64_t x)
{ int n = 0; if (!x) return 64; while (!(x & ((uint64_t)1 << 63))) { x <<= 1; n++; } return n; }
