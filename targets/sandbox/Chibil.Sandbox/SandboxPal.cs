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
        const long SYS_stat   = 4;
        const long SYS_fstat  = 5;
        const long SYS_lstat  = 6;
        const long SYS_lseek  = 8;
        const long SYS_getdents64 = 217;
        const long SYS_newfstatat = 262;
        // struct stat st_mode type bits + a default permission
        const uint S_IFREG = 0x8000, S_IFDIR = 0x4000, S_IFIFO = 0x1000;
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
            // A directory open (e.g. opendir) returns a DirHandle for getdents64.
            if (_vfs.Stat(path, proc.Cwd, out bool isDir, out _) && isDir)
                return proc.Fds.Add(new DirHandle(_vfs.ListDir(path, proc.Cwd)));
            bool create = (flags & O_CREAT) != 0;
            bool trunc  = (flags & O_TRUNC) != 0;
            long acc = flags & 3;                       // O_RDONLY=0 / O_WRONLY=1 / O_RDWR=2
            bool writable = acc == O_WRONLY || acc == O_RDWR;
            bool readable = acc == 0 || acc == O_RDWR;
            VfsFile file = _vfs.Open(path, proc.Cwd, create, trunc, out int err);
            if (file == null) return err;               // negative errno
            return proc.Fds.Add(new VfsFileHandle(file, readable, writable));
        }

        /// <summary>getdents64: fill <paramref name="bufptr"/> with linux_dirent64 records from
        /// the directory <paramref name="dh"/>, advancing its cursor. Returns bytes written, or 0
        /// at end of directory. Layout: d_ino@0(8) d_off@8(8) d_reclen@16(2) d_type@18(1) d_name@19.</summary>
        static long DoGetDents(DirHandle dh, long bufptr, long bufsize)
        {
            byte* buf = (byte*)bufptr;
            long written = 0;
            while (dh.Pos < dh.Entries.Length)
            {
                var (name, isDir) = dh.Entries[dh.Pos];
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
                int reclen = (19 + nameBytes.Length + 1 + 7) & ~7;   // 8-byte aligned, NUL-terminated
                if (written + reclen > bufsize) break;               // no room — stop (caller calls again)
                byte* rec = buf + written;
                *(long*)(rec + 0)  = dh.Pos + 1;                     // d_ino (any nonzero)
                *(long*)(rec + 8)  = dh.Pos + 1;                     // d_off (next cursor)
                *(ushort*)(rec + 16) = (ushort)reclen;              // d_reclen
                rec[18] = (byte)(isDir ? 4 : 8);                    // d_type: DT_DIR=4 / DT_REG=8
                for (int i = 0; i < nameBytes.Length; i++) rec[19 + i] = nameBytes[i];
                rec[19 + nameBytes.Length] = 0;                     // NUL
                written += reclen;
                dh.Pos++;
            }
            return written;
        }

        /// <summary>Fill an x86-64 Linux `struct stat` (144 bytes): st_mode@24, st_size@48
        /// are what bash/ls actually read; the rest is zeroed with sane st_ino/nlink/blksize.</summary>
        static void FillStat(byte* buf, uint mode, long size)
        {
            for (int i = 0; i < 144; i++) buf[i] = 0;
            *(long*)(buf + 8)  = 1;        // st_ino
            *(long*)(buf + 16) = 1;        // st_nlink
            *(uint*)(buf + 24) = mode;     // st_mode
            *(long*)(buf + 48) = size;     // st_size
            *(long*)(buf + 56) = 4096;     // st_blksize
        }

        static long DoStat(string path, long bufptr)
        {
            if (!_vfs.Stat(path, CurrentProc().Cwd, out bool isDir, out long size))
                return -2;                                  // -ENOENT
            FillStat((byte*)bufptr, (isDir ? S_IFDIR : S_IFREG) | (isDir ? 0x1EDu : 0x1A4u), size);
            return 0;
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
                case SYS_stat:
                case SYS_lstat:
                    return DoStat(ReadCString(a1), a2);
                case SYS_newfstatat:
                    return DoStat(ReadCString(a2), a3);     // a1 = dirfd (AT_FDCWD)
                case SYS_fstat:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h == null) return -EBADF;
                    if (h is VfsFileHandle vf) FillStat((byte*)a2, S_IFREG | 0x1A4u, vf.File.Length);
                    else FillStat((byte*)a2, S_IFIFO | 0x1A4u, 0);   // pipe/sink -> FIFO
                    return 0;
                }
                case SYS_lseek:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h is VfsFileHandle vf) return vf.Seek(a2, (int)a3);
                    return -EBADF;
                }
                case SYS_getdents64:
                {
                    var h = CurrentFds().Get((int)a1);
                    if (h is DirHandle dh) return DoGetDents(dh, a2, a3);
                    return -EBADF;                              // -ENOTDIR would need a distinct fd kind
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
