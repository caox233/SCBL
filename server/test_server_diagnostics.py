#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[1]
collector = (root / "server/scbl_server_diagnostics.sh").read_text(encoding="utf-8")
manager = (root / "server/install_public_server.sh").read_text(encoding="utf-8")
workflow = (root / ".github/workflows/server-tool-release.yml").read_text(encoding="utf-8")

assert "SCBL_Server_Diagnostics_" in collector
assert "journalctl -u" in collector
assert "PRAGMA quick_check" in collector
assert "ticket_key" in collector and "REDACTED" in collector
assert "5th-echelon.db" in collector
assert 'cp -a "$DB"' not in collector
assert "scbl-server-diagnostics" in manager
assert "15. 一键收集服务端诊断日志" in manager
assert "scbl_server_diagnostics.sh" in workflow
print("server diagnostic collector packaging and privacy checks passed")
