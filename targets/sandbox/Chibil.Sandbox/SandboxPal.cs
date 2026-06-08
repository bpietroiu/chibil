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
    public static class SandboxPal
    {
        // Shared kernel state (singleton across all green-processes).
        static readonly ConcurrentDictionary<int, long> _reports = new ConcurrentDictionary<int, long>();

        // Per-green-process context: which pid the calling thread is running as.
        [ThreadStatic] static int _currentPid;

        // Process table, attached once by the host.
        static ProcessTable _table;

        const long SYS_report = 0x1000;
        const long SYS_spawn  = 0x1001;
        const long SYS_wait   = 0x1002;
        const long ENOSYS = 38;

        /// <summary>Bind target for __chibil_get_tp (no TLS pointer needed in the spike).</summary>
        public static ulong GetTp() => 0;

        /// <summary>Set the current green-process for the calling thread. Call before running a tool's Main.</summary>
        public static void EnterProcess(int pid) => _currentPid = pid;

        /// <summary>Attach the host's process table so spawn/wait syscalls can reach it.</summary>
        public static void AttachProcessTable(ProcessTable t) => _table = t;

        /// <summary>Bind target for __chibil_syscall. Linux x86-64 syscall ABI.</summary>
        public static long Syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
        {
            switch (n)
            {
                case SYS_report:
                    _reports[_currentPid] = a1;
                    return 0;
                case SYS_spawn:
                    return _table.Spawn((int)a1);
                case SYS_wait:
                    return _table.Wait((int)a1);
            }
            return -ENOSYS;
        }

        // Test/inspection surface (kernel-side).
        public static long GetReport(int pid) => _reports[pid];
        public static int ReportCount => _reports.Count;
        public static void Reset() => _reports.Clear();
    }
}
