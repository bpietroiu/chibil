namespace ChibilLink;

public static class Program
{
    public static int Main(string[] args)
    {
        var opts = LinkOptions.Parse(args);
        if (opts == null || opts.Inputs.Count == 0)
        {
            Console.Error.WriteLine("usage: chibil-link [-o out.dll] [-l<lib>...] <obj>...");
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

public sealed class LinkOptions
{
    public List<string> Inputs = new();
    public List<string> Libraries = new();   // from -l (e.g. "c" -> libc.so.6)
    public string Output = "a.dll";
    public string ExportClass = null;   // --export-class=<Namespace.Name>; null = no export type
    public Dictionary<string, string> PinvokeMap = new();   // symbol -> library token
    public bool Debug = false;          // -g: mark the assembly debuggable (JIT optimizer disabled)

    public static LinkOptions Parse(string[] args)
    {
        var o = new LinkOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") { o.Output = args[++i]; continue; }
            if (a == "-g") { o.Debug = true; continue; }
            if (a.StartsWith("-l")) { o.Libraries.Add(a[2..]); continue; }
            if (a.StartsWith("--export-class=")) { o.ExportClass = a["--export-class=".Length..]; continue; }
            if (a.StartsWith("--pinvoke="))
            {
                string spec = a["--pinvoke=".Length..];
                if (spec.Length == 0) { System.Console.Error.WriteLine("--pinvoke requires name=lib entries"); return null; }
                foreach (var pair in spec.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0 || eq == pair.Length - 1)
                    {
                        System.Console.Error.WriteLine($"bad --pinvoke entry: {pair}");
                        return null;
                    }
                    o.PinvokeMap[pair[..eq]] = pair[(eq + 1)..];
                }
                continue;
            }
            if (a.StartsWith("-")) { Console.Error.WriteLine($"unknown flag: {a}"); return null; }
            o.Inputs.Add(a);
        }
        return o;
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

        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries, opts.ExportClass, opts.PinvokeMap, opts.Debug);
        File.WriteAllBytes(opts.Output, pe);

        WriteRuntimeConfig(opts.Output);
    }

    private static void WriteRuntimeConfig(string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        string cfgPath = Path.Combine(dir ?? ".", baseName + ".runtimeconfig.json");
        File.WriteAllText(cfgPath, RuntimeConfigText.Json);
    }
}
