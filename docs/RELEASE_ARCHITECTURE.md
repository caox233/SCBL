# SCBL component release architecture

SCBL uses independent component versions so a client-only change does not rebuild or
renumber the Linux server tool, and a server-script-only change does not start a
Windows build.

## Components

| Component | Version source | Immutable release | Rolling stable release |
|---|---|---|---|
| Windows client | `VERSION_CLIENT` and `client/SCBL.Version.props` | `client-vX.Y.Z` | `client-stable-latest` |
| Linux server tool | `VERSION_SERVER_TOOL` and `SERVER_TOOL_VERSION` | `server-tool-vX.Y.Z` | `server-tool-stable-latest` |
| 5th dedicated server and Hooks | `caox233/5th-echelon` | project release | `scbl-public-stable-latest` |

The root `VERSION` remains a compatibility alias for the public Windows client.
`COMPONENT_VERSIONS.json` records the current compatible component set.

## Client delivery

The server manager can download `client-release-manifest.json` and the client ZIP
from `client-stable-latest`. It verifies SHA256 and the ZIP structure, then moves the
validated package into `/opt/scbl-public/incoming/client/`. The existing package
watcher continues to generate file-level delta manifests, update announcements and
the locally hosted full package.

The server keeps the current and immediately previous client release for rollback.
The update manifest is written through a temporary file and atomically replaced.

## Server-tool upgrade

The installed `SCBL` manager downloads only from `server-tool-stable-latest`.
It validates the release manifest, SHA256, tar paths, Bash syntax and Python syntax.
Before replacement it backs up the manager and control plane. Configuration, the
5th database, client update data, incoming files, backups and DDNS-GO settings are
not overwritten. Only the control plane is restarted when its source changed.

## Route Guard

Route Guard still authorizes the exact launcher-owned game PID set. Game IPv4
TCP/UDP packets addressed to the SCBL virtual subnet are pinned to the EasyTier
interface and source address; other destinations from those PIDs are blocked.
Other applications are reinjected unchanged.

Version 0.6.3 precomputes TCP and UDP local-port owner indexes during the background
IP Helper refresh. Packet-path fallback lookups are O(1), removing the previous
full owner-table scans. `owner-unknown` remains fail-open for unrelated-system
safety and is recorded for diagnostics; IPv6, loopback-proxy transfer and fragment
hardening remain separate compatibility-sensitive work.
