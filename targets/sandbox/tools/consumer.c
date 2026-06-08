/* Reads fd 0 until EOF, accumulating the byte count, then reports the total. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    char buf[64];
    long long total = 0;
    long long n;
    while ((n = __chibil_syscall(0 /* read */, 0 /* fd */, (long long)buf, 64, 0, 0, 0)) > 0)
        total += n;
    __chibil_syscall(0x1000 /* report */, total, 0, 0, 0, 0, 0);
    return 0;
}
