# SQLite SP2 — `Sqlite3.Native` Export Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make chibil-compiled SQLite consumable from C# by adding a linker export feature that emits a `public static class` of raw-passthrough forwarders to the extern-linkage C functions, proven by a Roslyn-compiled `unsafe` C# program running the `:memory:` CRUD to exit 55 on Windows and Linux/WSL.

**Architecture:** Linker-only, opt-in via `--export-class=<Namespace.Name>` (default off → byte-for-byte current behavior). When set, the linker (1) predicts an extra public `Native` TypeDef at **TypeDef row 2** (shifting the value-type TypeDefs to row 3+), (2) reserves **forwarder MethodDef rows as the contiguous tail** (after the entry method), owned by `Native`, each body `ldarg…; call <module fn>; ret`, and (3) promotes value-type TypeDefs referenced by exported signatures to `public`. chibil codegen and the `.obj` format are untouched. Forwarders reuse the existing `SynthMethod`/`Plan` machinery; the row-prediction `AssertRow` checks are the guardrail for the metadata layout.

**Tech Stack:** C# / .NET 10, `System.Reflection.Metadata` / `.PortableExecutable`; `tools/chibil-link/`; xUnit; Roslyn (`Microsoft.CodeAnalysis.CSharp`) for the compile-and-run consumer; `DotnetHostRunner` (Windows) + `WslRunner` (Linux) subprocess runners.

**Reference docs:** Spec `docs/superpowers/specs/2026-06-01-sqlite-managed-sp2-design.md`.

**Key existing code (verified, current line numbers):**
- `tools/chibil-link/Program.cs` — `LinkOptions` (`:26-45`: `Inputs`, `Libraries`, `Output`, `Parse`); `Linker.Run` (`:49-64`) calls `LinkPipeline.LinkToBytes(objs, opts.Libraries)`.
- `tools/chibil-link/PeWriter.cs` — `LinkPipeline.LinkToBytes(objs, libs)` (`:17-22`); `PeWriter` ctor (`:42-46`); `Write()` constructs `new MetadataMerger(_objs)` (`:50`), reserves `.cctor` synth (`:69-88`), `ReserveEntryRow()` (`:91`), `EntrySynthesizer.Synthesize` (`:95`); body-emission loop handles `slot.Synth` (`:123-129`); `<Module>` TypeDef emit (`:201-209`); value-type TypeDef loop (`:216-230`, uses `totalFields = merger.TotalFieldRows` and `totalMethods = merger.Plan.Count`, `AssertRow(ct.PredictedRow,…)`); MethodDef population loop (`:233-301`, handles `slot.Synth` at `:264-270`).
- `tools/chibil-link/MetadataMerger.cs` — ctor `MetadataMerger(IReadOnlyList<ObjectFile> objs)` (`:144-147`); `MergeAndPredict()` (`:172-226`): CopyAssemblyRefs → CopyTypeRefs → `CopyDataFieldsAndTypeDefs` (value-type TypeDef prediction via `_outTypeDefRow++`) → Ensure* passes → method-row prediction loop (`:216-225`, has `of` and `m`); `_outTypeDefRow` (`:141`, starts at `ModuleTypeDefRow = 1`); `_outMethodRow` (`:99`); `ReserveSynthRow(SynthMethod)` (`:256-261`); `ReserveEntryRow()` (`:266-271`); `RewriteMethodSignature(of, m)` (`:702-710`, public, returns `BlobHandle`); `MapToken(of, originalToken)` (`:153-165`, public); `SynthMethod` class (`:74-81`: `Name`, `SignatureBlob`, `Il`, `MaxStack`, `Attributes`); `MethodSlot.Synth` (`:68`); `EnsureMethodSigTypeDefs`/`ScanSigTypeForTypeDefs` (`:468-563`, the signature TypeDef walker to mirror); `_maps[of]` is a `TokenMap` with `MapTypeDef(handle)`; `_assemblyRefByName["mscorlib"]` exists after `CopyAssemblyRefs`; `Builder` is the shared `MetadataBuilder`.
- `tools/chibil-link/EntrySynthesizer.cs` — pattern to mirror for `ForwarderSynthesizer`; `ObjMethod` has `.Name`, `.Handle`, `.OriginalToken`.
- `SynthMethod` bodies: the body-emission loop encodes `slot.Synth.Il` with `slot.Synth.MaxStack`; the population loop adds the MethodDef with `slot.Synth.Name/Attributes/SignatureBlob`. Forwarders are just `SynthMethod`s, so both loops already handle them — the only new work is the `Native` TypeDef + reserving forwarder rows + ownership via `MethodList`.

**Test runners (exist):** `TestCompiler.CompileToObj(src, TargetProfile.CoreClr)` / `CompileFileToObj(path, target, defs, incs)`; `ObjectFile.Load(bytes, name)`; `LinkPipeline.LinkToBytes(objs, libs)`; `DotnetHostRunner.DotnetAvailable()` / `RunPeViaDotnetHost(pe, out output)`; `WslRunner.Available()` / `Run(pe, WslRunner.NetCoreRuntimeConfig)`.

**Metadata-layout invariant (the crux, applies to Tasks 1-4):** MethodDef ownership is by **contiguous row range**; consecutive TypeDef rows must have **non-decreasing** `MethodList`. Target layout when export is on:
- TypeDef rows: `<Module>`=1 (`MethodList`=1), **`Native`=2** (`MethodList`=firstForwarderRow), value-types=3..K (`MethodList`=totalMethods+1).
- MethodDef rows: all existing methods (real fns, P/Invoke stubs, `.cctor`, entry) = rows `1..R`; **forwarders = tail rows `R+1..M`**.
- So `<Module>` owns `[1, firstForwarderRow)` = rows `1..R`; `Native` owns `[firstForwarderRow, totalMethods+1)` = the forwarders; value-types own empty ranges. When there are **zero** forwarders, `Native.MethodList = totalMethods+1` (empty, valid).

---

## Task 1: Opt-in option + empty `Native` TypeDef at row 2

Threads `--export-class` through and emits an **empty** public static class (no methods yet), exercising the riskiest piece — the TypeDef-row-2 prediction and the value-type row shift — in isolation.

**Files:**
- Modify: `tools/chibil-link/Program.cs` (`LinkOptions`, `Linker.Run`)
- Modify: `tools/chibil-link/PeWriter.cs` (`LinkPipeline.LinkToBytes`, `PeWriter` ctor + `Write`)
- Modify: `tools/chibil-link/MetadataMerger.cs` (ctor, `MergeAndPredict`, accessors, `System.Object` ref helper)
- Create: `tests/Chibil.Tests/CoreClr/ExportClassTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chibil.Tests/CoreClr/ExportClassTests.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using ChibilLink;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class ExportClassTests
{
    static Assembly LinkAndLoad(string src, string exportClass)
    {
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "t.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new List<string>(), exportClass);
        return Assembly.Load(pe); // metadata-only inspection; nothing is executed
    }

    [Fact]
    public void Export_class_absent_by_default()
    {
        Assembly asm = LinkAndLoad("int main(void){ return 0; }", null);
        Assert.Null(asm.GetType("Foo.Bar"));
    }

    [Fact]
    public void Empty_export_class_is_public_static()
    {
        // A program whose only function is main (which is excluded from export)
        // yields an export class with zero methods.
        Assembly asm = LinkAndLoad("int main(void){ return 0; }", "Foo.Bar");
        Type t = asm.GetType("Foo.Bar");
        Assert.NotNull(t);
        Assert.True(t.IsPublic, "export class must be public");
        Assert.True(t.IsAbstract && t.IsSealed, "export class must be a static class (abstract+sealed)");
        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }
}
```

- [ ] **Step 2: Run it — expect FAIL (compile error: LinkToBytes has no 3-arg overload)**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter ExportClassTests`
Expected: FAIL — does not compile (`LinkToBytes` takes 2 args). That's the TDD starting point.

- [ ] **Step 3: Add the option to the CLI** (`Program.cs`)

In `LinkOptions` (after `public string Output = "a.dll";`):
```csharp
    public string ExportClass = null;   // --export-class=<Namespace.Name>; null = no export type
```
In `LinkOptions.Parse`, before the generic `if (a.StartsWith("-"))` rejection:
```csharp
            if (a.StartsWith("--export-class=")) { o.ExportClass = a["--export-class=".Length..]; continue; }
```
In `Linker.Run`, change the link call to:
```csharp
        byte[] pe = LinkPipeline.LinkToBytes(objs, opts.Libraries, opts.ExportClass);
```

- [ ] **Step 4: Thread the option through the pipeline** (`PeWriter.cs`)

Change `LinkPipeline.LinkToBytes`:
```csharp
    public static byte[] LinkToBytes(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null)
    {
        if (objs == null || objs.Count == 0)
            throw new LinkException("no input objects.");
        return new PeWriter(objs, libs ?? new List<string>(), exportClass).Write();
    }
```
Add a field + ctor param to `PeWriter`:
```csharp
    private readonly string _exportClass;

    public PeWriter(IReadOnlyList<ObjectFile> objs, List<string> libs, string exportClass = null)
    {
        _objs = objs;
        _libs = libs;
        _exportClass = ValidateExportClass(exportClass);
    }

    private static string ValidateExportClass(string name)
    {
        if (name == null) return null;
        if (name.Length == 0) throw new LinkException("--export-class name must not be empty.");
        foreach (var part in name.Split('.'))
            if (part.Length == 0)
                throw new LinkException($"--export-class '{name}' has an empty namespace/type segment.");
        return name;
    }
```

- [ ] **Step 5: Predict the `Native` TypeDef at row 2** (`MetadataMerger.cs`)

Add the export class to the merger ctor + a backing field and accessor:
```csharp
    private readonly string _exportClass;
    private int _exportTypeDefRow;   // 0 = no export type

    public MetadataMerger(IReadOnlyList<ObjectFile> objs, string exportClass = null)
    {
        _objs = objs;
        _exportClass = exportClass;
    }

    /// <summary>Output TypeDef row reserved for the export class (row 2), or 0 if disabled.</summary>
    public int ExportTypeDefRow => _exportTypeDefRow;
    public string ExportClass => _exportClass;
```
In `MergeAndPredict()`, **after** the `CopyTypeRefs` loop and **before** the `CopyDataFieldsAndTypeDefs` loop, reserve the export TypeDef row so value-types follow at row 3+:
```csharp
        // Reserve the export class's TypeDef row (row 2) BEFORE value-type TypeDefs
        // so they shift to 3+ and the export type sits directly after <Module>.
        if (_exportClass != null)
            _exportTypeDefRow = ++_outTypeDefRow;
```
Add a helper to resolve `System.Object` in the core lib (Native's base type):
```csharp
    /// <summary>TypeRef to System.Object in the core library, for the export class's base.</summary>
    public EntityHandle GetOrAddCoreObjectRef()
    {
        if (!_assemblyRefByName.TryGetValue("mscorlib", out var corlib))
            throw new LinkException("no core-library AssemblyRef available for the export class base type.");
        var key = (MetadataTokens.GetToken(corlib), "System", "Object");
        if (_typeRefByKey.TryGetValue(key, out var existing))
            return existing;
        var h = Builder.AddTypeReference(corlib, Builder.GetOrAddString("System"), Builder.GetOrAddString("Object"));
        _typeRefByKey[key] = h;
        return h;
    }
```
> `_assemblyRefByName` and `_typeRefByKey` are existing private fields (`:45`, `:50`). The mscorlib ref exists after `CopyAssemblyRefs` (every chibil object references it).

- [ ] **Step 6: Pass the export class into the merger** (`PeWriter.cs`, `Write`)

Change `var merger = new MetadataMerger(_objs);` to:
```csharp
        var merger = new MetadataMerger(_objs, _exportClass);
```

- [ ] **Step 7: Emit the `Native` TypeDef between `<Module>` and the value-types** (`PeWriter.cs`, `Write`)

Immediately **after** the `<Module>` TypeDef emission (`AssertRow(MetadataMerger.ModuleTypeDefRow,…)` at `:209`) and **before** `int totalFields = merger.TotalFieldRows;` (`:214`), insert:
```csharp
        // ── Export class TypeDef (row 2), public static class owning the forwarder
        //    tail. With zero forwarders, MethodList points past the end (empty).
        if (merger.ExportTypeDefRow != 0)
        {
            int firstForwarderRow = FirstForwarderRow(merger); // = totalMethods+1 when there are no forwarders
            string full = merger.ExportClass;
            int dot = full.LastIndexOf('.');
            string ns = dot < 0 ? "" : full[..dot];
            string nm = dot < 0 ? full : full[(dot + 1)..];
            var exportTd = mdBuilder.AddTypeDefinition(
                System.Reflection.TypeAttributes.Public
                    | System.Reflection.TypeAttributes.Abstract
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.Class
                    | System.Reflection.TypeAttributes.BeforeFieldInit,
                ns.Length == 0 ? default : mdBuilder.GetOrAddString(ns),
                mdBuilder.GetOrAddString(nm),
                merger.GetOrAddCoreObjectRef(),
                MetadataTokens.FieldDefinitionHandle(merger.TotalFieldRows + 1),  // owns no fields
                MetadataTokens.MethodDefinitionHandle(firstForwarderRow));
            AssertRow(merger.ExportTypeDefRow, MetadataTokens.GetRowNumber(exportTd), "TypeDef export class");
        }
```
Add a private helper to `PeWriter` (Task 1: no forwarders yet, so it returns "past the end"; Task 2 makes it real):
```csharp
    // First MethodDef row owned by the export class. In Task 1 there are no
    // forwarders, so the export class owns an empty range at the end.
    private static int FirstForwarderRow(MetadataMerger merger) => merger.Plan.Count + 1;
```
> The value-type TypeDef loop directly below is unchanged in code, but its `AssertRow(ct.PredictedRow,…)` now expects rows 3+ — satisfied because Step 5 shifted them. Do NOT change that loop in this task.

- [ ] **Step 8: Run the tests — expect PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter ExportClassTests`
Expected: PASS (both tests). If `AssertRow` fires for a value-type TypeDef, the row shift (Step 5) and emission order disagree — verify Step 5 runs before `CopyDataFieldsAndTypeDefs`.

- [ ] **Step 9: Confirm no regression**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: all green (export path is opt-in; absent `--export-class` is unchanged). Baseline: 46 passed / 1 skipped, now +2 new = 48 passed / 1 skipped.

- [ ] **Step 10: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/ExportClassTests.cs
git commit -m "feat(linker): --export-class emits an empty public static class (TypeDef row 2)"
```

---

## Task 2: Forwarders for exported functions

Adds the forwarder tail: every extern-linkage function (`UnmanagedExport`, excluding `main`) gets a public static forwarder on the export class. Tested with an `int add(int,int)` (no pointers → reflection-invokable directly).

**Files:**
- Create: `tools/chibil-link/ForwarderSynthesizer.cs`
- Modify: `tools/chibil-link/MetadataMerger.cs` (collect exported methods; param-count helper)
- Modify: `tools/chibil-link/PeWriter.cs` (reserve forwarder rows; real `FirstForwarderRow`)
- Modify: `tests/Chibil.Tests/CoreClr/ExportClassTests.cs`

- [ ] **Step 1: Write the failing test** (append to `ExportClassTests.cs`)

```csharp
    [Fact]
    public void Forwarder_calls_exported_function()
    {
        // `add` is extern-linkage (non-static) → exported; `main` is excluded.
        Assembly asm = LinkAndLoad(
            "int add(int a, int b){ return a + b; } int main(void){ return add(2, 3); }",
            "N.S");
        Type t = asm.GetType("N.S");
        Assert.NotNull(t);
        MethodInfo add = t.GetMethod("add", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(add);
        Assert.Null(t.GetMethod("main", BindingFlags.Public | BindingFlags.Static)); // main excluded
        object result = add.Invoke(null, new object[] { 2, 3 });
        Assert.Equal(5, (int)result);
    }
```

- [ ] **Step 2: Run it — expect FAIL**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Forwarder_calls_exported_function`
Expected: FAIL — `t.GetMethod("add")` is null (no forwarders emitted yet).

- [ ] **Step 3: Collect exported methods + a param-count helper** (`MetadataMerger.cs`)

Add a public list, populated during method-row prediction, and a param-count helper:
```csharp
    /// <summary>Real methods marked extern-linkage (UnmanagedExport) and eligible
    /// for export (excludes the synthesized entry and the C 'main').</summary>
    public readonly List<(ObjectFile of, ObjMethod m)> ExportedMethods = new();

    private const MethodAttributes UnmanagedExportFlag = (MethodAttributes)0x0008;
```
In `MergeAndPredict()`, inside the method-row prediction loop (the `foreach (var m in of.Methods)` block, after `Plan.Add(...)`), add:
```csharp
                if (_exportClass != null && m.Name != "main")
                {
                    var mdef = of.Md.GetMethodDefinition(m.Handle);
                    if ((mdef.Attributes & UnmanagedExportFlag) != 0)
                        ExportedMethods.Add((of, m));
                }
```
Add the param-count helper (decodes the method signature's parameter count, including chibil's hidden va param for variadics — forwarders pass everything through):
```csharp
    /// <summary>Number of parameters in a method's signature (raw count, incl. any
    /// hidden trailing va-buffer param). Used to emit the forwarder's ldarg sequence.</summary>
    public int MethodParamCount(ObjectFile of, ObjMethod m)
    {
        var def = of.Md.GetMethodDefinition(m.Handle);
        var reader = of.Md.GetBlobReader(def.Signature);
        var header = reader.ReadSignatureHeader();
        if (header.IsGeneric) reader.ReadCompressedInteger();
        return reader.ReadCompressedInteger();
    }
```

- [ ] **Step 4: Create `ForwarderSynthesizer`** (`tools/chibil-link/ForwarderSynthesizer.cs`)

```csharp
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
            list.Add(new MetadataMerger.SynthMethod
            {
                Name = m.Name,
                SignatureBlob = merger.RewriteMethodSignature(of, m), // identical sig (raw pointers)
                Il = BuildForwarderIl(n, calleeToken),
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
```

- [ ] **Step 5: Reserve forwarder rows + real `FirstForwarderRow`** (`PeWriter.cs`, `Write`)

**After** `var (entryRow, entryHandle) = merger.ReserveEntryRow();` and the entry synth (`:91-95`), reserve the forwarders so they become the tail of the Plan:
```csharp
        // Reserve forwarder rows AFTER the entry so they form the contiguous tail
        // owned by the export class. Built here (post-prediction) because each
        // forwarder body calls an exported method whose final token is now known.
        var forwarders = merger.ExportTypeDefRow != 0
            ? ForwarderSynthesizer.Build(merger)
            : new List<MetadataMerger.SynthMethod>();
        int firstForwarderRow = merger.Plan.Count + 1;   // next row to be reserved
        foreach (var fwd in forwarders)
            merger.ReserveSynthRow(fwd);
```
Replace the placeholder `FirstForwarderRow` helper from Task 1 with one that uses this value. Simplest: store it in a field and have the helper read it. Add a field and set it:
```csharp
    private int _firstForwarderRow;   // first MethodDef row owned by the export class
```
Set `_firstForwarderRow = firstForwarderRow;` right after the reserve loop above, and change the helper to:
```csharp
    private int FirstForwarderRow(MetadataMerger merger)
        => _firstForwarderRow != 0 ? _firstForwarderRow : merger.Plan.Count + 1;
```
> `ReserveSynthRow` appends each forwarder to `merger.Plan`; the existing body-emission loop (`:123-129`) and MethodDef-population loop (`:264-270`) already handle `slot.Synth`, so forwarders get bodies + rows with no further change. Because forwarders are reserved last, their `PredictedRow`s are `firstForwarderRow..`, exactly the tail the export TypeDef's `MethodList` points at.

- [ ] **Step 6: Run the test — expect PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Forwarder_calls_exported_function`
Expected: PASS — `N.S.add(2,3)` returns 5 (the forwarder runs `ldarg.0; ldarg.1; call add; ret`). If `AssertRow` fires for a forwarder, the reserve order and `Plan` order disagree — confirm forwarders are reserved after the entry row and nothing else reserves rows afterward.

- [ ] **Step 7: Confirm the empty-class case still holds**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter ExportClassTests`
Expected: all 3 PASS. `Empty_export_class_is_public_static` still passes because with only `main` (excluded), `ExportedMethods` is empty → `firstForwarderRow = Plan.Count+1` → empty MethodList.

- [ ] **Step 8: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/ExportClassTests.cs
git commit -m "feat(linker): raw-passthrough forwarders on the export class"
```

---

## Task 3: Promote referenced value-type structs to public

Pointers to named structs (`sqlite3*`) reference internal value-type TypeDefs, which external C# can't name. Promote the value-types referenced by exported signatures to `public`.

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs` (referenced-typedef-rows collector)
- Modify: `tools/chibil-link/PeWriter.cs` (apply `Public` in the value-type TypeDef loop)
- Modify: `tests/Chibil.Tests/CoreClr/ExportClassTests.cs`

- [ ] **Step 1: Write the failing test** (append to `ExportClassTests.cs`)

```csharp
    [Fact]
    public void Referenced_struct_is_public_for_export()
    {
        // psum takes `struct P*`; P's value-type TypeDef must be public so external
        // C# can reference the pointer parameter type.
        Assembly asm = LinkAndLoad(
            "struct P { int x; int y; }; " +
            "int psum(struct P *p){ return p->x + p->y; } " +
            "int main(void){ struct P q; q.x = 20; q.y = 35; return psum(&q); }",
            "N.S");
        Type t = asm.GetType("N.S");
        MethodInfo psum = t.GetMethod("psum", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(psum);
        Type paramType = psum.GetParameters()[0].ParameterType; // P*  (a pointer type)
        Assert.True(paramType.IsPointer, "expected a pointer parameter");
        Type pointee = paramType.GetElementType();             // P
        Assert.True(pointee.IsPublic, "the referenced struct must be promoted to public");
    }
```

- [ ] **Step 2: Run it — expect FAIL**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Referenced_struct_is_public_for_export`
Expected: FAIL — `pointee.IsPublic` is false (value-type TypeDefs are emitted non-public today).

- [ ] **Step 3: Collect value-type TypeDef rows referenced by exported signatures** (`MetadataMerger.cs`)

Add a collector that walks each exported method's signature and records the output rows of any value-type TypeDef it references. Mirror the existing `ScanSigTypeForTypeDefs` walk (`:495-563`) but collect mapped rows instead of ensuring copies:
```csharp
    /// <summary>Output rows of the value-type TypeDefs referenced (directly or via
    /// pointers/arrays) by any exported method's signature. PeWriter promotes these
    /// to public so external C# can name the pointer parameter types.</summary>
    public HashSet<int> BuildExportReferencedTypeRows()
    {
        var rows = new HashSet<int>();
        foreach (var (of, m) in ExportedMethods)
        {
            var def = of.Md.GetMethodDefinition(m.Handle);
            var reader = of.Md.GetBlobReader(def.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric) reader.ReadCompressedInteger();
            int paramCount = reader.ReadCompressedInteger();
            CollectSigTypeDefRows(of, ref reader, rows);                 // return type
            for (int p = 0; p < paramCount; p++)
            {
                if (reader.RemainingBytes > 0)
                {
                    byte peek = reader.ReadByte();
                    if (peek != (byte)SignatureTypeCode.Sentinel) reader.Offset -= 1;
                }
                CollectSigTypeDefRows(of, ref reader, rows);
            }
        }
        return rows;
    }

    private void CollectSigTypeDefRows(ObjectFile of, ref BlobReader reader, HashSet<int> rows)
    {
    again:
        var tc = reader.ReadSignatureTypeCode();
        switch (tc)
        {
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
            {
                EntityHandle modH = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, modH, rows);
                goto again;
            }
            case SignatureTypeCode.Pinned:
            case SignatureTypeCode.ByReference:
                goto again;
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.SZArray:
                CollectSigTypeDefRows(of, ref reader, rows);
                return;
            case SignatureTypeCode.Array:
            {
                CollectSigTypeDefRows(of, ref reader, rows);  // element type
                reader.ReadCompressedInteger();               // rank
                int bounds = reader.ReadCompressedInteger();
                for (int b = 0; b < bounds; b++) reader.ReadCompressedInteger();
                int los = reader.ReadCompressedInteger();
                for (int l = 0; l < los; l++) reader.ReadCompressedSignedInteger();
                return;
            }
            case SignatureTypeCode.GenericTypeInstance:
            {
                reader.ReadByte();
                EntityHandle genH = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, genH, rows);
                int args = reader.ReadCompressedInteger();
                for (int a = 0; a < args; a++) CollectSigTypeDefRows(of, ref reader, rows);
                return;
            }
            case SignatureTypeCode.TypeHandle:
            {
                reader.Offset -= 1; reader.ReadByte();
                EntityHandle th = reader.ReadTypeHandle();
                AddRowIfTypeDef(of, th, rows);
                return;
            }
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                reader.ReadCompressedInteger();
                return;
            case SignatureTypeCode.FunctionPointer:
            {
                var h = reader.ReadSignatureHeader();
                if (h.IsGeneric) reader.ReadCompressedInteger();
                int count = reader.ReadCompressedInteger();
                CollectSigTypeDefRows(of, ref reader, rows);  // return
                for (int p = 0; p < count; p++) CollectSigTypeDefRows(of, ref reader, rows);
                return;
            }
            default:
                return; // primitive
        }
    }

    private void AddRowIfTypeDef(ObjectFile of, EntityHandle h, HashSet<int> rows)
    {
        if (h.Kind != HandleKind.TypeDefinition) return;
        int row = MetadataTokens.GetRowNumber(_maps[of].MapTypeDef((TypeDefinitionHandle)h));
        if (row != 0) rows.Add(row);
    }
```
> This mirrors the existing `ScanSigTypeForTypeDefs`/`EnsureMethodSigTypeDefs` walkers in the same file — keep the two structurally identical so future signature-form fixes stay in sync. `_maps[of].MapTypeDef(handle)` returns the output `TypeDefinitionHandle` (row 0 if unmapped).

- [ ] **Step 4: Apply `Public` in the value-type TypeDef loop** (`PeWriter.cs`, `Write`)

Just before the value-type loop (`foreach (var ct in merger.CopiedTypeDefs)` at `:216`), compute the referenced set; inside the loop, OR in `Public` for referenced rows:
```csharp
        var exportPublicRows = merger.ExportTypeDefRow != 0
            ? merger.BuildExportReferencedTypeRows()
            : new System.Collections.Generic.HashSet<int>();
```
Change the `AddTypeDefinition` attributes argument in that loop from:
```csharp
                System.Reflection.TypeAttributes.SequentialLayout
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.AnsiClass,
```
to:
```csharp
                System.Reflection.TypeAttributes.SequentialLayout
                    | System.Reflection.TypeAttributes.Sealed
                    | System.Reflection.TypeAttributes.AnsiClass
                    | (exportPublicRows.Contains(ct.PredictedRow)
                        ? System.Reflection.TypeAttributes.Public
                        : 0),
```

- [ ] **Step 5: Run the test — expect PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Referenced_struct_is_public_for_export`
Expected: PASS — `P`'s value-type TypeDef is public. If the pointee is still non-public, confirm `BuildExportReferencedTypeRows` is reached and the row matches `ct.PredictedRow` (both are output rows).

- [ ] **Step 6: Confirm the full suite**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: all green (now 49 passed / 1 skipped: 46 baseline + 3 ExportClassTests... actually 48 + this one). Confirm 0 failures.

- [ ] **Step 7: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/ExportClassTests.cs
git commit -m "feat(linker): promote exported-signature value-types to public"
```

---

## Task 4: SQLite capstone — Roslyn-compiled C# consumer runs the CRUD

Proves the headline: a hand-written `unsafe` C# program, compiled against the chibil-generated `app.dll`, calls `Sqlite3.Native.sqlite3_*` to run the `:memory:` CRUD and exits 55 on Windows + Linux/WSL.

**Files:**
- Modify: `tests/Chibil.Tests/Chibil.Tests.csproj` (add Roslyn)
- Modify: `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs` (export-aware `BuildSqliteAppDll`)
- Create: `tests/Chibil.Tests/CoreClr/SqliteExportTests.cs`

- [ ] **Step 1: Add Roslyn to the test project** (`Chibil.Tests.csproj`)

In the `<PackageReference>` ItemGroup (next to xunit), add:
```xml
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*" />
```
Run `dotnet restore tests/Chibil.Tests/Chibil.Tests.csproj` and confirm it resolves.

- [ ] **Step 2: Make `BuildSqliteAppDll` export-aware** (`SqliteSmokeTests.cs`)

Change the existing `static byte[] BuildSqliteAppDll()` to accept an optional export class, threaded into the link, defaulting to the current behavior:
```csharp
    internal static byte[] BuildSqliteAppDll(string exportClass = null)
    {
        string sq = Path.Combine(RepoRoot(), "samples", "sqlite");
        string[] defs =
        {
            "SQLITE_OS_OTHER=1", "SQLITE_THREADSAFE=0", "SQLITE_TEMP_STORE=3",
            "SQLITE_ENABLE_MEMSYS5=1", "SQLITE_ZERO_MALLOC=1",
            "SQLITE_OMIT_LOADEXTENSION=1", "SQLITE_OMIT_AUTOINIT=1",
        };
        string[] incs = { Path.Combine(sq, "include"), Path.Combine(sq, "vendor") };
        var objs = new List<ObjectFile>();
        foreach (var src in new[]
        {
            Path.Combine(sq, "vendor", "sqlite3.c"),
            Path.Combine(sq, "sqlite_shim.c"),
            Path.Combine(sq, "main.c"),
        })
        {
            byte[] obj = TestCompiler.CompileFileToObj(src, Chibil.TargetProfile.CoreClr, defs, incs);
            objs.Add(ObjectFile.Load(obj, Path.GetFileName(src)));
        }
        return LinkPipeline.LinkToBytes(objs, new List<string>(), exportClass);
    }
```
> Make the method `internal static` (if not already) and `RepoRoot()` accessible to the new test class (it is `static` in the same namespace; if it is `private`, change to `internal static`). The existing `Memory_db_crud_returns_55_on_linux`/`_on_windows` callers pass no argument, so their behavior is unchanged.

- [ ] **Step 3: Write the capstone test** (`tests/Chibil.Tests/CoreClr/SqliteExportTests.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Chibil.Tests.CoreClr;

public class SqliteExportTests
{
    // Hand-written unsafe C# consumer of Sqlite3.Native doing the :memory: CRUD.
    // SQLITE_OK = 0, SQLITE_ROW = 100. sqlite3/sqlite3_stmt are the public-promoted
    // opaque structs; pointer params use the raw exported signatures.
    const string ConsumerSource = @"
using System;
using System.Text;
using Sqlite3;
public static class Program
{
    static byte[] Z(string s){ var b = Encoding.UTF8.GetBytes(s); Array.Resize(ref b, b.Length + 1); return b; }
    public static unsafe int Main()
    {
        Native.platform_init();
        byte[] path = Z("":memory:"");
        byte[] ddl  = Z(""CREATE TABLE t(a INTEGER);INSERT INTO t VALUES(20),(22),(13);"");
        byte[] sel  = Z(""SELECT sum(a) FROM t"");
        sqlite3* db = null;
        fixed (byte* p = path) if (Native.sqlite3_open(p, &db) != 0) return 101;
        fixed (byte* c = ddl)  if (Native.sqlite3_exec(db, c, null, null, null) != 0) return 102;
        sqlite3_stmt* st = null;
        fixed (byte* q = sel)  if (Native.sqlite3_prepare_v2(db, q, -1, &st, null) != 0) return 103;
        int sum = 0;
        if (Native.sqlite3_step(st) == 100) sum = Native.sqlite3_column_int(st, 0);
        Native.sqlite3_finalize(st);
        Native.sqlite3_close(db);
        return sum; // 55
    }
}";

    static byte[] CompileConsumer(byte[] appDll)
    {
        var consumerRef = MetadataReference.CreateFromImage(appDll);
        // Reference the running shared framework's assemblies (System.Private.CoreLib + the
        // System.Runtime facade) so the consumer binds to the same runtime as app.dll.
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        var fxRefs = tpa.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create(
            "consumer",
            new[] { CSharpSyntaxTree.ParseText(ConsumerSource) },
            fxRefs.Append(consumerRef),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));
        using var ms = new MemoryStream();
        EmitResult res = comp.Emit(ms);
        Assert.True(res.Success,
            "consumer compile failed:\n" + string.Join("\n", res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    // Run consumer.dll (which references app.dll) under the dotnet host; return exit code.
    static int RunConsumer(byte[] appDll, byte[] consumer, Func<string, int> hostRun)
    {
        // hostRun receives the temp dir containing app.dll + consumer.dll + runtimeconfig.
        string dir = Path.Combine(Path.GetTempPath(), "chibil_sp2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "app.dll"), appDll);
            File.WriteAllBytes(Path.Combine(dir, "consumer.dll"), consumer);
            File.WriteAllText(Path.Combine(dir, "consumer.runtimeconfig.json"), DotnetHostRunner.RuntimeConfigJson);
            return hostRun(dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Csharp_consumer_crud_returns_55_on_windows()
    {
        if (!DotnetHostRunner.DotnetAvailable()) return;
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        byte[] consumer = CompileConsumer(app);
        int exit = RunConsumer(app, consumer, dir =>
            DotnetHostRunner.RunDllInDir(Path.Combine(dir, "consumer.dll"), out _));
        Assert.True(exit == 55, $"windows consumer exit {exit}");
    }

    [Fact]
    public void Csharp_consumer_crud_returns_55_on_linux()
    {
        if (!WslRunner.Available()) return;
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        byte[] consumer = CompileConsumer(app);
        int exit = RunConsumer(app, consumer, dir =>
        {
            var (e, _) = WslRunner.RunDirEntry(dir, "consumer.dll");
            return e;
        });
        Assert.True(exit == 55, $"linux consumer exit {exit}");
    }

    [Fact]
    public void Native_surface_shape()
    {
        byte[] app = SqliteSmokeTests.BuildSqliteAppDll("Sqlite3.Native");
        Assembly asm = Assembly.Load(app);
        Type t = asm.GetType("Sqlite3.Native");
        Assert.NotNull(t);
        Assert.True(t.IsPublic && t.IsAbstract && t.IsSealed);
        foreach (var name in new[] { "sqlite3_open", "sqlite3_exec", "sqlite3_prepare_v2",
                                     "sqlite3_step", "sqlite3_column_int", "sqlite3_finalize", "sqlite3_close" })
            Assert.True(t.GetMethod(name, BindingFlags.Public | BindingFlags.Static) != null, $"missing {name}");
    }
}
```

- [ ] **Step 4: Add the two host helpers the test needs** (`DotnetHostRunner.cs`, `WslRunner.cs`)

The existing runners write a single `app.dll`; the consumer case needs to run a specific dll from a dir that already contains `app.dll`. Add minimal helpers and expose the runtime-config JSON.

In `DotnetHostRunner.cs` (it currently has `private const string RuntimeConfig`): expose it and add a run-a-dll-in-place helper:
```csharp
    public const string RuntimeConfigJson = RuntimeConfig; // expose for multi-assembly runs

    public static int RunDllInDir(string dllPath, out string stdout)
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        stdout = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new Exception("dotnet host timed out"); }
        if (err.Length > 0) stdout += "\n[stderr] " + err;
        return p.ExitCode;
    }
```
> If `RuntimeConfig` is declared with a value that can't be a `const` alias, instead make `RuntimeConfigJson` the primary `public const` and have the existing code reference it. Keep one source of truth.

In `WslRunner.cs`, add a helper that runs a named dll from a Windows dir already populated with both assemblies:
```csharp
    // Run `dotnet <dll>` in WSL against a Windows dir that already contains the dll
    // (and its sibling assemblies + runtimeconfig). Returns (exit, output).
    public static (int exit, string output) RunDirEntry(string winDir, string dllName)
    {
        string wslPath = "/mnt/" + char.ToLower(winDir[0]) + winDir[2..].Replace('\\', '/');
        using var p = Process.Start(new ProcessStartInfo("wsl",
            $"-u root -- bash -lc \"cd '{wslPath}' && dotnet {dllName}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, outp);
    }
```
> `Process` is `System.Diagnostics` (already imported in both files). Mirror the existing `WslRunner.Run` exit-code handling (read `wsl.exe`'s own exit code).

- [ ] **Step 5: Confirm the opaque-handle type names the consumer must use**

The consumer source names `sqlite3` and `sqlite3_stmt` (the pointee types of `sqlite3_open`'s out-param etc.). chibil's TypeDef name for these structs is an **empirical detail** — confirm it before relying on the consumer. Run only the shape test first and have it print the actual names:
- Temporarily add to `Native_surface_shape`: get `t.GetMethod("sqlite3_open")`, then `var pe = open.GetParameters()[1].ParameterType.GetElementType().GetElementType();` (`sqlite3**` → `sqlite3*` → `sqlite3`) and `Console.WriteLine(pe.FullName);` (also for `sqlite3_prepare_v2` param 3 → `sqlite3_stmt`). Run `dotnet test --filter Native_surface_shape -l "console;verbosity=detailed"` and read the printed full names.
- If they are not `sqlite3` / `sqlite3_stmt`, update the consumer's type names (and its `using`) to the actual emitted names. Then remove the temporary `Console.WriteLine`s.

- [ ] **Step 6: Run the capstone — expect 55 on both OSes**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter SqliteExportTests`
Expected: `Native_surface_shape` PASS; `Csharp_consumer_crud_returns_55_on_windows` PASS (exit 55); `Csharp_consumer_crud_returns_55_on_linux` PASS (exit 55, WSL is available on this machine). Failure triage:
- **Consumer fails to COMPILE** (Roslyn diagnostics in the assert message): an inaccessible type (a pointer param references a struct that wasn't promoted — verify Task 3 covers it), a struct-name mismatch (Step 5), or the `Sqlite3.Native` namespace/type split.
- **Compiles but exits non-55:** capture the host output — a non-zero `sqlite3_*` return (101/102/103) localizes the failing call; an AccessViolation points at a still-read-only global or a bad forwarder.

- [ ] **Step 7: Commit**

```bash
git add tests/Chibil.Tests/ tools/chibil-link/
git commit -m "test(sp2): C# consumer runs SQLite :memory: CRUD via Sqlite3.Native (Windows+WSL, exit 55)"
```

---

## Task 5: Regression gate + docs

**Files:**
- Modify: `samples/sqlite/README.md`
- Modify: `tools/chibil-link/` README/usage if present (the `--export-class` flag)

- [ ] **Step 1: Full MSVC suite** (codegen/`.obj` unchanged → IJW must be untouched)

Run: `run-tests.cmd x64`
Expected: 0 failures. Baseline was 170 passed / 3 skipped; this branch adds CoreCLR tests, so the total grows — confirm **0 failures**.

- [ ] **Step 2: Full CoreCLR suite incl. both SQLite consumer legs**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter CoreClr`
Expected: 0 failures; `SqliteExportTests` Windows + Linux consumer tests both green (exit 55).

- [ ] **Step 3: Update `samples/sqlite/README.md`**

Add a short "Consuming from C# (SP2)" subsection after the Status section, e.g.:
```markdown
## Consuming from C# (SP2)

Linking with `--export-class=Sqlite3.Native` emits a `public static class Sqlite3.Native`
whose static methods forward to the extern-linkage `sqlite3_*` functions (raw
signatures: `byte*`, `sqlite3*`, …; opaque handle structs are public). A hand-written
`unsafe` C# program, compiled against the generated `app.dll`, runs the same
`:memory:` CRUD and exits 55 on Windows and Linux/WSL
(`tests/Chibil.Tests/CoreClr/SqliteExportTests.cs`). IntPtr/string/`out` ergonomics
and the ADO.NET-style API are SP4.
```
Also update the "Limitations" line that says consuming from C# is incomplete, if present (the SP1 README's "the C# binding surface ... are the next sub-projects" — narrow it to SP4 ergonomics + SP3 persistence).

- [ ] **Step 4: Commit**

```bash
git add samples/sqlite/README.md tools/chibil-link/
git commit -m "docs(sqlite): C# can consume Sqlite3.Native (SP2)"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** §2 selection (UnmanagedExport) ↔ Task 2 Step 3; §2 raw passthrough ↔ Task 2 Step 4 (`RewriteMethodSignature` reuse); §3.1 option ↔ Task 1 Steps 3-4 + `ValidateExportClass`; §3.2 Native TypeDef ↔ Task 1 Step 7; §3.3 forwarders ↔ Task 2; §3.4 public promotion ↔ Task 3; §3.5 layout invariant ↔ Tasks 1-2 (`AssertRow` guard); §5 Roslyn proof ↔ Task 4; §6 non-regression ↔ Task 5.
- **Layout invariant is the crux:** Task 1 establishes Native at row 2 with an *empty* method range (de-risked in isolation); Task 2 turns the range into the forwarder tail. If `AssertRow` ever fires, the merger's reservation order and PeWriter's emission order disagree — that is the single most important thing to get right, and the assertions are the guardrail.
- **`main` is excluded** from export (Task 2 Step 3) so the surface doesn't carry a confusing `Native.main`; the entry point is unchanged.
- **Variadics** are exported with their hidden trailing va param (no skip logic, per spec §3.3) — harmless for the CRUD surface; the consumer never calls them.
- **Promotion scope** (Task 3) is targeted to value-types referenced by exported signatures via a walker that mirrors the existing `ScanSigTypeForTypeDefs` — keep the two structurally identical.
- **Roslyn framework refs** (Task 4) come from `TRUSTED_PLATFORM_ASSEMBLIES` so the consumer binds to the same runtime as `app.dll`; `MetadataReference.CreateFromImage` references the generated assembly from bytes.
- **No-regression bedrock:** linking without `--export-class` changes nothing (every export branch is gated on `_exportClass != null` / `ExportTypeDefRow != 0`); the MSVC/IJW suite is the gate (Task 5).
