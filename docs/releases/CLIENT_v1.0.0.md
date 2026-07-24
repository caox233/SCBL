# [CLIENT] Windows Client v1.0.0

SCBL Windows 客户端首个正式版本。

## 主要内容

- 客户端版本检查调整为启动后的首要流程。
- 未确认正式版本前，不扫描游戏目录、不启动 EasyTier、不播放启动音乐，也不能启动游戏。
- 发现版本不一致时，用户只能立即更新或退出。
- 无法连接更新服务时，用户只能重新检查或退出。
- 客户端使用服务器发布的完整 ZIP 更新，并在安装前校验文件。
- Launcher、Updater 和发布包版本统一从 `VERSION_CLIENT` 读取。

## 安全边界

- Hooks 源码及 `uplay_r1_loader.dll` 未修改。
- WinDivert、EasyTier 和 Route Guard 的既有安全边界保持不变。
