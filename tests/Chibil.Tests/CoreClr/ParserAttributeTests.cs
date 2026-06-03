using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ParserAttributeTests
{
    // GCC __attribute__((...)) in declaration-specifier position (before/among the
    // type) must parse and be ignored — MicroPython's MP_NORETURN = __attribute__
    // ((noreturn)) prefixes functions, which previously errored "variable name
    // omitted". Unknown attributes (noreturn/used/weak/format/section) are skipped.
    [Fact]
    public void Prefix_attribute_is_accepted_and_ignored()
    {
        // Prefix / inter-specifier attribute position (what MicroPython's MP_NORETURN
        // uses). NOTE: postfix `void f(void) __attribute__((noreturn));` is a separate
        // position still unhandled by chibil — not needed by the minimal port.
        const string src =
            "__attribute__((noreturn)) void boom(void){ for(;;); }\n" +
            "static __attribute__((noreturn)) inline void boom2(void){ for(;;); }\n" +
            "__attribute__((used)) int g = 3;\n" +
            "int main(void){ return g; }\n";
        // CompileToObj throws on any compile error; reaching the assert means it parsed.
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
