using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ChibilLink;

/// <summary>Identity of a referenced managed assembly, read from its
/// AssemblyDefinition — enough to emit a matching AssemblyRef in the output.</summary>
public sealed record AssemblyIdentity(string Name, Version Version, string Culture, byte[] PublicKeyToken);

public static class ManagedReference
{
    public static AssemblyIdentity Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var asm = md.GetAssemblyDefinition();
        string name = md.GetString(asm.Name);
        string culture = asm.Culture.IsNil ? "" : md.GetString(asm.Culture);
        // A strong-named assembly stores a full public KEY; the AssemblyRef needs the
        // 8-byte TOKEN (low 8 bytes of SHA-1 of the key, reversed). Compute it.
        byte[] pk = asm.PublicKey.IsNil ? Array.Empty<byte>() : md.GetBlobBytes(asm.PublicKey);
        byte[] token = pk.Length == 0 ? Array.Empty<byte>() : PublicKeyToken(pk);
        return new AssemblyIdentity(name, asm.Version, culture, token);
    }

    private static byte[] PublicKeyToken(byte[] publicKey)
    {
        byte[] hash = System.Security.Cryptography.SHA1.HashData(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++) token[i] = hash[hash.Length - 1 - i];   // last 8 bytes, reversed
        return token;
    }
}
