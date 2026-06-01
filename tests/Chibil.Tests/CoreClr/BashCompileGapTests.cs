using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

// Regression tests for two chibil compiler gaps surfaced while compiling GNU bash
// 5.3 against musl headers (the "compile bash to IL" spike).
public class BashCompileGapTests
{
    static int RunViaHost(string src, out string output)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Line_directive_inside_active_if_compiles_and_runs()
    {
        // A #line marker inside an OPEN #if block (the union YYSTYPE shape in
        // yacc-generated y.tab.h) used to make ReadLineMarker re-enter the public
        // Preprocess, whose epilogue falsely raised "unterminated conditional
        // directive" because _condIncl != null. Must now compile and run.
        const string src =
            "#if 1\n" +
            "struct S {\n" +
            "#line 375 \"/some/path/parse.y\"\n" +
            "  int a;\n" +
            "#line 9 \"y.tab.h\"\n" +
            "};\n" +
            "int g = 7;\n" +
            "#endif\n" +
            "int main(void){ return g; }\n";
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 7, $"expected 7, got {exit}. {o}");
    }

    [Fact]
    public void Builtin_alloca_alias_compiles_and_runs()
    {
        // musl's <alloca.h> does `#define alloca __builtin_alloca`, so an alloca call
        // reaches the parser as `__builtin_alloca(n)`. That name must resolve to the
        // same builtin (not "implicit declaration") and hit the localloc special-case.
        const string src =
            "#define alloca __builtin_alloca\n" +
            "int main(void){ char *p = (char*)alloca(8); int s = 0;" +
            " for (int i = 0; i < 8; i++){ p[i] = (char)(i + 1); s += p[i]; } return s; }\n";
        int exit = RunViaHost(src, out string o);   // 1+2+...+8 = 36
        Assert.True(exit == 36, $"expected 36, got {exit}. {o}");
    }

    [Fact]
    public void Indirect_variadic_call_through_function_pointer_runs()
    {
        // bash's print_cmd.c does `(*pfunc)(fmt, …)` where pfunc points at a
        // chibil-defined variadic (cprintf). The indirect call is lowered via the
        // Layer-1 va-buffer ABI: pack the varargs + a hidden __va pointer and calli
        // a signature that includes the trailing void* param. Must run correctly.
        const string src =
            "typedef __builtin_va_list va_list;\n" +
            "static int sum_va(int n, ...){ va_list ap; __builtin_va_start(ap, n); int s = 0;" +
            " for (int i = 0; i < n; i++) s += __builtin_va_arg(ap, int); __builtin_va_end(ap); return s; }\n" +
            "int main(void){ int (*f)(int, ...) = sum_va; return (*f)(3, 10, 20, 6); }\n";  // 36
        int exit = RunViaHost(src, out string o);
        Assert.True(exit == 36, $"expected 36, got {exit}. {o}");
    }
}
