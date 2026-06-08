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
            if (parent != null && fdMap != null)
                foreach (var (childFd, parentFd) in fdMap)
                {
                    var h = parent.Fds.Get(parentFd);
                    if (h == null) continue;        // parent fd not open — skip (child slot stays empty)
                    h.Ref();                         // shared between parent and child
                    gp.Fds.Set(childFd, h);          // Set consumes the ref we just took
                }
            _running[pid] = Task.Run(() => gp.Run(new[] { "child" }));
            return pid;
        }

        /// <summary>Block until `pid` exits; returns its exit code.</summary>
        public int Wait(int pid) => _running.TryGetValue(pid, out var t) ? t.GetAwaiter().GetResult() : 0;
    }
}
