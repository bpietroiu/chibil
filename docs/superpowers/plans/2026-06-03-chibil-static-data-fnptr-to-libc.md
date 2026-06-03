# Static-Data Function Pointers to libc — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Link a static-data function pointer to an undefined external function (e.g. QuickJS's `js_math_funcs[]` table of `&fabs`) by binding it to a P/Invoke stub and `ldftn`-ing it into the data slot in the `<Module>.cctor`.

**Architecture:** chibil emits a `MemberRef` (carrying the signature) for an address-taken external function in static data; chibil-link's `SymbolResolver` already synthesizes a P/Invoke stub for every `MemberRef`, so expose a name→stub map and have `FieldDataRelocator` route the previously-erroring external-function data relocation to that stub. The existing `.cctor` machinery `ldftn`s a method relocation — no new codegen.

**Tech Stack:** C# / .NET, `System.Reflection.Metadata`, xUnit, `DotnetHostRunner`.

**Spec:** `docs/superpowers/specs/2026-06-03-chibil-static-data-fnptr-to-libc-design.md`

---

### Task 1: Red test — link a static external-function pointer

**Files:**
- Test: `tests/Chibil.Tests/CoreClr/MuslLinkTests.cs` (add one `[Fact]`)

Background: `RunViaHost(src, out output)` and `LinkRun(...)` link via chibil-link. For a deterministic, host-independent assertion use `LinkPipeline.LinkToBytes` directly (as `FlexibleArrayGlobalTests` does) and assert the PE is produced — the bug is a link-time throw.

- [ ] **Step 1: Add the failing test**

Add near the other link tests in `MuslLinkTests` (the class already has `using System.Collections.Generic;`, `ChibilLink`, `Xunit`):

```csharp
    [Fact]
    public void Static_data_pointer_to_external_function_links()
    {
        // A function pointer to an undefined external function (libc `abs`) baked into
        // STATIC DATA emits a COFF data relocation to `abs`. chibil-link errored
        // "external data relocation target 'abs' ... not defined in any object". It must
        // bind `abs` to a P/Invoke stub and ldftn it into the slot in the <Module>.cctor.
        // (Same shape as QuickJS's js_math_funcs[] table of &fabs, &floor, ...)
        const string src =
            "extern int abs(int);\n" +
            "static int (*fp)(int) = abs;\n" +    // function pointer in static data
            "int main(void){ return fp(-9); }\n";
        byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
        var of = ObjectFile.Load(obj, "fp.obj");
        byte[] pe = LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string> { "c" });
        Assert.NotEmpty(pe);
    }
```

- [ ] **Step 2: Run the test to verify it fails (RED)**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Static_data_pointer_to_external_function_links"
```
Expected: FAIL — `LinkException: ... external data relocation target 'abs' ... is not defined in any object`.

- [ ] **Step 3: Commit the red test**

```
git add tests/Chibil.Tests/CoreClr/MuslLinkTests.cs
git commit -m "Add red test: static-data pointer to external function must link

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Bind the external-function data relocation to a P/Invoke stub

**Files:**
- Modify: `tools/chibil-link/MetadataMerger.cs` (add `PInvokeStubByName`)
- Modify: `tools/chibil-link/SymbolResolver.cs` (populate it, ~line 131-137)
- Modify: `tools/chibil-link/FieldDataRelocator.cs` (error branch, ~line 170)
- Modify: `chibil/CodeGen.cs` (`EmitGlobalDataRelocations`, the undefined-external `else` branch ~line 3841)

Background:
- `FieldDataRelocator.Collect(MetadataMerger merger, ...)` already has `merger`. `Reloc`'s ctor is `Reloc(int ownerRow, int intra, bool isMethod, int token, long addend)`; `isMethod: true` makes the `.cctor` do `ldftn <token>; store at field+intra`.
- `SynthesizePInvoke` / `ReservePInvokeRow` return a method **token** (used by `map.RecordExternal`), so it drops straight into a `Reloc`.
- `RegisterExternalFunction(Obj fn)` emits a `<Module>` `MemberRef` named `fn.Name` with the signature from `fn.Ty`; it does **not** guard against duplicates, so guard the call with `_externalFuncRefs`.
- The program list `prog` is a parameter of `EmitGlobalDataRelocations`; an `extern int abs(int)` is present in it as a function `Obj` (`IsFunction`, `!IsDefinition`).

- [ ] **Step 1: Add the name→stub map to `MetadataMerger`**

Near the other public collections in `MetadataMerger` (e.g. just after `public readonly List<CopiedField> CopiedFields = new();`), add:

```csharp
    // Name -> synthesized P/Invoke stub method token, recorded by SymbolResolver. Lets
    // a static-data relocation (which carries only a symbol name) bind a function
    // pointer to the same stub a call site would use. First write wins (one libc
    // signature per name).
    public readonly Dictionary<string, int> PInvokeStubByName = new();
```

- [ ] **Step 2: Populate it in `SymbolResolver`**

In `SymbolResolver.Resolve`, where the stub token is obtained (the native-import block, ~line 131-137), record the name after the stub exists. Change:

```csharp
                if (!pinvokeByNameSig.TryGetValue(key, out int pinvokeToken))
                {
                    var sigReader = md.GetBlobReader(mr.Signature);
                    pinvokeToken = SynthesizePInvoke(merger, of, name, sigReader, libs, pinvokeMap, probe);
                    pinvokeByNameSig[key] = pinvokeToken;
                }
                map.RecordExternal(originalToken, pinvokeToken);
```

to:

```csharp
                if (!pinvokeByNameSig.TryGetValue(key, out int pinvokeToken))
                {
                    var sigReader = md.GetBlobReader(mr.Signature);
                    pinvokeToken = SynthesizePInvoke(merger, of, name, sigReader, libs, pinvokeMap, probe);
                    pinvokeByNameSig[key] = pinvokeToken;
                }
                merger.PInvokeStubByName.TryAdd(name, pinvokeToken);
                map.RecordExternal(originalToken, pinvokeToken);
```

- [ ] **Step 3: Resolve the external-function data relocation in `FieldDataRelocator`**

In `FieldDataRelocator.Collect`, the branch that throws (currently ~line 170). Change:

```csharp
                        throw new LinkException(
                            $"{of.Path}: external data relocation target '{nm}' " +
                            $"in {sec.Name}+0x{r.VirtualAddress:X} is not defined in any object.");
```

to:

```csharp
                        // A function pointer to an undefined external FUNCTION baked into
                        // static data (e.g. QuickJS's js_math_funcs[] = { fabs, ... }):
                        // bind it to the same P/Invoke stub a call site would use and
                        // ldftn it into the slot via the .cctor (isMethod: true). Falls
                        // through to the error only for a genuinely unbound symbol.
                        if (merger.PInvokeStubByName.TryGetValue(nm, out int stubTok))
                        {
                            relocs.Add(new Reloc(owner.PredictedRow, intra, true, stubTok, 0));
                            continue;
                        }
                        throw new LinkException(
                            $"{of.Path}: external data relocation target '{nm}' " +
                            $"in {sec.Name}+0x{r.VirtualAddress:X} is not defined in any object.");
```

- [ ] **Step 4: Emit a `MemberRef` for an address-taken external function in chibil**

In `EmitGlobalDataRelocations`, the undefined-external `else` branch (~line 3841). Change:

```csharp
                else
                {
                    // Unknown target — create as undefined external
                    targetSym = _symtab.AddExternalDataSymbol(
                        SymPrefix + targetName, LogicalSection.Data, 0);
                }
```

to:

```csharp
                else
                {
                    // Unknown target — create as undefined external data symbol.
                    targetSym = _symtab.AddExternalDataSymbol(
                        SymPrefix + targetName, LogicalSection.Data, 0);
                    // If it is actually an undefined external FUNCTION whose address is
                    // baked into static data (a function-pointer table, e.g. QuickJS's
                    // js_math_funcs[]), also emit a MemberRef carrying its signature so
                    // chibil-link can bind a P/Invoke stub and ldftn it into the slot.
                    if (_options.Target == TargetProfile.CoreClr
                        && !_externalFuncRefs.ContainsKey(targetName))
                    {
                        for (Obj f = prog; f != null; f = f.Next)
                        {
                            if (f.IsFunction && !f.IsDefinition && f.Name == targetName
                                && f.Ty.CallConv != CallConv.Clrcall)
                            {
                                RegisterExternalFunction(f);
                                break;
                            }
                        }
                    }
                }
```

- [ ] **Step 5: Build chibil and chibil-link**

Run:
```
dotnet build chibil -c Debug && dotnet build tools/chibil-link -c Debug
```
Expected: `0 Error(s)` for both.

- [ ] **Step 6: Run the Task 1 test — now GREEN**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Static_data_pointer_to_external_function_links"
```
Expected: PASS (the PE links; `abs` resolves to a P/Invoke stub `ldftn`'d in the `.cctor`).

- [ ] **Step 7: Run the full CoreClr suite — no regressions**

Run:
```
dotnet test tests/Chibil.Tests --filter "FullyQualifiedName~Chibil.Tests.CoreClr"
```
Expected: all pass (the prior 139 + the 1 new = 140 passing, 1 skipped). In particular the defined-function data-relocation and data-to-data tests (`FlexibleArrayGlobalTests`, `LinkerOutputTests`, `MuslLinkTests`) stay green.

- [ ] **Step 8: Commit the fix**

```
git add tools/chibil-link/MetadataMerger.cs tools/chibil-link/SymbolResolver.cs tools/chibil-link/FieldDataRelocator.cs chibil/CodeGen.cs
git commit -m "Bind static-data pointers to external functions via P/Invoke stubs

A function pointer to an undefined external function baked into static data (e.g.
QuickJS's js_math_funcs[] = { fabs, ... }) emitted a data relocation chibil-link
could not resolve. chibil now emits a MemberRef (signature) for such an
address-taken external; chibil-link records each synthesized P/Invoke stub by name
(PInvokeStubByName) and FieldDataRelocator routes the data relocation to it, so the
<Module>.cctor ldftn's the stub into the slot. The function pointer is then callable
via calli, reaching the native function.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Runtime + QuickJS integration, regression, docs

**Files:**
- Modify: `CompileQuickJS.md` (create — short port doc, mirrors CompileMicroPython.md)

- [ ] **Step 1: Runtime correctness (WSL)**

Verify the fix runs end-to-end on Linux (where libc P/Invoke binds), not just links. Create `/tmp/fp.c` and run via WSL/PowerShell:
```
wsl bash -c "printf 'extern int abs(int);\nstatic int (*fp)(int)=abs;\nint main(void){return fp(-9);}\n' > /tmp/fp.c && cd /tmp && dotnet /mnt/d/sandbox/chibil/chibil/bin/Debug/net10.0/chibil.dll -c --target=coreclr fp.c -o fp.obj && dotnet /mnt/d/sandbox/chibil/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll -o fp.dll -lc fp.obj && dotnet fp.dll; echo exit=\$?"
```
Expected: `exit=9` (the `.cctor` `ldftn`'d the `abs` stub; `calli` reached native `abs`).

- [ ] **Step 2: Re-attempt the QuickJS link**

Run:
```
wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-chibil.sh
```
Expected: `compiled OK: 7 FAILED: 0`, then the link proceeds past the `js_math_funcs` `fabs` error. One of:
- `link exit: 0` with `qjs.dll` produced — the math table resolved; or
- a **different** link/relocation error (a further QuickJS blocker) — record it; the `fabs` data-relocation error specifically must be gone.

- [ ] **Step 3: If qjs.dll links, run a script**

If `qjs.dll` was produced, run a trivial script (no REPL bytecode needed):
```
wsl bash -c "cd /mnt/d/sandbox/chibil/targets/quickjs-2025-09-13 && echo 'print(1+2)' > /tmp/t.js && timeout 30 dotnet qjs.dll /tmp/t.js 2>&1 | head -20"
```
Expected: either `3` printed, or a runtime blocker to record. (qjs.c may require the compiled `repl.c` bytecode to start; if it fails before running the script, note that the link milestone is the deliverable and the REPL-bytecode/qjsc step is the next item.)

- [ ] **Step 4: Full suite in an MSVC dev shell (bash regression guard)**

Create `D:\sandbox\chibil\runtests.bat` (untracked — delete after):
```
@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
dotnet test D:\sandbox\chibil\tests\Chibil.Tests
```
Run via PowerShell: `& cmd.exe /c "D:\sandbox\chibil\runtests.bat"`.
Expected: only the known pre-existing `Data_MsvcDefine_ChibiConsume` environmental failure — no NEW failures. Then delete `runtests.bat`.

- [ ] **Step 5: Write `CompileQuickJS.md`**

Create a short port doc (mirroring `CompileMicroPython.md`'s style): the recipe (EMSCRIPTEN config, musl headers, the `quickjs-chibil-compat.h` + `<stdatomic.h>` stub), the chibil gaps fixed (forward-declared enums; static-data function pointers to libc), the `__attribute`/`__builtin_ctzll` shims, the current state (all 7 TUs compile; link result from Step 2), and the next blocker (whatever Step 2/3 surfaced, e.g. the `repl.c` bytecode for the interactive `qjs`, or a runtime issue).

- [ ] **Step 6: Commit the doc**

```
git add CompileQuickJS.md
git commit -m "Document QuickJS port: compiles, math-table function pointers resolved

Records the QuickJS bring-up: all 7 interpreter TUs compile under the EMSCRIPTEN
config; forward-declared enums and static-data libc function pointers fixed in
chibil/chibil-link; the js_math_funcs[] link blocker is resolved. Notes the current
link/run state and the next blocker.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the executor

- Do NOT edit anything under `targets/quickjs-2025-09-13/` (third-party source; gitignored). Compat lives in `targets/build/quickjs-chibil-compat.h` and `targets/build/qjs-compat/`.
- Commit messages end with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.
- This work continues on branch `chibil-quickjs-port`.
- The `cl.exe`/`link.exe` failures in a plain shell are environmental — run the full suite inside an MSVC dev shell.
- WSL invocations go through PowerShell (`wsl bash ...`), not Git-Bash (which mangles `/mnt/...` paths).
