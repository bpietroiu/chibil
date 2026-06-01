using System;
using System.Diagnostics;
using System.IO;

namespace Chibil.Tests.CoreClr;

/// <summary>Runs a produced PE under the dotnet host with the child WORKING
/// DIRECTORY set to the temp dir, so a C program that opens a relative file
/// (sp3.db) creates it there. Optionally reports a probed file's size before
/// cleanup (to assert on-disk persistence).</summary>
internal static class DiskRunner
{
    public static int RunWindows(byte[] pe, out string stdout, out long probeSize, string probeFile = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_disk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dll = Path.Combine(dir, "app.dll");
            File.WriteAllBytes(dll, pe);
            File.WriteAllText(Path.Combine(dir, "app.runtimeconfig.json"), DotnetHostRunner.RuntimeConfigJson);
            using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir });
            stdout = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
            if (err.Length > 0) stdout += "\n[stderr] " + err;
            probeSize = probeFile != null && File.Exists(Path.Combine(dir, probeFile))
                ? new FileInfo(Path.Combine(dir, probeFile)).Length : -1;
            return p.ExitCode;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
