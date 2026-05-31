using System;
using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// Rewrites the in-IL token operands of a method body so they reference the
/// FINAL merged metadata rows instead of the per-object original tokens.
///
/// <see cref="ObjMethod.Il"/> holds raw IL whose token operands still carry the
/// ORIGINAL tokens of the source object. <see cref="ObjMethod.TokenRelocs"/> maps
/// each IL-relative byte offset of a token operand to the original token there.
/// We clone the IL and overwrite every such 4-byte slot (little-endian) with the
/// merger's predicted final token.
/// </summary>
public static class RelocationFixer
{
    public static byte[] Fix(ObjMethod m, ObjectFile of, MetadataMerger merger)
    {
        byte[] il = (byte[])m.Il.Clone();
        foreach (KeyValuePair<int, int> kv in m.TokenRelocs)
        {
            int ilOffset = kv.Key;
            int originalToken = kv.Value;
            if (ilOffset < 0 || ilOffset + 4 > il.Length)
                throw new LinkException(
                    $"{of.Path}: method '{m.Name}' has a token reloc at IL offset {ilOffset} " +
                    $"outside the IL bounds (len {il.Length}).");

            int finalToken = merger.MapToken(of, originalToken);
            il[ilOffset + 0] = (byte)(finalToken & 0xFF);
            il[ilOffset + 1] = (byte)((finalToken >> 8) & 0xFF);
            il[ilOffset + 2] = (byte)((finalToken >> 16) & 0xFF);
            il[ilOffset + 3] = (byte)((finalToken >> 24) & 0xFF);
        }
        return il;
    }
}
