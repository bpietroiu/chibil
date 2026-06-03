# chibil-link Native Apphost Launcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** For executable (non-`-shared`) chibil-link targets, emit a native launcher (`foo.exe` on Windows, `foo` on Linux) next to the managed `foo.dll` by copying and patching the .NET apphost template, so the program runs as `./foo` instead of `dotnet foo.dll`.

**Architecture:** Three small units in `tools/chibil-link/` — `AppHostPatcher` (pure byte patching of the placeholder), `AppHostLocator` (find the SDK/runtime apphost template), and `AppHostWriter` (orchestrate locate → copy → patch → finalize, best-effort, never fails the link). `Linker.Run` calls `AppHostWriter.TryEmit` after writing the dll + runtimeconfig, gated on `!opts.Shared`. Every failure mode degrades to a stderr warning + successful link.

**Tech Stack:** C# / .NET 10, xUnit. Targets win-x64 and linux-x64 (framework-dependent hosting). No new dependencies — BCL `File`/`Path`/`RuntimeInformation`/`UnixFileMode` only.

---

## File Structure

- Create: `tools/chibil-link/AppHostPatcher.cs` — replaces the 64-byte placeholder in a launcher image with the managed dll filename. Pure function on a `byte[]`.
- Create: `tools/chibil-link/AppHostLocator.cs` — resolves `<dotnetRoot>` and returns the apphost template path or `null`. No throwing on "not found".
- Create: `tools/chibil-link/AppHostWriter.cs` — `TryEmit(managedDllPath)`: compute launcher path, copy template, patch, finalize (chmod on Linux). Swallows + warns on any failure.
- Modify: `tools/chibil-link/Program.cs:225` — call `AppHostWriter.TryEmit(opts.Output)` when `!opts.Shared`.
- Create: `tests/Chibil.Tests/CoreClr/AppHostTests.cs` — unit tests (patcher, locator) + integration test (launcher self-runs). `MuslLinkTests.cs` is already 575 lines, so apphost tests get their own file, following the existing one-concern-per-file convention.

Each task is independent and produces a self-contained, committed change.

---

## Task 1: AppHostPatcher (placeholder byte replacement)

**Files:**
- Create: `tools/chibil-link/AppHostPatcher.cs`
- Test: `tests/Chibil.Tests/CoreClr/AppHostTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Chibil.Tests/CoreClr/AppHostTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Patcher"`
Expected: FAIL — `AppHostPatcher` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `tools/chibil-link/AppHostPatcher.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Patcher"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/AppHostPatcher.cs tests/Chibil.Tests/CoreClr/AppHostTests.cs
git commit -m "chibil-link: AppHostPatcher binds apphost placeholder to dll name"
```

---

## Task 2: AppHostLocator (find the template)

**Files:**
- Create: `tools/chibil-link/AppHostLocator.cs`
- Test: `tests/Chibil.Tests/CoreClr/AppHostTests.cs` (add to existing file)

- [ ] **Step 1: Write the failing test**

Append to `AppHostTests.cs` (inside the class):

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Locator"`
Expected: FAIL — `AppHostLocator` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `tools/chibil-link/AppHostLocator.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Locator"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/chibil-link/AppHostLocator.cs tests/Chibil.Tests/CoreClr/AppHostTests.cs
git commit -m "chibil-link: AppHostLocator finds SDK/runtime apphost template"
```

---

## Task 3: AppHostWriter orchestration + Linker.Run hook

**Files:**
- Create: `tools/chibil-link/AppHostWriter.cs`
- Modify: `tools/chibil-link/Program.cs:225`
- Test: `tests/Chibil.Tests/CoreClr/AppHostTests.cs` (add to existing file)

- [ ] **Step 1: Write the failing test**

Append to `AppHostTests.cs` (inside the class). These tests exercise `TryEmit` directly on a managed dll written to a temp dir — isolating apphost emission from the link pipeline:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Writer|FullyQualifiedName~AppHostTests.Shared"`
Expected: FAIL — `AppHostWriter` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `tools/chibil-link/AppHostWriter.cs`:

```csharp
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
```

- [ ] **Step 4: Hook into `Linker.Run`**

In `tools/chibil-link/Program.cs`, change the tail of `Run` (currently lines 223-225):

```csharp
        File.WriteAllBytes(opts.Output, pe);

        WriteRuntimeConfig(opts.Output);

        // For executable targets, also emit a native launcher (foo.exe / foo) that
        // boots CoreCLR and runs the dll — like `dotnet build`. Best-effort.
        if (!opts.Shared)
            AppHostWriter.TryEmit(opts.Output);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Writer|FullyQualifiedName~AppHostTests.Shared"`
Expected: PASS (3 tests). On a machine with the SDK, `Writer_emits_patched_launcher_next_to_dll` also asserts the patch and `Shared_target_emits_no_launcher_executable_does` asserts the executable launcher appears; with no SDK they still pass via the skip path.

- [ ] **Step 6: Commit**

```bash
git add tools/chibil-link/AppHostWriter.cs tools/chibil-link/Program.cs tests/Chibil.Tests/CoreClr/AppHostTests.cs
git commit -m "chibil-link: emit native apphost launcher for executable targets"
```

---

## Task 4: Integration — launcher self-runs without `dotnet`

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/AppHostTests.cs` (add to existing file)

- [ ] **Step 1: Write the failing test**

Append to `AppHostTests.cs` (inside the class). This links a real program, writes the dll + runtimeconfig, emits the launcher, and runs the launcher **directly** — no `dotnet` on the command line:

```csharp
[Fact]
public void Launcher_runs_the_program_without_dotnet_on_the_command_line()
{
    if (!DotnetHostRunner.DotnetAvailable()) return; // need an SDK for the template

    const string src = "int main(void){ return 42; }\n";
    byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "ah.obj");
    byte[] pe = LinkPipeline.LinkToBytes(new System.Collections.Generic.List<ObjectFile> { of },
        new System.Collections.Generic.List<string>());

    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "chibil_ahrun_" + Guid.NewGuid().ToString("N")[..8]);
    System.IO.Directory.CreateDirectory(dir);
    try
    {
        string dll = System.IO.Path.Combine(dir, "ah.dll");
        System.IO.File.WriteAllBytes(dll, pe);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "ah.runtimeconfig.json"),
            DotnetHostRunner.RuntimeConfigJson);

        AppHostWriter.TryEmit(dll);

        string exe = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "ah.exe" : "ah";
        string launcher = System.IO.Path.Combine(dir, exe);
        Assert.True(System.IO.File.Exists(launcher),
            "launcher was not produced (apphost template missing despite SDK present)");

        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("launcher timed out"); }
        Assert.Equal(42, p.ExitCode); // the apphost found hostfxr, read runtimeconfig, ran main()
    }
    finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
}
```

- [ ] **Step 2: Run test to verify it fails (then passes after build)**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests.Launcher"`
Expected: With Tasks 1-3 already implemented, this should PASS on a machine with the SDK. If `AppHostWriter`/types weren't yet built it would fail to compile — but since Task 3 is committed, the only failure cause would be a real bug (launcher not produced or wrong exit code). If it fails, fix `AppHostWriter`/`AppHostLocator` until it passes.

- [ ] **Step 3: Run the full apphost suite + adjacent link suite**

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~AppHostTests"`
Expected: PASS (all apphost tests).

Run: `dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~MuslLinkTests"`
Expected: PASS (no regression in the link path).

- [ ] **Step 4: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/AppHostTests.cs
git commit -m "chibil-link: integration test — native launcher self-runs program"
```

---

## Notes for the implementer

- The CoreCLR test helpers (`TestCompiler.CompileToObj`, `ObjectFile.Load`, `LinkPipeline.LinkToBytes`, `DotnetHostRunner`) live in `tests/Chibil.Tests/CoreClr/` and are used exactly as in `MuslLinkTests.cs` — copy those call shapes.
- `LinkPipeline.LinkToBytes` has overloads; the two-arg `(IEnumerable<ObjectFile>, List<string> libs)` form used in `MuslLinkTests` produces an executable (non-shared) PE — correct for Task 4.
- Run `dotnet test` inside a developer shell if MSVC-dependent tests are present, but the apphost tests themselves need only `dotnet` + an SDK; they do not invoke `cl.exe`/`link.exe`.
- The Linux self-run (Task 4) executes the ELF apphost natively on the host; it does not need WSL because the test process itself is the host. On Windows it runs the PE apphost. No WSL gate required for this test (unlike the musl libc tests).
- Do NOT edit anything under `targets/` (third-party, git-ignored).
```
