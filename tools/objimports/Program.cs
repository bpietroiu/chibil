using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ChibilLink;

// "tables <file.dll>" — authoritative metadata dump for debugging the load bug.
if (args.Length >= 2 && args[0] == "tables")
{
    using var fs = File.OpenRead(args[1]);
    using var pe = new PEReader(fs);
    var r = pe.GetMetadataReader();
    Console.WriteLine($"== {Path.GetFileName(args[1])} ==");
    foreach (TableIndex ti in Enum.GetValues(typeof(TableIndex)))
    {
        int c = r.GetTableRowCount(ti);
        if (c > 0) Console.WriteLine($"  {ti,-18} {c}");
    }
    // Try decoding every MethodDef signature; report the first failures.
    int ok = 0, bad = 0;
    var prov = new DummySig();
    foreach (var mh in r.MethodDefinitions)
    {
        var md = r.GetMethodDefinition(mh);
        try
        {
            md.DecodeSignature(prov, null);
            ok++;
        }
        catch (Exception e)
        {
            if (bad < 5) Console.WriteLine($"  SIG-FAIL {r.GetString(md.Name)}: {e.GetType().Name} {e.Message}");
            bad++;
        }
    }
    Console.WriteLine($"  method sigs: ok={ok} bad={bad}");
    return 0;
}

// "sig <file.dll|.obj> <methodName>" — dump a method's raw signature blob hex.
if (args.Length >= 3 && args[0] == "sig")
{
    string target = args[2];
    if (args[1].EndsWith(".obj"))
    {
        var of = ObjectFile.Load(File.ReadAllBytes(args[1]), Path.GetFileName(args[1]));
        var mr = of.Md;
        foreach (var m in of.Methods)
            if (m.Name == target)
            {
                var sig = mr.GetMethodDefinition(m.Handle).Signature;
                Console.WriteLine($"OBJ {target}: {Convert.ToHexString(mr.GetBlobBytes(sig))}");
            }
    }
    else
    {
        using var fs = File.OpenRead(args[1]);
        using var pe = new PEReader(fs);
        var r = pe.GetMetadataReader();
        foreach (var mh in r.MethodDefinitions)
        {
            var md = r.GetMethodDefinition(mh);
            if (r.GetString(md.Name) == target)
                Console.WriteLine($"DLL {target}: {Convert.ToHexString(r.GetBlobBytes(md.Signature))}");
        }
    }
    return 0;
}

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
