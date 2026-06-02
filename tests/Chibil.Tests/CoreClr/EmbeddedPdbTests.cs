using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class EmbeddedPdbTests
{
    // The linked PE must carry an embedded Portable PDB whose sequence points map
    // IL offsets to the original C source lines — the end-to-end managed-debug path
    // (chibil .chidbg -> chibil-link transcode -> embedded PDB -> VS).
    [Fact]
    public void Linked_pe_has_embedded_portable_pdb_with_source_lines()
    {
        const string src =
            "int sq(int x){\n" +            // line 1
            "  int r = x * x;\n" +          // line 2
            "  return r;\n" +               // line 3
            "}\n" +                         // line 4
            "int main(void){ return sq(7); }\n";   // line 5

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "epdb.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());

        using var peReader = new PEReader(ImmutableArray.Create(pe));

        // the PE advertises an embedded Portable PDB in its debug directory
        var entries = peReader.ReadDebugDirectory();
        Assert.Contains(entries, e => e.Type == DebugDirectoryEntryType.CodeView);
        var embedded = entries.Single(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        using var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        var pdb = pdbProvider.GetMetadataReader();

        // source document present
        Assert.True(pdb.Documents.Count >= 1);

        // across all methods, the sequence points cover the C body lines
        var lines = new HashSet<int>();
        foreach (var mh in pdb.MethodDebugInformation)
        {
            var mdi = pdb.GetMethodDebugInformation(mh);
            if (mdi.SequencePointsBlob.IsNil)
                continue;
            foreach (var sp in mdi.GetSequencePoints())
                if (!sp.IsHidden)
                    lines.Add(sp.StartLine);
        }
        Assert.Contains(2, lines);   // int r = x * x;
        Assert.Contains(3, lines);   // return r;
        Assert.Contains(5, lines);   // main

        // named locals are present — sq() declares `r`
        var localNames = new HashSet<string>();
        foreach (var lvh in pdb.LocalVariables)
            localNames.Add(pdb.GetString(pdb.GetLocalVariable(lvh).Name));
        Assert.Contains("r", localNames);
    }
}
