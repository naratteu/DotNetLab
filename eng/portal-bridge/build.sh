#!/usr/bin/env bash
# Builds the Portal connector into src/WebAssembly/wwwroot/portal/ (needs Go).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../src/WebAssembly/wwwroot/portal"
cd "$here"
GOOS=js GOARCH=wasm go build -trimpath -ldflags "-s -w" -o "$out/portal-bridge.wasm" .
cp "$(go env GOROOT)/lib/wasm/wasm_exec.js" "$out/"
