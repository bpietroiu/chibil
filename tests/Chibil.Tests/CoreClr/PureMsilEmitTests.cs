using System.Linq;
using Chibil;
using Chibil.CoffModel;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PureMsilEmitTests
{
    [Fact]
    public void Default_target_is_ijw()
    {
        var opts = new CompilerOptions();
        Assert.Equal(TargetProfile.Ijw, opts.Target);
    }

    [Fact]
    public void CoreClr_obj_has_no_ijw_sections_or_symbols()
    {
        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            TargetProfile.CoreClr);

        var coff = CoffFile.Parse(obj);
        Assert.Null(coff.FindSection(".nep"));
        Assert.Null(coff.FindSection(".rdata$ilfixup"));
        Assert.DoesNotContain(coff.Symbols, s => s.Name.StartsWith("__unep@"));
        Assert.DoesNotContain(coff.Symbols, s => s.Name.Contains("__mep@"));
        Assert.Contains(coff.Symbols, s => s.Name == "fib" || s.Name == "_fib");
    }

    [Fact]
    public void Ijw_obj_still_has_nep_section()
    {
        byte[] obj = TestCompiler.CompileToObj("int main(){return 0;}", TargetProfile.Ijw);
        var coff = CoffFile.Parse(obj);
        Assert.NotNull(coff.FindSection(".nep"));
    }
}
