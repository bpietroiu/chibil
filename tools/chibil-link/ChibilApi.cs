using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// The public-API manifest chibil emits in the <c>.chiapi</c> COFF section (v2): a facade
/// group name, the public function names, and the public struct/union/opaque type tags.
/// See CodeGen.BuildChiapiBlob.
/// </summary>
public sealed class ChibilApi
{
    public string Group = "";
    public readonly HashSet<string> Functions = new();
    public readonly HashSet<string> Types = new();   // public struct/union/opaque tags

    public static ChibilApi Parse(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'A' || br.ReadByte() != 'P' || br.ReadByte() != 'I')
            return null;
        int version = br.ReadByte();
        if (version != 2) return null;               // current format
        var api = new ChibilApi();
        int glen = br.ReadUInt16();
        api.Group = System.Text.Encoding.UTF8.GetString(br.ReadBytes(glen));
        int nf = br.ReadInt32();
        for (int i = 0; i < nf; i++)
        {
            int len = br.ReadUInt16();
            api.Functions.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        int nt = br.ReadInt32();
        for (int i = 0; i < nt; i++)
        {
            int len = br.ReadUInt16();
            api.Types.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        return api;
    }
}
