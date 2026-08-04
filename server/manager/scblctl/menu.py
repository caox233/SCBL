from __future__ import annotations

from .config import ConfigError, dump_toml, load_config, save_config
from .diagnostics import run_doctor
from .paths import RuntimePaths
from .services import SystemdManager


def run_menu(paths: RuntimePaths) -> int:
    while True:
        print("\nSCBL 2.0 服务端管理")
        print("  1. 服务状态")
        print("  2. 服务端部署")
        print("  3. 客户端发布")
        print("  4. 查看服务端配置")
        print("  5. 动态域名（DDNS）")
        print("  6. 数据与备份")
        print("  7. 日志与诊断")
        print("  8. 高级管理")
        print("  0. 退出")
        choice = input("请选择：").strip()
        if choice == "0":
            return 0
        if choice == "1":
            _show_status()
        elif choice == "2":
            _deployment_menu(paths)
        elif choice == "3":
            _client_publish_menu(paths)
        elif choice == "4":
            _show_config(paths)
        elif choice == "5":
            _ddns_menu(paths)
        elif choice == "6":
            _backup_menu(paths)
        elif choice == "7":
            _show_doctor(paths)
        elif choice == "8":
            _advanced_menu(paths)
        else:
            print("无效选项。")


def _show_status() -> None:
    for item in SystemdManager().all_statuses():
        marker = "✓" if item.active == "active" else "!"
        print(f" {marker} {item.component}: {item.active}/{item.sub}")


def _show_config(paths: RuntimePaths) -> None:
    try:
        print(dump_toml(load_config(paths.config), redact=True), end="")
    except ConfigError as exc:
        print(f"错误：{exc}")


def _show_doctor(paths: RuntimePaths) -> None:
    symbols = {"pass": "✓", "warn": "!", "fail": "✗"}
    for check in run_doctor(paths):
        print(f" {symbols[check.level]} {check.name}: {check.message}")


def _advanced_menu(paths: RuntimePaths) -> None:
    while True:
        try:
            config = load_config(paths.config)
        except ConfigError as exc:
            print(f"错误：{exc}")
            return
        state = "开启" if config.testing.allow_newer_clients else "关闭"
        print("\n高级管理")
        print(f"  1. 允许本地测试版客户端：{state}")
        print("  2. 显示命令行帮助提示")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice == "1":
            target = not config.testing.allow_newer_clients
            if target:
                print("仅应在本地测试服务器开启；正式服务器请保持关闭。")
                if input("确认开启 [y/N]：").strip().lower() not in {"y", "yes"}:
                    continue
            config.testing.allow_newer_clients = target
            save_config(config, paths.config)
            from .provision import ProvisionError, Provisioner

            try:
                Provisioner().apply_testing_setting(config)
                print("设置已生效；仅重启了控制面，正在进行的游戏不会中断。")
            except ProvisionError as exc:
                print(f"设置已保存，但应用失败：{exc}")
        elif choice == "2":
            print("高级命令请使用：SCBL --help")
        else:
            print("无效选项。")


def _deployment_menu(paths: RuntimePaths) -> None:
    from pathlib import Path

    from .client_publish import ClientPublishError, receive_with_rz
    from .deployment import (
        DeploymentError,
        DeploymentManager,
        download_online_package,
        installed_versions,
        online_package_url,
    )

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    while True:
        print("\n服务端部署")
        print("  1. 首次安装")
        print("  2. 修复")
        print("  3. 更新")
        print("  4. 查看已安装组件")
        print("  5. 查看发布历史")
        print("  0. 返回")
        print(f"在线源：{config.updates.repository} ({config.updates.channel})")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice in {"1", "2", "3"}:
                import tempfile

                operation = {"1": "install", "2": "repair", "3": "update"}[choice]
                kind = "patch" if operation == "update" else "full"
                package = None
                temporary = tempfile.TemporaryDirectory(
                    prefix=f"{operation}-", dir="/var/cache/scbl"
                )
                try:
                    root = Path(temporary.name)
                    print("  1. 在线下载")
                    print("  2. Xshell rz 手动上传")
                    print("  3. 使用服务端已有文件路径")
                    source = input("请选择来源：").strip()
                    if source == "1":
                        suffix = ".scblpatch" if kind == "patch" else ".scblfull"
                        print("下载地址：" + online_package_url(config, kind=kind))
                        package = download_online_package(
                            config, kind=kind, destination=root / f"online{suffix}"
                        )
                    elif source == "2":
                        print("请在 Xshell 中选择部署包。")
                        package = receive_with_rz(root)
                    elif source == "3":
                        package = Path(input("部署包路径：").strip())
                    else:
                        print("无效来源。")
                        continue
                    manager = DeploymentManager(config, paths)
                    verified = manager.verify(package, expected_kind=kind)
                    print(
                        f"包校验通过：{verified.package_type} v{verified.version}，"
                        f"组件={', '.join(item.component + '@' + item.version for item in verified.artifacts)}"
                    )
                    allow_online = False
                    changes_runtime = any(
                        item.component == "server.runtime" for item in verified.artifacts
                    )
                    if operation != "install" and changes_runtime:
                        from .live_state import LiveStateError, read_live_state

                        try:
                            live = read_live_state(config)
                            if live.online_count:
                                names = "、".join(live.usernames) or "未知玩家"
                                print(
                                    f"警告：当前有 {live.online_count} 名玩家在线（{names}）。"
                                    "本次操作会中断游戏。"
                                )
                                allow_online = input(
                                    "如确定立即执行，请输入 FORCE："
                                ).strip() == "FORCE"
                                if not allow_online:
                                    print("已取消；请等待玩家离线后再操作。")
                                    continue
                        except LiveStateError as exc:
                            print(f"警告：{exc}")
                            allow_online = input(
                                "无法确认在线状态；如确定无人在线，请输入 FORCE："
                            ).strip() == "FORCE"
                            if not allow_online:
                                print("已取消。")
                                continue
                    answer = input(f"确认执行{ {'install':'首次安装','repair':'修复','update':'更新'}[operation] } [y/N]：").strip().lower()
                    if answer not in {"y", "yes"}:
                        print("已取消。")
                        continue
                    result = manager.apply(
                        package, operation=operation, allow_online=allow_online
                    )
                    print("已应用组件：" + ", ".join(result.applied))
                    if result.backup:
                        print(f"操作前备份：{result.backup}")
                finally:
                    temporary.cleanup()
            elif choice == "4":
                versions = installed_versions()
                for component in sorted(versions):
                    print(f"{component} = {versions[component]}")
                print(f"ddns = {'启用' if config.ddns.enabled else '禁用'}")
            elif choice == "5":
                _show_release_history()
            else:
                print("无效选项。")
        except (DeploymentError, ClientPublishError, OSError, ValueError) as exc:
            print(f"部署错误：{exc}")


def _ddns_menu(paths: RuntimePaths) -> None:
    import getpass

    from .ddns import DdnsError, DdnsManager

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    manager = DdnsManager(config.ddns)
    while True:
        print("\nIPv6 动态域名（脚本管理，无 Web 设置页）")
        print("  1. 安装 / 更新 DDNS-Go")
        print("  2. 配置阿里云 DNS（A/AAAA）")
        print("  3. 启用并立即更新")
        print("  4. 查看同步状态")
        print("  5. 立即重新检测")
        print("  6. 查看最近日志")
        print("  7. 停止并禁用")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                print(f"已安装 {manager.install()}（官方 SHA256 校验通过）。")
            elif choice == "2":
                default_domain = config.server.public_host if "." in config.server.public_host else ""
                prompt = f"AAAA 域名 [{default_domain}]：" if default_domain else "AAAA 域名："
                domain = input(prompt).strip() or default_domain
                detected = manager.detect_interface()
                interface = input(f"IPv6 网卡 [{detected}]：").strip() or detected
                access_key_id = input("阿里云 AccessKey ID：").strip()
                secret = getpass.getpass("阿里云 AccessKey Secret（输入不回显）：").strip()
                ipv4_answer = input("是否同时更新 IPv4 A 记录 [y/N]：").strip().lower()
                address = manager.configure_alidns(
                    domain=domain,
                    access_key_id=access_key_id,
                    access_key_secret=secret,
                    interface=interface,
                    enable_ipv4=ipv4_answer in {"y", "yes"},
                )
                print(f"配置已保存；当前公网 IPv6：{address}")
            elif choice == "3":
                manager.enable()
                config.ddns.enabled = True
                save_config(config, paths.config)
                print("DDNS 已启用并立即执行。")
            elif choice == "4":
                status = manager.status()
                print(f"服务：{status.active}/{status.enabled}")
                print(f"域名：{status.domain or '-'}")
                print(f"网卡公网 IPv6：{status.local_ipv6 or '-'}")
                print(f"DNS AAAA：{', '.join(status.dns_ipv6) or '-'}")
                print(f"记录模式：{'A + AAAA' if status.ipv4_enabled else '仅 AAAA'}")
                print(f"同步：{'一致' if status.synchronized else '尚未一致'}")
            elif choice == "5":
                manager.restart()
                print("已重新检测并执行更新。")
            elif choice == "6":
                print(manager.recent_log() or "（无日志）")
            elif choice == "7":
                manager.stop()
                config.ddns.enabled = False
                save_config(config, paths.config)
                print("已停止；配置和凭据仍保留。")
            else:
                print("无效选项。")
        except (DdnsError, OSError) as exc:
            print(f"DDNS 错误：{exc}")


def _show_release_history() -> None:
    from pathlib import Path

    runtime = Path("/opt/scbl/releases")
    print("服务端运行时：")
    for path in sorted(runtime.glob("*"), reverse=True):
        if path.is_dir():
            print(f"  {path.name}")


def _client_publish_menu(paths: RuntimePaths) -> None:
    from pathlib import Path

    from .client_publish import (
        ClientPublishError,
        ClientPublisher,
        download_online_client,
        receive_with_rz,
    )

    try:
        config = load_config(paths.config)
    except ConfigError as exc:
        print(f"错误：{exc}")
        return
    publisher = ClientPublisher()
    while True:
        current = publisher.current_version() or "未发布"
        print("\n客户端发布")
        print(f"当前正式版本：{current}（仅此版本允许联机）")
        print("  1. 发布客户端")
        print("  2. 组件更新")
        print("  3. 公告管理")
        print("  4. 查看客户端版本")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                import tempfile

                cache = Path("/var/cache/scbl")
                cache.mkdir(parents=True, exist_ok=True)
                with tempfile.TemporaryDirectory(prefix="client-", dir=cache) as temporary:
                    root = Path(temporary)
                    print("  1. 从 GitHub 下载最新正式版")
                    print("  2. Xshell rz 手动上传")
                    print("  3. 使用服务端已有文件路径")
                    source = input("请选择来源：").strip()
                    if source == "1":
                        package = download_online_client(config.updates.repository, root)
                    elif source == "2":
                        print("请在 Xshell 中选择客户端 ZIP。")
                        package = receive_with_rz(root)
                    elif source == "3":
                        package = Path(input("客户端 ZIP 路径：").strip())
                    else:
                        print("无效来源。")
                        continue
                    verified = publisher.verify(package)
                    print(
                        f"校验通过：客户端 v{verified.version}，"
                        f"{verified.size / 1024 / 1024:.1f} MiB，SHA256={verified.sha256}"
                    )
                    same = verified.version == publisher.current_version()
                    if same:
                        print("该版本已发布；继续会用完整包执行修复覆盖。")
                    if input("确认发布 [y/N]：").strip().lower() not in {"y", "yes"}:
                        print("已取消。")
                        continue
                    note = input("更新说明（直接回车使用默认值）：").strip()
                    published = publisher.publish(
                        package,
                        release_notes=[note] if note else None,
                        force=same,
                    )
                    print(f"客户端 v{published.version} 已发布：{published.archive}")
            elif choice == "2":
                _client_component_menu()
            elif choice == "3":
                _announcement_menu()
            elif choice == "4":
                _show_client_history(publisher)
            else:
                print("无效选项。")
        except (ClientPublishError, OSError, ValueError) as exc:
            print(f"客户端发布错误：{exc}")


def _show_client_history(publisher) -> None:
    print(f"当前正式版本：{publisher.current_version() or '未发布'}")
    releases = publisher.root / "releases"
    versions = [path.name for path in sorted(releases.glob("*"), reverse=True) if path.is_dir()]
    if not versions:
        print("暂无客户端发布记录。")
        return
    print("服务器保留版本：")
    for version in versions:
        print(f"  {version}")


def _client_component_menu() -> None:
    from pathlib import Path

    from .client_components import (
        COMPONENTS,
        ClientComponentError,
        ClientComponentPublisher,
    )
    from .client_publish import ClientPublishError, receive_with_rz

    publisher = ClientComponentPublisher()
    while True:
        print("\n客户端组件更新")
        print("  1. 发布到本地测试通道")
        print("  2. 将测试组件提升为正式组件")
        print("  3. 查看组件版本")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice == "1":
                _print_component_choices()
                component = input("组件名称：").strip().lower()
                if component not in COMPONENTS:
                    print("无效组件。")
                    continue
                version = input("组件版本：").strip()
                import tempfile

                cache = Path("/var/cache/scbl")
                cache.mkdir(parents=True, exist_ok=True)
                with tempfile.TemporaryDirectory(prefix="component-", dir=cache) as temporary:
                    root = Path(temporary)
                    print("  1. Xshell rz 手动上传")
                    print("  2. 使用服务端已有文件路径")
                    source_choice = input("请选择来源：").strip()
                    if source_choice == "1":
                        print(f"请选择 {COMPONENTS[component].filename}。")
                        source = receive_with_rz(root)
                    elif source_choice == "2":
                        source = Path(input("组件文件路径：").strip())
                    else:
                        print("无效来源。")
                        continue
                    entry = publisher.publish(component, version, source, channel="test")
                    print(
                        f"测试组件已发布：{component}@{entry['version']}，"
                        f"SHA256={entry['sha256']}"
                    )
            elif choice == "2":
                _print_component_choices()
                component = input("要提升的组件名称：").strip().lower()
                if input("确认把测试通道的同一文件提升为正式组件 [y/N]：").strip().lower() not in {"y", "yes"}:
                    continue
                entry = publisher.promote(component)
                print(f"正式组件已发布：{component}@{entry['version']}")
            elif choice == "3":
                status = publisher.status()
                for channel in ("stable", "test"):
                    print(f"{channel}：")
                    if not status[channel]:
                        print("  （无）")
                    for name, entry in sorted(status[channel].items()):
                        print(f"  {name} = {entry['version']}  {entry['sha256']}")
            else:
                print("无效选项。")
        except (ClientComponentError, ClientPublishError, OSError, ValueError) as exc:
            print(f"组件发布错误：{exc}")


def _print_component_choices() -> None:
    from .client_components import COMPONENTS

    print("可用组件：")
    for name, spec in COMPONENTS.items():
        print(f"  {name}: {spec.filename}")


def _announcement_menu() -> None:
    from .announcements import AnnouncementError, AnnouncementManager

    manager = AnnouncementManager()
    labels = {"active": "滚动公告", "startup": "启动公告", "update": "更新公告"}
    while True:
        print("\n公告管理")
        for index, kind in enumerate(("active", "startup", "update"), start=1):
            status = manager.status(kind)
            marker = "启用" if status.enabled else "停用"
            suffix = f" / v{status.version}" if kind == "update" and status.version else ""
            print(f"  {index}. {labels[kind]}：{marker}{suffix} / {status.title or '无内容'}")
        print("  0. 返回")
        choice = input("请选择：").strip()
        if choice == "0":
            return
        if choice not in {"1", "2", "3"}:
            print("无效选项。")
            continue
        kind = {"1": "active", "2": "startup", "3": "update"}[choice]
        while True:
            status = manager.status(kind)
            print(f"\n{labels[kind]}：{'启用' if status.enabled else '停用'}")
            print(f"标题：{status.title or '-'}")
            if kind == "update":
                print(f"对应客户端：{status.version or '-'}")
            print("  1. 设置内容并启用")
            print("  2. 启用 / 停用")
            print("  3. 清空")
            print("  0. 返回")
            action = input("请选择：").strip()
            try:
                if action == "0":
                    break
                if action == "1":
                    title = input("中文标题：")
                    body = input("中文内容：")
                    title_en = input("英文标题（可留空）：")
                    body_en = input("英文内容（可留空）：")
                    level = "info"
                    if kind != "update":
                        level = input("级别 info/warning/error [info]：").strip() or "info"
                    version = ""
                    if kind == "update":
                        current = manager.current_client_version()
                        version = input(f"对应客户端版本 [{current}]：").strip() or current
                    manager.set(
                        kind,
                        title=title,
                        body=body,
                        title_en=title_en,
                        body_en=body_en,
                        level=level,
                        version=version,
                    )
                    print("公告已原子发布。")
                elif action == "2":
                    target = not status.enabled
                    manager.set_enabled(kind, target)
                    print("公告已启用。" if target else "公告已停用。")
                elif action == "3":
                    if input("确认清空 [y/N]：").strip().lower() in {"y", "yes"}:
                        manager.clear(kind)
                        print("公告已清空并停用。")
                else:
                    print("无效选项。")
            except (AnnouncementError, OSError, ValueError) as exc:
                print(f"公告错误：{exc}")


def _backup_menu(paths: RuntimePaths) -> None:
    from .backup import BackupError, BackupManager

    manager = BackupManager(paths)
    while True:
        print("\n数据与备份")
        print("  1. 创建核心数据备份")
        print("  2. 创建完整备份（包含客户端发布包）")
        print("  3. 列出备份")
        print("  4. 清理旧备份（保留最新 5 份）")
        print("  0. 返回")
        choice = input("请选择：").strip()
        try:
            if choice == "0":
                return
            if choice in {"1", "2"}:
                info = manager.create(include_client_packages=choice == "2")
                print(f"备份完成：{info.path}（{info.size / 1024 / 1024:.1f} MiB）")
            elif choice == "3":
                items = manager.list()
                if not items:
                    print("暂无备份。")
                for item in items:
                    print(f"{item.modified_at:%Y-%m-%d %H:%M:%SZ}  {item.size / 1024 / 1024:.1f} MiB  {item.path}")
            elif choice == "4":
                answer = input("确认删除第 6 份及更早的备份 [y/N]：").strip().lower()
                if answer in {"y", "yes"}:
                    print(f"已删除 {len(manager.prune(keep=5))} 份旧备份。")
            else:
                print("无效选项。")
        except (BackupError, OSError, ValueError) as exc:
            print(f"备份错误：{exc}")
