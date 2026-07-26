# [CLIENT] Windows Client v1.0.3

## 修复内容

- 修复客户端更新完成后偶尔没有自动重新打开启动器的问题。
- 更新完成后不再由仍在运行的主更新进程直接启动客户端，而是创建独立重启等待阶段。
- 重启等待阶段会先等待原更新进程退出，再从客户端根目录启动 `SplinterCellCNLauncher.exe`。
- 自动重启最多尝试20次，每次间隔500毫秒，并把调度、等待和启动结果写入 `logs/updater.log`。
- Launcher 传给 Updater 的重启路径优先固定为安装目录中的正式客户端文件，不再依赖当前进程路径。

## 版本边界

- Server Tool 继续保持 v1.0.3。
- EasyTier 网络策略、端口、Route Guard 和 Hooks 均未修改。
