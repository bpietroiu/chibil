#!/bin/bash
# Build MicroPython against the MANAGED musl + PAL (no native libc) into a RUNNABLE
# micropython.dll under build/bin/mpy (Windows-visible). Mirrors qjs-on-musl-api.sh.
# Run: wsl bash .../mpy-on-musl.sh
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
M=$ROOT/targets/musl-1.2.6
MUSLINC="-I$ROOT/targets/build/musl-compat -I$M/arch/x86_64 -I$M/arch/generic -I$M/src/include -I$M/src/internal -I$M/include -I$M/obj/include"
MM=$ROOT/build/managed-musl
OUT=$ROOT/build/bin/mpy; mkdir -p "$OUT"   # Windows-visible at D:\sandbox\chibil\build\bin\mpy
PORT=$ROOT/targets/micropython/ports/minimal

# 1. regenerate the MicroPython source list + compile the 138 TUs (reuse the harness).
: > /tmp/mpy_srcs.txt
( cd "$PORT" && find build -name '*.o' | while read -r o; do p="${o#build/}"; p="${p%.o}"
  case "$p" in py/*|shared/*|lib/*|extmod/*|drivers/*) s="../../$p.c";; *) s="$p.c";; esac
  [ -f "$s" ] && echo "$s" >> /tmp/mpy_srcs.txt; done )
echo "mpy srcs: $(wc -l < /tmp/mpy_srcs.txt)"
bash "$ROOT/targets/build/micropython-chibil.sh" 2>&1 | grep -E 'compiled OK|FAILED'
mpyobjs=$(cat /tmp/mpy_objs.txt)

# 2. compile the PAL shim against musl headers.
$CH -c --target=coreclr -nostdinc -mlp64 -include $ROOT/targets/build/musl-chibil-compat.h $MUSLINC \
   "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3

# 3. link a RUNNABLE micropython.dll (no -shared: main is the entry point) against
#    managed musl (minus oldmalloc) + pal-shim + PAL.
mmobjs=$(ls "$MM"/*.obj | grep -v 'oldmalloc')
echo "=== link runnable MicroPython on managed musl ==="
$LINK -g -o "$OUT/micropython.dll" $mpyobjs $mmobjs "$OUT/pal-shim.obj" \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | grep -vE "alias '" | tail -8
echo "link exit: ${PIPESTATUS[0]}"
cp "$PAL" "$OUT/"                       # Chibil.Pal.dll beside the app
ls -la "$OUT/micropython.dll" "$OUT/Chibil.Pal.dll" 2>/dev/null
