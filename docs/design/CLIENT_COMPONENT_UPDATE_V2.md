# SCBL Client Component Update v2

## Goal

Separate frequently changing runtime components from `SplinterCellCNLauncher.exe` so Hooks, Route Guard, EasyTier and Updater can be built, published, tested and rolled back independently without rebuilding the complete Windows client package.

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
- The argument is preserved through the existing administrator-elevation restart.
- The channel is not saved in `launcher_settings.json`; closing the test shortcut and starting the normal shortcut returns to `stable`.
- A running launcher keeps its original channel. Close the existing launcher before starting another shortcut with a different channel.

## Manifest endpoints

The component updater will resolve exactly one of these paths under the configured SCBL update base URL:

```text
components/channels/stable/client_components_v2.json
components/channels/test/client_components_v2.json
```

A future signature file will use the same path with `.sig` appended.

The existing full-client manifest remains:

```text
client_update_manifest.json
```

It is always treated as the formal production launcher/version source regardless of the component channel.

## Component responsibilities

| Component | Updated by | Normal replacement point |
|---|---|---|
| `uplay_r1_loader.dll` | Launcher | Before game launch |
| `scbl-process-router.exe` and WinDivert files | Launcher | Before network/runtime start |
| EasyTier runtime | Launcher | Before network/runtime start |
| `SCBL.Updater.exe` | Launcher | While Updater is not running |
| `SplinterCellCNLauncher.exe` | Updater | After launcher exits |
| Complete client package | Updater | Repair or launcher/platform upgrade |

## Local layout

Proposed runtime cache:

```text
components/
  hooks/uplay_r1_loader.dll
  route-guard/scbl-process-router.exe
  route-guard/WinDivert.dll
  route-guard/WinDivert64.sys
  easytier/easytier-core.exe
  easytier/easytier-cli.exe
  updater/SCBL.Updater.exe
component_state.json
```

The launcher must download to a temporary file, verify it, then atomically replace the cached component. The game-directory Hooks DLL is deployed only from the verified cache.

## Trust model

1. GitHub Actions builds each component once.
2. The workflow calculates SHA256 values and records the source commit.
3. The exact artifact is published to the test channel.
4. Multiplayer validation uses that exact SHA256.
5. Promotion to stable changes only the stable manifest reference; it does not rebuild the component.
6. The launcher verifies the signed manifest, then verifies the downloaded component SHA256.

The production server should mirror immutable artifacts and manifests. It must not treat a newly uploaded file as trusted merely because it calculated a matching hash locally.

## Server-side publication flow

### Publish to test

```text
Select component
-> fetch immutable GitHub artifact and checksum
-> verify source commit and SHA256
-> store under versioned artifact directory
-> update test manifest
-> sign test manifest
-> reload/update index
```

### Promote to stable

```text
Select already tested component SHA256
-> verify artifact still matches
-> update stable manifest to the same artifact
-> sign stable manifest
```

### Roll back stable

```text
Select previous immutable artifact
-> update stable manifest reference
-> sign stable manifest
```

No client package rebuild is required for these three operations.

## Implementation phases

### Phase 1: channel foundation

- Parse `--update-channel stable|test`.
- Preserve the argument through UAC elevation.
- Default and fail closed to `stable`.
- Expose the selected channel to launcher services.
- Document shortcut usage and the stable/full-client trust boundary.

### Phase 2: read-only component check

- Add component manifest models and validation.
- Resolve stable/test manifest path from the parsed channel.
- Log local and required component versions/hashes.
- Do not replace files yet.

### Phase 3: Hooks externalization

- Stop treating Hooks as a resource that must change with every launcher build.
- Keep a compatible embedded Hooks binary only as an offline recovery fallback.
- Download, verify and cache the selected-channel Hooks component.
- Deploy the verified cache into the game directory before launch.
- Add atomic replacement and `.bak` rollback.

### Phase 4: other runtime components

- Move Route Guard, WinDivert, EasyTier and Updater to the component manifest.
- Keep launcher self-update and complete repair in Updater.

### Phase 5: service-tool management

- Add test publication, promotion, stable rollback, manifest verification and channel status to the Linux Server Tool.
- Preserve `/opt/scbl-public/server/5th-echelon.db`, existing configuration, client packages and rollback files.

## Release boundaries

- Phase 1 is a foundation change only; it must not claim that the test manifest is already active.
- The first functional component-channel release requires both Phase 2 and Phase 3.
- No production server files or dedicated server binary are changed during design/foundation work.
- EasyTier topology, Route Guard policy and virtual IP behavior are outside this update-system change.
