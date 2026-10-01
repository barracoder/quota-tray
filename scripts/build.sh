#!/usr/bin/env bash
# Restore, build and test everything. Usage: scripts/build.sh [Debug|Release]
set -euo pipefail
cd "$(dirname "$0")/.."
CONFIG="${1:-Release}"
dotnet restore
dotnet build -c "$CONFIG" --no-restore
dotnet test -c "$CONFIG" --no-build --logger "trx;LogFileName=results.trx" --results-directory artifacts/test-results
