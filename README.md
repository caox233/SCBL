# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的自建客户端、Linux 服务端部署和客户端更新管理项目。

> 当前 Windows 客户端：**v1.0.14**<br>
> 当前 Linux 服务端工具：**v1.0.12**

## 快速开始

### Windows 客户端

前往仓库的 Releases 页面，下载标题为 **[CLIENT] Windows Client v1.0.14** 的版本：

```text
SCBL-Client-v1.0.14-win-x86.zip
```

解压后运行 `SplinterCellCNLauncher.exe`。启动器会先确认服务器当前正式版本；版本不一致时必须更新或退出。

### Linux 服务端

```bash
curl -fsSL https://raw.githubusercontent.com/caox233/SCBL/main/scripts/install-server.sh | sudo bash
```

安装脚本读取 `VERSION_SERVER_TOOL`，下载对应的 **[SERVER] Server Tool vX.Y.Z** 完整包并校验文件。服务端管理工具与 dedicated server 二进制保持独立版本和独立更新边界。

## 版本和更新

- `VERSION_CLIENT` 是 Windows 客户端正式版本来源。
- `VERSION_SERVER_TOOL` 是 Linux 服务端工具版本来源。
- Release 标签分别为 `client-vX.Y.Z` 和 `server-tool-vX.Y.Z`。
- Release 标题分别以 `[CLIENT]` 和 `[SERVER]` 开头，便于在同一列表中区分。
- Hooks 与 dedicated server 由 `caox233/5th-echelon` 独立构建和发布。

正式客户端版本门禁仍然优先执行，组件清单不能绕过或替代该门禁。客户端通过服务器更新服务读取组件清单，并逐项核对本地组件：

```text
本地文件不存在          → 下载该组件
本地大小或 SHA256 不同  → 重新下载该组件
本地文件与清单一致      → 直接复用，不重复下载
```

当前组件目录包括：

```text
hooks         uplay_r1_loader.dll
route-guard   Route Guard + WinDivert 原子组件包
easytier      EasyTier Windows 运行时组件包
updater       SCBL.Updater.exe
```

Hooks 在游戏启动前部署。Route Guard、EasyTier 和 Updater 先下载到版本化缓存，并在下一次相同更新通道启动、网络和游戏尚未运行时原子应用。切换回 `stable` 时不会使用 `test` 通道缓存。

普通启动默认使用正式 `stable` 通道。需要测试确定二进制时，在正式版快捷方式的“目标”末尾增加 `--test`：

```text
"D:\SCBL\SplinterCellCNLauncher.exe" --test
```

`--test` 等价于 `--update-channel test`，参数会在 UAC 提权重启后保留。关闭测试版启动器，再用不带参数的原快捷方式启动，即恢复正式通道。

`stable` 外置组件替换在组件清单签名验证完成前保持关闭；正式客户端继续使用完整包自带、经过 SHA256 校验的 bootstrap 组件。

## 构建方式

日常开发采用“改什么、编译什么、上传什么”：

```powershell
# 自动识别最近变更的 Windows 组件
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Auto -Fast

# 只构建 Launcher，不下载或嵌入 Hooks
powershell -ExecutionPolicy Bypass -File .\client\build_launcher_incremental.ps1 -Fast

# 仅正式发布或修复包时组装完整客户端
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Fast -Package
```

组件拥有独立 GitHub Actions 工作流和缓存。普通 PR 只验证受影响组件，不组装完整客户端；正式完整包工作流并行获取 Launcher、Updater、Route Guard、EasyTier 产物，加入已验证的 bootstrap Hooks 后组装 ZIP，不重新编译已经测试过的组件。

完整客户端包主要用于：

- 首次安装；
- 离线安装；
- 修复安装；
- Launcher 或平台级正式升级；
- 灾难恢复。

## 本地测试候选

Server Tool v1.0.12 的菜单 `16. 测试管理` 改为本地测试包流程：

- `16-1` 通过 Xshell/ZMODEM 从当前电脑上传测试 ZIP；
- `16-2` 从已上传 ZIP 中选择并部署；
- `16-3` 部署最新上传 ZIP；
- `16-6` 收集服务端诊断包，并可通过 ZMODEM 发回当前电脑。

完整的 Windows Hooks、WSL/Ubuntu dedicated server 编译及测试 ZIP 组装步骤见：

```text
docs/guides/LOCAL_TEST_CANDIDATE.md
```

## 服务端组件仓库

服务端在更新根目录维护不可变组件版本及 `stable` / `test` 清单。组件管理器支持：

- 发布确定 SHA256 的组件到 `test`；
- 将同一个已测试二进制提升到 `stable`，不重新编译；
- 回滚到现有不可变版本；
- 校验所有清单、组件大小和 SHA256；
- 拒绝同版本覆盖为不同内容。

组件仓库不会覆盖 dedicated server 数据库、EasyTier 配置、DDNS 配置、客户端包或运行时密钥。

## 网络路径

- 启动器、控制平面和游戏服务端流量优先与固定服务器直接通信。
- 玩家之间优先使用 EasyTier P2P UDP，UDP 无法打洞时保留 P2P TCP 打洞。
- 普通客户端不承担第三方数据中继；固定服务器使用 UDP 主入口和 WSS 兜底。
- 使用稳定的一跳优先策略，不为很小的延迟差异切换到多跳路径。

## 目录

```text
client/          Windows Launcher、Updater、EasyTier 与 Route Guard
server/          Linux 服务端管理器、组件仓库、控制平面与回归测试
scripts/         一键安装入口及长期维护脚本
docs/design/     当前设计文档
docs/changes/    重要架构变更基线
docs/releases/   当前与上一代正式发布说明
.github/         持续验证、组件构建、完整包和正式发布工作流
```

根目录只保留项目入口、版本源、许可证和协作规范。更早的版本说明查看 GitHub Releases、对应标签或 `CHANGELOG.md`；一次性验证结果保存在 Actions 日志和 Artifact 中，不提交状态快照文件。

## 安全说明

客户端的严格进程路由使用 WinDivert 2.2.2；虚拟网广播保持原样交给 EasyTier 处理。少数安全软件可能基于驱动的数据包处理能力显示风险提示。请只从本仓库正式 Release 下载，并核对 SHA256。

SCBL 不会自动关闭安全软件、添加排除项或绕过安全检测。`dedicated_server`、Hooks 源码和 Hooks DLL 由 5th 项目独立构建和发布，SCBL 只消费经校验的确定资产。

SCBL 是非官方社区项目，与 Ubisoft 无隶属或授权关系。本仓库不包含游戏本体文件。
