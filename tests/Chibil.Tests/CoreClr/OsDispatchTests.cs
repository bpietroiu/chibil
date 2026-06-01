using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class OsDispatchTests
{
    static byte[] Link(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
    }

    const string Src = "int __chibil_os_is_windows(void); " +
                       "int main(void){ return __chibil_os_is_windows() ? 55 : 44; }";

    [Fact]
    public void Os_is_windows_true_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DotnetHostRunner.RunPeViaDotnetHost(Link(Src), out string o);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Os_is_windows_false_on_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(Src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 44, $"linux expected 44, got {exit}. {o}");
    }
}
