/* Exercises the host-capable kernel: writev to fd 1 (a sink), an mmap that delegates to
 * the compute PAL, and exit_group terminating the green-process with a code. Freestanding. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    /* mmap one anon page — delegated to Chibil.Pal; returns a real pointer (> 0). */
    long long p = __chibil_syscall(9 /*mmap*/, 0, 4096, 3 /*PROT_RW*/, 0x22 /*MAP_ANON|PRIVATE*/, -1, 0);

    char a[] = "sink-ok\n";
    long long iov[2];
    iov[0] = (long long)a; iov[1] = 8;
    __chibil_syscall(20 /*writev*/, 1, (long long)iov, 1, 0, 0, 0);

    if (p > 0)
    {
        char b[] = "mmap-ok\n";
        iov[0] = (long long)b; iov[1] = 8;
        __chibil_syscall(20 /*writev*/, 1, (long long)iov, 1, 0, 0, 0);
    }

    __chibil_syscall(231 /*exit_group*/, 5, 0, 0, 0, 0, 0);
    return 99; /* unreachable: exit_group unwinds the green-process */
}
