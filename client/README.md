# SCBL Windows 客户端

SCBL v0.6.1 的正式客户端由 GitHub Actions 在 Windows Server 2022 上自动编译，普通用户直接从 GitHub Releases 下载完整 ZIP。仓库不提供 Windows 在线安装脚本。

## 本地一键编译

环境要求：Windows 10/11、PowerShell 5.1 或 7、.NET 8 SDK、Go 1.23.x、Git。

```powershell
git clone https://github.com/caox233/SCBL.git
cd SCBL\client
powershell -ExecutionPolicy Bypass -File .\build_all_windows.ps1
powershell -ExecutionPolicy Bypass -File .\create_client_full_package.ps1
```

构建脚本会下载固定版本 EasyTier 和 WinDivert，编译 WPF 启动器、更新器和 Route Guard，然后输出：

```text
client\ScblPublicLauncher\publish-single\
client\dist\SCBL-Client-v0.6.1-win-x86.zip
```

## 启动更新顺序

v0.6.1 保留原更新协议和完整客户端发布方式，只调整启动顺序：

1. 先通过“当前公网服务器主机 + TCP/18080”读取原 `client_update_manifest.json`；
2. 有更新时沿用原公告、差异下载、Updater 和回滚流程；
3. 公网检查成功且无更新时直接启动 EasyTier；
4. 公网检查失败时仍启动 EasyTier，私网连通后再用 `10.66.0.1:18080` 兜底检查。

TCP/11010 已由 EasyTier 占用，不能同时承载现有 HTTP 更新服务。

## 连接自己的自建服务器

服务端安装完成后会生成：

```text
/opt/scbl-public/client_launcher_settings.json
```

把该文件安全地交给客户端用户，并保存为：

```text
%LOCALAPPDATA%\SCBL_Public\launcher_settings.json
```

首次成功读取后，启动器会把隧道密钥迁移到当前 Windows 用户的 DPAPI 保护字段。不要把包含真实密钥的配置提交到 GitHub。

## Hooks 源码与嵌入存档

当前 Hooks Rust 源码快照位于：

```text
client\hooks-source\hooks\
```

它属于 `5th-echelon` 完整 Rust 工作区，具体编译方法见 [`hooks-source/README.md`](hooks-source/README.md)。客户端正式构建继续嵌入 `EmbeddedFiles/uplay_r1_loader.dll`，并通过 `EmbeddedFiles/SCBL_EMBEDDED_SHA256.txt` 校验 DLL 和四个联机存档文件。
