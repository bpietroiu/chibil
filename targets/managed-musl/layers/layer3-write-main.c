/* Layer 3a: real musl write() (cancellable syscall_cp path -> exercises the TCB).
   Links write.c + syscall_ret + __errno_location + pal-shim. Expect exit 0. */
typedef unsigned long size_t;
typedef long ssize_t;
ssize_t write(int, const void *, size_t);
int main(void) {
    const char *m = "real musl write()!\n";
    long n = 0; while (m[n]) n++;
    write(1, m, n);
    return 0;
}
