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
    private System.Runtime.InteropServices.GCHandle _metaPin; // pins _metaBytes for Md's lifetime

    public static ObjectFile Load(byte[] bytes, string path)
    {
        var of = new ObjectFile { Path = path };
        of.Coff = CoffFile.Parse(bytes);
        of.Machine = (Machine)of.Coff.Header.Machine;

        var meta = of.Coff.FindSection(".cormeta")
            ?? throw new LinkException($"{path}: no .cormeta section");
        of._metaBytes = of.Coff.GetSectionData(meta).ToArray();

        // The MetadataReader holds a RAW pointer into _metaBytes. A `fixed` block
        // only pins for its own scope, so once it exits the GC may relocate the
        // managed array out from under the reader — harmless for tiny objects, but
        // with SQLite's multi-MB .cormeta and the allocation pressure of the merge
        // the array DOES move, and the reader then reads freed/moved memory
        // ("Read out of bounds"). Pin for the ObjectFile's entire lifetime.
        of._metaPin = System.Runtime.InteropServices.GCHandle.Alloc(
            of._metaBytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        byte* p = (byte*)of._metaPin.AddrOfPinnedObject();
        of.Md = new MetadataReader(p, of._metaBytes.Length);

        var bodyLoc = of.Coff.BuildMethodBodyLocationMap();
        foreach (var mh in of.Md.MethodDefinitions)
        {
            var md = of.Md.GetMethodDefinition(mh);
            int token = MetadataTokens.GetToken(mh);
            if ((md.ImplAttributes & MethodImplAttributes.ForwardRef) != 0) continue; // extern decl
            if (!bodyLoc.TryGetValue(token, out var loc)) continue;                    // no body

            var sec = of.Coff.GetSection(loc.SectionNumber);
            // Read PATCHED section data: in a managed COFF obj, the fat method
            // header's LocalVarSigTok (and the IL operand tokens) are filled in by
            // CLR token relocations. The raw bytes carry 0 there, so the local-var
            // signature would parse as nil. The patched bytes have the ORIGINAL
            // tokens stamped in, which MethodBodyBlock.Create then surfaces as the
            // original StandaloneSignatureHandle. (RelocationFixer still remaps the
            // original IL operand tokens via TokenRelocs, which come from the
            // relocation TABLE and are independent of whether bytes are patched.)
            byte[] secData = of.Coff.GetPatchedSectionData(sec);
            var relocMap = of.Coff.BuildTokenRelocationMap(sec); // section-offset -> token

            MethodBodyBlock body;
            fixed (byte* sp = secData)
            {
                var br = new BlobReader(sp + loc.Offset, secData.Length - loc.Offset);
                body = MethodBodyBlock.Create(br);
            }
            byte[] il = body.GetILBytes();

            // Translate section-relative reloc offsets to IL-relative offsets.
            // Compute the header size directly from the body's first byte rather
            // than (body.Size - il.Length): body.Size includes 4-byte-aligned EH
            // regions, which would over-count the header when EH regions exist.
            byte firstByte = secData[loc.Offset];
            int headerSize = (firstByte & 0x03) == 0x02 ? 1   // tiny header
                           : (firstByte & 0x03) == 0x03 ? 12  // fat header
                           : throw new LinkException(
                                 $"{path}: method '{of.Md.GetString(md.Name)}' has an " +
                                 $"unrecognized method-body header byte 0x{firstByte:X2}.");
            int ilStartInSection = loc.Offset + headerSize; // header precedes IL
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
