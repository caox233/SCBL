# SCBL Client Component Update v2

## Goal

Separate frequently changing runtime components from `SplinterCellCNLauncher.exe` so Hooks, Route Guard, EasyTier and Updater can be built, published, downloaded, tested and rolled back independently without rebuilding the complete Windows client package.

The formal full-client version gate remains mandatory and continues to use `client_update_manifest.json`. Component channels do not bypass or replace that gate.

## Update channel selection

The launcher accepts a command-line-only component channel override:

```text
SplinterCellCNLauncher.exe --update-channel test
SplinterCellCNLauncher.exe --update-channel stable
```

The equivalent `--update-channel=test` and `--update-channel=stable` forms are also accepted.

Rules:

- No argument: use `stable`.
- `stable`: use the production component manifest.
- `test`: use the test component manifest.
- Missing, unsupported or conflicting values: fail closed to `stable` and write a warning to the launcher log.
- The argument is preserved through administrator elevation.
- The channel is not saved in `launcher_settings.json`; closing the test shortcut and starting the normal shortcut returns to `stable`.
- Cached component state records its channel. A `test` cache is never applied by a normal `stable` launch.

## Manifest endpoints

```text
components/channels/stable/client_components_v2.json
components/channels/test/client_components_v2.json
```

The existing formal full-client manifest remains:

```text
client_update_manifest.json
```

A future signature file uses the component manifest path with `.sig` appended. Until signature verification is implemented in Launcher, stable external component activation remains disabled.

## Component catalog

| Component name | Artifact | Update mode | Activation |
|---|---|---|---|
| `hooks` | `uplay_r1_loader.dll` | `before-game-start` | Same launch, immediately before game start |
| `route-guard` | `route-guard.zip` | `next-launch` | Next same-channel launch, before network/runtime start |
| `easytier` | `easytier-windows-x86_64.zip` | `next-launch` | Next same-channel launch, before EasyTier starts |
| `updater` | `SCBL.Updater.exe` | `next-launch` | Next same-channel launch, while Updater is stopped |

`route-guard.zip` is an atomic compatibility bundle:

```text
scbl-process-router.exe
WinDivert.dll
WinDivert64.sys
```

`easytier-windows-x86_64.zip` is also atomic:

```text
easytier-core.exe
easytier-cli.exe
```

Launcher remains self-updated by Updater after Launcher exits. A complete client package remains the repair and platform-upgrade unit.

## Runtime reconciliation

After the formal version check and virtual network are ready, the selected component manifest is reconciled:

```text
fetch manifest
-> validate schema, channel and supported component catalog
-> validate same-origin URL, required filename, update mode and minimum Launcher version
-> inspect versioned local cache
-> reuse exact size/SHA256 match
-> otherwise download to temporary file
-> verify size and SHA256
-> atomically replace cache entry
-> write aggregate component_state.json
```

The cache layout is versioned and immutable from the Launcher perspective:

```text
%LOCALAPPDATA%/SCBL_Public/components/
  hooks/<version>/uplay_r1_loader.dll
  route-guard/<version>/route-guard.zip
  easytier/<version>/easytier-windows-x86_64.zip
  updater/<version>/SCBL.Updater.exe
  component_state.json
```

A test-channel manifest or download failure blocks game launch instead of silently mixing old and new components.

## Two-stage activation

Hooks can be replaced immediately because the game is not running when the deployment step executes.

Route Guard, EasyTier and Updater are staged rather than replacing active runtime files:

1. The first `test` launch downloads and verifies them into the component cache.
2. `component_state.json` records the exact channel, version, path and SHA256.
3. The next `test` launch consumes only matching-channel state before EasyTier, Route Guard or the game is started.
4. Each target is written through a temporary file, verified, atomically moved and rolled back on failure.
5. Route Guard and EasyTier bundles reject unsafe ZIP paths and require every expected file.

Switching to a normal stable shortcut ignores staged test state.

## Bootstrap and full packages

Hooks is no longer a WPF `EmbeddedResource` and changing Hooks no longer changes the Launcher executable hash.

A complete client package carries a verified offline bootstrap copy at:

```text
bootstrap-components/hooks/uplay_r1_loader.dll
bootstrap-components/hooks/uplay_r1_loader.dll.sha256
```

Stable currently deploys this package bootstrap copy. Test can replace it with the verified test-channel component for that launch.

The formal package is assembled from independently produced component outputs. It includes a `client_package_manifest.json` with the size and SHA256 of each packaged file. Package assembly does not rebuild components that already passed their own validation.

## Build model

Routine work is component-scoped:

- Launcher changes run the Launcher workflow.
- Updater changes run the Updater workflow.
- Route Guard or WinDivert integration changes run the Route Guard workflow.
- EasyTier preparation changes run the EasyTier workflow.
- Hooks and dedicated server live in this repository and may use independent component workflows, while every artifact records the same SCBL source commit that produced it.
- Server component-manager changes run Linux Python/shell tests only.

Each workflow cancels superseded runs for the same PR and uses the relevant NuGet, Go or Rust cache.

The manual full-client workflow builds component jobs in parallel, downloads their artifacts, fetches the verified bootstrap Hooks asset and only then assembles the complete ZIP.

## Trust model

1. GitHub Actions builds each component once.
2. The workflow records the exact source commit, size and SHA256.
3. The exact artifact is published to `test`.
4. Multiplayer validation uses that exact SHA256.
5. Promotion to `stable` changes only the stable manifest reference; it does not rebuild the component.
6. Launcher will verify the signed manifest, then the downloaded component SHA256.
7. The complete package must consume the same tested artifacts rather than recompiling them.

The production server mirrors immutable artifacts and manifests. It must not treat a newly uploaded file as trusted merely because the server calculated a hash for it.

## Server-side publication flow

### Publish to test

```text
select component artifact
-> verify expected filename, source commit, size and SHA256
-> store under components/artifacts/<component>/<version>/
-> reject conflicting content for an existing version
-> update test manifest atomically
```

### Promote to stable

```text
select the current tested component
-> verify immutable artifact again
-> copy the exact test entry into stable manifest
-> sign stable manifest (future activation gate)
```

### Roll back stable

```text
select an existing immutable component version
-> verify artifact
-> replace only the stable manifest reference
-> sign stable manifest
```

No client package rebuild is required for publish, promotion or rollback.

## Protected boundaries

This component system does not change:

- the formal Launcher version gate;
- EasyTier network topology, virtual addressing or relay policy;
- Route Guard packet policy;
- the dedicated-server database or migrations;
- server secrets, DDNS configuration or `service.toml`;
- the rule that a tested artifact must be promoted without rebuilding.

Stable external activation and any production deployment remain separate work gated by signed-manifest verification and explicit validation evidence.
