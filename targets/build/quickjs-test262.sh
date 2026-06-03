#!/bin/bash
# Build run-test262 (run-test262.c + the QuickJS lib objs) to MSIL with chibil.
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

echo "=== compiling run-test262.c ==="
$CH $CFLAGS run-test262.c -o "$OBJDIR/run-test262.obj" || { echo "COMPILE FAILED"; exit 1; }
# Lib objs (everything except qjs.c / qjs.obj)
LIBOBJS="cutils dtoa libregexp libunicode quickjs quickjs-libc"
objs=("$OBJDIR/run-test262.obj"); for s in $LIBOBJS; do objs+=("$OBJDIR/$s.obj"); done
echo "=== linking run-test262.dll ==="
$LINK -g -o run-test262.dll -lc -lm "${objs[@]}" 2>&1 | tail -6
echo "link exit: ${PIPESTATUS[0]}"; ls -la run-test262.dll 2>/dev/null
