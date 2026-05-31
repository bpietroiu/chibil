using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// Export table of every DEFINED function across all linked objects: function
/// NAME → merged MethodDef token. Built after the merger has predicted the
/// MethodDef rows of all bodied methods, so each defined function's final token
/// is known. Cross-object references resolve against this table.
/// </summary>
public sealed class LinkSymbolTable
{
    // function NAME -> merged MethodDef token, for every DEFINED function.
    public readonly Dictionary<string, int> DefinedMethodToken = new();

    public void AddDefined(ObjectFile of, MetadataMerger merger)
    {
        foreach (var m in of.Methods)              // ObjectFile.Methods are bodied (defined) funcs
            DefinedMethodToken[m.Name] = merger.MapToken(of, m.OriginalToken); // last wins (COMDAT-ish)
    }
}
