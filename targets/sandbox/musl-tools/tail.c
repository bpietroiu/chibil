/* tail — managed coreutil (M5): print the last N lines (default 10) of each file, or of
 * stdin when no file is given. Unlike head, tail must see the whole input before it can
 * know where the last N lines begin, so it slurps each source into a malloc-grown buffer
 * (musl's allocator on the managed PAL) and then scans backward for N newline boundaries. */
#include <unistd.h>
#include <fcntl.h>
#include <stdlib.h>

/* Read all of fd into a heap buffer; sets *out/*len. Returns 0, or 1 on error (out freed). */
static int slurp(int fd, char **out, long *len)
{
    long cap = 8192, n = 0;
    char *b = malloc(cap);
    if (!b) return 1;
    for (;;)
    {
        if (n == cap) { cap *= 2; char *nb = realloc(b, cap); if (!nb) { free(b); return 1; } b = nb; }
        ssize_t r = read(fd, b + n, (size_t)(cap - n));
        if (r < 0) { free(b); return 1; }
        if (r == 0) break;
        n += r;
    }
    *out = b; *len = n;
    return 0;
}

/* Print the last `limit` lines of the buffer [b, b+len). A line is text ending in '\n';
 * a trailing partial line counts as a line too. */
static void emit_tail(const char *b, long len, long limit)
{
    if (len == 0 || limit == 0) return;
    /* Walk backward counting newlines. Ignore a final '\n' (end of the last line, not a
     * separator before a new one). Find the start of the (limit)-from-last line. */
    long i = len - 1;
    long seen = 0;
    if (b[i] == '\n') i--;            /* skip the trailing newline of the last line */
    for (; i >= 0; i--)
        if (b[i] == '\n')
        {
            seen++;
            if (seen == limit) break;
        }
    long start = i + 1;              /* i is at the newline before our first wanted line, or -1 */
    write(1, b + start, (size_t)(len - start));
}

static long parse_long(const char *s)
{
    long v = 0;
    if (!*s) return -1;
    for (; *s; s++) { if (*s < '0' || *s > '9') return -1; v = v * 10 + (*s - '0'); }
    return v;
}

int main(int argc, char **argv)
{
    long limit = 10;
    int argi = 1, rc = 0, i;

    if (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *a = argv[argi] + 1;
        if (*a == 'n')
        {
            const char *num = a + 1;
            if (*num == '\0' && argi + 1 < argc) { argi++; num = argv[argi]; }
            long v = parse_long(num);
            if (v >= 0) limit = v;
            argi++;
        }
        else if (*a >= '0' && *a <= '9')
        {
            long v = parse_long(a);
            if (v >= 0) limit = v;
            argi++;
        }
    }

    if (argi >= argc)
    {
        char *b; long len;
        if (slurp(0, &b, &len)) return 1;
        emit_tail(b, len, limit);
        free(b);
        return 0;
    }

    for (i = argi; i < argc; i++)
    {
        int fd = (argv[i][0] == '-' && argv[i][1] == '\0') ? 0 : open(argv[i], O_RDONLY);
        if (fd < 0) { rc = 1; continue; }
        char *b; long len;
        if (slurp(fd, &b, &len)) { rc = 1; if (fd != 0) close(fd); continue; }
        emit_tail(b, len, limit);
        free(b);
        if (fd != 0) close(fd);
    }
    return rc;
}
