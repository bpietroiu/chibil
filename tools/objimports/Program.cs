using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ChibilLink;

// Usage: objimports <dir-of-.obj-files> [data]
//   default : external FUNCTION symbols referenced but defined by none (libc surface)
//   "data"  : external DATA symbols (globals) — names that appear as a FieldDef
//             but are DEFINED (HasFieldRVA with section data) in no object. This is
//             the data-import surface: cross-TU globals + genuine libc-data imports.
if (args.Length < 1) { Console.Error.WriteLine("usage: objimports <obj-dir> [data]"); return 1; }
bool dataMode = args.Length > 1 && args[1] == "data";
bool fieldsMode = args.Length > 1 && args[1] == "fields";

if (fieldsMode)
{
    foreach (string path in Directory.GetFiles(args[0], "*.obj"))
    {
        var of = ObjectFile.Load(File.ReadAllBytes(path), Path.GetFileName(path));
        var md = of.Md;
        var loc = of.Coff.BuildFieldDataLocationMap();
        Console.WriteLine($"# {Path.GetFileName(path)}");
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.Field); r++)
        {
            var fh = MetadataTokens.FieldDefinitionHandle(r);
            var fd = md.GetFieldDefinition(fh);
            int tok = MetadataTokens.GetToken(fh);
            bool rva = (fd.Attributes & System.Reflection.FieldAttributes.HasFieldRVA) != 0;
            string inMap = loc.TryGetValue(tok, out var l) ? $"sect={l.SectionNumber} off={l.Offset}" : "NOT-IN-MAP";
            Console.WriteLine($"  row{r} 0x{tok:X8} {md.GetString(fd.Name),-20} rva={rva} {inMap}");
        }
    }
    return 0;
}

var defined = new HashSet<string>();
var referenced = new HashSet<string>();
int nobj = 0, nbad = 0;

foreach (string path in Directory.GetFiles(args[0], "*.obj"))
{
    ObjectFile of;
    try { of = ObjectFile.Load(File.ReadAllBytes(path), Path.GetFileName(path)); }
    catch (Exception e) { nbad++; Console.Error.WriteLine($"skip {Path.GetFileName(path)}: {e.Message}"); continue; }
    nobj++;
    var md = of.Md;

    if (dataMode)
    {
        // A field is a global DEFINITION when it carries HasFieldRVA (initialized
        // data or a BSS slot). Every FieldDef name is "declared"; declared-minus-
        // defined = extern data references with no definition in this set.
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.Field); r++)
        {
            var fd = md.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(r));
            string name = md.GetString(fd.Name);
            if (string.IsNullOrEmpty(name)) continue;
            // Skip compiler-generated string-literal fields (not real globals).
            if (name.StartsWith("?") || name.StartsWith("$") || name.StartsWith("__sl")) continue;
            referenced.Add(name);                                    // declared
            if ((fd.Attributes & System.Reflection.FieldAttributes.HasFieldRVA) != 0)
                defined.Add(name);                                   // defined
        }
        continue;
    }

    // Functions this object DEFINES.
    foreach (var m in of.Methods)
        if (!string.IsNullOrEmpty(m.Name)) defined.Add(m.Name);

    // External function references: a MemberRef whose parent is the <Module> TypeDef
    // and whose kind is Method (mirrors SymbolResolver's unresolved-symbol detection).
    for (int r = 1; r <= md.GetTableRowCount(TableIndex.MemberRef); r++)
    {
        var mr = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
        if (mr.Parent.Kind != HandleKind.TypeDefinition) continue;
        var td = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
        if (md.GetString(td.Name) != "<Module>") continue;
        if (mr.GetKind() != MemberReferenceKind.Method) continue;
        referenced.Add(md.GetString(mr.Name));
    }
}

referenced.ExceptWith(defined);   // imports = referenced/declared but defined nowhere

foreach (string s in referenced.OrderBy(x => x, StringComparer.Ordinal))
    Console.WriteLine(s);

Console.Error.WriteLine($"\nobjs={nobj} unreadable={nbad} defined={defined.Count} {(dataMode ? "data-imports" : "imports")}={referenced.Count}");
return 0;
