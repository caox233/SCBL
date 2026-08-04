#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPOSITORY_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
EASYTIER_VERSION="${1:-v2.6.4}"
VERSION="${2:-$(tr -d '[:space:]' < "$REPOSITORY_ROOT/VERSION_SERVER_TOOL")}"
[[ "$EASYTIER_VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]

source "$HOME/.cargo/env"
BUILD_ROOT="$(mktemp -d -t scbl-linux-build.XXXXXX)"
DOWNLOAD_ROOT="$(mktemp -d -t scbl-easytier.XXXXXX)"
cleanup() { rm -rf "$BUILD_ROOT" "$DOWNLOAD_ROOT"; }
trap cleanup EXIT

export RUSTUP_TOOLCHAIN=nightly-2025-10-15
export CARGO_TARGET_DIR="$BUILD_ROOT/target"
cd "$REPOSITORY_ROOT"
cargo build --locked --release --package dedicated_server
DEDICATED="$CARGO_TARGET_DIR/release/dedicated_server"
file "$DEDICATED" | grep -Fq 'ELF 64-bit'
if ldd "$DEDICATED" 2>&1 | grep -Fq 'not found'; then
  echo 'Dedicated Server has unresolved shared-library dependencies:' >&2
  ldd "$DEDICATED" >&2
  exit 1
fi

EASYTIER_ZIP="$DOWNLOAD_ROOT/easytier.zip"
curl -fL --retry 3 --retry-all-errors --connect-timeout 10 --max-time 300 \
  "https://github.com/EasyTier/EasyTier/releases/download/$EASYTIER_VERSION/easytier-linux-x86_64-$EASYTIER_VERSION.zip" \
  -o "$EASYTIER_ZIP"
unzip -q "$EASYTIER_ZIP" -d "$DOWNLOAD_ROOT/extract"
EASYTIER_CORE="$(find "$DOWNLOAD_ROOT/extract" -type f -name easytier-core -print -quit)"
EASYTIER_CLI="$(find "$DOWNLOAD_ROOT/extract" -type f -name easytier-cli -print -quit)"
[[ -n "$EASYTIER_CORE" && -n "$EASYTIER_CLI" ]]
file "$EASYTIER_CORE" | grep -Fq 'ELF 64-bit'
file "$EASYTIER_CLI" | grep -Fq 'ELF 64-bit'

OUTPUT="$REPOSITORY_ROOT/server/packaging/dist/SCBL-Server-Runtime-v$VERSION-linux-x86_64.tar.gz"
bash "$SCRIPT_DIR/build-runtime.sh" \
  --output "$OUTPUT" \
  --dedicated "$DEDICATED" \
  --easytier-core "$EASYTIER_CORE" \
  --easytier-cli "$EASYTIER_CLI"
echo "SCBL_RUNTIME_OUTPUT=$OUTPUT"
