# Compiling QuickJS to MSIL with chibil

Status of bringing up **QuickJS** (Bellard's JS engine, `quickjs-2025-09-13`) on
CoreCLR via chibil — a sibling experiment to the bash and MicroPython ports. This
documents the recipe, the chibil gaps it surfaced, and the current state.

**Headline result:** all **7** interpreter translation units compile to MSIL, link
into a single **1.77 MB** `qjs.dll`, and **QuickJS executes JavaScript** — arithmetic,
`Math`, arrays/`sort`, string methods, `JSON`, objects, and loops all run correctly.

The QuickJS tree under `targets/quickjs-2025-09-13/` is git-ignored (third-party; not
vendored). The source is **never edited** — every incompatibility is handled in chibil
or in chibil-side compat headers.

---

## 0. Build & run (Windows or Linux, no WSL required)

The managed-musl pipeline is now MSBuild-driven and cross-platform:

```
dotnet build build.proj -t:QuickJs
```

This builds the toolchain (`chibil`, `chibil-link`, `Chibil.Pal`, the build tasks),
compiles the managed-musl object set (`ManagedMusl.proj`), links `qjs.dll` against it
+ the managed PAL (`QuickJsManaged.proj`), and runs the C# consumer asserting
`JS_Eval("40+2") == 42`. All output lands under `./build/` (the .NET artifacts
layout). To rebuild just `qjs.dll` (then F5 the consumer in VS):
`dotnet build targets/quickjs/QuickJsManaged.proj`.

Regression suite (the 5 managed musl/PAL layer tests + the JS_Eval oracle):
`dotnet build build.proj -t:Test` (or just the layers:
`dotnet build targets/managed-musl/Layers.proj`).

The native-libc oracle (`targets/build/quickjs-api-run.sh`) and the historical
`*.sh` harnesses below remain for Linux/WSL; they now resolve the toolchain through
`targets/build/env.sh` under the same `./build/` layout.

---

## 1. Recipe

The `qjs` interpreter is `cutils.c dtoa.c libregexp.c libunicode.c quickjs.c
quickjs-libc.c qjs.c`, plus the generated `repl.c` (see §3). Harness:
`targets/build/quickjs-chibil.sh`, which first invokes `qjs-gen-repl.sh` to bootstrap
`repl.c`, then compiles every TU and links `qjs.dll`.

Per TU:
```
dotnet chibil.dll -c --target=coreclr -nostdinc -mlp64 \
  -include targets/build/quickjs-chibil-compat.h \
  -Itargets/build/qjs-compat -I. <musl includes> \
  -DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION="2025-09-13" \
  <src.c> -o <obj>
```

**`-DEMSCRIPTEN` is the key config choice.** QuickJS's EMSCRIPTEN build is its
"no-computed-goto, no-native-stack-pointer" configuration — exactly chibil's MSIL
constraints. It selects:
- **switch dispatch** instead of computed goto (`DIRECT_DISPATCH 0`) — chibil can't
  emit `goto *ptr`;
- **no `CONFIG_ATOMICS`** (no OS-thread atomics);
- **no `CONFIG_STACK_CHECK`** (no `__builtin_frame_address` — meaningless in MSIL);
- `malloc_usable_size` → `0`.

## 2. chibil gaps surfaced (and how each was handled)

| Gap | Status |
|---|---|
| **Forward-declared enum** `typedef enum OPCodeEnum OPCodeEnum;` before the definition (a GCC extension) | **Fixed in chibil**: an incomplete enum is int-sized and usable before completion; the definition completes the same tag (mirrors struct forward-declaration). Test `Forward_declared_enum_compiles`. |
| **Static-data pointers to libc functions** — `js_math_funcs[] = { fabs, floor, … }` (a table of ~25 function pointers) emitted unresolvable data relocations | **Fixed in chibil + chibil-link**: chibil emits a `MemberRef` (signature) for an address-taken external function in static data; chibil-link binds it to a P/Invoke stub (`PInvokeStubByName`) and `ldftn`s it into the slot in the `<Module>.cctor`. Test `Static_data_pointer_to_external_function_links`. |
| **Union compound-literal initializer** — `JS_MKPTR(tag,p) = (JSValue){ (JSValueUnion){.ptr=p}, tag }` truncated the 8-byte pointer to the union's 4-byte first member → bad pointer → `AccessViolation` in `JS_SetImmutablePrototype` | **Fixed in chibil**: a non-brace union initializer that is itself a union value is now a whole-union copy (mirrors struct). Test `Union_compound_literal_initializes_the_designated_member_fully`. |
| `__attribute((unused))` — GCC's alternate spelling of `__attribute__` | Compat shim: `#define __attribute __attribute__`. |
| `__builtin_ctzll` (and `clz`/`clzll`/`ctz`) | Compat shim fallbacks (TODO: native via `System.Numerics.BitOperations`). |
| `<stdatomic.h>` — musl ships none | Stub header in `targets/build/qjs-compat/` (non-atomic; the EMSCRIPTEN build is single-threaded). |

After these, **all 7 TUs compile**, **link** (`qjs.dll`, link exit 0), and **run JS**.
With the bootstrapped `repl.c` linked in, the binary's only native imports are 140 libc/libm
functions plus 4 genuine data globals (`environ`, `stdin`/`stdout`/`stderr`) — verifiable
with `chibil-link --print-imports`.

## 3. Current state — runs JavaScript, broadly conformant

`dotnet qjs.dll script.js` runs real JS programs across the modern language surface. A
feature probe (all passing):

```
exceptions / try-catch-finally, closures, classes + getters, inheritance + super,
generators (function*/yield), destructuring, spread, template literals,
RegExp (incl. named capture groups), Map, Set, TypedArray, BigInt, JSON, Symbol,
Proxy, Array reduce/sort, Promises (.then), async/await
```

So the 56k-line VM, the regexp/unicode/dtoa libraries, the GC, the bytecode
interpreter (switch dispatch), the full standard library, **and the job queue
(Promises / async-await)** all execute correctly on .NET via chibil. (`qjs.c` also
embeds a compiled-bytecode REPL — `repl.c`, defining `qjsc_repl` / `qjsc_repl_size`.
QuickJS generates `repl.c` with its native `qjsc` compiler, which we don't ship, so
`targets/build/qjs-gen-repl.sh` **bootstraps a `qjsc.dll` with chibil itself** and runs
it to emit `repl.c`; the build then links it so those two symbols are defined rather
than unresolved imports — confirm with `chibil-link --print-imports`. The generator is
idempotent, and both `quickjs-chibil.sh` and `quickjs-api.sh` invoke it automatically.)

## 3a. Conformance blocker — FIXED (small-storage bitfield read)

A conformance probe first hit `ReferenceError: x is not initialized` on top-level
`let`/`const`/`function`/`var x = init` — and it was layout-dependent (the same code
ran inside a larger script but threw alone). Instrumenting `JS_DefineGlobalVar` showed
`var a` being defined as a **global lexical** (`DEFINE_GLOBAL_LEX_VAR`) with
`JS_UNINITIALIZED`, so the init threw the TDZ check. That flag is set from
`JSGlobalVar.is_lexical` — a **1-bit `uint8_t` bitfield** — which `add_global_var`
clears over memory from `js_resize_array` (realloc, not zeroed).

Root cause (chibil, general): a bitfield **read** shift-extracts from the MSB of the
*loaded value*. `Load` widens a sub-word storage type to a 32-bit int (`u8`/`u16` ->
`i4`), but the read sized its shifts by the *storage* width (`Ty.Size*8` = 8), leaving
the storage byte's other bits (here realloc garbage) in the result — so `is_lexical`
read `1`. Fixed by sizing the shifts by the loaded container width (32 for `Size<=4`,
64 for `Size==8`); `int`/`long` bitfields were already correct. (`chibil/CodeGen.cs`,
the `NodeKind.Member` bitfield read; red/green test
`Small_storage_bitfield_read_masks_other_bits`.)

This single fix cleared the whole cascade — top-level bindings, and with them
exceptions/classes/generators/Promises/async, all came up at once.

## 4. test262 conformance

`run-test262.c` (the QuickJS conformance runner) compiles + links with chibil into
`run-test262.dll` (build: `targets/build/quickjs-test262.sh`). Against a fresh
`tc39/test262` clone (`run-test262 -c test262.conf -d test262/test/<area>`):

| Area | errors / tests |
|---|---|
| `language/types` | **0 / 113** |
| `language/statements` | **0 / 9113** |
| `language/expressions` | **0 / 10665** |
| `built-ins/Array` | **0 / 2900** |
| `built-ins/String` | **0 / 1198** |
| `built-ins/Object` | **0 / 3402** |
| `built-ins/RegExp` (prototype, named-groups) | **0 / 511** |
| `built-ins/Map` | **0 / 171** |
| `built-ins/Promise` (async / job queue) | **0 / 640** |
| **total** | **0 / ~29,400** |

**Zero chibil-caused failures** across ~29k tests spanning the core language, the
standard library, and the async/Promise job queue. (Per-area "skipped"/"excluded"
counts are tests tagged with unsupported-by-this-QuickJS features, e.g.
`stable-array-sort`, `await-dictionary`, skip-listed in `test262.conf` — not failures.)

The only failures observed anywhere were ~61 tests under
`built-ins/RegExp/property-escapes/generated/` (`\p{Script=…}`, `\p{ID_Continue}`,
"unknown unicode script") — a **Unicode-data-version skew**: QuickJS's bundled
`libunicode` tables predate the freshly-cloned test262's Unicode version, so they fail
on native QuickJS too, independent of chibil.

(One chibil gap surfaced building the runner: `run-test262.c` uses a **postfix**
`__attribute__` — `void f(...) __attribute__((format(...)))` — which chibil parses in
prefix but not postfix position. Stripped via the compat shim, behavior-equivalent;
TODO: parse postfix attributes natively.)

## 5. Reproduce

```
# clone (git-ignored): tar xf quickjs-2025-09-13-2.tar.xz into targets/
wsl bash targets/build/quickjs-chibil.sh        # compile 7 TUs + link qjs.dll
cd targets/quickjs-2025-09-13 && dotnet qjs.dll /tmp/script.js
# test262:
git clone --depth 1 https://github.com/tc39/test262 targets/quickjs-2025-09-13/test262
wsl bash targets/build/quickjs-test262.sh       # build run-test262.dll
wsl bash targets/build/qjs-t262-run.sh built-ins/Array
```

## 6. C# API facade — consuming QuickJS from .NET

Building with **`--export-api=quickjs.h`** makes chibil-link emit a C# binding facade
directly from the public header: a `quickjs` namespace containing a `quickjs.Api` class of
forwarder functions plus the public types/enums, with real types and **no reflection, no
hand-written P/Invoke** (the engine is already managed IL in the same assembly). From the
public header alone the facade exposes:

- **188 functions** as `static` forwarders on `quickjs.Api` (`JS_NewRuntime`, `JS_Eval`,
  `JS_ToInt32`, …);
- **21 public types** — `JSValue`, `JSValueUnion` (with named fields `int32`/`float64`/
  `ptr`), opaque handles `JSRuntime`/`JSContext`, `JSClassDef`, `JSPropertyDescriptor`, …;
- **3 enums** (`JSTypedArrayEnum`, `JSPromiseStateEnum`, `JSCFunctionEnum`).

A C# program references `qjs.dll` and **evaluates JavaScript**:

```csharp
using quickjs;
unsafe {
    JSRuntime* rt = Api.JS_NewRuntime();
    JSContext* ctx = Api.JS_NewContext(rt);
    fixed (byte* c = System.Text.Encoding.ASCII.GetBytes("40+2")) {
        JSValue v = Api.JS_Eval(ctx, (sbyte*)c, 4, (sbyte*)0, 0);
        int outv; Api.JS_ToInt32(ctx, &outv, v);   // 42
        int direct = v.u.int32;                    // 42 — nested struct field, read by name
    }
}
```

Reproduce (WSL):
```
wsl bash targets/build/quickjs-api.sh          # build qjs.dll with the facade
wsl bash targets/build/quickjs-api-surface.sh  # assert the facade surface  -> SURFACE_ORACLE_OK
wsl bash targets/build/quickjs-api-run.sh      # eval 40+2 through quickjs.Api -> 42 (BEHAVIORAL_ORACLE_OK)
```

The toy `mylib` fixture (`tests/Chibil.Tests/CoreClr/fixtures/mylib/`) exercises every
facet of the facade in fast CI tests; QuickJS is the real-world oracle.
