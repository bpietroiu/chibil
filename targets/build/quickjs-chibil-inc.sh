#!/bin/bash
# Incremental QuickJS rebuild: recompile ONLY the named TU(s) and relink qjs.dll.
#   wsl bash .../quickjs-chibil-inc.sh quickjs.c
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
M=$ROOT/targets/musl-1.2.6
MUSLINC="-I$M/arch/x86_64 -I$M/arch/generic -I$M/obj/include -I$M/include"
SRC=$ROOT/targets/quickjs-2025-09-13
cd "$SRC" || exit 1
INCS="-I$ROOT/targets/build/qjs-compat -I. $MUSLINC"
DEFS="-DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION=\"2025-09-13\""
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -include $ROOT/targets/build/quickjs-chibil-compat.h $INCS $DEFS"
OBJDIR=/tmp/qjs_obj
ALL="cutils.c dtoa.c libregexp.c libunicode.c quickjs.c quickjs-libc.c qjs.c"

for src in "$@"; do
    echo "recompiling $src"
    $CH $CFLAGS "$src" -o "$OBJDIR/${src%.c}.obj" || { echo "COMPILE FAILED: $src"; exit 1; }
done
objs=(); for s in $ALL; do objs+=("$OBJDIR/${s%.c}.obj"); done
echo "=== relinking qjs.dll ==="
$LINK -g -o qjs.dll -lc -lm "${objs[@]}" 2>&1 | tail -3
echo "link exit: ${PIPESTATUS[0]}"; ls -la qjs.dll
