#!/usr/bin/env bash
set -euo pipefail

component="${1:?component is required: client or server-tool}"
keep_count="${2:-2}"

case "$component" in
  client) prefix='client-v' ;;
  server-tool) prefix='server-tool-v' ;;
  *)
    echo "unsupported component: $component" >&2
    exit 2
    ;;
esac

mapfile -t tags < <(
  gh release list --limit 200 --json tagName --jq '.[].tagName' |
    grep -E "^${prefix}[0-9]+\.[0-9]+\.[0-9]+$" |
    sort -Vr || true
)

for ((i=keep_count; i<${#tags[@]}; i++)); do
  tag="${tags[$i]}"
  echo "Removing obsolete release: $tag"
  gh release delete "$tag" --cleanup-tag -y
done
