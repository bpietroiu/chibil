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
    private const string RuntimeConfig =
        "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"rollForward\": \"Major\",\n" +
        "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" }\n  }\n}\n";

    private static bool DotnetAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dotnet", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static int RunPeViaDotnetHost(byte[] pe, out string stdout)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dll = Path.Combine(dir, "app.dll");
            File.WriteAllBytes(dll, pe);
            File.WriteAllText(Path.Combine(dir, "app.runtimeconfig.json"), RuntimeConfig);

            using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            stdout = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
            if (err.Length > 0) stdout += "\n[stderr] " + err;
            return p.ExitCode;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Fib_pe_runs_via_dotnet_host_exit_55()
    {
        Assert.True(DotnetAvailable(), "dotnet host not available on PATH");

        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());

        int exit = RunPeViaDotnetHost(pe, out string output);
        Assert.True(exit == 55, $"expected exit 55, got {exit}. Output:\n{output}");
    }
}
