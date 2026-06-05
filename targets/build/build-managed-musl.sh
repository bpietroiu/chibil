#!/bin/bash
# Build a comprehensive "managed musl" object set: compile every compilable .c in
# the compute/io subsystems QuickJS needs, to /tmp/managed_musl/*.obj. Incremental.
# Failures (the known residuals) are skipped — chibil-link only pulls what it needs.
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
M=$ROOT/targets/musl-1.2.6
INCS="-I$ROOT/targets/build/musl-compat -Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 -include $ROOT/targets/build/musl-chibil-compat.h $INCS"
OUT=/tmp/managed_musl; mkdir -p "$OUT"
cd "$M" || exit 1

# subsystems needed for compute + stdio + heap (no net/thread/process/signal/ldso).
DIRS="string ctype errno stdlib stdio math malloc mman internal locale multibyte prng exit env misc time fenv complex fcntl unistd stat dirent select signal process random linux"
dep="$ROOT/chibil/bin/Debug/net10.0/chibil.dll $ROOT/targets/build/musl-chibil-compat.h $ROOT/targets/build/musl-compat/syscall_arch.h"
newest=$(ls -t $dep 2>/dev/null | head -1)
ok=0; fail=0; cached=0
for d in $DIRS; do
    while read -r tu; do
        [ -z "$tu" ] && continue
        o="$OUT/$(echo "$tu" | tr '/' '_' | sed 's/\.c$/.obj/')"
        if [ -f "$o" ] && [ "$o" -nt "$tu" ] && { [ -z "$newest" ] || [ "$o" -nt "$newest" ]; }; then
            cached=$((cached+1)); ok=$((ok+1)); continue
        fi
        if $CH $CFLAGS "$tu" -o "$o" > "$o.log" 2>&1; then ok=$((ok+1)); else rm -f "$o"; fail=$((fail+1)); fi
    done < <(find "src/$d" -name '*.c' 2>/dev/null)
done
echo "managed musl objs: $ok ok ($cached cached) / $fail fail -> $OUT"
ls "$OUT"/*.obj 2>/dev/null | wc -l | xargs echo "obj count:"
