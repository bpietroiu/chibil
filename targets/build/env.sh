#!/bin/bash
# Single source of truth for the chibil toolchain DLL locations under the
# centralized artifacts output (build/bin/<Project>/<config>/). Source this AFTER
# setting ROOT (defaults to the WSL repo mount).
#
# The cross-platform managed builds are MSBuild .proj now (build.proj, ManagedMusl,
# QuickJsManaged, Layers). The scripts that still source this file are the ones that
# genuinely need WSL/Linux: native-libc QuickJS builds (link against libc.so.6 via
# P/Invoke), the test262 runner, the qjsc repl generator, diagnostics
# (probe-imports), and the blocked MicroPython port. They cannot become Windows
# .proj, so env.sh stays.
ROOT="${ROOT:-/mnt/d/sandbox/chibil}"
CH="dotnet $ROOT/build/bin/Chibil/debug/chibil.dll"
LINK="dotnet $ROOT/build/bin/ChibilLink/debug/chibil-link.dll"
PAL="$ROOT/build/bin/Chibil.Pal/release/Chibil.Pal.dll"
OBJIMPORTS="dotnet $ROOT/build/bin/objimports/release/objimports.dll"
