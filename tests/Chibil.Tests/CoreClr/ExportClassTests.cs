// tests/Chibil.Tests/CoreClr/ExportClassTests.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ExportClassTests
{
    static Assembly LinkAndLoad(string src, string exportClass)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), exportClass);
        return Assembly.Load(pe); // metadata-only inspection; nothing is executed
    }

    [Fact]
    public void Export_class_absent_by_default()
    {
        Assembly asm = LinkAndLoad("int main(void){ return 0; }", null);
        Assert.Null(asm.GetType("Foo.Bar"));
        Assert.DoesNotContain(asm.GetTypes(), t => t.IsPublic); // no public types without --export-class
    }

    [Fact]
    public void Export_class_without_namespace()
    {
        Assembly asm = LinkAndLoad("int main(void){ return 0; }", "MyNative");
        Type t = asm.GetType("MyNative");
        Assert.NotNull(t);
        Assert.True(t.IsPublic && t.IsAbstract && t.IsSealed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".Foo")]
    [InlineData("Foo.")]
    [InlineData("Foo..Bar")]
    [InlineData(".")]
    public void Malformed_export_class_throws(string bad)
    {
        byte[] obj = TestCompiler.CompileToObj("int main(void){ return 0; }", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        Assert.Throws<LinkException>(() =>
            LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>(), bad));
    }

    [Fact]
    public void Empty_export_class_is_public_static()
    {
        // A program whose only function is main (which is excluded from export)
        // yields an export class with zero methods.
        Assembly asm = LinkAndLoad("int main(void){ return 0; }", "Foo.Bar");
        Type t = asm.GetType("Foo.Bar");
        Assert.NotNull(t);
        Assert.True(t.IsPublic, "export class must be public");
        Assert.True(t.IsAbstract && t.IsSealed, "export class must be a static class (abstract+sealed)");
        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void Forwarder_calls_exported_function()
    {
        // `add` is extern-linkage (non-static) → exported; `main` is excluded.
        Assembly asm = LinkAndLoad(
            "int add(int a, int b){ return a + b; } int main(void){ return add(2, 3); }",
            "N.S");
        Type t = asm.GetType("N.S");
        Assert.NotNull(t);
        MethodInfo add = t.GetMethod("add", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(add);
        Assert.Null(t.GetMethod("main", BindingFlags.Public | BindingFlags.Static)); // main excluded
        object result = add.Invoke(null, new object[] { 2, 3 });
        Assert.Equal(5, (int)result);
    }

    [Fact]
    public void Forwarder_zero_arg_function()
    {
        // 0 params → no ldarg, MaxStack=1 branch.
        Assembly asm = LinkAndLoad(
            "int get_answer(void){ return 42; } int main(void){ return get_answer(); }",
            "N.S");
        MethodInfo m = asm.GetType("N.S").GetMethod("get_answer", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.Equal(42, (int)m.Invoke(null, null));
    }

    [Fact]
    public void Forwarder_five_arg_function_exercises_ldarg_s()
    {
        // 5 params → arg index 4 uses ldarg.s (0x0E).
        Assembly asm = LinkAndLoad(
            "int sum5(int a,int b,int c,int d,int e){ return a+b+c+d+e; } " +
            "int main(void){ return sum5(1,2,3,4,5); }",
            "N.S");
        MethodInfo m = asm.GetType("N.S").GetMethod("sum5", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.Equal(15, (int)m.Invoke(null, new object[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void Referenced_struct_is_public_for_export()
    {
        // psum takes `struct P*` → P must be promoted public. Q is used only inside
        // main (never in an exported signature) → must stay non-public.
        Assembly asm = LinkAndLoad(
            "struct P { int x; int y; }; " +
            "struct Q { int z; }; " +
            "int psum(struct P *p){ return p->x + p->y; } " +
            "int main(void){ struct P p; p.x = 20; p.y = 35; struct Q q; q.z = 0; return psum(&p) + q.z; }",
            "N.S");
        Type t = asm.GetType("N.S");
        MethodInfo psum = t.GetMethod("psum", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(psum);
        Type paramType = psum.GetParameters()[0].ParameterType; // P*  (a pointer type)
        Assert.True(paramType.IsPointer, "expected a pointer parameter");
        Assert.True(paramType.GetElementType().IsPublic, "referenced struct P must be promoted to public");

        // Q is referenced by no exported signature → stays non-public (no over-promotion).
        Type q = Array.Find(asm.GetTypes(), x => x.Name == "Q");
        if (q != null) Assert.False(q.IsPublic, "unreferenced struct Q must stay non-public");
    }

    [Fact]
    public void Opaque_forward_declared_struct_becomes_public_for_export()
    {
        // `struct Opaque;` is forward-declared only (no body in this TU), so it is
        // referenced via a module-scoped TypeRef with no TypeDef. The export feature
        // must synthesize an empty PUBLIC value-type TypeDef for it so external C#
        // can name `Opaque*`. `use` takes `struct Opaque*` and is exported.
        Assembly asm = LinkAndLoad(
            "struct Opaque; " +
            "int use(struct Opaque *p); " +              // prototype only — opaque pointer
            "int use(struct Opaque *p){ return p ? 1 : 0; } " +
            "int main(void){ return use(0); }",
            "N.S");
        Type t = asm.GetType("N.S");
        MethodInfo use = t.GetMethod("use", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(use);
        Type paramType = use.GetParameters()[0].ParameterType; // Opaque*
        Assert.True(paramType.IsPointer, "expected a pointer parameter");
        Type pointee = paramType.GetElementType();            // Opaque
        Assert.True(pointee.IsValueType, "opaque handle should be a value type");
        Assert.True(pointee.IsPublic, "forward-declared-only opaque struct must be synthesized public");
    }
}
