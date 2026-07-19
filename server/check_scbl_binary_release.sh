#!/usr/bin/env bash
set -euo pipefail
TAG="${1:-scbl-public-stable-latest}"
BASE="https://github.com/caox233/5th-echelon/releases/download/$TAG"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fL --retry 3 "$BASE/dedicated_server-linux-x86_64" -o "$TMP/dedicated_server-linux-x86_64"
curl -fsSL --retry 3 "$BASE/dedicated_server-linux-x86_64.sha256" -o "$TMP/dedicated_server-linux-x86_64.sha256"
(
  cd "$TMP"
  sha256sum -c dedicated_server-linux-x86_64.sha256
  file dedicated_server-linux-x86_64
)
echo "SCBL GitHub Release 二进制下载与校验正常。"
