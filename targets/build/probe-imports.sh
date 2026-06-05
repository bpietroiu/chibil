#!/bin/bash
# Diagnostic: link with -lc so unresolved FUNCTIONS become P/Invoke imports
# (instead of a fatal error on the first one), then --print-imports lists the
# whole native frontier at once. NOT part of the real build.
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
# PAL from env.sh (build/bin/Chibil.Pal/release/Chibil.Pal.dll)
MM=/tmp/managed_musl
OUT=/tmp/qjs_musl
$LINK --print-imports -shared -o "$OUT/qjs_probe.dll" \
    "$OUT"/cutils.obj "$OUT"/dtoa.obj "$OUT"/libregexp.obj "$OUT"/libunicode.obj "$OUT"/quickjs.obj \
    "$MM"/*.obj "$OUT"/pal-shim.obj \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" -lc 2>&1
echo "exit: ${PIPESTATUS[0]}"
