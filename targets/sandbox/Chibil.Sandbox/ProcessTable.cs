using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Chibil.Sandbox
{
    /// <summary>pid → green-process registry + tool registry. Owns pid allocation and the
    /// spawn/wait primitives the kernel exposes as syscalls.</summary>
    public sealed class ProcessTable
    {
        readonly ConcurrentDictionary<int, GreenProcess> _procs = new ConcurrentDictionary<int, GreenProcess>();
        readonly ConcurrentDictionary<int, string> _tools = new ConcurrentDictionary<int, string>();   // tool id → dll path
        readonly ConcurrentDictionary<int, Task<int>> _running = new ConcurrentDictionary<int, Task<int>>();
        readonly ConcurrentDictionary<int, int> _parent = new ConcurrentDictionary<int, int>();         // child pid → parent pid
        readonly ConcurrentDictionary<int, byte> _reaped = new ConcurrentDictionary<int, byte>();       // pids already reaped by waitpid
        int _nextPid;

        public void RegisterTool(int id, string toolDllPath) => _tools[id] = toolDllPath;

        /// <summary>The green-process with this pid (the kernel uses it to resolve fd tables).</summary>
        public GreenProcess Get(int pid) => _procs.TryGetValue(pid, out var gp) ? gp : null;

        public GreenProcess CreateRoot(string toolDllPath)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var gp = new GreenProcess(pid, toolDllPath);
            _procs[pid] = gp;
            return gp;
        }

        /// <summary>Spawn tool `toolId` as a new green-process running on a background thread.
        /// Returns the child's pid.</summary>
        public int Spawn(int toolId) => Spawn(toolId, null, null);

        /// <summary>Spawn tool `toolId`, inheriting fds from <paramref name="parent"/> per
        /// <paramref name="fdMap"/> — a list of (childFd, parentFd) the kernel duplicates into
        /// the child's table before it runs (the posix_spawn file-actions analog). Each inherited
        /// description is shared (ref-counted), so the child and parent see the same pipe/file.</summary>
        public int Spawn(int toolId, GreenProcess parent, System.Collections.Generic.IReadOnlyList<(int childFd, int parentFd)> fdMap)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var gp = new GreenProcess(pid, _tools[toolId]);
            _procs[pid] = gp;
            _parent[pid] = parent?.Pid ?? 0;
            if (parent != null && fdMap != null)
                foreach (var (childFd, parentFd) in fdMap)
                {
                    var h = parent.Fds.Get(parentFd);
                    if (h == null) continue;        // parent fd not open — skip (child slot stays empty)
                    h.Ref();                         // shared between parent and child
                    gp.Fds.Set(childFd, h);          // Set consumes the ref we just took
                }
            _running[pid] = RunOnDedicatedThread(gp, new[] { "child" });
            return pid;
        }

        /// <summary>Spawn a specific <paramref name="dllPath"/> image as a new green-process with
        /// an explicit <paramref name="args"/> (argv) and fd inheritance from <paramref name="parent"/>
        /// per <paramref name="fdMap"/>. This is the green-process analog of bash's "re-exec self with
        /// -c &lt;body&gt;": comsub/subshell spawn the SAME bash image, wiring the child's stdout to a
        /// pipe the parent reads. Returns the child's pid.</summary>
        public int SpawnImage(string dllPath, string[] args, GreenProcess parent,
            System.Collections.Generic.IReadOnlyList<(int childFd, int parentFd)> fdMap)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var gp = new GreenProcess(pid, dllPath);
            if (parent != null) gp.Cwd = parent.Cwd;
            _procs[pid] = gp;
            _parent[pid] = parent?.Pid ?? 0;
            if (parent != null && fdMap != null)
                foreach (var (childFd, parentFd) in fdMap)
                {
                    var h = parent.Fds.Get(parentFd);
                    if (h == null) continue;
                    h.Ref();
                    gp.Fds.Set(childFd, h);
                }
            _running[pid] = RunOnDedicatedThread(gp, args);
            return pid;
        }

        /// <summary>Run a green-process on its OWN dedicated thread (not the ThreadPool). Nested
        /// comsub/pipeline spawn-and-block-wait chains (each parent blocks in WaitPid until its
        /// child finishes) would otherwise pin ThreadPool threads and, worse, let the scheduler
        /// INLINE a child onto a waiting parent's thread — both corrupt deeply-nested green-process
        /// trees. A TaskCompletionSource (no delegate) can't be inlined, so the wait is a clean
        /// cross-thread block.</summary>
        static Task<int> RunOnDedicatedThread(GreenProcess gp, string[] args)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                try { tcs.SetResult(gp.Run(args)); }
                catch (System.Exception e) { tcs.SetException(e); }
            })
            { IsBackground = true, Name = $"green-{gp.Pid}" };
            t.Start();
            return tcs.Task;
        }

        /// <summary>Block until `pid` exits; returns its exit code.</summary>
        public int Wait(int pid) => _running.TryGetValue(pid, out var t) ? t.GetAwaiter().GetResult() : 0;

        /// <summary>waitpid(2) emulation over green-processes (backs SandboxPal's SYS_wait4, which
        /// backs bash's wait_for/waitchld reaping). Reaps a child of <paramref name="parentPid"/>:
        ///   <paramref name="pid"/> &gt; 0 — that specific child; pid == -1 — any child.
        /// When <paramref name="noHang"/>, returns 0 if no matching child has exited yet (each child
        /// is reaped at most once). Returns the reaped pid + its exit <paramref name="code"/>, 0 for
        /// WNOHANG-with-none, or -1 (ECHILD) when no matching unreaped child exists at all.</summary>
        public int WaitPid(int pid, bool noHang, out int code)
        {
            code = 0;
            if (pid > 0)
            {
                if (_reaped.ContainsKey(pid) || !_running.TryGetValue(pid, out var t)
                    || !_parent.TryGetValue(pid, out int pp) || pp != CallerPid())
                    return -1;   // ECHILD: not our unreaped child
                if (noHang && !t.IsCompleted) return 0;
                code = t.GetAwaiter().GetResult();
                _reaped[pid] = 1;
                return pid;
            }

            // pid == -1: any child of the caller.
            var mine = new System.Collections.Generic.List<(int p, Task<int> t)>();
            foreach (var kv in _parent)
                if (kv.Value == CallerPid() && !_reaped.ContainsKey(kv.Key) && _running.TryGetValue(kv.Key, out var rt))
                    mine.Add((kv.Key, rt));
            if (mine.Count == 0) return -1;   // ECHILD

            while (true)
            {
                foreach (var (p, t) in mine)
                    if (t.IsCompleted) { code = t.GetAwaiter().GetResult(); _reaped[p] = 1; return p; }
                if (noHang) return 0;
                Task.WaitAny(mine.ConvertAll(x => (Task)x.t).ToArray());
            }
        }

        // The caller's pid resolves through the kernel's per-thread current pid.
        int CallerPid() => SandboxPal.CurrentPid;
    }
}
