# EasyTier runtime

SCBL v0.5.0 uses the official EasyTier binaries without changing their source code.

Run `download_easytier_windows.ps1` before building. The default pinned version is v2.6.4 and can be overridden with `EASYTIER_VERSION`. If GitHub cannot be reached, download the official `easytier-windows-x86_64-<version>.zip` and set `EASYTIER_WINDOWS_PACKAGE` to the local path.

The generated `bin` directory is copied into the launcher's `tools` directory.
