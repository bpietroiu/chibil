/* Stats a host-injected file and dir, verifying the x86-64 struct stat layout (st_mode@24,
 * st_size@48). Reports 0 on success, or a nonzero code identifying the first mismatch. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    char buf[144];
    char fpath[] = "/file.txt";
    char dpath[] = "/dir";

    /* stat("/file.txt") — host injected 5 bytes; expect a regular file of size 5. */
    if (__chibil_syscall(4 /*stat*/, (long long)fpath, (long long)buf, 0, 0, 0, 0) != 0)
        { __chibil_syscall(0x1000, 1, 0, 0, 0, 0, 0); return 1; }
    unsigned int fmode = *(unsigned int *)(buf + 24);
    long long fsize = *(long long *)(buf + 48);
    if ((fmode & 0xF000) != 0x8000) { __chibil_syscall(0x1000, 2, 0, 0, 0, 0, 0); return 2; } /* not S_IFREG */
    if (fsize != 5)                 { __chibil_syscall(0x1000, 3, 0, 0, 0, 0, 0); return 3; } /* wrong size  */

    /* stat("/dir") — host mkdir'd; expect a directory. */
    if (__chibil_syscall(4 /*stat*/, (long long)dpath, (long long)buf, 0, 0, 0, 0) != 0)
        { __chibil_syscall(0x1000, 4, 0, 0, 0, 0, 0); return 4; }
    unsigned int dmode = *(unsigned int *)(buf + 24);
    if ((dmode & 0xF000) != 0x4000) { __chibil_syscall(0x1000, 5, 0, 0, 0, 0, 0); return 5; } /* not S_IFDIR */

    __chibil_syscall(0x1000 /*report*/, 0, 0, 0, 0, 0, 0);   /* all checks passed */
    return 0;
}
