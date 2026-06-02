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
    public static Result Synthesize(MetadataMerger merger, string entrySymbol = "main")
    {
        // Find the entry function (default `main`).
        ObjectFile mainObj = null;
        ObjMethod mainMethod = null;
        foreach (var slot in merger.Plan)
        {
            if (slot.Method == null) continue; // entry placeholder
            if (slot.Method.Name == entrySymbol)
            {
                mainObj = slot.Obj;
                mainMethod = slot.Method;
                break;
            }
        }
        if (mainMethod == null)
            throw new LinkException($"no '{entrySymbol}' function found in input objects.");

        int mainFinalToken = merger.MapToken(mainObj, mainMethod.OriginalToken);

        // Determine main's parameter count from its SIGNATURE (authoritative;
        // the Param table may also carry a sequence-0 return row) to decide the
        // call shape.
        var md = mainObj.Md;
        var def = md.GetMethodDefinition(mainMethod.Handle);
        var sigReader = md.GetBlobReader(def.Signature);
        sigReader.ReadSignatureHeader();                 // calling convention
        int paramCount = sigReader.ReadCompressedInteger();

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
        else if (paramCount >= 1 && paramCount <= 3)
        {
            // Bridge `int main(int argc, char** argv[, char** envp])`. Push argc
            // and a freshly-marshalled char** argv via the synthesized helpers,
            // then NULL for envp if main takes three params, and call main.
            //
            //   call int32 __chibil_argc()            ; argc
            //   [call void* __chibil_make_argv()]     ; argv  (params >= 2)
            //   [ldc.i4.0; conv.i]                    ; envp = NULL (params == 3)
            //   call int32 main(...); ret
            il.WriteByte(0x28); il.WriteInt32(merger.ReserveArgcHelper());        // call __chibil_argc
            if (paramCount >= 2)
            {
                il.WriteByte(0x28); il.WriteInt32(merger.ReserveMakeArgvHelper()); // call __chibil_make_argv
            }
            if (paramCount == 3)
            {
                il.WriteByte(0x16);             // ldc.i4.0
                il.WriteByte(0xD3);             // conv.i   (envp = (char**)NULL)
            }
            il.WriteByte(0x28); il.WriteInt32(mainFinalToken);   // call main
            il.WriteByte(0x2A);                 // ret
            maxStack = 3;
        }
        else
        {
            throw new LinkException(
                $"unsupported main arity {paramCount}; expected int main(void), " +
                "(int, char**), or (int, char**, char**)");
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
