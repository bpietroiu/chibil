# API Facade — Plan 4: The QuickJS oracle

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Validate the whole API-facade feature against a real library. Build `qjs.dll` with `--export-api=quickjs.h` and prove, end-to-end, that a **C# program evaluates JavaScript through the facade** — `quickjs.Api.JS_Eval("40+2")` → `42` — with no reflection and no hand-written P/Invoke. This is the README's "consume C from .NET" item, realized on Bellard's JS engine.

**Why an oracle:** `mylib` (the in-repo fixture) proves each facet in isolation. QuickJS is the real-world ground truth that proves it holds at scale (the spike already emitted **188 functions, 21 public types, 3 enums** from `quickjs.h`) and produces a *correct* result (`40+2 == 42` is the oracle).

**Verified groundwork (spike):** `qjs.dll` builds cleanly with the facade, and the key functions are callable from C#:
```
JSRuntime* JS_NewRuntime();  JSContext* JS_NewContext(JSRuntime*);
JSValue JS_Eval(JSContext*, sbyte* input, ulong len, sbyte* filename, int flags);
int JS_ToInt32(JSContext*, int* out, JSValue);
struct JSValue { JSValueUnion u; long tag; }   struct JSValueUnion { int int32; double float64; void* ptr; int short_big_int; }
```

**Tech Stack:** C# / .NET 10. WSL-gated (qjs.dll's libc P/Invokes resolve on Linux; the consumer runs via `dotnet run` under WSL). This tier is **scripted + documented**, not xUnit (mirroring the existing QuickJS bring-up: build scripts + `CompileQuickJS.md`). Branch: `feature/api-facade-plan4-quickjs-oracle`. Builds on Plans 1-3.5 (merged).

**Note:** QuickJS lives git-ignored under `targets/quickjs-2025-09-13/`; `qjs.dll` is a build output (not committed). The committed artifacts are the build/run scripts, the C# consumer source, and the docs.

---

## File Structure

- Create `targets/build/quickjs-api.sh` — build `qjs.dll` with `--export-api=quickjs.h` (already drafted; Task 1 commits it).
- Create `targets/build/quickjs-api-surface.sh` — surface oracle: dump/assert the facade shape from the built `qjs.dll`.
- Create `targets/quickjs-api-consumer/` — a tiny C# project that references `qjs.dll` and evaluates JS via the facade (the behavioral oracle).
- Create `targets/build/quickjs-api-run.sh` — build qjs.dll, then `dotnet run` the consumer under WSL; assert `42`.
- Modify `CompileQuickJS.md` + `README.md` — document the facade result.

---

## Task 1: Build script + surface oracle

**Files:**
- Create: `targets/build/quickjs-api.sh` (drafted on this branch — verify it is present)
- Create: `targets/build/quickjs-api-surface.sh`

The surface oracle asserts the facade exposes the real public API: a high function
count, the core types, opaque handles, and the enums. It uses a small inline C# program
(via `MetadataLoadContext`, no execution) to read `qjs.dll`'s metadata.

- [ ] **Step 1: Confirm/commit the build script**

`targets/build/quickjs-api.sh` should already exist on this branch (it compiles the 7
QuickJS TUs with `--export-api=quickjs.h` and links `qjs.dll`). If missing, create it from
`targets/build/quickjs-chibil.sh` with `--export-api=quickjs.h` added to `CFLAGS`.

Build it (WSL): `wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-api.sh`
Expected: `compiled OK=7 FAIL=0`, `link exit: 0`, `qjs.dll` produced. (If WSL is unavailable, note it; this whole plan is WSL-gated.)

- [ ] **Step 2: Create the surface-oracle script**

Create `targets/build/quickjs-api-surface.sh`:

```bash
#!/bin/bash
# Surface oracle: assert qjs.dll's `quickjs` facade exposes the real public API.
set -u
ROOT=/mnt/d/sandbox/chibil
QJS=$ROOT/targets/quickjs-2025-09-13/qjs.dll
[ -f "$QJS" ] || { echo "qjs.dll missing — run quickjs-api.sh first"; exit 1; }
D=$(mktemp -d)
cat > "$D/Program.cs" <<'CS'
using System;using System.Linq;using System.Reflection;
class P{static int Main(){
 var rtDir=System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
 var asms=System.IO.Directory.GetFiles(rtDir,"*.dll").ToList();
 asms.Add(Environment.GetEnvironmentVariable("QJS"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asms));
 var asm=mlc.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("QJS"));
 var api=asm.GetType("quickjs.Api");
 int funcs=api.GetMethods(BindingFlags.Public|BindingFlags.Static).Length;
 string[] needType={"quickjs.JSValue","quickjs.JSValueUnion","quickjs.JSRuntime","quickjs.JSContext"};
 string[] needFunc={"JS_NewRuntime","JS_NewContext","JS_Eval","JS_ToInt32"};
 int enums=asm.GetTypes().Count(t=>t.Namespace=="quickjs"&&t.IsEnum);
 bool ok=funcs>=150;
 Console.WriteLine($"facade functions={funcs} (>=150: {funcs>=150})");
 foreach(var t in needType){bool p=asm.GetType(t)!=null;ok&=p;Console.WriteLine($"type {t}: {p}");}
 foreach(var f in needFunc){bool p=api.GetMethod(f,BindingFlags.Public|BindingFlags.Static)!=null;ok&=p;Console.WriteLine($"func {f}: {p}");}
 ok&=enums>=3;Console.WriteLine($"enums={enums} (>=3: {enums>=3})");
 Console.WriteLine(ok?"SURFACE_ORACLE_OK":"SURFACE_ORACLE_FAIL");
 return ok?0:1;
}}
CS
cat > "$D/s.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
<ItemGroup><PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.0"/></ItemGroup></Project>
CSPROJ
QJS="$QJS" dotnet run --project "$D" 2>&1 | tail -20
rc=${PIPESTATUS[0]}
rm -rf "$D"
exit $rc
```

- [ ] **Step 3: Run the surface oracle**

Run (WSL): `wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-api-surface.sh`
Expected: `facade functions=188 ...`, all `type`/`func` lines `True`, `enums=3`, and
`SURFACE_ORACLE_OK` (exit 0). This asserts the facade exposes the real public surface.

- [ ] **Step 4: Commit**

```bash
git add targets/build/quickjs-api.sh targets/build/quickjs-api-surface.sh
git commit -m "quickjs: build script + surface oracle for the --export-api facade"
```

---

## Task 2: Behavioral oracle — C# evaluates JS through the facade

**Files:**
- Create: `targets/quickjs-api-consumer/Program.cs`
- Create: `targets/quickjs-api-consumer/qjsconsumer.csproj`
- Create: `targets/build/quickjs-api-run.sh`

The consumer references the built `qjs.dll`, calls `quickjs.Api.JS_NewRuntime/JS_NewContext/
JS_Eval/JS_ToInt32`, and asserts `40+2 == 42`. It also reads the result `JSValue.u.int32`
directly (proving the Plan-3.5 nested field is usable). This is exploratory — if a call
or marshalling step fails, that is a gap to diagnose (note it; the surface oracle still
stands as the primary deliverable).

- [ ] **Step 1: Create the consumer project**

`targets/quickjs-api-consumer/qjsconsumer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- qjs.dll is the chibil-built facade assembly, sitting in the quickjs tree. -->
    <RestoreAdditionalProjectSources></RestoreAdditionalProjectSources>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="qjs">
      <HintPath>../quickjs-2025-09-13/qjs.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

`targets/quickjs-api-consumer/Program.cs`:

```csharp
using System;
using System.Text;
using quickjs;   // the facade namespace from qjs.dll

unsafe
{
    JSRuntime* rt = Api.JS_NewRuntime();
    if (rt == null) { Console.Error.WriteLine("JS_NewRuntime returned null"); Environment.Exit(2); }
    JSContext* ctx = Api.JS_NewContext(rt);
    if (ctx == null) { Console.Error.WriteLine("JS_NewContext returned null"); Environment.Exit(2); }

    byte[] code = Encoding.ASCII.GetBytes("40+2");
    byte[] file = Encoding.ASCII.GetBytes("<oracle>\0");
    int result;
    fixed (byte* c = code)
    fixed (byte* f = file)
    {
        // JS_Eval(ctx, input, input_len, filename, eval_flags=0 /* JS_EVAL_TYPE_GLOBAL */)
        JSValue v = Api.JS_Eval(ctx, (sbyte*)c, (ulong)code.Length, (sbyte*)f, 0);
        // Decode via the public API (robust regardless of tag) ...
        int outv;
        Api.JS_ToInt32(ctx, &outv, v);
        result = outv;
        // ... and ALSO read the nested field directly (proves Plan 3.5 JSValue.u is usable).
        int direct = v.u.int32;
        Console.WriteLine($"JS_ToInt32={outv}  JSValue.u.int32={direct}  tag={v.tag}");
    }

    Api.JS_FreeContext(ctx);
    Api.JS_FreeRuntime(rt);

    if (result != 42) { Console.Error.WriteLine($"ORACLE_FAIL expected 42 got {result}"); Environment.Exit(1); }
    Console.WriteLine("BEHAVIORAL_ORACLE_OK: quickjs.Api.JS_Eval(\"40+2\") == 42");
}
```

Notes for the implementer:
- The facade types `JSRuntime`/`JSContext` are opaque value types; the functions take/return
  `JSRuntime*`/`JSContext*` (pointers), so the consumer is `unsafe`. Confirm the exact
  pointer spelling C# expects from the facade (the spike showed `JSRuntime*`/`JSContext*`).
- `JS_Eval`'s `input` is `sbyte*` (the spike showed `SByte*`); cast `(sbyte*)c`. `input_len`
  is `ulong` (size_t).
- `eval_flags = 0` is `JS_EVAL_TYPE_GLOBAL`. If `40+2` returns an exception tag instead of an
  int, check the flag and the tag; `JS_ToInt32` is the robust decoder.
- If `JSValue.u.int32` reads wrong but `JS_ToInt32` is right, the struct field offset is off —
  but Plan 3.5 + Task-1 of named-fields make `u` an ExplicitLayout field at the C offset, so it
  should match. Report either way.

- [ ] **Step 2: Create the run script**

Create `targets/build/quickjs-api-run.sh`:

```bash
#!/bin/bash
# Behavioral oracle: build qjs.dll with the facade, then run the C# consumer that
# evaluates "40+2" through quickjs.Api and asserts 42.
set -u
ROOT=/mnt/d/sandbox/chibil
bash "$ROOT/targets/build/quickjs-api.sh" || { echo "qjs.dll build failed"; exit 1; }
cd "$ROOT/targets/quickjs-api-consumer" || exit 1
# Run in the quickjs dir so qjs.runtimeconfig.json + libc binding resolve like `dotnet qjs.dll`.
dotnet run -c Debug 2>&1 | tail -15
echo "consumer exit: ${PIPESTATUS[0]}"
```

- [ ] **Step 3: Run the behavioral oracle**

Run (WSL): `wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-api-run.sh`
Expected: `JS_ToInt32=42 ...`, `BEHAVIORAL_ORACLE_OK ...`, consumer exit 0.

If it fails, diagnose (do NOT fake the result):
- Build/reference errors → fix the `HintPath`/`unsafe`/pointer types.
- `JS_NewRuntime` null or a crash → the facade forwarder or a libc P/Invoke (the `atexit`
  warning at link time) — check the runtimeconfig and that qjs runs at all (`dotnet qjs.dll
  /tmp/x.js` should already work from the existing build).
- Wrong value → inspect the tag/flags; report the gap. A genuine facade bug surfaced here is
  a valuable oracle finding — note it for a follow-on fix; the surface oracle (Task 1) remains
  the validated deliverable.

- [ ] **Step 4: Commit**

```bash
git add targets/quickjs-api-consumer targets/build/quickjs-api-run.sh
git commit -m "quickjs: behavioral oracle — C# evaluates JS through the quickjs.Api facade"
```

---

## Task 3: Document the oracle

**Files:**
- Modify: `CompileQuickJS.md`
- Modify: `README.md`

- [ ] **Step 1: Add a facade section to CompileQuickJS.md**

Append a section documenting: building `qjs.dll` with `--export-api=quickjs.h` yields a
`quickjs` namespace with a `quickjs.Api` class of **188 forwarder functions**, **21 public
types** (`JSValue`, `JSValueUnion`, opaque `JSRuntime`/`JSContext`, …), and **3 enums**; and
that a C# program calls `quickjs.Api.JS_Eval("40+2")` → `42` with real types, no reflection,
no P/Invoke. Reference `targets/build/quickjs-api.sh`, `quickjs-api-surface.sh`,
`quickjs-api-run.sh`. State the numbers from the actual run (don't invent — use the surface
oracle's output).

- [ ] **Step 2: Update the README**

In `README.md`, under the chibil-link / facade material, add a one-line bullet: the linker
can emit a C# binding facade from a public header (`--export-api`), validated on QuickJS
(C# evaluates JS through `quickjs.Api`).

- [ ] **Step 3: Commit**

```bash
git add CompileQuickJS.md README.md
git commit -m "docs: document the QuickJS API-facade oracle"
```

---

## Notes for the implementer

- This entire plan is **WSL-gated**. If WSL/dotnet-on-Linux is unavailable, Tasks 1-2 cannot run to green; stop and report rather than faking results. (The repo's prior QuickJS work confirms WSL+dotnet works on this machine.)
- The surface oracle (Task 1) is the **primary, lower-risk deliverable** — it asserts the facade's shape from metadata alone. The behavioral oracle (Task 2) is the showcase and is **exploratory**: a failure there is an oracle finding (a real-world gap), not necessarily a plan failure — diagnose and report it; do not weaken the `== 42` assertion.
- Do NOT commit `qjs.dll` or anything under `targets/quickjs-2025-09-13/` (git-ignored, third-party). Commit only the scripts, the consumer source, and the docs.
- Known facade limitation (from the Plan-3.5 review, acceptable here): a public struct's
  *pointer-to-opaque* member fields are skipped from field-level reflection. This does NOT
  affect the oracle — `JS_Eval`/`JS_ToInt32` are functions (forwarders handle pointers fully),
  and the result is decoded via `JS_ToInt32` and the by-value `JSValue.u.int32`.
