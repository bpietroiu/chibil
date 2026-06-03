using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
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
    public static void Resolve(MetadataMerger merger, IReadOnlyList<ObjectFile> objs,
        List<string> libs, IReadOnlyDictionary<string, string> pinvokeMap,
        IReadOnlyList<string> libSearchPaths = null)
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

        // Probes the -l libraries (like a real linker scanning each library's
        // export table) so each symbol binds to the library that actually exports
        // it — e.g. tgetent → libtinfo.so.6, printf → libc.so.6 — rather than
        // every symbol going to the first -l. Built once; caches load + lookups.
        var probe = new LibraryProbe(libSearchPaths);

        // Synthesized Layer-1-variadic adapters, deduped by (name, call-site sig). A
        // cross-TU call to a chibil-defined variadic (e.g. builtin_error) arrives as a
        // Layer-2 cdecl MemberRef (no __va); the adapter packs the varargs into a
        // va-buffer and calls the Layer-1 definition.
        var adapterByNameSig = new Dictionary<(string Name, string Sig), int>();

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

                if (name == MetadataMerger.OsIsWindowsIntrinsicName)
                {
                    map.RecordExternal(originalToken, merger.ReserveOsIsWindowsIntrinsic());
                    continue;
                }

                // setjmp/longjmp runtime helpers (synthesized in MergeAndPredict).
                if (name.StartsWith("__chibil_longjmp", StringComparison.Ordinal))
                {
                    int htok = merger.ResolveSetjmpHelper(name);
                    if (htok != 0) { map.RecordExternal(originalToken, htok); continue; }
                }

                if (table.DefinedMethodToken.TryGetValue(name, out int definedToken))
                {
                    // Cross-TU call to a chibil-defined Layer-1 variadic: the call site
                    // emitted a Layer-2 cdecl MemberRef (no hidden __va buffer pointer),
                    // but the definition takes (fixed…, __va). Bridge with a per-signature
                    // adapter that packs the varargs and calls the definition.
                    if (table.Layer1VariadicFixed.TryGetValue(name, out int nFixed))
                    {
                        byte[] aSig = md.GetBlobBytes(mr.Signature);
                        var aKey = (name, Convert.ToHexString(aSig));
                        if (!adapterByNameSig.TryGetValue(aKey, out int adapterToken))
                        {
                            adapterToken = merger.ReserveVariadicAdapter(of, mr.Signature, definedToken, nFixed);
                            adapterByNameSig[aKey] = adapterToken;
                        }
                        map.RecordExternal(originalToken, adapterToken);
                        continue;
                    }
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
                    pinvokeToken = SynthesizePInvoke(merger, of, name, sigReader, libs, pinvokeMap, probe);
                    pinvokeByNameSig[key] = pinvokeToken;
                }
                merger.PInvokeStubByName.TryAdd(name, pinvokeToken);
                map.RecordExternal(originalToken, pinvokeToken);
            }
        }
    }

    private static int SynthesizePInvoke(
        MetadataMerger merger, ObjectFile of, string name, BlobReader signatureBlobReader,
        List<string> libs, IReadOnlyDictionary<string, string> pinvokeMap, LibraryProbe probe)
    {
        string lib;
        if (pinvokeMap.TryGetValue(name, out string libTok))
        {
            lib = MapLib(libTok);                    // explicit per-symbol routing (override)
        }
        else if (libs.Count == 1)
        {
            lib = MapLib(libs[0]);                   // only one choice — no probing needed
        }
        else if (libs.Count > 1)
        {
            // Like ld: bind to the -l library that actually EXPORTS the symbol.
            // Fall back to the first -l if none can be probed (e.g. the libraries
            // aren't loadable on this host) — a genuinely missing symbol then
            // surfaces as a runtime EntryPointNotFound, as it did before.
            lib = probe.FindExporting(libs, name);
            if (lib == null)
            {
                lib = MapLib(libs[0]);
                Console.Error.WriteLine(
                    $"chibil-link: warning: '{name}' not found in any -l library; " +
                    $"bound to '{lib}' (may fail at runtime). Use --pinvoke to override.");
            }
        }
        else
        {
            throw new LinkException(
                $"unresolved symbol '{name}' and no -l libraries or --pinvoke mapping given");
        }
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
            Lib = lib,
        };
        return merger.ReservePInvokeRow(stub);
    }

    // Map a library token to a platform module name. Unknown tokens fall through to
    // `lib<x>.so` (and dotted names pass through as-is) with NO diagnostic — a typo'd
    // token surfaces as a runtime DllNotFoundException, consistent with the -l path.
    internal static string MapLib(string l) => l switch
    {
        "c" => "libc.so.6",
        "m" => "libm.so.6",
        "tinfo" => "libtinfo.so.6",   // termcap/terminfo (ncurses): tgetent, tputs, …
        "kernel32" => "kernel32.dll",
        _ => l.Contains('.') ? l : $"lib{l}.so",   // e.g. "msvcrt.dll" -> used as-is
    };
}

/// <summary>
/// Determines which <c>-l</c> library exports a given symbol, the way a real
/// linker scans each library's export table. Loads each library once (via the
/// OS loader, like <c>dlopen</c>) and probes it with <c>dlsym</c>-equivalent
/// <see cref="NativeLibrary.TryGetExport"/>. Results are cached. If a library
/// cannot be loaded on this host (e.g. a Linux .so while linking on Windows for
/// tests), it simply contributes no exports and the caller falls back.
/// </summary>
internal sealed class LibraryProbe
{
    private readonly Dictionary<string, IntPtr> _handles = new();      // mapped lib name -> handle (Zero = unloadable)
    private readonly Dictionary<(string, string), bool> _exports = new();
    private readonly IReadOnlyList<string> _searchPaths;              // -L dirs, tried before default loader paths

    public LibraryProbe(IReadOnlyList<string> searchPaths = null)
        => _searchPaths = searchPaths ?? Array.Empty<string>();

    /// <summary>The mapped name of the first lib in <paramref name="libs"/> that
    /// exports <paramref name="symbol"/>, or null if none (or none loadable).</summary>
    public string FindExporting(List<string> libs, string symbol)
    {
        foreach (var l in libs)
        {
            string mapped = SymbolResolver.MapLib(l);
            if (Exports(mapped, symbol))
                return mapped;
        }
        return null;
    }

    private bool Exports(string mappedLib, string symbol)
    {
        var key = (mappedLib, symbol);
        if (_exports.TryGetValue(key, out bool cached))
            return cached;
        IntPtr h = HandleFor(mappedLib);
        bool ok = h != IntPtr.Zero && NativeLibrary.TryGetExport(h, symbol, out _);
        _exports[key] = ok;
        return ok;
    }

    private IntPtr HandleFor(string mappedLib)
    {
        if (_handles.TryGetValue(mappedLib, out var h))
            return h;
        h = IntPtr.Zero;
        // -L search dirs first, then the bare name via the default loader paths.
        foreach (var dir in _searchPaths)
        {
            try { if (NativeLibrary.TryLoad(System.IO.Path.Combine(dir, mappedLib), out h)) break; }
            catch { h = IntPtr.Zero; }
        }
        if (h == IntPtr.Zero)
        {
            try { if (!NativeLibrary.TryLoad(mappedLib, out h)) h = IntPtr.Zero; }
            catch { h = IntPtr.Zero; }   // bad name / unsupported — treat as no exports
        }
        _handles[mappedLib] = h;
        return h;
    }
}
