using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Asm2Obj;

namespace ChibilLink;

/// <summary>
/// Resolves UNRESOLVED external method references across all linked objects.
///
/// In CoreCLR-target objects, a call to a function that is declared but not
/// defined in that translation unit is emitted as a <c>MemberReference</c> whose
/// parent is the object's <c>&lt;Module&gt;</c> TypeDef. The merger does not copy
/// these MemberRefs (they have no row), so their IL tokens would be unmapped.
///
/// For each such external reference with name <c>n</c>:
///   • If some object DEFINES a function named <c>n</c> → record an override so
///     the reference's original token maps to that defined function's merged
///     MethodDef token (cross-object call).
///   • Otherwise → synthesize a <c>pinvokeimpl</c> MethodDef bound to a native
///     library from the <c>-l</c> flags, and override the reference to it.
///
/// Ordering contract: this runs DURING prediction (after defined MethodDef rows,
/// before the entry row). Synthesized P/Invoke rows are RESERVED in the merger's
/// Plan so the writer populates them in plan order and the row-prediction
/// assertions still hold. P/Invoke methods have no body and are excluded from the
/// method-body stream.
/// </summary>
public static class SymbolResolver
{
    public static void Resolve(MetadataMerger merger, IReadOnlyList<ObjectFile> objs, List<string> libs)
    {
        var table = new LinkSymbolTable();
        foreach (var of in objs)
            table.AddDefined(of, merger);

        // Synthesized P/Invoke methods, deduped by (native name + concrete
        // signature blob) across all objects. A native variadic callee (e.g.
        // snprintf) produces one MemberRef per call-site argument shape — all
        // named the same but with DIFFERENT signature blobs (e.g. `"%d"` → int
        // vs `"%s"` → char*). Each distinct concrete signature must get its OWN
        // pinvokeimpl MethodDef (all bound to the same native entry name), and
        // each original MemberRef token is redirected to the stub matching ITS
        // signature. Deduping by bare name alone would bind every call site to
        // one stub with one signature → arg-marshalling mismatch.
        //
        // Key: (name, hex-encoded signature blob bytes). The blob bytes are the
        // pre-rewrite call-site signature; two MemberRefs that round-trip to the
        // same emitted pinvokeimpl signature always share identical source bytes
        // (the rewrite is a deterministic per-object token remap), so identical
        // (name, sig) reuse one stub while distinct signatures fork.
        var pinvokeByNameSig = new Dictionary<(string Name, string Sig), int>();

        foreach (var of in objs)
        {
            var md = of.Md;
            var map = merger.MapFor(of);

            for (int r = 1; r <= md.GetTableRowCount(TableIndex.MemberRef); r++)
            {
                var mrH = MetadataTokens.MemberReferenceHandle(r);
                var mr = md.GetMemberReference(mrH);

                // Only external FUNCTION references on <Module> matter here.
                if (mr.Parent.Kind != HandleKind.TypeDefinition) continue;
                var parentTd = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                if (md.GetString(parentTd.Name) != "<Module>") continue;
                if (mr.GetKind() != MemberReferenceKind.Method) continue;

                string name = md.GetString(mr.Name);
                int originalToken = MetadataTokens.GetToken(mrH);

                if (name == "__chibil_os_is_windows")
                {
                    map.RecordExternal(originalToken, merger.ReserveOsIsWindowsIntrinsic());
                    continue;
                }

                if (table.DefinedMethodToken.TryGetValue(name, out int definedToken))
                {
                    // Cross-object: redirect to the defining function's merged row.
                    map.RecordExternal(originalToken, definedToken);
                    continue;
                }

                // Native import: synthesize (or reuse) a P/Invoke stub keyed on
                // this MemberRef's OWN concrete signature, so distinct vararg
                // shapes get distinct stubs.
                byte[] sigBytes = md.GetBlobBytes(mr.Signature);
                var key = (name, Convert.ToHexString(sigBytes));
                if (!pinvokeByNameSig.TryGetValue(key, out int pinvokeToken))
                {
                    var sigReader = md.GetBlobReader(mr.Signature);
                    pinvokeToken = SynthesizePInvoke(merger, of, name, sigReader, libs);
                    pinvokeByNameSig[key] = pinvokeToken;
                }
                map.RecordExternal(originalToken, pinvokeToken);
            }
        }
    }

    private static int SynthesizePInvoke(
        MetadataMerger merger, ObjectFile of, string name, BlobReader signatureBlobReader, List<string> libs)
    {
        // MVP limitation: when no -l flag is given we cannot bind this symbol.
        if (libs.Count == 0)
            throw new LinkException($"unresolved symbol '{name}' and no -l libraries given");

        // MVP limitation: always bind to the first -l library. When multiple
        // -l flags are given we cannot determine which library exports this
        // symbol without a symbol table, so we warn and fall through.
        string lib = MapLib(libs[0]);
        if (libs.Count > 1)
            Console.Error.WriteLine(
                $"chibil-link: warning: '{name}' bound to '{lib}' (first -l library); " +
                $"per-symbol multi-library resolution is not yet implemented.");
        var moduleRef = merger.GetOrAddModuleRef(lib);

        // Copy the call-site signature, remapping tokens with this object's map.
        var sigB = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(signatureBlobReader, merger.MapFor(of), sigB);
        var sigBlob = merger.Md.GetOrAddBlob(sigB);

        var stub = new MetadataMerger.PInvokeStub
        {
            Name = name,
            SignatureBlob = sigBlob,
            ModuleRef = moduleRef,
        };
        return merger.ReservePInvokeRow(stub);
    }

    private static string MapLib(string l) => l switch
    {
        "c" => "libc.so.6",
        "m" => "libm.so.6",
        _ => l.Contains('.') ? l : $"lib{l}.so",   // e.g. "msvcrt.dll" -> used as-is
    };
}
