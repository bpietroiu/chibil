using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

/// <summary>
/// Builds a standalone Portable PDB from per-method debug info (documents +
/// sequence points), the managed debugging counterpart of the CodeView info chibil
/// emits per TU. The <c>MethodDebugInformation</c> table is 1:1 with
/// <c>MethodDef</c>, so the caller supplies the final method count and a map keyed
/// by FINAL <c>MethodDef</c> RID; this fills every row in order (nil where a method
/// has no debug info). Sequence-point blobs are encoded by hand per the Portable
/// PDB spec. Locals/scopes are a later increment.
/// </summary>
public static class PortablePdbWriter
{
    // SHA-256, the algorithm chibil hashes source files with.
    private static readonly Guid HashAlgorithmSha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    /// <summary>One source position attached to an IL offset.</summary>
    public sealed class SeqPoint
    {
        public int IlOffset;
        public int StartLine, StartColumn, EndLine, EndColumn;
        public bool Hidden;     // synthesized code with no source (0xFEEFEE)
    }

    public sealed class LocalVar
    {
        public int Slot;
        public string Name;
    }

    public sealed class MethodDebug
    {
        public string DocumentName;             // source file path
        public byte[] Hash;                     // SHA-256 of the source (or null)
        public List<SeqPoint> SequencePoints = new();
        public List<LocalVar> Locals = new();   // named C locals (one method-wide scope)
        public int IlSize;                      // method body IL byte length (scope length)
    }

    /// <param name="methodCount">total MethodDef rows in the PE (table is 1:1).</param>
    /// <param name="byRid">final-MethodDef-RID → debug info (sparse).</param>
    /// <param name="typeSystemRowCounts">the PE's metadata table row counts.</param>
    /// <param name="entryPointRid">entry-point MethodDef RID, 0 if none.</param>
    public static (byte[] pdb, BlobContentId id) Build(
        int methodCount,
        IReadOnlyDictionary<int, MethodDebug> byRid,
        ImmutableArray<int> typeSystemRowCounts,
        int entryPointRid)
    {
        var md = new MetadataBuilder();

        // Documents, deduped by path within this build.
        var docs = new Dictionary<string, DocumentHandle>(StringComparer.Ordinal);
        DocumentHandle DocFor(MethodDebug m)
        {
            if (docs.TryGetValue(m.DocumentName, out var h))
                return h;
            BlobHandle hashBlob = m.Hash is { Length: > 0 } ? md.GetOrAddBlob(m.Hash) : default;
            GuidHandle hashAlg = m.Hash is { Length: > 0 } ? md.GetOrAddGuid(HashAlgorithmSha256) : default;
            h = md.AddDocument(
                name: md.GetOrAddDocumentName(m.DocumentName),
                hashAlgorithm: hashAlg,
                hash: hashBlob,
                language: default);             // VS picks the editor from the .c extension
            docs[m.DocumentName] = h;
            return h;
        }

        // A single empty root import scope shared by every local scope (C has no
        // using-directives, but the format requires an ImportScope reference).
        var rootScope = md.AddImportScope(default, md.GetOrAddBlob(new BlobBuilder()));

        // MethodDebugInformation is 1:1 with MethodDef — emit a row for every RID,
        // in order, nil where there's no debug info. Local scopes/variables are
        // separate tables added in the same RID order.
        for (int rid = 1; rid <= methodCount; rid++)
        {
            byRid.TryGetValue(rid, out var m);

            var clean = m != null ? CleanSequencePoints(m.SequencePoints) : new List<SeqPoint>();
            if (clean.Count > 0)
                md.AddMethodDebugInformation(DocFor(m), md.GetOrAddBlob(EncodeSequencePoints(clean)));
            else
                md.AddMethodDebugInformation(default, default);

            if (m != null && m.Locals.Count > 0)
            {
                LocalVariableHandle first = default;
                for (int i = 0; i < m.Locals.Count; i++)
                {
                    var lv = m.Locals[i];
                    var h = md.AddLocalVariable(LocalVariableAttributes.None, lv.Slot,
                        md.GetOrAddString(string.IsNullOrEmpty(lv.Name) ? ("V_" + lv.Slot) : lv.Name));
                    if (i == 0) first = h;
                }
                md.AddLocalScope(
                    MetadataTokens.MethodDefinitionHandle(rid),
                    rootScope,
                    first,
                    MetadataTokens.LocalConstantHandle(1),    // no local constants
                    startOffset: 0,
                    length: m.IlSize > 0 ? m.IlSize : 1);
            }
        }

        var entryPoint = entryPointRid > 0
            ? MetadataTokens.MethodDefinitionHandle(entryPointRid)
            : default;

        var pdbBuilder = new PortablePdbBuilder(md, typeSystemRowCounts, entryPoint,
                                                idProvider: BlobContentId.GetTimeBasedProvider());
        var blob = new BlobBuilder();
        BlobContentId id = pdbBuilder.Serialize(blob);
        return (blob.ToArray(), id);
    }

    /// <summary>
    /// Sequence points must have strictly increasing IL offsets; chibil marks a line
    /// at the start of every expression, so duplicates and out-of-order marks at the
    /// same offset occur. Keep the first mark per IL offset, sorted by offset.
    /// </summary>
    private static List<SeqPoint> CleanSequencePoints(List<SeqPoint> pts)
    {
        var byOffset = new SortedDictionary<int, SeqPoint>();
        foreach (var p in pts)
            byOffset.TryAdd(p.IlOffset, p);
        return new List<SeqPoint>(byOffset.Values);
    }

    /// <summary>
    /// Encode the Portable-PDB sequence-points blob for a single-document method
    /// (the document is supplied separately to AddMethodDebugInformation, so the blob
    /// carries no initial-document record). Format per ECMA Portable PDB spec.
    /// </summary>
    private static BlobBuilder EncodeSequencePoints(List<SeqPoint> pts)
    {
        var b = new BlobBuilder();
        b.WriteCompressedInteger(0);    // header: LocalSignature rowid (0 = none)

        int prevOffset = 0, prevLine = -1, prevCol = -1;
        bool first = true;
        foreach (var sp in pts)
        {
            // δIL offset (absolute for the first record, delta thereafter; >0).
            b.WriteCompressedInteger(first ? sp.IlOffset : sp.IlOffset - prevOffset);
            prevOffset = sp.IlOffset;
            first = false;

            int dLines = sp.EndLine - sp.StartLine;
            int dCols = sp.EndColumn - sp.StartColumn;

            if (sp.Hidden || (dLines == 0 && dCols == 0))
            {
                // Hidden: ΔLines == 0 && ΔColumns == 0; no line/col data follows.
                b.WriteCompressedInteger(0);
                b.WriteCompressedInteger(0);
                continue;
            }

            b.WriteCompressedInteger(dLines);
            if (dLines == 0)
                b.WriteCompressedInteger(dCols);            // unsigned, nonzero
            else
                b.WriteCompressedSignedInteger(dCols);

            if (prevLine < 0)                                // first non-hidden point: absolute
            {
                b.WriteCompressedInteger(sp.StartLine);
                b.WriteCompressedInteger(sp.StartColumn);
            }
            else                                             // delta from previous non-hidden
            {
                b.WriteCompressedSignedInteger(sp.StartLine - prevLine);
                b.WriteCompressedSignedInteger(sp.StartColumn - prevCol);
            }
            prevLine = sp.StartLine;
            prevCol = sp.StartColumn;
        }
        return b;
    }
}
