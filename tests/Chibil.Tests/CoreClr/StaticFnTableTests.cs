using System.Collections.Generic;
using Chibil;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// Validates that the linker's static-data relocation (FieldDataRelocator /
/// synthesized &lt;Module&gt;.cctor) correctly patches function-pointer table slots,
/// and that switch dispatch (as used by the SQLite VDBE opcode interpreter) works.
/// </summary>
public class StaticFnTableTests
{
    static byte[] LinkSource(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new List<ObjectFile> { of }, new List<string>());
    }

    [Fact]
    public void Static_function_pointer_table_dispatches_correctly()
    {
        // A static array of function pointers indexed at runtime — the linker's
        // FieldDataRelocator/.cctor must patch each slot with the right ldftn.
        string src = @"
static int f0(void){ return 10; }
static int f1(void){ return 20; }
static int f2(void){ return 25; }
static int (*ops[3])(void) = { f0, f1, f2 };
int main(void){
  int s = 0;
  for (int i = 0; i < 3; i++) s += ops[i]();
  return s;   /* 10+20+25 = 55 */
}";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Switch_dispatch_returns_correct_branch()
    {
        // A large-ish switch (mirrors the VDBE opcode interpreter dispatch).
        string src = @"
int dispatch(int op){
  int r = -1;
  switch(op){
    case 0: r = 1; break;
    case 1: r = 2; break;
    case 7: r = 7; break;
    case 13: r = 13; break;
    case 40: r = 40; break;
    case 100: r = 100; break;
    default: r = 999; break;
  }
  return r;
}
int main(void){
  /* 40 + 13 + 2 = 55 */
  return dispatch(40) + dispatch(13) + dispatch(1);
}";
        var asm = System.Reflection.Assembly.Load(LinkSource(src));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }
}
