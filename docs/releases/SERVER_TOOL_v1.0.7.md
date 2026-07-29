# Server Tool v1.0.7

- 修复在线升级只安装管理器、控制平面和更新服务，却遗漏 `scbl_server_diagnostics.sh` 的问题。
- 诊断脚本现在属于升级必需文件：下载后执行 Shell 语法检查，同时安装到管理目录和 `/usr/local/bin/scbl-server-diagnostics`。
- 升级前备份现有诊断脚本和命令；任一步骤失败时恢复原文件，不修改账号数据库、`scbl.env`、客户端包、DDNS 配置或历史日志。
- 当前版本再次执行在线升级检查时会修复管理命令链接，菜单15可直接收集脱敏诊断包。
- 控制平面区分“本人房间状态”和“全局多人拓扑”：单人创建者可识别为房主，但不会把单人会话广播为多人房间。

dedicated_server 的 PRUDP 分片修复由 `caox233/5th-echelon` 的 `scbl-public-stable-latest` 独立构建发布；本包不携带数据库或 Hooks DLL。
