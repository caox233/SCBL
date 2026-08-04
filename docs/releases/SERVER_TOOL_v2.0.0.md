# SCBL Linux Server Tool v2.0.0

## Monorepo 基线

- Dedicated Server、Quazal 和共享 API 源码合并到 SCBL 主仓库。
- 正式二进制与测试候选改由 `caox233/SCBL` 发布，不再依赖维护者的独立 5th-echelon 仓库。
- Control Plane 修复 SQLite 连接泄漏，并保留游戏会话、在线玩家和版本门禁能力。
- Dedicated Server 对缺失身份和不一致会话数据改为安全返回，不再因异常元数据 panic。

## 运维与升级

- 服务端安装、修复和补丁更新统一消费 GitHub Actions 构建的二进制部署包，生产服务器不编译源码。
- 服务端运行时更新继续保留数据库、配置、客户端更新数据、DDNS 配置和升级备份，并在玩家在线时默认阻止重启。
- 客户端发布、组件发布和公告管理与服务端运行时部署分离；单组件支持 GitHub 在线下载、rz 上传和已有路径三种来源。
- GitHub 只作为服务端的上游资产源，客户端只读取服务端同源组件清单。

## 配套版本

- Windows Client：v2.0.0
- Server Tool：v2.0.0
- Hooks / Route Guard / Dedicated Server / shared API：v2.0.0
