using System.IO;
using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class MuslHelloTests
{
    // Build the tool first:  dotnet build targets/sandbox/SandboxMusl.proj -c Release
    [Fact]
    public void Musl_printf_program_runs_on_sandbox_pal()
    {
        string dll = Path.Combine(SandboxToolBuilder.RepoRoot(), "build", "bin", "sandbox", "hello.dll");
        if (!File.Exists(dll)) return;   // skip if the SandboxMusl tool hasn't been built

        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        var sink = new BufferSinkHandle();
        var proc = table.CreateRoot(dll);
        proc.Fds.Set(1, sink);           // capture stdout

        int rc = proc.Run(new[] { "hello" });

        // Real musl: printf -> malloc + stdio buffering -> writev(1) on flush, all on SandboxPal.
        Assert.Equal("hello, sandbox\n", Encoding.ASCII.GetString(sink.ToArray()));
        Assert.Equal(0, rc);
    }
}
