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

Hooks 不嵌入 Launcher EXE。正式包在 `bootstrap-components/hooks/` 携带校验后的副本；测试模式还可从 `local-components/hooks/uplay_r1_loader.dll` 动态覆盖，替换 DLL 不需要重编 Launcher。正式 `stable` 通道不会读取本地覆盖目录。

`build_launcher_smoke.ps1` 生成的 `SplinterCellCNLauncher.Smoke.exe` 只用于无法代点 UAC 的远程界面检查。它不请求管理员权限，因此不能用于驱动、网络、组件部署或游戏测试，也绝不能进入正式包。

## 自建服务器配置

服务端安装完成后会生成：

```text
/opt/scbl-public/client_launcher_settings.json
```

将它保存为：

```text
%LOCALAPPDATA%\SCBL_Public\launcher_settings.json
```

首次成功读取后，Launcher 会把隧道密钥迁移到当前 Windows 用户的 DPAPI 保护字段。不要提交真实密钥、生成后的配置、日志或诊断包。
