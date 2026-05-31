using System.Diagnostics;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// End-to-end acceptance for the self-contained CoreCLR path: compile C to a
/// pure-MSIL object, link it to a PE + runtimeconfig.json, then launch the
/// produced assembly through the real <c>dotnet</c> host and assert its exit
/// code. This exercises the host-bootstrap + runtimeconfig path that an
/// in-process <c>Assembly.Load</c> does not.
///
/// CoreCLR is the same runtime on Windows and Linux, so a green run here is
/// the Phase 0 acceptance; the Linux invocation is the identical
/// <c>samples/linux/build.sh ... &amp;&amp; dotnet app.dll</c> flow.
/// </summary>
public class LinuxEndToEndTests
{
    [Fact]
    public void Fib_pe_runs_via_dotnet_host_exit_55()
    {
        Assert.True(DotnetHostRunner.DotnetAvailable(), "dotnet host not available on PATH");

        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());

        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string output);
        Assert.True(exit == 55, $"expected exit 55, got {exit}. Output:\n{output}");
    }
}
