using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteDiskTests
{
    static byte[] BuildDiskAppDll()
    {
        string sq = Path.Combine(SqliteSmokeTests.RepoRootDir(), "samples", "sqlite");
        string[] defs = { "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1", "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1" };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };
        var objs = new List<ObjectFile>();
        foreach (var src in new[]
        {
            Path.Combine(sq, "vendor", "sqlite3.c"),
            Path.Combine(sq, "sqlite_shim.c"),
            Path.Combine(sq, "sqlite_vfs_disk.c"),
            Path.Combine(sq, "main_disk.c"),
        })
        {
            byte[] obj = TestCompiler.CompileFileToObj(src, Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }
        return LinkPipeline.LinkToBytes(objs, new List<string>(), null, PositionedIoTests.PinvokeMap());
    }

    [Fact]
    public void Disk_db_crud_returns_55_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = DiskRunner.RunWindows(BuildDiskAppDll(), out string o, out long size, "sp3.db");
        Assert.True(exit == 55, $"windows exit {exit}: {o}");
        Assert.True(size > 0, $"sp3.db not created on disk (size {size})");
    }

    [Fact]
    public void Disk_db_crud_returns_55_on_linux()
    {
        if (!WslRunner.Available()) return;
        var (exit, o) = WslRunner.Run(BuildDiskAppDll(), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux exit {exit}: {o}");
    }
}
