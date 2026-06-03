using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class UnusedExternTests
{
    // An UNUSED `extern` declaration of a global (here an incomplete struct type)
    // must not become a phantom undefined symbol — a real linker (ld) emits no symbol
    // for an unused extern. chibil-link previously misclassified it as a native data
    // import and errored: "field 'g' RVA data references a non-TypeDef value type."
    // (Root cause of the MicroPython minimal-port link failure.)
    [Fact]
    public void Unused_extern_global_does_not_become_a_data_import()
    {
        const string src =
            "struct S;\n" +                 // incomplete, never completed
            "extern const struct S g;\n" +  // declared, NEVER used
            "int main(void){ return 0; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "ext.obj");

        // -lc enables the data-import path that the unused extern wrongly fell into.
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });
        Assert.NotEmpty(pe);
    }

    // A USED extern must still resolve normally (regression guard for the fix).
    [Fact]
    public void Used_extern_global_still_links_against_its_definition()
    {
        byte[] refObj = TestCompiler.CompileToObj(
            "extern int shared;\nint use(void){ return shared; }\nint main(void){ return use(); }\n",
            Chibil.TargetProfile.CoreClr);
        byte[] defObj = TestCompiler.CompileToObj("int shared = 7;\n", Chibil.TargetProfile.CoreClr);

        byte[] pe = LinkPipeline.LinkToBytes(
            new[] { ObjectFile.Load(refObj, "ref.obj"), ObjectFile.Load(defObj, "def.obj") },
            new List<string>());
        Assert.NotEmpty(pe);
    }
}
