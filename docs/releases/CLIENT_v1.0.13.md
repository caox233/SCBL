# SCBL Windows Client v1.0.13

## 主要更新

- 将邀请功能修复后的 `uplay_r1_loader.dll` 正式嵌入启动器单文件 EXE；启动游戏前仍由启动器从内置资源覆盖部署到游戏目录。
- 支持已经创建私人房间后，邀请线上好友接受并加入该房间。
- 支持在大厅邀请多名线上好友进入同一大厅队伍，为队长后续创建私人房间或发起快速搜索提供同队基础。
- F5 邀请窗口增加中文提示和明确的接受操作。
- 修复大厅邀请遇到服务端错误时 Hooks 使用 `unwrap()` 导致游戏直接退出的问题。
- 增加 API 在线心跳，降低玩家仍停留在大厅却被好友列表错误显示为离线的情况。

## 配套版本

- Embedded Hooks source: `caox233/5th-echelon@3defed6f595b59ed01a4c9c8af27b0f02d3ce905`
- Embedded Hooks SHA256: `c816b06d3f651de5b6ae17dd9dd548a396b4e9e55cb802b98aa4d8fd601abbca`
- Linux Server Tool: v1.0.8（未变更）

## 升级说明

所有参与邀请测试的 Windows 客户端都必须升级到 v1.0.13。只更新 dedicated server 或手工替换一次 DLL 不足以保证生效，因为旧版启动器每次启动游戏时都会用自身内置资源重新覆盖 `uplay_r1_loader.dll`。

本版本的正式构建流程会在发布前从 `5th-echelon` 的 `scbl-public-stable-latest` Release 下载 Hooks DLL，校验 SHA256 后再嵌入启动器。

## 构建验证

完整 Windows 打包流程已实际生成 `SCBL-Client-v1.0.13-win-x86.zip`，确认构建期间嵌入的 Hooks DLL SHA256 与上述值完全一致；Route Guard 源码在最终合并前执行格式化、Windows 目标测试和编译。
