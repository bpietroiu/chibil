# Chibil self-contained Linux builds — design

**Date:** 2026-05-31
**Status:** Approved (design); ready for implementation planning
**Scope of this spec:** Phase 0 + Phase 1 only (first runnable C program on Linux, end-to-end, with no Windows tools). Phase 2 is a roadmap, not part of this spec.

## 1. Goal

Make Linux a first-class target for chibil: a `.c` source file compiles, links, and runs on Linux with **zero Windows tooling** (no `link.exe`, no MSVC, no Windows import libs). The produced artifact is a pure-MSIL .NET assembly that loads and runs under `dotnet`/CoreCLR on Linux.

This is the most ambitious of the possible end states (compile-only, run-output, CI-only were the alternatives). It necessarily replaces the two Windows dependencies in today's pipeline:

1. **`link.exe`** — the final link step (`Driver.RunLinker` hardcodes `link.exe` + `mscoree.lib`).
2. **`/clr` mixed-mode IJW output** — CoreCLR will not load mixed-mode/IJW assemblies on Linux, so output must be **pure MSIL** (`ILOnly`).

## 2. Key decisions (locked during brainstorming)

| Decision | Choice | Rationale |
|---|---|---|
| How to combine translation units without `link.exe` | **In-house managed-COFF linker** (`chibil-link`) | Matches the README's stated "own linker" goal; preserves separate compilation; reuses existing `coffobjectemitter`/`coffobjdumper` machinery. |
| First runnable milestone surface | **Real C program calling libc via P/Invoke** | User's target. (MVP uses a *non-variadic* libc call; see §6.) |
| How an `extern` is identified as a native import | **Link-time auto-resolve** | CodeGen emits every extern as an undefined symbol; the linker treats whatever is still unresolved after merge as a native import and generates P/Invoke against `-l` libraries. Mirrors Lcc.NET's `illink`; keeps C source unchanged; centralizes the managed/unmanaged decision in the linker. |
| Linker metadata-merge engine | **Approach A: `System.Reflection.Metadata` read → remap → write** | Reuses `asm2obj/MetadataCopier` (cross-image metadata copy + token remap) and `coffobjdumper` parsing; stays within the project's existing `System.Reflection.Metadata` backbone. |
| Linker location | **Separate project** `tools/chibil-link` | Separation of concerns; shares parsing code with `chibil`. |
| Verification environment | **WSL on the dev machine** | Local end-to-end `dotnet app.dll` checks during development. |

**Non-regression principle:** the existing Windows/MSVC `/clr` IJW path is unchanged. Everything here is a parallel **CoreCLR target** selected on Linux (or via `--target=coreclr`). CoreCLR mode is strictly additive.

## 3. Architecture & data flow

```
a.c ─┐
b.c ─┤ chibil --target=coreclr -c   →   a.obj, b.obj   (pure-MSIL managed COFF)
     ┘
a.obj b.obj ─►  chibil-link -lc -o app.dll
                  ├─ parse objects (sections, symbols, relocs, metadata)
                  ├─ merge metadata  (per-object token remap)      ← MetadataCopier reuse
                  ├─ resolve symbols: defined→MethodDef/Field; unresolved→pinvokeimpl(libc.so.6)
                  ├─ fix up CLR-token relocs in IL + data
                  ├─ synthesize entry (argv→C main→exit code) + module .cctor
                  └─ ManagedPEBuilder → app.dll  (ILOnly) + app.runtimeconfig.json
                                                  │
                                          WSL:  dotnet app.dll ; echo $?
```

### Components

| Component | New? | Role |
|---|---|---|
| Pure-MSIL emit mode in `CodeGen.cs` | extend | On CoreCLR target: emit plain managed static methods + value-type structs (IL largely unchanged), but **drop** IJW machinery — no NEP/`__unep@` thunks, no `.nep` section, no `__CxxPureMSILEntry` shim. Address-of-function → `ldftn`; indirect calls stay `calli`. CorFlags = `ILOnly`. Core-lib refs retarget to CoreCLR's core assembly. Still emits a managed COFF `.obj`. |
| `chibil-link` | **new** `tools/chibil-link` | Reads N managed-COFF `.obj`, merges metadata, resolves symbols, synthesizes P/Invoke, fixes relocations, emits pure-MSIL PE + `runtimeconfig.json`. |
| `Driver.cs` | extend | On CoreCLR target, route link to `chibil-link` instead of `link.exe`; write `runtimeconfig.json`; pass `-l` libs through. |
| Build tooling | new | `build.sh` for the WSL dev loop; pure-MSIL auto-selected on non-Windows. |

The linker — not the compiler — owns the managed/unmanaged decision.

## 4. Pure-MSIL emit mode (CodeGen)

Selected by a target flag (`--target=coreclr`, implied on non-Windows). Differences from the MSVC/IJW path:

- No NEP thunks, no `__unep@` fields, no `.nep` section, no `__CxxPureMSILEntry` IJW shim.
- C functions are plain managed static methods on the module/global type.
- External references emitted as undefined COFF symbols (unchanged) — resolved by the linker.
- Address-of-function uses `ldftn` (native int managed method pointer); indirect calls stay `calli`.
- CorFlags = `ILOnly` (not `32BITREQUIRED`, not native-entry).
- Core-library `AssemblyRef` retargeted to CoreCLR's core assembly (see §7, open detail).

The bulk of `CodeGen` (statement/expression IL, value-type structs, pointer arithmetic, `.data`/`.rdata`/`.bss`) is already pure CIL and is reused unchanged.

## 5. `chibil-link` pipeline

Straight-line pipeline; each stage is a focused, independently testable class:

1. **Load** (`ObjectFileReader`) — parse each `.obj`: COFF header, sections (`.text$mn` IL, `.data`, `.rdata`, `.bss` size, embedded ECMA metadata, optional `.debug$S`), COFF symbol table, relocations (esp. CLR-token relocs). Reuses `coffobjdumper.cs` parsing.
2. **Symbol table** (`LinkSymbolTable`) — gather defined symbols (functions, globals) and undefined symbols across all objects; fold COMDAT duplicates; flag genuine multiple-definition errors.
3. **Metadata merge** (`MetadataMerger`) — read each object's tables via `MetadataReader`; copy rows into one shared `MetadataBuilder` under a per-object **token remap**; dedup `AssemblyRef`/`TypeRef`/`MemberRef`/string/blob heaps. Generalizes `asm2obj/MetadataCopier`.
4. **Symbol resolution** (`SymbolResolver`):
   - *Defined* externs → rewritten to the merged `MethodDef`/`Field` token.
   - *Unresolved* externs → synthesize a `pinvokeimpl` `MethodDef`: a `ModuleRef` for the native lib (from `-l`; `-lc` → `libc.so.6`), an `ImplMap` row (`CallConvCdecl`, exact entry name, no mangling), signature copied from the call-site `MemberRef`. Symbol maps to that MethodDef.
5. **Fixup** (`RelocationFixer`) — for every CLR-token reloc in each merged IL body (and data reloc for string-literal RVAs / address-of-global initializers), write the final resolved/remapped 4-byte token.
6. **Entry + module ctor** (`EntrySynthesizer`):
   - Entry: synthesized managed `int __entry(string[])` that marshals `argv` and calls C `main`, returning its exit code; set as the assembly entry point. Models the existing `mainCRTStartup`/`__CxxPureMSILEntry` contract, emitted directly as IL (no `asm2obj` round-trip for the MVP).
   - `.cctor`: emits calls to each TU's dynamic initializers (`??__E…`) in a deterministic order — the "sane module constructor" `TODO.md` wants; provides cross-TU static-init ordering.
7. **Emit** (`PeWriter`) — lay out section/IL/field-RVA data; CorFlags = `ILOnly`; emit PE via `ManagedPEBuilder` → `app.dll`; write `app.runtimeconfig.json` (`Microsoft.NETCore.App`).

## 6. Phasing

The full goal is too large for one spec. **This spec = Phase 0 + Phase 1.**

### Phase 0 — de-risk smoke (no libc, no P/Invoke)
Pure-MSIL emit + single-object link of a freestanding program:
```c
int fib(int n){ return n<2 ? n : fib(n-1)+fib(n-2); }
int main(){ return fib(10); }     // expect exit code 55
```
`dotnet app.dll; echo $?` prints `55` in WSL. Proves the riskiest thesis cheaply: pure-MSIL output loads and runs on CoreCLR/Linux, the PE layout + synthesized entry work, and the core-lib reference resolves — before any P/Invoke or multi-object complexity.

### Phase 1 — MVP (real libc via auto-resolved P/Invoke)
A program split across **two objects** (to exercise cross-object symbol resolution + metadata merge) calling a **non-variadic** libc function:
```c
// greet.c
int puts(const char*);
void greet(void){ puts("hello from chibil on linux"); }
// main.c
void greet(void);
int main(){ greet(); return 0; }
```
`chibil-link main.obj greet.obj -lc -o app.dll` → unresolved `puts` becomes `pinvokeimpl(libc.so.6)`; `greet` resolves cross-object. `dotnet app.dll` prints the line, exits 0.

### Phase 2 — roadmap (OUT of this spec)
Robust N-object merge/dedup at scale; `printf`/varargs P/Invoke; static-init ordering hardening; DOOM-on-Linux (needs an SDL/X11 PAL or the headless checksum harness); `ubuntu-latest` CI gate; managed↔unmanaged callback thunks. Each is its own future spec.

## 7. Open technical details (resolve during implementation)

- **Core-lib reference.** Emitted refs to `System.ValueType` etc. must resolve on CoreCLR. Settle `mscorlib`-facade vs. direct `System.Private.CoreLib` vs. `System.Runtime` empirically in WSL during Phase 0. Small knob, real choice.
- **Varargs P/Invoke.** `printf` is variadic; cdecl-varargs P/Invoke on CoreCLR is awkward (`__arglist`/`calli`-vararg). The MVP deliberately uses a non-variadic libc call; `printf`/varargs is Phase 2.

## 8. Testing strategy

- **Unit** — per-stage linker tests: metadata-merge round-trips, symbol resolution, P/Invoke synthesis, reloc fixup. Reuse `ObjDumper`-style normalized dumps to assert merged metadata is well-formed.
- **Integration (WSL)** — golden tests: compile→link→run sample C, assert **stdout + exit code** (Phase 0 fib=55; Phase 1 the greet line). Mirrors existing scenario discipline.
- **Regression** — existing Windows scenario suite and `link.exe` path stay green; CoreCLR mode is strictly additive.

## 9. Explicitly out of scope (YAGNI for this spec)

`printf`/varargs, DOOM-on-Linux, static-init *hardening* (we still synthesize the `.cctor`, just don't stress it), CI, managed↔unmanaged callback thunks, and any change to the MSVC/IJW path's behavior.
