from __future__ import annotations

import re
from dataclasses import asdict, dataclass
from typing import Any, Mapping

from .config import ServerConfig


UNIFIED_COMPONENTS = frozenset(
    {
        "client.launcher",
        "client.hooks",
        "client.route_guard",
        "client.easytier",
        "server.manager",
        "server.runtime",
    }
)


class UpdateError(ValueError):
    pass


def release_index_url(config: ServerConfig) -> str:
    repository = config.updates.repository
    channel = config.updates.channel
    return (
        f"https://github.com/{repository}/releases/download/"
        f"scbl-{channel}-latest/scbl-release-index.json"
    )


@dataclass(frozen=True, slots=True)
class ComponentRelease:
    component: str
    version: str
    url: str
    sha256: str
    size: int

    @classmethod
    def from_mapping(cls, raw: Mapping[str, Any]) -> "ComponentRelease":
        expected = {"component", "version", "url", "sha256", "size"}
        extra = sorted(set(raw) - expected)
        if extra:
            raise UpdateError("组件清单包含未知字段：" + ", ".join(extra))
        component = raw.get("component")
        version = raw.get("version")
        url = raw.get("url")
        digest = raw.get("sha256")
        size = raw.get("size")
        if component not in UNIFIED_COMPONENTS:
            raise UpdateError(f"未知组件：{component}")
        if not isinstance(version, str) or not _valid_version(version):
            raise UpdateError(f"{component} 的版本号无效")
        if not isinstance(url, str) or not url.startswith("https://"):
            raise UpdateError(f"{component} 的下载地址必须使用 HTTPS")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise UpdateError(f"{component} 的 SHA256 无效")
        if type(size) is not int or not 0 < size <= 1024 * 1024 * 1024:
            raise UpdateError(f"{component} 的文件大小无效")
        return cls(component, version, url, digest.lower(), size)


@dataclass(frozen=True, slots=True)
class UnifiedReleaseIndex:
    repository: str
    channel: str
    sequence: int
    key_id: str
    signature: str
    components: tuple[ComponentRelease, ...]

    @classmethod
    def from_mapping(cls, raw: Mapping[str, Any]) -> "UnifiedReleaseIndex":
        expected = {
            "schemaVersion",
            "repository",
            "channel",
            "sequence",
            "keyId",
            "signature",
            "components",
        }
        extra = sorted(set(raw) - expected)
        if extra:
            raise UpdateError("统一更新清单包含未知字段：" + ", ".join(extra))
        if raw.get("schemaVersion") != 1:
            raise UpdateError("不支持的统一更新清单版本")
        repository = raw.get("repository")
        if not isinstance(repository, str) or not re.fullmatch(
            r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository
        ):
            raise UpdateError("统一更新仓库格式无效")
        channel = raw.get("channel")
        if channel not in {"stable", "test"}:
            raise UpdateError("统一更新通道无效")
        sequence = raw.get("sequence")
        if type(sequence) is not int or sequence < 1:
            raise UpdateError("统一更新序号无效")
        key_id = raw.get("keyId")
        signature = raw.get("signature")
        if not isinstance(key_id, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", key_id):
            raise UpdateError("更新签名 keyId 无效")
        if not isinstance(signature, str) or not re.fullmatch(r"[A-Za-z0-9+/=]{40,512}", signature):
            raise UpdateError("更新清单签名格式无效")
        component_values = raw.get("components")
        if not isinstance(component_values, list) or not component_values:
            raise UpdateError("统一更新清单没有组件")
        components = tuple(ComponentRelease.from_mapping(item) for item in component_values)
        names = [component.component for component in components]
        if len(names) != len(set(names)):
            raise UpdateError("统一更新清单包含重复组件")
        return cls(repository, channel, sequence, key_id, signature, components)

    def assert_source(self, config: ServerConfig) -> None:
        if self.repository != config.updates.repository:
            raise UpdateError(
                f"清单仓库 {self.repository} 与配置仓库 {config.updates.repository} 不一致"
            )
        if self.channel != config.updates.channel:
            raise UpdateError(
                f"清单通道 {self.channel} 与配置通道 {config.updates.channel} 不一致"
            )


@dataclass(frozen=True, slots=True)
class UpdateAction:
    component: str
    installed_version: str
    available_version: str
    scope: str

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


def build_update_plan(
    index: UnifiedReleaseIndex, installed_versions: Mapping[str, str]
) -> list[UpdateAction]:
    actions: list[UpdateAction] = []
    for release in index.components:
        installed = installed_versions.get(release.component, "0.0.0")
        if not _valid_version(installed):
            raise UpdateError(f"已安装组件 {release.component} 的版本记录无效：{installed}")
        if _version_tuple(release.version) <= _version_tuple(installed):
            continue
        actions.append(
            UpdateAction(
                component=release.component,
                installed_version=installed,
                available_version=release.version,
                scope=release.component.split(".", 1)[0],
            )
        )
    return sorted(actions, key=lambda item: item.component)


def _valid_version(value: str) -> bool:
    return bool(re.fullmatch(r"\d+\.\d+\.\d+", value))


def _version_tuple(value: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in value.split("."))  # type: ignore[return-value]
