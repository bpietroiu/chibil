#!/bin/bash
# L5 attempt: link QuickJS's engine against the MANAGED musl (+ PAL), no native libc.
# Compile the engine TUs with the facade (--export-api) + the musl shim, then link
# against /tmp/managed_musl + Chibil.Pal and report the unresolved surface.
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
Q=$ROOT/targets/quickjs-2025-09-13
MUSL=$ROOT/targets/musl-1.2.6
PAL=$(ls "$ROOT/targets/build/Chibil.Pal/bin/Release/net10.0/Chibil.Pal.dll")
MM=/tmp/managed_musl
OUT=/tmp/qjs_musl; mkdir -p "$OUT"

MUSLINC="-I$ROOT/targets/build/musl-compat -I$MUSL/arch/x86_64 -I$MUSL/arch/generic -I$MUSL/src/include -I$MUSL/src/internal -I$MUSL/include -I$MUSL/obj/include"
# QuickJS: facade + EMSCRIPTEN config + both compat layers (qjs + musl arch shadows).
CFLAGS="-c --target=coreclr -nostdinc -mlp64 --export-api=quickjs.h \
  -include $ROOT/targets/build/quickjs-chibil-compat.h \
  -I$ROOT/targets/build/qjs-compat -I$Q $MUSLINC \
  -DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION=\"2025-09-13\""

cd "$Q" || exit 1
echo "=== compile QuickJS engine TUs (facade + managed-musl headers) ==="
qjsobjs=()
for s in cutils dtoa libregexp libunicode quickjs; do
    if $CH $CFLAGS "$s.c" -o "$OUT/$s.obj" > "$OUT/$s.log" 2>&1; then qjsobjs+=("$OUT/$s.obj"); else echo "FAIL $s"; tail -4 "$OUT/$s.log"; fi
done

$CH -c --target=coreclr -nostdinc -mlp64 -include $ROOT/targets/build/musl-chibil-compat.h $MUSLINC \
   "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3

echo "=== link qjs engine + managed musl + PAL (--print-imports) ==="
$LINK --print-imports -shared -o "$OUT/qjs.dll" "${qjsobjs[@]}" "$MM"/*.obj "$OUT"/pal-shim.obj \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | tail -25
echo "link exit: ${PIPESTATUS[0]}"