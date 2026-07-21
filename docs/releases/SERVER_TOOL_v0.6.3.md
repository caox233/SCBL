# SCBL Server Tool v0.6.3

## Main changes

- Adds direct download of the latest stable Windows client Release.
- Reuses the existing incoming-directory and file-level delta publication flow.
- Adds online self-upgrade for the installed `SCBL` manager and control plane.
- Verifies release manifests, SHA256, archive paths and script syntax.
- Backs up the existing manager and control plane and rolls back on health failure.
- Preserves `scbl.env`, `5th-echelon.db`, client updates, incoming packages,
  backups and DDNS-GO configuration.
- Keeps the current and immediately previous client release for rollback.
- Uses independent `client-stable-latest`, `server-tool-stable-latest` and
  `scbl-public-stable-latest` channels.

## Files

- `SCBL-Server-Tool-v0.6.3-linux-x86_64.tar.gz`
- `SCBL-Server-Tool-v0.6.3-linux-x86_64.tar.gz.sha256`
- `SCBL-Server-Tool-latest-linux-x86_64.tar.gz`
- `SCBL-Server-Tool-latest-linux-x86_64.tar.gz.sha256`
- `server-tool-release-manifest.json`
- `install-server.sh`
- `SHA256SUMS.txt`
