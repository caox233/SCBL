# SCBL Linux Server Tool v2.0.0

## Monorepo 基线

- Dedicated Server、Quazal 和共享 API 源码合并到 SCBL 主仓库。
- 正式二进制与测试候选改由 `caox233/SCBL` 发布，不再依赖维护者的独立 5th-echelon 仓库。
- Control Plane 修复 SQLite 连接泄漏，并保留游戏会话、在线玩家和版本门禁能力。
- Dedicated Server 对缺失身份和不一致会话数据改为安全返回，不再因异常元数据 panic。

## 运维与升级

- 主菜单15诊断、菜单14在线升级和菜单16本地测试包管理继续保留。
- 在线升级继续保留数据库、`scbl.env`、客户端更新数据、DDNS-GO 配置和升级备份。
- Dedicated Server 下载继续执行 SHA256 与 ELF x86_64 校验，并保留部署前备份和失败回滚。

## 配套版本

- Windows Client：v2.0.0
- Server Tool：v2.0.0
- Hooks / Route Guard / Dedicated Server / shared API：v2.0.0
