using System.Collections.Generic;
using System.Linq;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ImportsTests
{
    // A program that imports two libc FUNCTIONS (printf, malloc) and one libc DATA symbol
    // (chibil_data_sym, an unresolved extern → bound as a data import under -lc).
    const string Src =
        "typedef unsigned long size_t;\n" +
        "int printf(const char*, ...);\n" +
        "void* malloc(size_t);\n" +
        "extern int chibil_data_sym;\n" +
        "int main(void){ printf(\"%d\", chibil_data_sym); return (int)(long)malloc(8); }\n";

    [Fact]
    public void CollectImports_lists_function_and_data_imports_with_libraries()
    {
        byte[] obj = TestCompiler.CompileToObj(Src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        var imports = new List<ImportRecord>();
        LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" }, importsOut: imports);

        Assert.Contains(imports, i => i.Name == "printf" && i.Kind == "func" && i.Lib == "libc.so.6");
        Assert.Contains(imports, i => i.Name == "malloc" && i.Kind == "func" && i.Lib == "libc.so.6");
        Assert.Contains(imports, i => i.Name == "chibil_data_sym" && i.Kind == "data" && i.Lib == "libc.so.6");
        // Deduplicated + sorted: data after func within a library, no duplicate (name,kind,lib).
        Assert.Equal(imports.Count, imports.Select(i => (i.Lib, i.Kind, i.Name)).Distinct().Count());
    }
}
