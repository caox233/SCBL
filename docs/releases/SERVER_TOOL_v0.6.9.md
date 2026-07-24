# SCBL Server Tool v0.6.9

This is a server-tool-only release. The Windows client remains v0.6.3 and the Hooks source/binary are unchanged.

## DDNS-GO native management

- Moves DNS provider, domain, A/AAAA, network-interface and IPv6 matching configuration entirely to the official DDNS-GO Web UI.
- Removes SCBL-owned IPv4/IPv6 helper commands, mode enforcement and configuration watcher services.
- Backs up the DDNS-GO configuration before migrating only entries that still reference the removed SCBL helper commands to DDNS-GO's native `netInterface` method.
- Preserves provider credentials, domains and all unrelated DDNS-GO settings.
- Binds the Web UI only to an RFC1918 private IPv4 address on port 9876, falling back to `127.0.0.1:9876` when no private address exists. It never defaults to `0.0.0.0`, a public IPv4 address or IPv6.
- Limits SCBL management to install/update, start/restart, status/logs, password reset and uninstall while preserving configuration backups.

## Verified compatibility

- Server Tool v0.6.8 was verified on a real Ubuntu 26 Server to repair the affected `service.toml`.
- Windows client v0.6.3 can enter online mode after the server repair.
- `5th-echelon.db`, the Windows client and Hooks are not modified by this release.
