/* samples/sqlite/include/stddef.h — minimal, chibil-parseable */
#ifndef _CHIBIL_STDDEF_H
#define _CHIBIL_STDDEF_H
typedef unsigned long size_t;
typedef long ptrdiff_t;
#define NULL ((void*)0)
#define offsetof(t,m) ((size_t)&(((t*)0)->m))
#endif
