using System;

namespace Chibil.Sandbox
{
    /// <summary>The content of a regular file in the virtual FS: a growable, seekable byte
    /// store. Shared by every fd that has the file open (offsets live in the fd handle).</summary>
    public sealed class VfsFile
    {
        byte[] _data = Array.Empty<byte>();
        int _len;
        readonly object _lock = new object();

        public int Length { get { lock (_lock) return _len; } }

        /// <summary>Read up to dst.Length bytes from <paramref name="off"/>; 0 past EOF.</summary>
        public int ReadAt(long off, Span<byte> dst)
        {
            lock (_lock)
            {
                if (off < 0 || off >= _len) return 0;
                int n = (int)Math.Min(dst.Length, _len - off);
                new ReadOnlySpan<byte>(_data, (int)off, n).CopyTo(dst);
                return n;
            }
        }

        /// <summary>Write src at <paramref name="off"/>, growing the file as needed. Returns the
        /// count written (== src.Length).</summary>
        public int WriteAt(long off, ReadOnlySpan<byte> src)
        {
            lock (_lock)
            {
                long end = off + src.Length;
                if (end > _data.Length)
                {
                    int cap = _data.Length == 0 ? 64 : _data.Length;
                    while (cap < end) cap *= 2;
                    Array.Resize(ref _data, cap);
                }
                src.CopyTo(_data.AsSpan((int)off));
                if (end > _len) _len = (int)end;
                return src.Length;
            }
        }

        public void Truncate() { lock (_lock) _len = 0; }

        public byte[] Snapshot()
        {
            lock (_lock) { var b = new byte[_len]; Array.Copy(_data, b, _len); return b; }
        }
    }
}
