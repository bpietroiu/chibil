using System.Diagnostics;
namespace Chibil.Tests.CoreClr;

static class WslRunner
{
    public static bool Available()
    {
        try {
            using var p = Process.Start(new ProcessStartInfo("wsl", "-u root -- bash -lc \"command -v dotnet\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        } catch { return false; }
    }

    // Writes pe to a temp dir, runs `dotnet app.dll` in WSL, returns (exit, stdout+stderr).
    public static (int exit, string output) Run(byte[] pe, string runtimeConfig)
    {
        string winDir = Path.Combine(Path.GetTempPath(), "chibil_wsl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(winDir);
        try {
            File.WriteAllBytes(Path.Combine(winDir, "app.dll"), pe);
            File.WriteAllText(Path.Combine(winDir, "app.runtimeconfig.json"), runtimeConfig);
            string wslPath = "/mnt/" + char.ToLower(winDir[0]) + winDir[2..].Replace('\\', '/');
            using var p = Process.Start(new ProcessStartInfo("wsl",
                $"-u root -- bash -lc \"cd '{wslPath}' && dotnet app.dll; echo EXIT=$?\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            int exit = 0;
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"EXIT=(-?\d+)");
            if (m.Success) exit = int.Parse(m.Groups[1].Value);
            return (exit, outp);
        } finally { try { Directory.Delete(winDir, true); } catch { } }
    }

    public const string NetCoreRuntimeConfig =
        "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"rollForward\": \"Major\",\n" +
        "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" }\n  }\n}\n";
}
