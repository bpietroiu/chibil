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
        Assert.NotEmpty(id.PublicKeyToken);   // corelib is strong-named
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
}
