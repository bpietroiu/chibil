using System;
using System.Collections.Concurrent;

namespace Chibil.Sandbox
{
    /// <summary>
    /// The in-process sandbox micro-kernel. Loaded ONCE in the default load context and
    /// shared by every green-process (which runs in its own AssemblyLoadContext). Tools
    /// bind <c>__chibil_syscall</c> to <see cref="Syscall"/>. Shared kernel state lives in
    /// static fields here; per-green-process state is resolved via the [ThreadStatic]
    /// current pid set by <see cref="EnterProcess"/> before a green-process runs.
    /// </summary>
    public static unsafe class SandboxPal
    {
        // Shared kernel state (singleton across all green-processes).
        static readonly ConcurrentDictionary<int, long> _reports = new ConcurrentDictionary<int, long>();

        // Per-green-process context: which pid the calling thread is running as.
        [ThreadStatic] static int _currentPid;

        // Process table, attached once by the host.
        static ProcessTable _table;

        // The sandbox's virtual filesystem (one per sandbox process).
        static VirtualFs _vfs = new VirtualFs();
        public static VirtualFs Vfs => _vfs;

        // Real Linux x86-64 syscall numbers (so tools — and eventually bash — bind unchanged).
        const long SYS_read   = 0;
        const long SYS_write  = 1;
        const long SYS_open   = 2;
        const long SYS_close  = 3;
        const long SYS_lseek  = 8;
        const long SYS_ioctl  = 16;
        const long SYS_writev = 20;
        const long SYS_dup2   = 33;
        const long SYS_exit       = 60;
        const long SYS_exit_group = 231;
        const long SYS_openat = 257;
        const long SYS_pipe2  = 293;
        const long ENOTTY = 25;

        // open() flags (x86-64 Linux)
        const long O_WRONLY = 1, O_RDWR = 2, O_CREAT = 0x40, O_TRUNC = 0x200;
        // Sandbox-internal calls for the spike harness.
        const long SYS_report = 0x1000;
        const long SYS_spawn  = 0x1001;
        const long SYS_wait   = 0x1002;
        const long ENOSYS = 38;
        const long EBADF  = 9;

        static GreenProcess CurrentProc() => _table.Get(_currentPid);
        static FdTable CurrentFds() => _table.Get(_currentPid).Fds;

        static string ReadCString(long ptr)
        {
            byte* p = (byte*)ptr;
            int len = 0;
            while (p[len] != 0) len++;
            return System.Text.Encoding.UTF8.GetString(p, len);
        }

        static long DoOpen(string path, long flags)
        {
            var proc = CurrentProc();
            bool create = (flags & O_CREAT) != 0;
            bool trunc  = (flags & O_TRUNC) != 0;
            long acc = flags & 3;                       // O_RDONLY=0 / O_WRONLY=1 / O_RDWR=2
            bool writable = acc == O_WRONLY || acc == O_RDWR;
            bool readable = acc == 0 || acc == O_RDWR;
            VfsFile file = _vfs.Open(path, proc.Cwd, create, trunc, out int err);
            if (file == null) return err;               // negative errno
            return proc.Fds.Add(new VfsFileHandle(file, readable, writable));
        }

        /// <summary>Bind target for __chibil_get_tp (no TLS pointer needed in the spike).</summary>
        public static ulong GetTp() => 0;

        /// <summary>The pid the calling thread is currently running as (0 if none).</summary>
        public static int CurrentPid => _currentPid;

        /// <summary>Set the current green-process for the calling thread. Call before running a tool's Main.</summary>
        public static void EnterProcess(int pid) => _currentPid = pid;

        /// <summary>Attach the host's process table so spawn/wait syscalls can reach it.</summary>
        public static void AttachProcessTable(ProcessTable t) => _table = t;

        /// <summary>Bind target for __chibil_syscall. Linux x86-64 syscall ABI.</summary>
        public static long Syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            switch (n)
            {
                case SYS_read:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h == null) return -EBADF;
                    return h.Read(new Span<byte>((void*)a2, (int)a3));
                }
                case SYS_write:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h == null) return -EBADF;
                    return h.Write(new ReadOnlySpan<byte>((void*)a2, (int)a3));
                }
                case SYS_writev:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h == null) return -EBADF;
                    long total = 0;
                    byte* iov = (byte*)a2;                       // struct iovec { void* base; size_t len } (16 bytes)
                    for (int i = 0; i < (int)a3; i++)
                    {
                        ulong* e = (ulong*)(iov + i * 16);
                        ulong basep = e[0], len = e[1];
                        if (len == 0) continue;
                        int w = h.Write(new ReadOnlySpan<byte>((void*)basep, (int)len));
                        if (w < 0) return total > 0 ? total : w;
                        total += w;
                    }
                    return total;
                }
                case SYS_ioctl:
                    return -ENOTTY;                              // sandbox fds are never ttys
                case SYS_exit:
                case SYS_exit_group:
                    throw new GreenProcessExit((int)a1);        // terminate this green-process, not the host
                case SYS_open:
                    return DoOpen(ReadCString(a1), a2);
                case SYS_openat:
                    return DoOpen(ReadCString(a2), a3);     // a1 = dirfd (treated as AT_FDCWD)
                case SYS_lseek:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h is VfsFileHandle vf) return vf.Seek(a2, (int)a3);
                    return -EBADF;
                }
                case SYS_close:
                    return CurrentFds().Close((int)a1);
                case SYS_dup2:
                    return CurrentFds().Dup2((int)a1, (int)a2);
                case SYS_pipe2:
                {
                    var pipe = new Pipe();
                    var fds = CurrentFds();
                    int rfd = fds.Add(new PipeReadHandle(pipe));
                    int wfd = fds.Add(new PipeWriteHandle(pipe));
                    int* outFds = (int*)a1;        // int pipefd[2]
                    outFds[0] = rfd; outFds[1] = wfd;
                    return 0;
                }
                case SYS_report:
                    _reports[_currentPid] = a1;
                    return 0;
                case SYS_spawn:
                    return _table.Spawn((int)a1);
                case SYS_wait:
                    return _table.Wait((int)a1);
            }
            // Pure memory / random / misc syscalls (mmap, mprotect, munmap, madvise,
            // getrandom, ...) reuse the proven compute PAL. NOTE: SandboxPal handles
            // read/write/writev/exit above, so those never reach here (Chibil.Pal would
            // route them to the host Console / Environment.Exit).
            return global::Chibil.Pal.Syscall(n, a1, a2, a3, a4, a5, a6);
        }

        // Test/inspection surface (kernel-side).
        public static long GetReport(int pid) => _reports[pid];
        public static int ReportCount => _reports.Count;
        public static void Reset() { _reports.Clear(); _vfs = new VirtualFs(); }
    }
}
