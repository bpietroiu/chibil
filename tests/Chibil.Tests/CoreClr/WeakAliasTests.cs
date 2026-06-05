using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class WeakAliasTests
{
    // __attribute__((alias("target"))) — GCC symbol aliasing, the basis of musl's
    // weak_alias. `myalias` must resolve to `target`'s definition. We assert
    // RESOLUTION at link, not just compilation: with NO -l libraries, an unaliased
    // `myalias` is an unresolved symbol and LinkToBytes throws; aliasing makes it
    // resolve in-module. (Uses the typedef function-type form, which parses cleanly;
    // musl's `extern __typeof(old) new` form additionally needs __typeof-of-function
    // support, handled by a separate task in this plan.)
    [Fact]
    public void Function_alias_resolves_to_target_at_link()
    {
        const string src =
            "int target(int x){ return x + 1; }\n" +
            "typedef int FT(int);\n" +
            "extern FT myalias __attribute__((weak, alias(\"target\")));\n" +
            "int main(void){ return myalias(41); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        // No -l libraries: `myalias` must resolve in-module via the alias, else throw.
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        Assert.NotEmpty(pe);
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
