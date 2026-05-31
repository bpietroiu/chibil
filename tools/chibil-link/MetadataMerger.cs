using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Asm2Obj;

namespace ChibilLink;

/// <summary>
/// Merges the metadata of one or more CoreCLR-target COFF objects into a single
/// shared <see cref="MetadataBuilder"/> destined for a pure-MSIL PE.
///
/// Responsibilities (Phase 0):
///   • Copy + dedup AssemblyRefs (by name) across all objects.
///   • Copy the TypeRefs referenced by surviving method signatures.
///   • Provide a single shared <c>&lt;Module&gt;</c> TypeDef that owns every method.
///   • PREDICT the final MethodDef row of every method across all objects, plus
///     the synthesized entry method (assigned last), so IL token fixup can run
///     before any MethodDef row is added. This is the ordering contract demanded
///     by System.Reflection.Metadata's PE emission (a method body must be encoded
///     before its MethodDef row, yet the body's IL holds tokens of OTHER methods'
///     final rows).
///
/// The actual MethodDef-row POPULATION (AddMethodDefinition) is performed by
/// <see cref="PeWriter"/> during PE emission, because the body offset returned by
/// MethodBodyStreamEncoder must be baked into the row. This class exposes the
/// predicted ordering (<see cref="Plan"/>) and the per-object token maps so the
/// writer can populate rows in the predicted order and assert the predictions.
///
/// Per-object phase tables (TypeRef/TypeDef/StandAloneSig/etc.) are copied here
/// eagerly so that <see cref="MapToken"/> resolves signature/IL tokens to their
/// final rows. Method rows are reserved (predicted) but added later by the writer.
/// </summary>
public sealed class MetadataMerger
{
    public readonly MetadataBuilder Builder = new();

    private readonly IReadOnlyList<ObjectFile> _objs;

    // Per object: a TokenMap that maps input handles -> output rows for that obj.
    private readonly Dictionary<ObjectFile, TokenMap> _maps = new();

    // Dedup AssemblyRefs by name -> output AssemblyReferenceHandle.
    private readonly Dictionary<string, AssemblyReferenceHandle> _assemblyRefByName = new();

    /// <summary>The core-library AssemblyRef actually emitted (mscorlib facade
    /// kept verbatim, or remapped). Recorded for diagnostics.</summary>
    public string CoreLibReferenceUsed { get; private set; } = "mscorlib (verbatim)";

    // Predicted emission plan: every method, in the deterministic order their
    // MethodDef rows are assigned. The synthesized entry is appended by
    // ReserveEntryRow() as the final entry (with Obj == null).
    public sealed class MethodSlot
    {
        public ObjectFile Obj;          // null => synthesized entry
        public ObjMethod Method;        // null => synthesized entry
        public int PredictedRow;        // 1-based MethodDef row
    }

    public readonly List<MethodSlot> Plan = new();

    // Output <Module> TypeDef is row 1; methods all hang off it.
    public const int ModuleTypeDefRow = 1;

    // Running output-row counter for predicted MethodDef rows.
    private int _outMethodRow;

    public MetadataMerger(IReadOnlyList<ObjectFile> objs)
    {
        _objs = objs;
    }

    public TokenMap MapFor(ObjectFile of) => _maps[of];

    /// <summary>Map an original token from <paramref name="of"/> to its final
    /// output token. Throws <see cref="LinkException"/> on an unmapped token.</summary>
    public int MapToken(ObjectFile of, int originalToken)
    {
        if (originalToken == 0) return 0;
        var map = _maps[of];
        int mapped = ((int)((uint)originalToken & 0x70000000)) == 0x70000000
            ? map.MapUserStringToken(originalToken)
            : map.MapToken(originalToken);
        if (mapped == 0 || (mapped & 0x00FFFFFF) == 0)
            throw new LinkException(
                $"{of.Path}: IL references token 0x{originalToken:X8} which has no mapping " +
                "(external symbol resolution is a later task).");
        return mapped;
    }

    /// <summary>
    /// Phase 1: copy AssemblyRefs + TypeRefs for every object and predict the
    /// MethodDef rows for every real method. Must be called before any method
    /// body / row is emitted. Does NOT add MethodDef rows (the writer does that).
    /// </summary>
    public void MergeAndPredict()
    {
        foreach (var of in _objs)
            _maps[of] = new TokenMap(of.Md, Builder);

        // ── AssemblyRefs (dedup by name) ──────────────────────────────────────
        foreach (var of in _objs)
            CopyAssemblyRefs(of);

        // ── TypeRefs (copied per object; needed by method signatures) ─────────
        foreach (var of in _objs)
            CopyTypeRefs(of);

        // ── StandAloneSigs (local-variable sigs) ──────────────────────────────
        foreach (var of in _objs)
            CopyStandaloneSigs(of);

        // ── Predict MethodDef rows in a fixed order: per object, methods in the
        //    order they appear in ObjectFile.Methods. Entry method appended last
        //    via ReserveEntryRow().
        foreach (var of in _objs)
        {
            var map = _maps[of];
            foreach (var m in of.Methods)
            {
                _outMethodRow++;
                map.SetMethodDef(m.Handle, _outMethodRow);
                Plan.Add(new MethodSlot { Obj = of, Method = m, PredictedRow = _outMethodRow });
            }
        }
    }

    /// <summary>Reserve the MethodDef row for the synthesized entry method.
    /// Returns the predicted row (and handle). Call exactly once, after
    /// MergeAndPredict, before emitting bodies.</summary>
    public (int row, MethodDefinitionHandle handle) ReserveEntryRow()
    {
        _outMethodRow++;
        Plan.Add(new MethodSlot { Obj = null, Method = null, PredictedRow = _outMethodRow });
        return (_outMethodRow, MetadataTokens.MethodDefinitionHandle(_outMethodRow));
    }

    private void CopyAssemblyRefs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.AssemblyRef); r++)
        {
            var inH = MetadataTokens.AssemblyReferenceHandle(r);
            var ar = md.GetAssemblyReference(inH);
            string name = md.GetString(ar.Name);

            if (_assemblyRefByName.TryGetValue(name, out var existing))
            {
                map.SetAssemblyRef(inH, MetadataTokens.GetRowNumber(existing));
                continue;
            }

            AssemblyReferenceHandle outH = AddAssemblyRef(of, ar, name);
            _assemblyRefByName[name] = outH;
            map.SetAssemblyRef(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    private AssemblyReferenceHandle AddAssemblyRef(ObjectFile of, AssemblyReference ar, string name)
    {
        var md = of.Md;
        // Phase 0: copy the AssemblyRef verbatim. CoreCLR ships an mscorlib
        // facade that forwards to System.Private.CoreLib, so an in-process load
        // resolves the verbatim mscorlib reference. (If a future input needs a
        // System.Runtime remap, branch here.)
        if (name == "mscorlib")
            CoreLibReferenceUsed = "mscorlib (verbatim facade)";

        return Builder.AddAssemblyReference(
            Builder.GetOrAddString(name),
            ar.Version,
            ar.Culture.IsNil ? default : Builder.GetOrAddString(md.GetString(ar.Culture)),
            ar.PublicKeyOrToken.IsNil ? default : Builder.GetOrAddBlob(md.GetBlobBytes(ar.PublicKeyOrToken)),
            ar.Flags,
            ar.HashValue.IsNil ? default : Builder.GetOrAddBlob(md.GetBlobBytes(ar.HashValue)));
    }

    private void CopyTypeRefs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.TypeRef); r++)
        {
            var inH = MetadataTokens.TypeReferenceHandle(r);
            var tr = md.GetTypeReference(inH);
            EntityHandle outScope = tr.ResolutionScope.IsNil
                ? default
                : map.MapEntity(tr.ResolutionScope);
            var outH = Builder.AddTypeReference(
                outScope,
                Builder.GetOrAddString(md.GetString(tr.Namespace)),
                Builder.GetOrAddString(md.GetString(tr.Name)));
            map.SetTypeRef(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    private void CopyStandaloneSigs(ObjectFile of)
    {
        var md = of.Md;
        var map = _maps[of];
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.StandAloneSig); r++)
        {
            var inH = MetadataTokens.StandaloneSignatureHandle(r);
            var ss = md.GetStandaloneSignature(inH);
            var sigReader = md.GetBlobReader(ss.Signature);
            var sigBuilder = new BlobBuilder();
            EcmaSignatureRewriter.RewriteStandaloneSignatureBlob(sigReader, map, sigBuilder);
            var outH = Builder.AddStandaloneSignature(Builder.GetOrAddBlob(sigBuilder));
            map.SetStandaloneSig(inH, MetadataTokens.GetRowNumber(outH));
        }
    }

    /// <summary>Rewrite a method's signature blob into the shared heap.</summary>
    public BlobHandle RewriteMethodSignature(ObjectFile of, ObjMethod m)
    {
        var md = of.Md;
        var def = md.GetMethodDefinition(m.Handle);
        var sigReader = md.GetBlobReader(def.Signature);
        var sigBuilder = new BlobBuilder();
        EcmaSignatureRewriter.RewriteMethodSignature(sigReader, _maps[of], sigBuilder);
        return Builder.GetOrAddBlob(sigBuilder);
    }

    /// <summary>Map the local-variable signature handle for a method (or nil).
    /// Throws if a non-nil local-var sig fails to remap, converting a silent
    /// runtime InvalidProgramException into a clear link error.</summary>
    public StandaloneSignatureHandle MapLocalSig(ObjectFile of, ObjMethod m)
    {
        if (m.LocalSig.IsNil) return default;
        var mapped = _maps[of].MapStandaloneSig(m.LocalSig);
        if (mapped.IsNil || MetadataTokens.GetRowNumber(mapped) == 0)
            throw new LinkException($"lost local-variable signature for method {m.Name}");
        return mapped;
    }
}
