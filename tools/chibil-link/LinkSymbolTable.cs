using System.Collections.Generic;
using System.Reflection.Metadata;

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

    // function NAME -> fixed-param count, for DEFINED chibil variadics lowered with
    // the Layer-1 va-buffer ABI (a trailing hidden param named "__va"). A cross-TU
    // call to one of these arrives as a Layer-2 (cdecl, no __va) MemberRef and must be
    // bridged by a synthesized adapter — see SymbolResolver.
    public readonly Dictionary<string, int> Layer1VariadicFixed = new();

    public void AddDefined(ObjectFile of, MetadataMerger merger)
    {
        foreach (var m in of.Methods)              // ObjectFile.Methods are bodied (defined) funcs
        {
            DefinedMethodToken[m.Name] = merger.MapToken(of, m.OriginalToken); // last wins (COMDAT-ish)

            var def = of.Md.GetMethodDefinition(m.Handle);
            foreach (var ph in def.GetParameters())
            {
                if (of.Md.GetString(of.Md.GetParameter(ph).Name) == "__va")
                {
                    Layer1VariadicFixed[m.Name] = merger.MethodParamCount(of, m) - 1; // minus the __va param
                    break;
                }
            }
        }
    }
}
