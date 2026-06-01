using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteLockTests
{
    static byte[] Build(string harness)
    {
        string sq = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite");
        string[] defs = { "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1", "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1" };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };
        var objs = new List<ObjectFile>();
        foreach (var src in new[] { "vendor/sqlite3.c", "sqlite_shim.c", "sqlite_vfs_disk.c", harness })
        {
            byte[] obj = TestCompiler.CompileFileToObj(Path.Combine(sq, src.Replace('/', Path.DirectorySeparatorChar)),
                Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }
        return LinkPipeline.LinkToBytes(objs, new List<string>(), null, PositionedIoTests.PinvokeMap());
    }

    [Fact]
    public void Two_process_contention_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        var (contended, recovered) = LockContentionRunner.RunWindows(Build("main_lock_holder.c"), Build("main_lock_contender.c"));
        Assert.True(contended == 55, $"windows contender expected 55 (BUSY while held), got {contended}");
        Assert.True(recovered == 0, $"windows contender expected 0 (acquired after release), got {recovered}");
    }

    [Fact]
    public void Two_process_contention_linux()
    {
        if (!WslRunner.Available()) return;
        var (contended, recovered) = LockContentionRunner.RunLinux(Build("main_lock_holder.c"), Build("main_lock_contender.c"));
        Assert.True(contended == 55, $"linux contender expected 55 (BUSY while held), got {contended}");
        Assert.True(recovered == 0, $"linux contender expected 0 (acquired after release), got {recovered}");
    }
}
