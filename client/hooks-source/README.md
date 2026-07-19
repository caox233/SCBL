# SCBL 当前 Hooks 源码快照

本目录保存 SCBL v0.6.0 客户端当前使用的 `uplay_r1_loader.dll` 对应 Hooks 源码快照。

## 目录说明

```text
client/hooks-source/hooks/
```

该目录原本属于 `5th-echelon` Rust 工作区中的 `hooks` crate，不是独立 Cargo 工作区。它引用：

- 根工作区的 `[workspace.dependencies]`；
- 相邻的 `api` crate；
- `hooks-addresses`、`hooks-config`、`hooks-proc` 和 `shim-gen` 子 crate。

因此不要直接在本目录运行独立 `cargo build`。需要编译时：

1. 克隆 `caox233/5th-echelon`；
2. 切换到 `scbl-public-stable` 分支；
3. 用本目录的 `hooks/` 覆盖该工作区根目录中的 `hooks/`；
4. 安装 `i686-pc-windows-msvc` Rust 目标；
5. 在完整工作区中构建 `hooks` 包；
6. 将生成的 DLL 作为 `uplay_r1_loader.dll` 放入 `client/ScblPublicLauncher/EmbeddedFiles/`。

SCBL v0.6.0 的 GitHub 客户端发布流程继续嵌入仓库中已经确认使用的 DLL，避免 Hooks 工作区构建变化影响客户端主发布。源码快照用于公开审阅、后续维护和可追溯性。

## 当前快照

- Hooks crate 版本：`0.2.5`
- 目标平台：`i686-pc-windows-msvc`
- 产品名：`UPlay R1 Loader for 5th Echelon CN`
- 原始上传压缩包 SHA256：`b6df6f731eb9f06e71ca0a6f844138c229be8070e12790a9f97a36db63398cfd`
