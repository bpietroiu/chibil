using System.Collections.Generic;

namespace Chibil.Sandbox
{
    /// <summary>A green-process's file-descriptor table: fd (int) → open FileHandle. POSIX
    /// lowest-free-fd allocation; dup2 shares the underlying description (refcounted).</summary>
    public sealed class FdTable
    {
        readonly Dictionary<int, FileHandle> _fds = new Dictionary<int, FileHandle>();
        readonly object _lock = new object();

        /// <summary>Install <paramref name="handle"/> at the lowest free fd; returns that fd.
        /// Consumes the caller's reference (no extra Ref()).</summary>
        public int Add(FileHandle handle)
        {
            lock (_lock)
            {
                int fd = 0;
                while (_fds.ContainsKey(fd)) fd++;
                _fds[fd] = handle;
                return fd;
            }
        }

        /// <summary>Install <paramref name="handle"/> at an explicit fd, consuming the caller's
        /// reference (like Add) and closing whatever was there. Used to seed a green-process's
        /// stdio/pipe fds before it runs.</summary>
        public void Set(int fd, FileHandle handle)
        {
            lock (_lock)
            {
                if (_fds.TryGetValue(fd, out var old) && old != handle) old.Unref();
                _fds[fd] = handle;
            }
        }

        public FileHandle Get(int fd)
        {
            lock (_lock) { return _fds.TryGetValue(fd, out var h) ? h : null; }
        }

        /// <summary>Point <paramref name="newFd"/> at the same description as
        /// <paramref name="oldFd"/> (shared). Returns newFd, or -EBADF if oldFd is invalid.</summary>
        public int Dup2(int oldFd, int newFd)
        {
            lock (_lock)
            {
                if (!_fds.TryGetValue(oldFd, out var h)) return -FileHandle.EBADF;
                if (oldFd == newFd) return newFd;
                if (_fds.TryGetValue(newFd, out var old)) old.Unref();
                h.Ref();
                _fds[newFd] = h;
                return newFd;
            }
        }

        /// <summary>Close <paramref name="fd"/>; the underlying description is released when its
        /// last fd closes. Returns 0, or -EBADF if fd is not open.</summary>
        public int Close(int fd)
        {
            lock (_lock)
            {
                if (!_fds.TryGetValue(fd, out var h)) return -FileHandle.EBADF;
                _fds.Remove(fd);
                h.Unref();
                return 0;
            }
        }

        /// <summary>Close every open fd — called when a green-process exits so its pipe/file
        /// ends are released (a writer-end close gives the reader EOF).</summary>
        public void CloseAll()
        {
            lock (_lock)
            {
                foreach (var h in _fds.Values) h.Unref();
                _fds.Clear();
            }
        }
    }
}
