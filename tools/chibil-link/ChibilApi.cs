using System.Collections.Generic;

namespace ChibilLink;

/// <summary>
/// The public-API manifest chibil emits in the <c>.chiapi</c> COFF section (v3): a facade
/// group name, the public function names, the public struct/union/opaque type tags,
/// the public enum definitions, and the enum usages on public function parameters/returns.
/// See CodeGen.BuildChiapiBlob.
/// </summary>
public sealed class ChibilApi
{
    public string Group = "";
    public readonly HashSet<string> Functions = new();
    public readonly HashSet<string> Types = new();   // public struct/union/opaque tags
    public readonly List<ApiEnum> Enums = new();
    public readonly List<ApiEnumUse> EnumUsages = new();

    public static ChibilApi Parse(byte[] data)
    {
        using var br = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
        if (br.ReadByte() != 'C' || br.ReadByte() != 'A' || br.ReadByte() != 'P' || br.ReadByte() != 'I')
            return null;
        int version = br.ReadByte();
        if (version != 3) return null;               // current format (v3)
        var api = new ChibilApi();
        api.Group = ReadStr(br);
        int nf = br.ReadInt32();
        for (int i = 0; i < nf; i++)
            api.Functions.Add(ReadStr(br));
        int nt = br.ReadInt32();
        for (int i = 0; i < nt; i++)
            api.Types.Add(ReadStr(br));
        // Enums section (v3)
        int ne = br.ReadInt32();
        for (int i = 0; i < ne; i++)
        {
            var e = new ApiEnum();
            e.Tag = ReadStr(br);
            e.IsUnsigned = br.ReadByte() != 0;
            int mc = br.ReadInt32();
            for (int j = 0; j < mc; j++)
            {
                string n = ReadStr(br);
                int v = br.ReadInt32();
                e.Members.Add((n, v));
            }
            api.Enums.Add(e);
        }
        // Usages section (v3)
        int nu = br.ReadInt32();
        for (int i = 0; i < nu; i++)
        {
            var u = new ApiEnumUse();
            u.Function = ReadStr(br);
            u.Position = br.ReadInt32();
            u.EnumTag = ReadStr(br);
            api.EnumUsages.Add(u);
        }
        return api;
    }

    private static string ReadStr(System.IO.BinaryReader br)
    {
        int len = br.ReadUInt16();
        return System.Text.Encoding.UTF8.GetString(br.ReadBytes(len));
    }
}

public sealed class ApiEnum
{
    public string Tag = "";
    public bool IsUnsigned;
    public readonly List<(string Name, int Value)> Members = new();
}

public sealed class ApiEnumUse
{
    public string Function = "";
    public int Position;
    public string EnumTag = "";
}
