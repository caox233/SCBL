#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[1]
main = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted((root / "client/ScblPublicLauncher").glob("MainWindow*.cs"))
)
control = (root / "client/ScblPublicLauncher/Services/ControlPlaneService.cs").read_text(encoding="utf-8")
log = (root / "client/ScblPublicLauncher/Services/LogService.cs").read_text(encoding="utf-8")
diag = (root / "client/ScblPublicLauncher/Services/DiagnosticExportService.cs").read_text(encoding="utf-8")
channel = (root / "client/ScblPublicLauncher/Models/ClientUpdateChannel.cs").read_text(encoding="utf-8")
server = (root / "server/scbl_control_plane.py").read_text(encoding="utf-8")

assert "GetControlPlaneSigningSecret" in main
assert "scbl-easytier-client.toml" in main
assert "TryReadAuthorizationFailure" in control
assert "clock_skew" in control
assert "EscapedTomlSecretRegex" in log
assert "TicketKeyArrayRegex" in log
assert "LauncherExecutableSha256" in diag
assert 'private const string TestAlias = "--test";' in channel
assert 'requestedValues.Add("test")' in channel
assert "authorization_failure_reason" in server
assert "invalid_signature" in server
assert "serverTimeUnixMs" in server
print("client signing, auth diagnostics, --test alias and redaction source checks passed")
