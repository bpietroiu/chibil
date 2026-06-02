using System.Linq;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ChidbgTests
{
    // The .chidbg side-stream chibil emits must carry the source file and per-method
    // IL-offset -> line points, which chibil-link turns into Portable PDB sequence
    // points. Compile a tiny program and read the debug data back out of the object.
    [Fact]
    public void Object_carries_source_and_per_method_line_points()
    {
        const string src =
            "int add(int a, int b){\n" +   // line 1
            "  int s = a + b;\n" +         // line 2
            "  return s;\n" +              // line 3
            "}\n" +                        // line 4
            "int main(void){ return add(2, 3); }\n";   // line 5

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "chidbg.obj");

        Assert.NotNull(of.Debug);
        Assert.False(string.IsNullOrEmpty(of.Debug.SourceFile));
        Assert.NotEmpty(of.Debug.MethodPoints);

        // Every method's points must have strictly recognizable lines; across all
        // methods we should see add's body (lines 2 and 3) and main (line 5).
        var lines = of.Debug.MethodPoints.Values
            .SelectMany(p => p)
            .Select(p => p.Line)
            .ToHashSet();
        Assert.Contains(2, lines);
        Assert.Contains(3, lines);
        Assert.Contains(5, lines);

        // IL offsets within a method are non-negative.
        Assert.All(of.Debug.MethodPoints.Values.SelectMany(p => p), pt => Assert.True(pt.Il >= 0));
    }
}
