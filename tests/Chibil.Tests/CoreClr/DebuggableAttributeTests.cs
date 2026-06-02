using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class DebuggableAttributeTests
{
    // Linking with -g must stamp [assembly: Debuggable(..., isJITOptimizerDisabled: true)].
    // Without it the JIT optimizes the C methods and VS/netcoredbg refuse to bind a
    // breakpoint ("no executable code of the debugger's target code type is associated
    // with this line") — even though the embedded PDB's sequence points are correct.
    const string Src =
        "int sq(int x){ int r = x * x; return r; }\n" +
        "int main(void){ return sq(7); }\n";

    [Fact]
    public void Dash_g_emits_debuggable_attribute_with_optimizer_disabled()
    {
        byte[] obj = TestCompiler.CompileToObj(Src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "dbg.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), debuggable: true);
        Assert.True(HasOptimizerDisabledDebuggable(pe));
    }

    [Fact]
    public void Without_dash_g_assembly_is_not_marked_debuggable()
    {
        byte[] obj = TestCompiler.CompileToObj(Src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "nodbg.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>());
        Assert.False(HasOptimizerDisabledDebuggable(pe));
    }

    // Read the assembly's custom attributes via MetadataReader (not Assembly.Load,
    // which would collide on the shared "a" identity across the two test cases) and
    // look for DebuggableAttribute with the isJITOptimizerDisabled fixed arg set.
    private static bool HasOptimizerDisabledDebuggable(byte[] pe)
    {
        using var peReader = new PEReader(ImmutableArray.Create(pe));
        var md = peReader.GetMetadataReader();
        foreach (var cah in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(cah);
            if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
            var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            if (md.GetString(tr.Name) != "DebuggableAttribute") continue;
            // value blob: 01 00 <isJITTrackingEnabled> <isJITOptimizerDisabled> 00 00
            var blob = md.GetBlobBytes(ca.Value);
            return blob.Length >= 4 && blob[3] == 1;
        }
        return false;
    }
}
