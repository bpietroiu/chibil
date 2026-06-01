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
}
