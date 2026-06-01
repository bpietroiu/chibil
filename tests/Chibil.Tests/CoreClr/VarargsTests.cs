using System.Linq;
using Chibil;
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
    public void Cdecl_variadic_call_emits_concrete_signature()
    {
        string src = @"
typedef unsigned long size_t;
int __cdecl snprintf(char*, size_t, const char*, ...);
int main(void){ char b[16]; return snprintf(b, 16, ""%d"", 42); }
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        // snprintf must appear as a MemberRef. Decode its signature: cdecl, paramCount == 4
        // (char*, size_t, const char*, int) — the trailing int is the promoted "%d" arg.
        var refs = of.Md.MemberReferences
            .Select(h => of.Md.GetMemberReference(h))
            .Where(mr => of.Md.GetString(mr.Name) == "snprintf").ToList();
        Assert.NotEmpty(refs);
        bool foundConcrete = refs.Any(mr => {
            var br = of.Md.GetBlobReader(mr.Signature);
            byte conv = br.ReadByte();           // calling convention byte
            int pc = br.ReadCompressedInteger(); // param count
            if (pc != 4) return false;           // 3 fixed + 1 concrete vararg
            // Skip the return type, then decode each param's leading SignatureTypeCode,
            // skipping leading modopt/modreq custom-modifier chains. The 4th param must
            // be Int32 (the promoted "%d" vararg) — Layer 1 would instead emit a trailing
            // hidden va-buffer pointer (Pointer) here, so this distinguishes the paths.
            byte SkipMods()
            {
                byte b;
                while (true)
                {
                    b = br.ReadByte();
                    // 0x20 = CMOD_REQD, 0x1F = CMOD_OPT
                    if (b == 0x20 || b == 0x1F) { br.ReadCompressedInteger(); continue; }
                    return b;
                }
            }
            void SkipOneType()
            {
                byte b = SkipMods();
                while (b == 0x0F /*Ptr*/ || b == 0x10 /*ByRef*/ || b == 0x1B /*FnPtr-ish*/)
                {
                    if (b == 0x0F) { b = SkipMods(); continue; }
                    break;
                }
            }
            SkipOneType();                       // return type
            SkipOneType();                       // param 1: char*
            SkipOneType();                       // param 2: size_t
            SkipOneType();                       // param 3: const char*
            byte last = SkipMods();              // param 4: leading type code
            return last == 0x08;                 // ELEMENT_TYPE_I4 (Int32)
        });
        Assert.True(foundConcrete, "expected a snprintf MemberRef with 4 concrete params (3 fixed + promoted int), 4th param Int32 not a va-buffer pointer");
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

    [Fact]
    public void Native_snprintf_int_runs_on_linux()
    {
        if (!WslRunner.Available()) return;
        string src = @"
typedef unsigned long size_t;
int __cdecl snprintf(char*, size_t, const char*, ...);
int __cdecl strcmp(const char*, const char*);
int main(void){ char b[16]; snprintf(b, 16, ""%d"", 42); return strcmp(b, ""42"")==0 ? 55 : 1; }
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>{ "c" });
        var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }

    [Fact]
    public void Native_snprintf_two_signatures_on_linux()
    {
        if (!WslRunner.Available()) return;
        string src = @"
typedef unsigned long size_t;
int __cdecl snprintf(char*, size_t, const char*, ...);
int __cdecl strcmp(const char*, const char*);
int main(void){
  char a[16], b[16];
  snprintf(a, 16, ""%d"", 42);       /* 4th param int */
  snprintf(b, 16, ""%s"", ""hi"");   /* 4th param char* */
  return (strcmp(a,""42"")==0 && strcmp(b,""hi"")==0) ? 55 : 1;
}
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>{ "c" });
        var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }

    [Fact]
    public void Native_snprintf_int_runs_via_dotnet_host_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        string src = @"
typedef unsigned long size_t;
int __cdecl _snprintf(char*, size_t, const char*, ...);
int __cdecl strcmp(const char*, const char*);
int main(void){ char b[16]; _snprintf(b, 16, ""%d"", 42); return strcmp(b, ""42"")==0 ? 55 : 1; }
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>{ "msvcrt.dll" });
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string outp);
        Assert.True(exit == 55, $"exit {exit}: {outp}");
    }

    [Fact]
    public void Variadic_call_as_store_rhs_keeps_localloc_stack_empty()
    {
        // A Layer-1 variadic call packs its va-buffer with `localloc`, which
        // ECMA-335 requires to run with an empty evaluation stack. When the call
        // is the RHS of a store to a COMPUTED lvalue (e.g. p->b = sum(...)), the
        // destination address is otherwise pushed first and localloc then runs
        // with it underneath -> InvalidProgramException. The codegen must spill
        // the RHS to a scratch first. Regression for the SQLite bring-up.
        string src = @"
typedef __builtin_va_list va_list;
int sum_n(int n, ...){ va_list ap; __builtin_va_start(ap,n); int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); __builtin_va_end(ap); return s; }
struct S { int a; int b; };
int main(void){ struct S s; struct S* p = &s; p->b = sum_n(2, 20, 35); return p->b; }
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Variadic_call_as_argument_keeps_localloc_stack_empty()
    {
        // A Layer-1 variadic call packs its va-buffer with `localloc`, which must
        // run with an empty evaluation stack. When such a call is NESTED as an
        // argument of another call, the outer call's earlier args are already on
        // the stack when the inner localloc runs -> InvalidProgramException. The
        // codegen must pre-spill the outer call's args to scratch locals first.
        // SQLite hit this in sqlite3EndTable via
        //   sqlite3VdbeAddOp4(v, OP_SqlExec, .., sqlite3MPrintf(..), P4_DYNAMIC).
        string src = @"
typedef __builtin_va_list va_list;
int vsum(int n, ...){ va_list ap; __builtin_va_start(ap,n); int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); __builtin_va_end(ap); return s; }
int consume4(int a, int b, int c, int d){ return a + b + c + d; }
int main(void){
  /* nested variadic call with sibling args already on the stack */
  return consume4(10, vsum(3, 14, 15, 16), 0, 0);   /* 10 + 45 = 55 */
}";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Two_variadic_calls_as_arguments()
    {
        // Two localloc-producing args in the same call: both must be pre-spilled.
        string src = @"
typedef __builtin_va_list va_list;
int vsum(int n, ...){ va_list ap; __builtin_va_start(ap,n); int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); __builtin_va_end(ap); return s; }
int consume(int a, int b){ return a + b; }
int main(void){ return consume(vsum(2, 20, 13), vsum(2, 12, 10)); }   /* 33 + 22 = 55 */
";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Indirect_variadic_call_is_rejected()
    {
        // A variadic function called through a function pointer must produce a clean
        // compile error. Use __clrcall so taking the function's address succeeds
        // (ldftn path); the indirect call site is the one that must be rejected.
        string src = "int __clrcall f(int n, ...){ return n; } int main(void){ int(__clrcall *fp)(int,...) = f; return fp(1, 2, 3); }";
        var ex = Assert.Throws<ChibiException>(() => TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr));
        Assert.Contains("variadic", ex.Message);
    }

    // Per spec §7, native FLOAT varargs do NOT work through a monomorphized
    // concrete cdecl P/Invoke, on EITHER platform — and this is fundamental, not
    // a missing AL byte:
    //   • A C variadic callee reads a float/double vararg per the *variadic* ABI.
    //     On SysV x64 the caller must set AL = number of vector registers used;
    //     on Win64 a float vararg must be duplicated into BOTH the XMM register
    //     AND its shadow GP register.
    //   • Our call site lowers to a FIXED (non-variadic) cdecl signature whose 4th
    //     param is `double` (R8). The CLR marshals that via the managed cdecl ABI:
    //     XMM only, AL unset, no GP shadow. The native libc/msvcrt then reads the
    //     wrong register and formats garbage.
    // Empirically verified on Windows: msvcrt `_snprintf("%.1f", 3.5)` writes
    // "0.0" (the double never reaches the variadic read site). Integer / pointer
    // / string varargs are unaffected and pass on both platforms (see the int and
    // two-signature tests above). Skipped rather than asserted because the
    // monomorphized-cdecl approach cannot satisfy the variadic FP register
    // contract without a real variadic-call thunk.
    [Fact(Skip = "Native float varargs need the variadic FP-register contract (SysV AL / Win64 XMM+GP shadow) that a concrete cdecl P/Invoke cannot express; msvcrt _snprintf(\"%.1f\",3.5) observed writing \"0.0\". Integer/ptr/string varargs work — see the int and two-signature tests.")]
    public void Native_snprintf_float_caveat_documented()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        string src = @"
typedef unsigned long size_t;
int __cdecl _snprintf(char*, size_t, const char*, ...);
int __cdecl strcmp(const char*, const char*);
int main(void){ char b[32]; _snprintf(b, 32, ""%.1f"", 3.5); return strcmp(b, ""3.5"")==0 ? 55 : 1; }
";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>{ "msvcrt.dll" });
        int exit = DotnetHostRunner.RunPeViaDotnetHost(pe, out string outp);
        Assert.True(exit == 55, $"Windows float vararg expected 55, got {exit}: {outp}");
    }
}
