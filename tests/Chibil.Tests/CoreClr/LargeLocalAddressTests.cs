using Chibil;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// Regression for the <c>ldloca.s</c> / <c>ldarga.s</c> / <c>starg.s</c> operand
/// truncation bug: a function with more than 256 locals could assign a local
/// (e.g. a loop pointer) a slot index >= 256. Address-of on that local was
/// emitted via the short form <c>ldloca.s &lt;uint8&gt;</c>, which truncated the
/// index modulo 256 — pointing at the WRONG local. In SQLite's
/// <c>sqlite3VdbeExec</c> this made the <c>for(pOp=...; ; pOp++)</c> increment
/// (which the parser lowers through <c>&amp;pOp</c>) update an unrelated struct
/// local, so the VDBE program counter never advanced past a jump and queries
/// produced no rows. The fix routes address/store of locals/args through the
/// long-form-aware encoder helpers.
/// </summary>
public class LargeLocalAddressTests
{
    static byte[] LinkSource(string src)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        return LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
    }

    [Fact]
    public void Address_of_local_past_slot_255_uses_long_form()
    {
        // ~300 dummy int locals push `p` and `a` past local slot 255, then a
        // pointer loop whose increment is lowered through &p must still advance
        // p correctly (not truncate the slot index to p%256).
        var sb = new System.Text.StringBuilder();
        sb.Append("int main(void){\n");
        sb.Append("  int data[8] = {1,2,3,4,5,6,7,15};\n");
        // 300 dummy locals, each used so the compiler keeps distinct slots.
        sb.Append("  int sink = 0;\n");
        for (int i = 0; i < 300; i++)
            sb.Append($"  int d{i} = {i}; sink += d{i};\n");
        sb.Append("  (void)sink;\n");
        sb.Append("  int *p = &data[0];\n");
        sb.Append("  int **pp = &p;\n");   // force p's address to be taken => stable home
        sb.Append("  (void)pp;\n");
        sb.Append("  int sum = 0;\n");
        sb.Append("  int i;\n");
        sb.Append("  for(i = 0; i < 8; i++){ sum += *p; p++; }\n"); // p++ lowered via &p
        // data sums to 1+2+3+4+5+6+7+15 = 43; need 55 -> add 12 via a second pass
        sb.Append("  p = &data[7];\n");
        sb.Append("  int last = *p;\n");          // 15
        sb.Append("  return sum - 43 + 55;\n");   // == 55 iff sum==43 (loop advanced correctly)
        sb.Append("}\n");

        var asm = System.Reflection.Assembly.Load(LinkSource(sb.ToString()));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }

    [Fact]
    public void Pointer_loop_increment_with_jump_advances_past_slot_255()
    {
        // Mirrors the VDBE pattern: for(p=&a[start]; 1; p++){ switch(p->op){
        // case JUMP: p = &a[target-1]; break; ... } } — the post-increment after
        // a jump-store must advance p even though p lives in a slot >= 256.
        var sb = new System.Text.StringBuilder();
        sb.Append("struct Op { int op; int p2; };\n");
        sb.Append("int main(void){\n");
        sb.Append("  struct Op a[5];\n");
        sb.Append("  a[0].op = 0; a[0].p2 = 4;\n");  // jump -> 4
        sb.Append("  a[1].op = 1; a[1].p2 = 0;\n");  // add
        sb.Append("  a[2].op = 2; a[2].p2 = 0;\n");  // result
        sb.Append("  a[3].op = 3; a[3].p2 = 0;\n");  // halt
        sb.Append("  a[4].op = 4; a[4].p2 = 1;\n");  // goto -> 1
        sb.Append("  int sink = 0;\n");
        for (int i = 0; i < 300; i++)
            sb.Append($"  int d{i} = {i}; sink += d{i};\n");
        sb.Append("  (void)sink;\n");
        sb.Append("  struct Op *aOp = &a[0];\n");
        sb.Append("  struct Op *p;\n");
        sb.Append("  int result = 0, got = 0, guard = 0;\n");
        sb.Append("  for(p = &aOp[0]; 1; p++){\n");
        sb.Append("    if(++guard > 50) return 1;\n");
        sb.Append("    switch(p->op){\n");
        sb.Append("      case 0: goto jmp;\n");
        sb.Append("      case 1: result += 55; break;\n");
        sb.Append("      case 2: got = 1; break;\n");
        sb.Append("      case 3: return (result == 55 && got == 1) ? 55 : 2;\n");
        sb.Append("      case 4: jmp: p = &aOp[p->p2 - 1]; break;\n");
        sb.Append("    }\n");
        sb.Append("  }\n");
        sb.Append("}\n");

        var asm = System.Reflection.Assembly.Load(LinkSource(sb.ToString()));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }
}
