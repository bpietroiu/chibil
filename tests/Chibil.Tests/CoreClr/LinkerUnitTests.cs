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

    [Fact]
    public void Linked_pe_starts_with_mz()
    {
        byte[] obj = TestCompiler.CompileToObj("int main(){return 7;}", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
        Assert.Equal((byte)'M', pe[0]);
        Assert.Equal((byte)'Z', pe[1]);
    }

    [Fact]
    public void Linked_fib_runs_in_process_returns_55()
    {
        byte[] obj = TestCompiler.CompileToObj(
            "int fib(int n){return n<2?n:fib(n-1)+fib(n-2);} int main(){return fib(10);}",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());

        var asm = System.Reflection.Assembly.Load(pe);
        var entry = asm.EntryPoint;
        Assert.NotNull(entry);
        object result = entry.Invoke(null, new object[] { new string[0] });
        Assert.Equal(55, (int)result);
    }

    [Fact]
    public void Linked_function_with_locals_runs_in_process()
    {
        // A loop forces a genuine local-variable signature (non-foldable); result must be 55.
        byte[] obj = TestCompiler.CompileToObj(
            "int main(){ int s = 0; for (int i = 1; i <= 10; i++) s += i; return s; }",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");

        // Make sure this test actually exercises the locals path.
        Assert.False(of.Methods.First(m => m.Name == "main").LocalSig.IsNil);

        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
        var asm = System.Reflection.Assembly.Load(pe);
        object result = asm.EntryPoint.Invoke(null, new object[] { new string[0] });
        Assert.Equal(55, (int)result);
    }

    [Fact]
    public void Linked_cross_object_call_runs_in_process()
    {
        byte[] a = TestCompiler.CompileToObj("int add(int,int); int main(){return add(50,5);}", Chibil.TargetProfile.CoreClr, "main.c");
        byte[] b = TestCompiler.CompileToObj("int add(int x,int y){return x+y;}", Chibil.TargetProfile.CoreClr, "add.c");
        var ofa = ObjectFile.Load(a, "main.obj");
        var ofb = ObjectFile.Load(b, "add.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[] { ofa, ofb }, new System.Collections.Generic.List<string>());
        var asm = System.Reflection.Assembly.Load(pe);
        object r = asm.EntryPoint.Invoke(null, new object[] { new string[0] });
        Assert.Equal(55, (int)r);
    }

    [Fact]
    public void Linked_pinvoke_puts_runs_via_dotnet_host()
    {
        Assert.True(DotnetHostRunner.DotnetAvailable(), "dotnet host not available on PATH");
        byte[] obj = TestCompiler.CompileToObj(
            "int puts(const char*); int main(){ puts(\"hello from chibil pinvoke\"); return 0; }",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string> { "msvcrt.dll" });
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string stdout);
        Assert.True(exit == 0, $"expected exit 0, got {exit}. Output:\n{stdout}");
        Assert.Contains("hello from chibil pinvoke", stdout);
    }
}
