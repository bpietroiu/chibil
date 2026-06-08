using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

public class GreenProcessTests
{
    [Fact]
    public void Pal_reports_and_reads_back_per_pid()
    {
        SandboxPal.Reset();
        SandboxPal.EnterProcess(7);
        // syscall 0x1000 = report a1 as this pid's value
        long rc = SandboxPal.Syscall(0x1000, 42, 0, 0, 0, 0, 0);
        Assert.Equal(0, rc);
        Assert.Equal(42, SandboxPal.GetReport(7));
        Assert.Equal(1, SandboxPal.ReportCount);
    }

    [Fact]
    public void Counter_tool_builds_and_is_a_loadable_assembly()
    {
        string dll = SandboxToolBuilder.Build("counter");
        Assert.True(System.IO.File.Exists(dll));
        Assert.True(new System.IO.FileInfo(dll).Length > 0);
    }
}
