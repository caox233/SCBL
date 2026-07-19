请把旧项目中的以下文件复制到这个目录：

1. uplay_r1_loader.dll
2. 00000001.meta
3. 00000001.sav
4. 00000002.meta
5. 00000002.sav

注意：
- 这里的 uplay_r1_loader.dll 必须是你修改过的国内服务器专用 HOOKS DLL。
- 启动器每次启动游戏前都会把这个 DLL 覆盖到游戏目录。
- 启动器不会读取或写入原启动器的 uplay.toml。
- 启动器会写入游戏目录下的 5th_auth.dat 给你的 HOOKS DLL 使用。
