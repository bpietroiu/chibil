using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Chibil.Build
{
    /// <summary>Links .obj files into a managed .dll by invoking `dotnet chibil-link.dll`.
    /// Binds are joined into one `--bind=a=X,b=Y` (the CLI's form). Args via ArgumentList.</summary>
    public sealed class ChibilLink : Task
    {
        [Required] public string ChibilLinkDll { get; set; }
        [Required] public ITaskItem[] Objects { get; set; }
        [Required] public string Output { get; set; }
        public bool Shared { get; set; }
        public bool Debug { get; set; }
        public string[] Binds { get; set; } = Array.Empty<string>();      // "sym=Ns.Type.Method"
        public string[] References { get; set; } = Array.Empty<string>(); // -r asm.dll
        public string ExtraArgs { get; set; } = "";                       // e.g. "--print-imports" / "-l c"

        public override bool Execute()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Output)));
            var a = new List<string> { ChibilLinkDll };
            if (Debug) a.Add("-g");
            if (Shared) a.Add("-shared");
            a.Add("-o"); a.Add(Output);
            // Object paths can number in the thousands; Windows caps a process command
            // line at ~32K chars, so pass them through a chibil-link @response file
            // (whitespace/newline separated). Non-object args stay inline.
            string rsp = Path.GetFullPath(Output) + ".rsp";
            File.WriteAllLines(rsp, Objects.Select(o => o.GetMetadata("FullPath")));
            a.Add("@" + rsp);
            if (Binds.Length > 0) a.Add("--bind=" + string.Join(",", Binds));
            foreach (var r in References) { a.Add("-r"); a.Add(r); }
            foreach (var e in ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) a.Add(e);

            var psi = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in a) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (outp.Length > 0) Log.LogMessage(MessageImportance.High, outp.TrimEnd());
            if (err.Length > 0) Log.LogMessage(MessageImportance.High, err.TrimEnd());
            if (p.ExitCode != 0) { Log.LogError($"chibil-link failed ({p.ExitCode}) -> {Output}"); return false; }
            Log.LogMessage(MessageImportance.High, $"ChibilLink: -> {Output}");
            return true;
        }
    }
}
