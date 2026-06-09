using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class HostCapTests
{
    [Fact]
    public void Writev_to_sink_mmap_delegates_and_exit_group_sets_code()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        var sink = new BufferSinkHandle();
        var proc = table.CreateRoot(SandboxToolBuilder.Build("hostcap"));
        proc.Fds.Set(1, sink);                       // capture the tool's stdout

        int rc = proc.Run(new[] { "hostcap" });

        Assert.Equal(5, rc);                                              // exit_group(5)
        Assert.Equal("sink-ok\nmmap-ok\n", Encoding.ASCII.GetString(sink.ToArray()));
        // sink-ok = writev reached the fd handle; mmap-ok = mmap delegated to Chibil.Pal
    }
}
