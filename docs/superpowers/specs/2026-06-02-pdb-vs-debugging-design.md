# PDB generation + Visual Studio consume/debug for chibil assemblies — Design

**Goal:** Make chibil-compiled C usable and debuggable from Visual Studio as a
managed assembly — both (A) stepping through the `.c` source of a whole compiled
program (e.g. bash) and (B) referencing a C-library assembly from a **C# project**
and stepping from C# *into* the C source.

**Scope:** the full ladder (tiers 4–5): Portable-PDB source-line mapping, named
local variables + scopes, a nameable C#-callable public surface, clean stepping
over synthesized code, and source-embedded single-file artifacts.

**Architecture (three pillars):**
1. **Central PDB re-emission** — chibil emits a per-TU Portable PDB; chibil-link
   *re-emits* one unified PDB by placing each method's debug rows at its final
   `MethodDef` RID (reusing the token map it already computes), rather than
   *merging/predicting* PDB rows.
2. **Linker-synthesized façade** — a public, nameable static class forwarding to
   the `<Module>` functions, so C# can call them at all.
3. **Embedded PDB + embedded source** — one DLL that "just works" in VS with no
   sidecar `.pdb` and no `.c` files on disk.

---

## 0. Implementation finding (2026-06-02): the front-end is already done

Investigation of the codebase changed the plan materially:

- **chibil already tracks and emits complete per-TU debug info** — as native
  **CodeView**, *unconditionally* (not gated on `-g`). `coffobjectemitter.cs` has a
  full CodeView system: `CodeViewLineNumberBuilder` (IL-offset → line),
  `CodeViewManSlot` (named managed locals with type tokens), `CodeViewLocalScope`,
  and SHA-256 file checksums. `CodeGen.GenExpr` calls `_enc.MarkLineNumber(_cvFile,
  node.Tok.LineNo)` for every expression.
- **chibil-link drops all of it.** It reads no `.debug$S`, emits no
  debug-directory, and produces no PDB. (This is why the bash debugging this session
  used perfmap + gdb — the native path — instead of source-level managed stepping.)

**Consequence:** the design's "chibil emits a per-TU debug side-stream" front-end
work is *already present* (as CodeView). The feature collapses to a **chibil-link
responsibility**: consume the per-TU debug data, transcode it to a unified Portable
PDB (placing each method at its final `MethodDef` RID via the existing token map),
and emit the PDB + the PE debug-directory entry.

**Intermediate choice (revised):** rather than parse CodeView bytes in the linker,
chibil emits the same already-computed line/local/scope data into a small,
purpose-built `.chibildbg` section (trivial for the linker to read), and chibil-link
builds the Portable PDB centrally. This keeps both sides simple and avoids a
CodeView round-trip. CodeView emission stays for native consumers; `.chibildbg` is
the managed-PDB path.

---

## 1. Why this is non-trivial here

Two chibil-specific facts shape everything:

- **chibil-link is a row-predicting merger.** It combines N COFF objects into one
  PE by predicting metadata rows and remapping every token. A PDB's tables
  (`Document`, `MethodDebugInformation`, `LocalScope`, `LocalVariable`) are keyed
  by the *same* method/document tokens being remapped, so debug info cannot be a
  bolt-on — it must move in lockstep with the merge.
- **C functions live on `<Module>`.** chibil emits each function as a static on the
  module's global type. **C# cannot name `<Module>`**, so a C# project cannot call
  those methods in idiomatic code at all — consumption (B) is *blocked*, not merely
  unergonomic, until a nameable surface exists.

Method *names* are already present (we read `<Module>.find_pipeline` off crash
stacks during the fork work), so the readability floor is already met without any
PDB. The missing pieces are **source lines**, **locals**, and **a C# surface**.

---

## 2. Decision 1 — PDB production: re-emit centrally, don't merge

### The key insight

A Portable PDB's `MethodDebugInformation` table is **1:1 with `MethodDef`**: row *R*
describes method *R*. chibil-link already knows, for every method, its **final**
`MethodDef` RID. So instead of *merging* PDB rows (prediction, the scary path), the
linker **builds a PDB of exactly the final method count and fills each row from the
source TU's debug info** — a deterministic placement, not a prediction.

### Intermediate format

The intermediate is itself a **per-TU Portable PDB blob**, emitted by chibil into a
`.chibilpdb` COFF section (chibil already uses `System.Reflection.Metadata` to write
the object's IL+metadata; the same library writes the PDB blob). This avoids
inventing a custom format — the linker parses it with `MetadataReader` and writes
the unified one with `MetadataBuilder`.

Each per-TU PDB carries, keyed by the TU-local method RID:
- **Documents** — source path, hash algorithm + hash, language GUID (C), and
  (optionally) the deflated source bytes for embedding.
- **Sequence points** — `(IL offset → document, startLine, startCol, endLine,
  endCol)` or the *hidden* marker (`0xFEEFEE`).
- **Local scopes** — a nested tree of `(IL range, locals, child scopes)`; each local
  is `(slot, name, flags)`.

### Linker re-emission algorithm

After prediction (the token map is complete):
1. **Merge documents.** Union all TUs' documents, dedup by `(path, hash)`; build the
   final `Document` table and a per-TU `localDocRow → finalDocRow` map.
2. **Size the tables.** `MethodDebugInformation` table = total method count.
3. **Place each method.** For each object, for each method with debug info: look up
   its **final** `MethodDef` RID via the existing token map; emit its sequence-point
   blob at that row, with document references rewritten through the document map.
4. **Locals.** Append `LocalScope`/`LocalVariable` rows ordered by final method RID
   (the PDB requires scopes grouped by method) — straightforward because step 3
   already visits methods; emit scopes in the same pass.
5. **Synthesized methods** (P/Invoke stubs, variadic adapters, longjmp carrier,
   façade forwarders, entry shim) get a single *hidden* sequence point and the
   compiler-generated marker so the debugger steps over them (see §5).
6. **Stamp the handshake.** Generate the PDB ID; write the PE's CodeView
   debug-directory entry and (for embedded) the embedded-PDB entry to match.

**Why this is low-risk:** no row *prediction* for the PDB — the linker fills a table
it sizes exactly, using a map it already owns. New code is isolated to a re-emission
pass; it does not perturb the PE merge.

---

## 3. Decision 2 — The C# consumption façade

### Export selection

The façade exposes only **API functions**, not all ~3900 methods. Selection by
**export linkage**: a chibil `__declspec(dllexport)`-style attribute (or a `--export`
list / a marked header). File-local `static` functions are never exposed.

### Surface shape

chibil-link synthesizes a public type — `namespace <module>; public static class
Native` — whose methods **forward** to the `<Module>` functions. The linker already
synthesizes method bodies (stubs, adapters, the carrier), so forwarders fit its
repertoire: `ldarg…; call <Module>::fn; ret`.

### Signature-translation rules (C → C#-visible)

| C type | Façade surface | Notes |
|---|---|---|
| `int`/`long`/`short`/`char` (+ unsigned) | `Int32`/`Int64`/`Int16`/`SByte`/`Byte`… | LP64: `long`/`size_t` → `Int64`/`UIntPtr` |
| `T*` (T a value type) | `unsafe T*` | consumer must be `unsafe`, or use an `IntPtr` overload |
| `void*` / opaque `T*` | `void*` / `IntPtr` | |
| `const char*` / `char*` | `byte*` (faithful) | optional friendly `string` overload (pin + convert) in a *separate* layer |
| `struct S` by value/ref | the IL value type | **requires S to be a public nameable type too** — see below |
| function pointer | `delegate*`/fnptr | `unsafe` |
| variadic (`...`) | **excluded** from the friendly façade | expose the Layer-1 `__va` form only via a low-level variant |

### Two layers, ranked by importance

- **High (the unblock): a faithful blittable façade** — primitives + pointers as
  `unsafe`. This is "callable + nameable," which is the whole point of (B).
- **Lower (polish): a friendly layer** — `string`/array marshalling, `IntPtr`
  overloads, XML docs/IntelliSense. This is a binding-generator concern; keep it
  decoupled so it never blocks the core.

### Open sub-decision: struct-type nameability

For C# to pass a `struct S` by value, `S` must also be public and nameable, but
chibil emits struct value-types referenced from `<Module>` too. **MVP:** expose
functions whose signatures are primitives + pointers (the bulk of a real C API);
surfacing public struct *types* (and thus by-value struct params) is a follow-on.

---

## 4. Decision 3 — Packaging

**Default: embedded PDB + embedded source.** One DLL; VS finds the PDB
automatically; you reference it, set a breakpoint, step into C, and the source
appears with **no `.pdb` and no `.c` files** present. Matches the single-file ethos.
*Cost:* a larger DLL, and it embeds third-party source (fine for bash's GPL; note it
for proprietary inputs).

**Opt-out: sidecar `.pdb` + SourceLink** for size-sensitive builds, when the source
lives in a SourceLink-tagged repo.

**Determinism** (reproducible MVID/PDB-ID) is a nice-to-have layered on top, not a
prerequisite for the debug experience.

---

## 5. Supporting work

- **Locals + scopes.** chibil already maps C locals → IL slots and knows block
  scopes (chibicc has them). Emit names + nested scopes; hide chibil's synthesized
  temporaries (or name them generically). *High value, medium effort.*
- **Synthesized-method hiding.** Stubs/adapters/carrier/forwarders/entry shim get a
  hidden sequence point + compiler-generated marker so single-stepping flows over
  them. *Without this, stepping derails.*
- **The setjmp/longjmp wrinkle.** Our whole-function `try/filter/handler` wrap
  reorders the body's IL (`Lhead: nop; .try { body } …`). Sequence points must map
  the wrapped body back to its original lines, and the wrapper scaffolding (the
  `leave`-funneled returns, the filter/handler) must be marked hidden. This is *our*
  codegen cleverness fighting debuggability — budget for it explicitly.

---

## 6. Milestones (de-risked: prove the scary parts early)

1. **Single-TU PDB + line mapping + ID → step one `.c` in VS.** Proves the whole
   managed-debug-of-C story end-to-end with **zero merge risk**.
2. **Multi-TU central re-emit (Decision 1).** Proves the hard part.
3. **Locals + scopes.**
4. **Hiding synthesized methods + setjmp-wrap line hygiene.** Makes stepping correct.
5. **Façade (Decision 2)** — *parallel track from day one*; unblocks (B) on its own,
   before any PDB exists.
6. **Embedded PDB + embedded source.**

Treat 1–4 as "debuggable", 5–6 as "consumable & distributable".

---

## 7. Risks & open questions

- **Risk — central re-emit correctness.** The placement-by-final-RID assumes the
  token map is total and exact for every method, including synthesized ones (which
  have no source → hidden row). Prototype milestone 1→2 before committing.
- **Risk — setjmp-wrap line hygiene** is the most underestimated item; naïve
  sequence points over the wrapped body produce a janky step experience.
- **Open — struct-type public surface.** How much of the C type system becomes
  public C#-visible (by-value struct params, exposing struct types) vs. staying
  pointer-only at the façade.
- **Open — variadics in the façade.** Exclude, or expose a low-level `__va` variant?
- **Open — friendly layer ownership.** Linker-synthesized vs. a separate
  binding-generator pass vs. hand-written partials.
- **Open — packaging default.** Embedded-everything (best UX, bigger DLL) vs. sidecar
  (conventional) as the out-of-the-box default.

**Net architecture:** central PDB **re-emission** off the existing token map +
linker **façade** keyed by export linkage + **embedded** PDB/source, sequenced
single-TU-first, with the façade as a parallel early track.
