# [SERVER] Server Tool v1.0.4

## 修复内容

- 同步 Windows Client v1.0.5 所需的控制面兼容版本元数据。
- 保持 `tcpPort=null` 表示裸 TCP 入口关闭；新版客户端已能正确反序列化该字段。
- 服务端安装、双栈更新服务、EasyTier入口和控制面端口保持不变。

## dedicated_server

- dedicated_server 的游戏会话实现已在独立的 `caox233/5th-echelon` 仓库恢复为 `unixoide/5th-echelon` 上游实现，并由 `scbl-public-stable-latest` 滚动 Release 发布。
- 不修改 Hooks DLL、数据库迁移或数据库结构。
