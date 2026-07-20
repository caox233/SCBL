# SCBL v0.6.2

本版本主要修复多人联机房主识别、Route Guard 数据路径卡顿以及服务端控制平面稳定性问题，并统一 5th Echelon 服务端与 Hooks 的构建来源。

## 主要更新

- 修复 5th Echelon `JoinSession` 未持久化参与者的问题，完善创建会话和房主迁移流程。
- 调整 SCBL 控制平面的多人会话选择逻辑，避免单人会话覆盖真实多人房间。
- 将控制平面健康检查与会话数据库查询移出 HTTP 请求热路径，降低请求超时概率。
- Route Guard 的进程归属缓存未命中改为后台异步刷新，避免阻塞 WinDivert 收发循环。
- 修复游戏正常结束后 Route Guard 退出被误计为异常退出的问题。
- 保持 EasyTier 分布式 Mesh、P2P、多跳 Relay、TCP/WSS 备用接入能力。
- Hooks 与 dedicated server 统一由 `caox233/5th-echelon` 构建并进行 SHA256 校验。
- SCBL 仓库不再保留重复的 Hooks 源码和预编译 DLL。
- 服务端安装脚本支持默认 Release 下载，以及指定仓库、分支和 GitHub PAT 下载 Actions Artifact。

## 发布文件

- `SCBL-Client-v0.6.2-win-x86.zip`
- `SCBL-Client-v0.6.2-win-x86.zip.sha256`
- `SCBL-Server-v0.6.2-linux-x86_64.tar.gz`
- `SCBL-Server-v0.6.2-linux-x86_64.tar.gz.sha256`
- `release-manifest.json`
- `SHA256SUMS.txt`

## 测试重点

本版本发布后重点验证多人房间参与者数量、房主识别与迁移、长时间联机卡顿、控制平面超时，以及不能通过 UDP 直连服务器的玩家通过 TCP/WSS 和其他 EasyTier 节点加入游戏的能力。
