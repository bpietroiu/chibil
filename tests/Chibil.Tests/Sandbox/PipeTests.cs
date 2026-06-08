using System.Text;
using System.Threading.Tasks;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

public class PipeTests
{
    [Fact]
    public void Read_gets_written_bytes_then_eof_when_writer_closes()
    {
        var pipe = new Pipe(capacity: 64);
        byte[] msg = Encoding.ASCII.GetBytes("hello pipe");

        Assert.Equal(msg.Length, pipe.Write(msg));
        pipe.CloseWriter();

        var got = new byte[64];
        int n = pipe.Read(got);
        Assert.Equal("hello pipe", Encoding.ASCII.GetString(got, 0, n));

        // All data drained + writer closed -> EOF (0).
        Assert.Equal(0, pipe.Read(got));
    }

    [Fact]
    public void Write_backpressures_until_reader_drains_a_full_buffer()
    {
        var pipe = new Pipe(capacity: 16);          // smaller than the payload
        byte[] payload = new byte[100_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0x7f);

        // Writer fills past capacity repeatedly; only completes because the reader drains.
        Task<int> writer = Task.Run(() => { int w = pipe.Write(payload); pipe.CloseWriter(); return w; });

        var sink = new System.IO.MemoryStream();
        var chunk = new byte[7];                     // odd size to exercise wrap-around
        int r;
        while ((r = pipe.Read(chunk)) > 0) sink.Write(chunk, 0, r);

        Assert.Equal(payload.Length, writer.GetAwaiter().GetResult());
        Assert.Equal(payload, sink.ToArray());       // every byte, in order, through a 16-byte buffer
    }

    [Fact]
    public void Write_to_pipe_with_no_readers_returns_epipe()
    {
        var pipe = new Pipe(capacity: 64);
        pipe.CloseReader();
        Assert.Equal(-Pipe.EPIPE, pipe.Write(Encoding.ASCII.GetBytes("x")));
    }
}
