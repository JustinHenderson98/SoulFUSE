# SoulFUSE

A read-only Soulseek filesystem powered by slskd and FUSE.

**Prototype 0.6.0 · C# / .NET 10 + a small native libfuse3 bridge · Linux**

SoulFUSE mounts selected Soulseek users' shares as directories. Listing files uses a local catalog. Reading an uncached file asks slskd to download it, waits for the completed-file event, indexes the finished file in a dedicated slskd download directory, then serves normal file reads.

**This release waits for the complete file. It does not stream incomplete downloads.** A timed-out read returns an error while the underlying download continues. Use `fetch` or `pin` ahead of time for large files. No live Soulseek account is configured in this package.

## What works in this prototype

- Cached share listings for an explicit username allowlist; background refresh while mounted.
- Read-only Linux mount, stable open-file entries, random reads of completed cached files.
- One download task per cache entry even with multiple readers.
- Existing matching slskd transfers reused where their records and completion events remain available.
- Exact completion-event matching on transfer ID, username, remote filename, and size; no guessing of destination filenames.
- Bounded read waits, separate transfer lifetime, retained cached data across restart.
- Dedicated completed-download cache with size reservations, LRU eviction, and pinned/open-file protection.
- `fetch`, `pin`, `unpin`, `evict`, `list`, `status`, and `refresh` commands.
- Local Unix control socket so commands use the running mount's engine; exclusive state-directory lock prevents two engines from modifying it.
- Directory pagination in native FUSE callbacks, read-only statfs response, and safe file opening beneath the configured completed-download root.

## Validation status

The .NET project and native bridge compile. Managed setup also has isolated CLI tests using substituted Docker/systemctl commands; real service startup needs host validation. Thirty-four tests pass using a real local HTTP listener simulating slskd's API. Tests cover browse/open without downloading, concurrent reads, success-before-move/event ordering, byte equality, offline cache recovery, pin/eviction behavior, queued-read timeout and continued download, safe paths/symlink rejection, malformed listings, failed enqueue responses, budget limits, CLI operation, and transfer reuse.

**Host validation:** the user confirmed prototype 0.1 browsing, fetching, and FUSE reads on WSL2 with slskd 0.26.1. The user also confirmed 0.2 fetch/evict/refetch against the live slskd instance. The user confirmed readable paths through the 0.3 mount; 0.4 prefix hiding and VLC playback after download were also confirmed; Samba remains unverified. The build environment has no `/dev/fuse` and prohibits Unix-domain sockets. This is a runnable prototype for integration testing, not a production-ready filesystem. Run the host checks below before relying on it.

## Requirements

- Linux with FUSE 3 and `/dev/fuse` accessible to your account.
- .NET 10 SDK to build; .NET 10 runtime to run the built DLL.
- C compiler, `pkg-config`, and libfuse3 development headers.
- slskd with an API key authorized to browse users, enqueue downloads, inspect/remove transfer records, and read events.
- Adapter must have local read and delete access to slskd's completed-download directory, directly or through a shared Docker volume.
- Trusted, private local state directory and trusted local administration of the slskd download root. Don't expose the API key through Samba.

On Debian/Ubuntu, install `build-essential pkg-config libfuse3-dev fuse3` through your package manager and install the .NET 10 SDK using your normal .NET installation method.

## Build

```bash
chmod +x build.sh test.sh scripts/mount-smoke.sh
./build.sh
./test.sh
./out/soulfuse --help
```

The C# project uses YamlDotNet 18.1.0 (MIT) for safe YAML parsing in friend management; the first build restores this NuGet dependency. `build.sh` compiles the native bridge against your installed libfuse3; it does not vendor Tmds.Fuse or slskd source. The `out` directory contains the `soulfuse` launcher, a compatible `slskfs` launcher, DLLs, and the native bridge. Keep them together. Existing commands and configuration remain supported; the internal assembly is still `SlskFs.dll`.

## Managed setup

Use [SETUP.md](SETUP.md) to set up slskd and SoulFUSE together, adopt an existing runtime, and manage startup, friends, mounted users and shares. The existing config-first commands below remain available.

## Configure

Copy `config.example.json` to `config.json`, then edit:

| Setting | Meaning |
| --- | --- |
| `slskdConfigPath` | Optional absolute host path to `slskd.yml`; required only for `friends` commands |
| `apiUrl` | Full slskd API base, ending in `/api/v0/`; include any reverse-proxy path prefix |
| `apiKeyEnv` | Environment variable holding the API key; defaults to `SLSKD_API_KEY` |
| `users` | Exact Soulseek usernames whose shares should be mounted |
| `stateDirectory` | Absolute, adapter-owned directory for catalog, cache index, lock, and local socket |
| `remoteDownloadsRoot` | Completed-download root as slskd sees it, e.g. `/app/downloads` inside Docker |
| `localDownloadsRoot` | Same directory as the adapter sees it, e.g. `/srv/slskd/downloads` on the host |
| `dedicatedDownloadsCache` | Required `true` acknowledgement: the completed directory is dedicated to this adapter and eviction deletes adopted downloads |
| `cacheBytes` | Maximum tracked completed bytes plus reserved pending files; default 20 GiB |
| `readTimeoutSeconds` | Maximum wait for one uncached filesystem read; default 30 seconds |
| `transferTimeoutSeconds` | Maximum adapter wait for a transfer and final event; default 24 hours |
| `apiTimeoutSeconds` | Timeout for one HTTP operation; default 60 seconds |
| `refreshSeconds` | Background catalog-refresh interval; default 15 minutes |
| `pollMilliseconds` | Transfer polling interval; default 1 second |
| `eventPageLimit` | Maximum event pages searched per lookup, 100 events per page; default 50 |
| `maxCatalogEntries` | Combined file/directory entry ceiling for the catalog |

`stateDirectory` and `localDownloadsRoot` must be separate, non-overlapping trees. The state directory is set to owner-only permissions. Use the same OS account for the mount and its CLI commands. API keys are read from the environment, never written to catalog/cache files. A local command routed to an existing mount does not need its own API key.

For Docker, an example mapping is `/srv/slskd/downloads:/app/downloads`. You do not need to expose the incomplete directory to the adapter. Do not point slskd's download directory at the FUSE mount.

Use a dedicated slskd instance/download area for this experiment. Keep completed transfer records and download-completion events long enough for the adapter to observe them. Disable post-download scripts that immediately move or delete files. Prefer the `rename` destination-conflict policy over overwrite. The adapter requires the Events API; it does not infer paths from folder naming conventions and will wait/fail if the matching event is unavailable.

## First run

Choose a writable state location in the config, for example `/home/YOU/.local/share/slskfs`. Create an empty mountpoint. Replace `alice` and the example filename with your actual username/path.

```bash
mkdir -p "$HOME/friends"
read -rs -p 'slskd API key: ' SLSKD_API_KEY
export SLSKD_API_KEY
./out/soulfuse config.json refresh
./out/soulfuse config.json list /
./out/soulfuse config.json list /alice
./out/soulfuse config.json mount "$HOME/friends"
```

Leave the mount running in the foreground. In another terminal, under the same OS account:

```bash
ls "$HOME/friends/alice/Music"
./out/soulfuse config.json fetch /alice/Music/song.flac
cat "$HOME/friends/alice/Music/song.flac" > /dev/null
./out/soulfuse config.json pin /alice/Music/song.flac
./out/soulfuse config.json status
./out/soulfuse config.json unpin /alice/Music/song.flac
./out/soulfuse config.json evict /alice/Music/song.flac
fusermount3 -u "$HOME/friends"
```

CLI paths are **virtual absolute paths**, starting at the mount's root (`/alice/...`), not paths prefixed with the host mountpoint. Usernames and filename components are displayed as ordinary Linux names: `alice space`, `Captain Laserhawk`, `@@szgoh`, Unicode, and literal percent signs are preserved. Quote paths containing spaces in shell commands. URL escaping is used only for HTTP requests, never for mounted names. A leading nonempty `@@...` component is hidden in the mounted view; nested `@@...` names are preserved. Full remote filenames are retained for downloads and cache identity. If stripping would combine top-level names from different roots (including file/directory conflicts), the original hierarchy is retained for that entire user. No shares are silently merged. A later refresh can therefore restore prefixes if a new share introduces a conflict. Components must fit 255 UTF-8 bytes; slash, NUL, empty, `.` and `..` components are rejected. Original Soulseek strings are retained for API requests. Traversal components and ambiguous/colliding paths reject the refreshed listing and retain its prior snapshot. A username containing a slash is unsupported.

On upgrade, old encoded catalog paths are automatically rebuilt from the stored original remote names, including offline. The catalog on disk retains the original readable paths; FUSE and CLI listings apply prefix hiding when building the visible tree. Cache identity and pins are unchanged. Previously URL-encoded CLI paths must be replaced with the new readable paths; they are not accepted as aliases because a real filename can contain literal `%20`.

Running `fetch`, `pin`, or `refresh` without a mount starts a temporary engine. Running them while mounted routes to the same engine over an owner-only Unix socket. `serve` starts the engine and socket without FUSE for diagnostics.

## Friend access management

Use `./out/soulfuse config.json friends list`, `friends add USERNAME`, or `friends remove USERNAME`. Configure `slskdConfigPath` with the host path to your slskd YAML first. This is the inbound sharing allowlist, separate from the `users` whose shares you mount. Commands work without the mount, API key, or web remote-configuration access. See [FRIENDS.md](FRIENDS.md) for setup, limitations, backup/recovery, and reload verification.

## Materialization and cache behavior

1. `readdir`, metadata lookup, and `open` do not enqueue content downloads.
2. The first nonempty read before EOF reserves space and starts or reuses a slskd transfer.
3. The adapter waits for `DownloadFileComplete` and the actual `localFilename`.
4. The source is safely opened beneath the configured download root with symlinks rejected. Its length must match the catalog.
5. The adapter records the completed file’s relative path, device/inode, size, and transfer identity in `downloads-cache.json`. No second content copy is created.
6. Reads use only complete cache files. No missing content is reported as EOF or replaced with zero bytes.

`cat file > /dev/null` materializes a file if it finishes within the read deadline. For a larger transfer the first attempt can fail with a timeout; the download continues in the running engine, and a later attempt can succeed. `fetch` waits up to the configured transfer lifetime without the shorter filesystem-read deadline. `pin` fetches and prevents automatic eviction. `unpin` leaves content present but makes it evictable. `evict` deletes the actual indexed slskd download and rejects pinned/open/downloading files. The remote listing remains visible.

The slskd completed-download directory is the cache. Only indexed files are eligible for automatic eviction; unrelated/unindexed files are left alone. Use one dedicated slskd instance and one adapter state directory for this root. Do not share the directory between independently managed adapter instances. Configure slskd destination conflicts to `rename`, and disable external cleanup/postprocessing for files you want to keep pinned. A pin protects against adapter eviction only.

After eviction or external deletion, the adapter uses its persisted transfer identity to clear the exact stale successful transfer record, then enqueues a new transfer. The mapping survives eviction even when old events have expired. If no mapping exists yet, discovery still requires the matching completion event. Reads of already-open Linux file descriptors continue after an external unlink; new reads re-materialize the file. Changed inode/device/size or symlinks cause an error instead of deleting or reading a replacement. In-place same-size edits cannot be detected. This is a trusted local directory, not a defense against hostile concurrent filesystem mutation.

`cacheBytes` bounds tracked content and pending reservations. It is not a hard filesystem quota: unindexed downloads, old 0.1 copies, metadata, incomplete files, and downloads completing after an adapter timeout can consume additional space. Status reports accounted bytes; missing entries may remain conservatively accounted until refetch/eviction. Deleted files held open by other applications can retain disk blocks. LRU timestamps persist across restart. No directory sweep or recursive deletion is performed.

**Upgrading from 0.1:** follow [MIGRATION.md](MIGRATION.md). Old copied content is left untouched and is not used by 0.2; pins must be reapplied.
Files larger than the cache budget are rejected before enqueue. Pinning a folder recursively is not implemented. In this version pins apply to a cache entry (user, remote path, size), not automatically to future replacements.

## Windows through Samba (integration target)

Mount with `--allow-other`; non-root FUSE mounting with that option generally requires `user_allow_other` in `/etc/fuse.conf`. This makes the mount readable by other local users, so use a dedicated host/account and Samba access restrictions.

Example share to adapt to your Samba setup:

```ini
[friends]
    path = /home/YOU/friends
    read only = yes
    browseable = yes
    guest ok = no
    valid users = YOUR_SAMBA_USER
```

Windows would access `\\SERVER\friends`. Use `fetch` before large copies during initial testing. Explorer thumbnailers and scanners can issue real reads and therefore trigger downloads. Every SMB user of this mount sees content through the same upstream slskd account; separate permission contexts require separate instances/mounts. No native Windows FUSE driver is included.

## API contract and upstream inspection

The adapter was written against inspected upstream sources at slskd commit `90a0d24a84a132a998b937b6a60de945caa267cc`. This is a source-verified contract, not a live-version compatibility certification. Your installed release may differ. It uses:

- `GET users/{username}/browse`: `directories[].name`, `files[].filename`, `files[].size`. Locked directories are excluded. An array response is also accepted.
- `GET transfers/downloads/{username}`: grouped `directories[].files[]`; 404 means no transfers.
- `POST transfers/downloads/{username}`: array of `{ filename, size }`; this endpoint is marked obsolete in current upstream but remains implemented. The adapter inspects `failed`, not merely the HTTP status.
- `DELETE transfers/downloads/{username}/{id}?remove=true`: remove the exact stale completed transfer before a fresh enqueue.
- `GET events?offset=...&limit=100`: event records whose `data` is JSON text; `DownloadFileComplete` includes `transfer`, `remoteFilename`, and `localFilename`.

Relevant source paths under [slskd](https://github.com/slskd/slskd/tree/90a0d24a84a132a998b937b6a60de945caa267cc): `src/slskd/Users/API/Controllers/UsersController.cs`, `src/slskd/Transfers/API/Controllers/TransfersController.cs`, `src/slskd/Transfers/Downloads/DownloadService.cs`, `src/slskd/Events/API/EventsController.cs`, and `src/slskd/Events/Types/EventRecord.cs`.

## Known limitations and failure semantics

- Whole-file materialization only; no progressive streaming or range-triggered Soulseek restarts.
- Catalog identity is username + remote path + advertised size, **not a content hash/version**. A same-size replacement is not reliably detectable. Eviction deletes the indexed completed download; the next fetch removes its stale transfer record and requests a new transfer. This still does not establish a cryptographic version or detect changed remote content without a fetch. True version detection requires protocol/source support.
- No cross-user deduplication, multisource downloads, automatic publishing/seeding, or private encrypted Soulseek overlay is added.
- Cached listings remain while peers are offline; a failed refresh is reported and never silently replaces them with an empty share. Empty remote folders are not represented in this initial version.
- Metadata refresh may update a path, but an already-open handle keeps its recorded entry. Snapshot identity is limited by the metadata available from Soulseek.
- An absent completion event, failed final move, repeatedly missing final file, or disappearing transfer record causes an error/timeout, not a guessed-path read.
- API authentication/network errors are reported; the adapter does not retry arbitrary failed transfers indefinitely. A user can resolve the slskd error and fetch again.
- Timing out or closing a read does not cancel the underlying slskd transfer. Shutdown also leaves its queue intact. Reopening can attach to that transfer.
- Event lookup is bounded and polled; very busy instances may need a larger `eventPageLimit`. An old completion outside the search window cannot be materialized without another usable event.
- The C callbacks are bounded blocking operations; enough concurrent uncached reads can still consume FUSE worker threads. Load testing and interrupt-aware async reads are follow-on work.
- The mount reports unknown remote capacity (zero blocks) and synthetic file modes; it is read-only. Extended attributes, symlinks, original timestamps, native Windows mounting, and per-SMB-user upstream identities are not supported.
- Do not expose the mount itself as a slskd shared directory: scanning/uploading it can recursively trigger downloads.

## Host acceptance check

Pick a small file you own and whose source hash you know. Refresh, mount, then run:

```bash
./scripts/mount-smoke.sh config.json "$HOME/friends" /alice/Test/sample.bin
```

Compare the printed SHA-256 with the original. Verify `ls` does not queue downloads; a first `cat` queues exactly one; a second `cat` uses cache; two readers share one transfer; offline sources yield a bounded read failure; pinned content survives restart. Then test Windows via Samba. FUSE smoke checks require a real host; they were not run in the build environment.

## Next implementation milestone

Validate against your installed slskd and actual FUSE/Samba host, pin the tested server release, then add progressive reads only after verifying incomplete-file write/rename/resume semantics. Better diagnostics per virtual path and robust same-size replacement invalidation should precede general distribution.
