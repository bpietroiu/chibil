/* samples/sqlite/include/assert.h — minimal. Asserts are disabled for SP1
   (the harness is deterministic; SQLite runs fine with NDEBUG-style asserts). */
#ifndef _CHIBIL_ASSERT_H
#define _CHIBIL_ASSERT_H
#define assert(x) ((void)0)
#endif
