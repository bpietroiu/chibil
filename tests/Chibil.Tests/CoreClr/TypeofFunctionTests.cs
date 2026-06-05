using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class TypeofFunctionTests
{
    // __typeof of a function declares a function-typed symbol (like a typedef'd
    // function type). Compile-only: the call site just needs to type-check + emit.
    [Fact]
    public void Typeof_of_function_declares_function_typed_symbol()
    {
        const string src =
            "int target(int x){ return x + 1; }\n" +
            "extern __typeof(target) myalias;\n" +
            "int use(int v){ return myalias(v); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        Assert.NotEmpty(obj);
    }

    // The full musl weak_alias form: __typeof(old) + alias attribute, end to end.
    // RESOLUTION check: with no -l libs, `myalias` must resolve in-module via the
    // alias (else LinkToBytes throws "unresolved symbol 'myalias'").
    [Fact]
    public void Musl_weak_alias_form_resolves()
    {
        const string src =
            "int target(int x){ return x + 1; }\n" +
            "extern __typeof(target) myalias __attribute__((weak, alias(\"target\")));\n" +
            "int main(void){ return myalias(41); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        Assert.NotEmpty(pe);
    }
}
