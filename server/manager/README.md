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
单组件重启、统一菜单，以及本地运行时包 SHA256 校验、原子发布与安装失败回滚。

全新 Linux 主机可运行 `server/bootstrap/install.sh`。安装器只询问公网入口、更新
通道和是否启用 DDNS；它不读取任何旧版配置。运行时包必须带
`server-package.json`，清单中的每个文件都在写入 `/opt/scbl/releases` 前校验。
