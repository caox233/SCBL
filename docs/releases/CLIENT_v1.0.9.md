# [CLIENT] Windows Client v1.0.9

本版本修复状态栏路线显示频繁跳动，以及诊断包继续携带旧版本房间路线历史的问题。

## 主要修复

- 首次获得服务端路线时立即显示。
- 后续协议或地址族变化必须连续三次采样一致，约30秒稳定后才更新界面。
- 延迟和真实 EasyTier 选路继续正常采集；只对界面文本做防抖，不强制 IPv4、IPv6、UDP 或 WSS。
- 单次路线查询为空时保留最后一次确认结果，不再出现短暂空白。
- 启动器启动及导出诊断时删除旧版遗留的 `game-route-status.json`、`game-route-history.jsonl` 和 `.1` 轮转文件。
- 诊断包不再包含已停用的本地房主流量推断历史；当前游戏质量、Route Guard健康状态和EasyTier实时信息仍保留。

## 保持不变

- EasyTier仍自行选择真实传输路线，UDP与WSS备用连接均保留。
- Server Tool保持v1.0.4。
- 不修改Hooks、WinDivert、游戏服务端、数据库结构或端口。
- 不修改游戏自身的PRUDP登录和房主迁移逻辑。

## 升级

旧客户端通过现有完整包更新流程升级到v1.0.9。正式ZIP与SHA256由GitHub Actions构建发布。
