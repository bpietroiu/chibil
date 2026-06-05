#!/bin/bash
# Layer 2 seam proof: a C program calls __chibil_syscall(SYS_write,...) directly;
# chibil-link --bind routes it to the managed Chibil.Pal.Syscall, which writes to
# stdout. Proves the seam: managed musl's bottom edge reaches managed code.
set -u
ROOT=/mnt/d/sandbox/chibil
source "$ROOT/targets/build/env.sh"
PALDIR=$ROOT/targets/build/Chibil.Pal
OUT=/tmp/musl_layer2; mkdir -p "$OUT"

echo "=== build Chibil.Pal.dll ==="
dotnet build "$PALDIR/Chibil.Pal.csproj" -c Release -v q --nologo 2>&1 | grep -E "error|Build succeeded" | head -1
# PAL from env.sh

echo "=== compile program (calls __chibil_syscall directly) ==="
cat > "$OUT/main.c" <<'EOF'
extern long __chibil_syscall(long, long, long, long, long, long, long);
int main(void){
    const char *m = "hi from managed musl, via the PAL seam!\n";
    long n = 0; while (m[n]) n++;
    __chibil_syscall(1, 1, (long)m, n, 0, 0, 0);   /* SYS_write=1, fd=1=stdout */
    return 0;
}
EOF
$CH -c --target=coreclr -mlp64 "$OUT/main.c" -o "$OUT/main.obj" 2>&1 | tail -3 || { echo "compile FAIL"; exit 1; }

echo "=== link with --bind=__chibil_syscall=Chibil.Pal.Syscall ==="
$LINK -o "$OUT/l2.dll" "$OUT/main.obj" \
    --bind=__chibil_syscall=Chibil.Pal.Syscall -r "$PAL" 2>&1 | tail -5
echo "link exit: ${PIPESTATUS[0]}"

echo "=== place Chibil.Pal.dll beside the app + run ==="
cp "$PAL" "$OUT/"
dotnet "$OUT/l2.dll"
echo "exit=$?"