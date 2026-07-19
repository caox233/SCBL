# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的自建客户端、服务端部署和客户端更新管理项目。每位部署者运行自己的 Linux 服务端、EasyTier 私有网络、用户数据库和客户端更新源。

> 首个公开测试版本：**v0.6.0**

## 快速开始

### Windows 客户端

普通用户请前往 [GitHub Releases](https://github.com/caox233/SCBL/releases/latest) 下载：

```text
SCBL-Client-v0.6.0-win-x86.zip
```

解压后运行 `SplinterCellCNLauncher.exe`。本项目不提供 Windows 在线安装脚本。

### Windows 本地编译

```powershell
git clone https://github.com/caox233/SCBL.git
cd SCBL\client
powershell -ExecutionPolicy Bypass -File .\build_all_windows.ps1
powershell -ExecutionPolicy Bypass -File .\create_client_full_package.ps1
```

### Linux 服务端一键部署

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/latest/download/install-server.sh | sudo bash
```

服务端安装器下载预编译专版 `dedicated_server`，不会在服务器上编译 Rust。现有文件与 Release SHA256 一致时直接复用。

服务端完成后会生成 `/opt/scbl-public/client_launcher_settings.json`。自建网络管理员应将其安全地提供给自己的客户端用户，保存到 `%LOCALAPPDATA%\SCBL_Public\launcher_settings.json`；真实密钥不得提交到 GitHub。

## 目录

```text
client/      Windows 启动器、Updater、Hooks 源码、EasyTier 准备脚本和 Route Guard
server/      Linux 服务端管理器和控制平面
scripts/     GitHub Release 一键部署入口
docs/        GitHub Pages 完整网页说明
.github/     验证、Windows 构建、Release 和 Pages 工作流
```

## 客户端更新流程

更新格式和服务端发布方式保持不变，只把检查提前：

```text
客户端启动
→ 公网主机:18080 检查现有 manifest
→ 有更新：执行原更新流程并重启
→ 无更新：启动 EasyTier
→ 公网失败：仍启动 EasyTier
→ 私网连通后通过 10.66.0.1:18080 兜底检查
```

EasyTier 使用 TCP/UDP 11010，因此现有 HTTP 更新服务不能直接复用 11010；公网更新使用 TCP/18080。

## WinDivert 安全软件提示

客户端的严格路由与局域网广播转换功能使用 WinDivert 2.2.2。客户端包中的 `WinDivert.dll` 和 `WinDivert64.sys` 来自 WinDivert 官方发行包，构建时不会修改驱动文件。

WinDivert 是具有数据包截获、修改和重新注入能力的 Windows 内核网络驱动。少数安全软件可能基于这类能力将其识别为风险工具、可疑驱动或潜在不受欢迎程序。这类提示并不等同于确认存在恶意代码，但用户仍应只从本仓库正式 Release 下载客户端，并核对 Release 提供的 SHA256。

SCBL 不会自动关闭安全软件、添加杀毒排除项或绕过安全检测。如果安全软件阻止 `WinDivert64.sys`，Route Guard、广播转换或严格路由功能可能无法正常工作。

## 发布

- 提交到 `main`：校验 Shell、Python、Go 和版本一致性；
- 客户端代码变化：GitHub Actions 自动编译完整 Windows 客户端并提供临时 Artifact；
- 推送 `v0.6.0` 标签：自动生成 Windows 客户端、Linux 服务端包、SHA256、Release 清单并发布 GitHub Release；
- `docs/` 变化：自动更新 GitHub Pages。

完整文档见 [项目网站](https://caox233.github.io/SCBL/) 和 [`docs/`](docs/)。

## 重要说明

SCBL 是非官方社区项目，与 Ubisoft 无隶属或授权关系。本仓库不应包含游戏本体文件。`dedicated_server` 不打包进 SCBL 源码仓库，而由安装器从专用构建仓库下载。当前 Hooks 源码快照位于 [`client/hooks-source/`](client/hooks-source/)，嵌入 DLL 与存档的固定校验值位于 `SCBL_EMBEDDED_SHA256.txt`。其他第三方组件按 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 处理。
