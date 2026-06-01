// tests/Chibil.Tests/CoreClr/GlobalDataTests.cs
using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class GlobalDataTests
{
    static int RunViaHost(string src, out string output)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        return DotnetHostRunner.RunPeViaDotnetHost(pe, out output);
    }

    [Fact]
    public void Mutable_scalar_global_writable_via_dotnet_host()
    {
        // Writing an initialized global must work. On Windows this AV'd before
        // the fix (FieldRVA data is read-only there); on Linux it already worked.
        int exit = RunViaHost("static int g = 10; int main(void){ g = 55; return g; }", out string outp);
        Assert.True(exit == 55, $"expected 55, got {exit}. {outp}");
    }

    [Fact]
    public void Mutable_scalar_global_writable_on_linux()
    {
        if (!WslRunner.Available()) return;
        byte[] obj = TestCompiler.CompileToObj("static int g=10; int main(void){ g=55; return g; }", Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
        var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
        Assert.True(exit == 55, $"linux exit {exit}: {outp}");
    }

    [Fact]
    public void Mutable_array_global() // .data aggregate, cpblk-initialized then read
    {
        int exit = RunViaHost("static int a[3]={20,22,13}; int main(void){ a[0]+=0; return a[0]+a[1]+a[2]; }", out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Bss_global_writable() // zero-init array: no FieldRVA, no source — CLR auto-zeroes the static
    {
        int exit = RunViaHost("static int b[100]; int main(void){ b[7]=55; return b[7]; }", out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Pointer_init_struct_global() // g_vfs shape: fn-pointer field initialized, reassigned, called
    {
        string src = @"
static int f0(void){ return 0; }
static int f55(void){ return 55; }
static struct { int (*fn)(void); } s = { f0 };
int main(void){ s.fn = f55; return s.fn(); }";
        int exit = RunViaHost(src, out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }
}
