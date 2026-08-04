# SCBL

SCBL 是面向《细胞分裂：黑名单》社区联机环境的自建客户端、Linux 服务端部署和客户端更新管理项目。

> 当前 Windows 客户端：**v2.0.0**<br>
> 当前 Linux 服务端工具：**v2.0.2**

## 快速开始

### Windows 客户端

前往仓库的 Releases 页面，下载标题为 **[CLIENT] Windows Client v2.0.0** 的版本：

```text
SCBL-Client-v2.0.0-win-x86.zip
```

解压后运行 `SplinterCellCNLauncher.exe`。启动器会先确认服务器当前正式版本；版本不一致时必须更新或退出。

### Linux 服务端

```bash
curl -fsSL https://raw.githubusercontent.com/caox233/SCBL/main/server/bootstrap/install.sh \
  | sudo bash -s -- --public-host 你的域名或IP --online
```

安装器只在服务器执行 GitHub 已构建的二进制包，不在生产服务器编译源码。它会下载并校验
`server-tool-vX.Y.Z` 中的管理器，再从 `scbl-stable-latest` 安装完整服务端包。

## 版本和更新

- `VERSION_CLIENT` 是 Windows 客户端正式版本来源。
- `VERSION_SERVER_TOOL` 是 Linux 服务端工具版本来源。
- 完整 Release 标签分别为 `client-vX.Y.Z`、`server-tool-vX.Y.Z` 和滚动入口 `scbl-stable-latest`。
- Release 标题分别以 `[CLIENT]` 和 `[SERVER]` 开头，便于在同一列表中区分。
- Hooks 源码位于 `client/hooks`，dedicated server 源码位于 `server/dedicated-server`；两者由同一个 SCBL Commit 统一追踪，但仍作为独立组件构建和发布。

正式客户端版本门禁仍然优先执行，组件清单不能绕过或替代该门禁。组件分发的权威链路是：

```text
GitHub 单组件 Release ──> SCBL 服务端校验并发布 ──> 客户端读取服务端清单
```

客户端永远不直接读取 GitHub 组件地址。服务端可从 GitHub 只下载选中的一个组件，也可通过
`rz` 或已有文件路径手动上传；三种来源最终都生成同一种服务端同源清单。完整包内记录四个
组件的基线版本，客户端仅在服务端组件版本更高时下载；同版本直接使用完整包文件，较旧版本
拒绝降级，已下载版本继续核对大小和 SHA256。

当前组件目录包括：

```text
hooks         uplay_r1_loader.dll
route-guard   Route Guard + WinDivert 原子组件包
easytier      EasyTier Windows 运行时组件包
updater       SCBL.Updater.exe
```

完整客户端把经过校验的 Hooks 源副本放在 `tools/uplay_r1_loader.dll`，游戏启动前再部署到游戏 `SYSTEM` 根目录。Route Guard、EasyTier 和 Updater 先下载到版本化缓存，并在下一次相同更新通道启动、网络和游戏尚未运行时原子应用。切换回 `stable` 时不会使用 `test` 通道缓存。

普通启动默认使用正式 `stable` 通道。需要测试确定二进制时，在正式版快捷方式的“目标”末尾增加 `--test`：

```text
"D:\SCBL\SplinterCellCNLauncher.exe" --test
```

`--test` 等价于 `--update-channel test`，参数会在 UAC 提权重启后保留。关闭测试版启动器，再用不带参数的原快捷方式启动，即恢复正式通道。

本地反复调试 Hooks 时，可把 DLL 放到启动器旁的 `local-components/hooks/uplay_r1_loader.dll`，再用 `--test` 启动。启动器每次启动游戏前都会读取并覆盖这个当前文件，无需重编启动器或手工更新固定哈希；复制前后的实际 SHA256 仍会写入日志和部署标记。`stable` 通道永远忽略该本地覆盖目录。

客户端生成的设置、日志、网络状态、组件缓存、更新工作文件和诊断包统一保存在 `temp/计算机名/`。Hooks 使用游戏 `SYSTEM` 目录中的标准 `scbl.toml`；旧 `5th_auth.dat` 不再读取或保留。

`stable` 与 `test` 组件都使用不可变版本目录、同源下载、大小和 SHA256 校验。GitHub 滚动
标签使用 `client-component-组件名-stable`；服务端取得文件后仍写入自己的不可变版本目录。
测试组件可先发布到 `test`，验证后以同一文件和同一 SHA256 提升到 `stable`；正式通道下载
失败时继续使用完整包自带的组件。

## 构建方式

日常开发采用“改什么、编译什么、上传什么”：

```powershell
# 自动识别最近变更的 Windows 组件
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Auto -Fast

# 只构建 Launcher，不下载或嵌入 Hooks
powershell -ExecutionPolicy Bypass -File .\client\build_launcher_incremental.ps1 -Fast

# 仅正式发布或修复包时组装完整客户端
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Fast -Package

# 校验 Hooks、dedicated server 与共享协议；加 -Release 生成本机发布产物
powershell -ExecutionPolicy Bypass -File .\scripts\build-rust-components.ps1
```

组件拥有独立 GitHub Actions 工作流和缓存。修改组件时必须同步提升
`COMPONENT_VERSIONS.json` 中对应版本；工作流拒绝同版本替换为不同 SHA256。正式完整包由
`Publish SCBL stable release` 工作流统一构建 Windows 客户端、Linux 服务端和二进制部署包；
四个单组件 Release 只由各自的组件工作流发布，避免重复构建争用同一个不可变版本。

完整客户端包主要用于：

- 首次安装；
- 离线安装；
- 修复安装；
- Launcher 或平台级正式升级；
- 灾难恢复。

## 服务端组件仓库

服务端在更新根目录维护不可变组件版本及 `stable` / `test` 清单。组件管理器支持：

- 从 GitHub 按名称取得单个组件，或通过 `rz` / 路径手动发布；
- 发布确定 SHA256 的组件到 `stable` 或 `test`；
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
client/          Windows Launcher、Updater、Hooks、EasyTier 与 Route Guard
server/          Dedicated Server、Quazal、Linux 管理器、控制平面与回归测试
shared/          客户端与服务端共用的 gRPC 协议
scripts/         一键安装入口及长期维护脚本
docs/design/     当前设计文档
docs/changes/    重要架构变更基线
docs/releases/   当前与上一代正式发布说明
.github/         持续验证、组件构建、完整包和正式发布工作流
```

根目录只保留项目入口、版本源、许可证和协作规范。更早的版本说明查看 GitHub Releases、对应标签或 `CHANGELOG.md`；一次性验证结果保存在 Actions 日志和 Artifact 中，不提交状态快照文件。

## 安全说明

客户端的严格进程路由使用 WinDivert 2.2.2；虚拟网广播保持原样交给 EasyTier 处理。少数安全软件可能基于驱动的数据包处理能力显示风险提示。请只从本仓库正式 Release 下载，并核对 SHA256。

SCBL 不会自动关闭安全软件、添加排除项或绕过安全检测。`dedicated_server` 与 Hooks 在本仓库内维护；正式分发仍只消费带版本、大小和 SHA256 的确定资产。

SCBL 是非官方社区项目，与 Ubisoft 无隶属或授权关系。本仓库不包含游戏本体文件。
