using System.IO;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LinkOptionsParseTests
{
    private static LinkOptions Parse(string cmdline) =>
        LinkOptions.Parse(cmdline.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Pinvoke_parses_comma_separated_pairs()
    {
        var o = LinkOptions.Parse(new[] { "--pinvoke=open=c,CreateFileA=kernel32", "a.obj" });
        Assert.NotNull(o);
        Assert.Equal("c", o.PinvokeMap["open"]);
        Assert.Equal("kernel32", o.PinvokeMap["CreateFileA"]);
    }

    [Theory]
    [InlineData("--pinvoke=foo")]    // no '='
    [InlineData("--pinvoke==bar")]   // empty name
    [InlineData("--pinvoke=foo=")]   // empty lib value
    [InlineData("--pinvoke=")]       // empty value entirely
    public void Pinvoke_rejects_malformed(string flag)
    {
        Assert.Throws<LinkException>(() => LinkOptions.Parse(new[] { flag, "a.obj" }));
    }

    // -o accepts attached and separated forms, plus the --output long alias.
    [Theory]
    [InlineData("-o out.dll a.obj")]
    [InlineData("-oout.dll a.obj")]
    [InlineData("--output out.dll a.obj")]
    [InlineData("--output=out.dll a.obj")]
    public void Output_accepts_both_forms(string cmd)
    {
        var o = Parse(cmd);
        Assert.Equal("out.dll", o.Output);
        Assert.Equal(new[] { "a.obj" }, o.Inputs);
    }

    [Theory]
    [InlineData("-l c a.obj")]
    [InlineData("-lc a.obj")]
    public void Library_accepts_both_forms(string cmd)
    {
        Assert.Equal(new[] { "c" }, Parse(cmd).Libraries);
    }

    [Theory]
    [InlineData("-L /opt/lib a.obj")]
    [InlineData("-L/opt/lib a.obj")]
    public void Search_path_accepts_both_forms(string cmd)
    {
        Assert.Equal(new[] { "/opt/lib" }, Parse(cmd).LibSearchPaths);
    }

    [Theory]
    [InlineData("--export-class=N.T a.obj")]
    [InlineData("--export-class N.T a.obj")]
    public void Long_option_accepts_equals_and_separated(string cmd)
    {
        Assert.Equal("N.T", Parse(cmd).ExportClass);
    }

    [Fact]
    public void Print_imports_flag_parses()
    {
        Assert.True(Parse("--print-imports a.obj").PrintImports);
        Assert.False(Parse("a.obj").PrintImports);
    }

    [Theory]
    [InlineData("-e _start a.obj")]
    [InlineData("-e_start a.obj")]
    [InlineData("--entry=_start a.obj")]
    [InlineData("--entry _start a.obj")]
    public void Entry_accepts_both_forms(string cmd)
    {
        Assert.Equal("_start", Parse(cmd).Entry);
    }

    [Fact]
    public void Shared_and_g_flags_set()
    {
        var o = Parse("-shared -g a.obj");
        Assert.True(o.Shared);
        Assert.True(o.Debug);
        Assert.Equal("main", o.Entry);   // default
    }

    [Fact]
    public void Double_dash_ends_option_processing()
    {
        var o = LinkOptions.Parse(new[] { "-g", "--", "-o", "a.obj" });
        Assert.True(o.Debug);
        Assert.Equal(new[] { "-o", "a.obj" }, o.Inputs);   // '-o' is now an input, not a flag
    }

    [Fact]
    public void Help_and_version_recognized()
    {
        Assert.True(LinkOptions.Parse(new[] { "--help" }).ShowHelp);
        Assert.True(LinkOptions.Parse(new[] { "--version" }).ShowVersion);
    }

    [Fact]
    public void Unknown_option_throws()
    {
        Assert.Throws<LinkException>(() => LinkOptions.Parse(new[] { "--nope", "a.obj" }));
    }

    [Fact]
    public void Response_file_is_expanded()
    {
        string rsp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(rsp, "-shared -lc\n  --export-class=N.T\n  a.obj");
            var o = LinkOptions.Parse(new[] { "@" + rsp, "b.obj" });
            Assert.True(o.Shared);
            Assert.Equal(new[] { "c" }, o.Libraries);
            Assert.Equal("N.T", o.ExportClass);
            Assert.Equal(new[] { "a.obj", "b.obj" }, o.Inputs);
        }
        finally { File.Delete(rsp); }
    }

    [Fact]
    public void Bind_parses_comma_separated_pairs()
    {
        var o = LinkOptions.Parse(new[] { "--bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp", "a.obj" });
        Assert.Equal("Chibil.Pal.Syscall", o.BindMap["__chibil_syscall"]);
        Assert.Equal("Chibil.Pal.GetTp", o.BindMap["__chibil_get_tp"]);
    }

    [Theory]
    [InlineData("-r Chibil.Pal.dll a.obj")]
    [InlineData("--reference Chibil.Pal.dll a.obj")]
    [InlineData("--reference=Chibil.Pal.dll a.obj")]
    public void Reference_accepts_forms(string cmd)
    {
        var o = LinkOptions.Parse(cmd.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(new[] { "Chibil.Pal.dll" }, o.References);
    }
}
