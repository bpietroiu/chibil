using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ParserEnumTests
{
    // A forward-declared enum (`typedef enum E E;` before `enum E {...}`) is a GCC
    // extension QuickJS relies on (`typedef enum OPCodeEnum OPCodeEnum;`). chibil
    // errored "unknown enum type" because the tag wasn't defined yet. An incomplete
    // enum is int-sized and usable before completion; the later definition registers
    // the constants and completes the tag.
    [Fact]
    public void Forward_declared_enum_compiles()
    {
        const string src =
            "typedef enum E E;\n" +              // forward declaration (used below before def)
            "static int use(E op);\n" +          // typedef name as a param type, pre-definition
            "enum E { A, B, C };\n" +            // definition registers the constants
            "static int use(E op){ return (int)op + B; }\n" +
            "int main(void){ return use(A); }\n";   // 0 + 1
        // CompileToObj throws on any compile error; reaching the assert means it parsed
        // and the enum constants resolved against the completed tag.
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
