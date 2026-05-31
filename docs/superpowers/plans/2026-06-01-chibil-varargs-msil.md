# Varargs on the MSIL Target — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make chibil compile C variadic functions on its CoreCLR/MSIL target — define/consume/forward chibil-internal varargs (Layer 1, va-buffer) and call native variadic functions like `printf` (Layer 2, monomorphized cdecl) — portably on Windows and Linux.

**Architecture:** Two portable mechanisms, no CLR vararg calling convention (proven broken on CoreCLR Linux). **Layer 1:** a variadic definition gains a hidden trailing `void* __va` parameter; the caller packs promoted varargs into a `localloc` buffer of 8-byte slots and passes the pointer; `va_list` is that pointer; `va_start/va_arg/va_end/va_copy` are pointer ops. **Layer 2:** at a `__cdecl`-variadic call site, emit a concrete promoted signature; `chibil-link` turns it into a monomorphized `pinvokeimpl` keyed by name+signature.

**Tech Stack:** C# / .NET 10; `chibil` compiler (`Parser.cs`, `CodeGen.cs`, `TypeSystem.cs`, `ChibiTypes.cs`); `tools/chibil-link`; `System.Reflection.Metadata`; the CoreCLR in-process test harness (`TestCompiler`, `LinkPipeline`, `ObjectFile`, `DotnetHostRunner`); WSL + `dotnet` (cross-platform runs).

**Reference docs:** Spec at `docs/superpowers/specs/2026-06-01-chibil-varargs-msil-design.md` (read first). Branch: `varargs-msil` (off `linux-selfcontained-builds`).

**Key existing code (verified):**
- `Parser.cs:1153-1160` — the `__builtin_*` dispatch block in `Primary()` (extend here).
- `Parser.cs:1841-1843` — the variadic-def **error** + the vestigial `__va_area__` (replace).
- `CodeGen.cs:2199-2274` — `GenFunCall` (call emission; Layer-1 packing + Layer-2 sig go here).
- `CodeGen.cs:1027` `RegisterFunction` / `:1139` `RegisterExternalFunction` (def signatures; add hidden param / concrete sig).
- Helpers: `GenAddr(Node)` (`:1569`, lvalue address), `GenExpr(Node)` (`:1897`, rvalue), `Store(CType)` (`:1726`, `stind`), `_enc.LoadArgument(int)`, `Push()`/`Pop(n)`, `ILOpCode.Localloc`. `NodeKind` enum at `ChibiTypes.cs:28`.
- `TypeSystem.cs:10-42` — built-in `CType`s (add `TyVaList`).

**Test harness note:** CoreCLR in-process tests live in `tests/Chibil.Tests/CoreClr/`. `TestCompiler.CompileToObj(string src, TargetProfile.CoreClr)` compiles one source; `ObjectFile.Load` + `LinkPipeline.LinkToBytes` link; load via `Assembly.Load(pe)` + invoke `EntryPoint`, or run through `DotnetHostRunner.RunPeViaDotnetHost(pe, out output)`. A `RunOnLinux` helper (Task A0) runs the same `.dll` under WSL.

---

## Task A0: Cross-platform test helper

**Files:** Create `tests/Chibil.Tests/CoreClr/WslRunner.cs`

- [ ] **Step 1: Write the helper** (runs a PE under WSL `dotnet`, returns exit code + stdout; skips cleanly if WSL/dotnet absent)

```csharp
// tests/Chibil.Tests/CoreClr/WslRunner.cs
using System.Diagnostics;
namespace Chibil.Tests.CoreClr;

static class WslRunner
{
    public static bool Available()
    {
        try {
            using var p = Process.Start(new ProcessStartInfo("wsl", "-u root -- bash -lc \"command -v dotnet\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        } catch { return false; }
    }

    // Writes pe to a temp dir under /mnt, runs `dotnet app.dll` in WSL, returns (exit, stdout).
    public static (int exit, string output) Run(byte[] pe, string runtimeConfig)
    {
        string winDir = Path.Combine(Path.GetTempPath(), "chibil_wsl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(winDir);
        try {
            File.WriteAllBytes(Path.Combine(winDir, "app.dll"), pe);
            File.WriteAllText(Path.Combine(winDir, "app.runtimeconfig.json"), runtimeConfig);
            string wslPath = "/mnt/" + char.ToLower(winDir[0]) + winDir[2..].Replace('\\', '/');
            using var p = Process.Start(new ProcessStartInfo("wsl",
                $"-u root -- bash -lc \"cd '{wslPath}' && dotnet app.dll; echo EXIT=$?\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            int exit = 0;
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"EXIT=(-?\d+)");
            if (m.Success) exit = int.Parse(m.Groups[1].Value);
            return (exit, outp);
        } finally { try { Directory.Delete(winDir, true); } catch { } }
    }

    public const string NetCoreRuntimeConfig =
        "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"rollForward\": \"Major\",\n" +
        "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" }\n  }\n}\n";
}
```

- [ ] **Step 2: Build the test project to confirm it compiles**

Run: `dotnet build tests/Chibil.Tests/Chibil.Tests.csproj --nologo`
Expected: builds.

- [ ] **Step 3: Commit**

```bash
git add tests/Chibil.Tests/CoreClr/WslRunner.cs
git commit -m "test: WSL runner helper for cross-platform varargs validation"
```

---

## PART A — Layer 1: chibil-internal varargs (va-buffer)

### Task A1: `__builtin_va_list` type + hidden `__va` parameter + remove the block

This makes a variadic *definition* compile and run (with zero used varargs), proving the def + call-packing plumbing before `va_arg` exists.

**Files:** Modify `chibil/TypeSystem.cs`, `chibil/Parser.cs` (`:1841-1843`, the type-specifier path), `chibil/ChibiTypes.cs` (remove `VaArea`), `chibil/CodeGen.cs` (`RegisterFunction` signature, `GenFunCall` packing, delete `:1423` skip). Test: `tests/Chibil.Tests/CoreClr/VarargsTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
// add to tests/Chibil.Tests/CoreClr/VarargsTests.cs
[Fact]
public void Variadic_def_with_zero_varargs_runs()
{
    // f is variadic but main passes no variable args; f just returns its fixed arg.
    string src = "int f(int a, ...){ return a; } int main(void){ return f(55); }";
    byte[] pe = LinkSource(src);
    var asm = System.Reflection.Assembly.Load(pe);
    Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[] { new string[0] }));
}

// helper used by all tests in this file:
static byte[] LinkSource(string src)
{
    byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    return ChibilLink.LinkPipeline.LinkToBytes(new[] { of }, new System.Collections.Generic.List<string>());
}
```

- [ ] **Step 2: Run — expect FAIL** (`"variadic function definitions are not supported in MSIL mode"`)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Variadic_def_with_zero_varargs_runs`
Expected: FAIL with the not-supported error (a `ChibiException`).

- [ ] **Step 3: Implement**

(a) `TypeSystem.cs` — add a `TyVaList` built-in. It is a pointer-width type; model it as a pointer to `TyChar` (so pointer arithmetic / `ldind` work). Add near the other `Ty*` fields:
```csharp
public readonly CType TyVaList;   // __builtin_va_list — a byte pointer under the hood
```
and initialize it in the constructor after `TyChar` exists: `TyVaList = PointerTo(TyChar);` (use the existing `PointerTo`/`ArrayOf` helper that the constructor uses; confirm the exact method name — it's used at `Parser.cs:1843`/`1844` as `TypeSystem.ArrayOf` and `_types.PointerTo`).

(b) `Parser.cs` — recognize `__builtin_va_list` in the type-specifier path. Find `DeclSpec` (where keywords like `int`/`char`/`struct` are matched) and add a branch: if the token is `__builtin_va_list`, consume it and set the base type to `_types.TyVaList`. (Search `DeclSpec` for how `void`/`_Bool` are matched and mirror it.) This must also let `__builtin_va_list` appear as a parameter type.

(c) `Parser.cs:1841-1843` — replace:
```csharp
        if (ty.IsVariadic && ty.Params != null)
            Util.ErrorTok(ty.Name, "variadic function definitions are not supported in MSIL mode");
        if (ty.IsVariadic) fn.VaArea = NewLvar("__va_area__", TypeSystem.ArrayOf(_types.TyChar, 136));
```
with: append a hidden trailing parameter to the variadic function so codegen can pass/receive the va-buffer pointer. Add it as a real lvar/param named `__va` of type `_types.TyVaList`, recorded on `fn` so codegen knows the arg index. Add a field to `Obj` (`ChibiTypes.cs`): `public Obj VaPtr;` (replacing `VaArea`). Implementation:
```csharp
        if (ty.IsVariadic)
            fn.VaPtr = NewLvar("__va", _types.TyVaList);   // hidden trailing parameter
```
> `NewLvar` adds to `_locals`. The hidden `__va` must be treated as a PARAMETER (highest arg index), not an ordinary local — verify how `CreateParamLvars`/`fn.Params` assign argument indices, and ensure `__va` lands as the last parameter. If params and locals are distinguished by a flag/list, set `__va` as a param. The Step-4 run is the oracle.

(d) `ChibiTypes.cs` — replace `public Obj VaArea;` with `public Obj VaPtr;`.

(e) `CodeGen.cs:1423` — delete the `if (local == fn.VaArea) continue;` skip (the `__va` is now a param, not a skipped local).

(f) `CodeGen.cs` `RegisterFunction` (`:1027`) — when `funcTy.IsVariadic`, the emitted MethodDef signature must include the hidden trailing pointer param. After encoding the fixed params, if variadic, increment the param count by 1 and encode one pointer param (`void*`). Mirror the existing param-count + `EncodeType` loop; append `EncodeType` for `_types.TyVaList` (a pointer). Keep calling convention `0x00`.

(g) `CodeGen.cs` `GenFunCall` (`:2213-2219` arg push, and the direct/indirect call) — when the **callee `funcTy.IsVariadic`** and is **not** `__cdecl` (Layer 1): split args into fixed (first `nFixed = count(funcTy.Params)`) and variadic (the rest). Push the fixed args normally. Then build the va-buffer:
```
// after pushing fixed args:
int nVar = (number of variadic args);
if (nVar == 0) { _enc.OpCode(ILOpCode.Ldc_i4_0); _enc.OpCode(ILOpCode.Conv_u); Push(); } // null ptr
else {
    // localloc 8*nVar, keep the base pointer in a temp local
    emit ldc.i4 (8*nVar); conv.u; localloc;   // -> buffer ptr on stack
    store to a fresh temp local 'vbuf' (or dup-based);
    for each variadic arg i (0..nVar-1):
        load vbuf; ldc.i4 (i*8); conv.i; add;   // slot address
        GenExpr(promoted arg);                    // value (apply default promotions, see note)
        Store(promotedType);                      // stind/stobj into slot
    load vbuf; Push();                            // push buffer ptr as the hidden last arg
}
// then emit the call with signature including the hidden pointer param (see RegisterFunction / the MemberRef sig)
```
> **Default argument promotions:** before `GenExpr` of each variadic arg, the arg's type must be promoted (`char/short/_Bool→int`, `float→double`, array/func→pointer). chibil already performs usual promotions somewhere (search `UsualArithConv` / integer-promotion helpers in `TypeSystem.cs`/`Parser.cs`); apply the same so the slot stores the promoted value and `Store` uses the promoted type. For Task A1 (zero varargs) this path isn't exercised; it is fully exercised by Task A2. Implement it now but A2's test is its oracle.
> Keep a clean structure: a helper `EmitVaBuffer(Node firstVariadicArg, int nVar)` returning after pushing the pointer. Use a temp local for the buffer base (allocate via the same mechanism `GenFunCall` uses for any scratch; if none exists, `dup` carefully — but a temp local is clearer).
> The **call signature** for the variadic callee already includes the hidden pointer (from (f) for MethodDef; for an external/`MemberRef` callee, `RegisterExternalFunction` must do the same — add the hidden param there too when `IsVariadic` and not cdecl).

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Variadic_def_with_zero_varargs_runs`
Expected: PASS (returns 55). Also confirm no regression: `dotnet test ... --filter CoreClr` stays green, and the existing IJW suite is unaffected (varargs path only triggers on `IsVariadic`).

- [ ] **Step 5: Commit**

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "feat(codegen): variadic definitions via hidden va-buffer pointer param"
```

---

### Task A2: `va_start` + `va_arg` (the spike)

**Files:** Modify `chibil/Parser.cs` (builtin dispatch `:1160`), `chibil/ChibiTypes.cs` (`NodeKind` + node fields), `chibil/CodeGen.cs` (`GenExpr` dispatch + lowering). Test: `VarargsTests.cs`.

- [ ] **Step 1: Un-skip the spike test** (it already exists, skip-marked, in `VarargsTests.cs`)

Remove the `Skip = ...` from `Variadic_function_definition_sums_args` so it runs.

- [ ] **Step 2: Run — expect FAIL** (parse error on `__builtin_va_start`)

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter Variadic_function_definition_sums_args`
Expected: FAIL.

- [ ] **Step 3: Implement**

(a) `ChibiTypes.cs:28` `NodeKind` — add `VaStart, VaArg, VaEnd, VaCopy`. Add node operand fields if the generic `Lhs`/`Rhs` aren't enough: reuse `Lhs` for the `ap` lvalue and `Rhs` for `last`/`src`; `va_arg`'s result type is `node.Ty`.

(b) `Parser.cs` — add to the `Primary()` dispatch after `:1160`:
```csharp
        if (Util.Equal(tok, "__builtin_va_start"))
        { var node = NewNode(NodeKind.VaStart, tok); tok = Util.Skip(tok.Next, "("); node.Lhs = Assign(ref tok, tok); tok = Util.Skip(tok, ","); node.Rhs = Assign(ref tok, tok); rest = Util.Skip(tok, ")"); node.Ty = _types.TyVoid; return node; }
        if (Util.Equal(tok, "__builtin_va_arg"))
        { tok = Util.Skip(tok.Next, "("); Node ap = Assign(ref tok, tok); tok = Util.Skip(tok, ","); CType ty = Typename(ref tok, tok); rest = Util.Skip(tok, ")"); var node = NewNode(NodeKind.VaArg, tok); node.Lhs = ap; node.Ty = ty; return node; }
        if (Util.Equal(tok, "__builtin_va_end"))
        { var node = NewNode(NodeKind.VaEnd, tok); tok = Util.Skip(tok.Next, "("); node.Lhs = Assign(ref tok, tok); rest = Util.Skip(tok, ")"); node.Ty = _types.TyVoid; return node; }
        if (Util.Equal(tok, "__builtin_va_copy"))
        { var node = NewNode(NodeKind.VaCopy, tok); tok = Util.Skip(tok.Next, "("); node.Lhs = Assign(ref tok, tok); tok = Util.Skip(tok, ","); node.Rhs = Assign(ref tok, tok); rest = Util.Skip(tok, ")"); node.Ty = _types.TyVoid; return node; }
```
> Confirm `NewNode`/`Assign`/`Typename`/`Util.Skip` signatures match the existing builtins (they do — copied from `:1157-1160`). `va_arg`'s node has `Ty` = the requested type and `Lhs` = the `ap` expression.

(c) `CodeGen.cs` — in `GenExpr` (`:1897`, the big `switch (node.Kind)`), add cases:
- **`VaStart`**: `ap = __va`. Emit the address of the `ap` lvalue (`GenAddr(node.Lhs)`), load the hidden `__va` parameter (`_enc.LoadArgument(vaPtrArgIndex); Push();` — get the arg index of `_currentFn.VaPtr`), then `Store(_types.TyVaList)` (stind of a pointer). Net stack 0. (`VaStart` is a void statement-expression.)
- **`VaArg`**: read `*(Ty*)ap` and advance `ap += 8`. Sequence:
  ```
  GenAddr(node.Lhs);          // &ap   (pointer to the va_list variable)
  dup;                         // keep &ap for the store-back
  ldind.i / ldind.<ptr>;       // load ap (the current pointer value)   [Load(TyVaList)]
  // value read:
  dup the loaded ap? -> need ap twice: once to read value, once to advance.
  ```
  Cleaner: load `ap` into a temp, read value, advance, store back. Concretely:
  ```
  // compute &ap once into temp 'pap' (pointer to the va_list var):
  GenAddr(node.Lhs); store pap;
  // ap = *pap
  load pap; Load(TyVaList);  -> ap   ; store apTmp
  // result = *(Ty*)ap
  load apTmp; ldind/ldobj for node.Ty;  -> result (leave on stack, Push())
  // *pap = ap + 8
  load pap; load apTmp; ldc.i4.8; conv.i; add; Store(TyVaList);
  ```
  Use existing `Load(CType)` (the rvalue-deref helper; find its name — likely `Load` paired with `Store` at `:1726`) and `Store(CType)`. Result type = `node.Ty`; leave the value on the stack with `Push()`.
- **`VaEnd`**: evaluate nothing (or evaluate `node.Lhs` for side effects? none) — emit nothing, push nothing (void).
- **`VaCopy`**: `dst = src` (pointer copy): `GenAddr(node.Lhs)` (&dst); `GenExpr(node.Rhs)` (src value, a pointer); `Store(TyVaList)`.

> You need the **arg index of `_currentFn.VaPtr`** for `VaStart`. Determine how params map to arg indices in this codegen (search where `LoadArgument`/`Ldarga_s` compute `argIdx` for `NodeKind.Var` params — `GenAddr:1590` shows `argIdx`). The hidden `__va` is the last parameter, so its index = (number of fixed params) [+1 if there's a struct-return hidden first param — check `EmitFunctions`/the return-large-struct path]. Compute it the same way the existing code computes a param's arg index, applied to `_currentFn.VaPtr`.

- [ ] **Step 4: Run — expect PASS**

Run: `dotnet test tests/Chibil.Tests/Chibil.Tests.csproj --filter "Variadic_function_definition_sums_args|Variadic_def_with_zero"`
Expected: both PASS (spike returns 55).

- [ ] **Step 5: Cross-platform check**

Add and run a Linux variant:
```csharp
[Fact]
public void Variadic_sum_runs_on_linux()
{
    Skip.IfNot(WslRunner.Available(), "WSL+dotnet unavailable");   // or: if(!Available) return;
    byte[] pe = LinkSource("int sum_n(int c, ...){ __builtin_va_list ap; __builtin_va_start(ap,c); int s=0; for(int i=0;i<c;i++) s+=__builtin_va_arg(ap,int); __builtin_va_end(ap); return s; } int main(void){ return sum_n(3,20,22,13); }");
    var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
    Assert.True(exit == 55, $"exit {exit}: {outp}");
}
```
Run it; expected EXIT 55 on Linux (proving no vararg-convention wall). If `Skippable` isn't referenced, gate with a plain `if (!WslRunner.Available()) return;`.

- [ ] **Step 6: Commit**

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "feat(codegen): va_start/va_arg/va_end/va_copy via va-buffer (spike: 55, Win+Linux)"
```

---

### Task A3: Mixed scalar types + zero-args + va_copy

**Files:** `tests/Chibil.Tests/CoreClr/VarargsTests.cs` (+ any codegen fixes the tests surface).

- [ ] **Step 1: Write the tests**

```csharp
[Fact]
public void Variadic_mixed_types()
{
    // int + long long + double, then truncate the double sum to int.
    string src = @"
typedef __builtin_va_list va_list;
long long mix(int n, ...){ va_list ap; __builtin_va_start(ap,n);
  long long acc = __builtin_va_arg(ap, int);
  acc += __builtin_va_arg(ap, long long);
  acc += (long long)__builtin_va_arg(ap, double);
  __builtin_va_end(ap); return acc; }
int main(void){ return (int)mix(3, 10, 20LL, 25.0); }   // 55
";
    var asm = System.Reflection.Assembly.Load(LinkSource(src));
    Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
}

[Fact]
public void Variadic_va_copy_reiterates()
{
    string src = @"
typedef __builtin_va_list va_list;
int twice(int n, ...){ va_list a,b; __builtin_va_start(a,n);
  __builtin_va_copy(b,a);
  int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(a,int);
  for(int i=0;i<n;i++) s+=__builtin_va_arg(b,int);
  __builtin_va_end(a); __builtin_va_end(b); return s; }
int main(void){ return twice(2, 10, 15); }   // (10+15)*2 = 50... choose values summing to 55
";
    // pick args so the doubled sum is 55? 2 args doubled can't be odd; use 3 args: (5+10+12)*2=54; instead assert the actual doubled value.
    var asm = System.Reflection.Assembly.Load(LinkSource(
        src.Replace("twice(2, 10, 15)", "twice(3, 5, 10, 12)")));   // (5+10+12)*2 = 54
    Assert.Equal(54, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
}
```
> Note the `va_copy` test asserts the *doubled* sum (54), not 55 — pick concrete values and assert their exact computed result.

- [ ] **Step 2: Run — expect FAIL or PASS** depending on whether A2's slot model already handles `long long`/`double`.

Run: `dotnet test ... --filter "Variadic_mixed_types|Variadic_va_copy"`
Expected: if the 8-byte slot read/store for `long long`/`double` is correct, PASS; otherwise debug the `Store`/`Load` type widths in `va_arg`/packing.

- [ ] **Step 3: Fix** any slot-width issues so both pass (the packing `Store(promotedType)` and `va_arg` `Load(node.Ty)` must use the right `ldind`/`stind`/`ldobj`/`stobj` for `int`/`long long`/`double`/pointer; the 8-byte advance is constant).

- [ ] **Step 4: Run — expect PASS**, then add a Linux variant of `Variadic_mixed_types` via `WslRunner` and confirm.

- [ ] **Step 5: Commit**

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "test(varargs): mixed scalar types + va_copy (Win+Linux)"
```

---

### Task A4: `va_list` as a parameter + forwarding (the SQLite shape)

**Files:** `tests/Chibil.Tests/CoreClr/VarargsTests.cs` (+ any fixes). The parser already accepts `__builtin_va_list` as a param type (Task A1b); this verifies forwarding works end-to-end.

- [ ] **Step 1: Write the test** (the `mprintf → VXPrintf` shape)

```csharp
[Fact]
public void Variadic_forwarding_via_va_list_param()
{
    string src = @"
typedef __builtin_va_list va_list;
int vsum(int n, va_list ap){ int s=0; for(int i=0;i<n;i++) s+=__builtin_va_arg(ap,int); return s; }
int sum(int n, ...){ va_list ap; __builtin_va_start(ap,n); int r=vsum(n,ap); __builtin_va_end(ap); return r; }
int main(void){ return sum(3, 20, 22, 13); }   // 55
";
    var asm = System.Reflection.Assembly.Load(LinkSource(src));
    Assert.Equal(55, (int)asm.EntryPoint.Invoke(null, new object[]{ new string[0] }));
}
```

- [ ] **Step 2: Run — expect PASS or a fixable failure.**

Run: `dotnet test ... --filter Variadic_forwarding_via_va_list_param`
Expected: PASS if `va_list` passes as an ordinary pointer param and `vsum` reads it. If `vsum` (a non-variadic function taking `va_list`) mis-handles the param, fix: a `va_list` parameter is just a pointer parameter — no hidden `__va`, no special handling. Ensure non-variadic functions with a `va_list` param are unaffected by the variadic machinery (they have `IsVariadic == false`).

- [ ] **Step 3: Fix** if needed; re-run to PASS. Add a Linux variant via `WslRunner`.

- [ ] **Step 4: Commit**

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "test(varargs): va_list-as-param forwarding (mprintf shape, Win+Linux)"
```

---

## PART B — Layer 2: native variadic calls (monomorphized cdecl)

### Task B1: `__cdecl`-variadic call site emits a concrete promoted signature

**Files:** Modify `chibil/CodeGen.cs` (`GenFunCall`, the call-emission branch). Test: a unit test asserting the emitted external call carries the concrete signature.

- [ ] **Step 1: Write the failing test** (compile a `__cdecl` variadic call; assert the object references the callee with a concrete signature — fixed + promoted vararg types)

```csharp
[Fact]
public void Cdecl_variadic_call_emits_concrete_signature()
{
    // snprintf declared __cdecl variadic; a single call with (char*, unsigned long, char*, int).
    string src = @"
typedef unsigned long size_t;
int __cdecl snprintf(char*, size_t, const char*, ...);
int main(void){ char b[16]; return snprintf(b, 16, ""%d"", 42); }
";
    byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    // snprintf must appear as an external method reference whose signature includes the
    // concrete promoted vararg (int32) AFTER the 3 fixed params — i.e. 4 params, no va-buffer pointer.
    Assert.Contains(of.Md.MemberReferences, h => {
        var mr = of.Md.GetMemberReference(h);
        return of.Md.GetString(mr.Name) == "snprintf";
    });
    // (Stronger: decode the MemberRef signature and assert paramCount==4 and last param is I4.
    //  Use System.Reflection.Metadata BlobReader on mr.Signature.)
}
```
> Make the assertion concrete: decode `mr.Signature` with a `BlobReader`, read the calling-convention byte and param count, and assert `paramCount == 4` (3 fixed + 1 promoted vararg) and that there is **no** trailing `void*` va-buffer param (i.e. this is the Layer-2 path, not Layer-1). If decoding is heavy, at minimum assert the MemberRef exists and the obj has no `localloc` in `main`'s IL (Layer-2 does not pack).

- [ ] **Step 2: Run — expect FAIL** (today a variadic callee either errors or, post-A1, takes the Layer-1 hidden pointer — wrong for `__cdecl`).

Run: `dotnet test ... --filter Cdecl_variadic_call_emits_concrete_signature`
Expected: FAIL.

- [ ] **Step 3: Implement** — in `GenFunCall`, branch the variadic handling on calling convention:
  - `funcTy.IsVariadic && funcTy.CallConv == CallConv.Cdecl` → **Layer 2**: push ALL args (fixed + variadic, each with default promotions), and emit the call to an external `MemberRef`/`calli` whose signature is **cdecl, paramCount = total args, encoding the concrete promoted type of every argument** (fixed types from `funcTy.Params`, variadic types from the actual arg expressions' promoted types). No va-buffer, no hidden pointer. For a direct call this is a `MemberRef` registered with that concrete signature (the name = callee name); for indirect, the `calli` standalone sig.
  - else if `funcTy.IsVariadic` (default conv) → **Layer 1** (Task A1g).
  - else → unchanged.
  Factor the concrete-signature build into a helper `EncodeConcreteVarargSig(BlobBuilder, funcTy, actualArgNodes)` that writes cdecl conv byte, total param count, return type, each fixed param type, then each promoted variadic arg type.

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add chibil/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "feat(codegen): __cdecl variadic calls emit concrete monomorphized signatures"
```

---

### Task B2: `chibil-link` monomorphized P/Invoke (dedup by name+signature) + libc snprintf e2e

**Files:** Modify `tools/chibil-link/SymbolResolver.cs` (dedup key). Test: `VarargsTests.cs` (run `snprintf` via libc on Linux / msvcrt on Windows).

- [ ] **Step 1: Write the failing e2e test**

```csharp
[Fact]
public void Native_snprintf_int_runs_on_linux()
{
    if (!WslRunner.Available()) return;
    string src = @"
typedef unsigned long size_t;
int __cdecl snprintf(char*, size_t, const char*, ...);
int __cdecl strcmp(const char*, const char*);
int main(void){ char b[16]; snprintf(b, 16, ""%d"", 42); return strcmp(b, ""42"")==0 ? 55 : 1; }
";
    byte[] obj = TestCompiler.CompileToObj(src, Chibil.TargetProfile.CoreClr);
    var of = ObjectFile.Load(obj, "t.obj");
    byte[] pe = ChibilLink.LinkPipeline.LinkToBytes(new[]{of}, new System.Collections.Generic.List<string>{ "c" });
    var (exit, outp) = WslRunner.Run(pe, WslRunner.NetCoreRuntimeConfig);
    Assert.True(exit == 55, $"exit {exit}: {outp}");
}
```
> `-lc` → `libc.so.6`; `snprintf`/`strcmp` resolve to libc. `strcmp` is non-variadic (already works via F1). `snprintf("%d")` is the integer-vararg case (no SysV-AL float issue).

- [ ] **Step 2: Run — expect FAIL** (F1's P/Invoke dedup is by-name; two different `snprintf` arities or the concrete sig may collide / be wrong).

Run: `dotnet test ... --filter Native_snprintf_int_runs_on_linux`
Expected: FAIL.

- [ ] **Step 3: Implement** — in `SymbolResolver`, change the synthesized-P/Invoke dedup from by-name to **by (name, signature-blob)** so multiple concrete signatures for the same native entry coexist (e.g. `snprintf(byte*,...,int)` vs another arity). Read the current `pinvokeByName` dedup (added in F1) and key it on `(name, signatureBlobBytes)` instead. Ensure each distinct concrete signature gets its own `pinvokeimpl` MethodDef bound to the same `ModuleRef`/entry name.

- [ ] **Step 4: Run — expect PASS** (`EXIT=55` on Linux). Add a Windows variant using `-lmsvcrt.dll` + `DotnetHostRunner` and confirm `55` there too.

- [ ] **Step 5: Document the float caveat** — add a test that prints a float and records behavior per platform (do NOT assert a fixed value cross-platform; assert Windows, and on Linux assert-or-document per §7 of the spec):
```csharp
[Fact]
public void Native_snprintf_float_documents_sysv_caveat()
{
    // %f via native snprintf: works on Windows; on Linux x64 may misbehave (AL unset). Document, don't hard-assert cross-platform.
    // Implement as a Windows-only assertion + a Linux observation logged via Assert messages.
}
```
Implement it as a Windows-only correctness assert (msvcrt) and, on Linux, capture+report the output without failing the suite (the caveat is known/documented).

- [ ] **Step 6: Commit**

```bash
git add tools/chibil-link/ tests/Chibil.Tests/CoreClr/VarargsTests.cs
git commit -m "feat(linker): monomorphized cdecl P/Invoke; native snprintf varargs (Win+Linux)"
```

---

## PART C — Wrap

### Task C1: Finalize SQLite's stdarg.h + full-suite regression

**Files:** Modify `samples/sqlite/include/stdarg.h` (on the `sqlite-managed` branch later; here just ensure the builtins match). Run the full suite.

- [ ] **Step 1: Confirm the placeholder `stdarg.h` macros match the implemented builtins** — `va_start`→`__builtin_va_start`, `va_arg`→`__builtin_va_arg`, `va_end`→`__builtin_va_end`, `va_copy`→`__builtin_va_copy`, `va_list`→`__builtin_va_list`. (The file already has these; no change expected — just verify.)

- [ ] **Step 2: Run the full suite under MSVC dev env** (regression gate)

Run: `run-tests.cmd x64`
Expected: all green (existing tests + new varargs tests; IJW path unaffected — varargs only triggers on `IsVariadic`).

- [ ] **Step 3: Run the CoreCLR varargs tests on Linux** via WSL variants — confirm Layer 1 (1–5) and Layer 2 int/ptr (6) pass on Linux.

- [ ] **Step 4: Commit** any final touch-ups.

```bash
git add -A
git commit -m "test(varargs): full-suite green; SQLite stdarg.h verified"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** §4 Layer-1 va-buffer ↔ Tasks A1–A4; §5 Layer-2 cdecl ↔ B1–B2; §6 components ↔ all tasks; §7 scope/SysV caveat ↔ B2 Step 5 (float) + A-tests (64-bit scalars); §8 testing (Win+Linux, the 7 cases) ↔ A2/A3/A4/B2 with `WslRunner`. Struct-by-value-vararg "not supported" error: add a guard in `va_arg` codegen if `node.Ty` is a struct → `Util.Error` (small; fold into A2).
- **Empirical/iteration points (oracles given):** the param→arg-index of the hidden `__va` (A1/A2 — the run is the oracle); the exact `ldind`/`stind`/`ldobj`/`stobj` per scalar width in slots (A3); the MemberRef concrete-signature decode in B1; the SysV-AL float behavior (B2 Step 5, documented not asserted cross-platform).
- **Promotions:** locate chibil's existing usual-arithmetic/integer-promotion + array/func-decay helpers and reuse them for variadic args (Layer 1 packing and Layer 2 concrete sig) — do not hand-roll.
- **Non-regression:** every change is gated on `IsVariadic` (and, for Layer 2, `CallConv.Cdecl`), so non-variadic codegen and the IJW path are untouched. The full MSVC suite (`run-tests.cmd`) is the gate.
- **Downstream:** after this lands, the `sqlite-managed` branch rebases onto `varargs-msil` and resumes SQLite SP1 Part C.
```
