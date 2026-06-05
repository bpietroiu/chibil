#!/bin/bash
# Layer 1 floor: compile a PURE musl TU (strlen) + a tiny main that calls it,
# link with chibil-link, and RUN it. Pure functions have no unresolved externs,
# so this needs NO PAL — the minimal proof that managed musl links + runs.
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
M=$ROOT/targets/musl-1.2.6
INCS="-I$ROOT/targets/build/musl-compat -Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 -include $ROOT/targets/build/musl-chibil-compat.h $INCS"
OUT=/tmp/musl_layer1; mkdir -p "$OUT"
cd "$M" || exit 1

echo "=== compile musl src/string/strlen.c ==="
$CH $CFLAGS src/string/strlen.c -o "$OUT/strlen.obj" 2>&1 | tail -3 || { echo "strlen.c FAILED"; exit 1; }

echo "=== compile main (calls strlen) ==="
cat > "$OUT/main.c" <<'EOF'
typedef unsigned long size_t;
size_t strlen(const char *);
int main(void){ return (int)strlen("hello"); }   /* expect 5 */
EOF
$CH -c --target=coreclr -mlp64 "$OUT/main.c" -o "$OUT/main.obj" 2>&1 | tail -3 || { echo "main.c FAILED"; exit 1; }

echo "=== link (no -l, no --bind: strlen is pure) ==="
$LINK -o "$OUT/l1.dll" "$OUT/main.obj" "$OUT/strlen.obj" 2>&1 | tail -5
echo "link exit: ${PIPESTATUS[0]}"

echo "=== run ==="
dotnet "$OUT/l1.dll"
echo "exit=$? (expect 5)"