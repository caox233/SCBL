这个目录只保存嵌入 Launcher 的初始存档资源：

1. 00000001.meta
2. 00000001.sav
3. 00000002.meta
4. 00000002.sav

注意：
- uplay_r1_loader.dll 不嵌入 Launcher；完整客户端把它放在 tools 目录。
- 启动器每次启动游戏前都会把选定并校验后的 DLL 部署到游戏目录。
- 启动器不会读取或写入原启动器的 uplay.toml。
- 启动器会写入游戏目录下的 scbl.toml 给 Hooks DLL 使用。
