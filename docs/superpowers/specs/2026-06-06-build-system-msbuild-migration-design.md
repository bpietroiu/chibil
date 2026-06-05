# Build-system reorg + sh→MSBuild migration — Design

**Date:** 2026-06-06
**Status:** Draft for review

## Goal

Stop scattering build output across the repo and stop hardcoding tool paths in
~21 shell scripts. Centralize all .NET build output under `./build/`, and convert
the **primary** chibil build pipeline (managed-musl objects → QuickJS-on-musl →
consumer oracle) from WSL bash scripts into cross-platform MSBuild projects that
run and debug in Visual Studio on Windows with **no WSL**. Keep the upstream
(`MichalStrehovsky/chibil`) tree mergeable.

## Constraints / decisions (agreed)

- **Output layout:** .NET artifacts layout (`UseArtifactsOutput`), output → `./build/`.
- **Scope of redirect:** all projects, via a NEW root `Directory.Build.props`
  (zero edits to existing `.csproj`). The upstream `scenarios/Directory.Build.props`
  shadows the root for that one project — `scenarios/CoffEmitterTests` keeps its
  local `bin/obj`; accepted exception.
- **`.gitignore`:** add a single `/build/` line (the only upstream-file edit).
- **Migration scope:** PHASED. Phase 1 = primary pipeline → MSBuild. Phase 2 =
  secondary ports (bash-5.3, micropython, test262, layer spikes, quickjs-* variants).
- **DSL:** custom MSBuild tasks (`ChibilCompile`, `ChibilLink`) + `.proj` files.
- **Native-libc oracle** (`quickjs-api-run.sh`, needs Linux `libc.so.6`) stays a
  thin Linux-gated script — out of scope for MSBuild conversion.

## Upstream vs. ours (do-not-edit map)

Upstream (mergeable, do not modify): `chibil/`, `crt/`, `tests/`, `scenarios/`,
`tools/asm2obj/`, `.github/`, `docs/` (their parts), `samples/`, `README.md`,
`THIRD-PARTY-NOTICES.md`. The ONE allowed upstream edit: `+/build/` in `.gitignore`.

Ours (free to restructure): `tools/chibil-link/`, `tools/objimports/`, all of
`targets/`, top-level `Compile*.md` / `DebugAndConsume.md`, `run-tests.cmd`.

## Architecture

### A. Centralized output (root `Directory.Build.props`)

```xml
<Project>
  <PropertyGroup>
    <UseArtifactsOutput>true</UseArtifactsOutput>
    <ArtifactsPath>$(MSBuildThisFileDirectory)build</ArtifactsPath>
  </PropertyGroup>
</Project>
```

Result (single-TFM projects, lowercase config in the leaf):
```
build/bin/Chibil/debug/chibil.dll            build/obj/Chibil/debug/
build/bin/ChibilLink/debug/chibil-link.dll   build/bin/Chibil.Pal/release/Chibil.Pal.dll
build/bin/qjsconsumer/debug/                 build/bin/Chibil.Tests/debug/
```
Wipe `./build` to clean. Likely side-benefit: the AnyCPU-vs-x64 split path gotcha
goes away (Platform is not part of the artifacts pivot — to be verified).

### B. MSBuild tasks: `Chibil.Build.Tasks`

New project `tools/chibil-build/Chibil.Build.Tasks.csproj` (a `Microsoft.Build.Utilities`-
based task assembly, targeting the same net10.0), plus `tools/chibil-build/build/`
holding `Chibil.Build.props` + `Chibil.Build.targets` for consumers to import.

**Resolved tool paths (no hardcoding):** the targets file exposes
```
$(ChibilDll)     = build/bin/Chibil/<cfg>/chibil.dll
$(ChibilLinkDll) = build/bin/ChibilLink/<cfg>/chibil-link.dll
```
derived from `$(ArtifactsPath)` (with a configurable `$(ChibilToolConfig)`, default
`debug`). A `.proj` may instead `ProjectReference` the tools to force a build first.

**`ChibilCompile` task** — compile C TUs to `.obj`.
| Property | Meaning |
|---|---|
| `Sources` (items) | `.c` files |
| `IncludeDirs` (items) | `-I` dirs |
| `Defines` (items) | `-D` macros |
| `ForceInclude` | `-include <hdr>` |
| `ExtraArgs` | passthrough (e.g. `--target=coreclr -nostdinc -mlp64 --export-api=…`) |
| `OutputDir` | where `.obj` land |
| `ContinueOnError` | skip known-residual TU failures (managed-musl set) |
| `Objects` (output items) | produced `.obj` paths |

Behavior: **incremental** (skip a TU whose `.obj` is newer than the `.c` + the
force-include header + the tool dll), **parallel** (bounded `Parallel.ForEach`).
Invocation strategy is an implementation detail (in-process via chibil's library
API like `TestCompiler.CompileToObj`, preferred for speed; or `dotnet chibil.dll`
per file). Emits one obj per source; logs skipped failures so coverage isn't
silently truncated.

**`ChibilLink` task** — merge `.obj` → `.dll`.
| Property | Meaning |
|---|---|
| `Objects` (items) | inputs |
| `Output` | output dll |
| `Shared` (bool) | `-shared` |
| `Debug` (bool) | `-g` |
| `Binds` (items) | `--bind=sym=Ns.Type.Method` |
| `References` (items) | `-r asm.dll` |
| `ExtraArgs` | passthrough (`--export-api`, `--print-imports`, `-l`, …) |

### C. Primary pipeline as `.proj`

1. **`targets/managed-musl/ManagedMusl.proj`** — replaces `build-managed-musl.sh`.
   ItemGroup of musl `src/<subsys>/**/*.c`, the musl-compat include set, defines,
   force-include `musl-chibil-compat.h`; `ChibilCompile … ContinueOnError=true
   OutputDir=$(ArtifactsPath)/managed-musl`. Default target produces the obj set
   (no more `/tmp/managed_musl` — output lives under `build/`).

2. **`targets/quickjs/QuickJsManaged.proj`** — replaces `qjs-on-musl-api.sh`.
   - `ChibilCompile` the engine TUs (`cutils dtoa libregexp libunicode quickjs`)
     with the facade flags (`--export-api=quickjs.h`, musl headers) + `pal-shim.c`.
   - `ChibilLink` against `@(ManagedMuslObj)` (minus `*oldmalloc*`) + `Chibil.Pal`,
     binding `__chibil_syscall`/`__chibil_get_tp`, `-g -shared` → `qjs.dll`.
   - Copy `qjs.dll` to `targets/quickjs-2025-09-13/qjs.dll` (the consumer's stable
     HintPath) so VS F5 keeps working unchanged.
   - `-t:Oracle` target: build + run `qjsconsumer`, assert exit 0 / `==42`.
   Depends on `ManagedMusl.proj` and the `Chibil.Pal` build.

The existing `qjsconsumer.csproj` (already a normal VS project with a `Chibil.Pal`
ProjectReference) is unchanged; build order is `QuickJsManaged.proj` → F5 consumer.

### D. Secondary scripts (Phase 1 keep-working, Phase 2 convert)

Phase 1 does NOT convert the secondary scripts, but they must not break under the
new output paths. Add **`targets/build/env.sh`** (single source of truth):
```bash
ROOT="${ROOT:-/mnt/d/sandbox/chibil}"
CH="dotnet $ROOT/build/bin/Chibil/debug/chibil.dll"
LINK="dotnet $ROOT/build/bin/ChibilLink/debug/chibil-link.dll"
PAL="$ROOT/build/bin/Chibil.Pal/release/Chibil.Pal.dll"
OBJIMPORTS="dotnet $ROOT/build/bin/objimports/release/objimports.dll"
```
Update each still-shell script to `source "$ROOT/targets/build/env.sh"` in place of
its `CH=/LINK=/PAL=` lines. Phase 2 ports these to `.proj` and deletes `env.sh`.

### E. Cleanup & docs

- Delete scattered `bin/`, `obj/`, stray `.vs/` (all git-ignored).
- `+/build/` in root `.gitignore`.
- Update `CompileQuickJS.md`, `CompileBash.md`, `CompileMicroPython.md`,
  `DebugAndConsume.md`, and the consumer `README.md` to the new commands
  (`dotnet build targets/quickjs/QuickJsManaged.proj -t:Oracle`) and `build/` paths.

## File structure (new/changed)

```
Directory.Build.props                              (new — output → build/)
.gitignore                                         (edit — +/build/)
tools/chibil-build/Chibil.Build.Tasks.csproj       (new — task assembly)
tools/chibil-build/ChibilCompileTask.cs            (new)
tools/chibil-build/ChibilLinkTask.cs               (new)
tools/chibil-build/build/Chibil.Build.props        (new — $(ChibilDll) etc.)
tools/chibil-build/build/Chibil.Build.targets      (new — UsingTask + helpers)
targets/managed-musl/ManagedMusl.proj              (new — replaces build-managed-musl.sh)
targets/quickjs/QuickJsManaged.proj                (new — replaces qjs-on-musl-api.sh)
targets/build/env.sh                               (new — secondary-script tool paths)
targets/build/*.sh (secondary)                     (edit — source env.sh)
Compile*.md / DebugAndConsume.md / consumer README (edit — new commands/paths)
```

## Testing strategy

- **Gating spike — DONE 2026-06-06, PASSED.** Replicated layer-3b entirely on
  **Windows**: chibil compiled 13 real musl TUs (mallocng/mmap/errno/memcpy…),
  chibil-link linked them with `--bind` to the managed PAL, and the program ran →
  **exit 42**. No WSL. The "toolchain runs natively on Windows" premise is
  confirmed, so the WSL-fallback risk below is not expected to trigger.
- **Tasks unit-ish:** a tiny fixture `.proj` compiling 2 TUs + linking + running,
  asserting incremental (second build is a no-op) and that a failing TU under
  `ContinueOnError` is skipped+logged.
- **Primary E2E on Windows:** `dotnet build targets/quickjs/QuickJsManaged.proj
  -t:Oracle` → `JS_Eval("40+2")==42`, no WSL.
- **Regression:** `dotnet test` slice still green; VS F5 of the consumer still runs.
- **Secondary scripts:** one still-shell script (e.g. `musl-layer3b-malloc.sh`)
  runs green via `env.sh` under WSL.

## Risks

- **chibil-on-Windows TU compile** — linchpin; **validated 2026-06-06** (layer-3b
  spike → exit 42 on Windows, no WSL). Residual mitigation if a specific TU later
  misbehaves on Windows: that `.proj` can fall back to invoking chibil under WSL
  via `<Exec>` (keeps centralized output; loses the no-WSL win for that port).
- **Task perf** — 1000+ musl TUs. Mitigation: in-process invocation + parallelism;
  incremental skip on rebuild.
- **scenarios exception** — `CoffEmitterTests` keeps local `bin/obj`. Accepted.
- **Two-step VS flow** — qjs.dll must be built by `QuickJsManaged.proj` before F5.
  Documented; consumer HintPath stays stable via the copy step.

## Out of scope (Phase 2+)

Converting bash-5.3 / micropython / test262 / layer-spike / quickjs-* variant
scripts; the Linux-only native-libc oracle; renaming the confusingly-named
`targets/build/` infra dir.
