# SCBL Linux 服务端

正式用户可在 Linux x86_64 上执行：

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/latest/download/install-server.sh | sudo bash
```

引导脚本会下载并校验完整服务端部署包，然后启动现有 `install_public_server.sh` 菜单。游戏服务端仍从 `caox233/5th-echelon` 的专版 Release 下载；若本机 `dedicated_server` 与当前专版 SHA256 一致，则直接复用，不下载、不停服、不替换。

## 主要端口

- TCP/UDP 11010：EasyTier 公网入口；
- TCP 10443：EasyTier WSS 入口；
- TCP 18080：客户端公网更新和私网兜底更新；
- TCP 19080：私网控制平面；
- TCP 50051：私网账号服务。

第 11 项仍只进入 `/opt/scbl-public/incoming/client`。把完整客户端 ZIP 上传到该目录后，原 `scbl-package-watch.timer` 每 60 秒自动处理和发布。

## 客户端接入配置

安装完成后，将 `/opt/scbl-public/client_launcher_settings.json` 安全地提供给该自建网络的客户端用户。客户端保存路径为 `%LOCALAPPDATA%\SCBL_Public\launcher_settings.json`。该文件包含网络密钥，不应上传到公开仓库或公开聊天。
