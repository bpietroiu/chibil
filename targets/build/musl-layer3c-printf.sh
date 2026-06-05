#!/bin/bash
# Layer 3c: printf to stdout on the managed PAL (stdio FILE + writev). Link the
# printf closure + a main, follow unresolved symbols, then run.
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
LINK="dotnet $ROOT/tools/chibil-link/bin/Debug/net10.0/chibil-link.dll"
M=$ROOT/targets/musl-1.2.6
INCS="-I$ROOT/targets/build/musl-compat -Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 -include $ROOT/targets/build/musl-chibil-compat.h $INCS"
OUT=/tmp/musl_layer3c; mkdir -p "$OUT"
cd "$M" || exit 1

echo "=== build Chibil.Pal (with writev) ==="
dotnet build "$ROOT/targets/build/Chibil.Pal/Chibil.Pal.csproj" -c Release -v q --nologo 2>&1 | grep -E "error|Build succeeded" | head -1
PAL=$(ls "$ROOT/targets/build/Chibil.Pal/bin/Release/net10.0/Chibil.Pal.dll")

# printf closure — extend as the linker reports unresolved symbols.
MUSL_TUS="
  src/stdio/printf.c src/stdio/vfprintf.c src/stdio/stdout.c
  src/stdio/__stdio_write.c src/stdio/__stdout_write.c src/stdio/__towrite.c src/stdio/__lockfile.c
  src/stdio/ofl.c src/stdio/ofl_add.c src/stdio/__stdio_close.c
  src/stdio/fwrite.c
  src/internal/syscall_ret.c src/internal/libc.c src/errno/__errno_location.c
  src/string/memset.c src/string/memcpy.c src/string/memchr.c src/string/strnlen.c
  src/string/strlen.c
  src/ctype/isdigit.c
  src/errno/strerror.c
  src/locale/__lctrans.c src/locale/c_locale.c
  src/multibyte/wctomb.c src/multibyte/wcrtomb.c src/multibyte/internal.c
  src/math/__signbitl.c src/math/__fpclassifyl.c src/math/frexpl.c
  src/math/scalbn.c src/math/scalbnl.c src/math/frexp.c
  src/mman/mmap.c src/mman/munmap.c
"
muslobjs=()
echo "=== compile musl TUs ==="
for tu in $MUSL_TUS; do
    o="$OUT/$(echo "$tu" | tr '/' '_' | sed 's/\.c$/.obj/')"
    if $CH $CFLAGS "$tu" -o "$o" > "$o.log" 2>&1; then muslobjs+=("$o"); else echo "COMPILE FAIL: $tu"; tail -3 "$o.log"; fi
done

cat > "$OUT/main.c" <<'EOF'
int printf(const char *, ...);
extern void __chibil_pal_init(void);
int main(void){
    __chibil_pal_init();
    printf("hello from managed musl printf: %d + %d = %d\n", 40, 2, 42);
    return 0;
}
EOF
$CH -c --target=coreclr -mlp64 "$OUT/main.c" -o "$OUT/main.obj" 2>&1 | tail -3
$CH $CFLAGS "$ROOT/targets/build/musl-compat/pal-shim.c" -o "$OUT/pal-shim.obj" 2>&1 | tail -3

echo "=== link (print-imports, no -l) ==="
$LINK --print-imports -o "$OUT/l3c.dll" "$OUT/main.obj" "${muslobjs[@]}" "$OUT/pal-shim.obj" \
    --bind=__chibil_syscall=Chibil.Pal.Syscall,__chibil_get_tp=Chibil.Pal.GetTp -r "$PAL" 2>&1 | tail -20
echo "link exit: ${PIPESTATUS[0]}"
if [ -f "$OUT/l3c.dll" ]; then echo "=== run ==="; cp "$PAL" "$OUT/"; dotnet "$OUT/l3c.dll"; echo "exit=$?"; fi