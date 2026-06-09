using System;
using System.Threading;

namespace Chibil.Sandbox
{
    /// <summary>
    /// A bounded in-memory byte pipe — the managed equivalent of an anonymous pipe. A
    /// blocking ring buffer: writers block when full (backpressure), readers block when
    /// empty and see EOF (0) once all write-ends are closed; a write with no read-ends
    /// returns -EPIPE. Shared by green-processes via their fd tables.
    /// </summary>
    public sealed class Pipe
    {
        public const int EPIPE = 32;
        public const int Capacity = 65536;     // ring-buffer size; reported by fcntl(F_GETPIPE_SZ)

        readonly byte[] _buf;
        readonly object _lock = new object();
        int _head;      // index of the next byte to read
        int _count;     // bytes currently buffered
        int _writers = 1;
        int _readers = 1;

        public Pipe(int capacity = Capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buf = new byte[capacity];
        }

        /// <summary>Write all of <paramref name="data"/>, blocking for space as needed.
        /// Returns the count written, or -EPIPE if all readers are gone.</summary>
        public int Write(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                int written = 0;
                while (written < data.Length)
                {
                    while (_count == _buf.Length && _readers > 0) Monitor.Wait(_lock);
                    if (_readers == 0) return written > 0 ? written : -EPIPE;

                    int tail = (_head + _count) % _buf.Length;
                    int free = _buf.Length - _count;
                    int run = Math.Min(free, _buf.Length - tail);     // contiguous space to the buffer end
                    int take = Math.Min(run, data.Length - written);
                    data.Slice(written, take).CopyTo(_buf.AsSpan(tail, take));
                    _count += take;
                    written += take;
                    Monitor.PulseAll(_lock);
                }
                return written;
            }
        }

        /// <summary>Read up to <paramref name="dst"/>.Length bytes, blocking until data is
        /// available. Returns 0 at EOF (buffer drained and all writers closed).</summary>
        public int Read(Span<byte> dst)
        {
            if (dst.Length == 0) return 0;
            lock (_lock)
            {
                while (_count == 0 && _writers > 0) Monitor.Wait(_lock);
                if (_count == 0) return 0;   // EOF

                int run = Math.Min(_count, _buf.Length - _head);     // contiguous data to the buffer end
                int take = Math.Min(run, dst.Length);
                _buf.AsSpan(_head, take).CopyTo(dst.Slice(0, take));
                _head = (_head + take) % _buf.Length;
                _count -= take;
                Monitor.PulseAll(_lock);
                return take;
            }
        }

        public void CloseWriter() { lock (_lock) { if (_writers > 0) _writers--; Monitor.PulseAll(_lock); } }
        public void CloseReader() { lock (_lock) { if (_readers > 0) _readers--; Monitor.PulseAll(_lock); } }
        public void AddWriter()   { lock (_lock) { _writers++; } }
        public void AddReader()   { lock (_lock) { _readers++; } }
    }
}
