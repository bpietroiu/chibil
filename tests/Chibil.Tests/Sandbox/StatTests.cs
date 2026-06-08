using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class StatTests
{
    [Fact]
    public void Tool_stats_file_and_dir_with_correct_mode_and_size()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        SandboxPal.Vfs.WriteFile("/file.txt", Encoding.ASCII.GetBytes("hello"));   // 5 bytes
        SandboxPal.Vfs.Mkdir("/dir", "/");

        var proc = table.CreateRoot(SandboxToolBuilder.Build("stat_tool"));
        int rc = proc.Run(new[] { "stat_tool" });

        // The tool self-verifies S_IFREG + size 5 for the file and S_IFDIR for the dir,
        // reading st_mode@24 / st_size@48 — so report 0 proves the struct stat layout.
        Assert.Equal(0, rc);
        Assert.Equal(0, SandboxPal.GetReport(proc.Pid));
    }
}
