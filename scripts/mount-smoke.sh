#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -ne 3 ]; then
  echo 'Usage: mount-smoke.sh CONFIG MOUNTPOINT /USER/PATH' >&2
  exit 2
fi
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
config="$1"
mountpoint="$2"
virtual_path="$3"
case "$virtual_path" in /*) ;; *) echo 'Virtual path must be absolute' >&2; exit 2;; esac
echo 'Materializing through the coordinator (can wait for the remote queue)...'
"$project_dir/out/soulfuse" "$config" fetch "$virtual_path"
echo 'Reading through the mounted filesystem; compare this hash to your source:'
sha256sum -- "${mountpoint%/}$virtual_path"
echo 'Reading a second time from cache...'
cat -- "${mountpoint%/}$virtual_path" > /dev/null
"$project_dir/out/soulfuse" "$config" status
