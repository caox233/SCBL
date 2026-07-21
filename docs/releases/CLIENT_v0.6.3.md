# SCBL Windows Client v0.6.3

## Main changes

- Separates the Windows client release from the Linux server-tool release.
- Keeps the exact launcher-owned PID authorization model for Route Guard.
- Replaces TCP and UDP fallback owner-table scans with precomputed O(1) indexes.
- Keeps owner-table refresh in the background and logs unusually slow refreshes.
- Continues to obtain the approved 5th Hooks DLL from `caox233/5th-echelon`.
- Does not require a Linux server-tool upgrade solely because this client version changes.

## Compatibility

- Control API version: 1
- Minimum server tool: 0.6.2
- 5th binary channel: `scbl-public-stable-latest`

## Files

- `SCBL-Client-v0.6.3-win-x86.zip`
- `SCBL-Client-v0.6.3-win-x86.zip.sha256`
- `client-release-manifest.json`
- `SHA256SUMS.txt`
