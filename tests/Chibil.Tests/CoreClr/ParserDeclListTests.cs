using Xunit;

namespace Chibil.Tests.CoreClr;

public class ParserDeclListTests
{
    // Multiple FUNCTION declarators in one declaration — `T f(...), g(...);` — is
    // valid C and used by musl, e.g. src/internal/syscall.h:
    //   hidden long __syscall_ret(unsigned long), __syscall_cp(...);
    // chibil previously routed the 2nd..Nth declarators into function-DEFINITION
    // handling, erroring "parameter name omitted" on the unnamed prototype params.
    [Fact]
    public void Comma_separated_function_prototypes_compile()
    {
        const string src =
            "long a(int), b(long);\n" +
            "long a(int x){ return x + 1; }\n" +
            "long b(long y){ return y + 2; }\n" +
            "int main(void){ return (int)(a(40) + b(-1)); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }

    // A variable followed by a function prototype in one declaration: `int x, g(int);`
    // — the 2nd declarator is a function, exercising the GlobalVariable() path.
    [Fact]
    public void Comma_separated_variable_then_function_prototype_compile()
    {
        const string src =
            "int x, g(int);\n" +
            "int g(int v){ return v + x; }\n" +
            "int main(void){ return g(0); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
