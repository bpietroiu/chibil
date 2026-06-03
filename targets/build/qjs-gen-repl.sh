#!/bin/bash
# Generate repl.c (the precompiled-bytecode REPL) for the QuickJS build.
#
# repl.c is normally produced by QuickJS's own `qjsc` compiler from repl.js. We
# don't ship a native qjsc, so we BOOTSTRAP one with chibil itself: compile the
# QuickJS library TUs + qjsc.c to MSIL, link a qjsc.dll, and run it to emit
# repl.c. The result defines qjsc_repl / qjsc_repl_size, which qjs.c references —
# so including repl.c in the qjs link turns those two symbols from unresolved
# "data imports" into real definitions (verify with chibil-link --print-imports).
#
# Idempotent: skips regeneration if repl.c already exists and is newer than
# repl.js. Invoked by quickjs-chibil.sh and quickjs-api.sh before they compile.
# Run standalone: wsl bash /mnt/d/sandbox/chibil/targets/build/qjs-gen-repl.sh
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
M=$ROOT/targets/musl-1.2.6
MUSLINC="-I$M/arch/x86_64 -I$M/arch/generic -I$M/obj/include -I$M/include"
SRC=$ROOT/targets/quickjs-2025-09-13
cd "$SRC" || exit 1

# Already current? (repl.c exists and is at least as new as its source repl.js)
if [ -f repl.c ] && [ repl.c -nt repl.js ]; then
    echo "=== repl.c is up to date — skipping qjsc bootstrap ==="
    exit 0
fi

# Plain CFLAGS — qjsc is a build tool, NOT part of the API facade, so no
# --export-api here. repl.c is just data, generated identically either way.
INCS="-I$ROOT/targets/build/qjs-compat -I. $MUSLINC"
DEFS="-DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION=\"2025-09-13\""
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -include $ROOT/targets/build/quickjs-chibil-compat.h $INCS $DEFS"
OBJDIR=/tmp/qjs_repl_boot
mkdir -p "$OBJDIR"

echo "=== bootstrapping qjsc (chibil) to generate repl.c ==="
objs=()
for src in cutils dtoa libregexp libunicode quickjs quickjs-libc qjsc; do
    obj="$OBJDIR/$src.obj"
    if [ ! -f "$obj" ] || [ "$SRC/$src.c" -nt "$obj" ]; then
        $CH $CFLAGS "$src.c" -o "$obj" > "$obj.log" 2>&1 || { echo "COMPILE FAIL: $src"; tail -6 "$obj.log"; exit 1; }
    fi
    objs+=("$obj")
done

$LINK -o "$OBJDIR/qjsc.dll" -lc -lm "${objs[@]}" 2>&1 | tail -3
[ "${PIPESTATUS[0]}" -eq 0 ] || { echo "qjsc link FAILED"; exit 1; }

# Generate repl.c from repl.js (-c: output C source; -m: module).
rm -f repl.c
dotnet "$OBJDIR/qjsc.dll" -c -o repl.c -m repl.js 2>&1 | tail -3
[ -f repl.c ] || { echo "qjsc did NOT produce repl.c"; exit 1; }
echo "=== generated repl.c ($(wc -c < repl.c) bytes) ==="
