#!/usr/bin/env python3
"""One-click deployment, rollback and diagnostics for SCBL invitation test candidates.

The manager intentionally touches only:
- the SCBL test component manifest and immutable Hooks store;
- the dedicated server binary and its SQLite database backup/restore;
- the dedicated server systemd unit.

It never restarts or rewrites EasyTier, Route Guard, the control plane, firewall rules,
or the production stable component manifest.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any

SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")
CHECKSUM_LINE_RE = re.compile(r"^([0-9a-fA-F]{64})\s+\*?(?:\./)?(.+)$")
MAX_BUNDLE_BYTES = 256 * 1024 * 1024
MAX_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
MAX_ENTRY_BYTES = 160 * 1024 * 1024
DEFAULT_SERVICE = "scbl-dedicated.service"


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def atomic_write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def run(command: list[str], *, env: dict[str, str] | None = None, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        check=True,
        text=True,
        env=env,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
    )


def is_zip_symlink(info: zipfile.ZipInfo) -> bool:
    mode = (info.external_attr >> 16) & 0xFFFF
    return stat.S_ISLNK(mode)


def safe_extract_zip(bundle: Path, destination: Path) -> None:
    if not bundle.is_file():
        raise FileNotFoundError(f"测试包不存在：{bundle}")
    if bundle.stat().st_size > MAX_BUNDLE_BYTES:
        raise ValueError(f"测试包过大：{bundle.stat().st_size} bytes")

    with zipfile.ZipFile(bundle) as archive:
        total = 0
        seen: set[str] = set()
        for info in archive.infolist():
            normalized = PurePosixPath(info.filename.replace("\\", "/"))
            if normalized.is_absolute() or ".." in normalized.parts:
                raise ValueError(f"测试包包含不安全路径：{info.filename}")
            if is_zip_symlink(info):
                raise ValueError(f"测试包包含符号链接：{info.filename}")
            name = normalized.as_posix().rstrip("/")
            if not name:
                continue
            if name in seen:
                raise ValueError(f"测试包包含重复路径：{name}")
            seen.add(name)
            if info.file_size > MAX_ENTRY_BYTES:
                raise ValueError(f"测试包文件过大：{name}={info.file_size} bytes")
            total += info.file_size
            if total > MAX_UNCOMPRESSED_BYTES:
                raise ValueError("测试包解压后总大小超过限制。")

            target = (destination / Path(*normalized.parts)).resolve()
            root = destination.resolve()
            if target != root and root not in target.parents:
                raise ValueError(f"测试包路径越界：{info.filename}")
            if info.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(info) as source, target.open("xb") as output:
                copied = 0
                while True:
                    chunk = source.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > MAX_ENTRY_BYTES:
                        raise ValueError(f"测试包文件解压超过限制：{name}")
                    output.write(chunk)


def resolve_bundle_root(extracted: Path) -> Path:
    if (extracted / "CHECKSUMS.sha256").is_file():
        return extracted
    candidates = [item for item in extracted.iterdir() if item.is_dir()]
    if len(candidates) == 1 and (candidates[0] / "CHECKSUMS.sha256").is_file():
        return candidates[0]
    raise ValueError("测试包目录结构无效：未找到唯一的 CHECKSUMS.sha256。")


def verify_checksum_manifest(root: Path) -> None:
    manifest = root / "CHECKSUMS.sha256"
    if not manifest.is_file():
        raise FileNotFoundError("测试包缺少 CHECKSUMS.sha256。")
    checked = 0
    root_resolved = root.resolve()
    for raw_line in manifest.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        match = CHECKSUM_LINE_RE.fullmatch(line)
        if not match:
            raise ValueError(f"CHECKSUMS.sha256 格式无效：{raw_line}")
        expected = match.group(1).lower()
        relative = PurePosixPath(match.group(2).replace("\\", "/"))
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"CHECKSUMS.sha256 包含不安全路径：{relative}")
        path = (root / Path(*relative.parts)).resolve()
        if path != root_resolved and root_resolved not in path.parents:
            raise ValueError(f"校验路径越界：{relative}")
        if not path.is_file():
            raise FileNotFoundError(f"校验文件不存在：{relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise ValueError(f"测试文件 SHA256 不一致：{relative}, expected={expected}, actual={actual}")
        checked += 1
    if checked < 4:
        raise ValueError("CHECKSUMS.sha256 条目过少，拒绝部署。")


def read_single_hash(path: Path, expected_name: str) -> str:
    text = path.read_text(encoding="ascii", errors="strict").strip()
    match = re.fullmatch(rf"([0-9a-fA-F]{{64}})\s+\*?{re.escape(expected_name)}", text)
    if not match:
        raise ValueError(f"校验文件格式无效：{path.name}")
    return match.group(1).lower()


def read_commit(path: Path) -> str:
    value = path.read_text(encoding="ascii", errors="strict").strip().lower()
    if not COMMIT_RE.fullmatch(value):
        raise ValueError(f"提交 SHA 无效：{path}")
    return value


def read_component(path: Path, expected_component: str, expected_file: str) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if value.get("component") != expected_component:
        raise ValueError(f"组件类型不匹配：{path}")
    if value.get("file") != expected_file:
        raise ValueError(f"组件文件名不匹配：{path}")
    digest = str(value.get("sha256", "")).strip().lower()
    if not SHA256_RE.fullmatch(digest):
        raise ValueError(f"组件 SHA256 无效：{path}")
    size = value.get("size")
    if not isinstance(size, int) or size <= 0:
        raise ValueError(f"组件大小无效：{path}")
    version = str(value.get("version", "")).strip()
    if not version or len(version) > 80 or "/" in version or "\\" in version or ".." in version:
        raise ValueError(f"组件版本无效：{path}")
    commit = str(value.get("commit", "")).strip().lower()
    if not COMMIT_RE.fullmatch(commit):
        raise ValueError(f"组件提交 SHA 无效：{path}")
    return value


def validate_candidate(root: Path) -> dict[str, Any]:
    verify_checksum_manifest(root)

    hooks_bundle = root / "Artifacts/scbl-hooks-party-follow-test.zip"
    hooks_dir = root / "Artifacts/hooks-extracted"
    dedicated_dir = root / "Artifacts/dedicated-extracted"
    dedicated_file = dedicated_dir / "dedicated_server-linux-x86_64"
    required = [
        hooks_bundle,
        hooks_dir / "uplay_r1_loader.dll",
        hooks_dir / "uplay_r1_loader.dll.sha256",
        hooks_dir / "commit_sha.txt",
        hooks_dir / "component.json",
        dedicated_file,
        dedicated_dir / "dedicated_server-linux-x86_64.sha256",
        dedicated_dir / "commit_sha.txt",
        dedicated_dir / "component.json",
    ]
    for path in required:
        if not path.is_file():
            raise FileNotFoundError(f"测试包缺少文件：{path.relative_to(root)}")

    hooks = read_component(hooks_dir / "component.json", "hooks", "uplay_r1_loader.dll")
    dedicated = read_component(dedicated_dir / "component.json", "dedicated_server", "dedicated_server-linux-x86_64")
    hooks_commit = read_commit(hooks_dir / "commit_sha.txt")
    dedicated_commit = read_commit(dedicated_dir / "commit_sha.txt")
    if hooks_commit != dedicated_commit or hooks_commit != hooks["commit"] or dedicated_commit != dedicated["commit"]:
        raise ValueError("Hooks 与 dedicated server 的来源提交不一致。")

    hooks_file = hooks_dir / "uplay_r1_loader.dll"
    hooks_hash = sha256_file(hooks_file)
    if hooks_hash != hooks["sha256"] or hooks_hash != read_single_hash(hooks_dir / "uplay_r1_loader.dll.sha256", hooks_file.name):
        raise ValueError("Hooks DLL 元数据或校验文件不一致。")
    if hooks_file.stat().st_size != hooks["size"]:
        raise ValueError("Hooks DLL 大小与 component.json 不一致。")

    dedicated_hash = sha256_file(dedicated_file)
    if dedicated_hash != dedicated["sha256"] or dedicated_hash != read_single_hash(
        dedicated_dir / "dedicated_server-linux-x86_64.sha256", dedicated_file.name
    ):
        raise ValueError("dedicated server 元数据或校验文件不一致。")
    if dedicated_file.stat().st_size != dedicated["size"]:
        raise ValueError("dedicated server 大小与 component.json 不一致。")

    launcher_hash = ""
    launcher = root / "Windows/SplinterCellCNLauncher.exe"
    if launcher.is_file():
        launcher_hash = sha256_file(launcher)

    return {
        "sourceCommit": hooks_commit,
        "hooksVersion": hooks["version"],
        "hooksSha256": hooks_hash,
        "hooksBundle": str(hooks_bundle),
        "dedicatedVersion": dedicated["version"],
        "dedicatedSha256": dedicated_hash,
        "dedicatedFile": str(dedicated_file),
        "launcherSha256": launcher_hash,
    }


class InviteTestManager:
    def __init__(self, scbl_root: Path, service: str = DEFAULT_SERVICE) -> None:
        self.root = scbl_root.resolve()
        self.service = service
        self.incoming = self.root / "incoming/invite-test"
        self.update_root = self.root / "client-updates"
        self.server_dir = self.root / "server"
        self.dedicated_target = self.server_dir / "dedicated_server"
        self.database = self.server_dir / "5th-echelon.db"
        self.state_dir = self.root / "test-candidates/invite-party"
        self.state_path = self.state_dir / "active.json"
        self.backup_root = self.root / "backups/invite-party-test"
        manager_dir = Path(os.environ.get("SCBL_MANAGER_DIR", "/usr/local/lib/scbl-public"))
        self.publisher = manager_dir / "scbl_publish_hooks_bundle.py"
        self.component_manager = manager_dir / "scbl_component_manager.py"
        self.diagnostics = Path("/usr/local/bin/scbl-server-diagnostics")

    def initialize(self) -> None:
        self.incoming.mkdir(parents=True, exist_ok=True)
        self.state_dir.mkdir(parents=True, exist_ok=True)
        self.backup_root.mkdir(parents=True, exist_ok=True)

    def latest_bundle(self) -> Path:
        self.initialize()
        candidates = sorted(
            (item for item in self.incoming.glob("SCBL-Invite-Party-Test-*.zip") if item.is_file()),
            key=lambda item: item.stat().st_mtime,
            reverse=True,
        )
        if not candidates:
            raise FileNotFoundError(
                f"未找到测试包。请把 SCBL-Invite-Party-Test-*.zip 上传到：{self.incoming}"
            )
        return candidates[0]

    def _require_runtime(self) -> None:
        if os.geteuid() != 0:
            raise PermissionError("请使用 root 运行。")
        for path in (self.dedicated_target, self.database, self.publisher, self.component_manager):
            if not path.is_file():
                raise FileNotFoundError(f"缺少必需文件：{path}")
        run(["systemctl", "cat", self.service], capture=True)

    def _restore_manifest(self, backup: Path, existed: bool) -> None:
        manifest = self.update_root / "components/channels/test/client_components_v2.json"
        manifest.parent.mkdir(parents=True, exist_ok=True)
        if existed:
            shutil.copy2(backup / "client_components_v2.test.before.json", manifest)
        elif manifest.exists():
            manifest.unlink()

    def _rollback_from_backup(self, backup: Path, *, restore_manifest: bool = True) -> None:
        target = Path((backup / "dedicated_target.txt").read_text(encoding="utf-8").strip())
        database = Path((backup / "database_path.txt").read_text(encoding="utf-8").strip())
        service = (backup / "service.txt").read_text(encoding="utf-8").strip()
        manifest_existed = (backup / "test_manifest_existed.txt").read_text(encoding="ascii").strip() == "1"
        run(["systemctl", "stop", service])
        shutil.copy2(backup / "dedicated_server.before", target)
        shutil.copy2(backup / "5th-echelon.db.before", database)
        os.chmod(target, 0o755)
        if restore_manifest:
            self._restore_manifest(backup, manifest_existed)
        run(["systemctl", "start", service])
        self._wait_service(service)

    @staticmethod
    def _wait_service(service: str, timeout_seconds: int = 12) -> None:
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            result = subprocess.run(["systemctl", "is-active", "--quiet", service], check=False)
            if result.returncode == 0:
                return
            time.sleep(1)
        raise RuntimeError(f"服务未在限定时间内进入 active：{service}")

    def deploy(self, bundle: Path | None, *, assume_yes: bool, dry_run: bool) -> dict[str, Any]:
        self.initialize()
        bundle = (bundle or self.latest_bundle()).resolve()
        with tempfile.TemporaryDirectory(prefix="scbl-invite-test-") as temporary_name:
            temporary = Path(temporary_name)
            safe_extract_zip(bundle, temporary)
            extracted_root = resolve_bundle_root(temporary)
            candidate = validate_candidate(extracted_root)

            if dry_run:
                candidate["bundle"] = str(bundle)
                candidate["dryRun"] = True
                return candidate

            self._require_runtime()
            if self.state_path.is_file():
                active = json.loads(self.state_path.read_text(encoding="utf-8"))
                if active.get("status") == "active":
                    raise RuntimeError("已有测试候选处于启用状态。请先执行回滚，再部署新的测试包。")

            print("准备一键部署邀请/组队测试候选：")
            print(f"  测试包：{bundle}")
            print(f"  来源提交：{candidate['sourceCommit']}")
            print(f"  Hooks：{candidate['hooksVersion']}  {candidate['hooksSha256']}")
            print(f"  Dedicated：{candidate['dedicatedVersion']}  {candidate['dedicatedSha256']}")
            print("  仅重启 scbl-dedicated.service；不会修改 EasyTier、Route Guard 或 stable 通道。")
            if not assume_yes:
                answer = input("确认部署？请输入 DEPLOY-TEST：").strip()
                if answer != "DEPLOY-TEST":
                    raise RuntimeError("已取消部署。")

            stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S")
            backup = self.backup_root / stamp
            backup.mkdir(parents=True, exist_ok=False)
            shutil.copy2(self.dedicated_target, backup / "dedicated_server.before")
            shutil.copy2(self.database, backup / "5th-echelon.db.before")
            (backup / "dedicated_target.txt").write_text(str(self.dedicated_target) + "\n", encoding="utf-8")
            (backup / "database_path.txt").write_text(str(self.database) + "\n", encoding="utf-8")
            (backup / "service.txt").write_text(self.service + "\n", encoding="utf-8")
            manifest = self.update_root / "components/channels/test/client_components_v2.json"
            manifest_existed = manifest.is_file()
            (backup / "test_manifest_existed.txt").write_text("1\n" if manifest_existed else "0\n", encoding="ascii")
            if manifest_existed:
                shutil.copy2(manifest, backup / "client_components_v2.test.before.json")
            shutil.copy2(bundle, backup / bundle.name)
            atomic_write_json(backup / "candidate.json", candidate)

            try:
                run(
                    [
                        sys.executable,
                        str(self.publisher),
                        "--root",
                        str(self.update_root),
                        "--zip",
                        candidate["hooksBundle"],
                    ]
                )
                run([sys.executable, str(self.component_manager), "--root", str(self.update_root), "verify"])

                run(["systemctl", "stop", self.service])
                source = Path(candidate["dedicatedFile"])
                temporary_target = self.dedicated_target.with_suffix(".new")
                shutil.copy2(source, temporary_target)
                os.chmod(temporary_target, 0o755)
                if sha256_file(temporary_target) != candidate["dedicatedSha256"]:
                    raise ValueError("服务端临时文件写入后 SHA256 不一致。")
                os.replace(temporary_target, self.dedicated_target)
                run(["systemctl", "start", self.service])
                self._wait_service(self.service)
            except Exception:
                print("测试候选部署失败，正在自动恢复二进制、数据库和 test 清单。", file=sys.stderr)
                self._rollback_from_backup(backup)
                raise

            state = {
                "schemaVersion": 1,
                "status": "active",
                "deployedAt": utc_now(),
                "bundle": str(bundle),
                "backup": str(backup),
                **candidate,
            }
            atomic_write_json(self.state_path, state)
            return state

    def rollback(self, *, assume_yes: bool) -> dict[str, Any]:
        self._require_runtime()
        if not self.state_path.is_file():
            raise FileNotFoundError("没有处于启用状态的邀请测试候选。")
        state = json.loads(self.state_path.read_text(encoding="utf-8"))
        if state.get("status") != "active":
            raise RuntimeError("最近的测试候选已经回滚。")
        backup = Path(str(state.get("backup", "")))
        if not backup.is_dir():
            raise FileNotFoundError(f"测试备份目录不存在：{backup}")
        print(f"准备恢复测试前 dedicated server、数据库和 test 组件清单：{backup}")
        if not assume_yes:
            answer = input("确认回滚？请输入 ROLLBACK：").strip()
            if answer != "ROLLBACK":
                raise RuntimeError("已取消回滚。")
        self._rollback_from_backup(backup)
        state["status"] = "rolled-back"
        state["rolledBackAt"] = utc_now()
        atomic_write_json(self.state_path, state)
        return state

    def status(self) -> dict[str, Any]:
        self.initialize()
        active: dict[str, Any] = {}
        if self.state_path.is_file():
            active = json.loads(self.state_path.read_text(encoding="utf-8"))
        service_state = subprocess.run(
            ["systemctl", "is-active", self.service],
            check=False,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
        ).stdout.strip()
        dedicated_hash = sha256_file(self.dedicated_target) if self.dedicated_target.is_file() else ""
        manifest_path = self.update_root / "components/channels/test/client_components_v2.json"
        hooks: dict[str, Any] = {}
        if manifest_path.is_file():
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
                value = manifest.get("components", {}).get("hooks", {})
                if isinstance(value, dict):
                    hooks = value
            except Exception as exc:  # noqa: BLE001
                hooks = {"error": str(exc)}
        return {
            "service": self.service,
            "serviceState": service_state or "unknown",
            "dedicatedSha256": dedicated_hash,
            "database": str(self.database),
            "testHooks": hooks,
            "candidate": active,
            "incomingDirectory": str(self.incoming),
        }

    def collect_diagnostics(self, since: str) -> Path:
        self.initialize()
        if not self.diagnostics.is_file():
            raise FileNotFoundError(f"服务端诊断命令不存在：{self.diagnostics}")
        output = self.incoming / "diagnostics"
        output.mkdir(parents=True, exist_ok=True)
        before = set(output.glob("SCBL_Server_Diagnostics_*.tar.gz"))
        env = dict(os.environ)
        env["SCBL_ROOT"] = str(self.root)
        env["SCBL_DIAGNOSTIC_OUTPUT_DIR"] = str(output)
        run([str(self.diagnostics), since], env=env)
        created = sorted(set(output.glob("SCBL_Server_Diagnostics_*.tar.gz")) - before, key=lambda item: item.stat().st_mtime)
        if created:
            return created[-1]
        candidates = sorted(output.glob("SCBL_Server_Diagnostics_*.tar.gz"), key=lambda item: item.stat().st_mtime)
        if not candidates:
            raise RuntimeError("诊断命令执行完成，但没有找到输出文件。")
        return candidates[-1]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="SCBL 邀请/组队测试候选一键管理")
    parser.add_argument("--root", type=Path, default=Path(os.environ.get("SCBL_ROOT", "/opt/scbl-public")))
    parser.add_argument("--service", default=DEFAULT_SERVICE)
    sub = parser.add_subparsers(dest="command", required=True)

    deploy = sub.add_parser("deploy", help="校验并一键部署最新测试包")
    deploy.add_argument("--bundle", type=Path, help="测试包路径；省略时自动选择 incoming/invite-test 中最新 ZIP")
    deploy.add_argument("--yes", action="store_true", help="跳过 DEPLOY-TEST 二次输入")
    deploy.add_argument("--dry-run", action="store_true", help="只校验测试包，不修改服务器")

    sub.add_parser("status", help="显示当前测试候选和 test Hooks 状态")
    rollback = sub.add_parser("rollback", help="一键恢复测试前二进制、数据库和 test 清单")
    rollback.add_argument("--yes", action="store_true", help="跳过 ROLLBACK 二次输入")
    diagnostics = sub.add_parser("diagnostics", help="收集邀请测试服务端诊断包")
    diagnostics.add_argument("--since", default="1 hour ago")
    sub.add_parser("incoming", help="显示测试包上传目录")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    manager = InviteTestManager(args.root, args.service)
    try:
        if args.command == "deploy":
            result = manager.deploy(args.bundle, assume_yes=args.yes, dry_run=args.dry_run)
            if args.dry_run:
                print("测试包校验通过：")
            else:
                print("邀请/组队测试候选已一键部署。")
            print(json.dumps(result, ensure_ascii=False, indent=2))
            if not args.dry_run:
                print("两台 Windows 测试机请使用“SCBL 测试通道”快捷方式。")
                print("尚未证明游戏内成功，必须完成双人实测。")
        elif args.command == "rollback":
            result = manager.rollback(assume_yes=args.yes)
            print("已恢复测试前 dedicated server、数据库和 test 组件清单。")
            print(json.dumps(result, ensure_ascii=False, indent=2))
        elif args.command == "status":
            print(json.dumps(manager.status(), ensure_ascii=False, indent=2))
        elif args.command == "diagnostics":
            path = manager.collect_diagnostics(args.since)
            print(f"诊断包已生成：{path}")
        elif args.command == "incoming":
            manager.initialize()
            print(manager.incoming)
        else:
            raise AssertionError(args.command)
        return 0
    except Exception as exc:  # noqa: BLE001 - command line should return one clear error
        print(f"错误：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
