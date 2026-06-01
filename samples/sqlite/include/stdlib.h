/* samples/sqlite/include/stdlib.h — minimal, chibil-parseable */
#ifndef _CHIBIL_STDLIB_H
#define _CHIBIL_STDLIB_H
#include <stddef.h>
void *malloc(size_t);
void *realloc(void *, size_t);
void *calloc(size_t, size_t);
void  free(void *);
void  abort(void);
void  exit(int);
int   atoi(const char *);
long  atol(const char *);
double atof(const char *);
long  strtol(const char *, char **, int);
unsigned long strtoul(const char *, char **, int);
double strtod(const char *, char **);
char *getenv(const char *);
void  qsort(void *, size_t, size_t, int (*)(const void *, const void *));
int   abs(int);
#endif
