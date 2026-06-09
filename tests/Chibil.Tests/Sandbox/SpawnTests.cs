using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class SpawnTests
{
    [Fact]
    public void Parent_spawns_child_with_piped_stdout_and_drains_it()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        // Tool id 2 = producer: writes 256000 bytes to its fd 1.
        table.RegisterTool(id: 2, toolDllPath: SandboxToolBuilder.Build("producer"));
        SandboxPal.AttachProcessTable(table);

        var parent = table.CreateRoot(SandboxToolBuilder.Build("pipe_spawner"));
        int rc = parent.Run(new[] { "pipe_spawner" });

        // pipe_spawner: pipe2 -> (rfd,wfd); spawn(producer) mapping CHILD fd1 = parent wfd;
        // close its own wfd; drain rfd to EOF; wait child; report total bytes read.
        // 256000 proves spawn wired the child's stdout into the parent's pipe write-end
        // (the green-process analog of a shell pipeline stage) and EOF/wait work.
        Assert.Equal(0, rc);
        Assert.Equal(256_000, SandboxPal.GetReport(parent.Pid));
    }
}
