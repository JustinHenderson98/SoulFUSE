# SoulFUSE friends

Friend management controls **who may access your slskd shares**. The SoulFUSE `users` configuration independently controls **whose shares you mount**. Adding a friend does not mount their shares or grant different folders to different friends.

## Your WSL setup

Add this top-level setting to `$HOME/slskfs-runtime/config.json`:

```json
"slskdConfigPath": "/home/jeh24/slskfs-runtime/slskd/slskd.yml"
```

Use the host file path, not `/app/slskd.yml`. Your Compose bind mount `./slskd:/app` already makes this file visible to slskd. Run commands as the same account owning the file and running slskd; the directory must be writable for atomic replacement. Do not configure this file as an individual Docker bind mount: bind its parent directory, as in your setup, so atomic replacement becomes visible in the container.

```bash
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends list
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends add alice
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends remove alice
./out/soulfuse "$HOME/slskfs-runtime/config.json" friends add 'friend.with+punctuation'
```

Commands need neither a running SoulFUSE mount nor an API key. `remote_configuration: true` is not required: these commands edit the host YAML directly. Quote usernames in the shell. Output contains usernames and status, never the YAML/passwords.

## How access is stored

The allowlist is persisted in slskd's own `transfers.groups.blacklisted.patterns`, so slskd continues enforcing it when SoulFUSE stops. SoulFUSE translates the list into one escaped regex. Your existing expression `^(?!(?:jeh24|jeh24-slskfs)$).*$` is automatically recognized, preserving those two users when a third is added. Newly written expressions use absolute string anchors and match all nonmembers, including names containing newlines.

The commands recognize literal-name allowlists in the legacy form above or SoulFUSE's generated form. They do not reverse-engineer arbitrary regex policies. Multiple recognized allowlists are rejected for manual resolution. `list` reports `configured: false` if none is recognized. The first `add` establishes an allowlist; `remove` refuses when there is no managed allowlist. Removing the last friend writes a block-everyone pattern, never a public-share policy.

Other blacklist patterns, explicit blocked-user `members`, CIDR rules and managed IP blacklists remain in force. Adding a username cannot override those deny rules. Case sensitivity follows `flags.case_sensitive_regex`; default is insensitive. Do not manage these settings concurrently with another editor.

No metadata sidecar is needed: the generated pattern itself stores the literal usernames. The `.soulfuse.lock` file is a process lock, not the friend database.

## Initializing another configuration

The three mappings and the `patterns` list must already exist. If they do not, merge this into your YAML (do not create duplicate `transfers` keys):

```yaml
transfers:
  groups:
    blacklisted:
      patterns:
        - '\A[\s\S]*\z'
```

This initialization blocks everyone until you add friends. An existing `patterns: []` is also supported, but has no allowlist restriction until the first add. Never temporarily remove your current restriction merely to initialize this feature.

## Editing and backup

SoulFUSE parses the YAML using YamlDotNet, replaces only the `patterns` field, validates the resulting document, and atomically replaces the file. Unrelated content is preserved; formatting/comments inside the patterns field may be normalized. A single previous-version backup is saved alongside it as `slskd.yml.soulfuse.bak`, with owner-only permissions because it includes credentials. Restore that backup manually only after reviewing the access policy it contains.

The editor rejects YAML anchors/merges, complex keys, nonempty flow-style pattern lists, and multiline regex strings. Convert those to plain block-style YAML before using the commands. Files over 2 MiB and symlink config files are rejected. Local edits are serialized by a file lock, and intervening edits are checked before replacement. There is no shared transaction with slskd's web editor; avoid simultaneous edits. Keep the containing directory trusted.

## Reload and revocation

A successful command confirms the file was saved, **not that slskd has enforced it yet**. With normal configuration watching enabled, slskd reloads the file. With `flags.no_config_watch: true`, restart it. Environment/command-line configuration can override or combine with YAML settings; inspect effective options if behavior differs.

Check the slskd logs after your first change and test browsing from an allowed and a non-allowed account. Do not paste logs containing credentials. The new friend commands were tested against local files, not a live Soulseek account.

Removing a friend prevents new operations once slskd applies the rule. It does not automatically cancel previously queued or active transfers or retract previously downloaded files. Cancel those transfers separately in slskd when immediate revocation is needed. These commands do not provide per-folder permissions, invitations or cryptographic identities.
