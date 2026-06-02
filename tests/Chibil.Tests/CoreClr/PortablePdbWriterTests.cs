using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class PortablePdbWriterTests
{
    [Fact]
    public void Roundtrips_documents_and_sequence_points()
    {
        var m1 = new PortablePdbWriter.MethodDebug
        {
            DocumentName = "/src/foo.c",
            Hash = new byte[32],
            SequencePoints =
            {
                new() { IlOffset = 0,  StartLine = 10, StartColumn = 1, EndLine = 10, EndColumn = 20 },
                new() { IlOffset = 7,  StartLine = 11, StartColumn = 1, EndLine = 11, EndColumn = 15 },
                new() { IlOffset = 12, StartLine = 13, StartColumn = 3, EndLine = 13, EndColumn = 9 },
            },
        };
        var m3 = new PortablePdbWriter.MethodDebug
        {
            DocumentName = "/src/foo.c",
            Hash = new byte[32],
            SequencePoints = { new() { IlOffset = 0, StartLine = 42, StartColumn = 1, EndLine = 42, EndColumn = 8 } },
        };
        var byRid = new Dictionary<int, PortablePdbWriter.MethodDebug> { [1] = m1, [3] = m3 };

        var (pdb, _) = PortablePdbWriter.Build(
            methodCount: 3, byRid,
            typeSystemRowCounts: ImmutableArray.CreateRange(new int[MetadataTokens.TableCount]),
            entryPointRid: 0);
        Assert.NotEmpty(pdb);

        using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdb));
        var r = provider.GetMetadataReader();

        // exactly one (deduped) document, with the right name
        Assert.Equal(1, r.Documents.Count);
        var doc = r.GetDocument(MetadataTokens.DocumentHandle(1));
        Assert.Equal("/src/foo.c", r.GetString(doc.Name));

        // method 1 — three sequence points at the expected (IL offset, line)
        var mdi1 = r.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(1));
        var pts = new List<(int, int)>();
        foreach (var sp in mdi1.GetSequencePoints())
            pts.Add((sp.Offset, sp.StartLine));
        Assert.Equal(new[] { (0, 10), (7, 11), (12, 13) }, pts);

        // method 2 — nil row (no debug info)
        var mdi2 = r.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(2));
        Assert.True(mdi2.Document.IsNil);
        Assert.True(mdi2.SequencePointsBlob.IsNil);

        // method 3 — single point on line 42
        var mdi3 = r.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(3));
        int count = 0, line = 0;
        foreach (var sp in mdi3.GetSequencePoints()) { count++; line = sp.StartLine; }
        Assert.Equal(1, count);
        Assert.Equal(42, line);
    }
}
