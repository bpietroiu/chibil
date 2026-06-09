/* sort — managed coreutil (M5): sort the lines of the input (files, or stdin if none) and
 * write them out. Like tail, sort must see all input before producing any output, so it
 * slurps everything into a malloc-grown buffer, splits on '\n', qsorts, and emits. Flags:
 * -r reverse, -n numeric (compare leading integer value), -u unique (drop adjacent dups
 * after sorting). The cat/wc pattern applied to a whole-input transform. */
#include <unistd.h>
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>

static int opt_r, opt_n, opt_u;

/* Read all of fd, appending to *buf (cap-managed via *cap, length *len). 0 ok, 1 on error. */
static int slurp(int fd, char **buf, long *len, long *cap)
{
    for (;;)
    {
        if (*len + 8192 > *cap)
        {
            long nc = (*cap ? *cap : 8192) * 2;
            while (*len + 8192 > nc) nc *= 2;
            char *nb = realloc(*buf, (size_t)nc);
            if (!nb) return 1;
            *buf = nb; *cap = nc;
        }
        ssize_t r = read(fd, *buf + *len, 8192);
        if (r < 0) return 1;
        if (r == 0) return 0;
        *len += r;
    }
}

static long num_of(const char *s)
{
    while (*s == ' ' || *s == '\t') s++;
    int neg = 0;
    if (*s == '-') { neg = 1; s++; }
    long v = 0;
    for (; *s >= '0' && *s <= '9'; s++) v = v * 10 + (*s - '0');
    return neg ? -v : v;
}

static int cmp(const void *a, const void *b)
{
    const char *x = *(const char *const *)a, *y = *(const char *const *)b;
    int r;
    if (opt_n)
    {
        long nx = num_of(x), ny = num_of(y);
        r = (nx < ny) ? -1 : (nx > ny) ? 1 : strcmp(x, y);
    }
    else
        r = strcmp(x, y);
    return opt_r ? -r : r;
}

int main(int argc, char **argv)
{
    int argi = 1, i, rc = 0;
    while (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *f = argv[argi] + 1;
        for (; *f; f++)
        {
            if (*f == 'r') opt_r = 1;
            else if (*f == 'n') opt_n = 1;
            else if (*f == 'u') opt_u = 1;
        }
        argi++;
    }

    char *buf = NULL;
    long len = 0, cap = 0;
    if (argi >= argc)
        rc |= slurp(0, &buf, &len, &cap);
    else
        for (i = argi; i < argc; i++)
        {
            int fd = (argv[i][0] == '-' && argv[i][1] == '\0') ? 0 : open(argv[i], O_RDONLY);
            if (fd < 0) { rc = 1; continue; }
            rc |= slurp(fd, &buf, &len, &cap);
            if (fd != 0) close(fd);
        }

    /* Split into NUL-terminated lines (dropping the '\n'); a trailing partial line counts. */
    char **lines = NULL;
    long n = 0, lcap = 0, start = 0, k;
    for (k = 0; k <= len; k++)
        if (k == len || buf[k] == '\n')
        {
            if (k == len && k == start) break;          /* no trailing empty line */
            if (n == lcap)
            {
                lcap = lcap ? lcap * 2 : 64;
                char **nl = realloc(lines, (size_t)lcap * sizeof *lines);
                if (!nl) { free(lines); free(buf); return 1; }
                lines = nl;
            }
            buf[k] = '\0';
            lines[n++] = buf + start;
            start = k + 1;
        }

    qsort(lines, (size_t)n, sizeof *lines, cmp);

    const char *prev = NULL;
    for (k = 0; k < n; k++)
    {
        if (opt_u && prev && strcmp(prev, lines[k]) == 0) continue;
        write(1, lines[k], strlen(lines[k]));
        write(1, "\n", 1);
        prev = lines[k];
    }

    free(lines);
    free(buf);
    return rc;
}
