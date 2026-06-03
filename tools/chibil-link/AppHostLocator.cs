#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ChibilLink;

/// <summary>
/// Locates a .NET apphost template to copy. Prefers the SDK's AppHostTemplate
/// (what `dotnet build` uses), falling back to the runtime host pack. Returns null
/// — never throws — when no template can be found, so the linker degrades to a
/// warn-and-skip and still emits a runnable dll.
/// </summary>
public static class AppHostLocator
{
    private static string ExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "apphost.exe" : "apphost";

    /// <summary>Host RID component used in the runtime-pack path (win-x64 / linux-x64 / -arm64).</summary>
    private static string Rid
    {
        get
        {
            // Windows and Linux are the supported hosts; other OSes (macOS, etc.)
            // are out of scope and fall through to the linux RID.
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "linux";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// Resolve candidate dotnet roots (DOTNET_ROOT, then the root behind each `dotnet`
    /// on PATH) and return the first that yields a template, else null.
    /// </summary>
    public static string? Find()
    {
        foreach (string root in CandidateRoots())
        {
            string? hit = FindInRoot(root);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// The dotnet root for a given path to the `dotnet` executable: the directory of
    /// the symlink's final target (e.g. /usr/bin/dotnet -> /usr/lib/dotnet/dotnet, so
    /// the root is /usr/lib/dotnet), or the file's own directory when it is not a link.
    /// Public for hermetic testing.
    /// </summary>
    public static string DirOfResolvedDotnet(string dotnetFilePath)
    {
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(dotnetFilePath, returnFinalTarget: true);
            string resolved = target?.FullName ?? dotnetFilePath; // null when not a symlink
            return Path.GetDirectoryName(Path.GetFullPath(resolved))
                   ?? Path.GetFullPath(dotnetFilePath);
        }
        catch
        {
            return Path.GetDirectoryName(Path.GetFullPath(dotnetFilePath)) ?? dotnetFilePath;
        }
    }

    /// <summary>Search a specific dotnet root. Public for hermetic testing.</summary>
    public static string? FindInRoot(string dotnetRoot)
    {
        // A — SDK AppHostTemplate (highest version).
        string sdkDir = Path.Combine(dotnetRoot, "sdk");
        string? sdk = HighestVersionDir(sdkDir);
        if (sdk != null)
        {
            string p = Path.Combine(sdk, "AppHostTemplate", ExeName);
            if (File.Exists(p)) return p;
        }

        // B — runtime host pack (highest version).
        string packDir = Path.Combine(dotnetRoot, "packs", $"Microsoft.NETCore.App.Host.{Rid}");
        string? pack = HighestVersionDir(packDir);
        if (pack != null)
        {
            string p = Path.Combine(pack, "runtimes", Rid, "native", ExeName);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>
    /// Candidate dotnet roots to probe, in priority order: DOTNET_ROOT, then for each
    /// `dotnet` found on PATH both its symlink-resolved root (handles /usr/bin/dotnet ->
    /// /usr/lib/dotnet on WSL/Debian) and the raw PATH directory (handles installs where
    /// `dotnet` sits directly in the root, e.g. C:\Program Files\dotnet).
    /// </summary>
    private static IEnumerable<string> CandidateRoots()
    {
        string? env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) yield return env;

        string dotnet = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) yield break;

        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string? resolved = null;
            try
            {
                string full = Path.Combine(dir, dotnet);
                if (File.Exists(full)) resolved = DirOfResolvedDotnet(full);
            }
            catch { /* malformed PATH entry */ }

            if (resolved == null) continue;
            yield return resolved;     // symlink target's dir (the real root on WSL/Debian)
            if (resolved != dir) yield return dir; // fallback: the PATH dir itself
        }
    }

    private static string? HighestVersionDir(string parent)
    {
        if (!Directory.Exists(parent)) return null;
        string[] dirs;
        try { dirs = Directory.GetDirectories(parent); }
        catch { return null; } // permission/path errors: degrade to "not found", never throw
        string? best = null;
        Version? bestV = null;
        foreach (string d in dirs)
        {
            string name = Path.GetFileName(d);
            string core = name.Split('-')[0]; // strip "-preview.x" etc.
            if (Version.TryParse(core, out Version? v) && (bestV == null || v > bestV))
            {
                bestV = v;
                best = d;
            }
        }
        return best;
    }
}
