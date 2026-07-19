# SCBL v0.6.0 GitHub 整体测试步骤

## 1. 创建仓库

在 GitHub 创建公开仓库 `caox233/SCBL`，默认分支使用 `main`，不要自动生成 README，然后把本目录全部提交到 `main`。

```powershell
git init
git branch -M main
git add .
git commit -m "Initial SCBL v0.6.0 public test"
git remote add origin https://github.com/caox233/SCBL.git
git push -u origin main
```

## 2. 先看 Actions

推送后应运行：

- `Validate source`：Shell、Python、控制平面和 Windows Route Guard 交叉编译；
- `Build Windows client`：在 Windows Server 2022 上编译并生成临时完整客户端 Artifact；
- `Deploy documentation website`：部署 `docs/`。

在仓库 Settings → Pages 中把 Source 设置为 **GitHub Actions**。

## 3. 测试 Windows 构建产物

从 `Build Windows client` 的成功 Run 下载 `SCBL-Windows-client` Artifact，里面应有：

```text
SCBL-Client-v0.6.0-win-x86.zip
SCBL-Client-v0.6.0-win-x86.zip.sha256
```

解压测试启动器。自建服务器客户端需要把服务器生成的：

```text
/opt/scbl-public/client_launcher_settings.json
```

保存为：

```text
%LOCALAPPDATA%\SCBL_Public\launcher_settings.json
```

## 4. 测试服务端更新

先用本包中的服务端脚本更新现有服务器。确认：

```bash
sudo ss -lntp | grep ':18080'
sudo systemctl status scbl-update.service
```

云安全组和 Linux 防火墙需要开放 TCP/18080。TCP/UDP 11010 仍由 EasyTier 使用。

## 5. 测试客户端全量包发布

在服务端运行：

```bash
SCBL
```

选择第 11 项，进入：

```text
/opt/scbl-public/incoming/client
```

上传 GitHub Actions 生成的完整客户端 ZIP，退出上传 Shell。原定时器每 60 秒自动发布，不需要改变原流程。

## 6. 测试“先更新、后接入私网”

单纯使用 v0.6.0 对 v0.6.0 无法观察升级动作。建议：

1. 先运行 v0.6.0 客户端；
2. 将根目录 `VERSION`、`client/SCBL.Version.props` 和代码版本统一改为 `0.6.1`；
3. 让 GitHub Actions 生成 v0.6.1 完整包；
4. 通过第 11 项发布 v0.6.1；
5. 暂时阻断 EasyTier 11010，但保持公网 TCP/18080 可访问；
6. 启动 v0.6.0 客户端。

预期结果：客户端在 EasyTier 启动前从公网 `服务器主机:18080` 发现 v0.6.1，显示原更新公告并执行原更新流程。

再测试兜底：阻断公网 TCP/18080、允许 EasyTier 11010。预期客户端不因公网检查失败而停止，接入私网后通过 `10.66.0.1:18080` 再检查。

## 7. 发布正式 Release

全部验证后创建标签：

```powershell
git tag v0.6.0
git push origin v0.6.0
```

`Publish SCBL release` 应生成：

- Windows 客户端完整 ZIP 和 SHA256；
- Linux 服务端版本包与 `latest` 静态别名；
- `install-server.sh`；
- `release-manifest.json`；
- `SHA256SUMS.txt`。

Release 工作流拒绝覆盖已经存在的同版本 Release。
