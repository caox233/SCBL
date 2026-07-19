# SCBL v0.6.0

SCBL 首个公开测试版本，包含 Windows 客户端、Linux 自建服务端管理器、客户端更新发布流程和 GitHub 自动构建发布能力。

## 主要内容

- Windows 完整客户端：Launcher、Updater、EasyTier、Route Guard、Hooks 和替换存档；
- Linux 服务端一键部署与菜单化管理；
- 每个部署者运行独立的 EasyTier 私有网络、用户数据库和客户端更新源；
- 客户端启动时先通过公网 TCP/18080 使用现有协议检查更新，再启动 EasyTier；
- 公网更新检查失败时仍会尝试加入私网，并通过 `10.66.0.1:18080` 兜底检查；
- GitHub Actions 自动构建 Windows 客户端、Linux 服务端包、SHA256 和 Release 清单；
- Route Guard 修复 WinDivert 自动准备问题；
- 修复 Windows 客户端和正式 Release 的 ZIP SHA256 生成步骤。

## WinDivert 安全软件提示

SCBL 的严格路由、局域网广播转换和数据包重写功能使用 WinDivert 2.2.2。客户端包中的 `WinDivert.dll` 与 `WinDivert64.sys` 来自 WinDivert 官方发行包，构建时不会修改驱动文件。

WinDivert 是具有数据包截获、修改和重新注入能力的 Windows 内核网络驱动。少数安全软件可能基于这类能力将 `WinDivert64.sys` 识别为风险工具、可疑驱动或潜在不受欢迎程序。这类提示并不等同于确认存在恶意代码。

SCBL 不会自动关闭安全软件、添加杀毒排除项或绕过安全检测。请只从本仓库正式 Release 下载，并使用 `SHA256SUMS.txt` 或独立 `.sha256` 文件核对下载内容。如果安全软件阻止该驱动，Route Guard、广播转换或严格路由功能可能无法正常工作。

## 下载说明

普通 Windows 用户下载：

```text
SCBL-Client-v0.6.0-win-x86.zip
SCBL-Client-v0.6.0-win-x86.zip.sha256
```

Linux 服务端部署：

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/latest/download/install-server.sh | sudo bash
```

所有发布文件也统一列入 `SHA256SUMS.txt`。
