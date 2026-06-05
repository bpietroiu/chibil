using System.Text;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class WeakAliasEmitTests
{
    // chibil must emit a .chialias COFF section naming each alias + its target.
    [Fact]
    public void Alias_pair_is_emitted_in_chialias_section()
    {
        const string src =
            "int target(int x){ return x; }\n" +
            "typedef int FT(int);\n" +
            "extern FT myalias __attribute__((alias(\"target\")));\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        string ascii = Encoding.ASCII.GetString(obj);
        Assert.Contains(".chialias", ascii);   // section emitted
        Assert.Contains("myalias", ascii);     // alias name in payload
        Assert.Contains("target", ascii);      // target name in payload
    }
}
