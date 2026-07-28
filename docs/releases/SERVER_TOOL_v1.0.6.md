# Linux Server Tool v1.0.6

- 新增菜单15“一键收集服务端诊断日志”及命令 `scbl-server-diagnostics`。
- 诊断包包含 systemd 状态和日志、监听端口、路由、EasyTier、资源、OOM/coredump、数据库健康与活动房间元数据、文件清单和二进制SHA256。
- 不复制 `5th-echelon.db`，不导出账号和密码；网络密钥、ticket key、crypto/access key和Token统一脱敏。
- 控制平面401响应增加具体原因和服务器时间，并按来源限频记录鉴权拒绝。
- 安装和在线升级会同步安装诊断脚本与命令。

实时房主退出后的对局状态接管仍是已知限制；本版本不宣称解决游戏本身的主机迁移。
