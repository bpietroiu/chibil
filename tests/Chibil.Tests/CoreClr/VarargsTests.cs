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

    [Fact]
    public void Variadic_mixed_types()
    {
        // int + long long + double, accumulated as long long, returned truncated to int.
        string src = @"
typedef __builtin_va_list va_list;
long long mix(int n, ...){ va_list ap; __builtin_va_start(ap,n);
  long long acc = __builtin_va_arg(ap, int);
  acc += __builtin_va_arg(ap, long long);
  acc += (long long)__builtin_va_arg(ap, double);
  __builtin_va_end(ap); return acc; }
int main(void){ return (int)mix(3, 10, 20LL, 25.0); }   // 10+20+25 = 55
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
    }

    [Fact]
    public void Variadic_pointer_arg()
    {
        // pass a pointer through varargs, deref it.
        string src = @"
typedef __builtin_va_list va_list;
int deref_first(int n, ...){ va_list ap; __builtin_va_start(ap,n);
  int* p = __builtin_va_arg(ap, int*); __builtin_va_end(ap); return *p; }
int main(void){ int x = 55; return deref_first(1, &x); }
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
    }

    [Fact]
    public void Variadic_va_copy_reiterates()
    {
        // va_copy then iterate both copies; doubled sum.
        string src = @"
typedef __builtin_va_list va_list;
int twice(int n, ...){ va_list a,b; __builtin_va_start(a,n);
  __builtin_va_copy(b,a);
  int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(a,int);
  for(int i=0;i<n;i++) s+=__builtin_va_arg(b,int);
  __builtin_va_end(a); __builtin_va_end(b); return s; }
int main(void){ return twice(3, 5, 10, 12); }   // (5+10+12)*2 = 54
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(54, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
    }

    [Fact]
    public void Variadic_mixed_types_on_linux()
    {
        if (!WslRunner.Available()) return;
        string src = @"
typedef __builtin_va_list va_list;
int mix(int n, ...){ va_list ap; __builtin_va_start(ap,n);
  long long acc = __builtin_va_arg(ap, int);
  acc += __builtin_va_arg(ap, long long);
  acc += (long long)__builtin_va_arg(ap, double);
  __builtin_va_end(ap); return (int)acc; }
int main(void){ return mix(3, 10, 20LL, 25.0); }
";
        var (exit, outp) = WslRunner.Run(LinkSource(src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }

    [Fact]
    public void Variadic_va_start_with_indirect_ap()
    {
        // ap accessed through a pointer (Deref lvalue) — exercises the AddType path.
        string src = @"
typedef __builtin_va_list va_list;
int sum_via_ptr(int n, ...){
  va_list ap; va_list* pap = &ap;
  __builtin_va_start(*pap, n);
  int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(*pap, int);
  __builtin_va_end(*pap);
  return s;
}
int main(void){ return sum_via_ptr(3, 20, 22, 13); }   // 55
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
    }

    [Fact]
    public void Variadic_forwarding_via_va_list_param()
    {
        string src = @"
typedef __builtin_va_list va_list;
int vsum(int n, va_list ap){ int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); return s; }
int sum(int n, ...){ va_list ap; __builtin_va_start(ap,n); int r=vsum(n,ap); __builtin_va_end(ap); return r; }
int main(void){ return sum(3, 20, 22, 13); }   // 55
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
    }

    [Fact]
    public void Variadic_forwarding_on_linux()
    {
        if (!WslRunner.Available()) return;
        string src = @"
typedef __builtin_va_list va_list;
int vsum(int n, va_list ap){ int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); return s; }
int sum(int n, ...){ va_list ap; __builtin_va_start(ap,n); int r=vsum(n,ap); __builtin_va_end(ap); return r; }
int main(void){ return sum(3, 20, 22, 13); }
";
        var (exit, outp) = WslRunner.Run(LinkSource(src), WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }
}
