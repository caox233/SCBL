# Componentized build and update baseline

This branch changes development and packaging boundaries without publishing a Release or changing the production server.

## Implemented

- Generic server component catalog for Hooks, Route Guard, EasyTier and Updater.
- Immutable test/stable component storage, exact-artifact promotion and rollback.
- Client reconciliation by version, size and SHA256; matching files are reused and only missing or mismatched components are downloaded.
- Hooks activation before game start.
- Route Guard, EasyTier and Updater staging followed by same-channel next-launch activation before runtime startup.
- Channel-bound component state so test files are never applied by a normal stable launch.
- Hooks removed from Launcher embedded resources.
- Full packages carry a separately verified bootstrap Hooks component.
- Independent Launcher, Updater, Route Guard and EasyTier workflows with path filters, caches and superseded-run cancellation.
- Manual full-package assembly from independently built artifacts.
- PowerShell and workflow syntax validation.

## Deliberately not activated

- Stable external components remain read-only until signed-manifest verification is implemented.
- No production server files, database, EasyTier topology or Route Guard packet policy are changed.
- No Client or Server Tool Release is published by this refactor.
- 5th-echelon multiplayer logic PR #14 remains gated by the exact two-client game test.
