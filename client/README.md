# SCBL Windows 客户端

Windows 客户端包含 WPF Launcher、Updater、EasyTier、Route Guard 和 Hooks。普通用户使用自包含的 x86 完整包，不需要单独安装 .NET Runtime。

## 本地构建

环境要求：Windows 10/11、PowerShell 5.1 或 7、.NET 10 SDK、Visual Studio 2022 C++ Build Tools、仓库指定的 Rust 工具链、Protobuf、Go 1.23.x 和 Git。

```powershell
# Launcher / Updater / Route Guard / EasyTier
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Fast

# Hooks 与 dedicated server 回归
powershell -ExecutionPolicy Bypass -File .\scripts\build-rust-components.ps1

# 生成正式完整客户端包；Hooks 从本仓库 client/hooks 构建
powershell -ExecutionPolicy Bypass -File .\client\build_all_windows.ps1 -Fast -Package

# 仅供远程 UI/启动冒烟检查，不提权且不得发布
powershell -ExecutionPolicy Bypass -File .\client\build_launcher_smoke.ps1

# .NET 客户端单元测试（配置安全、版本策略、事务更新、存储保留）
dotnet test .\client\SCBL.Client.Tests\SCBL.Client.Tests.csproj -c Release -r win-x86
```

主要输出：

```text
client\ScblPublicLauncher\publish-single\
client\dist\SCBL-Client-vX.Y.Z-win-x86.zip
target\i686-pc-windows-msvc\release\hooks.dll
```

## 组件边界

- `ScblPublicLauncher/`：登录、更新门禁、虚拟网络编排、Hooks 部署和游戏生命周期。
- `SCBL.Updater/`：Launcher 完整包的原子更新与回滚。
- `hooks/`：游戏进程内 Uplay/Quazal 兼容层，运行时文件名为 `uplay_r1_loader.dll`。
- `scbl-process-router/`：基于 WinDivert 的严格进程路由。
- `easytier/`：固定版本的 Windows EasyTier 运行时准备脚本。

Hooks 不嵌入 Launcher EXE。正式包在 `tools/uplay_r1_loader.dll` 携带校验后的副本；测试模式还可从 `local-components/hooks/uplay_r1_loader.dll` 动态覆盖，替换 DLL 不需要重编 Launcher。正式 `stable` 通道不会读取本地覆盖目录。启动游戏时，Launcher 会把选定并校验后的 DLL 部署到游戏 `SYSTEM` 根目录。

`build_launcher_smoke.ps1` 生成的 `SplinterCellCNLauncher.Smoke.exe` 只用于无法代点 UAC 的远程界面检查。它不请求管理员权限，因此不能用于驱动、网络、组件部署或游戏测试，也绝不能进入正式包。

## 自建服务器配置

服务端安装完成后会生成：

```text
/opt/scbl-public/client_launcher_settings.json
```

将它保存为：

```text
客户端目录\temp\计算机名\config\launcher_settings.json
```

`TunnelSecret` 只作为首次配置引导；首次成功读取并保存后，Launcher 会清空明文字段并改用当前 Windows 用户的 DPAPI 保护字段。密码不接受明文 JSON。不要提交真实密钥、生成后的配置、日志或诊断包。

启动器右上角的 `⚙` 统一提供使用指引、中英文切换、声音开关和服务器设置。服务器设置可填写域名或 IP 与隧道端口，并单独指定更新服务端口；输入会先经过格式和端口范围校验，保存到本机 `launcher_settings.json`，重新启动 Launcher 后生效。恢复默认不会删除账号、密码或其他设置。

Launcher 生成的配置、日志、组件状态、网络运行状态、更新缓存和诊断包统一位于 `temp/计算机名/`。同一份 NAS 客户端可由多台电脑使用，各机器的数据不会互相覆盖；整个 `temp` 目录在完整包更新时都会被保留。

SCBL 2.0 只读取上述正式目录，不再扫描旧 `SCBL_Public` AppData 或旧客户端 `logs` 配置。启动时会轮转过大的游戏日志、清除过期工作文件，并限制诊断包、下载包和组件缓存数量。

```text
temp/<computer-name>/
  config/       launcher_settings.json
  logs/         Launcher、Updater 与 logs/game 下的 Hooks 日志
  network/      EasyTier 生成配置和运行资料
  runtime/      Route Guard、广播探测与游戏网络状态
  components/   版本化组件缓存和 component_state.json
  updates/      下载、解压工作区、回滚快照和更新回执
  diagnostics/  脱敏诊断包
```

游戏 `SYSTEM` 目录只保留游戏必须直接读取的 `scbl.toml`、Hooks DLL、原版 DLL 备份和游戏存档。`scbl.toml` 使用标准分区 TOML 格式，不读取旧 `5th_auth.dat`；Launcher 写入新配置时会删除旧文件。

Route Guard 和 EasyTier 组件包按整组事务安装：全部目标文件预先写入并校验，所有文件完成后才删除回滚副本；中途失败会恢复整组旧文件。客户端版本只允许向更高版本自动更新，不接受普通清单降级。
