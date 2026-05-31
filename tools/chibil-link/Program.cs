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

    public static LinkOptions Parse(string[] args)
    {
        var o = new LinkOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") { o.Output = args[++i]; continue; }
            if (a.StartsWith("-l")) { o.Libraries.Add(a[2..]); continue; }
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
        // Filled in by later tasks (B2..F).
        throw new LinkException("not implemented yet");
    }
}
