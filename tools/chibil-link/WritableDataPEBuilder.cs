using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

/// <summary>
/// A <see cref="ManagedPEBuilder"/> that places the initialized C-global / FieldRVA
/// data into its OWN page-aligned, writable <c>.sdata</c> section instead of the
/// read-only <c>.text</c> section ManagedPEBuilder uses by default.
///
/// WHY: a C program — SQLite in particular — both MUTATES its initialized globals
/// at runtime (sqlite3GlobalConfig, the shared-cache list, …) and relies on the
/// load-time pointer fixups performed by the synthesized <c>&lt;Module&gt;.cctor</c>
/// (g_vfs's function table, sqlite3.c's static method tables). Those are writes
/// into FieldRVA data. ManagedPEBuilder keeps that data in <c>.text</c>, which is
/// read-only — and a <c>.text</c> that is BOTH executable and writable is rejected
/// by the CLR ("Bad IL format") for an IL-only image. A separate, non-executable,
/// writable data section satisfies both the W^X rule and C's mutable-global
/// semantics.
///
/// HOW: we do NOT hand the field data to ManagedPEBuilder as <c>mappedFieldData</c>
/// (that path is hardwired to <c>.text</c>). Instead we add a <c>.sdata</c> section
/// and emit the bytes there ourselves. The FieldRVA table is written by the caller
/// with section-relative offsets; after serialization the caller adds this
/// section's base RVA (exposed via <see cref="SDataRva"/>) to each entry. Because
/// changing 32-bit FieldRVA values never alters any table/stream size, the layout —
/// and therefore <see cref="SDataRva"/> — is identical before and after that patch.
/// </summary>
public sealed class WritableDataPEBuilder : ManagedPEBuilder
{
    private readonly BlobBuilder _fieldData;

    /// <summary>RVA at which the <c>.sdata</c> section was laid out (valid after Serialize).</summary>
    public int SDataRva { get; private set; } = -1;

    public WritableDataPEBuilder(
        PEHeaderBuilder header,
        MetadataRootBuilder metadataRootBuilder,
        BlobBuilder ilStream,
        BlobBuilder fieldData,
        MethodDefinitionHandle entryPoint,
        CorFlags flags)
        : base(header, metadataRootBuilder, ilStream,
               mappedFieldData: null, entryPoint: entryPoint, flags: flags)
    {
        _fieldData = fieldData;
    }

    protected override ImmutableArray<Section> CreateSections()
    {
        var sections = base.CreateSections();
        if (_fieldData == null || _fieldData.Count == 0)
            return sections;

        // Append .sdata LAST (after .text and .reloc). Inserting it BETWEEN .text
        // and .reloc desynchronizes ManagedPEBuilder's base-relocation emission for
        // the native entry-point stub (the .reloc block comes out empty and the CLR
        // rejects the image). Keeping .text/.reloc in their original relative order
        // and appending our data section avoids that.
        var b = ImmutableArray.CreateBuilder<Section>(sections.Length + 1);
        b.AddRange(sections);
        b.Add(new Section(".sdata",
            SectionCharacteristics.ContainsInitializedData
            | SectionCharacteristics.MemRead
            | SectionCharacteristics.MemWrite));
        return b.MoveToImmutable();
    }

    protected override BlobBuilder SerializeSection(string name, SectionLocation location)
    {
        if (name == ".sdata")
        {
            SDataRva = location.RelativeVirtualAddress;
            var bb = new BlobBuilder();
            bb.LinkSuffix(_fieldData);
            return bb;
        }
        return base.SerializeSection(name, location);
    }
}
