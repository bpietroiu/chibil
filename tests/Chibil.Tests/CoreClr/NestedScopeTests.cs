using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class NestedScopeTests
{
    // Block-scoped locals must land in nested LocalScopes, not one flat method-wide
    // scope. Two same-named locals in sibling blocks (the classic shadowing case)
    // must resolve to disjoint scopes so the debugger shows the right one.
    [Fact]
    public void Shadowed_locals_get_disjoint_scopes_and_blocks_nest()
    {
        const string src =
            "int main(void){\n" +
            "  int sum = 0;\n" +
            "  for (int i = 0; i < 3; i = i + 1) { int t = i * i; sum = sum + t; }\n" +
            "  { int i = 99; sum = sum + i; }\n" +
            "  return sum;\n" +
            "}\n";

        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "scopes.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), debuggable: true);

        using var peReader = new PEReader(ImmutableArray.Create(pe));
        var embedded = peReader.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        using var prov = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        var pdb = prov.GetMetadataReader();

        var scopes = new List<(int Start, int End, HashSet<string> Names)>();
        foreach (var sh in pdb.LocalScopes)
        {
            var s = pdb.GetLocalScope(sh);
            var names = new HashSet<string>();
            foreach (var lvh in s.GetLocalVariables())
                names.Add(pdb.GetString(pdb.GetLocalVariable(lvh).Name));
            scopes.Add((s.StartOffset, s.StartOffset + s.Length, names));
        }

        // Two distinct scopes each declare an `i` (loop var vs the standalone block).
        var iScopes = scopes.Where(s => s.Names.Contains("i")).ToList();
        Assert.True(iScopes.Count >= 2, $"expected >=2 scopes declaring 'i', got {iScopes.Count}");

        // Some pair of them is disjoint (one ends at or before the other begins) —
        // that disjointness is what makes shadowing resolve correctly.
        bool disjointPair = iScopes.Any(a => iScopes.Any(b => a.End <= b.Start));
        Assert.True(disjointPair, "the two 'i' scopes overlap — shadowing would resolve to the wrong slot");

        // `t` lives in a scope nested inside the loop's `i` scope.
        var tScope = scopes.Single(s => s.Names.Contains("t"));
        Assert.Contains(iScopes, i => i.Start <= tScope.Start && tScope.End <= i.End && !i.Names.Contains("t"));

        // `sum` is visible across the whole method (outermost scope contains the rest).
        var sumScope = scopes.Single(s => s.Names.Contains("sum"));
        Assert.All(scopes, s => Assert.True(s.Start >= sumScope.Start && s.End <= sumScope.End));
    }
}
