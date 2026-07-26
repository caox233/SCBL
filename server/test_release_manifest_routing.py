#!/usr/bin/env python3
from pathlib import Path

manager = Path("server/install_public_server.sh").read_text(encoding="utf-8")
bootstrap = Path("scripts/install-server.sh").read_text(encoding="utf-8")
client_release = Path(".github/workflows/client-release.yml").read_text(encoding="utf-8")
server_release = Path(".github/workflows/server-tool-release.yml").read_text(encoding="utf-8")

for text in (manager, bootstrap, client_release, server_release):
    assert "client-stable-latest" not in text
    assert "server-tool-stable-latest" not in text
assert "VERSION_CLIENT" in manager
assert "client-v${version}" in manager
assert "VERSION_SERVER_TOOL" in manager
assert "server-tool-v${version}" in manager
assert "VERSION_SERVER_TOOL" in bootstrap
assert "server-tool-v${version}" in bootstrap
assert "client-release-manifest.json" not in client_release
assert "server-tool-release-manifest.json" not in server_release
assert "[CLIENT] Windows Client v${version}" in client_release
assert "[SERVER] Server Tool v${version}" in server_release

# The dual-stack update service is a required Server Tool runtime dependency.
assert "server/scbl_update_server.py" in server_release
assert 'tar -tzf "dist/$package" | grep -Fxq "$root/scbl_update_server.py"' in server_release
assert "scbl_update_server.py" in bootstrap
assert 'update_server_file="$package_root/scbl_update_server.py"' in bootstrap
assert 'update_new="${package_root}/scbl_update_server.py"' in manager
assert 'install -m 0644 "$update_new" "$MANAGER_DIR/scbl_update_server.py"' in manager

print("direct formal release routing and package dependency checks passed")
