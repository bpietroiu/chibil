using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class FsIntegrationTests
{
    [Fact]
    public void Tool_reads_injected_file_and_writes_one_the_host_extracts()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        byte[] content = Encoding.ASCII.GetBytes("the quick brown fox jumps\n");
        SandboxPal.Vfs.WriteFile("/in.txt", content);          // host injects an input file

        var fs = table.CreateRoot(SandboxToolBuilder.Build("fs"));
        int rc = fs.Run(new[] { "fs" });

        Assert.Equal(0, rc);
        Assert.Equal(content.Length, (int)SandboxPal.GetReport(fs.Pid));   // bytes copied
        Assert.Equal(content, SandboxPal.Vfs.ReadFile("/out.txt"));         // host extracts the output
    }
}
