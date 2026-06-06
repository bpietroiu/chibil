using System;
using System.IO;
using Chibil;

namespace Chibil.Tests.CoreClr;

// In-process compile helper mirroring Driver.Run/Cc1's tokenize→preprocess→parse→codegen pipeline.
static class TestCompiler
{
    // Compile an on-disk C file with -D defines and -I include dirs (for large
    // real-world sources like the SQLite amalgamation). Mirrors Driver.Cc1.
    public static byte[] CompileFileToObj(string path, TargetProfile target,
        string[] defines = null, string[] includeDirs = null)
    {
        var opts = new CompilerOptions { Target = target, BaseFile = path };
        if (includeDirs != null)
            foreach (var d in includeDirs) opts.IncludePaths.Add(d);

        var types = new TypeSystem(opts.DataModel);
        var tokenizer = new Tokenizer(opts, types);
        var preprocessor = new Preprocessor(tokenizer, opts, types);
        preprocessor.InitMacros();
        var parser = new Parser(tokenizer, opts, types);
        preprocessor.SetParser(parser);

        if (defines != null)
            foreach (var def in defines)
            {
                int eq = def.IndexOf('=');
                if (eq >= 0) preprocessor.DefineMacro(def[..eq], def[(eq + 1)..]);
                else preprocessor.DefineMacro(def, "1");
            }

        Token tok = tokenizer.TokenizeFile(path)
            ?? throw new InvalidOperationException($"Failed to tokenize {path}");
        tok = preprocessor.Preprocess(tok);
        Obj prog = parser.Parse(tok);
        var codegen = new MsilObjectEmitter(opts, tokenizer, types);
        return codegen.Generate(prog, Path.GetFileNameWithoutExtension(path) + ".obj", Path.GetFullPath(path));
    }

    // Runs the in-process compile pipeline; caller has already set up opts (Target,
    // BaseFile, IncludePaths, ExportApiHeaders) and written the source/header files into
    // the temp dir. If generate is true returns the COFF object bytes; otherwise returns
    // null after Parse (so callers can inspect the post-parse CompilerOptions).
    private static byte[] RunPipeline(CompilerOptions opts, bool generate)
    {
        var types = new TypeSystem(opts.DataModel);
        var tokenizer = new Tokenizer(opts, types);
        var preprocessor = new Preprocessor(tokenizer, opts, types);
        preprocessor.InitMacros();
        var parser = new Parser(tokenizer, opts, types);
        preprocessor.SetParser(parser);

        Token tok = tokenizer.TokenizeFile(opts.BaseFile);
        if (tok == null)
            throw new InvalidOperationException($"Failed to tokenize {opts.BaseFile}");

        tok = preprocessor.Preprocess(tok);
        Obj prog = parser.Parse(tok);

        if (!generate) return null;
        var codegen = new MsilObjectEmitter(opts, tokenizer, types);
        return codegen.Generate(prog, "t.obj", Path.GetFullPath(opts.BaseFile));
    }

    public static byte[] CompileToObj(string source, TargetProfile target, string name = "t.c")
    {
        // The tokenizer reads from a file, so materialize the source to a temp path.
        string dir = Path.Combine(Path.GetTempPath(), "chibil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string srcPath = Path.Combine(dir, name);
        File.WriteAllText(srcPath, source);

        try
        {
            var opts = new CompilerOptions { Target = target, BaseFile = srcPath };
            return RunPipeline(opts, generate: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    public static byte[] CompileToObjWithApi(string source, string header, string headerName,
        TargetProfile target, string name = "t.c")
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string srcPath = Path.Combine(dir, name);
        string hdrPath = Path.Combine(dir, headerName);
        File.WriteAllText(srcPath, source);
        File.WriteAllText(hdrPath, header);
        try
        {
            var opts = new CompilerOptions { Target = target, BaseFile = srcPath };
            opts.IncludePaths.Add(dir);          // so #include "mylib.h" resolves
            opts.ExportApiHeaders.Add(hdrPath);  // the public header
            return RunPipeline(opts, generate: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    public static CompilerOptions CompileAndReturnOptions(string source, string header,
        string headerName, TargetProfile target, string name = "t.c")
    {
        string dir = Path.Combine(Path.GetTempPath(), "chibil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string srcPath = Path.Combine(dir, name);
            File.WriteAllText(srcPath, source);
            File.WriteAllText(Path.Combine(dir, headerName), header);
            var opts = new CompilerOptions { Target = target, BaseFile = srcPath };
            opts.IncludePaths.Add(dir);
            opts.ExportApiHeaders.Add(Path.Combine(dir, headerName));
            RunPipeline(opts, generate: false);
            return opts;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
