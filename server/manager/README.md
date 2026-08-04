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

当前第一阶段已经提供：新配置生成和校验、配置影响范围、服务状态、只读诊断、
单组件重启、统一菜单、脚本内 IPv6 DDNS，以及本地运行时包 SHA256 校验、原子发布
与安装失败回滚。

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

## 统一更新源

默认更新仓库明确固定为 `caox233/SCBL`，复制或 Fork 源码不会自动改变更新源。
高级用户主动修改 `updates.repository` 后才会使用其他仓库。

更新菜单不再区分“客户端升级”和“服务端升级”。一次在线检查读取同一个已签名
清单，同时规划 `client.*` 与 `server.*` 组件；一个本地 `.scblpatch` 也可以同时
携带两类组件。执行前必须完整显示计划，执行后共同写入更新历史并支持回滚。

## IPv6 DDNS

运行 `SCBL` 后进入“IPv6 动态域名”，即可在终端内完成 DDNS-Go 的安装、阿里云
凭据录入、启停、立即更新、状态核对和日志查看，不需要打开 DDNS-Go Web 页面。
程序从官方 GitHub Release 下载并校验官方 `checksums.txt`；AccessKey Secret 仅写入
权限为 `0600` 的 DDNS-Go 配置文件，不写入 `server.toml` 或状态输出。

默认只更新 AAAA：IPv6 从默认路由网卡读取，并过滤 ULA、链路本地和临时地址。
菜单可明确选择同时更新 A 记录；该选项默认关闭，启用后 IPv4 从公网查询接口获取，
不会把 NAT 后的局域网 IPv4 写入 DNS。DDNS-Go 使用 `-noweb` 运行，不监听管理端口。
