using ChibilLink;
using Chibil;
using System.Linq;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class LinkerUnitTests
{
    [Fact]
    public void ObjectFile_reads_methods_from_chibil_obj()
    {
        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            TargetProfile.CoreClr);

        var of = ObjectFile.Load(obj, "t.obj");

        Assert.True(of.Methods.Count >= 2);
        Assert.Contains(of.Methods, m => m.Name == "fib");
        Assert.Contains(of.Methods, m => m.Name == "main");
        var fib = of.Methods.First(m => m.Name == "fib");
        Assert.NotEmpty(fib.Il);
    }
}
