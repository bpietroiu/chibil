using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

/// <summary>
/// Rebases every FieldRVA-table entry in a serialized PE by a fixed delta.
///
/// When the FieldRVA data is emitted into our own <c>.sdata</c> section (rather
/// than via ManagedPEBuilder's <c>.text</c> mappedFieldData), the FieldRVA rows
/// are written with offsets RELATIVE to the start of that data. This adds the
/// section's base RVA so each entry becomes a real image RVA. Only the 4-byte RVA
/// cell of each FieldRVA row is modified, so no table/stream sizes change.
/// </summary>
public static class FieldRvaRebaser
{
    // ECMA-335 metadata table indices.
    private const int FieldRvaTableIndex = 0x1D; // 29

    /// <summary>Move every FieldRVA entry so the field data, which we emitted into
    /// the <c>.sdata</c> section at <paramref name="sdataRva"/>, is addressed there.
    ///
    /// ManagedPEBuilder writes each FieldRVA as (its own conceptual field-data base
    /// in <c>.text</c>) + (the section-relative offset we passed). We rebase by the
    /// difference between <paramref name="sdataRva"/> and that base. The base is
    /// recovered as the SMALLEST FieldRVA present (the field whose data offset is 0
    /// starts exactly at the base), so the shift is uniform and exact.</summary>
    /// <summary>Set every FieldRVA entry to its ABSOLUTE image RVA, computed as
    /// <paramref name="sdataRva"/> + the field's offset within the field-data blob
    /// (as recorded by the writer). This is exact and robust, unlike inferring a
    /// single rebase delta from the placeholder RVAs (see PeWriter for why).</summary>
    public static void SetAbsolute(byte[] pe, int sdataRva,
        System.Collections.Generic.IReadOnlyDictionary<int, int> fieldDataOffsets)
    {
        using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(pe));
        var md = peReader.GetMetadataReader();
        int fieldRvaRows = md.GetTableRowCount(TableIndex.FieldRva);
        if (fieldRvaRows == 0) return;

        var corHeader = peReader.PEHeaders.CorHeader;
        int mdRva = corHeader.MetadataDirectory.RelativeVirtualAddress;
        int mdFileOff = RvaToFileOffset(peReader, mdRva);
        var (tablesStreamFileOff, _) = FindTablesStream(pe, mdFileOff);
        PatchFieldRvaAbsolute(pe, tablesStreamFileOff, md, sdataRva, fieldDataOffsets);
    }

    private static int RvaToFileOffset(PEReader pe, int rva)
    {
        foreach (var s in pe.PEHeaders.SectionHeaders)
        {
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize)
                return s.PointerToRawData + (rva - s.VirtualAddress);
        }
        throw new LinkException("internal: metadata RVA not inside any section.");
    }

    private static (int off, int size) FindTablesStream(byte[] pe, int mdFileOff)
    {
        // Metadata root: Signature(4) MajorVer(2) MinorVer(2) Reserved(4)
        //                VersionLength(4) Version(padded to 4) Flags(2) Streams(2)
        int p = mdFileOff;
        uint sig = BitConverter.ToUInt32(pe, p); p += 4;
        if (sig != 0x424A5342) throw new LinkException("internal: bad metadata signature.");
        p += 2 + 2 + 4;
        int verLen = BitConverter.ToInt32(pe, p); p += 4;
        p += (verLen + 3) & ~3;     // version string, 4-byte aligned
        p += 2;                     // Flags
        ushort streams = BitConverter.ToUInt16(pe, p); p += 2;

        for (int i = 0; i < streams; i++)
        {
            int streamOffset = BitConverter.ToInt32(pe, p); p += 4;
            int streamSize = BitConverter.ToInt32(pe, p); p += 4;
            // Stream name: null-terminated, padded to 4 bytes.
            int nameStart = p;
            while (pe[p] != 0) p++;
            string name = System.Text.Encoding.ASCII.GetString(pe, nameStart, p - nameStart);
            p++;                                    // null terminator
            p = (p + 3) & ~3;                       // align
            if (name == "#~" || name == "#-")
                return (mdFileOff + streamOffset, streamSize);
        }
        throw new LinkException("internal: no #~ tables stream found.");
    }

    private static void PatchFieldRvaAbsolute(byte[] pe, int tablesOff, MetadataReader md,
        int sdataRva, System.Collections.Generic.IReadOnlyDictionary<int, int> fieldDataOffsets)
    {
        // Tables stream header: Reserved(4) Major(1) Minor(1) HeapSizes(1) Reserved(1)
        //                       Valid(8) Sorted(8) RowCounts[set bits](4 each)
        int p = tablesOff;
        p += 4;                       // Reserved
        p += 1 + 1;                   // Major, Minor
        byte heapSizes = pe[p]; p += 1;
        p += 1;                       // Reserved
        ulong valid = BitConverter.ToUInt64(pe, p); p += 8;
        p += 8;                       // Sorted

        // Row counts for each present table (ascending table index).
        var rowCounts = new int[64];
        for (int t = 0; t < 64; t++)
        {
            if ((valid & (1UL << t)) != 0)
            {
                rowCounts[t] = BitConverter.ToInt32(pe, p); p += 4;
            }
        }

        // p now points at the first table's rows. Compute index sizes.
        bool stringBig = (heapSizes & 0x01) != 0;
        bool guidBig = (heapSizes & 0x02) != 0;
        bool blobBig = (heapSizes & 0x04) != 0;

        // Walk every present table with index < FieldRva, adding its byte size.
        int offset = p;
        for (int t = 0; t < FieldRvaTableIndex; t++)
        {
            if ((valid & (1UL << t)) == 0) continue;
            offset += rowCounts[t] * RowSize(t, rowCounts, stringBig, guidBig, blobBig);
        }

        // FieldRva row layout: RVA (uint, 4) + Field (index into Field table).
        int fieldIndexSize = rowCounts[0x04] >= 65536 ? 4 : 2;
        int fieldRvaRowSize = 4 + fieldIndexSize;
        int rows = md.GetTableRowCount(TableIndex.FieldRva);

        // SAFETY: cross-check our hand-computed table offset against the authoritative
        // MetadataReader before mutating bytes. For each FieldRva row, the field index
        // we parse must match the field whose GetRelativeVirtualAddress equals the RVA
        // we parse — proving the offset/row-size math is correct. (Row sizes of the
        // metadata tables that can precede FieldRva are easy to miscompute.)
        var rvaByField = new Dictionary<int, uint>();
        foreach (var fh in md.FieldDefinitions)
        {
            int rva = md.GetFieldDefinition(fh).GetRelativeVirtualAddress();
            if (rva != 0) rvaByField[MetadataReaderRow(fh)] = (uint)rva;
        }
        for (int i = 0; i < rows; i++)
        {
            int cell = offset + i * fieldRvaRowSize;
            uint rva = BitConverter.ToUInt32(pe, cell);
            int fieldIdx = fieldIndexSize == 2
                ? BitConverter.ToUInt16(pe, cell + 4)
                : BitConverter.ToInt32(pe, cell + 4);
            if (!rvaByField.TryGetValue(fieldIdx, out uint expected) || expected != rva)
                throw new LinkException(
                    $"internal: FieldRva table offset miscomputed (row {i}: parsed field {fieldIdx} rva 0x{rva:X}, " +
                    "metadata disagrees). Refusing to corrupt the image.");
        }

        // Validated — set each FieldRVA to its absolute image RVA:
        //   sdataRva + (the field's offset within the field-data blob).
        for (int i = 0; i < rows; i++)
        {
            int cell = offset + i * fieldRvaRowSize;       // RVA is the first field
            int fieldRow = fieldIndexSize == 2
                ? BitConverter.ToUInt16(pe, cell + 4)
                : BitConverter.ToInt32(pe, cell + 4);
            if (!fieldDataOffsets.TryGetValue(fieldRow, out int dataOffset))
                throw new LinkException(
                    $"internal: FieldRva row {i} references field {fieldRow} with no recorded data offset.");
            BitConverter.GetBytes((uint)(sdataRva + dataOffset)).CopyTo(pe, cell);
        }
    }

    private static int MetadataReaderRow(FieldDefinitionHandle h) => MetadataTokens.GetRowNumber(h);

    // ── Row-size computation for the tables that can precede FieldRva (0..28) ──
    private static int RowSize(int table, int[] rc, bool sBig, bool gBig, bool bBig)
    {
        int S = sBig ? 4 : 2;     // #Strings index
        int G = gBig ? 4 : 2;     // #GUID index
        int B = bBig ? 4 : 2;     // #Blob index

        // Coded-index size: 4 bytes iff the largest target table needs more than
        // (16 - tagBits) bits to index. tagBits = ceil(log2(numTargetTables)).
        int Coded(int numTargets, params int[] tables)
        {
            int tagBits = (int)Math.Ceiling(Math.Log2(numTargets));
            int max = 0;
            foreach (var t in tables) if (t >= 0) max = Math.Max(max, rc[t]);
            return max < (1 << (16 - tagBits)) ? 2 : 4;
        }
        int Simple(int tbl) => rc[tbl] >= 65536 ? 4 : 2;

        // Coded index target-table sets (ECMA-335 II.24.2.6). numTargets counts the
        // tag slots (including reserved/-1 ones), which sets tagBits.
        int TypeDefOrRef() => Coded(3, 0x02, 0x01, 0x1B);
        int HasConstant() => Coded(3, 0x04, 0x08, 0x17);
        int HasCustomAttribute() => Coded(22,
            0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00,
            0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23,
            0x26, 0x27, 0x28, 0x2A, 0x2C);
        int HasFieldMarshal() => Coded(2, 0x04, 0x08);
        int HasDeclSecurity() => Coded(3, 0x02, 0x06, 0x20);
        int MemberRefParent() => Coded(5, 0x02, 0x01, 0x1A, 0x06, 0x1B);
        int HasSemantics() => Coded(2, 0x14, 0x17);
        int MethodDefOrRef() => Coded(2, 0x06, 0x0A);
        int MemberForwarded() => Coded(2, 0x04, 0x06);
        int CustomAttributeType() => Coded(5, 0x06, 0x0A); // 3 tag bits; only 2 real targets
        int ResolutionScope() => Coded(4, 0x00, 0x1A, 0x23, 0x01);

        switch (table)
        {
            case 0x00: return 2 + S + G + G + G;                               // Module: Generation, Name, Mvid, EncId, EncBaseId
            case 0x01: return ResolutionScope() + S + S;                       // TypeRef
            case 0x02: return 4 + S + S + TypeDefOrRef() + Simple(0x04) + Simple(0x06); // TypeDef
            case 0x04: return 2 + S + B;                                       // Field
            case 0x06: return 4 + 2 + 2 + S + B + Simple(0x08);               // MethodDef
            case 0x08: return 2 + S;                                           // Param
            case 0x09: return Simple(0x02) + TypeDefOrRef();                   // InterfaceImpl
            case 0x0A: return MemberRefParent() + S + B;                       // MemberRef
            case 0x0B: return 1 + 1 + HasConstant() + B;                       // Constant
            case 0x0C: return HasCustomAttribute() + CustomAttributeType() + B; // CustomAttribute
            case 0x0D: return HasFieldMarshal() + B;                           // FieldMarshal
            case 0x0E: return 2 + HasDeclSecurity() + B;                       // DeclSecurity
            case 0x0F: return 2 + 4 + Simple(0x02);                            // ClassLayout: PackingSize, ClassSize(u4), Parent
            case 0x10: return 4 + Simple(0x04);                                // FieldLayout
            case 0x11: return B;                                              // StandAloneSig
            case 0x12: return Simple(0x02) + Simple(0x06);                     // EventMap
            case 0x14: return 2 + S + TypeDefOrRef();                          // Event
            case 0x15: return Simple(0x02) + Simple(0x17);                     // PropertyMap
            case 0x17: return 2 + S + B;                                       // Property
            case 0x18: return 2 + HasSemantics() + Simple(0x06);              // MethodSemantics
            case 0x19: return Simple(0x06) + MethodDefOrRef() + MethodDefOrRef(); // MethodImpl
            case 0x1A: return S;                                              // ModuleRef
            case 0x1B: return B;                                              // TypeSpec
            case 0x1C: return 2 + MemberForwarded() + S + Simple(0x1A);        // ImplMap
            default:
                throw new LinkException($"internal: unhandled metadata table 0x{table:X} preceding FieldRva.");
        }
    }
}
