/* Writes a known 256-byte pattern 1000 times (256000 bytes) to fd 1.
 * `long long` for the 64-bit syscall ABI; pointer cast to long long carries the full
 * 64-bit buffer address for the kernel to deref. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    char buf[256];
    for (int i = 0; i < 256; i++) buf[i] = (char)i;
    for (int k = 0; k < 1000; k++)
        __chibil_syscall(1 /* write */, 1 /* fd */, (long long)buf, 256, 0, 0, 0);
    return 0;
}
