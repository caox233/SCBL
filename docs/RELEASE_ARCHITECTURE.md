# SCBL 发布结构

## 版本来源

| 组件 | 唯一版本文件 | Release 标签 | Release 标题 |
|---|---|---|---|
| Windows 客户端 | `VERSION_CLIENT` | `client-vX.Y.Z` | `[CLIENT] Windows Client vX.Y.Z` |
| Linux 服务端工具 | `VERSION_SERVER_TOOL` | `server-tool-vX.Y.Z` | `[SERVER] Server Tool vX.Y.Z` |
| 5th dedicated server 与 Hooks | 5th 项目维护 | 5th 项目标签 | 5th 项目维护 |

客户端和服务端不再创建 `stable-latest` Release。安装器和管理工具读取主分支版本文件，再下载对应的正式完整包和 SHA256。

## 客户端启动顺序

1. 启动 Updater 自检。
2. 通过公网更新端口读取服务器客户端更新信息。
3. 版本一致：继续启动。
4. 版本不一致：立即更新或退出。
5. 无法检查：重新检查或退出。
6. 版本确认完成后，才扫描游戏目录、播放音乐、清理旧进程和启动 EasyTier。

## 服务端客户端包同步

首次部署和 Server Tool 升级完成后，服务端读取 `VERSION_CLIENT`，下载 `client-vX.Y.Z` 的完整 ZIP 和 SHA256。服务器更新信息只包含当前版本、完整包路径、完整包 SHA256、更新摘要和公告。

## 保留策略

每个组件保留最新两个正式 Release。版本包不会被同名覆盖；同版本重新运行发布流程只在发布工作流本身发生变化或手动执行时更新资产。

## 数据边界

Server Tool 更新不得覆盖 `scbl.env`、`server/5th-echelon.db`、客户端更新目录、备份目录或 DDNS-GO 配置。Hooks 源码和 DLL 不在 SCBL 仓库内修改或重新构建。
