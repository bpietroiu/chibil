/* ls — managed coreutil (M5): list a directory's entries, one per line, sorted. Exercises
 * the PAL's directory path (opendir/readdir -> getdents64 -> DirHandle) rather than the
 * plain read/write of cat/wc/head/tail. Default dir is "."; hidden entries (leading '.')
 * are omitted unless -a is given, matching `ls -1`. Names are sorted with strcmp. */
#include <unistd.h>
#include <dirent.h>
#include <stdlib.h>
#include <string.h>

static int cmp(const void *a, const void *b)
{
    return strcmp(*(const char *const *)a, *(const char *const *)b);
}

/* List one directory. Returns 0, or 1 if it can't be opened. */
static int list_dir(const char *path, int all)
{
    DIR *d = opendir(path);
    if (!d) return 1;

    char **names = NULL;
    long n = 0, cap = 0;
    struct dirent *de;
    while ((de = readdir(d)) != NULL)
    {
        if (!all && de->d_name[0] == '.') continue;
        if (n == cap)
        {
            cap = cap ? cap * 2 : 16;
            char **nn = realloc(names, (size_t)cap * sizeof *names);
            if (!nn) { closedir(d); free(names); return 1; }
            names = nn;
        }
        names[n++] = strdup(de->d_name);
    }
    closedir(d);

    qsort(names, (size_t)n, sizeof *names, cmp);
    for (long i = 0; i < n; i++)
    {
        write(1, names[i], strlen(names[i]));
        write(1, "\n", 1);
        free(names[i]);
    }
    free(names);
    return 0;
}

int main(int argc, char **argv)
{
    int all = 0, argi = 1, rc = 0, i, listed = 0;

    while (argi < argc && argv[argi][0] == '-' && argv[argi][1] != '\0')
    {
        const char *f = argv[argi] + 1;
        for (; *f; f++) if (*f == 'a') all = 1;   /* other flags ignored */
        argi++;
    }

    if (argi >= argc) return list_dir(".", all);

    for (i = argi; i < argc; i++) { rc |= list_dir(argv[i], all); listed++; }
    (void)listed;
    return rc;
}
