#!/usr/bin/env bash
# ============================================================================
#  Self-contained C -> runnable .NET assembly on Linux, with NO Windows tools.
#
#  Compiles each .c with chibil's CoreCLR target (pure-MSIL managed COFF .obj),
#  then links the objects with the in-house linker (chibil-link) into a single
#  pure-MSIL PE + runtimeconfig.json that runs on CoreCLR:  dotnet <out>.dll
#
#  Usage:
#    build.sh <out.dll> <src1.c> [src2.c ...] [-l<lib> ...]
#
#  Examples:
#    build.sh app.dll fib.c
#    build.sh app.dll main.c greet.c -lc
#
#  Prerequisites:
#    - .NET SDK (net10.0) on PATH:  dotnet --version
#    - PureDOOM submodule etc. are NOT needed for this script.
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

OUT=""
SRCS=()
LIBS=()
for a in "$@"; do
  case "$a" in
    -l*)   LIBS+=("$a") ;;
    *.dll) OUT="$a" ;;
    *.c)   SRCS+=("$a") ;;
    *)     echo "build.sh: ignoring unrecognized argument '$a'" >&2 ;;
  esac
done

if [[ -z "$OUT" ]]; then
  echo "usage: build.sh <out.dll> <src1.c> [src2.c ...] [-l<lib> ...]" >&2
  exit 1
fi
if [[ ${#SRCS[@]} -eq 0 ]]; then
  echo "build.sh: no .c source files given" >&2
  exit 1
fi

OBJS=()
cleanup() { rm -f "${OBJS[@]}"; }
trap cleanup EXIT

for s in "${SRCS[@]}"; do
  o="$(mktemp --suffix=.obj)"
  OBJS+=("$o")
  echo "[chibil]  $s -> $o"
  dotnet run -c Release --project "$ROOT/chibil" -- \
      --target=coreclr -cc1 -cc1-input "$s" -cc1-output "$o"
done

echo "[chibil-link]  ${OBJS[*]} -> $OUT  ${LIBS[*]:-}"
dotnet run -c Release --project "$ROOT/tools/chibil-link" -- \
    -o "$OUT" "${LIBS[@]}" "${OBJS[@]}"

echo "built $OUT  (run with: dotnet $OUT)"
