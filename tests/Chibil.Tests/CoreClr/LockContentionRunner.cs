using System;
using System.Diagnostics;
using System.IO;

namespace Chibil.Tests.CoreClr;

internal static class LockContentionRunner
{
    static void StageWin(string dir, string name, byte[] pe)
    {
        File.WriteAllBytes(Path.Combine(dir, name), pe);
        File.WriteAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".runtimeconfig.json"),
            DotnetHostRunner.RuntimeConfigJson);
    }
    static int RunWin(string dir, string dll)
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"")
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("contender timed out"); }
        return p.ExitCode;
    }

    public static (int contended, int recovered) RunWindows(byte[] holder, byte[] contender)
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil_lock_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Process bg = null;
        try
        {
            StageWin(dir, "holder.dll", holder);
            StageWin(dir, "contender.dll", contender);
            bg = Process.Start(new ProcessStartInfo("dotnet", "\"holder.dll\"")
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string held = Path.Combine(dir, "held.marker");
            if (!WaitFile(() => File.Exists(held), 30000)) throw new Exception("holder never acquired the lock");
            int contended = RunWin(dir, "contender.dll");
            File.WriteAllText(Path.Combine(dir, "release.marker"), "");
            bg.WaitForExit(30000);
            int recovered = RunWin(dir, "contender.dll");
            return (contended, recovered);
        }
        finally { try { if (bg != null && !bg.HasExited) bg.Kill(true); } catch { } try { Directory.Delete(dir, true); } catch { } }
    }

    public static (int contended, int recovered) RunLinux(byte[] holder, byte[] contender)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string ltmp = "/tmp/chibil_lock_" + id;
        string win = Path.Combine(Path.GetTempPath(), "chibil_lockstage_" + id);
        Directory.CreateDirectory(win);
        StageWin(win, "holder.dll", holder);
        StageWin(win, "contender.dll", contender);
        string winWsl = "/mnt/" + char.ToLower(win[0]) + win[2..].Replace('\\', '/');
        Process bg = null;
        try
        {
            Wsl($"mkdir -p {ltmp} && cp '{winWsl}'/* {ltmp}/");
            bg = StartWslBg($"cd {ltmp} && dotnet holder.dll");
            if (!WaitFile(() => WslTest($"test -f {ltmp}/held.marker"), 30000)) throw new Exception("holder never acquired the lock (linux)");
            int contended = WslExit($"cd {ltmp} && dotnet contender.dll");
            Wsl($"touch {ltmp}/release.marker");
            bg.WaitForExit(30000);
            int recovered = WslExit($"cd {ltmp} && dotnet contender.dll");
            return (contended, recovered);
        }
        finally
        {
            try { if (bg != null && !bg.HasExited) bg.Kill(true); } catch { }
            try { Wsl($"rm -rf {ltmp}"); } catch { }
            try { Directory.Delete(win, true); } catch { }
        }
    }

    static bool WaitFile(Func<bool> ready, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 100)
        { if (ready()) return true; System.Threading.Thread.Sleep(100); }
        return ready();
    }

    static Process WslStart(string bashCmd, bool background)
    {
        var psi = new ProcessStartInfo("wsl", $"-u root -- bash -lc \"{bashCmd}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var p = Process.Start(psi);
        if (!background) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
        return p;
    }
    static void Wsl(string cmd) { var p = WslStart(cmd, false); p.WaitForExit(30000); }
    static bool WslTest(string cmd) { var p = WslStart(cmd, false); p.WaitForExit(15000); return p.ExitCode == 0; }
    static int WslExit(string cmd) { var p = WslStart(cmd, false); if (!p.WaitForExit(30000)) { p.Kill(true); } return p.ExitCode; }
    static Process StartWslBg(string cmd) => WslStart(cmd, true);
}
