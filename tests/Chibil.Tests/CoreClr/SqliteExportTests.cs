using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteExportTests
{
    const string ConsumerSource = @"
using System;
using System.Text;
using Sqlite3;
public static class Program
{
    static sbyte[] Z(string s){ var u = Encoding.UTF8.GetBytes(s); var b = new sbyte[u.Length + 1]; for (int i = 0; i < u.Length; i++) b[i] = (sbyte)u[i]; return b; }
    public static unsafe int Main()
    {
        Native.platform_init();
        sbyte[] path = Z("":memory:"");
        sbyte[] ddl  = Z(""CREATE TABLE t(a INTEGER);INSERT INTO t VALUES(20),(22),(13);"");
        sbyte[] sel  = Z(""SELECT sum(a) FROM t"");
        sqlite3* db = null;
        fixed (sbyte* p = path) if (Native.sqlite3_open(p, &db) != 0) return 101;
        fixed (sbyte* c = ddl)  if (Native.sqlite3_exec(db, c, null, null, null) != 0) return 102;
        sqlite3_stmt* st = null;
        fixed (sbyte* q = sel)  if (Native.sqlite3_prepare_v2(db, q, -1, &st, null) != 0) return 103;
        int sum = 0;
        if (Native.sqlite3_step(st) == 100) sum = Native.sqlite3_column_int(st, 0);
        Native.sqlite3_finalize(st);
        Native.sqlite3_close(db);
        return sum; // 55
    }
}";

    static byte[] CompileConsumer(byte[] appDll)
    {
        var consumerRef = MetadataReference.CreateFromImage(appDll);
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        var fxRefs = tpa.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create(
            "consumer",
            new[] { CSharpSyntaxTree.ParseText(ConsumerSource) },
            fxRefs.Append(consumerRef),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));
        using var ms = new MemoryStream();
        EmitResult res = comp.Emit(ms);
        Assert.True(res.Success,
            "consumer compile failed:\n" + string.Join("\n", res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    static int RunConsumer(byte[] appDll, byte[] consumer, Func<string, int> hostRun)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_sp2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // chibil names the linked assembly "a" (see PeWriter.AddAssembly), so
            // the consumer's metadata reference binds to simple name "a". The host
            // resolves a referenced assembly by simple name -> file "a.dll".
            File.WriteAllBytes(Path.Combine(dir, "a.dll"), appDll);
            File.WriteAllBytes(Path.Combine(dir, "consumer.dll"), consumer);
            File.WriteAllText(Path.Combine(dir, "consumer.runtimeconfig.json"), DotnetHostRunner.RuntimeConfigJson);
            return hostRun(dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Native_surface_shape()
    {
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        Assembly asm = Assembly.Load(app);
        Type t = asm.GetType("Sqlite3.Native");
        Assert.NotNull(t);
        Assert.True(t.IsPublic && t.IsAbstract && t.IsSealed);
        foreach (var name in new[] { "sqlite3_open", "sqlite3_exec", "sqlite3_prepare_v2",
                                     "sqlite3_step", "sqlite3_column_int", "sqlite3_finalize", "sqlite3_close" })
            Assert.True(t.GetMethod(name, BindingFlags.Public | BindingFlags.Static) != null, $"missing {name}");
    }

    [Fact]
    public void Csharp_consumer_crud_returns_55_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        byte[] consumer = CompileConsumer(app);
        string outp = "";
        int exit = RunConsumer(app, consumer, dir =>
            DotnetHostRunner.RunDllInDir(Path.Combine(dir, "consumer.dll"), out outp));
        Assert.True(exit == 55, $"windows consumer exit {exit}\n{outp}");
    }

    [Fact]
    public void Csharp_consumer_crud_returns_55_on_linux()
    {
        if (!WslRunner.Available()) return;
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        byte[] consumer = CompileConsumer(app);
        string outp = "";
        int exit = RunConsumer(app, consumer, dir =>
        {
            var (e, o) = WslRunner.RunDirEntry(dir, "consumer.dll");
            outp = o;
            return e;
        });
        Assert.True(exit == 55, $"linux consumer exit {exit}\n{outp}");
    }
}
