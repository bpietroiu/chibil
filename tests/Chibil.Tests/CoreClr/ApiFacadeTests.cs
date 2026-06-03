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

    const string TyHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "struct MlPoint { int x; int y; };\n" +
        "struct MlCtx;\n" +
        "int ml_sum(struct MlPoint p);\n" +
        "struct MlCtx *ml_ctx_new(void);\n" +
        "int ml_ctx_id(struct MlCtx *c);\n" +
        "#endif\n";
    const string TySrc =
        "#include \"mylib.h\"\n" +
        "int ml_sum(struct MlPoint p){ return p.x + p.y; }\n" +
        "struct MlCtx *ml_ctx_new(void){ return (struct MlCtx*)0; }\n" +
        "int ml_ctx_id(struct MlCtx *c){ return c ? 1 : 0; }\n";

    [Fact]
    public void Public_struct_and_opaque_tag_are_recorded()
    {
        var opts = TestCompiler.CompileAndReturnOptions(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        Assert.Contains("MlPoint", opts.PublicApiTypes);  // defined in the header
        Assert.Contains("MlCtx", opts.PublicApiTypes);    // forward-declared (opaque) in the header
    }

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

    [Fact]
    public void Chiapi_v2_round_trips_public_types()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);
        Assert.Contains("ml_sum", of.Api.Functions);
        Assert.Contains("MlPoint", of.Api.Types);
        Assert.Contains("MlCtx", of.Api.Types);
    }

    static System.Reflection.Assembly LinkTyLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true);
        return System.Reflection.Assembly.Load(pe);
    }

    [Fact]
    public void Public_struct_is_public_and_in_facade_namespace()
    {
        var asm = LinkTyLib();
        Type pt = asm.GetType("mylib.MlPoint");
        Assert.NotNull(pt);                         // re-namespaced into mylib, not global
        Assert.True(pt.IsPublic, "public struct must be public");
        Assert.True(pt.IsValueType, "struct must be a value type");
        Assert.Null(asm.GetType("MlPoint"));        // no longer in the global namespace
    }

    [Fact]
    public void Opaque_handle_is_public_and_in_facade_namespace()
    {
        var asm = LinkTyLib();
        Type ctx = asm.GetType("mylib.MlCtx");
        Assert.NotNull(ctx);                        // synthesized opaque handle, re-namespaced
        Assert.True(ctx.IsPublic && ctx.IsValueType);
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

    [Fact]
    public void Public_struct_typedef_in_object_has_named_member_fields()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        var md = of.Md;
        System.Reflection.Metadata.TypeDefinitionHandle mlPoint = default;
        foreach (var h in md.TypeDefinitions)
            if (md.GetString(md.GetTypeDefinition(h).Name) == "MlPoint") { mlPoint = h; break; }
        Assert.False(mlPoint.IsNil, "MlPoint TypeDef must exist in the object");

        var td = md.GetTypeDefinition(mlPoint);
        Assert.True((td.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) != 0,
            "public struct must be ExplicitLayout so member offsets are exact");

        var names = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var fh in td.GetFields())
        {
            var fd = md.GetFieldDefinition(fh);
            string n = md.GetString(fd.Name);
            if (n == "<alignment member>") continue;       // chibil's alignment filler
            names[n] = fd.GetOffset();                      // FieldLayout offset
        }
        Assert.True(names.ContainsKey("x") && names.ContainsKey("y"), "members x and y must be named fields");
        Assert.Equal(0, names["x"]);                        // int x at offset 0
        Assert.Equal(4, names["y"]);                        // int y at offset 4
    }

    [Fact]
    public void Public_struct_fields_are_named_and_readable_in_the_assembly()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(TySrc, TyHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
        Type pt = asm.GetType("mylib.MlPoint");
        Assert.NotNull(pt);
        FieldInfo fx = pt.GetField("x", BindingFlags.Public | BindingFlags.Instance);
        FieldInfo fy = pt.GetField("y", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(fx); Assert.NotNull(fy);
        Assert.Equal(typeof(int), fx.FieldType);
        object p = Activator.CreateInstance(pt);
        fx.SetValue(p, 3); fy.SetValue(p, 4);
        MethodInfo sum = asm.GetType("mylib.Api").GetMethod("ml_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(7, (int)sum.Invoke(null, new object[] { p }));   // field offsets match the IL
    }

    const string EnHdr =
        "#ifndef MYLIB_H\n#define MYLIB_H\n" +
        "enum MlColor { ML_RED, ML_GREEN = 5, ML_BLUE };\n" +
        "int ml_color_code(enum MlColor c);\n" +
        "enum MlColor ml_default_color(void);\n" +
        "#endif\n";
    const string EnSrc =
        "#include \"mylib.h\"\n" +
        "int ml_color_code(enum MlColor c){ return (int)c + 100; }\n" +
        "enum MlColor ml_default_color(void){ return ML_GREEN; }\n";

    [Fact]
    public void Public_enum_and_usages_are_captured()
    {
        var opts = TestCompiler.CompileAndReturnOptions(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var e = opts.PublicApiEnums.Find(x => x.Tag == "MlColor");
        Assert.NotNull(e);
        Assert.Equal(0, e.Members.Find(m => m.Name == "ML_RED").Value);
        Assert.Equal(5, e.Members.Find(m => m.Name == "ML_GREEN").Value);
        Assert.Equal(6, e.Members.Find(m => m.Name == "ML_BLUE").Value);
        Assert.Contains(opts.PublicApiEnumUsages, u => u.Function == "ml_color_code" && u.Position == 1 && u.EnumTag == "MlColor");
        Assert.Contains(opts.PublicApiEnumUsages, u => u.Function == "ml_default_color" && u.Position == 0 && u.EnumTag == "MlColor");
    }

    [Fact]
    public void Chiapi_v3_round_trips_enums_and_usages()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assert.NotNull(of.Api);
        var e = of.Api.Enums.Find(x => x.Tag == "MlColor");
        Assert.NotNull(e);
        Assert.Equal(6, e.Members.Find(m => m.Name == "ML_BLUE").Value);
        Assert.Contains(of.Api.EnumUsages, u => u.Function == "ml_color_code" && u.Position == 1 && u.EnumTag == "MlColor");
    }

    static System.Reflection.Assembly LinkEnLib()
    {
        byte[] obj = TestCompiler.CompileToObjWithApi(EnSrc, EnHdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        return System.Reflection.Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));
    }

    [Fact]
    public void Public_enum_is_synthesized_as_a_real_enum_type()
    {
        var asm = LinkEnLib();
        Type t = asm.GetType("mylib.MlColor");
        Assert.NotNull(t);
        Assert.True(t.IsEnum, "must be a real CLR enum");
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(t));
        Assert.Equal(5, (int)Enum.Parse(t, "ML_GREEN"));
        Assert.Equal(6, (int)Enum.Parse(t, "ML_BLUE"));
        Assert.True(t.IsPublic);
    }

    [Fact]
    public void Forwarder_signature_uses_enum_and_runs()
    {
        var asm = LinkEnLib();
        Type api = asm.GetType("mylib.Api");
        Type mc = asm.GetType("mylib.MlColor");
        MethodInfo code = api.GetMethod("ml_color_code", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(code);
        Assert.Equal(mc, code.GetParameters()[0].ParameterType);   // param threaded to the enum
        MethodInfo def = api.GetMethod("ml_default_color", BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(mc, def.ReturnType);                          // return threaded to the enum
        object blue = Enum.Parse(mc, "ML_BLUE");                   // 6
        Assert.Equal(106, (int)code.Invoke(null, new object[] { blue }));  // enum arg -> int callee -> runs
        object dc = def.Invoke(null, null);
        Assert.Equal(5, (int)dc);                                  // ML_GREEN
    }

    [Fact]
    public void Fixture_struct_value_passes_through_forwarder()
    {
        string lib = FixtureDir();
        string src = File.ReadAllText(Path.Combine(lib, "src", "mylib.c"));
        string hdr = File.ReadAllText(Path.Combine(lib, "include", "mylib.h"));
        byte[] obj = TestCompiler.CompileToObjWithApi(src, hdr, "mylib.h", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "mylib.obj");
        Assembly asm = Assembly.Load(LinkPipeline.LinkToBytes(new[] { of }, new List<string>(),
            exportClass: null, pinvokeMap: null, debuggable: false, shared: true));

        Type api = asm.GetType("mylib.Api");
        Type pt = asm.GetType("mylib.MlPoint");
        MethodInfo sum = api.GetMethod("ml_sum", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(sum);
        Assert.Equal(pt, sum.GetParameters()[0].ParameterType);   // forwarder takes the re-namespaced struct

        object p = Activator.CreateInstance(pt);
        pt.GetField("x", BindingFlags.Public | BindingFlags.Instance).SetValue(p, 3);
        pt.GetField("y", BindingFlags.Public | BindingFlags.Instance).SetValue(p, 4);
        Assert.Equal(7, (int)sum.Invoke(null, new object[] { p }));   // 3+4, by value, fields by name
    }
}
