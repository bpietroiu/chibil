#nullable enable
using System;
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
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "linux";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }

    /// <summary>Resolve dotnet root (DOTNET_ROOT, else dir of `dotnet` on PATH) then search.</summary>
    public static string? Find()
    {
        string? root = ResolveDotnetRoot();
        return root == null ? null : FindInRoot(root);
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

    private static string? ResolveDotnetRoot()
    {
        string? env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        string dotnet = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
        {
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try { if (File.Exists(Path.Combine(dir, dotnet))) return dir; }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    private static string? HighestVersionDir(string parent)
    {
        if (!Directory.Exists(parent)) return null;
        string? best = null;
        Version? bestV = null;
        foreach (string d in Directory.GetDirectories(parent))
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
