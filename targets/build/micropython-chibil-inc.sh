#!/bin/bash
# Incremental MicroPython rebuild: recompile ONLY the named source file(s) and
# relink micropython.dll from the cached objects in /tmp/mpy_obj. Pass basenames
# (e.g. parse.c gc.c); each is resolved to its full path from /tmp/mpy_srcs.txt so
# the .obj name matches what the full harness produced.
#   wsl bash /mnt/d/sandbox/chibil/targets/build/micropython-chibil-inc.sh parse.c
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
M=$ROOT/targets/musl-1.2.6
MUSLINC="-I$M/arch/x86_64 -I$M/arch/generic -I$M/obj/include -I$M/include"
PORT=$ROOT/targets/micropython/ports/minimal
cd "$PORT" || exit 1
INCS="-I. -I../.. -Ibuild $MUSLINC"
DEFS="-DMICROPY_ROM_TEXT_COMPRESSION=1 -DMICROPY_NLR_SETJMP=1 -DMICROPY_OPT_COMPUTED_GOTO=0"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -include chibil-compat.h $INCS $DEFS"
OBJDIR=/tmp/mpy_obj

if [ ! -f /tmp/mpy_srcs.txt ] || [ ! -f /tmp/mpy_objs.txt ]; then
    echo "missing /tmp/mpy_srcs.txt or /tmp/mpy_objs.txt — run the full harness first."; exit 1
fi

for name in "$@"; do
    src=$(grep -E "(^|/)$name\$" /tmp/mpy_srcs.txt | head -1)
    if [ -z "$src" ]; then echo "source '$name' not found in /tmp/mpy_srcs.txt"; exit 1; fi
    obj="$OBJDIR/$(echo "$src" | sed 's@[./]@_@g').obj"
    echo "recompiling $src -> $obj"
    if ! $CH $CFLAGS "$src" -o "$obj"; then echo "COMPILE FAILED: $src"; exit 1; fi
done

echo "=== relinking micropython.dll ==="
mapfile -t objs < /tmp/mpy_objs.txt
$LINK -g -o micropython.dll -lc "${objs[@]}" 2>&1 | tail -5
echo "link exit: ${PIPESTATUS[0]}"
ls -la micropython.dll
