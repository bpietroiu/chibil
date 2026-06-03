using System.Collections.Generic;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class FlexibleArrayGlobalTests
{
    // A global whose struct has a flexible array member, initialized with trailing
    // elements (one of them a pointer = a relocation), has actual data LARGER than the
    // struct's fixed ClassLayout size. chibil-link sized the FieldRVA field by the
    // struct's fixed size, so the FAM relocation fell outside the field:
    //   "data relocation ... is not inside any FieldRVA field"
    // and the FAM bytes were truncated on copy. The field must be sized by its actual
    // data extent. (Same shape as MicroPython's mp_rom_obj_tuple_t / mp_sys_implementation_obj.)
    [Fact]
    public void Flexible_array_member_global_is_sized_by_its_data_extent()
    {
        const string src =
            "struct b { const void *t; };\n" +
            "typedef struct { struct b base; long len; const void *items[]; } tup_t;\n" +
            "extern const int sentinel;\n" +
            "const tup_t g = { {0}, 1, { &sentinel } };\n" +  // items[0] past sizeof(struct)
            "const int sentinel = 42;\n" +
            "const void *use(void){ return g.items[0]; }\n" +
            "int main(void){ return use() != 0; }\n";

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fam.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });
        Assert.NotEmpty(pe);
    }
}
