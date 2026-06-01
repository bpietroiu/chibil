using System.Collections.Generic;
using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// Capstone end-to-end test: the full SQLite amalgamation (~250k lines),
/// compiled to pure MSIL by chibil's CoreCLR target, linked into a single
/// managed assembly by the in-house linker, runs a <c>:memory:</c>
/// CREATE/INSERT/SELECT sum(a) query on CoreCLR/Linux and exits 55 — with no
/// native sqlite3 binary. Validated on WSL (the runtime managed-SQLite target).
///
/// Slow (~40s for the sqlite3.c compile) and WSL-gated; skips cleanly if WSL +
/// dotnet are unavailable.
/// </summary>
public class SqliteSmokeTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "sqlite")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (samples/sqlite) not found");
    }

    internal static string RepoRootDir() => RepoRoot();

    /// <summary>
    /// Compiles the SQLite amalgamation + shim + main to CoreCLR objs and links
    /// them into a single pure-MSIL <c>app.dll</c>, returning the PE bytes. Shared
    /// by the Linux (WSL) and Windows (dotnet host) smoke tests so both exercise
    /// the SAME linked assembly.
    /// </summary>
    internal static byte[] BuildSqliteAppDll(string exportClass = null)
    {
        string sq = Path.Combine(RepoRoot(), "samples", "sqlite");
        string[] defs =
        {
            "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1",
            "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1",
        };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };

        var objs = new List<ObjectFile>();
        foreach (var src in new[]
        {
            Path.Combine(sq, "vendor", "sqlite3.c"),
            Path.Combine(sq, "sqlite_shim.c"),
            Path.Combine(sq, "main.c"),
        })
        {
            byte[] obj = TestCompiler.CompileFileToObj(src, Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }

        return LinkPipeline.LinkToBytes(objs, new List<string>(), exportClass);
    }

    [Fact]
    public void Memory_db_crud_returns_55_on_linux()
    {
        if (!WslRunner.Available()) return; // WSL+dotnet required for the Linux runtime check

        byte[] pe = BuildSqliteAppDll();
        var (exit, output) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"expected exit 55 (sum 20+22+13), got {exit}.\n{output}");
    }

    [Fact]
    public void Memory_db_crud_returns_55_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        byte[] pe = BuildSqliteAppDll();
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string output);
        Assert.True(exit == 55, $"windows exit {exit}: {output}");
    }
}
