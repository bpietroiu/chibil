using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PositionedIoTests
{
    static readonly string[] Map =
    {
        "open=c","pread=c","pwrite=c","ftruncate=c","fsync=c","close=c","unlink=c","access=c","lseek=c",
        "CreateFileA=kernel32","ReadFile=kernel32","WriteFile=kernel32","SetFilePointerEx=kernel32",
        "SetEndOfFile=kernel32","FlushFileBuffers=kernel32","CloseHandle=kernel32","DeleteFileA=kernel32",
        "GetFileSizeEx=kernel32","GetFileAttributesA=kernel32",
    };
    internal static Dictionary<string,string> PinvokeMap()
    {
        var d = new Dictionary<string,string>();
        foreach (var e in Map) { int i = e.IndexOf('='); d[e[..i]] = e[(i+1)..]; }
        return d;
    }

    // Writes 0x41424344 at offset 8 to "pio.tmp", reads it back, returns 55 on match.
    const string Src = @"
#include ""chibil_os.h""
static long w_open(const char* p){
    if (__chibil_os_is_windows())
        return (long long)(void*)CreateFileA(p, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    return open(p, O_RDWR|O_CREAT|O_TRUNC, 420);
}
static int w_pwrite(long h, const void* b, unsigned int n, long long off){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; char* z=(char*)&ov; for(int i=0;i<(int)sizeof ov;i++) z[i]=0; ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32); unsigned int wr=0; return WriteFile((void*)h,b,n,&wr,&ov)&&wr==n?0:-1; }
    return pwrite((int)h,b,n,off)==(long)n?0:-1;
}
static int w_pread(long h, void* b, unsigned int n, long long off){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; char* z=(char*)&ov; for(int i=0;i<(int)sizeof ov;i++) z[i]=0; ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32); unsigned int rd=0; return ReadFile((void*)h,b,n,&rd,&ov)&&rd==n?0:-1; }
    return pread((int)h,b,n,off)==(long)n?0:-1;
}
static void w_close(long h){ if(__chibil_os_is_windows()) CloseHandle((void*)h); else close((int)h); }
int main(void){
    long h = w_open(""pio.tmp"");
    unsigned int v = 0x41424344u, r = 0;
    if (w_pwrite(h, &v, 4, 8) != 0) return 1;
    if (w_pread (h, &r, 4, 8) != 0) return 2;
    w_close(h);
    return r == 0x41424344u ? 55 : 44;
}";

    static byte[] Link()
    {
        string root = SqliteSmokeTests.RepoRootDir();
        string inc = Path.Combine(root, "samples", "sqlite", "include");
        string dir = Path.Combine(Path.GetTempPath(), "chibil_pio_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string c = Path.Combine(dir, "pio.c");
            File.WriteAllText(c, Src);
            byte[] obj = TestCompiler.CompileFileToObj(c, Chibil.TargetProfile.CoreClr, null, new[] { inc });
            var of = ObjectFile.Load(obj, "pio.obj");
            return LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), null, PinvokeMap());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Positioned_io_roundtrip_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(Link(), out string o, out _);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Positioned_io_roundtrip_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
