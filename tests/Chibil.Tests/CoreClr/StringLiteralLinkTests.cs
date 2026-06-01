using System.Collections.Generic;
using Chibil;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// Regression tests for cross-object string-literal (FieldRVA) data placement.
///
/// Two linker bugs caused string literals defined in a LATER object to read back
/// as zero once enough initialized data preceded them:
///   1. WritableDataPEBuilder.SerializeSection used BlobBuilder.LinkSuffix on the
///      shared field-data blob. ManagedPEBuilder serializes in multiple passes; the
///      first pass drained the blob, so later passes wrote a desynchronized/zeroed
///      .sdata whose bytes no longer matched the FieldRVA offsets.
///   2. FieldRvaRebaser inferred a single rebase delta from the placeholder RVAs,
///      whose base shifts by a file-alignment quantum once the field data crosses a
///      size threshold, mis-addressing every field uniformly.
/// </summary>
public class StringLiteralLinkTests
{
    static byte[] LinkSources(params string[] srcs)
    {
        var ofs = new List<ObjectFile>();
        int i = 0;
        foreach (var s in srcs)
            ofs.Add(ObjectFile.Load(TestCompiler.CompileToObj(s, TargetProfile.CoreClr, $"t{i++}.c"), $"t{i}.obj"));
        return LinkPipeline.LinkToBytes(ofs, new List<string>());
    }

    [Fact]
    public void String_literals_in_a_later_object_read_correctly()
    {
        // First object: enough initialized data (string literals) to push the second
        // object's literals well past the size threshold that triggered both bugs.
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < 40; k++)
            sb.Append($"const char *lit{k}(void){{ return \"string_literal_number_{k}_padding\"; }}\n");
        string first = sb.ToString();

        // Second object: main reads ITS OWN literals. Before the fix these read 0.
        string second = @"
int main(void){
  const char *a="":memory:"";
  const char *b=""hello"";
  const char *c=""CREATE TABLE t(a INTEGER);"";
  if ((unsigned char)a[0] != 58)  return 1;   /* ':' */
  if ((unsigned char)b[0] != 104) return 2;   /* 'h' */
  if ((unsigned char)b[4] != 111) return 3;   /* 'o' */
  if ((unsigned char)c[0] != 67)  return 4;   /* 'C' */
  if ((unsigned char)a[7] != 58)  return 5;   /* trailing ':' */
  return 55;
}";
        var asm = System.Reflection.Assembly.Load(LinkSources(first, second));
        Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
    }
}
