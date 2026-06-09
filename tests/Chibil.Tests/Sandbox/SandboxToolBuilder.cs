using System;
using System.Collections.Generic;
using System.IO;
using Chibil;                 // TargetProfile, CompilerOptions
using Chibil.Tests.CoreClr;   // TestCompiler
using ChibilLink;             // LinkPipeline, ObjectFile

namespace Chibil.Tests.Sandbox;

/// <summary>Compiles a freestanding tool .c under targets/sandbox/tools into a runnable
/// CoreCLR .dll, with __chibil_syscall bound to Chibil.Sandbox.SandboxPal.Syscall, using
/// the in-process compiler + linker (same path the CoreClr unit tests use).</summary>
public static class SandboxToolBuilder
{
    /// <summary>Walk up from the test binary to the repo root (the dir containing
    /// targets/sandbox/tools).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "targets", "sandbox", "tools")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (targets/sandbox/tools) not found");
    }

    public static string Build(string toolName)
    {
        string src = Path.Combine(RepoRoot(), "targets", "sandbox", "tools", toolName + ".c");
        string sandboxDll = typeof(global::Chibil.Sandbox.SandboxPal).Assembly.Location;

        byte[] obj = TestCompiler.CompileFileToObj(src, TargetProfile.CoreClr,
            defines: null, includeDirs: new[] { Path.GetDirectoryName(src) });

        var bindMap = new Dictionary<string, string>
        {
            ["__chibil_syscall"] = "Chibil.Sandbox.SandboxPal.Syscall",
            ["__chibil_get_tp"]  = "Chibil.Sandbox.SandboxPal.GetTp",
        };

        byte[] dllBytes = LinkPipeline.LinkToBytes(
            new[] { ObjectFile.Load(obj, toolName + ".obj") },
            new List<string>(),
            assemblyName: toolName,
            entrySymbol: "main",
            bindMap: bindMap,
            references: new[] { sandboxDll });

        string outDir = Path.Combine(Path.GetTempPath(), "chibil-sandbox-tools");
        Directory.CreateDirectory(outDir);
        string dll = Path.Combine(outDir, toolName + ".dll");
        File.WriteAllBytes(dll, dllBytes);
        return dll;
    }
}
