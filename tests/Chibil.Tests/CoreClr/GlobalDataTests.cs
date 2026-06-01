// tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class GlobalDataTests
{
    static int RunViaHost(string src, out string output)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Mutable_scalar_global_writable_via_dotnet_host()
    {
        // Writing an initialized global must work. On Windows this AV'd before
        // the fix (FieldRVA data is read-only there); on Linux it already worked.
        int exit = RunViaHost("static int g = 10; int main(void){ g = 55; return g; }", out string outp);
        Assert.True(exit == 55, $"expected 55, got {exit}. {outp}");
    }
}
