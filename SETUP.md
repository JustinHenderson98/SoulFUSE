# Managed SoulFUSE setup (0.6.1)

SoulFUSE can set up a dedicated slskd Docker container and a native FUSE mount together. Docker runs slskd; a systemd **user** service runs SoulFUSE and starts the container before mounting. Setup only writes files. `enable` installs the service, and `start` launches it.

## Requirements

Build requirements are in README.md. Managed startup additionally needs Docker with Compose v2, Docker access without sudo, systemd user services, and `/usr/bin/fusermount3`. Verify `docker compose version`, `docker info`, `systemctl --user status`, and access to `/dev/fuse`. A nonzero `systemctl status` alone may mean a degraded user session; it must be able to connect to the user service manager.

In WSL2, systemd must be enabled for these lifecycle commands. Docker Desktop integration or a Linux Docker daemon must be running. This release does not enable Windows startup, start Docker Desktop, change router forwarding, or change Windows firewall rules. TCP peer port 50300 still needs an appropriate inbound route for remote peers. The web/API port is localhost-only in newly generated installations.

## New installation

From the built source directory:

```bash
sudo apt update
sudo apt install build-essential pkg-config libfuse3-dev fuse3

bash ./build.sh
./out/soulfuse setup
./out/soulfuse enable
./out/soulfuse start
./out/soulfuse status
```

Default runtime directory: `~/.local/share/soulfuse`. Use `--home ABSOLUTE_DIRECTORY` on every managed command to select another installation. Setup prompts for Soulseek login, web admin password, inbound friends, users to browse, shared folders, cache size and mountpoint. Password input is hidden in an interactive terminal.

Names in comma-separated lists cannot contain commas in the wizard; add such names individually with the commands below. `-` means an empty list. An empty friend list **blocks everyone**; an empty mounted-user list is valid for hosting only. Friends must allow your Soulseek username in their own sharing settings.

New configurations use the slskd 0.26.1 image, read-only shared-folder bindings and a dedicated completed-download cache. Generated credentials/configuration files are mode 0600 and the runtime directory is mode 0700. Fresh setup writes a private `.env` beside Compose with the API key and numeric UID/GID. Direct Compose commands can now run without exports. Required-value expressions reject missing or empty variables. Managed commands also supply these values explicitly. The `api-key` file remains authoritative for SoulFUSE; `.env` contains a generated copy for direct Compose use. If rotating the key manually, update both files and recreate the container. Existing exported shell values override `.env`. Adoption preserves existing `.env` files.

Setup copies the published application and native library into `RUNTIME/bin`; the service does not depend on the source folder remaining in Downloads. The installed .NET runtime path must remain available.

## Adopt jeh24's existing installation

Stop the old foreground mount with Ctrl-C in its terminal. If still mounted, unmount it with `fusermount3 -u "$HOME/friends"`. The mountpoint must be empty before setup. Keep the existing slskd container and runtime files; don't delete downloads or state.

```bash
bash ./build.sh
./out/soulfuse setup --adopt --home "$HOME/slskfs-runtime"
./out/soulfuse enable --home "$HOME/slskfs-runtime"
./out/soulfuse start --home "$HOME/slskfs-runtime"
./out/soulfuse status --home "$HOME/slskfs-runtime"
```

Choose `/home/jeh24/friends` when asked for the mountpoint.

Adoption requires `config.json`, `compose.yaml`, `api-key`, and the slskd YAML file (defaults to `slskd/slskd.yml`) in the existing layout. The Compose service must be called `slskd`. Existing Compose, `.env`, slskd YAML, download files and state are retained in place. The adapter config is backed up to `config.before-setup.json` before adding `slskdConfigPath` if missing. Compose uses the same project directory, rather than creating a second deployment. Your existing image tag and share mappings are retained.

Adoption does not import arbitrary Docker deployments, discover credentials, cancel transfers or switch Soulseek accounts. Stop SoulseekQt if it still uses the username configured in slskd. Preserve any custom Compose environment files. If your existing deployment used a custom Compose project name, add that same `name:` to compose.yaml before adoption so Compose addresses the existing containers.

## Everyday commands

Append `--home "$HOME/slskfs-runtime"` for the adopted installation:

```bash
./out/soulfuse friends list
./out/soulfuse friends add alice
./out/soulfuse friends remove alice
./out/soulfuse users list
./out/soulfuse users add alice
./out/soulfuse users remove alice
./out/soulfuse shares list
```

Friends govern incoming access. Users govern which remote users appear in the mount. Friend edits use the existing slskd config watcher; see FRIENDS.md for restrictions and queued-transfer behavior. Restart SoulFUSE after changing mounted users.

For shared-folder changes, stop first, change the bindings, then start:

```bash
./out/soulfuse stop
./out/soulfuse shares add Books /absolute/path/to/books
./out/soulfuse start
# Later, with services stopped:
./out/soulfuse shares remove Books
```

Share edits update both Compose and slskd YAML and create `.before-shares` backups. They preserve other settings but may reformat YAML and remove comments. Only shares added with `shares add` can be removed by that command. Wizard-created and adopted mappings remain editable in Compose and slskd YAML. Avoid concurrent manual/web edits. If interrupted between writes, restore both `.before-shares` files before restarting. `stop` stops the mount before stopping slskd; files and volumes are never removed.

## Configuration ownership and persistence

The existing files remain authoritative: `config.json` for mount/cache settings, `slskd/slskd.yml` for Soulseek credentials/access and share aliases, and `compose.yaml` for container bindings. `installation.json` only holds the mountpoint and executable paths. There is no duplicate friend or user database. The commands coordinate these files; this is not yet a general-purpose configuration UI.

`enable` enables startup with the systemd user session. `disable` prevents future session startup and does not stop a running service. For startup without an interactive Linux login, an administrator can enable lingering for the account (`sudo loginctl enable-linger "$USER"`). This does not start a stopped WSL distribution or Docker Desktop after Windows boots.

The mount restarts on failure; its startup waits for an authenticated slskd API response. Docker restarts slskd according to the Compose restart policy. User logout may stop the mount while Docker continues serving shares. Use `soulfuse stop` to stop both explicitly. Startup API readiness does not prove Soulseek login or incoming peer reachability; inspect slskd logs/web UI for those.

Existing config-first commands remain supported. They still require the API key environment variable when they instantiate their own API client:

```bash
export SLSKD_API_KEY="$(cat "$HOME/slskfs-runtime/api-key")"
./out/soulfuse "$HOME/slskfs-runtime/config.json" fetch '/alice/Books/example.pdf'
```

Setup refuses to overwrite an already managed installation. To update installed binaries, stop services, copy the rebuilt `out/` contents into `RUNTIME/bin/`, then start again. Do not rerun adoption for an upgrade.

## Validation boundaries

Automated tests cover generated configuration, secret file permissions, repeat-setup refusal, friend/user/share commands, lifecycle argument/environment wiring and adoption preserving existing files. Docker/systemctl are substituted in these tests. Real Docker creation, systemd startup, WSL reboot, inbound networking and mounting must be verified on the host. `tests/setup-smoke.py DOTNET_PATH out/SlskFs.dll` runs the isolated setup tests after a full build.
