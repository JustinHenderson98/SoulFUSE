# SoulFUSE 0.6.1: generated API-key casing fix

The 0.6.0 generator accidentally camel-cased `SLSKD_API_KEY` to `slskD_API_KEY`. 0.6.1 uses an exact dictionary key and tests the generated Compose environment. Fresh setups also create a mode-0600 `.env` and require nonempty Compose variables. Authentication failures now report a specific configuration error.

Existing installations: correct the environment key in compose.yaml to uppercase `SLSKD_API_KEY`, then recreate slskd through SoulFUSE stop/start. Do not rerun setup or delete state. Build the new release and copy out/ contents into RUNTIME/bin while the service is stopped to update the installed application. Existing configurations are not overwritten by this release. See SETUP.md for credential rotation and direct Compose use.

# SoulFUSE 0.6.0: managed setup

See [SETUP.md](SETUP.md) for the existing-runtime adoption procedure. Keep your current downloads/state and rebuild before running setup. Existing config-first commands are unchanged; empty mounted-user lists are now allowed for hosting-only installations.

# SoulFUSE 0.5.0: friend management

Update source and run `bash ./build.sh`. This release adds the YamlDotNet 18.1.0 dependency, restored by the build. Restart existing mounts when upgrading normally; `friends` commands operate directly on the local slskd config and do not route through the mount.

Add this to your SoulFUSE JSON config for your WSL setup:

```json
"slskdConfigPath": "/home/jeh24/slskfs-runtime/slskd/slskd.yml"
```

Your existing blacklist pattern is recognized automatically. Then run:

```bash
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends list
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends add alice
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends remove alice
```

No changes to cache data, pins or mounted-user settings. Read FRIENDS.md before enabling friend management on a different slskd configuration.

---

# SoulFUSE branding update (0.4.1)

The project is now named **SoulFUSE**. Update source in your existing checkout and run `bash ./build.sh`. The primary command is `./out/soulfuse`; `./out/slskfs` remains available for existing scripts. Startup/help/errors and the FUSE filesystem name now use the new branding.

Your project folder, configuration, API-key environment variable, state directory, catalog, cache and pins need no changes. Internal C# namespaces, assembly filename and native ABI retain their existing names for compatibility. Restart the mount after rebuilding to see the new filesystem name.

Earlier release instructions below retain their historical commands, which still work.

---

# Upgrade to 0.4: hide opaque share prefixes

Stop/unmount the adapter, update source in your existing project folder, then run `bash ./build.sh` and remount using the same config. The folder may still be called `slskfs-prototype-0.1`; that is fine. The build script now forces a rebuild and disables shared compilation to avoid the stale-output behavior encountered during the last update.

Your paths become:

- `/jeh24/@@szgoh/Captain Laserhawk/...` → `/jeh24/Captain Laserhawk/...`
- `/jeh24/@@yzvjb/Books/...` → `/jeh24/Books/...`

No configuration change, refresh, or re-download is required for an existing catalog. Original remote filenames, cache keys and pins are retained. Old prefixed CLI paths are no longer aliases when hiding is applied. Use `list /jeh24` to inspect current paths.

Only the first component beginning with `@@` followed by a nonempty token is hidden, and only when there is another component beneath it. Nested `@@` names remain unchanged. This is a presentation convention, not proof of how the remote client generated the name.

Collision policy: if different roots would share a visible top-level name, retain the full original hierarchy for that user, including its prefixes. This also protects file/directory collisions and prevents unrelated shares being silently merged. Other users are unaffected. Paths may regain prefixes after refresh if a newly shared directory introduces a collision; use `list` to discover the resulting paths. Existing open handles retain their original entry.

```bash
fusermount3 -u "$HOME/friends"
bash ./build.sh
export SLSKD_API_KEY="$(cat "$HOME/slskfs-runtime/api-key")"
./out/slskfs "$HOME/slskfs-runtime/config.json" list /jeh24
./out/slskfs "$HOME/slskfs-runtime/config.json" mount "$HOME/friends"
```

The previous migration notes below describe earlier releases.

---

# Upgrade to 0.3: readable mount paths

Unmount/stop the existing adapter, extract the updated source, and run `bash ./build.sh`. Keep your existing config, state directory and dedicated slskd download directory. Remount with the new build. Do not run old and new builds together.

Saved catalog paths are upgraded automatically on startup using original remote names, without needing the peer online. Cache keys and pins do not change, so completed files can be read without another transfer. No cache data is deleted by this migration.

- `%40%40szgoh` becomes `@@szgoh`.
- `Captain%20Laserhawk` becomes `Captain Laserhawk`.
- Unicode and punctuation display normally.
- An actual remote filename containing the literal characters `%20` stays `%20`; it is not decoded to a space.
- Old URL-encoded CLI paths are not aliases. Use `list` to see current names and quote paths in your shell.
- The adapter preserves remote `@@...` share names. Friendly share aliases are not implemented.
- Linux-valid components up to 255 UTF-8 bytes are supported. A slash within a username is now rejected rather than URL-encoded.

Example for your existing share:

```bash
./out/slskfs "$HOME/slskfs-runtime/config.json" list '/jeh24/@@szgoh/Captain Laserhawk'
```

The older dedicated-cache migration instructions below apply only if upgrading from 0.1.

---

# Upgrade to 0.2: dedicated slskd downloads as cache

This release reads completed slskd downloads directly. Eviction now deletes those files. Use a dedicated slskd download root, one adapter state directory, and slskd's `rename` destination-conflict policy. The adapter needs read/delete permissions on that directory. Do not point slskd at the FUSE mount.

## Your WSL2 setup

Your existing paths can stay as configured if this slskd instance is dedicated to the adapter:

- slskd container: `/app/downloads`
- WSL host: `$HOME/slskfs-runtime/slskd/downloads`
- Adapter metadata: `$HOME/slskfs-runtime/state`

No Docker volume change is needed. If that slskd instance is also used for unrelated downloads, use a separate instance/download area before enabling this mode. Unknown files are not swept, but a file adopted via `fetch` becomes eligible for eviction.

1. Unmount the old adapter with `fusermount3 -u "$HOME/friends"`. Stop any `serve` process too. Keep slskd running. Do not run the old and new builds together.
2. Extract the updated archive to a separate directory (or apply its source changes to your checkout). From the new project's directory, run `bash ./build.sh`; this is required to rebuild the native helper as well as C#.
3. Add `"dedicatedDownloadsCache": true` to your existing config. The new build refuses to start without this explicit setting. Keep the current `remoteDownloadsRoot`, `localDownloadsRoot`, and `stateDirectory`.
4. Export your API key and fetch the previously tested small file. Existing completed downloads are reused if their transfer/event metadata is still available. Check `status`: it now includes `relativePath`, device/inode and transfer identity. There should be no new private `state/content` data.
5. Evict that test file, confirm its completed-download file disappeared, then fetch it again. This tests the new DELETE-transfer API path against your installed slskd. Finally mount and test reads as before.

Example commands (replace the virtual path with your actual small test file):

```bash
export SLSKD_API_KEY="$(cat "$HOME/slskfs-runtime/api-key")"
./out/slskfs "$HOME/slskfs-runtime/config.json" fetch '/jeh24/SHARE/FILE'
./out/slskfs "$HOME/slskfs-runtime/config.json" status
./out/slskfs "$HOME/slskfs-runtime/config.json" evict '/jeh24/SHARE/FILE'
./out/slskfs "$HOME/slskfs-runtime/config.json" fetch '/jeh24/SHARE/FILE'
./out/slskfs "$HOME/slskfs-runtime/config.json" mount "$HOME/friends"
```

## Old private cache and pins

0.2 uses a new `downloads-cache.json` index. It does not modify the old `cache.json` or `content` directory. Old pinned content is not automatically migrated: inspect the old index and reapply `pin` for the files you want retained. If the corresponding slskd file is gone, the old private copy is not automatically imported; preserve it until you have verified the replacement.

After validating 0.2 and reapplying pins, the old `state/content` directory and `state/cache.json` can be deleted manually to reclaim the duplicated space. Do not delete `catalog.json`, `downloads-cache.json`, or the whole state directory. Keep old data if you need rollback. No upgrade step automatically deletes old files.

## Ownership and recovery

- Eviction deletes only indexed completed files and retains a small metadata record for future transfer cleanup. There is no bulk clear command yet.
- Pins and open/downloading files are protected from adapter eviction. External tools/slskd can still delete pinned content.
- Existing open reads survive unlink on Linux; new opens may download again.
- Missing downloads with known mappings can be recovered even after completion-event expiry. Discovering a previously unindexed completion still requires its event.
- Keep transfer/event retention generous and do not move completed files with scripts. A wrong volume mapping can look like missing data; retry is bounded to avoid endless downloads.
- Inode/device/size mismatches cause a visible failure. Investigate external modifications instead of blindly deleting the replacement.
- Avoid concurrent external renames/writes in the download tree. Native checks reject symlinks and validate inode identity before unlink, but do not promise atomic protection against hostile local races.
- The cache budget counts tracked files/reservations, not every byte slskd might write. Monitor free disk space separately.
