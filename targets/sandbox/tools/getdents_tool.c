/* Opens a host-injected directory and walks getdents64 records, counting entries.
 * Reports the entry count (0 = failure marker is impossible — empty dir is itself a check).
 * The linux_dirent64 layout: d_ino@0(8) d_off@8(8) d_reclen@16(2) d_type@18(1) d_name@19. */
extern long long __chibil_syscall(long long n, long long a1, long long a2,
                                  long long a3, long long a4, long long a5, long long a6);

int main(int argc, char **argv)
{
    char dpath[] = "/d";
    char buf[1024];

    long long fd = __chibil_syscall(2 /*open*/, (long long)dpath, 0, 0, 0, 0, 0);
    if (fd < 0) { __chibil_syscall(0x1000, 1000, 0, 0, 0, 0, 0); return 1; }

    /* getdents64(fd, buf, sizeof buf) — read all records in one shot (dir is small). */
    long long n = __chibil_syscall(217 /*getdents64*/, fd, (long long)buf, sizeof buf, 0, 0, 0);
    if (n < 0) { __chibil_syscall(0x1000, 1001, 0, 0, 0, 0, 0); return 2; }

    /* Walk the records by d_reclen, counting entries. */
    int count = 0;
    long long off = 0;
    while (off < n) {
        unsigned short reclen = *(unsigned short *)(buf + off + 16);
        if (reclen == 0) break;
        count++;
        off += reclen;
    }

    __chibil_syscall(0x1000 /*report*/, count, 0, 0, 0, 0, 0);
    return 0;
}
