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
        const long SYS_statx  = 332;
        // struct stat st_mode type bits + a default permission
        const uint S_IFREG = 0x8000, S_IFDIR = 0x4000, S_IFIFO = 0x1000;
        const long SYS_ioctl  = 16;
        const long SYS_rt_sigaction   = 13;
        const long SYS_rt_sigprocmask = 14;
        const long SYS_writev = 20;
        const long SYS_dup2   = 33;
        const long SYS_exit       = 60;
        const long SYS_wait4      = 61;
        const long SYS_exit_group = 231;
        const long WNOHANG = 1;
        const long ECHILD  = 10;
        const long SYS_openat = 257;
        const long SYS_pipe   = 22;
        const long SYS_pipe2  = 293;
        const long SYS_getcwd = 79;
        const long ENOTTY = 25;
        const long ERANGE = 34;

        // open() flags (x86-64 Linux)
        const long O_WRONLY = 1, O_RDWR = 2, O_CREAT = 0x40, O_TRUNC = 0x200;
        // Sandbox-internal calls for the spike harness.
        const long SYS_report = 0x1000;
        const long SYS_spawn  = 0x1001;
        const long SYS_wait   = 0x1002;
        const long SYS_spawn_self = 0x1003;
        const long SYS_spawn_tool = 0x1004;   // M5: spawn a registered managed external by name
        const long SYS_access    = 21;
        const long SYS_faccessat = 269;
        const long SYS_dup    = 32;
        const long SYS_dup3   = 292;
        const long SYS_fcntl  = 72;
        // fcntl commands (x86-64 Linux)
        const long F_DUPFD = 0, F_GETFD = 1, F_SETFD = 2, F_GETFL = 3, F_SETFL = 4, F_DUPFD_CLOEXEC = 1030;
        const long EINVAL = 22;
        const long ENOENT = 2;
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

        // Resolve a path to a (found, full st_mode, size) triple. Real vfs entries take
        // precedence; a registered managed external (a PATH dir's "cat") then appears as an
        // executable 0755 regular file so bash's PATH search/exec-bit check finds it.
        static bool ResolvePath(string path, out uint mode, out long size)
        {
            if (_vfs.Stat(path, CurrentProc().Cwd, out bool isDir, out size))
            {
                mode = (isDir ? S_IFDIR : S_IFREG) | (isDir ? 0x1EDu : 0x1A4u);
                return true;
            }
            if (_table != null && _table.IsTool(BaseName(path)))
            {
                mode = S_IFREG | 0x1EDu;                         // 0755 — executable
                size = 0;
                return true;
            }
            mode = 0; size = 0;
            return false;
        }

        static long DoStat(string path, long bufptr)
        {
            if (ResolvePath(path, out uint mode, out long size))
            {
                FillStat((byte*)bufptr, mode, size);
                return 0;
            }
            return -2;                                          // -ENOENT
        }

        // statx(dirfd, path, flags, mask, struct statx*) — musl 1.2.6's stat()/lstat()/fstatat()
        // funnel through SYS_statx on x86-64 (kstat time fields are 32-bit < 64-bit time_t), so
        // this, not SYS_stat, is what bash's PATH search actually issues. struct statx layout per
        // linux/stat.h: stx_mask@0 blksize@4 nlink@16 mode@28(u16) ino@32 size@40 (256 bytes).
        static long DoStatx(string path, long bufptr)
        {
            if (!ResolvePath(path, out uint mode, out long size))
                return -ENOENT;
            byte* b = (byte*)bufptr;
            for (int i = 0; i < 256; i++) b[i] = 0;
            *(uint*)(b + 0)    = 0x7ff;                          // stx_mask = STATX_BASIC_STATS
            *(uint*)(b + 4)    = 4096;                           // stx_blksize
            *(uint*)(b + 16)   = 1;                              // stx_nlink
            *(ushort*)(b + 28) = (ushort)mode;                  // stx_mode (type | perm)
            *(ulong*)(b + 32)  = 1;                              // stx_ino
            *(ulong*)(b + 40)  = (ulong)size;                   // stx_size
            return 0;
        }

        static string BaseName(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        static long DoAccess(string path)
        {
            if (_vfs.Stat(path, CurrentProc().Cwd, out _, out _)) return 0;
            if (_table != null && _table.IsTool(BaseName(path))) return 0;
            return -ENOENT;
        }

        /// <summary>Bind target for __chibil_get_tp (musl's TLS base / thread-control block).
        /// Returns the CURRENT green-process's own TCB so musl's thread-pointer-relative TLS
        /// (errno, locale, tsd/dtv, …) is isolated PER PROCESS — green-processes share pooled
        /// threads, so a per-thread TCB would bleed one process's TLS into another and corrupt
        /// it. Falls back to Chibil.Pal's per-thread TCB when no green-process is current.</summary>
        public static ulong GetTp()
        {
            var p = _table?.Get(_currentPid);
            return p != null && p.Tcb != IntPtr.Zero ? (ulong)p.Tcb : global::Chibil.Pal.GetTp();
        }

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
                case SYS_rt_sigaction:
                    // Non-interactive Layer-1: accept handler installs but don't deliver yet.
                    return 0;
                case SYS_rt_sigprocmask:
                    // Accept the mask change; report an empty previous mask if oldset is given.
                    if (a3 != 0)
                    {
                        int n2 = (int)a4;                       // sigsetsize
                        for (int i = 0; i < n2; i++) ((byte*)a3)[i] = 0;
                    }
                    return 0;
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
                case SYS_statx:
                    return DoStatx(ReadCString(a2), a5);    // a1=dirfd a2=path a3=flags a4=mask a5=buf
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
                case SYS_getcwd:
                {
                    // long getcwd(char *buf, size_t size): write cwd + NUL, return length
                    // INCLUDING the NUL (Linux ABI); -ERANGE if it doesn't fit.
                    byte[] cwd = System.Text.Encoding.UTF8.GetBytes(CurrentProc().Cwd);
                    if (cwd.Length + 1 > (long)a2) return -ERANGE;
                    byte* buf = (byte*)a1;
                    for (int i = 0; i < cwd.Length; i++) buf[i] = cwd[i];
                    buf[cwd.Length] = 0;
                    return cwd.Length + 1;
                }
                case SYS_close:
                    return CurrentFds().Close((int)a1);
                case SYS_dup:
                    return CurrentFds().DupFrom((int)a1, 0);
                case SYS_dup2:
                    return CurrentFds().Dup2((int)a1, (int)a2);
                case SYS_dup3:
                    // dup3(old, new, flags) — flags (O_CLOEXEC) are a no-op in the sandbox.
                    // EINVAL if old==new (the one case dup3 differs from dup2).
                    return a1 == a2 ? -EINVAL : CurrentFds().Dup2((int)a1, (int)a2);
                case SYS_fcntl:
                    // The descriptor-management subset bash needs. F_DUPFD(_CLOEXEC) is how bash
                    // saves an fd before redirecting over it (then restores via dup2) — without it,
                    // a redirect over stdout permanently closes it. CLOEXEC/fd-flags are no-ops in
                    // the single-image sandbox; F_GETFL reports a plain read/write description.
                    switch (a2)
                    {
                        case F_DUPFD:
                        case F_DUPFD_CLOEXEC:
                            return CurrentFds().DupFrom((int)a1, (int)a3);
                        case F_GETFD:
                        case F_SETFD:
                        case F_SETFL:
                            return CurrentFds().Get((int)a1) == null ? -EBADF : 0;
                        case F_GETFL:
                            return CurrentFds().Get((int)a1) == null ? -EBADF : O_RDWR;
                        default:
                            return CurrentFds().Get((int)a1) == null ? -EBADF : 0;
                    }
                case SYS_pipe:      // pipe(int[2])      — musl x86-64 uses this
                case SYS_pipe2:     // pipe2(int[2], flags) — flags (O_CLOEXEC/O_NONBLOCK) are no-ops here
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
                {
                    // a1 = toolId; a2 = int* of flat (childFd, parentFd) pairs; a3 = pair count.
                    int pairs = (int)a3;
                    var fdMap = new (int childFd, int parentFd)[pairs];
                    int* m = (int*)a2;
                    for (int i = 0; i < pairs; i++) fdMap[i] = (m[2 * i], m[2 * i + 1]);
                    return _table.Spawn((int)a1, CurrentProc(), fdMap);
                }
                case SYS_spawn_self:
                {
                    // Spawn the SAME image as the caller (bash) as a new green-process with an
                    // explicit argv and stdout/stdin wired to pipes — bash's comsub/subshell
                    // "re-exec self with -c <body>" on green-processes.
                    //   a1 = char** argv (NULL-terminated); a2 = int* (childFd,parentFd) pairs; a3 = pair count.
                    var argvList = new System.Collections.Generic.List<string>();
                    long* av = (long*)a1;
                    for (int i = 0; av[i] != 0; i++) argvList.Add(ReadCString(av[i]));
                    int pairs = (int)a3;
                    var fdMap = new (int childFd, int parentFd)[pairs];
                    int* m = (int*)a2;
                    for (int i = 0; i < pairs; i++) fdMap[i] = (m[2 * i], m[2 * i + 1]);
                    var self = CurrentProc();
                    return _table.SpawnImage(self.ToolDllPath, argvList.ToArray(), self, fdMap);
                }
                case SYS_spawn_tool:
                {
                    // Spawn a registered managed external (M5). a1 = char* name; a2 = char** argv
                    // (NULL-terminated); a3 = int* (childFd,parentFd) pairs; a4 = pair count.
                    string name = ReadCString(a1);
                    var argvList = new System.Collections.Generic.List<string>();
                    long* av = (long*)a2;
                    for (int i = 0; av[i] != 0; i++) argvList.Add(ReadCString(av[i]));
                    int pairs = (int)a4;
                    var fdMap = new (int childFd, int parentFd)[pairs];
                    int* m = (int*)a3;
                    for (int i = 0; i < pairs; i++) fdMap[i] = (m[2 * i], m[2 * i + 1]);
                    int tpid = _table.SpawnTool(name, argvList.ToArray(), CurrentProc(), fdMap);
                    return tpid < 0 ? -ENOENT : tpid;
                }
                case SYS_access:
                    return DoAccess(ReadCString(a1));
                case SYS_faccessat:
                    return DoAccess(ReadCString(a2));       // a1 = dirfd (AT_FDCWD)
                case SYS_wait:
                    return _table.Wait((int)a1);
                case SYS_wait4:
                {
                    // wait4(pid, int *status, options, rusage*) — backs bash's waitpid/waitchld.
                    // We honour pid (>0 or -1) + WNOHANG; other options/rusage are ignored.
                    int reaped = _table.WaitPid((int)a1, (a3 & WNOHANG) != 0, out int code);
                    if (reaped < 0) return -ECHILD;
                    if (reaped == 0) return 0;                       // WNOHANG, nothing ready
                    if (a2 != 0) *(int*)a2 = (code & 0xff) << 8;     // WIFEXITED + WEXITSTATUS(code)
                    return reaped;
                }
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
