using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Chibil.CoffModel;

namespace ChibilLink;

public sealed class LinkException : Exception
{
    public LinkException(string m) : base(m) { }
}

public sealed class ObjMethod
{
    public string Name;
    public int OriginalToken;       // 0x06xxxxxx in this object
    public byte[] Il;               // raw IL bytes (operands hold ORIGINAL tokens)
    public Dictionary<int, int> TokenRelocs; // IL-relative offset -> original token to remap
    public int MaxStack;
    public bool InitLocals;
    public StandaloneSignatureHandle LocalSig;
    public MethodDefinitionHandle Handle;
}

public sealed unsafe class ObjectFile
{
    public string Path;
    public Machine Machine;
    public CoffFile Coff;
    public MetadataReader Md;       // reader over .cormeta
    public List<ObjMethod> Methods = new();

    private byte[] _metaBytes;      // backing store for Md (kept alive by this field)

    public static ObjectFile Load(byte[] bytes, string path)
    {
        var of = new ObjectFile { Path = path };
        of.Coff = CoffFile.Parse(bytes);
        of.Machine = (Machine)of.Coff.Header.Machine;

        var meta = of.Coff.FindSection(".cormeta")
            ?? throw new LinkException($"{path}: no .cormeta section");
        of._metaBytes = of.Coff.GetSectionData(meta).ToArray();

        fixed (byte* p = of._metaBytes)
            of.Md = new MetadataReader(p, of._metaBytes.Length);

        var bodyLoc = of.Coff.BuildMethodBodyLocationMap();
        foreach (var mh in of.Md.MethodDefinitions)
        {
            var md = of.Md.GetMethodDefinition(mh);
            int token = MetadataTokens.GetToken(mh);
            if ((md.ImplAttributes & MethodImplAttributes.ForwardRef) != 0) continue; // extern decl
            if (!bodyLoc.TryGetValue(token, out var loc)) continue;                    // no body

            var sec = of.Coff.GetSection(loc.SectionNumber);
            byte[] secData = of.Coff.GetSectionData(sec).ToArray();
            var relocMap = of.Coff.BuildTokenRelocationMap(sec); // section-offset -> token

            MethodBodyBlock body;
            fixed (byte* sp = secData)
            {
                var br = new BlobReader(sp + loc.Offset, secData.Length - loc.Offset);
                body = MethodBodyBlock.Create(br);
            }
            byte[] il = body.GetILBytes();

            // Translate section-relative reloc offsets to IL-relative offsets.
            // TODO(D1): body.Size = header + IL + EH regions, so (Size - il.Length)
            // over-counts the header by the EH-region size when exception regions
            // exist, shifting reloc offsets. Exact for EH-free methods (all current
            // chibil output). D1 validates offsets; switch to a direct header-size
            // read there: tiny header = 1 byte when (b & 3)==2, fat = 12 when (b & 3)==3.
            int ilStartInSection = loc.Offset + (body.Size - il.Length); // header precedes IL
            var ilRelocs = new Dictionary<int, int>();
            foreach (var kv in relocMap)
            {
                int ilOff = kv.Key - ilStartInSection;
                if (ilOff >= 0 && ilOff + 4 <= il.Length) ilRelocs[ilOff] = kv.Value;
            }

            of.Methods.Add(new ObjMethod
            {
                Name = of.Md.GetString(md.Name),
                OriginalToken = token,
                Il = il,
                TokenRelocs = ilRelocs,
                MaxStack = body.MaxStack,
                InitLocals = body.LocalVariablesInitialized,
                LocalSig = body.LocalSignature,
                Handle = mh,
            });
        }
        return of;
    }
}
