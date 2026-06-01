using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LinkOptionsParseTests
{
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
    public void Pinvoke_rejects_malformed(string flag)
    {
        Assert.Null(LinkOptions.Parse(new[] { flag, "a.obj" }));
    }
}
