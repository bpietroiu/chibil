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
            SandboxPal.EnterProcess(Pid);
            object ret = _main.Invoke(null, new object[] { args });
            return ret is int code ? code : 0;
        }
    }
}
