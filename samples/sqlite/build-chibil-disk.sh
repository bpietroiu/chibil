#!/usr/bin/env bash
# ============================================================================
#  Build the on-disk SQLite assembly with chibil — no native sqlite3 binary,
#  no MSVC. Compiles the 4-file disk set (amalgamation + platform shim +
#  disk VFS + disk harness) with chibil's CoreCLR target, then links them
#  into a single PE with the in-house linker (chibil-link), passing the
#  --pinvoke name=lib map so native P/Invokes resolve to the right module.
#
#  Usage:   bash build-chibil-disk.sh [out.dll]   (default: app-disk.dll here)
#  Run:     dotnet <out.dll>                       (exit code 55 == on-disk CRUD)
#           creates sp3.db in the working directory
#
#  Requires the .NET 10 SDK on PATH (dotnet --version).
# ============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HERE/app-disk.dll}"

CH="$ROOT/chibil"
LINK="$ROOT/tools/chibil-link"

DEFS=(-DSQLITE_OS_OTHER=1 -DSQLITE_THREADSAFE=0 -DSQLITE_TEMP_STORE=3
      -DSQLITE_ENABLE_MEMSYS5=1 -DSQLITE_ZERO_MALLOC=1
      -DSQLITE_OMIT_LOADEXTENSION=1 -DSQLITE_OMIT_AUTOINIT=1)
INC=(-I"$HERE/include" -I"$HERE/vendor")

PINVOKE="open=c,pread=c,pwrite=c,ftruncate=c,fsync=c,close=c,unlink=c,access=c,lseek=c,CreateFileA=kernel32,ReadFile=kernel32,WriteFile=kernel32,SetFilePointerEx=kernel32,SetEndOfFile=kernel32,FlushFileBuffers=kernel32,CloseHandle=kernel32,DeleteFileA=kernel32,GetFileSizeEx=kernel32,GetFileAttributesA=kernel32,GetLastError=kernel32,fcntl=c,usleep=c,LockFileEx=kernel32,UnlockFileEx=kernel32,Sleep=kernel32"

OBJS=()
cleanup(){ rm -f "${OBJS[@]}"; }
trap cleanup EXIT

for src in "$HERE/vendor/sqlite3.c" "$HERE/sqlite_shim.c" "$HERE/sqlite_vfs_disk.c" "$HERE/main_disk.c"; do
  o="$(mktemp --suffix=.obj)"; OBJS+=("$o")
  echo "[chibil]  $(basename "$src")"
  dotnet run -c Release --project "$CH" -- \
    --target=coreclr "${DEFS[@]}" "${INC[@]}" -cc1 -cc1-input "$src" -cc1-output "$o"
done

echo "[chibil-link]  -> $OUT"
dotnet run -c Release --project "$LINK" -- -o "$OUT" --pinvoke="$PINVOKE" "${OBJS[@]}"

echo "built $OUT  (run in an empty dir: dotnet $OUT ; exit 55 == on-disk CRUD; creates sp3.db)"
