#ifndef CHIBIL_COMPAT_H
#define CHIBIL_COMPAT_H
// Portable C fallbacks for GCC builtins chibil does not implement yet.
// -include'd ahead of every TU. These are bit-count intrinsics MicroPython uses
// (misc.h mp_clz/mp_ctz). TODO: implement natively in chibil (map to
// System.Numerics.BitOperations.LeadingZeroCount/TrailingZeroCount/PopCount).
static inline int __builtin_clz(unsigned int x) { int n = 0; while (n < 32 && !(x & 0x80000000u)) { x <<= 1; n++; } return n; }
static inline int __builtin_clzl(unsigned long x) { int n = 0; while (n < 64 && !(x & 0x8000000000000000ul)) { x <<= 1; n++; } return n; }
static inline int __builtin_clzll(unsigned long long x) { int n = 0; while (n < 64 && !(x & 0x8000000000000000ull)) { x <<= 1; n++; } return n; }
static inline int __builtin_ctz(unsigned int x) { if (!x) return 32; int n = 0; while (!(x & 1)) { x >>= 1; n++; } return n; }
static inline int __builtin_ctzl(unsigned long x) { if (!x) return 64; int n = 0; while (!(x & 1)) { x >>= 1; n++; } return n; }
static inline int __builtin_popcount(unsigned int x) { int n = 0; while (x) { n += x & 1; x >>= 1; } return n; }
// chibil doesn't parse the C11 _Static_assert keyword yet — drop the compile-time
// check (TODO: implement _Static_assert in chibil's parser).
#define _Static_assert(cond, msg)
// __builtin_expect(x, c): branch-prediction hint, value is just x.
#define __builtin_expect(x, c) (x)
#endif

