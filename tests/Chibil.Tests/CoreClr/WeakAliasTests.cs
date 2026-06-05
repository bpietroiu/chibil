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
}
