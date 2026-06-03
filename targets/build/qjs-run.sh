#!/bin/bash
cd /mnt/d/sandbox/chibil/targets/quickjs-2025-09-13 || exit 1
cat > /tmp/t.js <<'JS'
print(1 + 2);
print(Math.sqrt(144));
print([3, 1, 2].sort().join(","));
print("hello".toUpperCase());
print(JSON.stringify({a: 1, b: [2, 3]}));
var s = 0; for (var i = 1; i <= 100; i++) s += i; print(s);
JS
timeout 30 dotnet qjs.dll /tmp/t.js 2>&1 | head -30
echo "exit=${PIPESTATUS[0]}"
