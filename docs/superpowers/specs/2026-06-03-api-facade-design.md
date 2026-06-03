# C# API facade from a public header — design

## Goal

Make a chibil-built library **directly consumable from C#**, with the library's
own public header as the source of truth for the export surface. Today everything
lands in the global `<Module>` type and consumers need reflection (README, "Consuming
C code from .NET code"). This feature generates, from a designated public header, a
clean namespaced facade — a `static class Api` of callable functions plus real public
struct/enum/handle types — so a C# program writes `mylib.Api.fn(...)` with real types,
no reflection and no P/Invoke (the implementation is already managed IL in the same
assembly).

## Core idea: the public header is the manifest

A C library has private sources (`src/`) and a public header (`include/lib.h`) that
defines its public surface. Anything declared in that header is public; everything
else stays private in `<Module>`. The header — not the translation unit — is the unit
we build a facade around. One public header → one facade group (namespace + `Api`
class). This sidesteps the "which TU owns a struct" problem entirely: a struct is
public iff it is declared in the public header.

## Scope

In scope (the "callable surface + enums"):
- **Functions** declared in the public header → `static` forwarder methods on `Api`.
- **Opaque handles** (forward-declared structs used only by pointer, e.g. `JSContext`)
  → opaque public handle types.
- **Public structs/unions** declared in the header → real public value types,
  deduplicated to one canonical type and given the facade namespace.
- **Enums** declared in the header → synthesized `enum : <underlying>` types, with
  enumerators as named constants, **threaded into function signatures and struct
  fields** (see Enums).

Out of scope (deferred):
- Object-like `#define` macro constants (preprocessor-only; needs macro-value capture).
- Self-contained/single-file packaging; this is purely a metadata-shaping feature.
- Subsuming the existing `--export-class` into this mechanism (it coexists for now).

## Naming and layout

- The facade **namespace** derives from the public header's path **relative to its
  public include root** (the `-I` directory that exposes it) — matching how C libraries
  publish headers. `include/mylib.h` (root `include/`) → namespace `mylib`;
  `include/net/http.h` → `net.http`. The `.h` is dropped, path separators become dots,
  and each segment is sanitized to a valid identifier (`quickjs-libc.h` → `quickjs_libc`).
- **Functions** live in a `public static class Api` in that namespace.
- **Types** (structs/unions, opaque handles, enums) are **siblings** at namespace
  level: `mylib.MyStruct`, `mylib.Color`.
- Consumer surface: `var x = mylib.Api.fn(...);  mylib.MyStruct s = ...;`.

## Architecture

Two stages — chibil parses (it already has a full C parser); chibil-link assembles
(never duplicate the parser into the linker).

### Stage 1 — chibil (compile time)

New repeatable flag `--export-api=<header>` names public header(s) (and carries/infers
the include root for namespace derivation). While compiling each TU, every top-level
declaration whose source location is one of those headers is tagged "public API".
chibil writes a new COFF section **`.chiapi`** per object: a flat record list —

- `func <name>` — a public function (its real MethodDef already lands in `<Module>`);
- `struct|union <name>` / `opaque <name>` — a public type (an existing TypeDef);
- `enum <name> : <underlying> { ident=value, … }` — enumerators + underlying type;
- per public function parameter/return and per public struct field that was
  enum-typed: the enum identity (so the linker can substitute `int`→enum);
- the header's path and include root (for namespace derivation) and a group key.

Independent of `-g` — this is API surface, not debug info.

### Stage 2 — chibil-link (link time)

Aggregates `.chiapi` across all objects, dedups by (group, name), and synthesizes the
facade per header group:

- **Functions** → `static class Api` of forwarder methods (`ldarg…; call <module fn>;
  ret`), reusing the real signature — the existing `ForwarderSynthesizer` path, grouped
  per header instead of one global class.
- **Public types** → see Public types (re-namespace the already-canonical TypeDef).
- **Enums** → synthesize `enum : <underlying>` TypeDefs; thread into signatures/fields.
- **Namespace** → from the header path relative to the include root.

Built only when `.chiapi` manifests are present; coexists with `--export-class`.
Assemblies built without `--export-api` are byte-for-byte unchanged.

## Public types — re-namespace the already-canonical TypeDef

The linker **already** canonicalizes value-type TypeDefs across TUs, so there is no
dedup to build here. Every object that includes a header carries a complete copy of
that header's structs, but `MetadataMerger` dedups them by `(namespace, name, size)`
into **one** output TypeDef row, mapping every object's handle to that single row
(`MetadataMerger.cs:185`; genuine get-or-add at `:2061-2064`). This is not new work —
it is the pre-existing mechanism that lets multi-TU programs pass structs **by value**
across TUs at all (it is exactly why QuickJS can return `JSValue` by value and thread a
context struct from one TU to another without `InvalidProgramException`, despite value
types being nominal in IL). So there is already exactly one canonical `JSValue`, and
both `<Module>` methods and the `Api` forwarders reference it through the token map.

The facade's type work therefore reduces to a metadata edit on **one existing row**: for
each public type name, take the canonical `CopiedTypeDef` and set its `Namespace` to the
facade namespace and its visibility to `public` (a new `IsPublic` flag on
`CopiedTypeDef` that `PeWriter` honors). References are by token and unchanged, so there
is no value-type mismatch to avoid. The `(ns,name,size)` key already keeps genuinely
different layouts apart, so a same-named/different-size type stays a distinct type and is
surfaced as a warning rather than wrongly merged.

Opaque handles (forward-declared, no definition) → an empty `public` value-type TypeDef
in the namespace — the linker already synthesizes empty opaque-handle TypeDefs for the
export surface (`MetadataMerger.cs:383`), which this reuses.

## Enums

C lowers `enum` to `int`, so public function signatures already carry `int`. But an
enum's **underlying type is its representation**: a synthesized `enum Color : int` is
**stack-interchangeable with `int`** in IL (passing an `int` where the enum is expected,
or vice-versa, is a no-op — exactly why `(int)e` / `(Color)i` emit no conversion in C#).

Therefore:
- chibil records each public enum's underlying type + enumerators, and tags each public
  function parameter/return and public struct field that was enum-typed with the enum's
  identity.
- chibil-link synthesizes `enum <Name> : <underlying>` (`value__` field + a literal
  const per enumerator) in the namespace.
- The `Api` forwarder signatures **use the enum type** where the C decl did; the body
  still calls the `int`-typed `<Module>` method, relying on enum↔underlying
  interchangeability (`ldarg` enum → `call`(int32) → `ret` enum).
- **Public struct fields** of enum type are retyped to the enum the same way, so a
  field reads as `mylib.Color`, consistent with the function surface.

This interchangeability is the one novel IL claim and gets a dedicated keystone test.

## Error handling (degrade, never fail the link)

- Public function declared in the header but **not defined** in the linked objects →
  warn, skip that forwarder.
- Public type name with **no matching TypeDef** → emit an opaque handle type.
- Name sanitization for namespace segments and type names.
- No explicit include root → default to the directory the `--export-api` header sits in.
- Same-named public type, conflicting layout → warning, keep first.
- Facade built only when manifests are present; no `--export-api` → unchanged output.

## Components

| Unit | Side | Responsibility |
|---|---|---|
| API-tagging pass | chibil | Mark declarations originating from `--export-api` headers as public; record enum usage on params/fields |
| `.chiapi` emitter | chibil | Serialize the public-API records into a COFF section |
| `.chiapi` parser | chibil-link (`ObjectFile`) | Read the manifest records from each object |
| `ApiManifest` aggregator | chibil-link | Aggregate + dedup records across objects by (group, name); detect layout conflicts |
| Public-type publicizer | chibil-link (`MetadataMerger`) | Re-namespace + mark `public` the already-canonical `CopiedTypeDef` for each public type name (cross-TU dedup is pre-existing) |
| Enum synthesizer | chibil-link | Emit `enum : underlying` TypeDefs; substitute `int`→enum in public signatures/fields |
| `ApiFacadeSynthesizer` | chibil-link | Build the `Api` class + forwarders per group (extends `ForwarderSynthesizer`) |
| Wiring | chibil-link (`LinkPipeline`/`PeWriter`) | Reserve rows, emit the facade TypeDefs/methods, write namespaces |

## Testing — two tiers

### Tier 1 — `mylib` (controlled, grown, in-repo, CI)

A fixture library in the conventional layout under
`tests/Chibil.Tests/CoreClr/fixtures/mylib/`:

```
mylib/
  include/mylib.h     ← public surface (the --export-api header)
  src/*.c             ← private implementation + internal-only structs/statics
```

`mylib.h` carries one of every aspect we convert; `src/` also defines deliberately
**private** symbols (a `static` helper, an internal struct not in the header) that must
**not** leak into the facade. The fixture grows facet-by-facet alongside the
implementation — it is the spec's executable definition and the TDD ratchet:

1. one public function → `mylib.Api.fn` (+ assert a `static` src helper stays hidden)
2. a public struct param/return → deduped, public `mylib.MyStruct`, usable from C#
3. an opaque handle → handle type
4. an enum (with underlying type) → `mylib.Color : int`, named constants
5. enum threaded into a function signature and a struct field
6. a second public header → second facade group / namespace
7. edge cases: declared-but-undefined fn (warn+skip), private struct/static absent

Test layers over `mylib` (reusing `TestCompiler.CompileToObj` — extended with an
overload that passes extra chibil args — and `LinkPipeline.LinkToBytes`):
- **Manifest emission**: read the object's `.chiapi`, assert the expected records.
- **Aggregation + re-namespace**: assert a public struct resolves to the one
  pre-existing canonical TypeDef, now in the facade namespace and `public` (and that a
  same-named/different-size type stays distinct).
- **Metadata shape** (`System.Reflection.Metadata`): namespace, `Api` class + forwarder
  signatures, single canonical public structs, opaque handles, synthesized enums,
  enum-in-signature/field, **and absence** of private symbols.
- **Keystone consumption** (in-process `Assembly.Load` + reflection-invoke): call
  `mylib.Api.fn(...)` with a constructed public-struct value and enum value; assert the
  returned struct/enum. Proves forwarders resolve, struct dedup yields a real usable
  type, and the **enum-in-signature IL JITs and runs**. Runs anywhere `dotnet` does.
- **Enum interchangeability micro-test**: an isolated forwarder whose signature uses the
  synthesized enum, forwarding to the `int`-typed `<Module>` method — pinpoints the one
  novel IL claim.
- **Opt-out regression**: linking the same `src/` without `--export-api` yields no
  `.chiapi` and no facade types.

### Tier 2 — QuickJS (oracle + repro, WSL/heavier)

The real `quickjs.h` against the real engine, playing two oracle roles `mylib` can't:

- **Surface oracle**: chibil already parses `quickjs.h`, so enumerate its real public
  prototypes, structs (`JSValue`, `JSValueUnion`), opaque handles (`JSRuntime`,
  `JSContext`), and enums, then assert the `quickjs.Api` facade exposes them — coverage
  cross-checked against the header. Catches anything the converter silently drops at real
  scale.
- **Behavioral oracle / repro**: a C# consumer drives the facade end-to-end —
  `var rt = quickjs.Api.JS_NewRuntime(); … quickjs.Api.JS_Eval(ctx, "40+2") …` — and
  checks the resulting `quickjs.JSValue` decodes to `42` (the known-correct answer is the
  oracle). Reproduces the README's "consume C from .NET" goal through real
  structs/handles/enums; a mis-deduped `JSValue`, botched enum tag, or wrong handle type
  surfaces as a wrong or crashing result. WSL-gated integration acceptance gate, reusing
  the existing git-ignored `targets/` QuickJS build.

So `mylib` proves each facet in isolation and fast; **QuickJS is the oracle** that proves
the whole thing holds on a real library's public surface and produces correct results.

## Open risks

- **Public-type sharing** relies on the linker's pre-existing TypeDef dedup
  (`MetadataMerger.cs:185`), already proven correct by every multi-TU program (QuickJS
  included); this feature only re-namespaces the canonical row, so it adds little risk
  here. Genuine layout mismatches (different size) stay distinct via the dedup key and
  are warned. The keystone + QuickJS behavioral oracle remain the end-to-end guard.
- **Enum↔underlying interchangeability** in IL is asserted, not assumed — the
  micro-test fails loudly if any edge needs an explicit (no-op) `conv`.
- **`.chiapi` format** is an internal contract between chibil and chibil-link; versioned
  with a small header so a version skew is detected rather than mis-parsed.
- **Source-location attribution** (is a decl "from" the public header?) depends on
  chibil's existing file/line tracking through the preprocessor; verified by the
  private-symbol-absent assertions in `mylib`.
