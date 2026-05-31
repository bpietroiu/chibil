/* samples/sqlite/include/stdarg.h
   NOTE: va_start/va_arg map to compiler intrinsics that chibil does NOT yet
   implement (see SP1 plan Part B). This header is finalized in Part B once the
   varargs lowering exists. For now it lets includes resolve. */
#ifndef _CHIBIL_STDARG_H
#define _CHIBIL_STDARG_H
typedef __builtin_va_list va_list;
#define va_start(ap, last) __builtin_va_start(ap, last)
#define va_arg(ap, type)   __builtin_va_arg(ap, type)
#define va_end(ap)         __builtin_va_end(ap)
#define va_copy(d, s)      __builtin_va_copy(d, s)
#endif
