#!/usr/bin/env bash
# ============================================================================
#  Build SQLite as a fully-managed, pure-MSIL .NET assembly with chibil — no
#  native sqlite3 binary, no MSVC. Compiles the vendored amalgamation + the
#  C platform shim + the :memory: harness with chibil's CoreCLR target, then
#  links them into a single PE with the in-house linker (chibil-link).
#
#  Usage:   bash build-chibil.sh [out.dll]      (default: app.dll here)
#  Run:     dotnet <out.dll>                     (exit code 55 == success)
#
#  Requires the .NET 10 SDK on PATH (dotnet --version).
# ============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/app.dll}"

CH="$ROOT/chibil"
LINK="$ROOT/tools/chibil-link"

DEFS=(-DSQLITE_OS_OTHER=1 -DSQLITE_THREADSAFE=0 -DSQLITE_TEMP_STORE=3
      -DSQLITE_ENABLE_MEMSYS5=1 -DSQLITE_ZERO_MALLOC=1
      -DSQLITE_OMIT_LOADEXTENSION=1 -DSQLITE_OMIT_AUTOINIT=1)
INC=(-I"$HERE/include" -I"$HERE/vendor")

OBJS=()
cleanup(){ rm -f "${OBJS[@]}"; }
trap cleanup EXIT

for src in "$HERE/vendor/sqlite3.c" "$HERE/sqlite_shim.c" "$HERE/main.c"; do
  o="$(mktemp --suffix=.obj)"; OBJS+=("$o")
  echo "[chibil]  $(basename "$src")"
  dotnet run -c Release --project "$CH" -- \
    --target=coreclr "${DEFS[@]}" "${INC[@]}" -cc1 -cc1-input "$src" -cc1-output "$o"
done

echo "[chibil-link]  -> $OUT"
dotnet run -c Release --project "$LINK" -- -o "$OUT" "${OBJS[@]}"

echo "built $OUT  (run with: dotnet $OUT ; exit code 55 == :memory: CRUD succeeded)"
