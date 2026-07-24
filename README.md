# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的自建客户端、Linux 服务端部署和客户端更新管理项目。

> 当前 Windows 客户端：**v1.0.0**  
> 当前 Linux 服务端工具：**v1.0.0**

## 快速开始

### Windows 客户端

前往仓库的 Releases 页面，下载标题为 **[CLIENT] Windows Client v1.0.0** 的版本：

```text
SCBL-Client-v1.0.0-win-x86.zip
```

解压后运行 `SplinterCellCNLauncher.exe`。启动器会先确认服务器当前正式版本；版本不一致时必须更新或退出。

### Linux 服务端

```bash
curl -fsSL https://raw.githubusercontent.com/caox233/SCBL/main/scripts/install-server.sh | sudo bash
```

安装脚本读取 `VERSION_SERVER_TOOL`，下载对应的 **[SERVER] Server Tool vX.Y.Z** 完整包并校验文件。

## 版本和更新

- `VERSION_CLIENT` 是 Windows 客户端版本来源。
- `VERSION_SERVER_TOOL` 是 Linux 服务端工具版本来源。
- Release 标签分别为 `client-vX.Y.Z` 和 `server-tool-vX.Y.Z`。
- Release 标题分别以 `[CLIENT]` 和 `[SERVER]` 开头，便于在同一列表中区分。
- 每个组件仅保留当前版本和上一版本，不再创建 Stable Release。

服务端首次部署或升级后会自动检查正式客户端：

```text
没有客户端包        → 下载最新版完整包
本地版本较旧        → 下载最新版完整包
版本和 SHA256 一致  → 不重复下载
本地版本较高        → 保留现有版本
检查失败且已有包    → 继续使用现有版本
检查失败且没有包    → 提示部署未完成
```

服务器把当前正式客户端版本写入更新信息。Launcher 启动时先读取该信息：版本一致才继续初始化游戏目录、网络和其他功能；版本不一致只能更新或退出。

## 本地编译

```powershell
git clone https://github.com/caox233/SCBL.git
cd SCBL\client
powershell -ExecutionPolicy Bypass -File .uild_all_windows.ps1 -Fast -Package
```

客户端构建从 `caox233/5th-echelon` 的已验证 Release 获取 Hooks DLL；SCBL 仓库不保存 Hooks 源码或预编译 Hooks 文件。

## 目录

```text
client/      Windows Launcher、Updater、EasyTier 准备脚本和 Route Guard
server/      Linux 服务端管理器和控制平面
scripts/     服务端安装入口和维护脚本
docs/        发布说明和技术文档
.github/     验证、构建和发布工作流
```

## 安全说明

客户端的严格路由和局域网广播转换使用 WinDivert 2.2.2。少数安全软件可能基于驱动的数据包处理能力显示风险提示。请只从本仓库正式 Release 下载，并核对 SHA256。

SCBL 不会自动关闭安全软件、添加排除项或绕过安全检测。`dedicated_server`、Hooks 源码和 Hooks DLL 由 5th 项目独立构建和发布，SCBL 只下载经校验的正式资产。

SCBL 是非官方社区项目，与 Ubisoft 无隶属或授权关系。本仓库不包含游戏本体文件。
