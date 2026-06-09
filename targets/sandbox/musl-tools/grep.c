/* grep — managed coreutil (M5): print input lines matching a PATTERN, using the REAL POSIX
 * regex engine (musl's TRE regcomp/regexec, now compiled into the managed musl set). Basic
 * regex by default; -E selects extended. Reads the named files, or stdin if none. Flags:
 * -v invert, -i ignore case, -c count only, -n line numbers, -E extended regex. Exit 0 if any
 * line matched, 1 if none, 2 on error. */
#include <regex.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>

static int opt_v, opt_c, opt_n;

static void put_long(long v)
{
    char t[24]; int i = 0;
    if (v == 0) t[i++] = '0';
    while (v > 0) { t[i++] = (char)('0' + v % 10); v /= 10; }
    char o[24]; int j = 0;
    while (i > 0) o[j++] = t[--i];
    write(1, o, (size_t)j);
}

/* Stream fd line by line, NUL-terminating each line for regexec. */
static int grep_fd(int fd, const regex_t *re, long *count, long *lineno)
{
    char buf[8192], line[8192];
    long llen = 0;
    ssize_t n;
    for (;;)
    {
        n = read(fd, buf, sizeof buf);
        if (n < 0) return 1;
        for (ssize_t i = 0; i <= (n == 0 ? 0 : n); i++)
        {
            int eof_flush = (n == 0 && i == 0 && llen > 0);
            int nl = (i < n && buf[i] == '\n');
            if (nl || eof_flush || llen == (long)sizeof line - 1)
            {
                line[llen] = '\0';
                (*lineno)++;
                int hit = regexec(re, line, 0, 0, 0) == 0;
                if (hit != opt_v)
                {
                    (*count)++;
                    if (!opt_c)
                    {
                        if (opt_n) { put_long(*lineno); write(1, ":", 1); }
                        write(1, line, (size_t)llen);
                        write(1, "\n", 1);
                    }
                }
                llen = 0;
                if (!nl && i < n) line[llen++] = buf[i];   /* overflow split: keep the char */
            }
            else if (i < n)
                line[llen++] = buf[i];
        }
        if (n == 0) break;
    }
    return 0;
}

int main(int argc, char **argv)
{
    int argi = 1, i, cflags = 0;
    while (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *f = argv[argi] + 1;
        for (; *f; f++)
        {
            if (*f == 'v') opt_v = 1;
            else if (*f == 'c') opt_c = 1;
            else if (*f == 'n') opt_n = 1;
            else if (*f == 'i') cflags |= REG_ICASE;
            else if (*f == 'E') cflags |= REG_EXTENDED;
        }
        argi++;
    }
    if (argi >= argc) return 2;                 /* no pattern */
    const char *pat = argv[argi++];

    regex_t re;
    if (regcomp(&re, pat, cflags | REG_NOSUB) != 0)
    {
        const char *msg = "grep: invalid pattern\n";
        write(2, msg, strlen(msg));
        return 2;
    }

    long count = 0, lineno = 0;
    int err = 0;
    if (argi >= argc)
        err |= grep_fd(0, &re, &count, &lineno);
    else
        for (i = argi; i < argc; i++)
        {
            int fd = (argv[i][0] == '-' && argv[i][1] == '\0') ? 0 : open(argv[i], O_RDONLY);
            if (fd < 0) { err = 1; continue; }
            err |= grep_fd(fd, &re, &count, &lineno);
            if (fd != 0) close(fd);
        }
    regfree(&re);

    if (opt_c) { put_long(count); write(1, "\n", 1); }
    if (err) return 2;
    return count > 0 ? 0 : 1;
}
