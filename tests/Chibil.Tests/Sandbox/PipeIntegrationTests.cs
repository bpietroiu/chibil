using System.Threading.Tasks;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class PipeIntegrationTests
{
    [Fact]
    public void Two_green_processes_stream_bytes_through_a_pipe()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        var pipe = new Pipe();   // default 64 KiB — smaller than the 256 KB payload, so it
                                 // backpressures and the reader must keep up: real flow control.
        var producer = table.CreateRoot(SandboxToolBuilder.Build("producer"));
        producer.Fds.Set(1, new PipeWriteHandle(pipe));   // producer's fd 1 (stdout) -> pipe
        var consumer = table.CreateRoot(SandboxToolBuilder.Build("consumer"));
        consumer.Fds.Set(0, new PipeReadHandle(pipe));    // consumer's fd 0 (stdin)  <- pipe

        Task pt = Task.Run(() => producer.Run(new[] { "producer" }));
        Task ct = Task.Run(() => consumer.Run(new[] { "consumer" }));
        Task.WaitAll(pt, ct);

        // The consumer read exactly the 256000 bytes the producer wrote — every byte, in
        // order, through a backpressured 64 KiB pipe, across two green-processes.
        Assert.Equal(256_000, SandboxPal.GetReport(consumer.Pid));
    }
}
