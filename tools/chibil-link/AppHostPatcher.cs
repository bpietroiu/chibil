using System;
using System.Text;

namespace ChibilLink;

/// <summary>
/// Patches a copied apphost template in place: finds the 64-byte placeholder the
/// .NET apphost embeds and overwrites it with the managed assembly's filename, so
/// the launcher knows which dll to load. Writing a non-placeholder value into the
/// slot is also what marks the apphost "bound".
/// </summary>
public static class AppHostPatcher
{
    // Microsoft.NET.HostModel AppBinaryPathPlaceholder — 64 ASCII bytes.
    private static readonly byte[] Placeholder = Encoding.ASCII.GetBytes(
        "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2");

    /// <summary>
    /// Overwrites the placeholder in <paramref name="image"/> with
    /// <paramref name="appDllFileName"/> (UTF-8, NUL-terminated, zero-padded to the
    /// 64-byte slot). Returns false (leaving the image unchanged) if the placeholder
    /// is absent or the name does not fit with room for a terminator.
    /// </summary>
    public static bool Patch(byte[] image, string appDllFileName)
    {
        int offset = IndexOf(image, Placeholder);
        if (offset < 0) return false;

        byte[] name = Encoding.UTF8.GetBytes(appDllFileName);
        if (name.Length + 1 > Placeholder.Length) return false; // need room for NUL

        Buffer.BlockCopy(name, 0, image, offset, name.Length);
        for (int i = name.Length; i < Placeholder.Length; i++)
            image[offset + i] = 0; // NUL terminator + zero padding
        return true;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
