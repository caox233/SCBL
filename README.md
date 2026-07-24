# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的自建客户端、服务端部署和客户端更新管理项目。每位部署者运行自己的 Linux 服务端、EasyTier 分布式网络、用户数据库和客户端更新源。

> 当前 Windows 客户端：**v0.6.3**  
> 当前 Linux 服务端工具：**v0.6.9**

## 快速开始

### Windows 客户端

普通用户请前往 [GitHub Releases](https://github.com/caox233/SCBL/releases/latest) 下载：

```text
SCBL-Client-v0.6.3-win-x86.zip
```

解压后运行 `SplinterCellCNLauncher.exe`。本项目不提供 Windows 在线安装脚本。

### Windows 本地编译

```powershell
git clone https://github.com/caox233/SCBL.git
cd SCBL\client
powershell -ExecutionPolicy Bypass -File .\build_all_windows.ps1
powershell -ExecutionPolicy Bypass -File .\create_client_full_package.ps1
```

客户端构建时从 `caox233/5th-echelon` 的已验证 Release 获取 Hooks DLL；SCBL 仓库不再保存 Hooks 源码或预编译 Hooks 文件。

### Linux 服务端一键部署

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/download/server-tool-stable-latest/install-server.sh | sudo bash
```

服务端安装器下载预编译专版 `dedicated_server`，不会在服务器上编译 Rust。现有文件与 Release SHA256 一致时直接复用。

服务端完成后会生成 `/opt/scbl-public/client_launcher_settings.json`。自建网络管理员应将其安全地提供给自己的客户端用户，保存到 `%LOCALAPPDATA%\SCBL_Public\launcher_settings.json`；真实密钥不得提交到 GitHub。

## 目录

```text
client/      Windows 启动器、Updater、EasyTier 准备脚本和 Route Guard
server/      Linux 服务端管理器和控制平面
scripts/     服务端工具稳定版的一键部署入口
docs/        GitHub Pages 和发布架构说明
.github/     验证、客户端构建、服务端工具构建和 Pages 工作流
```

## 客户端更新流程

更新格式和服务端发布方式保持不变，只增加 GitHub 全量包来源：

```text
GitHub 客户端稳定 Release
→ 服务端校验 manifest、文件名和 SHA256
→ 原子投递到 /opt/scbl-public/incoming/client/
→ 继续使用原有 package watcher
→ 生成文件级差量清单、完整包和更新公告
```

客户端启动时仍按原流程检查服务端更新源：

```text
客户端启动
→ 公网主机:18080 检查 manifest
→ 有更新：执行原更新流程并重启
→ 无更新：启动 EasyTier
→ 公网失败：仍启动 EasyTier
→ 私网连通后通过 10.66.0.1:18080 兜底检查
```

EasyTier 使用 TCP/UDP 11010，因此 HTTP 更新服务使用 TCP/18080。

## Route Guard

Route Guard 只授权本次由 Launcher 创建并写入实时会话心跳的游戏 PID。授权 PID 发往 SCBL 虚拟网段的 IPv4 TCP/UDP 流量会被固定到 EasyTier 网卡和虚拟源地址；授权 PID 发往公网、物理网卡或其他 VPN 的流量会被阻断；其他程序的数据包原样放行。

v0.6.3 将 TCP、UDP 端口级 PID 回退查询改为后台预生成索引，避免在 WinDivert 数据包热路径遍历完整 owner 表。`owner-unknown`、IPv6、回环代理转交和分片策略仍作为兼容性敏感项目继续测试。

## WinDivert 安全软件提示

客户端的严格路由与局域网广播转换功能使用 WinDivert 2.2.2。客户端包中的 `WinDivert.dll` 和 `WinDivert64.sys` 来自 WinDivert 官方发行包，构建时不会修改驱动文件。

WinDivert 是具有数据包截获、修改和重新注入能力的 Windows 内核网络驱动。少数安全软件可能基于这类能力将其识别为风险工具、可疑驱动或潜在不受欢迎程序。这类提示并不等同于确认存在恶意代码，但用户仍应只从本仓库正式 Release 下载客户端，并核对 Release 提供的 SHA256。

SCBL 不会自动关闭安全软件、添加杀毒排除项或绕过安全检测。如果安全软件阻止 `WinDivert64.sys`，Route Guard、广播转换或严格路由功能可能无法正常工作。

## 独立组件版本与 Release

Windows 客户端、Linux 服务端工具和 5th 二进制采用独立版本与构建通道：

| 组件 | 固定版本 Release | 滚动稳定 Release |
|---|---|---|
| Windows 客户端 | `client-vX.Y.Z` | `client-stable-latest` |
| Linux 服务端工具 | `server-tool-vX.Y.Z` | `server-tool-stable-latest` |
| 5th dedicated server 与 Hooks | 5th 项目 Release | `scbl-public-stable-latest` |

因此，仅修改客户端时不会重新打包服务端工具；仅修改服务端脚本时也不会启动 Windows 完整编译。

服务器运行 `SCBL` 后，可以分别：

- 从 GitHub 拉取最新 Windows 客户端并继续使用原有差量发布流程；
- 在线升级 SCBL 服务端管理工具和控制平面；
- 独立更新 5th Echelon 游戏服务端。

完整的校验、备份、回滚和兼容关系见 [`docs/RELEASE_ARCHITECTURE.md`](docs/RELEASE_ARCHITECTURE.md)。

## 发布与验证

- 提交到 `main`：校验 Shell、Python、Go、Hooks 外部所有权和版本一致性；
- 客户端文件或 `VERSION_CLIENT` 变化：只编译并发布 Windows 客户端；
- 服务端文件或 `VERSION_SERVER_TOOL` 变化：只校验并发布 Linux 服务端工具；
- `docs/` 变化：更新 GitHub Pages；
- Release 同时提供组件 manifest、SHA256 文件和汇总校验文件。

完整文档见 [项目网站](https://caox233.github.io/SCBL/) 和 [`docs/`](docs/)。

## 重要说明

SCBL 是非官方社区项目，与 Ubisoft 无隶属或授权关系。本仓库不应包含游戏本体文件。`dedicated_server`、Hooks 源码和 Hooks DLL 均不存放在 SCBL 源码仓库中，由 5th 项目独立编译和发布；SCBL 客户端构建流程只下载经 SHA256 校验的 Hooks Release 资产。其他第三方组件按 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 处理。
