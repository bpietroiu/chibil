#nullable enable
using System;
using System.Text;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class AppHostTests
{
    // The 64-byte ASCII placeholder embedded in every apphost template
    // (Microsoft.NET.HostModel AppBinaryPathPlaceholder).
    const string Placeholder =
        "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    static byte[] FakeApphost(out int slotOffset)
    {
        // [16 bytes junk][64-byte placeholder][16 bytes junk]
        byte[] head = new byte[16];
        byte[] tail = new byte[16];
        for (int i = 0; i < 16; i++) { head[i] = 0xAA; tail[i] = 0xBB; }
        byte[] mid = Encoding.ASCII.GetBytes(Placeholder);
        slotOffset = head.Length;
        byte[] img = new byte[head.Length + mid.Length + tail.Length];
        Buffer.BlockCopy(head, 0, img, 0, head.Length);
        Buffer.BlockCopy(mid, 0, img, head.Length, mid.Length);
        Buffer.BlockCopy(tail, 0, img, head.Length + mid.Length, tail.Length);
        return img;
    }

    [Fact]
    public void Patcher_writes_dll_name_nul_terminated_and_zero_pads_the_slot()
    {
        byte[] img = FakeApphost(out int slot);
        bool ok = AppHostPatcher.Patch(img, "foo.dll");
        Assert.True(ok);

        byte[] name = Encoding.UTF8.GetBytes("foo.dll");
        for (int i = 0; i < name.Length; i++) Assert.Equal(name[i], img[slot + i]);
        Assert.Equal(0, img[slot + name.Length]);                 // NUL terminator
        for (int i = name.Length + 1; i < 64; i++)                // zero-padded to 64
            Assert.Equal(0, img[slot + i]);
        // Bytes outside the 64-byte slot are untouched.
        Assert.Equal(0xAA, img[slot - 1]);
        Assert.Equal(0xBB, img[slot + 64]);
        // The placeholder text is gone.
        Assert.DoesNotContain(Placeholder, Encoding.ASCII.GetString(img));
    }

    [Fact]
    public void Patcher_returns_false_when_no_placeholder_present()
    {
        byte[] img = new byte[128]; // all zeros, no sentinel
        Assert.False(AppHostPatcher.Patch(img, "foo.dll"));
    }

    [Fact]
    public void Patcher_returns_false_when_name_exceeds_slot()
    {
        byte[] img = FakeApphost(out _);
        string tooLong = new string('a', 60) + ".dll"; // 64 bytes, no room for NUL
        Assert.False(AppHostPatcher.Patch(img, tooLong));
    }

    [Fact]
    public void Locator_returns_null_for_empty_dotnet_root()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_emptyroot_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(root);
        try
        {
            Assert.Null(AppHostLocator.FindInRoot(root)); // no sdk/, no packs/ → not found, no throw
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Locator_finds_highest_sdk_apphost_template()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_fakeroot_" + Guid.NewGuid().ToString("N")[..8]);
        string exe = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "apphost.exe" : "apphost";
        string lowTpl  = System.IO.Path.Combine(root, "sdk", "9.0.100", "AppHostTemplate");
        string highTpl = System.IO.Path.Combine(root, "sdk", "10.0.100", "AppHostTemplate");
        System.IO.Directory.CreateDirectory(lowTpl);
        System.IO.Directory.CreateDirectory(highTpl);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(lowTpl, exe), new byte[] { 1 });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(highTpl, exe), new byte[] { 2 });
        try
        {
            string? found = AppHostLocator.FindInRoot(root);
            Assert.NotNull(found);
            Assert.Equal(System.IO.Path.Combine(highTpl, exe), found); // picks 10.0.100 over 9.0.100
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void DirOfResolvedDotnet_returns_containing_dir_for_a_plain_file()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_dotnetdir_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string dotnet = System.IO.Path.Combine(dir, "dotnet");
            System.IO.File.WriteAllBytes(dotnet, new byte[] { 1 });
            // Not a symlink: the dotnet root is simply the file's own directory.
            Assert.Equal(System.IO.Path.GetFullPath(dir), AppHostLocator.DirOfResolvedDotnet(dotnet));
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DirOfResolvedDotnet_follows_symlink_to_real_root()
    {
        // The WSL/Debian case: /usr/bin/dotnet is a symlink to /usr/lib/dotnet/dotnet;
        // the real root (with sdk/, packs/) is the symlink TARGET's directory.
        string baseDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_dotnetlink_" + Guid.NewGuid().ToString("N")[..8]);
        string realRoot = System.IO.Path.Combine(baseDir, "real");
        string binDir = System.IO.Path.Combine(baseDir, "bin");
        System.IO.Directory.CreateDirectory(realRoot);
        System.IO.Directory.CreateDirectory(binDir);
        string realDotnet = System.IO.Path.Combine(realRoot, "dotnet");
        System.IO.File.WriteAllBytes(realDotnet, new byte[] { 1 });
        string linkDotnet = System.IO.Path.Combine(binDir, "dotnet");
        try { System.IO.File.CreateSymbolicLink(linkDotnet, realDotnet); }
        catch { return; } // no symlink privilege (e.g. Windows without Developer Mode) — skip
        try
        {
            Assert.Equal(System.IO.Path.GetFullPath(realRoot),
                AppHostLocator.DirOfResolvedDotnet(linkDotnet));
        }
        finally { try { System.IO.Directory.Delete(baseDir, true); } catch { } }
    }

    [Fact]
    public void Writer_emits_patched_launcher_next_to_dll()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_ahw_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string dll = System.IO.Path.Combine(dir, "myapp.dll");
            System.IO.File.WriteAllBytes(dll, new byte[] { 0 }); // contents irrelevant for emission
            AppHostWriter.TryEmit(dll);

            string exe = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "myapp.exe" : "myapp";
            string launcher = System.IO.Path.Combine(dir, exe);

            // If no SDK template is available the writer warns-and-skips — only assert
            // patching when a launcher was actually produced.
            if (System.IO.File.Exists(launcher))
            {
                string text = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(launcher));
                Assert.DoesNotContain(Placeholder, text); // sentinel replaced
                Assert.Contains("myapp.dll", text);       // bound to the dll filename
            }
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Writer_does_not_clobber_dll_when_launcher_path_would_collide()
    {
        // Linux `-o foo` (no extension): launcher path == dll path. Must skip, never
        // overwrite the assembly. (On Windows the .exe ext means no collision; the dll
        // stays intact either way — this asserts the dll is preserved.)
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_ahw_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string dll = System.IO.Path.Combine(dir, "foo"); // no .dll extension
            System.IO.File.WriteAllBytes(dll, new byte[] { 7, 7, 7 });
            AppHostWriter.TryEmit(dll);
            Assert.Equal(new byte[] { 7, 7, 7 }, System.IO.File.ReadAllBytes(dll)); // untouched
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async System.Threading.Tasks.Task Launcher_runs_the_program_without_dotnet_on_the_command_line()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return; // need an SDK for the template

        const string src = "int main(void){ return 42; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "ah.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_ahrun_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string dll = System.IO.Path.Combine(dir, "ah.dll");
            System.IO.File.WriteAllBytes(dll, pe);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "ah.runtimeconfig.json"),
                RuntimeConfigText.Json);

            AppHostWriter.TryEmit(dll);

            string exe = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "ah.exe" : "ah";
            string launcher = System.IO.Path.Combine(dir, exe);
            Assert.True(System.IO.File.Exists(launcher),
                "launcher was not produced (apphost template missing despite SDK present)");

            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })
                ?? throw new Exception("could not start launcher process");
            System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                p.Kill(true);
                try { p.WaitForExit(5000); } catch { }
                throw new Exception("launcher timed out");
            }
            await outTask;
            await errTask;
            Assert.Equal(42, p.ExitCode); // the apphost found hostfxr, read runtimeconfig, ran main()
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Shared_target_emits_no_launcher_executable_does()
    {
        // Drives the real Linker.Run gate: -shared must not produce a launcher; a
        // non-shared link of the same object must (when an SDK template is available).
        const string src = "int main(void){ return 0; }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "chibil_gate_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string objPath = System.IO.Path.Combine(dir, "g.obj");
            System.IO.File.WriteAllBytes(objPath, obj);
            string exe = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? ".exe" : "";

            // -shared: library, no launcher.
            string libOut = System.IO.Path.Combine(dir, "lib.dll");
            Linker.Run(new LinkOptions {
                Inputs = new System.Collections.Generic.List<string> { objPath },
                Output = libOut, Shared = true });
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(dir, "lib" + exe)),
                "shared target must not emit a launcher");

            // executable: dll + runtimeconfig always; launcher iff a template is available.
            string appOut = System.IO.Path.Combine(dir, "app.dll");
            Linker.Run(new LinkOptions {
                Inputs = new System.Collections.Generic.List<string> { objPath },
                Output = appOut, Shared = false });
            Assert.True(System.IO.File.Exists(appOut));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "app.runtimeconfig.json")));
            if (AppHostLocator.Find() != null)
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "app" + exe)),
                    "executable target should emit a launcher when a template is available");
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
