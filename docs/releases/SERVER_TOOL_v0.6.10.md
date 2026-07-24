# SCBL Server Tool v0.6.10

## 修复内容

- 修复 v0.6.9 首次执行 DDNS-GO 原生化迁移时，`write_ddns_go_service` 错误调用自身导致管理工具卡住的问题。
- 恢复正确的 `ddns-go.service` 生成逻辑，并继续把 Web 管理端口限制在自动检测到的局域网私有 IPv4 或 `127.0.0.1:9876`。
- 增加回归测试，明确禁止服务生成函数递归调用自身。

## 版本边界

- Windows client remains v0.6.3。
- Hooks 源码及 `uplay_r1_loader.dll` 未修改。
- 不修改、不覆盖 `server/5th-echelon.db`。
- 保留已有 DDNS-GO 服务商、域名和配置备份。

## 受影响版本

- Server Tool v0.6.9。

已升级到 v0.6.9 且运行 `SCBL` 卡在 DDNS-GO 迁移提示的服务器，应直接使用 `server-tool-stable-latest/install-server.sh` 升级到本版本。
