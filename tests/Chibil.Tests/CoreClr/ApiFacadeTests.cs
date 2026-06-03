using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ApiFacadeTests
{
    // A function declared in a public header and defined in a .c, plus a private
    // static helper that must never reach the facade.
    const string LibSrc =
        "#include \"mylib.h\"\n" +
        "static int secret(int x){ return x * 2; }\n" +
        "int ml_add(int a, int b){ return a + b + secret(0); }\n";

    const string LibHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "int ml_add(int a, int b);\n" +
        "#endif\n";

    [Fact]
    public void CompileToObj_accepts_export_api_headers()
    {
        // The new overload must compile without throwing and produce a non-empty object.
        byte[] obj = TestCompiler.CompileToObjWithApi(
            LibSrc, LibHdr, headerName: "mylib.h",
            target: Chibil.TargetProfile.CoreClr);
        Assert.NotNull(obj);
        Assert.True(obj.Length > 0);
    }
}
