using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ColumnInfoTests
{
    // The three clauses of for(init; cond; incr) live on one source line but at
    // different columns. The embedded PDB must give them distinct sequence-point
    // columns so a debugger can place a breakpoint / step on a specific clause
    // instead of treating the whole line as one statement.
    [Fact]
    public void For_clauses_on_one_line_get_distinct_columns()
    {
        const string src =
            "int main(void){\n" +                          // 1
            "  int s = 0;\n" +                             // 2
            "  for (int i = 0; i < 3; i = i + 1) {\n" +    // 3  <- init/cond/incr
            "    s = s + i;\n" +                           // 4
            "  }\n" +                                      // 5
            "  return s;\n" +                              // 6
            "}\n";                                         // 7

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "cols.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), debuggable: true);

        using var peReader = new PEReader(ImmutableArray.Create(pe));
        var embedded = peReader.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        using var prov = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        var pdb = prov.GetMetadataReader();

        var columnsOnLine3 = new HashSet<int>();
        foreach (var mh in pdb.MethodDebugInformation)
        {
            var mdi = pdb.GetMethodDebugInformation(mh);
            if (mdi.SequencePointsBlob.IsNil) continue;
            foreach (var sp in mdi.GetSequencePoints())
                if (!sp.IsHidden && sp.StartLine == 3)
                    columnsOnLine3.Add(sp.StartColumn);
        }

        // init, condition, and increment are three distinct columns on line 3.
        Assert.True(columnsOnLine3.Count >= 3,
            $"expected >=3 distinct columns on the for-line, got [{string.Join(",", columnsOnLine3.OrderBy(c => c))}]");
        // and they are real columns, not the old hardcoded 1.
        Assert.DoesNotContain(1, columnsOnLine3);
    }
}
