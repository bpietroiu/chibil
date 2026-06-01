# Managed SQLite via chibil — `Sqlite3.Native` export feature — design (Sub-Project 2)

**Date:** 2026-06-01
**Status:** Approved (design); ready for implementation planning
**Scope:** SP2 only — build the chibil **export feature** (extern-linkage C functions → public static methods on a named class) in the linker, and prove a hand-written **C#** program can consume a small `sqlite3_*` surface to run the same `:memory:` CRUD (exit 55), on Windows *and* Linux/WSL. No ergonomic API, no marshalling, no managed VFS (those are SP3/SP4).

## 1. Where this sits

```
┌ Ergonomic C# API (pure C#, ADO.NET-ish)              ┐  SP4
├ Sqlite3.Native — raw export surface (static class)   ┤  SP2  ← THIS
├ Managed platform provider (VFS + mem)                │  SP3
└ sqlite3 core — chibil-compiled amalgamation (IL)     ┘  SP1 (done)
```

SP1 (done, merged) compiles `sqlite3.c` to pure MSIL and runs a `:memory:` CRUD harness from a C `main`, exit 55, on Windows + Linux/WSL. SP2 makes that compiled SQLite **consumable from C#**: the linker emits a public static class whose methods forward to the chosen C functions, and a C# test calls them to do the same CRUD.

`Sqlite3.Native` is the **foundation** SP3/SP4 build on, so its design favors a faithful, robust, low-translation surface over per-call ergonomics.

## 2. Decisions (locked during brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| **Function selection** | Honor the `UnmanagedExport` marker chibil already sets on every non-static cdecl function (`CodeGen.cs:1069`). Export set = all extern-linkage functions ≈ the public `sqlite3_*` API. | Zero new config/codegen; the signal already exists (the linker currently strips it). The amalgamation makes internals `static`, so non-static ≈ public API. |
| **Pointer representation** | **Raw passthrough**: forwarders reuse the exact C signatures (`void*`, `sqlite3*`, `byte*`, `sqlite3**`, …). | Most robust (no unverifiable native-int↔pointer bridging across hundreds of auto-generated funcs; NativeAOT-friendly), most faithful (preserves pointee types), least linker work (copy the signature blob). IntPtr/string/`out` ergonomics belong in SP4, where the semantic knowledge (string vs handle vs out-param) actually exists. |
| **Consumption proof** | **Roslyn compile-and-run**: compile a small `unsafe` C# consumer against the runtime-generated `app.dll`, run it via the existing subprocess runners. | Real C#, real compiler, crash-isolated; establishes the exact pattern SP4 reuses. |
| **Type accessibility** | Promote value-type TypeDefs **referenced by an exported signature** to `public`. | Opaque handle structs (`sqlite3`, `sqlite3_stmt`) must be referenceable for external C# to form `sqlite3*`; a raw native layer exposing them is correct and keeps forwarders pure passthrough. |
| **Opt-in** | New linker option `--export-class=<Namespace.Name>` (default off → byte-for-byte current behavior). | The export feature is general; SQLite is one consumer. Existing tests/links are unaffected when the option is absent. |

## 3. The feature (linker-only)

chibil codegen and the `.obj` format are **unchanged**. Only final-PE emission gains the export class.

### 3.1 Option plumbing
- `LinkPipeline.LinkToBytes` gains an optional export-class name (via an added parameter or a small `LinkOptions`; default `null` = no export type). The CLI (`Program.cs`) maps `--export-class=<Namespace.Name>` to it. A malformed name (empty, or not a valid dotted identifier) throws a clear `LinkException`.

### 3.2 The exported type
When an export-class name is given, the linker synthesizes one `TypeDef`:
- Name/Namespace split from the option (`Sqlite3.Native` → ns `Sqlite3`, name `Native`; a name with no dot → empty namespace).
- Attributes: `Public | Abstract | Sealed | Class | BeforeFieldInit` (the CLR encoding of a C# `public static class`).
- `BaseType` = `System.Object` (a TypeRef into the core lib, deduped via the existing TypeRef machinery).
- Owns **no fields**; owns a contiguous block of forwarder methods (§3.3).

### 3.3 Forwarders
For each exported function (every method whose source `MethodDef` carries `UnmanagedExport`):
- **Name** = the C function name.
- **Signature** = a copy of the exported method's rewritten signature blob (same return + params, raw pointers). Reuse the merger's signature rewrite so referenced TypeDefs map to merged rows.
- **Attributes** = `Public | Static | HideBySig`.
- **Body** (synthesized IL): `ldarg.0; ldarg.1; … ldarg.(n-1); call <exported method final token>; ret`. `MaxStack = max(n, 1)`. `n` = parameter count of the exported signature. Uses `ldarg`/`ldarg.s`/long-form `ldarg` per index width (the existing long-form-for-≥256 rule applies; SQLite API arity is small, but emit correctly).
- Reuses the existing `SynthMethod` reservation + body-emission path (the same machinery as the `.cctor` and entry point), extended so a synth method can be owned by the export TypeDef rather than `<Module>`.

> **Variadic functions** (chibil lowers these with a hidden trailing va-buffer pointer param) are still exported, but their forwarder exposes that trailing param. They are documented as **not directly callable from C# in SP2** (the CRUD surface is non-variadic). Cleanly hiding/adapting variadics is SP4. No detection/skip logic — keeps the linker simple.

### 3.4 Type-accessibility promotion
Any value-type TypeDef (`CopiedTypeDef`) referenced by an exported forwarder's signature is emitted with `TypeAttributes.Public` (instead of the default non-public) so external C# can reference it (as an opaque blittable struct used only via pointer). Primitive pointees (`char*`→`sbyte*`, `int*`, `void*`) need no promotion. Determine the referenced set by scanning exported signatures for value-type TypeDef tokens (the merger already walks signatures for TypeDef discovery — reuse that walk).

### 3.5 Metadata layout — the load-bearing invariant
MethodDef ownership is by **contiguous row range**, and consecutive TypeDef rows must have **non-decreasing** `MethodList`. Therefore:

- **TypeDef order:** `<Module>` (row 1), **`Native` (row 2)**, then the value-type TypeDefs (rows 3..K). Inserting `Native` at row 2 shifts the value-type TypeDef rows by one — the merger predicts them accordingly.
- **MethodDef order:** all existing methods (real functions, P/Invoke stubs, `.cctor`, entry) occupy rows `1..R` (owned by `<Module>`, `MethodList = 1`); the **forwarders are the contiguous tail** rows `R+1..M` (owned by `Native`, `MethodList = R+1`). Value-type TypeDefs own empty ranges (`MethodList = M+1`).
- **Field layout** is unchanged; `Native` and the value-types own no fields, so their `FieldList` points past the end (as today).

The merger reserves the `Native` TypeDef row and the forwarder MethodDef rows during prediction; the writer's existing `AssertRow` checks fire if any prediction and emission disagree. This is the primary implementation risk, and the prediction/assert harness is exactly its guard.

## 4. Components

| File | Change |
|---|---|
| `tools/chibil-link/Program.cs` | Parse `--export-class=<Namespace.Name>`; pass to the pipeline. |
| `tools/chibil-link/PeWriter.cs` / `LinkPipeline` | Thread the export-class option; emit the `Native` TypeDef (row 2), the forwarder bodies, and the public-promotion of referenced value-types; extend `SynthMethod` ownership so forwarders hang off `Native`. |
| `tools/chibil-link/MetadataMerger.cs` | Predict the `Native` TypeDef row (row 2, shifting value-types) and the forwarder MethodDef tail rows; expose the exported-method list (those with `UnmanagedExport`) and the set of value-type TypeDefs referenced by their signatures. |
| `tools/chibil-link/EntrySynthesizer.cs` | Unaffected (entry stays a `<Module>` method in rows `1..R`). |
| `tests/Chibil.Tests/CoreClr/SqliteExportTests.cs` (new) | Roslyn-compile + run the C# consumer (§5); metadata-shape assertions. |
| `tests/Chibil.Tests/CoreClr/SqliteSmokeTests.cs` | Extend/relocate `BuildSqliteAppDll` to accept the export-class option (shared with the export test). |
| `tests/Chibil.Tests/Chibil.Tests.csproj` | Add `Microsoft.CodeAnalysis.CSharp` (Roslyn) for the compile-and-run test. |

## 5. The proof — Roslyn compile-and-run

`SqliteExportTests.cs`:
1. Link the 3 SQLite sources (sqlite3.c, sqlite_shim.c, main.c — or a dedicated `export_main` is unnecessary; `main` stays but is unused by the consumer) → `app.dll` with `--export-class=Sqlite3.Native`. The same `app.dll` keeps its C `main` entry; the consumer ignores it and calls `Sqlite3.Native` directly.
2. Compile an `unsafe` C# consumer in-memory with Roslyn (`CSharpCompilation`, `AllowUnsafe = true`, output a console exe), referencing:
   - `MetadataReference.CreateFromImage(appDllBytes)` (the generated SQLite assembly),
   - the runtime reference assemblies (resolve from `System.Private.CoreLib`/the shared framework, or `Basic.Reference.Assemblies` if simpler).
   The consumer's `Main` does, in `unsafe` C#:
   ```csharp
   // path/sqlCreate/sqlSelect are NUL-terminated UTF8 byte[] of ":memory:",
   // "CREATE TABLE t(a INTEGER);INSERT INTO t VALUES(20),(22),(13);",
   // and "SELECT sum(a) FROM t".
   Sqlite3.Native.platform_init();
   sqlite3* db;
   fixed (byte* p = path)      Sqlite3.Native.sqlite3_open(p, &db);
   fixed (byte* c = sqlCreate) Sqlite3.Native.sqlite3_exec(db, c, null, null, null);
   sqlite3_stmt* st;
   fixed (byte* q = sqlSelect) Sqlite3.Native.sqlite3_prepare_v2(db, q, -1, &st, null);
   int sum = 0;
   if (Sqlite3.Native.sqlite3_step(st) == 100 /*SQLITE_ROW*/) sum = Sqlite3.Native.sqlite3_column_int(st, 0);
   Sqlite3.Native.sqlite3_finalize(st);
   Sqlite3.Native.sqlite3_close(db);
   return sum; // 55
   ```
   (`sqlite3`/`sqlite3_stmt` are the public-promoted opaque structs; pointer params use the raw signatures. Exact opcodes/types are an implementation detail; the consumer is hand-written C#, not generated.)
3. Emit `consumer.dll` + `consumer.runtimeconfig.json` + the `app.dll` into a temp dir; run `dotnet consumer.dll` via `DotnetHostRunner.RunPeViaDotnetHost` (Windows) and `WslRunner.Run` (Linux). Assert exit **55**. Gate each on availability (`DotnetHostRunner.DotnetAvailable()` / `WslRunner.Available()`), matching the existing SQLite tests.
4. A cheaper **metadata-shape** test loads `app.dll` via `Assembly.Load`, asserts `Sqlite3.Native` exists, is `public`, `abstract`, `sealed`, and exposes the expected `sqlite3_*` public static methods — guarding the surface without the full compile.

## 6. Testing summary
- **Headline:** the Roslyn-built C# consumer exits 55 on Windows **and** Linux/WSL — proves end-to-end C# consumption of the raw export surface.
- **Surface shape:** metadata assertions on `Sqlite3.Native` (existence, accessibility, method set).
- **Non-regression:** full MSVC suite green (codegen/`.obj` unchanged → IJW untouched); existing CoreCLR + both SQLite smoke tests still pass; linking **without** `--export-class` produces a byte-for-byte-equivalent image to today (the export path is fully opt-in).

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Row-prediction layout (`Native` at row 2 + forwarder tail) desyncs from emission | Medium | `AssertRow` guardrail; the consumer-run + shape tests surface it immediately |
| Promoting a value-type TypeDef to public breaks load/verification | Low | Opaque structs; metadata-shape test + the CRUD run are oracles |
| External C# passing raw pointers trips runtime verification | Low | CoreCLR runs the `unsafe` consumer; subprocess isolates faults; the SQLite path is already proven |
| Roslyn referencing a runtime-generated PE / resolving framework refs | Low | `MetadataReference.CreateFromImage(byte[])` is supported; resolve framework refs from the running shared framework (or `Basic.Reference.Assemblies`) |
| Forwarding a function with a by-value struct param/return | Low | `ldarg`/`call` pass struct values natively; raw passthrough handles it without special cases |

## 8. Out of scope
- **SP4:** IntPtr/`nint` ergonomics, string↔UTF8 marshalling, `out`/`ref` params, `IDisposable`/SafeHandle wrappers, the ADO.NET-style API, C#→C callbacks (function pointers passed *into* sqlite3), variadic ergonomics, packaging as a NuGet library.
- **SP3:** managed VFS, `sqlite3_mem_methods`/`sqlite3_mutex_methods` managed providers, on-disk persistence.
- Performance tuning; exporting to multiple named classes; selective export subsets (the surface is all-extern-linkage).

## 9. Success criteria
1. The linker, given `--export-class=Sqlite3.Native`, emits a `public static class Sqlite3.Native` whose public static methods forward to the extern-linkage `sqlite3_*` functions, with referenced opaque structs public.
2. A hand-written `unsafe` C# program, compiled against the generated `app.dll`, runs the `:memory:` CRUD via `Sqlite3.Native` and exits **55** on Windows **and** Linux/WSL.
3. Linking without `--export-class` is unchanged; the full MSVC suite and existing CoreCLR/SQLite tests stay green.
