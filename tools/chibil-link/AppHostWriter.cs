#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ChibilLink;

/// <summary>
/// Emits a native launcher (foo.exe / foo) next to the managed assembly by copying
/// and patching the .NET apphost template — what `dotnet build` does. Best-effort:
/// any failure (no template, name clash, IO error) degrades to a stderr warning so
/// the link still succeeds and the dll remains runnable via `dotnet foo.dll`.
/// </summary>
public static class AppHostWriter
{
    public static void TryEmit(string managedDllPath)
    {
        try
        {
            string full = Path.GetFullPath(managedDllPath);
            string dir = Path.GetDirectoryName(full) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(full);
            string exeExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            string launcher = Path.Combine(dir, baseName + exeExt);

            if (string.Equals(Path.GetFullPath(launcher), full, StringComparison.OrdinalIgnoreCase))
            {
                Warn($"launcher path '{launcher}' would collide with the assembly; skipping apphost.");
                return;
            }

            string? template = AppHostLocator.Find();
            if (template == null)
            {
                Warn("no .NET apphost template found (set DOTNET_ROOT or put dotnet on PATH); " +
                     $"skipping native launcher. Run with: dotnet {Path.GetFileName(full)}");
                return;
            }

            byte[] image = File.ReadAllBytes(template);
            if (!AppHostPatcher.Patch(image, Path.GetFileName(full)))
            {
                Warn($"apphost template '{template}' has no recognizable placeholder; skipping launcher.");
                return;
            }

            File.WriteAllBytes(launcher, image);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                MakeExecutable(launcher);
        }
        catch (Exception ex)
        {
            Warn($"could not emit native launcher: {ex.Message}");
        }
    }

    private static void MakeExecutable(string path)
    {
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute); // 0755
    }

    private static void Warn(string msg) => Console.Error.WriteLine("chibil-link: warning: " + msg);
}
