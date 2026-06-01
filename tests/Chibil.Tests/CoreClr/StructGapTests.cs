using Chibil;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class StructGapTests
{
    static byte[] LinkSource(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
    }

    [Fact]
    public void Compound_assign_to_anonymous_struct_member()
    {
        string src = @"
struct W { union { struct { unsigned short omitMask; } vtab; } u; };
int main(void){ struct W w; w.u.vtab.omitMask = 51; w.u.vtab.omitMask |= 4; return w.u.vtab.omitMask; }  // 51|4 = 55
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }
}
