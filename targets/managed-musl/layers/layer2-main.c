/* Layer 2 (seam): call __chibil_syscall(SYS_write,...) directly; chibil-link --bind
   routes it to managed Chibil.Pal.Syscall, which writes to stdout. Expect exit 0. */
extern long __chibil_syscall(long, long, long, long, long, long, long);
int main(void) {
    const char *m = "hi from managed musl, via the PAL seam!\n";
    long n = 0; while (m[n]) n++;
    __chibil_syscall(1, 1, (long)m, n, 0, 0, 0);   /* SYS_write=1, fd=1=stdout */
    return 0;
}
