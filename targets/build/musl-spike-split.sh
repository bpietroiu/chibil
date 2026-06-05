#!/bin/bash
# Categorize the musl-spike failures by root cause (exact counts).
set -u
OBJ="${OBJ:-/tmp/musl_spike}"
sc=0; at=0; wa=0; dl=0; other=0; otherlist=""
for l in "$OBJ"/*.obj.log; do
    obj="${l%.log}"; [ -f "$obj" ] && continue          # compiled OK
    if   grep -q "syscall_arch.h" "$l"; then sc=$((sc+1))
    elif grep -q "atomic_arch.h"  "$l"; then at=$((at+1))
    elif grep -q "weak_alias"     "$l"; then wa=$((wa+1))
    elif grep -q "dynlink.h"      "$l"; then dl=$((dl+1))
    else other=$((other+1)); otherlist+="$(tail -2 "$l" | head -1)
"; fi
done
echo "failing TUs total: $((sc+at+wa+dl+other))"
echo "  syscall_arch.h inline-asm  (PAL provides __syscall) : $sc"
echo "  atomic_arch.h  inline-asm  (managed Interlocked)    : $at"
echo "  weak_alias attribute aliasing (shim/chibil)         : $wa"
echo "  dynlink.h hidden/tlsdesc parse gap                  : $dl"
echo "  genuinely other                                     : $other"
echo ""
echo "=== the 'genuinely other' lines ==="
printf "%s" "$otherlist" | sed -E "s|/[^ ]*musl-[^ ]*/||g" | sort | uniq -c | sort -rn
