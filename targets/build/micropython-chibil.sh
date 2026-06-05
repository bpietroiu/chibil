#!/bin/bash
# Compile MicroPython (minimal port) to MSIL with chibil, link to libc.
# Run under WSL: wsl bash /mnt/d/sandbox/chibil/targets/micropython/chibil-build.sh
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
M=$ROOT/targets/musl-1.2.6
MUSLINC="-I$M/arch/x86_64 -I$M/arch/generic -I$M/obj/include -I$M/include"
PORT=$ROOT/targets/micropython/ports/minimal
cd "$PORT" || exit 1

INCS="-I. -I../.. -Ibuild $MUSLINC"
# NLR via setjmp (no native asm in MSIL); switch-based VM dispatch (no computed goto).
DEFS="-DMICROPY_ROM_TEXT_COMPRESSION=1 -DMICROPY_NLR_SETJMP=1 -DMICROPY_OPT_COMPUTED_GOTO=0"
# -include chibil-compat.h : portable fallbacks for GCC builtins chibil lacks.
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -include chibil-compat.h $INCS $DEFS"

OBJDIR=/tmp/mpy_obj
mkdir -p "$OBJDIR"
: > /tmp/mpy_objs.txt
ok=0; fail=0; : > /tmp/mpy_failed.txt
objs=()

while read -r src; do
    [ -z "$src" ] && continue
    obj="$OBJDIR/$(echo "$src" | sed 's@[./]@_@g').obj"
    if $CH $CFLAGS "$src" -o "$obj" > "$obj.log" 2>&1; then
        ok=$((ok+1)); echo "$obj" >> /tmp/mpy_objs.txt; objs+=("$obj")
    else
        fail=$((fail+1)); echo "$src" >> /tmp/mpy_failed.txt
    fi
done < /tmp/mpy_srcs.txt

echo "=== compiled OK: $ok   FAILED: $fail ==="
if [ "$fail" -gt 0 ]; then
    echo "--- failed sources ---"; cat /tmp/mpy_failed.txt
    exit 1
fi

echo "=== linking micropython.dll (chibil-link) ==="
$LINK -g -o micropython.dll -lc "${objs[@]}" 2>&1 | tail -30
echo "link exit: ${PIPESTATUS[0]}"
ls -la micropython.dll 2>/dev/null
