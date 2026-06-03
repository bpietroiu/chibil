#!/bin/bash
# Summarize chibil compile failures: counts + first error line per failed file.
set -u
echo "OK objs: $(wc -l < /tmp/mpy_objs.txt)   FAILED: $(wc -l < /tmp/mpy_failed.txt)"
echo "=== first meaningful error line per failed file ==="
while read -r f; do
    [ -z "$f" ] && continue
    o="/tmp/mpy_obj/$(echo "$f" | sed 's@[./]@_@g').obj.log"
    line=$(grep -m1 -E ':[0-9]+:|omitted|not supported|cannot|error|expected|unknown' "$o" 2>/dev/null | head -1)
    echo "$line"
done < /tmp/mpy_failed.txt | sed -E 's@^[^:]*/@@; s@:[0-9]+:@:@' | sort | uniq -c | sort -rn | head -30
