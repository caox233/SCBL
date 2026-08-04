#!/usr/bin/env python3
from pathlib import Path

bootstrap = Path("server/bootstrap/install.sh").read_text(encoding="utf-8")
wrapper = Path("scripts/install-server.sh").read_text(encoding="utf-8")
builder = Path("server/manager/build.py").read_text(encoding="utf-8")
release = Path("server/manager/scblctl/release.py").read_text(encoding="utf-8")
provision = Path("server/manager/scblctl/provision.py").read_text(encoding="utf-8")
config = Path("server/manager/scblctl/config.py").read_text(encoding="utf-8")

assert "VERSION_SERVER_TOOL" in bootstrap
assert "server-tool-v$VERSION/scblctl.pyz" in bootstrap
assert "sha256sum --check --strict" in bootstrap
assert "server/bootstrap/install.sh" in wrapper
assert "install_public_server.sh" not in wrapper
assert 'interpreter="/usr/bin/env python3"' in builder
assert 'packageType": "scbl-server-runtime"' in release
assert "manifest.verify(package_dir)" in provision
assert "activate_release" in provision
assert "_rollback" in provision
assert "/etc/scbl/server.toml" in config or "server.toml" in bootstrap

for required in (
    "dedicated_server",
    "scbl_control_plane.py",
    "scbl_update_server.py",
    "easytier-core",
    "easytier-cli",
    "data/mp_balancing.ini",
):
    assert f'"{required}"' in release

combined = "\n".join((bootstrap, wrapper, builder, release, provision, config))
assert "scbl.env" not in combined
assert "/opt/scbl-public" not in combined
assert "migrate_legacy" not in combined

print("SCBL 2.0 clean-install, verified runtime and rollback routing checks passed")
