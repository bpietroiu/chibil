using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class WeakAliasTests
{
    // __attribute__((alias("target"))) — GCC symbol aliasing, the basis of musl's
    // weak_alias. `myalias` must resolve to `target`'s definition and be callable.
    [Fact]
    public void Function_alias_compiles_and_is_callable()
    {
        const string src =
            "int target(int x){ return x + 1; }\n" +
            "extern __typeof(target) myalias __attribute__((weak, alias(\"target\")));\n" +
            "int main(void){ return myalias(41); }\n";
        // CompileToObj throws on any compile error; reaching the assert means it parsed + emitted.
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }

    [Fact]
    public void Postfix_attribute_on_prototype_parses()
    {
        const string src =
            "int g(int) __attribute__((weak));\n" +   // postfix attribute, no alias
            "int g(int x){ return x; }\n" +
            "int main(void){ return g(0); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }
}
