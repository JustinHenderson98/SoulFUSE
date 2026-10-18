#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet publish src/SlskFs/SlskFs.csproj -c Release -o out
cc -std=c11 -O2 -Wall -Wextra -Werror -fPIC -shared native/slskfs_native.c \
  $(pkg-config --cflags --libs fuse3) -o out/libslskfs_native.so
printf '%s\n' '#!/usr/bin/env bash' 'exec dotnet "$(dirname "$0")/SlskFs.dll" "$@"' > out/slskfs
chmod +x out/slskfs
