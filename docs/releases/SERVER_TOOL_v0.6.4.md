# SCBL Server Tool v0.6.4

This server-tool-only release does not rebuild or change the Windows client.

## Changes

- Adds rollback to the retained previous Windows client package.
- Adds rollback to the latest server-tool upgrade backup with a pre-rollback safety copy.
- Requires component/version/file agreement in downloaded Release manifests.
- Rejects unsafe archive member types and paths during bootstrap and online upgrade.
- Compiles every embedded Python heredoc before installing a downloaded manager.
- Keeps configuration, database, client update data, incoming files and DDNS-GO settings unchanged.
