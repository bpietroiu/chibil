using System;
using System.IO;
using System.Reflection;

namespace Chibil.Sandbox
{
    /// <summary>One green-process: a tool's Main running in an isolated ToolLoadContext,
    /// with the SandboxPal current-process context set on the executing thread so the
    /// shared static Syscall resolves to this pid.</summary>
    public sealed class GreenProcess
    {
        readonly ToolLoadContext _alc;
        MethodInfo _main;

        public int Pid { get; }

        /// <summary>This green-process's file-descriptor table (the kernel resolves it via the
        /// current pid). Seed fds 0/1/2 here before Run to wire stdio/pipes.</summary>
        public FdTable Fds { get; } = new FdTable();

        public GreenProcess(int pid, string toolDllPath)
        {
            Pid = pid;
            _alc = new ToolLoadContext($"green-{pid}");
            // Load from memory (not LoadFromAssemblyPath) so the ALC never holds a file
            // handle: the dll stays overwritable by the next build, and the context can
            // unload cleanly (Task 5) without a lingering file lock.
            byte[] bytes = File.ReadAllBytes(toolDllPath);
            Assembly asm = _alc.LoadFromStream(new MemoryStream(bytes));
            _main = asm.EntryPoint
                ?? throw new InvalidOperationException($"tool '{toolDllPath}' has no entry point");
        }

        /// <summary>Run the tool to completion on the CALLING thread (so EnterProcess binds
        /// this thread to this pid). Returns the process exit code.</summary>
        public int Run(string[] args)
        {
            // Save/restore the calling thread's current pid: a child green-process may run
            // inline on this very thread (Task inlining during a parent's wait), so the
            // per-thread context must nest, not leak.
            int prev = SandboxPal.CurrentPid;
            SandboxPal.EnterProcess(Pid);
            try
            {
                object ret = _main.Invoke(null, new object[] { args });
                return ret is int code ? code : 0;
            }
            finally
            {
                Fds.CloseAll();   // exiting releases this process's fds (writer close -> reader EOF)
                SandboxPal.EnterProcess(prev);
            }
        }

        /// <summary>Initiate collectible unload of this green-process's load context and
        /// return a weak reference to it (for tests / pool reclamation). After this call the
        /// green-process is dead.</summary>
        public System.WeakReference Unload()
        {
            var weak = new System.WeakReference(_alc);
            _main = null;
            _alc.Unload();
            return weak;
        }
    }
}
