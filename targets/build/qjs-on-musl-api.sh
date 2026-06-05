#!/bin/bash
# Build qjs.dll as the --export-api facade, linked against MANAGED musl + the managed
# PAL (no native libc), then run the C# consumer that evaluates "40+2" and asserts 42.
# This is the managed-PAL analog of quickjs-api-run.sh.
# Run under WSL: wsl bash /mnt/d/sandbox/chibil/targets/build/qjs-on-musl-api.sh
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
# Facade build: --export-api=quickjs.h emits the quickjs.Api class; managed-musl headers.
CFLAGS="-c --target=coreclr -nostdinc -mlp64 --export-api=quickjs.h \
  -include $ROOT/targets/build/quickjs-chibil-compat.h \
  -I$ROOT/targets/build/qjs-compat -I$Q $MUSLINC \
  -DEMSCRIPTEN -D_GNU_SOURCE -DCONFIG_VERSION=\"2025-09-13\""

cd "$Q" || exit 1
echo "=== compile QuickJS engine TUs (facade + managed-musl headers) ==="
qjsobjs=()
for s in cutils dtoa libregexp libunicode quickjs; do
    if $CH $CFLAGS "$s.c" -o "$OUT/api_$s.obj" > "$OUT/api_$s.log" 2>&1; then qjsobjs+=("$OUT/api_$s.obj"); else echo "FAIL $s"; tail -6 "$OUT/api_$s.log"; exit 1; fi
done

$CH -c --target=coreclr -nostdinc -mlp64 -include $ROOT/targets/build/musl-chibil-compat.h $MUSLINC \
   "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3

echo "=== link facade qjs.dll against managed musl + PAL ==="
# Exclude oldmalloc: both oldmalloc and mallocng define a STRONG __libc_malloc_impl;
# with "last def wins" the link would pick oldmalloc, whose __expand_heap/__mmap path
# the managed PAL doesn't honour cleanly. mallocng is the proven allocator (layer3b).
mmobjs=$(ls "$MM"/*.obj | grep -v 'oldmalloc')
$LINK -g -shared -o "$OUT/qjs.dll" "${qjsobjs[@]}" $mmobjs "$OUT"/pal-shim.obj \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | tail -8
echo "link exit: ${PIPESTATUS[0]}"
ls -la "$OUT/qjs.dll" 2>/dev/null || exit 1

echo "=== run C# consumer (managed qjs.dll + Chibil.Pal.dll) ==="
CON=$ROOT/targets/quickjs-api-consumer
cp "$OUT/qjs.dll" "$Q/qjs.dll"          # the consumer references ../quickjs-2025-09-13/qjs.dll
cd "$CON" || exit 1
# Build with PalRun=1 so the csproj references Chibil.Pal (-> deps.json), letting the
# loader resolve the assembly qjs.dll's <Module> .cctor calls into. Then drop the
# fresh managed qjs.dll + Chibil.Pal.dll beside the consumer output.
dotnet build -c Debug --nologo -v q -p:PalRun=1 -p:PalDll="$PAL" 2>&1 | tail -3
OUTDIR=$(ls -d "$CON"/bin/Debug/net10.0 2>/dev/null | head -1)
cp "$PAL" "$OUTDIR/" 2>/dev/null
cp "$OUT/qjs.dll" "$OUTDIR/" 2>/dev/null
dotnet "$OUTDIR/qjsconsumer.dll" 2>&1 | head -40
echo "consumer exit: ${PIPESTATUS[0]}"
