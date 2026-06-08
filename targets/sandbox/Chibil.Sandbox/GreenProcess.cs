using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Chibil.Sandbox
{
    /// <summary>Thrown by the exit/exit_group syscall to unwind a green-process to completion
    /// with its exit code (instead of terminating the host process).</summary>
    public sealed class GreenProcessExit : Exception
    {
        public int Code { get; }
        public GreenProcessExit(int code) => Code = code;
    }

    /// <summary>One green-process: a tool's Main running in an isolated ToolLoadContext,
    /// with the SandboxPal current-process context set on the executing thread so the
    /// shared static Syscall resolves to this pid.</summary>
    public sealed unsafe class GreenProcess
    {
        readonly ToolLoadContext _alc;
        MethodInfo _main;     // synthesized `int Main(string[])` (fallback entry)
        MethodInfo _cMain;    // the C `int main(int,char**,char**)` — invoked directly so the
                              // green-process supplies its OWN argv/envp (the synthesized Main
                              // marshals from the shared host Environment, wrong in-process).
        MethodInfo _rtInit;   // optional managed-crt startup (locale/auxv); null for tools that lack it

        public int Pid { get; }

        /// <summary>Path of the image this green-process runs — so a child can re-spawn the
        /// SAME image (bash's comsub/subshell "re-exec self" analog on green-processes).</summary>
        public string ToolDllPath { get; }

        /// <summary>This green-process's musl thread-control block (TLS base for __chibil_get_tp).
        /// Per-PROCESS, not per-thread: green-processes share pooled threads, so a per-thread TCB
        /// would let one process inherit another's musl TLS (tsd/dtv/locale) → intermittent
        /// corruption. Allocated for the duration of <see cref="Run"/>.</summary>
        public IntPtr Tcb { get; private set; }

        /// <summary>This green-process's file-descriptor table (the kernel resolves it via the
        /// current pid). Seed fds 0/1/2 here before Run to wire stdio/pipes.</summary>
        public FdTable Fds { get; } = new FdTable();

        /// <summary>Current working directory, for resolving relative paths.</summary>
        public string Cwd { get; set; } = "/";

        /// <summary>The process environment handed to the tool's <c>main</c> as <c>envp</c>
        /// (each entry "KEY=VALUE"). Defaults to empty; the host seeds it before Run.</summary>
        public string[] Environ { get; set; } = Array.Empty<string>();

        public GreenProcess(int pid, string toolDllPath)
        {
            Pid = pid;
            ToolDllPath = toolDllPath;
            _alc = new ToolLoadContext($"green-{pid}");
            // Load from memory (not LoadFromAssemblyPath) so the ALC never holds a file
            // handle: the dll stays overwritable by the next build, and the context can
            // unload cleanly (Task 5) without a lingering file lock.
            byte[] bytes = File.ReadAllBytes(toolDllPath);
            Assembly asm = _alc.LoadFromStream(new MemoryStream(bytes));
            _main = asm.EntryPoint
                ?? throw new InvalidOperationException($"tool '{toolDllPath}' has no entry point");
            // C functions land as global methods on <Module>. Resolve the managed-crt init
            // (__chibil_rt_init: locale/auxv before main) and the C main itself (invoked
            // directly so this green-process supplies its own argv/envp).
            foreach (var m in asm.ManifestModule.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "__chibil_rt_init") _rtInit = m;
                else if (m.Name == "main") _cMain = m;
            }
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
            EnterExec();
            var owned = new List<IntPtr>();
            Tcb = (IntPtr)NativeMemory.AllocZeroed(4096);   // this process's musl TLS base
            try
            {
                _rtInit?.Invoke(null, null);   // managed-crt: locale/auxv before main
                object ret = _cMain != null
                    ? InvokeCMain(args ?? Array.Empty<string>(), owned)
                    : _main.Invoke(null, new object[] { args });
                return ret is int code ? code : 0;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is GreenProcessExit ex)
            {
                return ex.Code;   // the tool called exit_group(code)
            }
            finally
            {
                foreach (var p in owned) NativeMemory.Free((void*)p);   // argv/envp backing memory
                if (Tcb != IntPtr.Zero) { NativeMemory.Free((void*)Tcb); Tcb = IntPtr.Zero; }
                Fds.CloseAll();   // exiting releases this process's fds (writer close -> reader EOF)
                SandboxPal.EnterProcess(prev);
                ExitExec(this);   // retire; unload all retired ALCs once the system is fully idle
            }
        }

        // ── Collectible-ALC lifecycle safety ──────────────────────────────────────────────
        // ROOT CAUSE (proven via crash-dump + bisection): a green-process runs a chibil image in
        // its own COLLECTIBLE AssemblyLoadContext. If the (background) GC UNLOADS a completed
        // green-process's ALC while ANOTHER green-process is concurrently executing chibil-JITted
        // code, it corrupts runtime/native state — the executing image's bash heap gets a wild
        // pointer (AV in find_shell_builtin/vfprintf) or the CLR raises ExecutionEngineException.
        // FIX: every green-process is kept REFERENCED (so the GC cannot unload it) until the system
        // is FULLY IDLE — no green-process executing (_executing == 0) — then all completed ones
        // unload together at that safe point. ALCs stay collectible, so memory is still reclaimed;
        // they just never unload mid-execution. (_executing hits 0 only when the root and its whole
        // comsub/pipeline subtree have finished, since each parent stays executing — blocked in
        // WaitPid — until its children exit.)
        static int _executing;
        static readonly List<GreenProcess> _retired = new();
        static readonly object _retireLock = new();

        static void EnterExec() => System.Threading.Interlocked.Increment(ref _executing);

        static void ExitExec(GreenProcess gp)
        {
            lock (_retireLock) _retired.Add(gp);
            if (System.Threading.Interlocked.Decrement(ref _executing) == 0)
                DrainRetired();
        }

        /// <summary>Unload every retired green-process's ALC and reclaim it. Only called when
        /// _executing == 0 (a true safe point — no chibil-JITted code on any thread), so the
        /// unload + GC cannot race a concurrently-executing image.</summary>
        static void DrainRetired()
        {
            GreenProcess[] dead;
            lock (_retireLock)
            {
                if (_retired.Count == 0) return;
                dead = _retired.ToArray();
                _retired.Clear();
            }
            foreach (var gp in dead) gp.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>Invoke the C <c>int main(int argc, char** argv[, char** envp])</c> directly,
        /// marshalling this green-process's <paramref name="argv"/> and <see cref="Environ"/> into
        /// native char** vectors. Backing memory is recorded in <paramref name="owned"/> and freed
        /// after main returns (bash copies argv/env into its own heap during startup).</summary>
        object InvokeCMain(string[] argv, List<IntPtr> owned)
        {
            var ps = _cMain.GetParameters();
            if (ps.Length == 0) return _cMain.Invoke(null, null);   // int main(void)

            var call = new object[ps.Length];
            call[0] = argv.Length;                                  // argc
            call[1] = System.Reflection.Pointer.Box((void*)MakeVec(argv, owned), ps[1].ParameterType);
            if (ps.Length >= 3)
                call[2] = System.Reflection.Pointer.Box((void*)MakeVec(Environ, owned), ps[2].ParameterType);
            return _cMain.Invoke(null, call);
        }

        /// <summary>Build a NUL-terminated native char** from <paramref name="items"/>: an array of
        /// (items.Length + 1) pointers, each to a NUL-terminated UTF-8 copy. All allocations are
        /// appended to <paramref name="owned"/> for later free.</summary>
        static IntPtr MakeVec(string[] items, List<IntPtr> owned)
        {
            IntPtr* vec = (IntPtr*)NativeMemory.Alloc((nuint)((items.Length + 1) * IntPtr.Size));
            owned.Add((IntPtr)vec);
            for (int i = 0; i < items.Length; i++)
            {
                byte[] b = Encoding.UTF8.GetBytes(items[i]);
                byte* s = (byte*)NativeMemory.Alloc((nuint)(b.Length + 1));
                owned.Add((IntPtr)s);
                for (int j = 0; j < b.Length; j++) s[j] = b[j];
                s[b.Length] = 0;
                vec[i] = (IntPtr)s;
            }
            vec[items.Length] = IntPtr.Zero;
            return (IntPtr)vec;
        }

        /// <summary>Initiate collectible unload of this green-process's load context and
        /// return a weak reference to it (for tests / pool reclamation). After this call the
        /// green-process is dead.</summary>
        public System.WeakReference Unload()
        {
            var weak = new System.WeakReference(_alc);
            if (!_unloaded) { _unloaded = true; _main = null; _cMain = null; _rtInit = null; _alc.Unload(); }
            return weak;
        }
        bool _unloaded;
    }
}
