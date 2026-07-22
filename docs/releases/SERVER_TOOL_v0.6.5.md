# SCBL Server Tool v0.6.5

This is a server-tool-only bootstrap reliability hotfix. It does not rebuild or change the Windows client.

## Changes

- Adds explicit progress messages between package download, checksum verification, extraction and manager startup.
- Adds maximum execution times to GitHub downloads and local validation steps.
- Opens `/dev/tty` explicitly and connects the downloaded interactive manager to it when the bootstrap is executed through `curl | sudo bash`.
- Preserves configuration, database, client update data, incoming files, backups and DDNS-GO settings.
