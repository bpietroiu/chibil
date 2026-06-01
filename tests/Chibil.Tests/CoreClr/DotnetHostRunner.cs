using System;
using System.Diagnostics;
using System.IO;

namespace Chibil.Tests.CoreClr;

/// <summary>
/// Shared helper to launch a produced pure-MSIL PE through the real
/// <c>dotnet</c> host (with a synthesized runtimeconfig.json) and capture its
/// exit code + stdout. Used by both the Phase 0 end-to-end test and the
/// P/Invoke synthesis test, which must exercise a real native call that an
/// in-process <c>Assembly.Load</c> + reflection invoke would also support but
/// which is clearest to prove through the actual host.
/// </summary>
internal static class DotnetHostRunner
{
    private const string RuntimeConfig =
        "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"rollForward\": \"Major\",\n" +
        "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" }\n  }\n}\n";

    public const string RuntimeConfigJson = RuntimeConfig; // expose for multi-assembly runs

    public static int RunDllInDir(string dllPath, out string stdout)
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        stdout = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
        if (err.Length > 0) stdout += "\n[stderr] " + err;
        return p.ExitCode;
    }

    public static bool DotnetAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dotnet", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static int RunPeViaDotnetHost(byte[] pe, out string stdout)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dll = Path.Combine(dir, "app.dll");
            File.WriteAllBytes(dll, pe);
            File.WriteAllText(Path.Combine(dir, "app.runtimeconfig.json"), RuntimeConfig);

            using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            stdout = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
            if (err.Length > 0) stdout += "\n[stderr] " + err;
            return p.ExitCode;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
