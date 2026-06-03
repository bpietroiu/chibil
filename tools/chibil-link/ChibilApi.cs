using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// The public-API manifest chibil emits in the <c>.chiapi</c> COFF section: a facade
/// group name (the public header's base name, used for the namespace) and the names of
/// the functions declared in that header. See CodeGen.BuildChiapiBlob.
/// </summary>
public sealed class ChibilApi
{
    public string Group = "";
    public readonly HashSet<string> Functions = new();

    public static ChibilApi Parse(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'A' || br.ReadByte() != 'P' || br.ReadByte() != 'I')
            return null;
        if (br.ReadByte() != 1) return null; // version
        var api = new ChibilApi();
        int glen = br.ReadUInt16();
        api.Group = System.Text.Encoding.UTF8.GetString(br.ReadBytes(glen));
        int n = br.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int len = br.ReadUInt16();
            api.Functions.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
        }
        return api;
    }
}
