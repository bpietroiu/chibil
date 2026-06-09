/* head — managed coreutil (M5): print the first N lines (default 10) of each file, or of
 * stdin when no file is given. The natural front of a pipeline (`cat /f | head -2`). Uses
 * low-level read/write/open over SandboxPal; counts newlines in a streaming buffer and
 * stops once N have been written (so it never reads more than it needs to emit). */
#include <unistd.h>
#include <fcntl.h>

/* Emit lines from fd until `limit` newlines have been written; returns 0, or 1 on error.
 * A trailing partial line (no final newline) is emitted too, as GNU head does. */
static int head_fd(int fd, long limit)
{
    char buf[8192];
    ssize_t n;
    long lines = 0;
    while (lines < limit && (n = read(fd, buf, sizeof buf)) > 0)
    {
        ssize_t start = 0, i;
        for (i = 0; i < n && lines < limit; i++)
            if (buf[i] == '\n')
            {
                lines++;
                if (lines == limit)
                {
                    ssize_t len = i + 1 - start;
                    if (write(1, buf + start, (size_t)len) != len) return 1;
                    start = i + 1;
                }
            }
        if (lines < limit && start < n)
        {
            ssize_t len = n - start;
            if (write(1, buf + start, (size_t)len) != len) return 1;
        }
    }
    return n < 0 ? 1 : 0;
}

/* Parse a small non-negative decimal; returns -1 on a non-numeric string. */
static long parse_long(const char *s)
{
    long v = 0;
    if (!*s) return -1;
    for (; *s; s++)
    {
        if (*s < '0' || *s > '9') return -1;
        v = v * 10 + (*s - '0');
    }
    return v;
}

int main(int argc, char **argv)
{
    long limit = 10;
    int argi = 1, rc = 0, i;

    /* -n N , -nN , or the -NUM shorthand (GNU/BSD `head -2`). */
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

    if (argi >= argc) return head_fd(0, limit);

    for (i = argi; i < argc; i++)
    {
        int fd = (argv[i][0] == '-' && argv[i][1] == '\0') ? 0 : open(argv[i], O_RDONLY);
        if (fd < 0) { rc = 1; continue; }
        rc |= head_fd(fd, limit);
        if (fd != 0) close(fd);
    }
    return rc;
}
