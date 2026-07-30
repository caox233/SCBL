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
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath
from typing import Any

SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")
CHECKSUM_LINE_RE = re.compile(r"^([0-9a-fA-F]{64})\s+\*?(?:\./)?(.+)$")
MAX_BUNDLE_BYTES = 256 * 1024 * 1024
MAX_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
MAX_ENTRY_BYTES = 160 * 1024 * 1024
DEFAULT_SERVICE = "scbl-dedicated.service"
DEFAULT_TEST_REPOSITORY = "caox233/5th-echelon"
GITHUB_API_ROOT = "https://api.github.com"
MAX_GITHUB_RESPONSE_BYTES = 4 * 1024 * 1024
MAX_CHECKSUM_BYTES = 64 * 1024
TEST_BUNDLE_RE = re.compile(r"^SCBL-(?:Invite-Party-)?Test-[A-Za-z0-9][A-Za-z0-9._-]*\.zip$")
REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
TRUSTED_GITHUB_HOSTS = {
    "api.github.com",
    "github.com",
    "objects.githubusercontent.com",
    "release-assets.githubusercontent.com",
}


def validate_repository(value: str) -> str:
    value = value.strip()
    if not REPOSITORY_RE.fullmatch(value):
        raise ValueError(f"GitHub 仓库格式无效：{value!r}")
    return value


def validate_github_url(value: str) -> str:
    parsed = urllib.parse.urlparse(value)
    if parsed.scheme != "https" or parsed.hostname not in TRUSTED_GITHUB_HOSTS:
        raise ValueError(f"拒绝非受信任的 GitHub 下载地址：{value}")
    if parsed.username or parsed.password:
        raise ValueError("GitHub 下载地址不得包含用户名或密码。")
    return value


def parse_release_checksum(text: str, expected_name: str) -> str:
    line = text.strip()
    match = re.fullmatch(rf"([0-9a-fA-F]{{64}})\s+\*?{re.escape(expected_name)}", line)
    if not match:
        raise ValueError(f"Release SHA256 文件格式无效，必须校验 {expected_name}。")
    return match.group(1).lower()


def release_candidates_from_payload(release: dict[str, Any]) -> list[dict[str, Any]]:
    if bool(release.get("draft")):
        return []
    tag = str(release.get("tag_name", "")).strip()
    if not tag:
        return []
    raw_assets = release.get("assets", [])
    if not isinstance(raw_assets, list):
        return []

    assets: dict[str, dict[str, Any]] = {}
    duplicates: set[str] = set()
    for raw in raw_assets:
        if not isinstance(raw, dict):
            continue
        name = str(raw.get("name", "")).strip()
        if not name:
            continue
        if name in assets:
            duplicates.add(name)
        assets[name] = raw

    candidates: list[dict[str, Any]] = []
    for bundle_name, bundle_asset in sorted(assets.items()):
        if bundle_name in duplicates or not TEST_BUNDLE_RE.fullmatch(bundle_name):
            continue
        checksum_name = bundle_name + ".sha256"
        checksum_asset = assets.get(checksum_name)
        if checksum_asset is None or checksum_name in duplicates:
            continue

        try:
            bundle_url = validate_github_url(str(bundle_asset.get("browser_download_url", "")).strip())
            checksum_url = validate_github_url(str(checksum_asset.get("browser_download_url", "")).strip())
            bundle_size = int(bundle_asset.get("size", 0))
            checksum_size = int(checksum_asset.get("size", 0))
        except (TypeError, ValueError):
            continue
        if bundle_size <= 0 or bundle_size > MAX_BUNDLE_BYTES:
            continue
        if checksum_size <= 0 or checksum_size > MAX_CHECKSUM_BYTES:
            continue
        candidates.append(
            {
                "tag": tag,
                "releaseName": str(release.get("name") or tag),
                "prerelease": bool(release.get("prerelease")),
                "publishedAt": str(release.get("published_at") or release.get("created_at") or ""),
                "htmlUrl": str(release.get("html_url") or ""),
                "bundleName": bundle_name,
                "bundleUrl": bundle_url,
                "bundleSize": bundle_size,
                "checksumName": checksum_name,
                "checksumUrl": checksum_url,
                "checksumSize": checksum_size,
            }
        )
    return candidates


def print_release_candidates(candidates: list[dict[str, Any]]) -> None:
    if not candidates:
        print("GitHub Release 中没有可用的 SCBL 测试候选。")
        return
    for index, candidate in enumerate(candidates, start=1):
        marker = "预发布" if candidate["prerelease"] else "正式 Release"
        size_mb = candidate["bundleSize"] / (1024 * 1024)
        print(f"[{index}] {candidate['tag']}  ({marker})")
        print(f"    {candidate['releaseName']}")
        print(f"    {candidate['bundleName']}  {size_mb:.2f} MiB")
        if candidate["publishedAt"]:
            print(f"    发布时间：{candidate['publishedAt']}")


def choose_release_candidate(candidates: list[dict[str, Any]]) -> dict[str, Any]:
    if not candidates:
        raise FileNotFoundError("GitHub Release 中没有可下载的测试候选。")
    print_release_candidates(candidates)
    answer = input(f"请选择要下载并部署的候选 [1-{len(candidates)}，0取消]：").strip()
    if answer == "0":
        raise RuntimeError("已取消。")
    if not answer.isdigit() or not 1 <= int(answer) <= len(candidates):
        raise ValueError("选择无效。")
    return candidates[int(answer) - 1]




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
    def __init__(self, scbl_root: Path, service: str = DEFAULT_SERVICE, repository: str | None = None) -> None:
        self.root = scbl_root.resolve()
        self.service = service
        self.repository = validate_repository(
            repository or os.environ.get("SCBL_TEST_REPOSITORY", DEFAULT_TEST_REPOSITORY)
        )
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
            (
                item
                for item in self.incoming.glob("SCBL-*.zip")
                if item.is_file() and TEST_BUNDLE_RE.fullmatch(item.name)
            ),
            key=lambda item: item.stat().st_mtime,
            reverse=True,
        )
        if not candidates:
            raise FileNotFoundError(
                f"本地下载缓存中没有测试包：{self.incoming}。请先从 GitHub 下载测试候选。"
            )
        return candidates[0]

    def _github_headers(self) -> dict[str, str]:
        headers = {
            "Accept": "application/vnd.github+json",
            "User-Agent": "SCBL-Test-Manager/1",
            "X-GitHub-Api-Version": "2022-11-28",
        }
        token = os.environ.get("SCBL_GITHUB_TOKEN") or os.environ.get("GITHUB_TOKEN")
        if token:
            headers["Authorization"] = f"Bearer {token.strip()}"
        return headers

    def _read_github_url(self, url: str, *, max_bytes: int) -> bytes:
        validate_github_url(url)
        request = urllib.request.Request(url, headers=self._github_headers())
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                final_url = response.geturl()
                validate_github_url(final_url)
                length = response.headers.get("Content-Length")
                if length and int(length) > max_bytes:
                    raise ValueError(f"GitHub 响应超过大小限制：{length} bytes")
                data = response.read(max_bytes + 1)
        except urllib.error.HTTPError as exc:
            detail = exc.read(4096).decode("utf-8", errors="replace").strip()
            raise RuntimeError(f"GitHub 请求失败：HTTP {exc.code} {detail}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"无法连接 GitHub：{exc.reason}") from exc
        if len(data) > max_bytes:
            raise ValueError(f"GitHub 响应超过大小限制：>{max_bytes} bytes")
        return data

    def _github_json(self, url: str) -> Any:
        raw = self._read_github_url(url, max_bytes=MAX_GITHUB_RESPONSE_BYTES)
        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError("GitHub API 返回的 JSON 无效。") from exc

    def release_candidates(self, *, limit: int = 30) -> list[dict[str, Any]]:
        if limit < 1 or limit > 100:
            raise ValueError("Release 查询数量必须在 1 到 100 之间。")
        repository = urllib.parse.quote(self.repository, safe="/")
        payload = self._github_json(
            f"{GITHUB_API_ROOT}/repos/{repository}/releases?per_page={limit}"
        )
        if not isinstance(payload, list):
            raise ValueError("GitHub Releases API 返回格式无效。")
        candidates: list[dict[str, Any]] = []
        for release in payload:
            if isinstance(release, dict):
                candidates.extend(release_candidates_from_payload(release))
        return candidates

    def release_candidate_by_tag(self, tag: str) -> dict[str, Any]:
        tag = tag.strip()
        if not tag or len(tag) > 120:
            raise ValueError("Release 标签无效。")
        repository = urllib.parse.quote(self.repository, safe="/")
        encoded_tag = urllib.parse.quote(tag, safe="")
        payload = self._github_json(
            f"{GITHUB_API_ROOT}/repos/{repository}/releases/tags/{encoded_tag}"
        )
        if not isinstance(payload, dict):
            raise ValueError("GitHub Release API 返回格式无效。")
        candidates = release_candidates_from_payload(payload)
        if not candidates:
            raise FileNotFoundError(
                f"Release {tag} 不包含测试 ZIP 及同名 .sha256 文件。"
            )
        if len(candidates) != 1:
            names = ", ".join(candidate["bundleName"] for candidate in candidates)
            raise RuntimeError(f"Release {tag} 包含多个测试候选，请从列表选择：{names}")
        return candidates[0]

    def _download_asset(self, asset: dict[str, Any], key: str, destination: Path, max_bytes: int) -> None:
        url = validate_github_url(str(asset[key]))
        request = urllib.request.Request(url, headers=self._github_headers())
        copied = 0
        try:
            with urllib.request.urlopen(request, timeout=90) as response, destination.open("xb") as output:
                validate_github_url(response.geturl())
                length = response.headers.get("Content-Length")
                if length and int(length) > max_bytes:
                    raise ValueError(f"GitHub 下载文件超过大小限制：{length} bytes")
                while True:
                    chunk = response.read(min(1024 * 1024, max_bytes - copied + 1))
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > max_bytes:
                        raise ValueError(f"GitHub 下载文件超过大小限制：>{max_bytes} bytes")
                    output.write(chunk)
        except urllib.error.HTTPError as exc:
            detail = exc.read(4096).decode("utf-8", errors="replace").strip()
            raise RuntimeError(f"GitHub 下载失败：HTTP {exc.code} {detail}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"无法从 GitHub 下载测试候选：{exc.reason}") from exc
        if copied == 0:
            raise ValueError("GitHub 下载文件为空。")

    def download_candidate(self, candidate: dict[str, Any]) -> dict[str, Any]:
        self.initialize()
        bundle_name = str(candidate["bundleName"])
        checksum_name = str(candidate["checksumName"])
        final_bundle = self.incoming / bundle_name
        final_checksum = self.incoming / checksum_name
        metadata_path = self.incoming / f"{bundle_name}.release.json"
        if final_bundle.is_symlink() or final_checksum.is_symlink() or metadata_path.is_symlink():
            raise ValueError("测试候选缓存路径不得是符号链接。")

        with tempfile.TemporaryDirectory(prefix="scbl-test-download-") as temporary_name:
            temporary = Path(temporary_name)
            checksum_file = temporary / checksum_name
            self._download_asset(candidate, "checksumUrl", checksum_file, MAX_CHECKSUM_BYTES)
            expected = parse_release_checksum(
                checksum_file.read_text(encoding="ascii", errors="strict"),
                bundle_name,
            )

            reused = final_bundle.is_file() and sha256_file(final_bundle) == expected
            if not reused:
                downloaded = temporary / bundle_name
                self._download_asset(candidate, "bundleUrl", downloaded, MAX_BUNDLE_BYTES)
                actual = sha256_file(downloaded)
                if actual != expected:
                    raise ValueError(
                        f"GitHub 测试包 SHA256 不一致：expected={expected}, actual={actual}"
                    )
                os.replace(downloaded, final_bundle)
            actual = sha256_file(final_bundle)
            if actual != expected:
                raise ValueError("测试包写入缓存后 SHA256 不一致。")
            os.replace(checksum_file, final_checksum)

        metadata = {
            "schemaVersion": 1,
            "repository": self.repository,
            "downloadedAt": utc_now(),
            "sha256": expected,
            "reused": reused,
            **candidate,
            "bundle": str(final_bundle),
        }
        atomic_write_json(metadata_path, metadata)
        return metadata

    def download_latest_candidate(self) -> dict[str, Any]:
        candidates = self.release_candidates()
        if not candidates:
            raise FileNotFoundError(
                f"{self.repository} 的 GitHub Releases 中没有可用测试候选。"
            )
        return self.download_candidate(candidates[0])

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

            print("准备部署 SCBL 测试候选：")
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
            raise FileNotFoundError("没有处于启用状态的测试候选。")
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
    parser = argparse.ArgumentParser(description="SCBL 测试候选管理")
    parser.add_argument("--root", type=Path, default=Path(os.environ.get("SCBL_ROOT", "/opt/scbl-public")))
    parser.add_argument("--service", default=DEFAULT_SERVICE)
    parser.add_argument(
        "--repository",
        default=os.environ.get("SCBL_TEST_REPOSITORY", DEFAULT_TEST_REPOSITORY),
        help=f"测试 Release 仓库，默认 {DEFAULT_TEST_REPOSITORY}",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    releases = sub.add_parser("releases", help="查看 GitHub 可用测试候选")
    releases.add_argument("--limit", type=int, default=30)

    install = sub.add_parser("install", help="从 GitHub 下载并部署测试候选")
    source = install.add_mutually_exclusive_group(required=True)
    source.add_argument("--select", action="store_true", help="交互选择 GitHub 测试候选")
    source.add_argument("--latest", action="store_true", help="使用最新 GitHub 测试候选")
    source.add_argument("--tag", help="使用指定 GitHub Release 标签")
    install.add_argument("--yes", action="store_true", help="跳过 DEPLOY-TEST 二次输入")
    install.add_argument("--dry-run", action="store_true", help="下载并校验，但不修改服务器")

    deploy = sub.add_parser("deploy", help="校验并部署本地缓存测试包")
    deploy.add_argument("--bundle", type=Path, help="测试包路径；省略时使用下载缓存中最新 ZIP")
    deploy.add_argument("--yes", action="store_true", help="跳过 DEPLOY-TEST 二次输入")
    deploy.add_argument("--dry-run", action="store_true", help="只校验测试包，不修改服务器")

    sub.add_parser("status", help="显示当前测试候选和 test Hooks 状态")
    rollback = sub.add_parser("rollback", help="一键恢复测试前二进制、数据库和 test 清单")
    rollback.add_argument("--yes", action="store_true", help="跳过 ROLLBACK 二次输入")
    diagnostics = sub.add_parser("diagnostics", help="收集测试服务端诊断包")
    diagnostics.add_argument("--since", default="1 hour ago")
    sub.add_parser("incoming", help="显示测试候选本地下载缓存")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    manager = InviteTestManager(args.root, args.service, args.repository)
    try:
        if args.command == "releases":
            candidates = manager.release_candidates(limit=args.limit)
            print(f"测试 Release 仓库：{manager.repository}")
            print_release_candidates(candidates)
        elif args.command == "install":
            if args.select:
                candidate = choose_release_candidate(manager.release_candidates())
                downloaded = manager.download_candidate(candidate)
            elif args.tag:
                downloaded = manager.download_candidate(manager.release_candidate_by_tag(args.tag))
            else:
                downloaded = manager.download_latest_candidate()
            print("测试候选已从 GitHub 下载并通过外层 SHA256 校验：")
            print(json.dumps(downloaded, ensure_ascii=False, indent=2))
            result = manager.deploy(
                Path(str(downloaded["bundle"])),
                assume_yes=args.yes,
                dry_run=args.dry_run,
            )
            if args.dry_run:
                print("测试候选完整校验通过，未修改服务器。")
            else:
                print("SCBL 测试候选已部署。")
            print(json.dumps(result, ensure_ascii=False, indent=2))
            if not args.dry_run:
                print("两台 Windows 测试机请使用“SCBL 测试通道”快捷方式。")
                print("尚未证明游戏内成功，必须完成双人实测。")
        elif args.command == "deploy":
            result = manager.deploy(args.bundle, assume_yes=args.yes, dry_run=args.dry_run)
            if args.dry_run:
                print("测试包校验通过：")
            else:
                print("SCBL 测试候选已部署。")
            print(json.dumps(result, ensure_ascii=False, indent=2))
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
            for path in sorted(manager.incoming.glob("SCBL-*.zip")):
                if not TEST_BUNDLE_RE.fullmatch(path.name):
                    continue
                print(f"  {path.name}  {path.stat().st_size} bytes")
        else:
            raise AssertionError(args.command)
        return 0
    except Exception as exc:  # noqa: BLE001 - command line should return one clear error
        print(f"错误：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
