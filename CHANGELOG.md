# 更新记录

## v1.0.0 正式版

- 客户端版本确认成为启动器首要流程，版本不一致时必须更新或退出。
- 客户端和服务端统一使用完整包更新，版本由发布文件自动识别。
- 删除客户端和服务端的 Stable Release，正式版本使用清晰的 `[CLIENT]`、`[SERVER]` 标题。
- 删除人工最低客户端版本配置，控制平面自动要求服务器当前发布的客户端版本。
- 首次部署和 Server Tool 升级后自动检查并同步正式客户端。

## Server Tool v0.6.10

- 修复 v0.6.9 DDNS-GO 原生化迁移中 `write_ddns_go_service` 无限递归导致 `SCBL` 卡住的问题。
- 增加 DDNS-GO 服务生成函数的递归回归检查。

# 更新记录

## Server Tool v0.6.9

- DDNS-GO 改为官方原生配置：A / AAAA、DNS 服务商、域名、网卡和 IPv6 匹配规则全部由官方 Web 页面管理。
- 删除 SCBL 自建 IPv4 / IPv6 取址命令、模式强制器和配置监视服务；升级时只迁移引用这些旧命令的配置项，并先生成备份。
- Web 管理自动绑定服务器局域网私有 IPv4 的 9876 端口；没有私有 IPv4 时回退到 127.0.0.1，拒绝 0.0.0.0、公共 IPv4 和 IPv6 监听。
- DDNS-GO 菜单仅保留安装、更新、启动、状态、密码重置和保留配置的卸载。
- Server Tool v0.6.8 已在真实 Ubuntu 26 Server 上完成原地升级验证，Windows 客户端 v0.6.3 可进入线上模式；客户端和 Hooks 保持不变。

## Server Tool v0.6.8

- 修复全新服务器缺少 `service.toml.template` 时，dedicated_server 自动配置把 `SandboxUrl`、`SandboxUrlWS`、`secure_server_addr` 和 `storage_host` 保留为 `127.0.0.1`，导致客户端账号登录成功但游戏无法进入线上模式的问题。
- 首次安装生成与 dedicated_server 默认结构一致且带独立随机票据密钥的配置；升级已有服务器时自动备份，并只修复四个错误的客户端服务地址。
- 服务端状态新增在线配置、内容服务、PRUDP 认证与安全端口检查。
- 控制平面按 UDP 协议检查 21126/21127，不再用 TCP 检测 PRUDP 服务而误报 degraded。
- 不修改、不重编译 Hooks 源码；Windows 客户端版本保持不变。

## Server Tool v0.6.5

- Fixed the piped bootstrap installer appearing to hang after downloading checksums.
- Added bounded download and validation timeouts with visible stage messages.
- Reattached the downloaded manager explicitly to `/dev/tty` before entering the interactive menu.


## Server Tool v0.6.4

- Added explicit rollback for the retained previous Windows client package.
- Added manual rollback to the most recent server-tool upgrade backup with a pre-rollback safety copy.
- Hardened component manifest/file matching and safe bootstrap extraction.
- Added embedded-Python validation before installing a downloaded manager script.

## Client 0.6.3 / Server Tool 0.6.3

- Split Windows client and Linux server-tool versions and release workflows.
- Added GitHub client Release import through the existing package watcher.
- Added verified server-tool online self-upgrade with backup and rollback.
- Retained the newest two locally published client packages.
- Changed Route Guard TCP/UDP fallback owner resolution to precomputed O(1) indexes.


## v0.6.1

- 修复 Linux 服务端菜单选择“1. 首次安装 / 重新安装”后短时间无输出的问题；
- 移除 `set_defaults` 中静默执行的公网 IP 网络探测；
- 进入安装流程后立即显示阶段提示；
- 仅在需要填写公网入口时检测公网 IPv4，并显示最长等待时间与检测结果；
- 保持客户端更新协议、EasyTier、WinDivert 和 Route Guard 逻辑不变。

## v0.6.0

- 公开当前客户端使用的 Hooks Rust 源码快照；
- 使用项目维护者提供的新存档文件替换旧嵌入存档，并固定 SHA256 清单；

- 建立 GitHub 公开仓库结构，保留 `client/`、`server/` 主目录；
- Windows 客户端支持 GitHub Actions 自动编译和 Release 完整包；
- 仅提供客户端本地编译脚本，普通用户从 Release 下载预编译 ZIP；
- 新增 Linux 服务端 Release 一键部署入口；
- 服务端继续下载专版 `dedicated_server`，本地哈希一致时复用；
- 客户端更新检查提前到 EasyTier 启动前；
- 公网更新通过 TCP/18080，失败后通过原私网地址兜底；
- 保留现有更新公告、manifest、差异更新、Updater、回滚和服务端全量包发布流程；
- 第 11 项继续进入 `/opt/scbl-public/incoming/client`；
- 新增源码验证、客户端构建、正式 Release 和 GitHub Pages 工作流。
