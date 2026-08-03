# 本地生成 SCBL 测试包

Hooks、dedicated server 和共享协议现已在同一个 SCBL 仓库中。本流程从同一个 SCBL Commit 构建两端二进制，再组装为 Server Tool 菜单 16 可校验、部署的测试 ZIP。

## 1. 校验 Windows 端源码

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-rust-components.ps1
git rev-parse HEAD
```

脚本会检查格式并运行 Hooks、hook-loader 和 dedicated server 的本机测试。默认生产 Hooks 固定使用已经验证的私人邀请逻辑；只有显式使用 Cargo feature `diagnostic-evidence` 时才编译实验诊断证据代码。

## 2. Windows 编译 Hooks

安装 Visual Studio 2022 Build Tools 的“使用 C++ 的桌面开发”、仓库 `rust-toolchain.toml` 指定的 Rust 工具链，以及 Protobuf 编译器。随后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-rust-components.ps1 -Release
```

输出文件：

```text
target\i686-pc-windows-msvc\release\hooks.dll
```

部署和测试包中仍将其命名为 `uplay_r1_loader.dll`。

## 3. WSL/Ubuntu 编译 dedicated server

Linux 产物必须从同一份 SCBL 工作树、同一个 Commit 构建。建议将仓库放在 WSL 的 Linux 文件系统中，避免 `/mnt/c` 的文件系统开销。

```bash
sudo apt update
sudo apt install -y build-essential pkg-config protobuf-compiler libssl-dev

cd ~/SCBL
git rev-parse HEAD
cargo test --locked -p dedicated_server --release
cargo build --locked -p dedicated_server --release
```

确认 Commit 与 Windows 构建完全相同。Linux 输出文件为：

```text
target/release/dedicated_server
```

将它复制到 Windows 的本地打包目录，例如：

```bash
cp target/release/dedicated_server /mnt/d/SCBL-Build/dedicated_server-linux-x86_64
```

## 4. 组装测试 ZIP

```powershell
$Commit = (git rev-parse HEAD).Trim()
$Version = "2026.08.04.local1"
python .\scripts\package_local_test_candidate.py `
  --hooks .\target\i686-pc-windows-msvc\release\hooks.dll `
  --dedicated D:\SCBL-Build\dedicated_server-linux-x86_64 `
  --commit $Commit `
  --version $Version `
  --output D:\SCBL-Build
```

生成的 ZIP 和 `.sha256` 中，Hooks 与 dedicated 的 `commit_sha.txt`、`component.json.commit` 必须全部等于同一个 40 位 Commit SHA。打包器会拒绝空文件、错误的 PE/ELF 格式、不安全 ZIP 路径和无意覆盖同名输出。

## 5. 上传和部署

1. 在 Server Tool 中进入 `16. 测试管理`。
2. 通过 `16-1` 上传本地测试 ZIP。
3. 使用 `16-2` 选择、校验并部署；或用 `16-3` 部署最新上传包。
4. 两台 Windows 客户端均使用带 `--test` 的启动器启动。
5. 用 `16-6` 收集服务端诊断包。

上传阶段会先执行完整 dry-run 校验；校验失败的文件不会进入测试缓存。
