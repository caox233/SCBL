#!/usr/bin/env python3
from pathlib import Path

manager = Path('server/install_public_server.sh').read_text(encoding='utf-8')
bootstrap = Path('scripts/install-server.sh').read_text(encoding='utf-8')
client_workflow = Path('.github/workflows/client-release.yml').read_text(encoding='utf-8')
server_workflow = Path('.github/workflows/server-tool-release.yml').read_text(encoding='utf-8')

assert 'release_tag="$(manifest_value "$manifest" releaseTag)"' in manager
assert 'package_base="${SCBL_CLIENT_PACKAGE_BASE_URL:-https://github.com/${repo}/releases/download/${release_tag}}"' in manager
assert 'package_base="${SCBL_SERVER_TOOL_PACKAGE_BASE_URL:-https://github.com/${repo}/releases/download/${release_tag}}"' in manager
assert '"$package_base/$package"' in manager
assert 'release_tag="$(manifest_value releaseTag)"' in bootstrap
assert 'PACKAGE_BASE="${SCBL_PACKAGE_BASE_URL:-https://github.com/${REPO}/releases/download/${release_tag}}"' in bootstrap
assert 'releaseTag = "client-v$version"' in client_workflow
assert 'gh release upload client-stable-latest dist/client-release-manifest.json' in client_workflow
assert '"releaseTag": "server-tool-v${version}"' in server_workflow
assert 'gh release upload server-tool-stable-latest dist/install-server.sh dist/server-tool-release-manifest.json' in server_workflow
assert 'cp "dist/$package" dist/SCBL-Server-Tool-latest-linux-x86_64.tar.gz' not in server_workflow
assert 'gh release upload server-tool-stable-latest dist/*' not in server_workflow
assert 'gh release delete-asset "$tag" "$obsolete"' in server_workflow
print('Release manifest routing checks passed')
