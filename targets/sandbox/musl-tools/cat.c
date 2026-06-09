/* cat — the first managed coreutil (M5). A real managed-musl program that runs as a
 * green-process external: concatenate the named files (or stdin if none) to stdout,
 * using low-level read/write/open over SandboxPal. No stdio buffering needed. */
#include <unistd.h>
#include <fcntl.h>

static int cat_fd(int fd)
{
    char buf[8192];
    ssize_t n;
    while ((n = read(fd, buf, sizeof buf)) > 0)
    {
        char *p = buf;
        while (n > 0)
        {
            ssize_t w = write(1, p, (size_t)n);
            if (w <= 0) return 1;
            p += w; n -= w;
        }
    }
    return n < 0 ? 1 : 0;
}

int main(int argc, char **argv)
{
    int rc = 0, i;
    if (argc <= 1)
        return cat_fd(0);
    for (i = 1; i < argc; i++)
    {
        if (argv[i][0] == '-' && argv[i][1] == '\0')   /* "-" means stdin */
        {
            rc |= cat_fd(0);
            continue;
        }
        int fd = open(argv[i], O_RDONLY);
        if (fd < 0) { rc = 1; continue; }
        rc |= cat_fd(fd);
        close(fd);
    }
    return rc;
}
