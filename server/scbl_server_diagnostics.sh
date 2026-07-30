#!/usr/bin/env bash
set -euo pipefail

# SCBL server diagnostic collector. It only reads current state and never stops or
# reconfigures services. Sensitive configuration values are redacted before the
# archive is created; the account database itself is never copied.

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行：sudo scbl-server-diagnostics [journalctl时间范围]" >&2
  exit 1
fi

RAW_SINCE="${1:-3 hours ago}"
# A previous menu/invocation could pass a bare numeric value (for example "1"),
# which journalctl rejects as a timestamp. Treat it as an hour count instead.
if [[ "$RAW_SINCE" =~ ^[0-9]+$ ]]; then
  SINCE="$RAW_SINCE hours ago"
else
  SINCE="$RAW_SINCE"
fi
if ! journalctl --since "$SINCE" -n 0 --no-pager >/dev/null 2>&1; then
  echo "警告：日志时间范围无效：$SINCE；已回退到 3 hours ago。" >&2
  SINCE="3 hours ago"
fi

SCBL_ROOT="${SCBL_ROOT:-/opt/scbl-public}"
OUTPUT_DIR="${SCBL_DIAGNOSTIC_OUTPUT_DIR:-$PWD}"
STAMP="$(date '+%Y%m%d_%H%M%S')"
NAME="SCBL_Server_Diagnostics_${STAMP}"
WORK="$(mktemp -d -t "${NAME}.XXXXXX")"
ROOT="$WORK/$NAME"
ARCHIVE="$OUTPUT_DIR/$NAME.tar.gz"

cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT
umask 077
mkdir -p "$ROOT"/{commands,systemd,journals,files,db,crash}

safe_run() {
  local target="$1"; shift
  {
    printf 'Command:'
    printf ' %q' "$@"
    printf '\n\n'
    timeout --foreground 30s "$@"
  } >"$target" 2>&1 || true
}

redact_file() {
  local path="$1"
  [[ -f "$path" ]] || return 0
  python3 - "$path" <<'PY'
from pathlib import Path
import re, sys
p = Path(sys.argv[1])
try:
    text = p.read_text(encoding="utf-8", errors="replace")
except Exception:
    raise SystemExit(0)
marker = "***REDACTED***"
# Shell/env assignments.
text = re.sub(
    r"(?im)^(\s*(?:SCBL_SECRET|EASYTIER_SECRET|NETWORK_SECRET|PASSWORD|PASSWORD_HASH|TOKEN|ACCESS_TOKEN|API_KEY|PRIVATE_KEY)\s*=\s*).*$",
    lambda m: m.group(1) + marker,
    text,
)
# TOML and JSON scalar secrets.
text = re.sub(
    r'''(?im)^(\s*(?:network_secret|access_key|crypto_key|password|password_hash|token)\s*=\s*)"[^"]*"''',
    lambda m: m.group(1) + '"' + marker + '"',
    text,
)
text = re.sub(
    r'''(?i)("(?:network_secret|access_key|crypto_key|ticket_key|password|password_hash|token|secret|SCBL_SECRET)"\s*:\s*)"[^"]*"''',
    lambda m: m.group(1) + '"' + marker + '"',
    text,
)
# Multi-line TOML ticket-key arrays.
text = re.sub(
    r"(?ims)^(\s*ticket_key\s*=\s*)\[(?:.|\n)*?^\s*\]",
    lambda m: m.group(1) + '["' + marker + '"]',
    text,
)
# CLI arguments and URLs that may carry credentials.
text = re.sub(
    r'''(?i)(--(?:network-)?secret(?:=|\s+))(?:(?:"[^"]*")|[^\s]+)''',
    lambda m: m.group(1) + marker,
    text,
)
text = re.sub(r"(?i)(Authorization:\s*)(?:Bearer\s+)?\S+", lambda m: m.group(1) + marker, text)
p.write_text(text, encoding="utf-8")
PY
}

copy_text_redacted() {
  local source="$1" destination="$2"
  [[ -f "$source" ]] || return 0
  mkdir -p "$(dirname "$destination")"
  cp -a "$source" "$destination" 2>/dev/null || cat "$source" >"$destination"
  redact_file "$destination"
}

cat >"$ROOT/README.txt" <<EOF
SCBL server diagnostic bundle
Generated: $(date --iso-8601=seconds)
Journal range: $SINCE

This archive contains service logs, network state, non-sensitive database
metadata and file hashes. It does NOT contain the account database, password
hashes, game saves or private keys. Known secret fields are redacted.
EOF

cat >"$ROOT/summary.txt" <<EOF
GeneratedAt=$(date --iso-8601=seconds)
Hostname=$(hostname 2>/dev/null || true)
Kernel=$(uname -srmo 2>/dev/null || true)
JournalSince=$SINCE
ScblRoot=$SCBL_ROOT
ServerToolVersion=$(tr -d '[:space:]' < /usr/local/lib/scbl-public/VERSION_SERVER_TOOL 2>/dev/null || echo unknown)
EOF

SERVICES=(
  scbl-tunnel.service
  scbl-dedicated.service
  scbl-control-plane.service
  scbl-update.service
  scbl-package-watch.service
  scbl-package-watch.timer
  ddns-go.service
)
for unit in "${SERVICES[@]}"; do
  safe_run "$ROOT/systemd/${unit}.status.txt" systemctl --no-pager --full status "$unit"
  safe_run "$ROOT/systemd/${unit}.show.txt" systemctl show "$unit" \
    -p Id -p LoadState -p ActiveState -p SubState -p MainPID -p ExecMainCode \
    -p ExecMainStatus -p Result -p NRestarts -p MemoryCurrent -p MemoryPeak \
    -p TasksCurrent -p ActiveEnterTimestamp -p FragmentPath
  safe_run "$ROOT/systemd/${unit}.cat.txt" systemctl cat "$unit"
  journalctl -u "$unit" --since "$SINCE" --no-pager -o short-iso-precise \
    >"$ROOT/journals/${unit}.log" 2>&1 || true
  redact_file "$ROOT/journals/${unit}.log"
done

safe_run "$ROOT/commands/os-release.txt" bash -lc 'cat /etc/os-release; echo; uname -a; echo; uptime'
safe_run "$ROOT/commands/uptime.txt" uptime
safe_run "$ROOT/commands/free.txt" free -h
safe_run "$ROOT/commands/df.txt" df -hT
safe_run "$ROOT/commands/ps.txt" ps -eo pid,ppid,user,etimes,%cpu,%mem,rss,vsz,stat,comm,args --sort=-rss
safe_run "$ROOT/commands/ss-listeners.txt" ss -lntup
safe_run "$ROOT/commands/ss-summary.txt" ss -s
safe_run "$ROOT/commands/ip-address.txt" ip -details address show
safe_run "$ROOT/commands/ip-route-v4.txt" ip -4 route show table all
safe_run "$ROOT/commands/ip-route-v6.txt" ip -6 route show table all
safe_run "$ROOT/commands/ip-rule.txt" ip rule show
safe_run "$ROOT/commands/ip-neighbour.txt" ip neigh show
safe_run "$ROOT/commands/iptables-save.txt" iptables-save
safe_run "$ROOT/commands/ip6tables-save.txt" ip6tables-save
safe_run "$ROOT/commands/nft-ruleset.txt" nft list ruleset
safe_run "$ROOT/commands/dmesg-oom.txt" bash -lc "dmesg --ctime 2>/dev/null | grep -Ei 'out of memory|oom-killer|killed process|segfault|general protection|abort' | tail -400"
safe_run "$ROOT/commands/coredump-list.txt" coredumpctl --no-pager list
safe_run "$ROOT/commands/scbl-server-status.txt" /usr/local/bin/scbl-server-status

ET_CLI="$SCBL_ROOT/bin/easytier-cli"
if [[ -x "$ET_CLI" ]]; then
  RPC_PORT="$(sed -nE 's/^EASYTIER_RPC_PORT=(.*)$/\1/p' "$SCBL_ROOT/scbl.env" 2>/dev/null | tail -1)"
  RPC_PORT="${RPC_PORT:-15966}"
  INSTANCE="$(sed -nE 's/^EASYTIER_INSTANCE_NAME=(.*)$/\1/p' "$SCBL_ROOT/scbl.env" 2>/dev/null | tail -1)"
  INSTANCE="${INSTANCE:-scbl-public-server}"
  safe_run "$ROOT/commands/easytier-node.txt" "$ET_CLI" -p "127.0.0.1:$RPC_PORT" -o table -n "$INSTANCE" node
  safe_run "$ROOT/commands/easytier-peer.txt" "$ET_CLI" -p "127.0.0.1:$RPC_PORT" -o table -n "$INSTANCE" peer
  safe_run "$ROOT/commands/easytier-route.txt" "$ET_CLI" -p "127.0.0.1:$RPC_PORT" -o table -n "$INSTANCE" route
  safe_run "$ROOT/commands/easytier-node-info.json" "$ET_CLI" -p "127.0.0.1:$RPC_PORT" -v -o json -n "$INSTANCE" node info
  safe_run "$ROOT/commands/easytier-node-config.json" "$ET_CLI" -p "127.0.0.1:$RPC_PORT" -v -o json -n "$INSTANCE" node config
fi

copy_text_redacted "$SCBL_ROOT/scbl.env" "$ROOT/files/scbl.env"
copy_text_redacted "$SCBL_ROOT/easytier-server.toml" "$ROOT/files/easytier-server.toml"
copy_text_redacted "$SCBL_ROOT/server/service.toml" "$ROOT/files/server/service.toml"
copy_text_redacted "$SCBL_ROOT/client-updates/client_update_manifest.json" "$ROOT/files/client_update_manifest.json"
copy_text_redacted "$SCBL_ROOT/client-updates/components/channels/stable/client_components_v2.json" "$ROOT/files/components/stable/client_components_v2.json"
copy_text_redacted "$SCBL_ROOT/client-updates/components/channels/test/client_components_v2.json" "$ROOT/files/components/test/client_components_v2.json"
copy_text_redacted "/usr/local/lib/scbl-public/VERSION_SERVER_TOOL" "$ROOT/files/VERSION_SERVER_TOOL"
copy_text_redacted "/usr/local/lib/scbl-public/5th-echelon_branch.txt" "$ROOT/files/5th-echelon_branch.txt"

# File inventory and executable hashes are useful for detecting mixed deployments.
find "$SCBL_ROOT" -xdev -type f -printf '%p\t%s\t%TY-%Tm-%TdT%TH:%TM:%TS%Tz\n' \
  2>/dev/null | sort >"$ROOT/files/file-inventory.tsv" || true
for binary in \
  "$SCBL_ROOT/server/dedicated_server" \
  "$SCBL_ROOT/bin/easytier-core" \
  "$SCBL_ROOT/bin/easytier-cli" \
  /usr/local/lib/scbl-public/install_public_server.sh \
  /usr/local/lib/scbl-public/scbl_control_plane.py \
  /usr/local/lib/scbl-public/scbl_component_manager.py; do
  [[ -f "$binary" ]] || continue
  sha256sum "$binary" >>"$ROOT/files/binary-sha256.txt" 2>/dev/null || true
done

DB="$SCBL_ROOT/server/5th-echelon.db"
python3 - "$DB" >"$ROOT/db/metadata.json" 2>"$ROOT/db/metadata-error.txt" <<'PY' || true
from __future__ import annotations
import json, sqlite3, sys
from pathlib import Path
p = Path(sys.argv[1])
out = {"path": str(p), "exists": p.exists(), "sizeBytes": p.stat().st_size if p.exists() else 0}
if p.exists():
    con = sqlite3.connect(f"file:{p}?mode=ro", uri=True, timeout=1.0)
    con.execute("PRAGMA query_only=ON")
    out["quickCheck"] = [row[0] for row in con.execute("PRAGMA quick_check").fetchall()]
    for table in ("users", "user_sessions", "game_sessions", "participants", "station_urls", "invites"):
        try:
            out.setdefault("counts", {})[table] = con.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
        except sqlite3.Error as exc:
            out.setdefault("countErrors", {})[table] = str(exc)
    try:
        out["activeSessions"] = [
            {"sessionId": row[0], "typeId": row[1], "creatorId": row[2], "participantCount": row[3]}
            for row in con.execute(
                """SELECT g.id,g.type_id,g.creator_id,COUNT(p.user_id)
                   FROM game_sessions g LEFT JOIN participants p ON p.game_id=g.id
                   WHERE g.destroyed_at IS NULL GROUP BY g.id,g.type_id,g.creator_id ORDER BY g.id"""
            ).fetchall()
        ]
    except sqlite3.Error as exc:
        out["activeSessionError"] = str(exc)
    con.close()
print(json.dumps(out, ensure_ascii=False, indent=2))
PY

# Collect existing text logs, but never copy the SQLite database.
if [[ -d "$SCBL_ROOT/logs" ]]; then
  while IFS= read -r -d '' source; do
    relative="${source#$SCBL_ROOT/}"
    destination="$ROOT/files/$relative"
    copy_text_redacted "$source" "$destination"
  done < <(find "$SCBL_ROOT/logs" -type f -size -20M -print0 2>/dev/null)
fi

# Redact every generated text file once more so command output cannot bypass a
# source-specific copy path.
while IFS= read -r -d '' file; do
  redact_file "$file"
done < <(find "$ROOT" -type f -print0)

mkdir -p "$OUTPUT_DIR"
tar -C "$WORK" -czf "$ARCHIVE" "$NAME"
sha256sum "$ARCHIVE" >"$ARCHIVE.sha256"
chmod 0600 "$ARCHIVE" "$ARCHIVE.sha256"

echo
echo "服务端诊断包已生成："
echo "  $ARCHIVE"
echo "  $ARCHIVE.sha256"
echo "请只私下发送诊断包，不要上传到公开群或公开仓库。"
