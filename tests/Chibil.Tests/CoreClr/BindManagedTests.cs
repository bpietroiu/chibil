using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class BindManagedTests
{
    [Fact]
    public void Reads_assembly_identity_from_corelib()
    {
        // System.Private.CoreLib is always present; read its identity.
        string corelib = typeof(object).Assembly.Location;
        var id = ManagedReference.Read(corelib);
        Assert.Equal("System.Private.CoreLib", id.Name);
        Assert.True(id.Version.Major >= 8);
        // The well-known corelib public-key TOKEN — guards the SHA-1/last-8/reversed algo.
        Assert.Equal("7CEC85D7BEA7798E", System.Convert.ToHexString(id.PublicKeyToken));
    }

    [Fact]
    public void Bound_symbol_resolves_managed_and_is_not_a_native_import()
    {
        // A program that calls an unresolved extern `__chibil_echo`. With --bind it
        // resolves to a managed method in a referenced assembly (no -l, no P/Invoke).
        const string src =
            "long __chibil_echo(long);\n" +
            "int main(void){ return (int)__chibil_echo(41); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");

        var opts = LinkOptions.Parse(new[] {
            "--bind=__chibil_echo=Chibil.PalTest.Echo.Run",
            "-r", typeof(object).Assembly.Location,   // any real assembly: identity is read, method name trusted
        });

        var imports = new System.Collections.Generic.List<ImportRecord>();
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, opts.Libraries, opts.ExportClass,
            opts.PinvokeMap, opts.Debug, opts.Shared, "t", opts.Entry, opts.LibSearchPaths,
            imports, opts.BindMap, opts.References);

        Assert.NotEmpty(pe);                                            // linked (not "unresolved symbol")
        Assert.DoesNotContain(imports, i => i.Name == "__chibil_echo"); // not a native import
    }

    [Fact]
    public void Bound_call_invokes_managed_System_Math_Abs()
    {
        // bind `my_abs` to System.Math.Abs(long); abs(-42) == 42 proves the managed call ran.
        // C `long long` -> Int64 cleanly (C `long` is LLP64 int32 modopt(IsLong) on Windows,
        // which would carry a custom modifier the corelib overload doesn't have), so the
        // bound MemberRef matches the real Int64 overload of System.Math.Abs.
        if (!DotnetHostRunner.DotnetAvailable()) return; // need the dotnet host to run the dll

        const string src =
            "long long my_abs(long long);\n" +
            "int main(void){ return (int)my_abs(-42); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        string corelib = typeof(object).Assembly.Location;

        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>(),
            null, null, false, false, "boundabs", "main", null, null,
            new System.Collections.Generic.Dictionary<string, string> { ["my_abs"] = "System.Math.Abs" },
            new System.Collections.Generic.List<string> { corelib });

        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string stdout);
        Assert.True(exit == 42, $"expected exit 42, got {exit}; host output: {stdout}");
    }
}
