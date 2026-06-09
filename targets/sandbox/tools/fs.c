/* Copies /in.txt to /out.txt through the virtual FS, reporting the byte count.
 * Exercises open (read-only and O_CREAT|O_WRONLY|O_TRUNC), read, write, close. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    char inpath[]  = "/in.txt";
    char outpath[] = "/out.txt";

    long long fdin  = __chibil_syscall(2 /*open*/, (long long)inpath,  0 /*O_RDONLY*/, 0, 0, 0, 0);
    /* O_WRONLY|O_CREAT|O_TRUNC = 1|0x40|0x200 = 0x241, mode 0777 */
    long long fdout = __chibil_syscall(2 /*open*/, (long long)outpath, 0x241, 0x1ff, 0, 0, 0);

    char buf[128];
    long long total = 0, n;
    while ((n = __chibil_syscall(0 /*read*/, fdin, (long long)buf, 128, 0, 0, 0)) > 0)
    {
        __chibil_syscall(1 /*write*/, fdout, (long long)buf, n, 0, 0, 0);
        total += n;
    }

    __chibil_syscall(3 /*close*/, fdin,  0, 0, 0, 0, 0);
    __chibil_syscall(3 /*close*/, fdout, 0, 0, 0, 0, 0);
    __chibil_syscall(0x1000 /*report*/, total, 0, 0, 0, 0, 0);
    return 0;
}
