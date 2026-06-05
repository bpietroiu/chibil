#!/bin/bash
# Layer 3b: bring up malloc (mallocng) on the managed PAL via mmap. Link the malloc
# closure + a malloc/free test, follow unresolved symbols, then run.
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
M=$ROOT/targets/musl-1.2.6
INCS="-I$ROOT/targets/build/musl-compat -Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 -include $ROOT/targets/build/musl-chibil-compat.h $INCS"
OUT=/tmp/musl_layer3b; mkdir -p "$OUT"
cd "$M" || exit 1

echo "=== build Chibil.Pal (with mmap/munmap) ==="
dotnet build "$ROOT/targets/build/Chibil.Pal/Chibil.Pal.csproj" -c Release -v q --nologo 2>&1 | grep -E "error|Build succeeded" | head -1
PAL=$(ls "$ROOT/targets/build/Chibil.Pal/bin/Release/net10.0/Chibil.Pal.dll")

# malloc closure. mallocng/malloc.c defines __libc_malloc_impl (glue.h renames
# `malloc`); the PUBLIC malloc + __libc_malloc come from lite_malloc.c (which
# weak_alias's default_malloc -> malloc). Extend as the linker reports unresolved.
MUSL_TUS="
  src/malloc/mallocng/malloc.c src/malloc/mallocng/free.c
  src/malloc/lite_malloc.c src/malloc/free.c
  src/mman/mmap.c src/mman/munmap.c src/mman/madvise.c src/mman/mprotect.c
  src/internal/syscall_ret.c src/internal/libc.c src/errno/__errno_location.c
  src/string/memset.c src/string/memcpy.c
"
muslobjs=()
echo "=== compile musl TUs ==="
for tu in $MUSL_TUS; do
    o="$OUT/$(echo "$tu" | tr '/' '_' | sed 's/\.c$/.obj/')"
    if $CH $CFLAGS "$tu" -o "$o" > "$o.log" 2>&1; then muslobjs+=("$o"); else echo "COMPILE FAIL: $tu"; tail -4 "$o.log"; fi
done

cat > "$OUT/main.c" <<'EOF'
typedef unsigned long size_t;
void *malloc(size_t); void free(void *);
extern void __chibil_pal_init(void);   /* managed-crt startup (auxv) */
int main(void){
    __chibil_pal_init();
    char *p = (char*)malloc(100);
    for (int i = 0; i < 100; i++) p[i] = (char)i;
    int r = p[42];
    free(p);
    return r;            /* expect 42 */
}
EOF
$CH -c --target=coreclr -mlp64 "$OUT/main.c" -o "$OUT/main.obj" 2>&1 | tail -3
$CH $CFLAGS "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3

echo "=== link (print-imports, no -l) ==="
$LINK --print-imports -o "$OUT/l3b.dll" "$OUT/main.obj" "${muslobjs[@]}" "$OUT/pal-shim.obj" \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | tail -20
echo "link exit: ${PIPESTATUS[0]}"
if [ -f "$OUT/l3b.dll" ]; then echo "=== run ==="; cp "$PAL" "$OUT/"; dotnet "$OUT/l3b.dll"; echo "exit=$? (expect 42)"; fi