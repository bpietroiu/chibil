namespace ChibilLink;

public static class Program
{
    public static int Main(string[] args)
    {
        LinkOptions opts;
        try { opts = LinkOptions.Parse(args); }
        catch (LinkException ex) { Console.Error.WriteLine($"chibil-link: {ex.Message}"); return 1; }

        if (opts == null) return 1;
        if (opts.ShowHelp) { Console.Out.Write(LinkOptions.Usage); return 0; }
        if (opts.ShowVersion) { Console.Out.WriteLine($"chibil-link {LinkOptions.Version}"); return 0; }
        if (opts.Inputs.Count == 0)
        {
            Console.Error.WriteLine("chibil-link: no input files");
            Console.Error.Write(LinkOptions.Usage);
            return 1;
        }
        try
        {
            Linker.Run(opts);
            return 0;
        }
        catch (LinkException ex)
        {
            Console.Error.WriteLine($"chibil-link: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// Parsed chibil-link command line. The parser follows GNU ld / gcc-driver
/// conventions: short options accept both attached (<c>-ofoo</c>) and separated
/// (<c>-o foo</c>) values; long options accept both <c>--opt=val</c> and
/// <c>--opt val</c>; <c>@file</c> response files are expanded; <c>--</c> ends
/// option processing.
/// </summary>
public sealed class LinkOptions
{
    public List<string> Inputs = new();
    public List<string> Libraries = new();        // -l<name>  (e.g. "c" -> libc.so.6)
    public List<string> LibSearchPaths = new();   // -L<dir>   native-library probe dirs
    public string Output = "a.dll";               // -o
    public string ExportClass = null;             // --export-class=<Namespace.Name>
    public Dictionary<string, string> PinvokeMap = new();   // --pinvoke=name=lib,...
    public bool Debug = false;                    // -g       mark assembly debuggable
    public bool Shared = false;                   // -shared  emit a library (no entry point)
    public string Entry = "main";                 // -e/--entry  C entry symbol
    public bool ShowHelp = false;                 // --help
    public bool ShowVersion = false;              // --version
    public bool PrintImports = false;             // --print-imports  list native imports to stdout

    public const string Version = "0.1";

    public const string Usage =
        "usage: chibil-link [options] <obj>...\n" +
        "\n" +
        "  -o, --output <file>      output path (default a.dll); the assembly name\n" +
        "                           is the file's base name\n" +
        "  -shared                  emit a library with no entry point (no 'main'\n" +
        "                           required)\n" +
        "  -e, --entry <sym>        C entry symbol for an executable (default 'main')\n" +
        "  -l<name>                 link native library <name> (e.g. -lc -> libc.so.6)\n" +
        "  -L<dir>                  add <dir> to the native-library probe search path\n" +
        "  -g                       mark the assembly debuggable (JIT optimizer off)\n" +
        "  --export-class=<N.T>     emit a public static facade class N.T forwarding\n" +
        "                           to exported C functions (callable from C#)\n" +
        "  --pinvoke=<n=lib,...>    pin unresolved symbol <n> to native library <lib>\n" +
        "  --print-imports          also list the native imports (functions + data) to stdout\n" +
        "  @<file>                  read further options from <file>\n" +
        "  --help                   show this help and exit\n" +
        "  --version                show version and exit\n" +
        "\n" +
        "Short options take attached or separated values (-ofoo or -o foo); long\n" +
        "options take --opt=val or --opt val. '--' ends option processing.\n" +
        "Unsupported in the managed model (accepted and ignored): -static, -s,\n" +
        "-r/--relocatable, -pie/-no-pie, -rpath, -soname.\n";

    public static LinkOptions Parse(string[] argv)
    {
        var args = ExpandResponseFiles(argv, depth: 0);
        var o = new LinkOptions();
        bool endOpts = false;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];

            if (endOpts || a == "-" || a.Length == 0 || a[0] != '-')
            {
                o.Inputs.Add(a);
                continue;
            }
            if (a == "--") { endOpts = true; continue; }

            // ── long options: --name or --name=value ──────────────────────────
            if (a.StartsWith("--"))
            {
                int eq = a.IndexOf('=');
                string name = eq >= 0 ? a[..eq] : a;
                string inlineVal = eq >= 0 ? a[(eq + 1)..] : null;
                string Val() => inlineVal ?? Next(args, ref i, name);

                switch (name)
                {
                    case "--help": o.ShowHelp = true; break;
                    case "--version": o.ShowVersion = true; break;
                    case "--shared": o.Shared = true; break;
                    case "--output": o.Output = Val(); break;
                    case "--entry": o.Entry = Val(); break;
                    case "--export-class": o.ExportClass = Val(); break;
                    case "--pinvoke": ParsePinvoke(o, Val()); break;
                    case "--print-imports": o.PrintImports = true; break;
                    case "--relocatable": break;                  // accepted/ignored
                    default:
                        throw new LinkException($"unrecognized option '{a}'");
                }
                continue;
            }

            // ── single-dash long flags (GNU ld style) ─────────────────────────
            switch (a)
            {
                case "-shared": o.Shared = true; continue;
                case "-g": o.Debug = true; continue;
                case "-r": o.Shared = false; continue;            // accepted/ignored shape
                case "-static": case "-s": case "-pie": case "-no-pie":
                    continue;                                     // accepted/ignored
            }

            // ── short options with values (attached or separated) ─────────────
            if (TryValue(a, "-o", args, ref i, out var ov)) { o.Output = ov; continue; }
            if (TryValue(a, "-e", args, ref i, out var ev)) { o.Entry = ev; continue; }
            if (TryValue(a, "-l", args, ref i, out var lv)) { o.Libraries.Add(lv); continue; }
            if (TryValue(a, "-L", args, ref i, out var Lv)) { o.LibSearchPaths.Add(Lv); continue; }

            // GNU long opts also accepted with a single dash for a few names.
            if (a == "-help") { o.ShowHelp = true; continue; }
            if (a == "-version") { o.ShowVersion = true; continue; }

            throw new LinkException($"unrecognized option '{a}'");
        }
        return o;
    }

    // --pinvoke=name=lib[,name=lib...]
    private static void ParsePinvoke(LinkOptions o, string spec)
    {
        if (string.IsNullOrEmpty(spec))
            throw new LinkException("--pinvoke requires name=lib entries");
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1)
                throw new LinkException($"bad --pinvoke entry: {pair}");
            o.PinvokeMap[pair[..eq]] = pair[(eq + 1)..];
        }
    }

    // A short option's value is either attached (-ofoo) or the next argument
    // (-o foo). Returns true and sets value when <a> matches <opt>.
    private static bool TryValue(string a, string opt, List<string> args, ref int i, out string value)
    {
        if (a == opt) { value = Next(args, ref i, opt); return true; }
        if (a.StartsWith(opt)) { value = a[opt.Length..]; return true; }
        value = null;
        return false;
    }

    private static string Next(List<string> args, ref int i, string opt)
    {
        if (i + 1 >= args.Count)
            throw new LinkException($"option '{opt}' requires an argument");
        return args[++i];
    }

    // Expand @file response files (whitespace/newline separated), recursively,
    // with a small depth cap to defeat cycles.
    private static List<string> ExpandResponseFiles(IEnumerable<string> argv, int depth)
    {
        var outp = new List<string>();
        foreach (var a in argv)
        {
            if (a.Length > 1 && a[0] == '@')
            {
                if (depth > 16)
                    throw new LinkException("response files nested too deeply");
                string path = a[1..];
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception ex) { throw new LinkException($"cannot read response file '{path}': {ex.Message}"); }
                var toks = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                outp.AddRange(ExpandResponseFiles(toks, depth + 1));
            }
            else
            {
                outp.Add(a);
            }
        }
        return outp;
    }
}

public static class Linker
{
    public static void Run(LinkOptions opts)
    {
        var objs = new List<ObjectFile>(opts.Inputs.Count);
        foreach (string path in opts.Inputs)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { throw new LinkException($"cannot read '{path}': {ex.Message}"); }
            objs.Add(ObjectFile.Load(bytes, path));
        }

        // Assembly identity follows the output base name (like `gcc -o foo`), not a
        // hardcoded "a". The module name is the output file name.
        string asmName = Path.GetFileNameWithoutExtension(opts.Output);
        if (string.IsNullOrEmpty(asmName)) asmName = "a";

        var imports = opts.PrintImports ? new List<ImportRecord>() : null;
        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries, opts.ExportClass,
            opts.PinvokeMap, opts.Debug, opts.Shared, asmName, opts.Entry, opts.LibSearchPaths, imports);
        File.WriteAllBytes(opts.Output, pe);

        WriteRuntimeConfig(opts.Output);

        if (imports != null)
            PrintImports(imports);

        // For executable targets, also emit a native launcher (foo.exe / foo) that
        // boots CoreCLR and runs the dll — like `dotnet build`. Best-effort.
        if (!opts.Shared)
            AppHostWriter.TryEmit(opts.Output);
    }

    // Print the native imports (function P/Invoke stubs + data imports) the output binds,
    // one per line `<lib>  <kind>  <name>`, after a count header. Already deduped + sorted.
    private static void PrintImports(List<ImportRecord> imports)
    {
        int func = 0;
        foreach (var i in imports) if (i.Kind == "func") func++;
        Console.Out.WriteLine($"imports: {imports.Count} ({func} func, {imports.Count - func} data)");
        foreach (var i in imports)
            Console.Out.WriteLine($"{i.Lib}  {i.Kind}  {i.Name}");
    }

    private static void WriteRuntimeConfig(string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        string cfgPath = Path.Combine(dir ?? ".", baseName + ".runtimeconfig.json");
        File.WriteAllText(cfgPath, RuntimeConfigText.Json);
    }
}
