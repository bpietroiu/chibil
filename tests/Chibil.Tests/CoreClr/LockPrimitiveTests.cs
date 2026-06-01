using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LockPrimitiveTests
{
    const string Src = @"
#include ""chibil_os.h""
static long w_open(const char* p){
    if (__chibil_os_is_windows())
        return (long long)(void*)CreateFileA(p, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    return open(p, O_RDWR|O_CREAT, 420);
}
static void zero(void* p,int n){ char* z=(char*)p; for(int i=0;i<n;i++) z[i]=0; }
static int lock_ex(long h, long long off, long long len){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        return LockFileEx((void*)h, LOCKFILE_FAIL_IMMEDIATELY|LOCKFILE_EXCLUSIVE_LOCK, 0, (unsigned int)len, (unsigned int)(len>>32), &ov) != 0; }
    struct flock fl; zero(&fl,sizeof fl); fl.l_type=(short)F_WRLCK; fl.l_whence=(short)SEEK_SET; fl.l_start=off; fl.l_len=len;
    return fcntl((int)h, F_SETLK, &fl) == 0;
}
static void unlock(long h, long long off, long long len){
    if (__chibil_os_is_windows()){ OVERLAPPED ov; zero(&ov,sizeof ov); ov.Offset=(unsigned int)off; ov.OffsetHigh=(unsigned int)(off>>32);
        UnlockFileEx((void*)h, 0, (unsigned int)len, (unsigned int)(len>>32), &ov); return; }
    struct flock fl; zero(&fl,sizeof fl); fl.l_type=(short)F_UNLCK; fl.l_whence=(short)SEEK_SET; fl.l_start=off; fl.l_len=len;
    fcntl((int)h, F_SETLK, &fl);
}
int main(void){
    long h = w_open(""lk.tmp"");
    if (!lock_ex(h, 1000, 10)) return 1;
    unlock(h, 1000, 10);
    if (!lock_ex(h, 1000, 10)) return 2;   /* re-lock proves the first unlock worked */
    unlock(h, 1000, 10);
    return 55;
}";

    static byte[] Link()
    {
        string inc = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite", "include");
        string dir = Path.Combine(Path.GetTempPath(), "chibil_lk_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string c = Path.Combine(dir, "lk.c");
            File.WriteAllText(c, Src);
            byte[] obj = TestCompiler.CompileFileToObj(c, Chibil.TargetProfile.CoreClr, null, new[] { inc });
            return LinkPipeline.LinkToBytes(new[] { ObjectFile.Load(obj, "lk.obj") }, new List<string>(), null, PositionedIoTests.PinvokeMap());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Lock_primitive_roundtrip_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(Link(), out string o, out _);
        Assert.True(exit == 55, $"windows expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Lock_primitive_roundtrip_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(Link(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux expected 55, got {exit}. {o}");
    }
}
