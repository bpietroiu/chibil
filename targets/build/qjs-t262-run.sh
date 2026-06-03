#!/bin/bash
# Run a subset of test262 through run-test262.dll. Arg 1 = subdir under test262/test
cd /mnt/d/sandbox/chibil/targets/quickjs-2025-09-13 || exit 1
SUB="${1:-language/types}"
echo "=== test262 subset: $SUB ==="
timeout 300 dotnet run-test262.dll -c test262.conf -d "test262/test/$SUB" 2>&1 | tail -25
