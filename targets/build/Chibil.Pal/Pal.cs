using System;

namespace Chibil
{
    /// <summary>
    /// The managed PAL — the bottom edge of managed musl. Every libc syscall in musl
    /// bottoms out in <c>__chibil_syscall(n, a1..a6)</c>, bound (via chibil-link
    /// --bind) to <see cref="Syscall"/>. Returns the raw Linux ABI: >=0 success,
    /// -errno on error (musl's __syscall_ret turns that into errno + -1).
    ///
    /// L2 seam stub: just enough to prove the seam — write/exit. Everything else is
    /// -ENOSYS for now; later layers fill in mmap, openat/read, clock_gettime, ….
    /// </summary>
    public static unsafe class Pal
    {
        // x86-64 Linux syscall numbers (the subset implemented so far).
        const long SYS_read = 0;
        const long SYS_write = 1;
        const long SYS_ioctl = 16;
        const long SYS_writev = 20;
        const long SYS_mmap = 9;
        const long SYS_mprotect = 10;
        const long SYS_munmap = 11;
        const long SYS_madvise = 28;
        const long SYS_exit = 60;
        const long SYS_exit_group = 231;
        const long SYS_getrandom = 318;

        const long ENOSYS = 38;
        const long MAP_ANONYMOUS = 0x20;

        // Cached stdin stream — reading from a fresh OpenStandardInput() each call would
        // drop the host's input buffering (the REPL reads one byte at a time).
        static System.IO.Stream _stdin;

        public static long Syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            switch (n)
            {
                case SYS_read:
                {
                    // ssize_t read(int fd, void *buf, size_t count) — stdin only.
                    if ((int)a1 != 0) return -ENOSYS;
                    _stdin ??= Console.OpenStandardInput();
                    var dst = new Span<byte>((void*)a2, (int)a3);
                    return _stdin.Read(dst);   // 0 = EOF
                }
                case SYS_write:
                {
                    // ssize_t write(int fd, const void *buf, size_t count)
                    int fd = (int)a1;
                    var src = new ReadOnlySpan<byte>((void*)a2, (int)a3);
                    var stream = fd == 2 ? Console.OpenStandardError() : Console.OpenStandardOutput();
                    stream.Write(src);
                    stream.Flush();
                    return a3;   // bytes written
                }
                case SYS_writev:
                {
                    // ssize_t writev(int fd, const struct iovec *iov, int iovcnt)
                    // iovec = { void *iov_base; size_t iov_len; } — 16 bytes each.
                    var stream = (int)a1 == 2 ? Console.OpenStandardError() : Console.OpenStandardOutput();
                    long total = 0;
                    byte* iov = (byte*)a2;
                    for (int i = 0; i < (int)a3; i++)
                    {
                        ulong* e = (ulong*)(iov + i * 16);
                        ulong basep = e[0], len = e[1];
                        if (len != 0) { stream.Write(new ReadOnlySpan<byte>((void*)basep, (int)len)); total += (long)len; }
                    }
                    stream.Flush();
                    return total;
                }
                case SYS_mmap:
                {
                    // void *mmap(void *addr, size_t length, int prot, int flags, int fd, off_t off)
                    if (((int)a4 & MAP_ANONYMOUS) == 0) return -ENOSYS;   // only anonymous for now
                    nuint len = (nuint)a2;
                    void* p = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(len, 4096);
                    System.Runtime.InteropServices.NativeMemory.Fill(p, len, 0);  // anon mmap is zero-filled
                    return (long)p;
                }
                case SYS_munmap:
                    System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)a1);
                    return 0;
                case SYS_ioctl:
                    // 0 = "success": musl's __stdout_write treats this as a tty and
                    // keeps stdout LINE-buffered, so printf("…\n") flushes promptly
                    // (the managed crt doesn't run musl's atexit stdio flush yet).
                    return 0;
                case SYS_madvise:
                    return 0;   // advisory only — safe no-op
                case SYS_mprotect:
                    return 0;   // managed memory has no page protection — no-op (mallocng hardening)
                case SYS_getrandom:
                {
                    // ssize_t getrandom(void *buf, size_t buflen, unsigned int flags)
                    var dst = new Span<byte>((void*)a1, (int)a2);
                    System.Security.Cryptography.RandomNumberGenerator.Fill(dst);
                    return a2;
                }
                case SYS_exit:
                case SYS_exit_group:
                    Environment.Exit((int)a1);
                    return 0;
            }
            return -ENOSYS;
        }

        /// <summary>musl's thread pointer (TLS base). L2 stub: one zeroed control block
        /// per thread — enough for the cancel/errno reads on the simple paths.</summary>
        // Returns uintptr_t (UInt64) to match the C `__chibil_get_tp` extern exactly —
        // the bind resolves by signature, so the return type must agree.
        [ThreadStatic] static IntPtr _tcb;
        public static ulong GetTp()
        {
            if (_tcb == IntPtr.Zero)
                _tcb = (IntPtr)System.Runtime.InteropServices.NativeMemory.AllocZeroed(4096);
            return (ulong)_tcb;
        }
    }
}
