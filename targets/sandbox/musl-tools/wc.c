/* wc — managed coreutil (M5), the cat pattern applied to a stdin-consuming tool: it's
 * the natural right-hand side of a pipeline (`… | wc -l`). Counts lines/words/bytes of
 * each named file (or stdin if none), over low-level read/write/open on SandboxPal.
 *
 * Flags: -l lines, -w words, -c bytes (combinable). With no flag, all three are shown.
 * Output is the selected counts (l, w, c order) space-separated, then the filename when
 * one was given, then newline — and a trailing "total" line when multiple files are
 * given. Counts are not zero-padded (predictable for the sandbox harness). */
#include <unistd.h>
#include <fcntl.h>

static int want_l, want_w, want_c;

/* Count one open fd, accumulating into *l/*w/*c. Returns 0, or 1 on read error. */
static int count_fd(int fd, long *l, long *w, long *c)
{
    char buf[8192];
    ssize_t n;
    int in_word = 0;
    while ((n = read(fd, buf, sizeof buf)) > 0)
    {
        ssize_t i;
        for (i = 0; i < n; i++)
        {
            char ch = buf[i];
            (*c)++;
            if (ch == '\n') (*l)++;
            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f' || ch == '\v')
                in_word = 0;
            else if (!in_word) { in_word = 1; (*w)++; }
        }
    }
    return n < 0 ? 1 : 0;
}

/* Write a non-negative decimal to fd 1 (no stdio). */
static void put_long(long v)
{
    char tmp[24];
    int i = 0;
    if (v == 0) tmp[i++] = '0';
    while (v > 0) { tmp[i++] = (char)('0' + (v % 10)); v /= 10; }
    char out[24];
    int j = 0;
    while (i > 0) out[j++] = tmp[--i];
    write(1, out, (size_t)j);
}

static void put_str(const char *s)
{
    const char *p = s;
    while (*p) p++;
    write(1, s, (size_t)(p - s));
}

/* Emit the selected counts for one (l,w,c) triple, with an optional trailing name. */
static void report(long l, long w, long c, const char *name)
{
    int first = 1;
    if (want_l) { put_long(l); first = 0; }
    if (want_w) { if (!first) write(1, " ", 1); put_long(w); first = 0; }
    if (want_c) { if (!first) write(1, " ", 1); put_long(c); first = 0; }
    if (name) { write(1, " ", 1); put_str(name); }
    write(1, "\n", 1);
}

int main(int argc, char **argv)
{
    int i, rc = 0, nfiles = 0;
    long tl = 0, tw = 0, tc = 0;

    /* Parse leading -lwc flags. */
    int argi = 1;
    while (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *f = argv[argi] + 1;
        while (*f)
        {
            if (*f == 'l') want_l = 1;
            else if (*f == 'w') want_w = 1;
            else if (*f == 'c' || *f == 'm') want_c = 1;
            f++;
        }
        argi++;
    }
    if (!want_l && !want_w && !want_c) { want_l = want_w = want_c = 1; }

    if (argi >= argc)
    {
        long l = 0, w = 0, c = 0;
        rc |= count_fd(0, &l, &w, &c);
        report(l, w, c, (const char *)0);
        return rc;
    }

    for (i = argi; i < argc; i++)
    {
        long l = 0, w = 0, c = 0;
        int fd = open(argv[i], O_RDONLY);
        if (fd < 0) { rc = 1; continue; }
        rc |= count_fd(fd, &l, &w, &c);
        close(fd);
        report(l, w, c, argv[i]);
        tl += l; tw += w; tc += c; nfiles++;
    }
    if (nfiles > 1) report(tl, tw, tc, "total");
    return rc;
}
