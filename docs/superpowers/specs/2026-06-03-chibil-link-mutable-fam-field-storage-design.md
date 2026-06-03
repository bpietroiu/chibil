# chibil-link: size Mutable FAM field storage to its data extent

**Status:** design approved (pending spec review)
**Date:** 2026-06-03
**Component:** `tools/chibil-link/MetadataMerger.cs`

## Problem

MicroPython's `find_qstr` faults at runtime during `mp_init`. Root cause (probed
from `main` before `mp_init`):

- `mp_qstr_const_pool` reads correctly (`total_prev_len=183`, `len=31`).
- `mp_qstr_const_pool_static`, which `const_pool.prev` points to, sits at
  `const_pool + 0x68` — **inside** `const_pool`'s flexible-array region
  (`0x28 + 31*8 = 0x120` bytes). It reads garbage; `find_qstr` walks a bad
  pointer and faults.

The qstr pools are `const` but carry pointer relocations (`prev`, `lengths`,
`qstrs[]`), so chibil-link emits them as **Mutable** fields: plain CLR static
fields of the struct type, initialised by the `<Module>.cctor` doing a `cpblk`
from a synthesized read-only `<name>$init` source field.

The FieldRVA **extent fix** (commit `2aa6bbc`) correctly sizes the `$init` data to
the data extent (`0x120`). But the **target Mutable field's storage is the
struct's fixed `ClassLayout` (`0x28`)** — the CLR allocates only `0x28` bytes for
it. The `.cctor` copies `0x120` bytes into that `0x28` slot, overflowing `0xF8`
bytes into the adjacent static field. This corrupts `static_pool` → the crash.

The extent fix is correct for **ReadOnly** FAM globals (inline FieldRVA data, no
separate storage) but never widened the **Mutable** target field's storage to
match the bytes copied into it.

## Fix (Approach: sized storage value-type)

In `MetadataMerger.CopyDataFieldsAndTypeDefs`, where `typeSize` (struct
`ClassLayout`) and `size` (data extent) are already computed: when
`kind == Mutable && size > typeSize`, give the field a synthesized `valuetype`
whose `ClassLayout == size` instead of the struct signature. The `.cctor` copy
then lands in a field whose storage exactly matches the `size` bytes copied.

Scope is deliberately **Mutable** fields only:
- **ReadOnly** FAM (inline FieldRVA data) already works — unchanged.
- **BSS** keeps `typeSize` (`size == typeSize`) — unchanged.
- The `size > typeSize` guard catches both flexible-array members **and** trailing
  alignment padding (both make the extent exceed the struct size, both would
  overflow the `.cctor` copy).

### New helper: `GetOrAddSizedValueType(int size)`

```csharp
private readonly Dictionary<int, BlobHandle> _sizedStorageSig = new();

/// Field signature `valuetype <T>` where T is a synthesized value type with
/// ClassLayout == size. Used to give a Mutable field whose initialized data
/// extent exceeds its declared struct size (flexible array member / trailing
/// padding) storage that matches the bytes the .cctor copies into it.
private BlobHandle GetOrAddSizedStorageFieldSig(int size)
{
    if (_sizedStorageSig.TryGetValue(size, out var cached)) return cached;

    _outTypeDefRow++;
    var td = new CopiedTypeDef
    {
        Name = $"$FieldStorage${size}",
        Namespace = "",
        BaseType = GetOrAddCoreValueTypeRef(),
        LayoutSize = size,
        LayoutPack = 1,
        PredictedRow = _outTypeDefRow,
    };
    CopiedTypeDefs.Add(td);

    var b = new BlobBuilder();
    new BlobEncoder(b).FieldSignature()
        .Type(MetadataTokens.TypeDefinitionHandle(td.PredictedRow), isValueType: true);
    var sig = Builder.GetOrAddBlob(b);
    _sizedStorageSig[size] = sig;
    return sig;
}
```

- Cache keyed by `size` so N pools of the same extent share one storage type.
- `_outTypeDefRow++` already happens inside this same pass (via
  `EnsureFieldTypeDefs`), so minting one more TypeDef here keeps
  predicted == emitted. PeWriter's existing `CopiedTypeDefs` loop emits it with
  `AddTypeLayout` → a value type of exactly `size` bytes.

### Call site

In the field-copy loop, after `kind` is determined and before
`CopiedFields.Add`:

```csharp
BlobHandle sigBlob = Builder.GetOrAddBlob(sigB);   // rewritten struct signature
if (kind == CopiedField.FieldKind.Mutable && size > typeSize)
    sigBlob = GetOrAddSizedStorageFieldSig(size);
```

and `SignatureBlob = sigBlob` in the `CopiedField`. The `$init` source field
reuses the same (now correctly-sized) signature — its declared type becomes
honest with no behavior change (its data is inline regardless).

## Test (metadata assertion — deterministic)

New `[Fact]` in `tests/Chibil.Tests/CoreClr/FlexibleArrayGlobalTests.cs`:

```c
struct b { const void *t; };
typedef struct { struct b base; long len; const void *items[]; } tup_t;
extern const int sentinel;
tup_t g = { {0}, 4, { &sentinel, &sentinel, &sentinel, &sentinel } };
const int sentinel = 42;
int main(void){ return g.items[3] != &sentinel; }
```

A **non-const** global deterministically forces the `.data`/Mutable path (the
const-vs-`.rdata` placement is exactly the ambiguity to keep out of the test).
Extent = `0x10` (base+len) + `4*8` = **0x30**; struct `ClassLayout` = **0x10**.

The test:
1. `CompileToObj` + `LinkPipeline.LinkToBytes` (with `"c"`).
2. Reopen the PE with `PEReader` / `GetMetadataReader`.
3. Find the `FieldDefinition` named exactly `"g"` (not `"g$init"`).
4. Decode its signature: `ReadSignatureHeader()`, `ReadSignatureTypeCode()`
   (expect `ValueType`), `ReadTypeHandle()` → `TypeDefinitionHandle`.
5. `GetTypeDefinition(h).GetLayout().Size`, assert **`>= 0x30`**.

- **Before fix:** field type = the struct → layout `0x10` → RED.
- **After fix:** field type = `$FieldStorage$48` → layout `0x30` → GREEN.

The existing `Flexible_array_member_global_is_sized_by_its_data_extent`
(link-success) test stays. MicroPython's `find_qstr` running to completion is the
integration proof (manual, not CI).

## Risk / blast radius

- Only fields with `Kind == Mutable && size > typeSize` are rerouted; ReadOnly,
  BSS, and exactly-sized Mutable fields are byte-for-byte unchanged.
- The field is accessed via `ldsflda` + pointer cast in IL, so the storage type's
  identity is irrelevant — only its size matters. Changing the type to a sized
  blob is safe.
- The 130 CoreClr tests guard the unchanged paths.

## Out of scope

- Dropping the Mutable copy in favor of patching inline ReadOnly data (needs
  runtime-writable FieldRVA; the Mutable mechanism exists because that's
  unreliable cross-platform).
- Fixing the field type in chibil codegen upstream (cross-cutting; the linker
  still needs extent-sizing).
