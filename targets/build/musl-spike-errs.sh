#!/bin/bash
# Bucket the REAL chibil error messages (the '^ <message>' line) of the remaining
# failing TUs in an OBJ dir.
set -u
OBJ="${OBJ:-/tmp/musl_spike_shim}"
echo "=== remaining failures by error message ==="
for l in "$OBJ"/*.obj.log; do
    obj="${l%.log}"; [ -f "$obj" ] && continue
    # the caret line carries the message after '^'
    grep -m1 -oE '\^ .*' "$l" | sed -E 's/^\^ //'
done | sort | uniq -c | sort -rn
echo ""
echo "=== one example file per message ==="
declare -A seen
for l in "$OBJ"/*.obj.log; do
    obj="${l%.log}"; [ -f "$obj" ] && continue
    msg=$(grep -m1 -oE '\^ .*' "$l" | sed -E 's/^\^ //')
    if [ -z "${seen[$msg]:-}" ]; then
        seen[$msg]=1
        src=$(basename "$obj" .obj.log | sed 's/^src_/src\//; s/_/\//')
        ctx=$(grep -m1 -B1 '\^' "$l" | head -1)
        printf "[%s]\n   %s\n   -> %s\n" "$msg" "$src" "$ctx"
    fi
done
