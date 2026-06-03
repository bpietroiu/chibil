using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ChibilLink;

/// <summary>
/// Builds the raw-passthrough forwarder methods for the export class. Each
/// forwarder reuses the exported function's exact signature and its body is
/// simply <c>ldarg.0 … ldarg.(n-1); call &lt;module fn&gt;; ret</c>. The methods are
/// reserved as the contiguous tail of the MethodDef table and owned by the export
/// class TypeDef (via that TypeDef's MethodList). Mirrors EntrySynthesizer.
/// </summary>
public static class ForwarderSynthesizer
{
    /// <summary>One SynthMethod per exported function, in ExportedMethods order.</summary>
    public static List<MetadataMerger.SynthMethod> Build(MetadataMerger merger)
    {
        var list = new List<MetadataMerger.SynthMethod>(merger.ExportedMethods.Count);
        foreach (var (of, m) in merger.ExportedMethods)
        {
            int n = merger.MethodParamCount(of, m);
            int calleeToken = merger.MapToken(of, m.OriginalToken);

            // If this function has enum usages, build an enum-aware signature so the
            // public forwarder advertises the enum type at those positions. The forwarder
            // IL (ldarg…; call <module fn>; ret) is unchanged — enum↔int interchangeability
            // on the IL stack makes the int-typed <Module> callee verify and run.
            BlobHandle sigBlob = merger.ApiEnumUsageRows.TryGetValue(m.Name, out var posMap)
                ? merger.RewriteMethodSignatureWithEnums(of, m, posMap)
                : merger.RewriteMethodSignature(of, m);

            list.Add(new MetadataMerger.SynthMethod
            {
                Name = m.Name,
                SignatureBlob = sigBlob,
                Il = BuildForwarderIl(n, calleeToken),
                // peak depth = the n args pushed before `call`; the lone return value
                // (if any) never exceeds that. 0-arg calls still need a slot of 1.
                MaxStack = n < 1 ? 1 : n,
                Attributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            });
        }
        return list;
    }

    private static byte[] BuildForwarderIl(int paramCount, int calleeToken)
    {
        var il = new BlobBuilder();
        for (int i = 0; i < paramCount; i++) EmitLdarg(il, i);
        il.WriteByte(0x28);              // call
        il.WriteInt32(calleeToken);
        il.WriteByte(0x2A);              // ret
        return il.ToArray();
    }

    private static void EmitLdarg(BlobBuilder il, int i)
    {
        switch (i)
        {
            case 0: il.WriteByte(0x02); return;   // ldarg.0
            case 1: il.WriteByte(0x03); return;   // ldarg.1
            case 2: il.WriteByte(0x04); return;   // ldarg.2
            case 3: il.WriteByte(0x05); return;   // ldarg.3
        }
        if (i <= 255) { il.WriteByte(0x0E); il.WriteByte((byte)i); return; } // ldarg.s
        il.WriteByte(0xFE); il.WriteByte(0x09); il.WriteUInt16((ushort)i);   // ldarg (long form)
    }
}
