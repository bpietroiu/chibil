// COFF Object model + parser (CoffFile and friends).
//
// This is a plain, project-compilable extract of the COFF parsing types that
// also live in coffobjdumper.cs. coffobjdumper.cs is a single-file
// `dotnet run` script (it carries `#:property` file-based-app directives and a
// top-level `Main`), which cannot be compiled into a normal project. This file
// contains just the reusable parsing model so test projects (and, later, the
// in-house linker) can `<Compile Include>` it without pulling in the dumper's
// entry point or file-based-app directives.
//
// Keep the type shapes here in sync with coffobjdumper.cs.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Chibil.CoffModel;

public struct CoffFileHeader
{
    public ushort Machine;
    public ushort NumberOfSections;
    public uint TimeDateStamp;
    public uint PointerToSymbolTable;
    public uint NumberOfSymbols;
    public ushort SizeOfOptionalHeader;
    public ushort Characteristics;

    public static CoffFileHeader Read(ReadOnlySpan<byte> data)
    {
        return new CoffFileHeader
        {
            Machine = BinaryPrimitives.ReadUInt16LittleEndian(data),
            NumberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            TimeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            PointerToSymbolTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            NumberOfSymbols = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            Characteristics = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
        };
    }
}

public struct CoffSectionHeader
{
    public string Name;
    public uint VirtualSize;
    public uint VirtualAddress;
    public uint SizeOfRawData;
    public uint PointerToRawData;
    public uint PointerToRelocations;
    public uint PointerToLineNumbers;
    public ushort NumberOfRelocations;
    public ushort NumberOfLineNumbers;
    public uint Characteristics;

    public static CoffSectionHeader Read(ReadOnlySpan<byte> data)
    {
        string name = Encoding.UTF8.GetString(data[..8]).TrimEnd('\0');
        return new CoffSectionHeader
        {
            Name = name,
            VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            SizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            PointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            PointerToRelocations = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            PointerToLineNumbers = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            NumberOfRelocations = BinaryPrimitives.ReadUInt16LittleEndian(data[32..]),
            NumberOfLineNumbers = BinaryPrimitives.ReadUInt16LittleEndian(data[34..]),
            Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
        };
    }
}

public struct CoffRelocation
{
    public uint VirtualAddress; // offset within section
    public uint SymbolTableIndex;
    public ushort Type;

    public static CoffRelocation Read(ReadOnlySpan<byte> data)
    {
        return new CoffRelocation
        {
            VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data),
            SymbolTableIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            Type = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
        };
    }
}

public struct CoffSymbol
{
    public string Name;
    public uint Value;
    public short SectionNumber;
    public ushort Type;
    public byte StorageClass;
    public byte NumberOfAuxSymbols;
}

public class CoffFile
{
    public CoffFileHeader Header;
    public CoffSectionHeader[] Sections;
    public CoffSymbol[] Symbols;
    public byte[] FileData;

    const int CoffHeaderSize = 20;
    const int SectionHeaderSize = 40;
    const int SymbolSize = 18;
    const int RelocationSize = 10;

    public static CoffFile Parse(byte[] data)
    {
        var coff = new CoffFile { FileData = data };
        coff.Header = CoffFileHeader.Read(data);

        // Parse section headers (immediately after COFF header + optional header)
        int sectionOffset = CoffHeaderSize + coff.Header.SizeOfOptionalHeader;
        coff.Sections = new CoffSectionHeader[coff.Header.NumberOfSections];
        for (int i = 0; i < coff.Header.NumberOfSections; i++)
        {
            coff.Sections[i] = CoffSectionHeader.Read(data.AsSpan(sectionOffset + i * SectionHeaderSize));
        }

        // Parse symbol table
        if (coff.Header.PointerToSymbolTable > 0 && coff.Header.NumberOfSymbols > 0)
        {
            coff.Symbols = ParseSymbols(data, (int)coff.Header.PointerToSymbolTable, (int)coff.Header.NumberOfSymbols);
        }
        else
        {
            coff.Symbols = Array.Empty<CoffSymbol>();
        }

        // Resolve long section names (format: /NNN where NNN is offset into string table)
        if (coff.Header.PointerToSymbolTable > 0)
        {
            int stringTableOffset = (int)coff.Header.PointerToSymbolTable + (int)coff.Header.NumberOfSymbols * SymbolSize;
            for (int i = 0; i < coff.Sections.Length; i++)
            {
                if (coff.Sections[i].Name.StartsWith("/") && int.TryParse(coff.Sections[i].Name[1..], out int strOff))
                {
                    int strStart = stringTableOffset + strOff;
                    int strEnd = Array.IndexOf(data, (byte)0, strStart);
                    if (strEnd > strStart)
                        coff.Sections[i].Name = Encoding.UTF8.GetString(data, strStart, strEnd - strStart);
                }
            }
        }

        return coff;
    }

    static CoffSymbol[] ParseSymbols(byte[] data, int symTabOffset, int count)
    {
        // String table immediately follows the symbol table
        int stringTableOffset = symTabOffset + count * SymbolSize;

        var symbols = new CoffSymbol[count];
        int offset = symTabOffset;

        for (int i = 0; i < count; i++)
        {
            var span = data.AsSpan(offset, SymbolSize);

            // Name: first 4 bytes zero → use string table offset in next 4 bytes
            string name;
            uint nameCheck = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (nameCheck == 0)
            {
                uint strOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                int strStart = stringTableOffset + (int)strOffset;
                int strEnd = Array.IndexOf(data, (byte)0, strStart);
                name = Encoding.UTF8.GetString(data, strStart, strEnd - strStart);
            }
            else
            {
                name = Encoding.UTF8.GetString(data, offset, 8).TrimEnd('\0');
            }

            symbols[i] = new CoffSymbol
            {
                Name = name,
                Value = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                SectionNumber = BinaryPrimitives.ReadInt16LittleEndian(span[12..]),
                Type = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]),
                StorageClass = span[16],
                NumberOfAuxSymbols = span[17],
            };

            offset += SymbolSize;

            // Skip aux symbols
            int auxCount = symbols[i].NumberOfAuxSymbols;
            if (auxCount > 0)
            {
                for (int a = 0; a < auxCount && (i + 1) < count; a++)
                {
                    i++;
                    symbols[i] = new CoffSymbol { Name = "<aux>", NumberOfAuxSymbols = 0 };
                    offset += SymbolSize;
                }
            }
        }

        return symbols;
    }

    public CoffSectionHeader? FindSection(string name)
    {
        foreach (var s in Sections)
            if (s.Name == name) return s;
        return null;
    }

    public ReadOnlySpan<byte> GetSectionData(CoffSectionHeader section)
    {
        return FileData.AsSpan((int)section.PointerToRawData, (int)section.SizeOfRawData);
    }

    public CoffSectionHeader GetSection(int sectionNumber)
    {
        if (sectionNumber <= 0 || sectionNumber > Sections.Length)
            throw new ArgumentOutOfRangeException(nameof(sectionNumber));

        return Sections[sectionNumber - 1];
    }

    public CoffRelocation[] GetRelocations(CoffSectionHeader section)
    {
        if (section.NumberOfRelocations == 0)
            return Array.Empty<CoffRelocation>();

        var relocs = new CoffRelocation[section.NumberOfRelocations];
        int offset = (int)section.PointerToRelocations;
        for (int i = 0; i < relocs.Length; i++)
        {
            relocs[i] = CoffRelocation.Read(FileData.AsSpan(offset + i * RelocationSize));
        }
        return relocs;
    }
}
