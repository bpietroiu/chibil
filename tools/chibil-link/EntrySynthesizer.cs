using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

/// <summary>
/// Synthesizes the managed entry point that the CLR will invoke. It bridges the
/// CLR's <c>static int32 Main(string[])</c> entry-point contract to the C
/// <c>int main(...)</c> function.
///
/// Phase 0 supports <c>int main(void)</c>: the entry's IL is simply
/// <c>call int32 &lt;main&gt;; ret</c>. Marshalling argv for
/// <c>int main(int, char**)</c> is a later task.
/// </summary>
public static class EntrySynthesizer
{
    public sealed class Result
    {
        public byte[] Il;                 // entry IL with main's FINAL token already baked
        public int MaxStack;
        public BlobHandle SignatureBlob;  // static int32 Main(string[])
        public StringHandle Name;
    }

    /// <summary>
    /// Locate the C <c>main</c> across all objects and build the entry method.
    /// The returned IL has main's FINAL token written in; the caller reserves the
    /// entry's own MethodDef row separately.
    /// </summary>
    public static Result Synthesize(MetadataMerger merger)
    {
        // Find main.
        ObjectFile mainObj = null;
        ObjMethod mainMethod = null;
        foreach (var slot in merger.Plan)
        {
            if (slot.Method == null) continue; // entry placeholder
            if (slot.Method.Name == "main")
            {
                mainObj = slot.Obj;
                mainMethod = slot.Method;
                break;
            }
        }
        if (mainMethod == null)
            throw new LinkException("no 'main' function found in input objects.");

        int mainFinalToken = merger.MapToken(mainObj, mainMethod.OriginalToken);

        // Determine main's parameter count to decide the call shape.
        var md = mainObj.Md;
        var def = md.GetMethodDefinition(mainMethod.Handle);
        int paramCount = def.GetParameters().Count;

        var b = merger.Builder;

        // Signature: static int32 Main(string[] args)
        var sigBuilder = new BlobBuilder();
        new BlobEncoder(sigBuilder)
            .MethodSignature(SignatureCallingConvention.Default, 0, isInstanceMethod: false)
            .Parameters(1, ret => ret.Type().Int32(), par =>
            {
                par.AddParameter().Type().SZArray().String();
            });

        // IL.
        var il = new BlobBuilder();
        int maxStack;
        if (paramCount == 0)
        {
            // call int32 main(); ret
            il.WriteByte(0x28);                 // call
            il.WriteInt32(mainFinalToken);
            il.WriteByte(0x2A);                 // ret
            maxStack = 1;
        }
        else
        {
            // Phase 0 fallback for int main(int, char**): pass 0 / null.
            // ldc.i4.0 ; ldnull ; call int32 main(int,char**) ; ret
            il.WriteByte(0x16);                 // ldc.i4.0  (argc = 0)
            il.WriteByte(0x14);                 // ldnull     (argv = null)
            il.WriteByte(0x28);                 // call
            il.WriteInt32(mainFinalToken);
            il.WriteByte(0x2A);                 // ret
            maxStack = 2;
        }

        return new Result
        {
            Il = il.ToArray(),
            MaxStack = maxStack,
            SignatureBlob = b.GetOrAddBlob(sigBuilder),
            Name = b.GetOrAddString("Main"),
        };
    }
}
