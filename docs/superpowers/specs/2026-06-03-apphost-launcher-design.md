# chibil-link native apphost launcher — design

## Goal

For executable (non-`-shared`) link targets, chibil-link should emit a native
launcher next to the managed assembly — `foo.exe` on Windows, `foo` on Linux —
by copying and patching the .NET **apphost** template, exactly as `dotnet build`
does. After linking `foo.dll`, the user can run `./foo` (or `foo.exe`) directly
instead of `dotnet foo.dll`.

## Background: what the apphost is

The apphost is a small prebuilt **native bootstrapper** that ships with the .NET
SDK/runtime. It is *not* native machine code of the user's program. At startup it
locates `hostfxr` → `hostpolicy` → `coreclr`, reads the co-located
`<app>.runtimeconfig.json` to pick the shared framework, then JITs and runs the
managed entry point. `dotnet build` produces the runnable `MyApp.exe`/`MyApp` by
copying this template and writing the managed dll's path into a placeholder slot.

This is **framework-dependent** hosting (the launcher finds the installed shared
runtime). Self-contained bundling and NativeAOT are explicitly out of scope.

## Scope

In scope:
- Emit a native launcher for `!opts.Shared` targets, on Windows and Linux.
- Locate the apphost template from the installed .NET SDK / runtime packs.
- Patch the launcher to bind it to the emitted managed dll.
- `chmod +x` the launcher on Linux.
- Warn-and-skip (never fail the link) when no template can be found.

Out of scope:
- Self-contained / single-file bundling (runtime not embedded).
- NativeAOT (truly-native, no-CLR) compilation.
- macOS support (ad-hoc code signing required there — not handled).
- Windows icon / version-info resource stamping (cosmetic).

## Architecture

A new `AppHostWriter` type in `tools/chibil-link/`, invoked from
`Linker.Run` (`Program.cs:225`) immediately after `WriteRuntimeConfig`, gated on
`!opts.Shared`:

```csharp
File.WriteAllBytes(opts.Output, pe);
WriteRuntimeConfig(opts.Output);
if (!opts.Shared)
    AppHostWriter.TryEmit(opts.Output);   // warn-and-skip on any failure
```

`opts.Shared` is the executable-vs-library gate. A non-shared target without a
valid entry already fails earlier in `LinkPipeline.LinkToBytes`, so reaching this
point implies a valid entry point — no extra entry check is needed here.

### Pipeline (`AppHostWriter.TryEmit(string managedDllPath)`)

1. **Locate** the apphost template (see strategy below). If none found, emit a
   warning to stderr and return — the dll + runtimeconfig remain usable via
   `dotnet foo.dll`. The link still succeeds.
2. **Compute the launcher path**: `<outdir>/<base><exeExt>`, where `<base>` =
   `Path.GetFileNameWithoutExtension(managedDllPath)` and `<exeExt>` = `.exe` on
   Windows, empty on Linux. If that path would equal the managed dll path
   (e.g. Linux `-o foo` with no extension), warn and skip — we cannot have the
   launcher clobber the assembly. (The real builds use `-o foo.dll`.)
3. **Copy** the template to the launcher path.
4. **Patch** the copy (see below).
5. **Finalize**: on Linux, set the execute bit (`chmod 0755`).

### Template location strategy

Resolve `<dotnetRoot>` in order: `DOTNET_ROOT` env var → directory of the
`dotnet` executable found on `PATH`. Then search, first match wins:

- **A — SDK AppHostTemplate** (preferred): `<dotnetRoot>/sdk/<version>/AppHostTemplate/apphost[.exe]`,
  choosing the highest SDK version directory present. This is the exact template
  `dotnet build` uses (via `Microsoft.NET.HostModel`); it already matches the host
  RID (Windows SDK ships `apphost.exe`, Linux ships `apphost`).
- **B — Runtime host pack** (fallback): `<dotnetRoot>/packs/Microsoft.NETCore.App.Host.<rid>/<version>/runtimes/<rid>/native/apphost[.exe]`,
  highest version, where `<rid>` is `win-x64` or `linux-x64` per host platform.

If neither yields a file, return "not found" (→ warn-and-skip).

### Patching

The apphost template embeds a 64-byte ASCII placeholder:

```
c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2
```

(This is the `AppBinaryPathPlaceholder` from `Microsoft.NET.HostModel`.) Patching:

1. Search the copied bytes for the 64-byte placeholder. If absent, warn-and-skip
   (delete the partial copy) — an unrecognized template format.
2. Overwrite from that offset with the UTF-8 bytes of the managed dll's
   **filename** (relative, since launcher and dll are co-located —
   `Path.GetFileName(managedDllPath)`, e.g. `foo.dll`), followed by a NUL
   terminator, then zero-pad the remainder of the 64-byte region.
3. The dll filename is short (well under 64 bytes); if a pathological name
   exceeded 63 bytes, warn-and-skip rather than overflow.

Writing a non-placeholder value into the slot is what marks the apphost "bound";
no separate flag is needed. The console-subsystem template needs no PE subsystem
change.

## Error handling

The governing rule: **emitting a launcher is best-effort and never fails the
link.** Every failure mode — no SDK, no template, unrecognized template, name
clash, name too long, IO error during copy/patch — degrades to a single stderr
warning and a successful link that still produced `foo.dll` +
`foo.runtimeconfig.json`. The locator returns a nullable result (no throw on
"not found"); `TryEmit` wraps the copy/patch in a try/catch that logs and returns.

## Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `AppHostWriter.TryEmit` | Orchestrate locate → copy → patch → finalize; swallow+warn on failure | `AppHostLocator`, `BCL IO` |
| `AppHostLocator.Find` | Resolve `<dotnetRoot>` and return the template path or `null` | `DOTNET_ROOT`, `PATH` |
| `AppHostPatcher.Patch` | Replace the placeholder bytes in a launcher file with the dll filename | — |
| `Linker.Run` hook | Call `TryEmit` for `!opts.Shared` | `AppHostWriter` |

Splitting locate / patch from orchestration keeps each unit testable in isolation
(the locator and patcher are pure-ish functions; only `TryEmit` touches process
spawning in tests).

## Testing

In `tests/Chibil.Tests/CoreClr/` (new `AppHostTests.cs` if `MuslLinkTests.cs` is
already large; otherwise alongside, following the existing convention):

1. **Launcher self-runs (core).** Link a trivial program
   (`int main(){ return 42; }` / a `printf`), then execute the **native
   launcher directly** (`Process.Start("<out>/foo.exe")` on Windows,
   `"<out>/foo"` on Linux) — *without* `dotnet` on the command line — and assert
   the program's exit code / stdout. Proves the launcher found hostfxr, read the
   runtimeconfig, loaded CoreCLR, and ran `main`. Linux variant gated behind the
   existing WSL/CoreCLR test gate.
2. **Placeholder is patched.** After linking, read the launcher bytes; assert the
   64-byte sentinel is gone and the dll filename appears at that offset. Pure file
   assertion — fast, deterministic, runs everywhere.
3. **Shared targets emit no launcher.** Link with `-shared`; assert no
   `foo.exe`/`foo` is created. Guards the gate.
4. **Graceful skip when template missing.** Unit-test `AppHostLocator.Find`
   against an empty fake `DOTNET_ROOT`: assert it returns `null` (does not throw).
   Plus a `TryEmit`-level assertion that a missing template leaves the link
   successful with the dll still emitted. Tests the "never fail the link"
   guarantee without uninstalling the SDK.

## Open risks

- **SDK layout drift**: the `sdk/<v>/AppHostTemplate` path is stable across
  current .NET versions; the runtime-pack fallback covers runtime-only machines.
  Both are best-effort with warn-and-skip, so drift degrades gracefully.
- **`dotnet` not on PATH and `DOTNET_ROOT` unset**: locator returns `null` →
  warn-and-skip. The user can still `dotnet foo.dll`.
