#!/bin/bash
# Single source of truth for the chibil toolchain DLL locations under the
# centralized artifacts output (build/bin/<Project>/<config>/). Source this AFTER
# setting ROOT (defaults to the WSL repo mount). Phase 2 converts these scripts to
# .proj and deletes this file.
ROOT="${ROOT:-/mnt/d/sandbox/chibil}"
CH="dotnet $ROOT/build/bin/Chibil/debug/chibil.dll"
LINK="dotnet $ROOT/build/bin/ChibilLink/debug/chibil-link.dll"
PAL="$ROOT/build/bin/Chibil.Pal/release/Chibil.Pal.dll"
OBJIMPORTS="dotnet $ROOT/build/bin/objimports/release/objimports.dll"
