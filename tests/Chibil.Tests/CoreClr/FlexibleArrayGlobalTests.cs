using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    // A NON-const global with a flexible array member filled past sizeof(struct),
    // holding a pointer relocation (&sentinel), lands in .data -> chibil-link emits
    // it as a Mutable CLR static field. Its data extent (0x30) exceeds the struct's
    // fixed ClassLayout (0x10). The Mutable field's STORAGE must be sized to the
    // extent: the <Module>.cctor initialises it by copying `extent` bytes from a
    // $init source field, so a struct-sized field would overflow into the adjacent
    // static field. (Same shape as MicroPython's qstr pools, whose overflow
    // corrupted mp_qstr_const_pool_static -> find_qstr crash.)
    [Fact]
    public void Mutable_flexible_array_global_storage_type_is_sized_to_its_data_extent()
    {
        const string src =
            "struct b { const void *t; };\n" +
            "typedef struct { struct b base; long len; const void *items[]; } tup_t;\n" +
            "extern const int sentinel;\n" +
            "tup_t g = { {0}, 4, { &sentinel, &sentinel, &sentinel, &sentinel } };\n" +
            "const int sentinel = 42;\n" +
            "int main(void){ return g.items[3] != &sentinel; }\n";

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fam.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string> { "c" });

        using var pr = new PEReader(ImmutableArray.Create(pe));
        var md = pr.GetMetadataReader();

        // Find the target field named exactly "g" (NOT the "g$init" source field).
        FieldDefinition field = default;
        bool found = false;
        foreach (var fh in md.FieldDefinitions)
        {
            var fd = md.GetFieldDefinition(fh);
            if (md.GetString(fd.Name) == "g") { field = fd; found = true; break; }
        }
        Assert.True(found, "target field 'g' not found in linked PE");

        // Decode the field signature to its value-type TypeDef, read its ClassLayout.
        // (Mirrors MetadataMerger.GetFieldDataSize: ELEMENT_TYPE_VALUETYPE is reported
        // as SignatureTypeCode.TypeHandle; step back over the byte, then ReadTypeHandle.)
        var sr = md.GetBlobReader(field.Signature);
        sr.ReadSignatureHeader();
        SignatureTypeCode tc = sr.ReadSignatureTypeCode();
        Assert.Equal(SignatureTypeCode.TypeHandle, tc);
        sr.Offset -= 1; sr.ReadByte();
        EntityHandle th = sr.ReadTypeHandle();
        Assert.Equal(HandleKind.TypeDefinition, th.Kind);
        var layout = md.GetTypeDefinition((TypeDefinitionHandle)th).GetLayout();

        // extent = 16 (struct b + long len) + 4*8 (items[4]) = 0x30; struct sizeof = 0x10.
        Assert.False(layout.IsDefault, "storage value-type has no ClassLayout");
        Assert.True(layout.Size >= 0x30,
            $"Mutable FAM field 'g' storage ClassLayout 0x{layout.Size:X} < data extent 0x30");
    }
}
