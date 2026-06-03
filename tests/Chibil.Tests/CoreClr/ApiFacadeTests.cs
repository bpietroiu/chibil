using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [Fact]
    public void Public_function_in_header_is_tagged_private_static_is_not()
    {
        var opts = TestCompiler.CompileAndReturnOptions(LibSrc, LibHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        Assert.Contains("ml_add", opts.PublicApiFunctions);     // declared in mylib.h
        Assert.DoesNotContain("secret", opts.PublicApiFunctions); // static, src-only
    }

    [Fact]
    public void Chiapi_section_lists_public_functions_and_group_name()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(LibSrc, LibHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);                          // .chiapi parsed
        Assert.Equal("mylib", of.Api.Group);             // header base name → group/namespace
        Assert.Contains("ml_add", of.Api.Functions);
        Assert.DoesNotContain("secret", of.Api.Functions);
    }

    static Assembly LinkLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(LibSrc, LibHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        // shared=true: no main; no explicit exportClass so manifest drives the facade
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), exportClass: null,
            pinvokeMap: null, debuggable: false, shared: true);
        return Assembly.Load(pe);
    }

    [Fact]
    public void Facade_Api_class_in_header_namespace_forwards_public_functions()
    {
        Assembly asm = LinkLib();
        Type api = asm.GetType("mylib.Api");
        Assert.NotNull(api);                                   // namespace from header base name
        Assert.True(api.IsPublic && api.IsAbstract && api.IsSealed);
        MethodInfo add = api.GetMethod("ml_add", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(add);
        Assert.Null(api.GetMethod("secret", BindingFlags.Public | BindingFlags.Static)); // private hidden
        Assert.Equal(7, (int)add.Invoke(null, new object[] { 3, 4 })); // forwarder runs: 3+4+secret(0)=7
    }

    [Fact]
    public void No_export_api_means_no_facade()
    {
        byte[] obj = TestCompiler.CompileToObj("int ml_add(int a,int b){return a+b;} int main(void){return 0;}",
            Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>()));
        Assert.Null(asm.GetType("mylib.Api"));
        Assert.DoesNotContain(asm.GetTypes(), t => t.IsPublic); // unchanged: no public facade
    }

    static string FixtureDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "fixtures", "mylib");

    [Fact]
    public void Fixture_mylib_links_and_exposes_Api()
    {
        string lib = FixtureDir();
        string src = File.ReadAllText(Path.Combine(lib, "src", "mylib.c"));
        string hdr = File.ReadAllText(Path.Combine(lib, "include", "mylib.h"));
        byte[] obj = TestCompiler.CompileToObjWithApi(src, hdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        MethodInfo add = asm.GetType("mylib.Api").GetMethod("ml_add", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(11, (int)add.Invoke(null, new object[] { 5, 6 }));  // 5+6+secret(0)=11
    }
}
