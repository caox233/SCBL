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

# These files are required Server Tool runtime dependencies and must be present
# in the release source list, bootstrap installer and archive verification loop.
required_runtime_files = (
    "scbl_update_server.py",
    "scbl_component_manager.py",
    "scbl_publish_hooks_bundle.py",
    "scbl_invite_test_manager.py",
    "install_component_manager.sh",
)
for name in required_runtime_files:
    assert f"server/{name}" in server_release
    assert name in bootstrap

assert 'tar -tzf "dist/$package" > "$archive_list"' in server_release
assert 'grep -Fxq "$root/$required" "$archive_list"' in server_release
assert 'update_server_file="$package_root/scbl_update_server.py"' in bootstrap
assert 'component_manager_file="$package_root/scbl_component_manager.py"' in bootstrap
assert 'invite_test_file="$package_root/scbl_invite_test_manager.py"' in bootstrap
assert 'update_new="${package_root}/scbl_update_server.py"' in manager
assert 'install -m 0644 "$update_new" "$MANAGER_DIR/scbl_update_server.py"' in manager

print("direct formal release routing and package dependency checks passed")
