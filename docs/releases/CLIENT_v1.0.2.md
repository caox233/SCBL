# [CLIENT] Windows Client v1.0.2

## 网络调整

- 固定服务器入口改为 UDP 主用、WSS 兜底，不再配置裸 TCP 服务器 Peer。
- 玩家之间继续优先进行 UDP 打洞，并保留 TCP 打洞作为直接连接的第二选择。
- 普通客户端不转发第三方玩家数据，P2P 均失败时由固定服务器兜底。
- WSS 默认复用 11010/TCP；UDP 使用同一端口号的 11010/UDP。

## 安全边界

- Hooks 源码和 `uplay_r1_loader.dll` 未修改。
- Route Guard 的进程授权和虚拟网段限制保持不变。
