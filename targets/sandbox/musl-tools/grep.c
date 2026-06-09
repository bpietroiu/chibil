/* grep — managed coreutil (M5): print input lines that contain a fixed PATTERN (substring
 * match — not a regex; the managed musl set omits the regex engine, and fixed-string is what
 * pipelines mostly need). Reads the named files, or stdin if none. Flags: -v invert (print
 * non-matching lines), -i case-insensitive, -c print only the count of matching lines, -n
 * prefix each match with its 1-based line number. The streaming, line-at-a-time member of
 * the coreutil set — the canonical middle of a pipeline (`… | grep foo | …`). */
#include <unistd.h>
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>

static int opt_v, opt_i, opt_c, opt_n;

static char lower(char c) { return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c; }

/* Substring search honoring -i. Returns 1 if `pat` occurs in `s` (len `slen`). */
static int contains(const char *s, long slen, const char *pat)
{
    long plen = (long)strlen(pat);
    if (plen == 0) return 1;
    for (long i = 0; i + plen <= slen; i++)
    {
        long j = 0;
        for (; j < plen; j++)
        {
            char a = s[i + j], b = pat[j];
            if (opt_i) { a = lower(a); b = lower(b); }
            if (a != b) break;
        }
        if (j == plen) return 1;
    }
    return 0;
}

static void put_long(long v)
{
    char t[24]; int i = 0;
    if (v == 0) t[i++] = '0';
    while (v > 0) { t[i++] = (char)('0' + v % 10); v /= 10; }
    char o[24]; int j = 0;
    while (i > 0) o[j++] = t[--i];
    write(1, o, (size_t)j);
}

/* Stream fd line by line, applying the match; accumulates the match count into *count. */
static int grep_fd(int fd, const char *pat, long *count, long *lineno)
{
    char buf[8192], line[8192];
    long llen = 0;
    ssize_t n;
    while ((n = read(fd, buf, sizeof buf)) > 0)
    {
        for (ssize_t i = 0; i < n; i++)
        {
            char ch = buf[i];
            if (ch == '\n' || llen == (long)sizeof line - 1)
            {
                (*lineno)++;
                int hit = contains(line, llen, pat);
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
                if (ch != '\n') line[llen++] = ch;   /* overflow split: keep the char */
            }
            else
                line[llen++] = ch;
        }
    }
    if (llen > 0)   /* trailing partial line (no final newline) */
    {
        (*lineno)++;
        int hit = contains(line, llen, pat);
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
    }
    return n < 0 ? 1 : 0;
}

int main(int argc, char **argv)
{
    int argi = 1, i, rc = 1;   /* grep exits 1 when no line matched */
    while (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *f = argv[argi] + 1;
        for (; *f; f++)
        {
            if (*f == 'v') opt_v = 1;
            else if (*f == 'i') opt_i = 1;
            else if (*f == 'c') opt_c = 1;
            else if (*f == 'n') opt_n = 1;
        }
        argi++;
    }
    if (argi >= argc) return 2;            /* no pattern */
    const char *pat = argv[argi++];

    long count = 0, lineno = 0;
    int err = 0;
    if (argi >= argc)
        err |= grep_fd(0, pat, &count, &lineno);
    else
        for (i = argi; i < argc; i++)
        {
            int fd = (argv[i][0] == '-' && argv[i][1] == '\0') ? 0 : open(argv[i], O_RDONLY);
            if (fd < 0) { err = 1; continue; }
            err |= grep_fd(fd, pat, &count, &lineno);
            if (fd != 0) close(fd);
        }

    if (opt_c) { put_long(count); write(1, "\n", 1); }
    if (err) return 2;
    return count > 0 ? 0 : 1;
    (void)rc;
}
