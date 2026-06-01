using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Chibil.CoffModel;

namespace ChibilLink;

/// <summary>
/// Applies the pointer relocations carried by initialized-data sections of each
/// object at LOAD TIME, via a synthesized <c>&lt;Module&gt;</c> type initializer
/// (<c>.cctor</c>).
///
/// WHY THIS IS NEEDED
/// ------------------
/// A C global that is statically initialized with the address of a function or
/// of another global — e.g. the SQLite shim's
/// <code>static sqlite3_vfs g_vfs = { …, vfsOpen, vfsRandomness, "chibil-mem", … };</code>
/// or sqlite3.c's many static method tables — compiles to bytes in <c>.data</c>
/// plus COFF <c>ADDR64</c> relocations that a NATIVE linker would patch with the
/// final virtual addresses. In a pure-MSIL image there are no native code
/// addresses at load time: a function pointer is a managed method pointer
/// obtained with <c>ldftn</c>, and a data pointer is the managed address of a
/// FieldRVA field. Neither can be baked into static bytes. Left unpatched, every
/// such pointer slot is zero — g_vfs's function table is all-NULL and the first
/// VFS call faults (AccessViolation in sqlite3_vfs_register).
///
/// WHAT IT EMITS
/// -------------
/// For every <c>ADDR64</c> relocation in an initialized-data section whose source
/// offset lies inside a copied FieldRVA field, the initializer emits:
/// <code>
///   ldsflda  &lt;ownerField&gt;            // managed pointer to the field's data
///   [ ldc.i4 intra; conv.i; add ]    // + offset of the slot inside the field
///   ldftn   &lt;method&gt;     |  ldsflda &lt;targetField&gt; [ ; ldc.i4 addend; conv.i; add ]
///   stind.i                          // store the native-int pointer into the slot
/// </code>
/// The method is registered as the module's <c>.cctor</c>, so the CLR runs it
/// before any access to the module's static data (i.e. before main touches any
/// SQLite global).
/// </summary>
public static class FieldDataRelocator
{
    private const byte IMAGE_SYM_CLASS_EXTERNAL = 2;
    private const ushort IMAGE_REL_AMD64_ADDR64 = 0x0001;
    private const ushort IMAGE_REL_AMD64_ADDR32NB = 0x0003; // not expected, but recognised

    public readonly struct Reloc
    {
        public readonly int OwnerFieldRow;   // FieldRVA field that owns the slot
        public readonly int IntraOffset;     // byte offset of the slot inside that field
        public readonly bool TargetIsMethod;
        public readonly int TargetToken;     // 0x06xxxxxx (method) or 0x04xxxxxx (field)
        public readonly long Addend;         // extra byte offset added to a data target

        public Reloc(int ownerRow, int intra, bool isMethod, int token, long addend)
        {
            OwnerFieldRow = ownerRow; IntraOffset = intra;
            TargetIsMethod = isMethod; TargetToken = token; Addend = addend;
        }
    }

    /// <summary>Collect every resolvable FieldRVA pointer relocation across all
    /// objects, expressed in terms of merged output tokens.</summary>
    public static List<Reloc> Collect(MetadataMerger merger, IReadOnlyList<ObjectFile> objs)
    {
        var relocs = new List<Reloc>();

        // Global NAME maps for resolving EXTERNAL (cross-TU) relocation targets — a
        // function pointer or data pointer in one object's static initializer that
        // refers to a symbol DEFINED in another object (e.g. bash's builtins table
        // `{ "echo", echo_builtin }`, with echo_builtin in builtins/echo.c). Such a
        // COFF symbol is undefined (section 0), so the per-object (section,value)
        // maps below can't see it; resolve it by name to the merged method (→ ldftn)
        // or field (→ ldsflda).
        var methodByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in objs)
            foreach (var m in o.Methods)
                methodByName[m.Name] = merger.MapToken(o, m.OriginalToken);
        var fieldByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cf in merger.CopiedFields)
            if (cf.Name != null) fieldByName[cf.Name] = cf.PredictedRow;

        // Index copied fields by their SOURCE location so a reloc offset maps to
        // its owning field. Two indices per object: an exact-start lookup (for a
        // reloc whose offset equals a field start) and a sorted list (for offsets
        // INSIDE a multi-slot struct global such as g_vfs).
        foreach (var of in objs)
        {
            // Source fields of THIS object, grouped by section, sorted by offset.
            var fieldsBySection = new Dictionary<int, List<MetadataMerger.CopiedField>>();
            foreach (var cf in merger.CopiedFields)
            {
                if (cf.SourceObj != of) continue;
                if (!fieldsBySection.TryGetValue(cf.SourceSection, out var list))
                    fieldsBySection[cf.SourceSection] = list = new List<MetadataMerger.CopiedField>();
                list.Add(cf);
            }
            foreach (var list in fieldsBySection.Values)
                list.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));

            // Inverse maps for resolving a target symbol's (section, value).
            var bodyLoc = of.Coff.BuildMethodBodyLocationMap();
            var methodByLoc = new Dictionary<(int, int), int>();
            foreach (var kv in bodyLoc)
                methodByLoc[(kv.Value.SectionNumber, kv.Value.Offset)] = kv.Key;

            var fieldLoc = of.Coff.BuildFieldDataLocationMap();
            var fieldStartByLoc = new Dictionary<(int, int), int>();
            foreach (var kv in fieldLoc)
                fieldStartByLoc[(kv.Value.SectionNumber, kv.Value.Offset)] = kv.Key;

            int si = 0;
            foreach (var sec in of.Coff.Sections)
            {
                si++;
                // Only initialized-data sections carry these pointer relocations.
                if (sec.Name != ".data" && sec.Name != ".rdata") continue;
                var secRelocs = of.Coff.GetRelocations(sec);
                if (secRelocs.Length == 0) continue;
                byte[] secBytes = of.Coff.GetSectionData(sec).ToArray();

                if (!fieldsBySection.TryGetValue(si, out var srcFields)) srcFields = null;

                foreach (var r in secRelocs)
                {
                    if (r.Type != IMAGE_REL_AMD64_ADDR64)
                        throw new LinkException(
                            $"{of.Path}: unsupported data relocation type 0x{r.Type:X} in {sec.Name} " +
                            $"at offset 0x{r.VirtualAddress:X}");

                    // ── Find the owning copied field (source) ──────────────────
                    var owner = FindOwner(srcFields, (int)r.VirtualAddress);
                    if (owner == null)
                        throw new LinkException(
                            $"{of.Path}: data relocation at {sec.Name}+0x{r.VirtualAddress:X} " +
                            "is not inside any FieldRVA field (uninitialized/unknown global?).");
                    int intra = (int)r.VirtualAddress - owner.SourceOffset;

                    // ── Resolve the target symbol → merged method or field ─────
                    var sym = of.Coff.Symbols[(int)r.SymbolTableIndex];
                    long inlineAddend = r.VirtualAddress + 8 <= secBytes.Length
                        ? BitConverter.ToInt64(secBytes, (int)r.VirtualAddress) : 0;

                    // A GLOBAL (external-linkage) symbol may be DEFINED IN ANOTHER
                    // OBJECT; chibil then emits it with a stale (section,value) that
                    // collides with an unrelated local method/field (e.g. the first
                    // method at .text+0), so resolve it by NAME — the authoritative
                    // key — to the merged method (→ ldftn) or field (→ ldsflda).
                    // File-local statics (chibil mangles them "<name>_?A0x<hash>") are
                    // always defined in THIS object with a correct (section,value), as
                    // are string literals / STATIC-class data; those fall through to
                    // the (section,value) path below.
                    bool fileLocal = sym.Name.Contains("?A0x");
                    if (sym.StorageClass == IMAGE_SYM_CLASS_EXTERNAL && !fileLocal)
                    {
                        string nm = sym.Name;
                        if (methodByName.TryGetValue(nm, out int extMethodTok))
                        {
                            relocs.Add(new Reloc(owner.PredictedRow, intra, true, extMethodTok, 0));
                            continue;
                        }
                        if (fieldByName.TryGetValue(nm, out int extFieldRow))
                        {
                            relocs.Add(new Reloc(owner.PredictedRow, intra, false,
                                0x04000000 | extFieldRow, inlineAddend));
                            continue;
                        }
                        throw new LinkException(
                            $"{of.Path}: external data relocation target '{nm}' " +
                            $"in {sec.Name}+0x{r.VirtualAddress:X} is not defined in any object.");
                    }

                    var key = ((int)sym.SectionNumber, (int)sym.Value);

                    if (methodByLoc.TryGetValue(key, out int origMethodTok))
                    {
                        int mt = merger.MapToken(of, origMethodTok);
                        relocs.Add(new Reloc(owner.PredictedRow, intra, true, mt, 0));
                    }
                    else if (fieldStartByLoc.TryGetValue(key, out int origFieldTok))
                    {
                        int mappedRow = merger.MapFor(of).MapField(
                            MetadataTokens.FieldDefinitionHandle(origFieldTok & 0xFFFFFF)).RowId();
                        if (mappedRow == 0)
                            throw new LinkException(
                                $"{of.Path}: data relocation target field (sym '{sym.Name}') " +
                                "was not copied into the merged image.");
                        int ft = 0x04000000 | mappedRow;
                        relocs.Add(new Reloc(owner.PredictedRow, intra, false, ft, inlineAddend));
                    }
                    else
                    {
                        // A target that is neither a known method body nor a field
                        // start. This can be a pointer INTO the middle of a data
                        // object (sym.Value past the field start). Resolve by range.
                        var (fieldTok, fieldStart) = FindFieldByRange(fieldLoc, (int)sym.SectionNumber, (int)sym.Value);
                        if (fieldTok == 0)
                            throw new LinkException(
                                $"{of.Path}: unresolved data relocation target sym='{sym.Name}' " +
                                $"sec={sym.SectionNumber} val=0x{sym.Value:X} (no method/field match).");
                        int mappedRow = merger.MapFor(of).MapField(
                            MetadataTokens.FieldDefinitionHandle(fieldTok & 0xFFFFFF)).RowId();
                        if (mappedRow == 0)
                            throw new LinkException(
                                $"{of.Path}: ranged data relocation target field (sym '{sym.Name}') not copied.");
                        int ft = 0x04000000 | mappedRow;
                        relocs.Add(new Reloc(owner.PredictedRow, intra, false, ft,
                            inlineAddend + (sym.Value - fieldStart)));
                    }
                }
            }
        }

        return relocs;
    }

    private static MetadataMerger.CopiedField FindOwner(List<MetadataMerger.CopiedField> sorted, int offset)
    {
        if (sorted == null) return null;
        // Linear/loop is fine (few fields per data section per object); binary
        // search would micro-optimize but adds complexity.
        MetadataMerger.CopiedField best = null;
        foreach (var cf in sorted)
        {
            if (cf.SourceOffset <= offset && offset < cf.SourceOffset + cf.Size)
            {
                // Prefer the tightest (latest-starting) containing field.
                if (best == null || cf.SourceOffset > best.SourceOffset) best = cf;
            }
        }
        return best;
    }

    private static (int token, int start) FindFieldByRange(
        Dictionary<int, FieldDataLocation> fieldLoc, int section, int value)
    {
        // Find the field whose (section, start) is the greatest start <= value.
        int bestTok = 0, bestStart = -1;
        foreach (var kv in fieldLoc)
        {
            if (kv.Value.SectionNumber != section) continue;
            if (kv.Value.Offset <= value && kv.Value.Offset > bestStart)
            {
                bestStart = kv.Value.Offset; bestTok = kv.Key;
            }
        }
        return (bestTok, bestStart);
    }

    /// <summary>Build the .cctor IL: an init phase that cpblk-copies each .data
    /// global's bytes from its read-only source field into the writable CLR-static
    /// target, then the pointer-relocation phase that patches pointer slots in the
    /// (now writable) targets.</summary>
    public static byte[] BuildCctorIl(List<Reloc> relocs,
        IReadOnlyList<(int targetRow, int sourceRow, int size)> inits)
    {
        var il = new BlobBuilder();
        // Init phase: copy base bytes BEFORE any pointer relocation.
        foreach (var (targetRow, sourceRow, size) in inits)
        {
            il.WriteByte(0x7F); il.WriteInt32(0x04000000 | targetRow);   // ldsflda target  (dest)
            il.WriteByte(0x7F); il.WriteInt32(0x04000000 | sourceRow);   // ldsflda source  (src)
            EmitLdcI4(il, size);                                          // ldc.i4 size
            il.WriteByte(0xFE); il.WriteByte(0x17);                      // cpblk
        }
        // Reloc phase: unchanged body (writes pointers into the now-writable targets).
        foreach (var r in relocs)
        {
            // ldsflda ownerField
            il.WriteByte(0x7F); il.WriteInt32(0x04000000 | r.OwnerFieldRow);
            if (r.IntraOffset != 0)
            {
                EmitLdcI4(il, r.IntraOffset);
                il.WriteByte(0xD3);            // conv.i
                il.WriteByte(0x58);            // add
            }
            if (r.TargetIsMethod)
            {
                il.WriteByte(0xFE); il.WriteByte(0x06); il.WriteInt32(r.TargetToken); // ldftn
            }
            else
            {
                il.WriteByte(0x7F); il.WriteInt32(r.TargetToken);                     // ldsflda targetField
                if (r.Addend != 0)
                {
                    EmitLdcI4(il, checked((int)r.Addend));
                    il.WriteByte(0xD3);        // conv.i
                    il.WriteByte(0x58);        // add
                }
            }
            il.WriteByte(0xDF);                // stind.i
        }
        il.WriteByte(0x2A);                    // ret
        return il.ToArray();
    }

    private static void EmitLdcI4(BlobBuilder il, int v)
    {
        switch (v)
        {
            case 0: il.WriteByte(0x16); return;
            case 1: il.WriteByte(0x17); return;
            case 2: il.WriteByte(0x18); return;
            case 3: il.WriteByte(0x19); return;
            case 4: il.WriteByte(0x1A); return;
            case 5: il.WriteByte(0x1B); return;
            case 6: il.WriteByte(0x1C); return;
            case 7: il.WriteByte(0x1D); return;
            case 8: il.WriteByte(0x1E); return;
        }
        if (v >= sbyte.MinValue && v <= sbyte.MaxValue)
        {
            il.WriteByte(0x1F); il.WriteSByte((sbyte)v); return; // ldc.i4.s
        }
        il.WriteByte(0x20); il.WriteInt32(v);                    // ldc.i4
    }
}

internal static class HandleRowExtensions
{
    public static int RowId(this FieldDefinitionHandle h) => MetadataTokens.GetRowNumber(h);
}
