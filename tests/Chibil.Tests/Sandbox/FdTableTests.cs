using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

public class FdTableTests
{
    [Fact]
    public void Add_allocates_lowest_free_fd()
    {
        var t = new FdTable();
        var pipe = new Pipe();
        Assert.Equal(0, t.Add(new PipeWriteHandle(pipe)));
        Assert.Equal(1, t.Add(new PipeWriteHandle(pipe)));
        t.Close(0);
        Assert.Equal(0, t.Add(new PipeWriteHandle(pipe)));   // freed slot reused
    }

    [Fact]
    public void Dup2_shares_description_and_survives_until_last_fd_closes()
    {
        var pipe = new Pipe(capacity: 64);
        var t = new FdTable();
        int wfd = t.Add(new PipeWriteHandle(pipe));          // fd 0, writer ref = 1

        t.Dup2(wfd, 5);                                       // fd 5 -> same write end
        Assert.Same(t.Get(wfd), t.Get(5));

        t.Close(wfd);                                         // one fd gone, writer still open via fd 5
        Assert.Null(t.Get(wfd));

        // Writer end is still open: a write succeeds (not -EPIPE / not EOF for a reader).
        Assert.Equal(1, t.Get(5).Write(new byte[] { 42 }));

        t.Close(5);                                          // last writer fd -> pipe writer closed
        var rh = new PipeReadHandle(pipe);
        var buf = new byte[8];
        int n = rh.Read(buf);                                // drains the one buffered byte
        Assert.Equal(1, n);
        Assert.Equal(42, buf[0]);
        Assert.Equal(0, rh.Read(buf));                       // then EOF, proving the writer was closed
    }

    [Fact]
    public void Close_invalid_fd_is_ebadf()
    {
        var t = new FdTable();
        Assert.Equal(-FileHandle.EBADF, t.Close(3));
    }
}
