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
        var codegen = new CodeGen(opts, tokenizer, types);
        return codegen.Generate(prog, Path.GetFileNameWithoutExtension(path) + ".obj", Path.GetFullPath(path));
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
            var types = new TypeSystem(opts.DataModel);
            var tokenizer = new Tokenizer(opts, types);
            var preprocessor = new Preprocessor(tokenizer, opts, types);
            preprocessor.InitMacros();
            var parser = new Parser(tokenizer, opts, types);
            preprocessor.SetParser(parser);

            Token tok = tokenizer.TokenizeFile(srcPath);
            if (tok == null)
                throw new InvalidOperationException($"Failed to tokenize {srcPath}");

            tok = preprocessor.Preprocess(tok);
            Obj prog = parser.Parse(tok);

            var codegen = new CodeGen(opts, tokenizer, types);
            return codegen.Generate(prog, "t.obj", Path.GetFullPath(srcPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
