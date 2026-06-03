#ifndef QJS_CHIBIL_COMPAT_H
#define QJS_CHIBIL_COMPAT_H
// Portable C fallbacks for GCC builtins chibil does not implement yet.
// -include'd ahead of every QuickJS TU. (TODO: implement natively in chibil via
// System.Numerics.BitOperations.{LeadingZeroCount,TrailingZeroCount,PopCount}.)
static inline int __builtin_clz(unsigned int x) { int n = 0; while (n < 32 && !(x & 0x80000000u)) { x <<= 1; n++; } return n; }
static inline int __builtin_clzll(unsigned long long x) { int n = 0; while (n < 64 && !(x & 0x8000000000000000ull)) { x <<= 1; n++; } return n; }
static inline int __builtin_ctz(unsigned int x) { if (!x) return 32; int n = 0; while (!(x & 1)) { x >>= 1; n++; } return n; }
static inline int __builtin_ctzll(unsigned long long x) { if (!x) return 64; int n = 0; while (!(x & 1)) { x >>= 1; n++; } return n; }
// __builtin_expect(x, c): branch-prediction hint, value is just x.
#define __builtin_expect(x, c) (x)
// chibil doesn't parse the C11 _Static_assert keyword yet — drop the check.
#define _Static_assert(cond, msg)
// Strip GCC `__attribute__((...))` / `__attribute((...))` entirely. chibil parses it
// in prefix position but not POSTFIX (`void f(...) __attribute__((format(...)));`, used
// by run-test262.c), and ignores the attribute either way — so dropping it is
// behavior-equivalent and handles every position. (TODO: parse postfix attributes in
// chibil.)
#define __attribute__(x)
#define __attribute(x)
#endif
