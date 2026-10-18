# SoulFUSE

SoulFUSE mounts a read-only view of selected slskd REST API responses using
libfuse3. The mounted directory contains:

- `shares.json` — the response from `/api/v0/shares/contents`
- `downloads.json` — the response from `/api/v0/transfers/downloads`

The responses are fetched once when SoulFUSE starts, so unmount and remount to
refresh the snapshot.

## Build

Install a C compiler, CMake, pkg-config, libfuse3 development files, and
libcurl development files. Then build:

```sh
cmake -S . -B build
cmake --build build
```

## Run

Set the slskd base URL and, when API authentication is enabled, its API key:

```sh
export SLSKD_URL=http://localhost:5030
export SLSKD_API_KEY=your-api-key
mkdir -p /tmp/soulfuse
./build/soulfuse /tmp/soulfuse
```

Use `-f` to keep the process in the foreground. Unmount with
`fusermount3 -u /tmp/soulfuse`.

The mount only exposes these JSON snapshots; it does not provide access to the
contents of shared or downloaded files.
