using System.Text;
using Chibil.Sandbox;
using Xunit;

namespace Chibil.Tests.Sandbox;

// Pure data structures (no kernel singleton) — safe to run in parallel.
public class VirtualFsTests
{
    [Fact]
    public void Write_then_read_round_trips()
    {
        var fs = new VirtualFs();
        fs.WriteFile("/a.txt", Encoding.ASCII.GetBytes("hello fs"));
        Assert.True(fs.Exists("/a.txt"));
        Assert.Equal("hello fs", Encoding.ASCII.GetString(fs.ReadFile("/a.txt")));
    }

    [Fact]
    public void Mkdir_then_nested_file()
    {
        var fs = new VirtualFs();
        Assert.Equal(0, fs.Mkdir("/src", "/"));
        fs.WriteFile("/src/main.c", Encoding.ASCII.GetBytes("int main(){}"));
        Assert.True(fs.Exists("/src/main.c"));
        Assert.Equal("int main(){}", Encoding.ASCII.GetString(fs.ReadFile("/src/main.c")));
    }

    [Fact]
    public void File_handle_writes_seeks_reads()
    {
        var f = new VfsFile();
        var h = new VfsFileHandle(f, readable: true, writable: true);

        Assert.Equal(10, h.Write(Encoding.ASCII.GetBytes("0123456789")));
        Assert.Equal(3, h.Seek(3, 0));            // SEEK_SET to offset 3
        var buf = new byte[4];
        Assert.Equal(4, h.Read(buf));
        Assert.Equal("3456", Encoding.ASCII.GetString(buf));
        Assert.Equal(10, h.Seek(0, 2));           // SEEK_END == length
    }
}
