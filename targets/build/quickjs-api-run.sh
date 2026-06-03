#!/bin/bash
# Behavioral oracle: build qjs.dll with the facade, then run the C# consumer that
# evaluates "40+2" through quickjs.Api and asserts 42.
# Run under WSL: wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-api-run.sh
set -u
ROOT=/mnt/d/sandbox/chibil
bash "$ROOT/targets/build/quickjs-api.sh" || { echo "qjs.dll build failed"; exit 1; }
echo "=== surface oracle ==="
bash "$ROOT/targets/build/quickjs-api-surface.sh" | tail -3
echo "=== behavioral oracle ==="
cd "$ROOT/targets/quickjs-api-consumer" || exit 1
dotnet run -c Debug 2>&1 | tail -5
echo "consumer exit: ${PIPESTATUS[0]}"
