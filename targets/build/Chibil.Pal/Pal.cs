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
        const long SYS_write = 1;
        const long SYS_exit = 60;
        const long SYS_exit_group = 231;

        const long ENOSYS = 38;

        public static long Syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            switch (n)
            {
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
                case SYS_exit:
                case SYS_exit_group:
                    Environment.Exit((int)a1);
                    return 0;
            }
            return -ENOSYS;
        }

        /// <summary>musl's thread pointer (TLS base). L2 stub: one zeroed control block
        /// per thread — enough for the cancel/errno reads on the simple paths.</summary>
        [ThreadStatic] static IntPtr _tcb;
        public static long GetTp()
        {
            if (_tcb == IntPtr.Zero)
                _tcb = (IntPtr)System.Runtime.InteropServices.NativeMemory.AllocZeroed(4096);
            return (long)_tcb;
        }
    }
}
