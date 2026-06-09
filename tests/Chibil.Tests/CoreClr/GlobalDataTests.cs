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
        if (!DotnetHostRunner.DotnetAvailable()) return;
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
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = RunViaHost("static int a[3]={20,22,13}; int main(void){ a[0]+=0; return a[0]+a[1]+a[2]; }", out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Bss_global_writable() // zero-init array: no FieldRVA, no source — CLR auto-zeroes the static
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = RunViaHost("static int b[100]; int main(void){ b[7]=55; return b[7]; }", out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }

    [Fact]
    public void Braced_string_char_array_is_inline_not_pointer()
    {
        // C11 6.7.9p14: `char m[] = { "str" }` (string in optional braces) initializes the
        // ARRAY with the bytes — sizeof(m) is the length, m[i] are the chars. chibil used to
        // mis-route the braced form to the aggregate path, sizing m to one element and storing
        // the string as a DECAYED POINTER (an ADDR64 .data relocation) — so sizeof was wrong
        // and, with a second .data static present, the bogus pointer slot overlapped it,
        // yielding a garbage relocation addend that crashed the linker.
        if (!DotnetHostRunner.DotnetAvailable()) return;
        int exit = RunViaHost(
            "static const char m[] = { \"ABCDE\" };" +
            "int main(void){ return (sizeof(m)==6 && m[0]=='A' && m[4]=='E' && m[5]==0) ? 55 : 1; }",
            out var o);
        Assert.True(exit == 55, $"braced string array not laid out inline (sizeof/bytes wrong), got {exit}. {o}");
    }

    [Fact]
    public void Braced_string_array_beside_second_data_static() // the regerror.c crash shape
    {
        // Reproduces the exact failure: a braced string array used via pointer-decay PLUS a
        // second const char[] in .data (like musl regerror.c's `messages` + a_ctz's debruijn32).
        // Old chibil emitted `m` as a pointer overlapping `tbl`, so the .data relocation addend
        // was garbage and chibil-link threw OverflowException. Now both are inline and it runs.
        if (!DotnetHostRunner.DotnetAvailable()) return;
        string src = @"
static const char tbl[8] = {0,1,2,3,4,5,6,7};
static const char m[] = { ""ABC\0DE"" };
static int idx(unsigned x){ return tbl[x & 7]; }
int main(void){
    const char *s = m;            /* decay — the trigger for the old miscompile */
    int n = 0;
    while (*s) n += *s++;         /* 'A'+'B'+'C' = 198 (stops at the embedded NUL) */
    return (n + idx(3)) & 0xff;   /* 198 + 3 = 201 */
}";
        int exit = RunViaHost(src, out var o);
        Assert.True(exit == 201, $"expected 201, got {exit}. {o}");
    }

    [Fact]
    public void Pointer_init_struct_global() // g_vfs shape: verifies initial pointer-reloc value, then reassignment
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        // Observing s.fn() == 0 first proves the reloc (f0) was applied after cpblk;
        // wrong cpblk/reloc ordering would leave a null/garbage pointer → return 1 → exit 1.
        string src = @"
static int f0(void){ return 0; }
static int f55(void){ return 55; }
static struct { int (*fn)(void); } s = { f0 };
int main(void){
    if (s.fn() != 0) return 1;   /* initial slot must hold f0 (reloc applied after cpblk) */
    s.fn = f55;
    return s.fn();               /* 55 */
}";
        int exit = RunViaHost(src, out var o);
        Assert.True(exit == 55, $"expected 55, got {exit}. {o}");
    }
}
