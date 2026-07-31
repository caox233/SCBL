# 本地生成 SCBL 测试包

本流程从同一个 `caox233/5th-echelon` Commit 构建 Windows Hooks 和 Linux dedicated server，再组装为 Server Tool 菜单 16 可直接校验、部署的测试 ZIP。

## 1. 固定源码 Commit

Windows PowerShell 与 WSL/Ubuntu 必须使用同一个源码 Commit：

```powershell
git clone https://github.com/caox233/5th-echelon.git
cd 5th-echelon
git checkout agent/party-follow
git pull --ff-only
git rev-parse HEAD
```

记录输出的 40 位 Commit SHA。不要在 Windows 构建后又拉取新提交，再用不同源码构建 Linux 服务端。

## 2. Windows 编译 Hooks

安装 Visual Studio 2022 Build Tools，勾选“使用 C++ 的桌面开发”；安装 Rust MSVC 工具链和 Protobuf 29.x，并确认 `protoc --version` 可用。

```powershell
rustup toolchain install stable-x86_64-pc-windows-msvc
rustup target add i686-pc-windows-msvc
cargo test --locked -p hooks --release
cargo build --locked -p hooks --release
```

输出文件：

```text
target\i686-pc-windows-msvc\release\hooks.dll
```

测试包中必须改名为：

```text
uplay_r1_loader.dll
```

## 3. WSL/Ubuntu 编译 dedicated server

建议在 WSL 的 Linux 文件系统中单独克隆同一仓库，避免 `/mnt/c` 文件系统降低 Rust 编译速度。

```bash
sudo apt update
sudo apt install -y build-essential pkg-config protobuf-compiler libssl-dev python3 git

git clone https://github.com/caox233/5th-echelon.git ~/5th-echelon
cd ~/5th-echelon
git checkout agent/party-follow
git pull --ff-only
git rev-parse HEAD
cargo test --locked -p dedicated_server --release
cargo build --locked -p dedicated_server --release
```

确认这里的 Commit SHA 与 Windows 完全相同。输出文件：

```text
target/release/dedicated_server
```

复制到 Windows 仓库的本地打包目录，例如：

```bash
cp target/release/dedicated_server /mnt/d/SCBL-Build/dedicated_server-linux-x86_64
```

## 4. 组装测试 ZIP

推荐使用 `5th-echelon/scripts/package_local_test_candidate.py`。示例：

```powershell
$Commit = (git rev-parse HEAD).Trim()
$Version = "2026.07.31.local1"
python .\scripts\package_local_test_candidate.py `
  --hooks .\target\i686-pc-windows-msvc\release\hooks.dll `
  --dedicated D:\SCBL-Build\dedicated_server-linux-x86_64 `
  --commit $Commit `
  --version $Version `
  --output D:\SCBL-Build
```

生成：

```text
SCBL-Invite-Party-Test-2026.07.31.local1.zip
SCBL-Invite-Party-Test-2026.07.31.local1.zip.sha256
```

包内必须包含：

```text
SCBL-Invite-Party-Test-<版本>/
  CHECKSUMS.sha256
  TEST_CANDIDATE.txt
  Artifacts/
    scbl-hooks-party-follow-test.zip
    hooks-extracted/
      uplay_r1_loader.dll
      uplay_r1_loader.dll.sha256
      commit_sha.txt
      component.json
    dedicated-extracted/
      dedicated_server-linux-x86_64
      dedicated_server-linux-x86_64.sha256
      commit_sha.txt
      component.json
```

Hooks 与 dedicated 的 `commit_sha.txt`、`component.json.commit` 必须全部等于同一个 40 位 Commit SHA。

## 5. 上传和部署

1. 服务器升级到 Server Tool v1.0.12 后，退出并重新执行 `SCBL`。
2. 进入 `16. 测试管理`。
3. 选择 `1. 从当前电脑上传本地测试 ZIP`，在 Xshell 文件窗口中选择生成的 ZIP。
4. 上传完成后选择 `2. 选择已上传测试 ZIP并校验、部署`。
5. 确认包名、Commit 和 SHA256 后输入 `DEPLOY-TEST`。
6. 两台 Windows 客户端使用带 `--test` 参数的测试通道快捷方式启动。

上传阶段会先执行完整 dry-run 校验；校验失败的文件不会进入正式测试缓存。

## 6. 收集日志到当前电脑

服务端进入 `16-6`。诊断包生成后选择发送到当前电脑。Xshell 的本地保存目录由“工具 → 选项 → 文件传输 → ZMODEM 接收文件夹”控制；将其设置为 Windows 桌面即可。
