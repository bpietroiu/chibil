/* samples/sqlite/include/stdio.h — minimal, chibil-parseable.
   With SQLITE_OS_OTHER and no real file I/O, SQLite uses very little of stdio
   (it has its own printf engine). FILE is opaque; a few prototypes are declared
   for the paths that reference them. */
#ifndef _CHIBIL_STDIO_H
#define _CHIBIL_STDIO_H
#include <stddef.h>
#include <stdarg.h>
#define EOF (-1)
#define FILENAME_MAX 4096
#define BUFSIZ 8192
#define SEEK_SET 0
#define SEEK_CUR 1
#define SEEK_END 2
typedef struct __chibil_FILE FILE;
int   printf(const char *, ...);
int   fprintf(FILE *, const char *, ...);
int   snprintf(char *, size_t, const char *, ...);
int   sprintf(char *, const char *, ...);
int   vsnprintf(char *, size_t, const char *, va_list);
int   fputs(const char *, FILE *);
int   fputc(int, FILE *);
int   fflush(FILE *);
size_t fwrite(const void *, size_t, size_t, FILE *);
FILE *fopen(const char *, const char *);
int   fclose(FILE *);
#endif
