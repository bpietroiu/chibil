using System.Collections.Generic;

namespace Chibil.Sandbox
{
    /// <summary>An in-memory filesystem tree for one sandbox. Directories map names to child
    /// nodes; files hold a <see cref="VfsFile"/>. Host code injects/extracts files directly;
    /// the kernel resolves paths (against a green-process's cwd) for open/mkdir.</summary>
    public sealed class VirtualFs
    {
        public const int ENOENT = 2;
        public const int EISDIR = 21;

        sealed class Node
        {
            public bool IsDir;
            public Dictionary<string, Node> Children;   // when IsDir
            public VfsFile File;                        // when file
        }

        readonly Node _root = new Node { IsDir = true, Children = new Dictionary<string, Node>() };
        readonly object _lock = new object();

        static string[] Split(string path, string cwd)
        {
            string full = path.StartsWith("/") ? path : (cwd.TrimEnd('/') + "/" + path);
            var stack = new List<string>();
            foreach (var p in full.Split('/'))
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(p);
            }
            return stack.ToArray();
        }

        Node ResolveNode(string[] comps, int count)
        {
            Node cur = _root;
            for (int i = 0; i < count; i++)
            {
                if (!cur.IsDir || cur.Children == null) return null;
                if (!cur.Children.TryGetValue(comps[i], out cur)) return null;
            }
            return cur;
        }

        // ── Host-side inject/extract ──────────────────────────────────────────────

        public void WriteFile(string path, byte[] bytes)
        {
            lock (_lock)
            {
                var f = CreateOrGetFile(Split(path, "/"));
                f.Truncate();
                f.WriteAt(0, bytes);
            }
        }

        public byte[] ReadFile(string path)
        {
            lock (_lock)
            {
                var comps = Split(path, "/");
                var n = ResolveNode(comps, comps.Length);
                if (n == null || n.IsDir) throw new System.IO.FileNotFoundException(path);
                return n.File.Snapshot();
            }
        }

        public bool Exists(string path)
        {
            lock (_lock) { var c = Split(path, "/"); return ResolveNode(c, c.Length) != null; }
        }

        /// <summary>Metadata for <paramref name="path"/>: returns false if it does not exist;
        /// otherwise sets whether it is a directory and its size (0 for dirs).</summary>
        public bool Stat(string path, string cwd, out bool isDir, out long size)
        {
            lock (_lock)
            {
                var comps = Split(path, cwd);
                var node = ResolveNode(comps, comps.Length);
                if (node == null) { isDir = false; size = 0; return false; }
                isDir = node.IsDir;
                size = node.IsDir ? 0 : node.File.Length;
                return true;
            }
        }

        public int Mkdir(string path, string cwd)
        {
            lock (_lock)
            {
                var comps = Split(path, cwd);
                if (comps.Length == 0) return 0;                 // "/" already exists
                Node parent = ResolveNode(comps, comps.Length - 1);
                if (parent == null || !parent.IsDir) return -ENOENT;
                string name = comps[comps.Length - 1];
                if (!parent.Children.ContainsKey(name))
                    parent.Children[name] = new Node { IsDir = true, Children = new Dictionary<string, Node>() };
                return 0;
            }
        }

        /// <summary>Entries of a directory (name, isDir), or null if not a directory.</summary>
        public (string name, bool isDir)[] ListDir(string path, string cwd)
        {
            lock (_lock)
            {
                var comps = Split(path, cwd);
                var node = ResolveNode(comps, comps.Length);
                if (node == null || !node.IsDir) return null;
                var list = new System.Collections.Generic.List<(string, bool)>();
                foreach (var kv in node.Children) list.Add((kv.Key, kv.Value.IsDir));
                return list.ToArray();
            }
        }

        // ── Kernel-facing open ────────────────────────────────────────────────────

        /// <summary>Open the file at <paramref name="path"/> (resolved against
        /// <paramref name="cwd"/>). Creates it when <paramref name="create"/>; truncates when
        /// <paramref name="truncate"/>. Returns the file, or null with a negative errno in
        /// <paramref name="err"/>.</summary>
        public VfsFile Open(string path, string cwd, bool create, bool truncate, out int err)
        {
            lock (_lock)
            {
                err = 0;
                var comps = Split(path, cwd);
                var node = ResolveNode(comps, comps.Length);
                if (node != null)
                {
                    if (node.IsDir) { err = -EISDIR; return null; }
                    if (truncate) node.File.Truncate();
                    return node.File;
                }
                if (!create) { err = -ENOENT; return null; }
                var f = CreateOrGetFile(comps);
                if (f == null) { err = -ENOENT; return null; }
                return f;
            }
        }

        VfsFile CreateOrGetFile(string[] comps)
        {
            if (comps.Length == 0) return null;                  // "/" is a dir
            Node parent = ResolveNode(comps, comps.Length - 1);
            if (parent == null || !parent.IsDir) return null;
            string name = comps[comps.Length - 1];
            if (parent.Children.TryGetValue(name, out var node))
                return node.IsDir ? null : node.File;
            var f = new VfsFile();
            parent.Children[name] = new Node { IsDir = false, File = f };
            return f;
        }
    }
}
