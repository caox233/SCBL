把编译好的公网专版 uplay_r1_loader.dll 放到这个目录。

启动器发布后目录应为：
publish-single\EmbeddedFiles\uplay_r1_loader.dll

DLL 要求：
1. 不再写死 Radmin 26.*。
2. 读取游戏目录 5th_auth.dat 的 BindIP。
3. BindIP 支持 10.66.0.x。
4. bind / connect / WSAConnect / sendto 前强制绑定 BindIP。
5. GetAdaptersInfo / gethostbyname 优先返回公网虚拟网信息。
