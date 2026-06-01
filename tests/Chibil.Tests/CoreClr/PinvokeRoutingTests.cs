using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PinvokeRoutingTests
{
    // Calls a libc fn on Linux and a kernel32 fn on Windows, gated on the OS flag.
    // Both return a positive process id -> map to 55. Proves per-symbol routing AND
    // lazy resolution: the wrong-OS stub is present but never called, so it never loads.
    const string Src =
        "int __chibil_os_is_windows(void); " +
        "int getpid(void); " +                       // libc
        "unsigned int GetCurrentProcessId(void); " + // kernel32
        "int main(void){ int id = __chibil_os_is_windows() ? (int)GetCurrentProcessId() : getpid();" +
        " return id > 0 ? 55 : 44; }";

    static byte[] Link(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        var map = new Dictionary<string, string> { ["getpid"] = "c", ["GetCurrentProcessId"] = "kernel32" };
        return LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), null, map);
    }

    [Fact]
    public void Routing_windows_kernel32()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DotnetHostRunner.RunPeViaDotnetHost(Link(Src), out string o);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Routing_linux_libc()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(Src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
