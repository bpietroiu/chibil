using System;
using System.Threading;

namespace Chibil.Sandbox
{
    /// <summary>An open file description: the object an fd points at. Reference-counted so a
    /// shared description (e.g. via dup2) is only really closed when its last fd closes.</summary>
    public abstract class FileHandle
    {
        public const int EBADF = 9;

        int _refs = 1;

        public abstract int Read(Span<byte> dst);
        public abstract int Write(ReadOnlySpan<byte> src);

        /// <summary>Called once, when the last referencing fd is closed.</summary>
        protected abstract void OnLastClose();

        public void Ref() => Interlocked.Increment(ref _refs);
        public void Unref() { if (Interlocked.Decrement(ref _refs) == 0) OnLastClose(); }
    }

    /// <summary>The read end of a pipe.</summary>
    public sealed class PipeReadHandle : FileHandle
    {
        readonly Pipe _pipe;
        public PipeReadHandle(Pipe pipe) => _pipe = pipe;
        public override int Read(Span<byte> dst) => _pipe.Read(dst);
        public override int Write(ReadOnlySpan<byte> src) => -EBADF;   // not a writable end
        protected override void OnLastClose() => _pipe.CloseReader();
    }

    /// <summary>The write end of a pipe.</summary>
    public sealed class PipeWriteHandle : FileHandle
    {
        readonly Pipe _pipe;
        public PipeWriteHandle(Pipe pipe) => _pipe = pipe;
        public override int Read(Span<byte> dst) => -EBADF;            // not a readable end
        public override int Write(ReadOnlySpan<byte> src) => _pipe.Write(src);
        protected override void OnLastClose() => _pipe.CloseWriter();
    }
}
