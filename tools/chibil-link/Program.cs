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

    public static LinkOptions Parse(string[] args)
    {
        var o = new LinkOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") { o.Output = args[++i]; continue; }
            if (a.StartsWith("-l")) { o.Libraries.Add(a[2..]); continue; }
            if (a.StartsWith("--export-class=")) { o.ExportClass = a["--export-class=".Length..]; continue; }
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

        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries, opts.ExportClass);
        File.WriteAllBytes(opts.Output, pe);

        WriteRuntimeConfig(opts.Output);
    }

    private static void WriteRuntimeConfig(string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        string cfgPath = Path.Combine(dir ?? ".", baseName + ".runtimeconfig.json");
        const string cfg =
            "{\n" +
            "  \"runtimeOptions\": {\n" +
            "    \"tfm\": \"net10.0\",\n" +
            "    \"rollForward\": \"Major\",\n" +
            "    \"framework\": {\n" +
            "      \"name\": \"Microsoft.NETCore.App\",\n" +
            "      \"version\": \"10.0.0\"\n" +
            "    }\n" +
            "  }\n" +
            "}\n";
        File.WriteAllText(cfgPath, cfg);
    }
}
