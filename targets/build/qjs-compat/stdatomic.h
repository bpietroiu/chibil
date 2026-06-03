#ifndef QJS_STUB_STDATOMIC_H
#define QJS_STUB_STDATOMIC_H
// Minimal non-atomic stub of <stdatomic.h> for the chibil/MSIL build. musl ships no
// stdatomic.h (it's a compiler header). QuickJS's quickjs-libc.c uses only
// atomic_fetch_add for SharedArrayBuffer ref-counting; the chibil build is
// single-threaded (EMSCRIPTEN config disables OS threads/atomics), so plain
// read-modify-write is correct here.
#include <stdint.h>
#define _Atomic(T) T
static inline unsigned int atomic_fetch_add(volatile unsigned int *p, unsigned int v) { unsigned int o = *p; *p = o + v; return o; }
static inline unsigned int atomic_load(volatile unsigned int *p) { return *p; }
static inline void atomic_store(volatile unsigned int *p, unsigned int v) { *p = v; }
#endif
