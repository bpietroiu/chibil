using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Chibil.Build
{
    /// <summary>Compiles C translation units to .obj by invoking `dotnet chibil.dll`,
    /// in parallel and incrementally. Mirrors the bash build loops (build-managed-musl.sh,
    /// qjs-on-musl-api.sh). Args are passed via ArgumentList so paths/quotes never need
    /// shell escaping (e.g. -DCONFIG_VERSION="2025-09-13").</summary>
    public sealed class ChibilCompile : MSBuildTask
    {
        [Required] public string ChibilDll { get; set; }       // path to chibil.dll
        [Required] public ITaskItem[] Sources { get; set; }    // .c files
        [Required] public string OutputDir { get; set; }
        public string SourceRoot { get; set; }                 // for flat obj naming
        public string[] IncludeDirs { get; set; } = Array.Empty<string>();
        public string[] Defines { get; set; } = Array.Empty<string>();
        public string ForceInclude { get; set; }
        public string ExtraArgs { get; set; } = "";            // e.g. "--target=coreclr -nostdinc -mlp64"
        public string[] ExtraInputs { get; set; } = Array.Empty<string>(); // extra incremental deps (headers)
        public bool ContinueOnError { get; set; } = false;
        public int MaxParallel { get; set; } = 0;              // 0 => CPU count
        [Output] public ITaskItem[] Objects { get; set; }

        public override bool Execute()
        {
            Directory.CreateDirectory(OutputDir);
            string root = SourceRoot != null ? Path.GetFullPath(SourceRoot) : null;
            string[] deps = BuildDeps();
            var produced = new ConcurrentBag<string>();
            var failures = new ConcurrentBag<string>();
            int par = MaxParallel > 0 ? MaxParallel : Environment.ProcessorCount;

            Parallel.ForEach(Sources, new ParallelOptions { MaxDegreeOfParallelism = par }, item =>
            {
                string src = item.GetMetadata("FullPath");
                string obj = ObjPath(src, root);
                if (UpToDate(src, obj, deps)) { produced.Add(obj); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(obj));
                var (code, output) = Run(BuildArgs(src, obj));
                if (code == 0) { produced.Add(obj); return; }
                failures.Add(src);
                if (ContinueOnError) Log.LogWarning($"chibil skipped {src} (exit {code})");
                else Log.LogError($"chibil failed ({code}) for {src}:\n{output}");
            });

            Objects = produced.OrderBy(o => o).Select(o => (ITaskItem)new TaskItem(o)).ToArray();
            Log.LogMessage(MessageImportance.High,
                $"ChibilCompile: {produced.Count} ok, {failures.Count} failed" +
                (ContinueOnError && !failures.IsEmpty ? " (skipped)" : "") + $" -> {OutputDir}");
            return ContinueOnError || failures.IsEmpty;
        }

        string ObjPath(string src, string root)
        {
            string rel = root != null && src.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? src.Substring(root.Length).TrimStart('/', '\\')
                : Path.GetFileName(src);
            string flat = rel.Replace('\\', '_').Replace('/', '_');
            if (flat.EndsWith(".c", StringComparison.Ordinal))
                flat = flat.Substring(0, flat.Length - 2) + ".obj";
            return Path.Combine(OutputDir, flat);
        }

        string[] BuildDeps()
        {
            var d = new List<string> { Path.GetFullPath(ChibilDll) };
            if (!string.IsNullOrEmpty(ForceInclude)) d.Add(Path.GetFullPath(ForceInclude));
            foreach (var x in ExtraInputs) d.Add(Path.GetFullPath(x));
            return d.ToArray();
        }

        bool UpToDate(string src, string obj, string[] deps)
        {
            if (!File.Exists(obj)) return false;
            DateTime ot = File.GetLastWriteTimeUtc(obj);
            if (File.GetLastWriteTimeUtc(src) > ot) return false;
            foreach (var d in deps)
                if (File.Exists(d) && File.GetLastWriteTimeUtc(d) > ot) return false;
            return true;
        }

        List<string> BuildArgs(string src, string obj)
        {
            var a = new List<string> { ChibilDll, "-c" };
            foreach (var e in ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) a.Add(e);
            foreach (var i in IncludeDirs) a.Add("-I" + i);
            foreach (var df in Defines) a.Add("-D" + df);
            if (!string.IsNullOrEmpty(ForceInclude)) { a.Add("-include"); a.Add(ForceInclude); }
            a.Add(src);
            a.Add("-o"); a.Add(obj);
            return a;
        }

        static (int, string) Run(List<string> args)
        {
            var psi = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
    }
}
