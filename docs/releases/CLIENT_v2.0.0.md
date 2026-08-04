# SCBL Windows Client v2.0.0

## 2.0 本地重构基线

- Windows 客户端升级到 .NET 10，并统一使用 `VERSION_CLIENT` 作为版本来源。
- Hooks 源码合并到 `client/hooks`，客户端正式包从本仓库源码构建，并在 `tools/uplay_r1_loader.dll` 携带独立 bootstrap DLL。
- Route Guard、Updater、EasyTier 与 Launcher 使用统一构建和完整包发布流程。
- 移除旧网络链、废弃差分更新路径和固定测试 Hooks 哈希；测试通道允许反复替换 Hooks，正式通道继续验证完整性。
- 将公告、诊断、游戏进程监控和玩家发现从巨型窗口类拆为独立职责文件，不改变现有界面。
- 设置、日志、网络状态、组件缓存、更新文件与诊断包统一归档到 `temp/计算机名/`，支持 POWERPC 与 WORKPC 安全共用 NAS 客户端目录。
- Hooks 配置正式切换为游戏目录中的标准 `scbl.toml`；不再读取旧 `5th_auth.dat`，游戏日志集中到 `temp/计算机名/logs/game/`。
- 客户端不再读取 0.x AppData/日志配置；密码只接受 DPAPI 密文，首次隧道配置保存后也会转为 DPAPI。
- Route Guard 与 EasyTier 组件包使用整组事务替换和失败回滚，普通更新清单不能把客户端降级。
- 自动轮转游戏日志并限制诊断包、下载包与旧组件缓存数量。
- 新增 .NET 10 单元测试并统一 Windows CI 的 .NET SDK 版本。
- 将使用指引、中英文切换和声音开关收纳到右上角 `⚙` 菜单，并新增服务器地址、隧道端口和更新服务端口设置；输入校验通过后按机器保存，下次启动生效。

## 双机验证

- WORKPC 已验证版本门禁、EasyTier、SCBL 登录、Hooks 部署、Ubisoft 重启 PID 收养和 Route Guard 严格模式。
- 游戏成功进入 Paladin 主场景；退出后 Hooks GameSession、Route Guard 和启动器网络运行时均正常清理。

## 配套版本

- Linux Server Tool：v2.0.0
- Hooks / Route Guard / Dedicated Server / shared API：v2.0.0
