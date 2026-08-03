# SCBL 单仓库目录边界

SCBL 以运行位置和责任划分源码，而不是按历史仓库划分：

```text
client/
  Launcher、Updater、EasyTier、Route Guard
  hooks/                 游戏进程内的 Uplay/Quazal 兼容层
server/
  dedicated-server/      游戏服务端运行时与存储
  quazal/                服务端 Quazal 协议实现
  scbl_control_plane.py  公网控制平面
  scbl_server_manager.py Linux 部署与运维入口
shared/
  api/                   Hooks 与 dedicated server 共用的 gRPC 协议
scripts/
  构建、打包和安装入口
```

依赖方向固定为：

```text
client/hooks ──> shared/api <── server/dedicated-server
                                  │
                                  └──> server/quazal
```

`client` 和 `server` 不能直接引用对方的实现目录。跨端消息格式进入 `shared`；部署脚本不承载业务逻辑；编译产物、数据库、日志、密钥和本地诊断文件不进入源码目录。

Hooks 与 dedicated server 可以分别构建和部署，但测试候选必须记录同一个 SCBL Commit。这样既能独立替换组件，也不会重新出现两个仓库源码漂移的问题。
