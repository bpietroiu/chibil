using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class VarargsTests
{
    static byte[] LinkSource(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
    }

    [Fact]
    public void Variadic_def_with_zero_varargs_runs()
    {
        string src = "int f(int a, ...){ return a; } int main(void){ return f(55); }";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Variadic_function_definition_sums_args()
    {
        string src = @"
typedef __builtin_va_list va_list;
#define va_start(ap,last) __builtin_va_start(ap,last)
#define va_arg(ap,t)      __builtin_va_arg(ap,t)
#define va_end(ap)        __builtin_va_end(ap)
int sum_n(int count, ...){
    va_list ap; va_start(ap, count);
    int s = 0;
    for (int i = 0; i < count; i++) s += va_arg(ap, int);
    va_end(ap);
    return s;
}
int main(void){ return sum_n(3, 20, 22, 13); }
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Variadic_sum_runs_on_linux()
    {
        if (!WslRunner.Available()) return;
        byte[] pe = LinkSource("int sum_n(int c, ...){ __builtin_va_list ap; __builtin_va_start(ap,c); int s=0; for(int i=0;i<c;i++) s+=__builtin_va_arg(ap,int); __builtin_va_end(ap); return s; } int main(void){ return sum_n(3,20,22,13); }");
        var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }
}
