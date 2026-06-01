using Chibil;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class FuncPtrTests
{
    static byte[] LinkSource(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
    }

    [Fact]
    public void Funcptr_address_and_indirect_call()
    {
        string src = "int add(int a,int b){return a+b;} int main(void){ int(*fp)(int,int)=add; return fp(50,5); }";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Funcptr_passed_as_callback()
    {
        string src = @"
typedef int (*cmp)(int,int);
int apply(cmp f, int a, int b){ return f(a,b); }
int add(int a, int b){ return a+b; }
int main(void){ return apply(add, 50, 5); }";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Funcptr_runs_on_linux()
    {
        if (!WslRunner.Available()) return;
        string src = "int add(int a,int b){return a+b;} int apply(int(*f)(int,int),int a,int b){return f(a,b);} int main(void){ return apply(add,50,5); }";
        var (exit, outp) = WslRunner.Run(LinkSource(src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }
}
