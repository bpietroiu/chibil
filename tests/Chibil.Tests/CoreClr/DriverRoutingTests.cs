using Chibil;
using System.Linq;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class DriverRoutingTests
{
    [Fact]
    public void CoreClr_link_command_targets_chibil_link_with_libs()
    {
        var driver = new Driver();
        // Drive the seam: configure options for CoreCLR + a -lc lib, then build the command.
        string[] cmd = driver.BuildChibilLinkCommandForTest(
            target: TargetProfile.CoreClr,
            libs: new() { "c" },
            inputs: new() { "a.obj", "b.obj" },
            output: "app.dll");

        Assert.Equal("chibil-link", cmd[0]);
        Assert.Contains("-o", cmd);
        Assert.Contains("app.dll", cmd);
        Assert.Contains("-lc", cmd);
        Assert.Contains("a.obj", cmd);
        Assert.Contains("b.obj", cmd);
        Assert.DoesNotContain(cmd, s => s.Contains("link.exe"));
        Assert.DoesNotContain(cmd, s => s.Contains("mscoree"));
    }
}
