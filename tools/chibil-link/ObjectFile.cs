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
    // Exception-handling clauses (setjmp/longjmp filter regions). IL-relative
    // offsets — preserved verbatim because RelocationFixer only rewrites operand
    // tokens, never resizing the IL. Catch clauses carry a type token to remap.
    public System.Collections.Immutable.ImmutableArray<System.Reflection.Metadata.ExceptionRegion> ExceptionRegions;
}

public sealed unsafe class ObjectFile
{
    public string Path;
    public Machine Machine;
    public CoffFile Coff;
    public MetadataReader Md;       // reader over .cormeta
    public List<ObjMethod> Methods = new();
    public ChibilDebug Debug;       // managed-PDB side-stream (.chidbg), or null
    public ChibilApi Api;       // parsed .chiapi public-API manifest, or null

    /// <summary>Parsed .chidbg: the TU's source file + per-method line points and
    /// named locals, keyed by the object's LOCAL MethodDef RID (remapped to final
    /// RIDs at link).</summary>
    public sealed class ChibilDebug
    {
        public string SourceFile;
        public byte[] SourceHash;
        public Dictionary<int, MethodDbg> Methods = new();
    }

    public sealed class MethodDbg
    {
        public int IlSize;
        public List<(int Il, int Line, int StartCol, int EndCol)> Points = new();
        public List<ScopeDbg> Scopes = new();
    }

    /// <summary>A lexical block scope: its IL range and the locals it declares.</summary>
    public sealed class ScopeDbg
    {
        public int Start;
        public int Length;
        public List<(int Slot, string Name)> Locals = new();
    }

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
                ExceptionRegions = body.ExceptionRegions,
            });
        }

        var dbgSec = of.Coff.FindSection(".chidbg");
        if (dbgSec != null)
            of.Debug = ParseChibilDebug(of.Coff.GetSectionData(dbgSec.Value).ToArray());

        var apiSec = of.Coff.FindSection(".chiapi");
        if (apiSec != null)
            of.Api = ChibilApi.Parse(of.Coff.GetSectionData(apiSec.Value).ToArray());

        return of;
    }

    // Parse the .chidbg side-stream emitted by chibil (see CodeGen.BuildChibilDebugBlob):
    // magic 'CDBG', version 4, source path + SHA-256, then per-method (RID, IL size,
    // line points with columns, and nested lexical scopes with their named locals).
    private static ChibilDebug ParseChibilDebug(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'D' || br.ReadByte() != 'B' || br.ReadByte() != 'G')
            return null;
        int version = br.ReadByte();
        if (version != 4)
            return null;

        var dbg = new ChibilDebug();
        int pathLen = br.ReadUInt16();
        dbg.SourceFile = System.Text.Encoding.UTF8.GetString(br.ReadBytes(pathLen));
        int hashLen = br.ReadByte();
        dbg.SourceHash = br.ReadBytes(hashLen);

        int methodCount = br.ReadInt32();
        for (int m = 0; m < methodCount; m++)
        {
            int rid = br.ReadInt32();
            var info = new MethodDbg { IlSize = br.ReadInt32() };

            int ptCount = br.ReadInt32();
            for (int p = 0; p < ptCount; p++)
            {
                int il = br.ReadInt32();
                int line = br.ReadInt32();
                int sc = br.ReadInt32();
                int ec = br.ReadInt32();
                info.Points.Add((il, line, sc, ec));
            }

            int scopeCount = br.ReadInt32();
            for (int s = 0; s < scopeCount; s++)
            {
                var scope = new ScopeDbg { Start = br.ReadInt32(), Length = br.ReadInt32() };
                int locCount = br.ReadInt32();
                for (int l = 0; l < locCount; l++)
                {
                    int slot = br.ReadInt32();
                    int nameLen = br.ReadUInt16();
                    string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    scope.Locals.Add((slot, name));
                }
                info.Scopes.Add(scope);
            }

            dbg.Methods[rid] = info;
        }
        return dbg;
    }
}
