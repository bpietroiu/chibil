using Xunit;

namespace Chibil.Tests.CoreClr;

public class ParserConstEvalTests
{
    // The offsetof idiom `(char*)&((T*)0)->member - (char*)0` is a compile-time
    // integer constant (the member offset). musl's stddef.h uses it for offsetof()
    // when __GNUC__ is undefined (e.g. errno/strerror.c's errmsgidx[]). Pointer
    // subtraction lowers to Div(Sub(a,b), size); Eval2(Div) evaluated operands via
    // Eval() (a null-ref label sink), and the address operand reached EvalRval whose
    // `label = null` wrote through the null-ref → NullReferenceException crash.
    [Fact]
    public void Offsetof_null_pointer_idiom_in_global_initializer_compiles()
    {
        const string src =
            "struct T { char a[8]; char b[4]; };\n" +
            "static const unsigned long off_b =\n" +
            "    (unsigned long)((char*)&((struct T*)0)->b - (char*)0);\n" +
            "int main(void){ return (int)off_b; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
