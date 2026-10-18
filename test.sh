#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
./build.sh
LD_LIBRARY_PATH="$PWD/out${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" dotnet run --project tests/SlskFs.Tests -c Release
