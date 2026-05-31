using System;
using System.IO;
using Chibil;

namespace Chibil.Tests.CoreClr;

// In-process compile helper mirroring Driver.Run/Cc1's tokenize→preprocess→parse→codegen pipeline.
static class TestCompiler
{
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
