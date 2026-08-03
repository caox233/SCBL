# SCBL Hooks build

SCBL 的 Windows x86 Hooks DLL 与 Linux dedicated server 均由本仓库统一编译。

`Build SCBL 5th binaries` 工作流在 `main` 的 Hooks、dedicated server 或相关依赖发生变化时执行，并将校验后的滚动二进制发布到 `scbl-public-stable-latest`。Rust 源码在提交后使用固定工具链执行格式校验、测试和 Release 构建。

滚动 Release 至少包含：

- `uplay_r1_loader.dll` 与 SHA256；
- `dedicated_server-linux-x86_64` 与 SHA256。

两类二进制必须在同一次工作流中成功构建并通过 SHA256 自检后，才会覆盖滚动 Release。

SCBL 客户端仓库不再保存 Hooks 源码或预编译 DLL；构建时从本仓库 Release 或指定分支的 GitHub Actions Artifact 下载并校验。
