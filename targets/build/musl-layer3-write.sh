#!/bin/bash
# Layer 3a: link REAL musl src/unistd/write.c (not a direct __chibil_syscall call).
# write() uses the cancellable syscall_cp path -> exercises the TCB. See what externs
# it pulls in, then link+run against the PAL.
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
M=$ROOT/targets/musl-1.2.6
# PAL from env.sh
INCS="-I$ROOT/targets/build/musl-compat -Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 -include $ROOT/targets/build/musl-chibil-compat.h $INCS"
OUT=/tmp/musl_layer3; mkdir -p "$OUT"
cd "$M" || exit 1

# write()'s musl dependency closure: write.c -> __syscall_ret -> __errno_location
# (-> __pthread_self, which inlines to the PAL-bound __get_tp).
MUSL_TUS="src/unistd/write.c src/internal/syscall_ret.c src/errno/__errno_location.c"
muslobjs=()
echo "=== compile musl TUs: $MUSL_TUS ==="
for tu in $MUSL_TUS; do
    o="$OUT/$(basename "$tu" .c).obj"
    $CH $CFLAGS "$tu" -o "$o" 2>&1 | tail -3 || { echo "$tu COMPILE FAIL"; exit 1; }
    muslobjs+=("$o")
done

cat > "$OUT/main.c" <<'EOF'
typedef unsigned long size_t;
typedef long ssize_t;
ssize_t write(int, const void *, size_t);
int main(void){ const char *m="real musl write()!\n"; long n=0; while(m[n]) n++; write(1,m,n); return 0; }
EOF
$CH -c --target=coreclr -mlp64 "$OUT/main.c" -o "$OUT/main.obj" 2>&1 | tail -3 || { echo "main FAIL"; exit 1; }

echo "=== compile pal-shim.c (__syscall_cp -> direct syscall) ==="
$CH -c --target=coreclr -mlp64 "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3 || { echo "pal-shim FAIL"; exit 1; }

echo "=== link musl closure + pal-shim + main (print-imports, no -l) ==="
$LINK --print-imports -o "$OUT/l3.dll" "$OUT/main.obj" "${muslobjs[@]}" "$OUT/pal-shim.obj" \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | tail -20
echo "link exit: ${PIPESTATUS[0]}"

if [ -f "$OUT/l3.dll" ]; then
    echo "=== run ==="; cp "$PAL" "$OUT/"; dotnet "$OUT/l3.dll"; echo "exit=$?"
fi