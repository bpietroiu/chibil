#!/bin/bash
# SPIKE: how much of musl's source does chibil compile to MSIL today?
# Stratified sample across subsystems; report completeness rate + failure buckets.
# Run: wsl bash /mnt/d/sandbox/chibil/targets/build/musl-spike.sh
set -u
ROOT=/mnt/d/sandbox/chibil
CH="dotnet $ROOT/chibil/bin/Debug/net10.0/chibil.dll"
M=$ROOT/targets/musl-1.2.6
cd "$M" || exit 1

# musl source build include set (arch + internal + public + generated).
INCS="-Iarch/x86_64 -Iarch/generic -Isrc/include -Isrc/internal -Iinclude -Iobj/include"
# SHIM=1 prepends the chibil compat layer: shadow arch headers (syscall/atomic)
# first on the path + the force-included weak_alias/features override.
SHIM="${SHIM:-0}"
if [ "$SHIM" = "1" ]; then
    INCS="-I$ROOT/targets/build/musl-compat $INCS"
    SHIMFLAG="-include $ROOT/targets/build/musl-chibil-compat.h"
    OBJ=/tmp/musl_spike_shim
else
    SHIMFLAG=""
    OBJ=/tmp/musl_spike
fi
CFLAGS="-c --target=coreclr -nostdinc -mlp64 -D_GNU_SOURCE -D_XOPEN_SOURCE=700 $SHIMFLAG $INCS"
mkdir -p "$OBJ"; FAILTXT="$OBJ/fail.txt"; : > "$FAILTXT"

# Stratified sample: ALL of the small pure subsystems, a capped sample of large ones.
ALL_DIRS="string ctype errno stdlib prng exit env multibyte"
SAMPLE_DIRS="stdio math malloc misc time locale network signal setjmp"
SAMPLE_N=15

files=()
for d in $ALL_DIRS;    do while read -r f; do files+=("$f"); done < <(find "src/$d" -name '*.c' 2>/dev/null); done
for d in $SAMPLE_DIRS; do while read -r f; do files+=("$f"); done < <(find "src/$d" -name '*.c' 2>/dev/null | sort | head -$SAMPLE_N); done

echo "=== compiling ${#files[@]} musl TUs with chibil --target=coreclr ==="
ok=0; fail=0
for f in "${files[@]}"; do
    obj="$OBJ/$(echo "$f" | tr '/' '_').obj"
    if $CH $CFLAGS "$f" -o "$obj" > "$obj.log" 2>&1; then
        ok=$((ok+1))
    else
        fail=$((fail+1))
        # first compiler error line for bucketing
        err=$(grep -m1 -iE "error:|not supported|unsupported|InvalidOperation|Exception|assert" "$obj.log" | head -1)
        echo "$f :: ${err:-<no error line; see log>}" >> "$FAILTXT"
    fi
done

echo ""
echo "=== RESULT: $ok ok / $fail fail  (rate: $(awk "BEGIN{printf \"%.0f\", 100*$ok/($ok+$fail)}")%) ==="
echo ""
echo "=== failure buckets (normalized error message x count) ==="
sed -E 's/.*:: //; s/'"'"'[^'"'"']*'"'"'/QUOTE/g; s/[0-9]+/N/g; s|[A-Za-z0-9_./-]+\.[ch]|FILE|g' "$FAILTXT" \
    | sort | uniq -c | sort -rn | head -20
echo ""
echo "=== per-subsystem ok/total ==="
declare -A tot ok2
for f in "${files[@]}"; do d=$(echo "$f" | cut -d/ -f2); tot[$d]=$(( ${tot[$d]:-0} + 1 )); done
while read -r line; do d=$(echo "$line" | cut -d/ -f2); ok2[$d]=$(( ${ok2[$d]:-0} + 1 )); done < <(grep -L . /dev/null; for f in "${files[@]}"; do obj="$OBJ/$(echo "$f" | tr '/' '_').obj"; [ -f "$obj" ] && echo "$f"; done)
for d in $ALL_DIRS $SAMPLE_DIRS; do printf "%-12s %s/%s\n" "$d" "${ok2[$d]:-0}" "${tot[$d]:-0}"; done
