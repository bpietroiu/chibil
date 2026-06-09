using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

[Collection("SandboxKernel")]
public class GetDentsTests
{
    [Fact]
    public void Tool_lists_directory_entries_via_getdents64()
    {
        SandboxPal.Reset();
        var table = new ProcessTable();
        SandboxPal.AttachProcessTable(table);

        // /d with three entries: two files and a subdirectory.
        SandboxPal.Vfs.Mkdir("/d", "/");
        SandboxPal.Vfs.WriteFile("/d/a.txt", Encoding.ASCII.GetBytes("a"));
        SandboxPal.Vfs.WriteFile("/d/b.txt", Encoding.ASCII.GetBytes("bb"));
        SandboxPal.Vfs.Mkdir("/d/sub", "/");

        var proc = table.CreateRoot(SandboxToolBuilder.Build("getdents_tool"));
        int rc = proc.Run(new[] { "getdents_tool" });

        // The tool walks linux_dirent64 records by d_reclen and reports the count — 3 entries
        // proves open-as-DirHandle + getdents64 record layout (d_reclen@16) are correct.
        Assert.Equal(0, rc);
        Assert.Equal(3, SandboxPal.GetReport(proc.Pid));
    }
}
