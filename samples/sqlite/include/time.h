/* samples/sqlite/include/time.h — minimal, chibil-parseable */
#ifndef _CHIBIL_TIME_H
#define _CHIBIL_TIME_H
#include <stddef.h>
typedef long time_t;
struct tm {
    int tm_sec, tm_min, tm_hour, tm_mday, tm_mon, tm_year,
        tm_wday, tm_yday, tm_isdst;
};
time_t time(time_t *);
struct tm *gmtime(const time_t *);
struct tm *localtime(const time_t *);
#endif
