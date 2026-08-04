# SCBL 2.0 服务端管理器

这是全新的 Linux 服务端管理层，不读取旧版 `scbl.env`，也不兼容
`/opt/scbl-public` 部署。游戏 Dedicated、控制平面和客户端更新服务仍使用现有、
已经验证过的运行时代码；本目录只负责安装、配置、诊断、更新和回滚。

配置固定使用 `/etc/scbl/server.toml`，运行数据使用 `/var/lib/scbl`，版本发布目录
使用 `/opt/scbl/releases`。配置文件中的网络密钥由 `SCBL init` 随机生成。

本地运行：

```bash
PYTHONPATH=server/manager python3 -m scblctl --help
python3 server/manager/build.py --output dist/scblctl.pyz
python3 dist/scblctl.pyz --help
```

当前管理器提供：新配置生成和校验、配置影响范围、服务状态、只读诊断、单组件重启、
脚本内 DDNS、服务端安装/修复/更新、客户端完整 ZIP 与不可变组件发布、三类客户端
公告、SQLite 在线一致性备份和旧备份清理。发布文件先校验再原子替换，服务端部署失败
会回滚；会重启运行时的操作在检测到在线玩家时默认禁止。

全新 Linux 主机可运行 `server/bootstrap/install.sh`。安装器只询问公网入口、更新
通道和是否启用 DDNS；它不读取任何旧版配置。运行时包必须带
`server-package.json`，清单中的每个文件都在写入 `/opt/scbl/releases` 前校验。

## Windows 构建、Linux 只运行

正式运行时包在 Windows 开发机执行以下命令生成：

```powershell
powershell -ExecutionPolicy Bypass -File server/packaging/build-runtime.ps1
```

脚本使用 Windows 内的 WSL2 编译 Linux x86_64 Dedicated Server，并把官方
EasyTier Linux 二进制、控制平面和更新服务一起封装。Linux 服务端只接收最终
`SCBL-Server-Runtime-vX.Y.Z-linux-x86_64.tar.gz`，不安装 Cargo/Rust，也不接收源码。

## 服务端部署与客户端发布

默认更新仓库明确固定为 `caox233/SCBL`，复制或 Fork 源码不会自动改变更新源。
高级用户主动修改 `updates.repository` 后才会使用其他仓库。

管理入口仍是同一个 `SCBL`，但生命周期明确分开：服务端部署包只包含管理器与 Linux
运行时；客户端发布单独管理正式完整包、组件和公告。两者不共享版本号、事务或回滚，
客户端公告调整不会触发任何游戏服务重启。

服务端菜单显示“首次安装 / 修复 / 更新”：安装和修复使用完整安装包，更新使用补丁包，
这些是后台校验规则，不在交互菜单中暴露实现术语。三项均支持 GitHub、Xshell `rz` 和
服务器现有路径。正式文件名为 `SCBL-Server-Full.scblfull` 与
`SCBL-Server-Patch.scblpatch`。完整安装包必须包含 `server.manager` 和
`server.runtime`，补丁包可只包含其中一个。

客户端完整包从 GitHub 下载时会先读取 `VERSION_CLIENT` 和 Release 的 SHA256 文件，
再校验 ZIP 内逐文件清单。服务端只公布一个当前正式客户端版本，不提供最低版本设置；
普通客户端必须与它完全一致。测试服务器可单独开启 `testing.allow_newer_clients`，并且
只有用 `--test` 启动的较高版本客户端才会获得例外，生产服务器默认关闭。

组件可从固定 GitHub 仓库按单个文件下载，也可通过 rz 或服务端路径手动上传；三种来源
最终都写入服务端自己的同源清单，客户端不会直连 GitHub。组件也可按“测试发布 ->
同一 SHA256 提升到正式通道”管理，版本目录不可变；Hooks 可在
开始游戏前生效，Route Guard、EasyTier 和 Updater 在下次启动时应用。滚动公告、启动
公告和更新公告均由客户端发布菜单原子写入；更新公告只会嵌入与其版本相同的客户端清单。

## 修复与备份

`SCBL repair` 会先复核当前运行时的 `server-package.json` 和所有文件摘要，然后重新
生成托管配置、systemd 服务和 UFW 规则并执行健康检查，不会重新编译服务端。

`SCBL backup create` 使用 SQLite Backup API 在线复制玩家数据库，并备份 `/etc/scbl`、
Dedicated ticket、平衡文件、客户端更新清单和 DDNS 配置。默认不重复打包体积较大的
客户端发布 ZIP；需要完整离线副本时使用 `--include-client-packages`。备份和 SHA256
文件均为 `0600`，保存在 `/var/backups/scbl`。

## IPv6 DDNS

运行 `SCBL` 后进入“IPv6 动态域名”，即可在终端内完成 DDNS-Go 的安装、阿里云
凭据录入、启停、立即更新、状态核对和日志查看，不需要打开 DDNS-Go Web 页面。
程序从官方 GitHub Release 下载并校验官方 `checksums.txt`；AccessKey Secret 仅写入
权限为 `0600` 的 DDNS-Go 配置文件，不写入 `server.toml` 或状态输出。

默认只更新 AAAA：IPv6 从默认路由网卡读取，并过滤 ULA、链路本地和临时地址。
菜单可明确选择同时更新 A 记录；该选项默认关闭，启用后 IPv4 从公网查询接口获取，
不会把 NAT 后的局域网 IPv4 写入 DNS。DDNS-Go 使用 `-noweb` 运行，不监听管理端口。
