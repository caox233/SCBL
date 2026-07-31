# SCBL Linux Server Tool v1.0.12

## 本地测试包流程

- 主菜单 `16. 测试管理` 不再依赖 GitHub 测试 Release。
- `16-1` 改为通过 Xshell/ZMODEM 从当前 Windows 电脑选择并上传本地测试 ZIP。
- 上传文件先放入隔离临时目录，执行完整 dry-run 校验后才进入 `/opt/scbl-public/incoming/invite-test`。
- 仅接受符合 `SCBL-(Invite-Party-)?Test-<版本>.zip` 命名规则、大小不超过 256 MiB 的文件。
- 同名文件已存在且 SHA256 不同会被拒绝，避免用相同版本号覆盖不同二进制。
- `16-2` 从已上传 ZIP 列表中选择并部署；`16-3` 部署最新上传的 ZIP。

## 日志回传

- `16-6` 继续收集最近一小时服务端诊断包。
- 诊断完成后可立即通过 Xshell/ZMODEM 将压缩包发送到当前电脑。
- Linux 服务端无法强制决定 Windows 保存目录；把 Xshell 的“ZMODEM 接收目录”设置为桌面，即可自动保存到当前电脑桌面。
- 即使本地接收取消或失败，诊断包仍保留在服务器测试日志目录。

## 保留能力

- 主菜单15和 `scbl-server-diagnostics` 一键诊断继续保留。
- 主菜单14在线升级继续保留现有服务端配置、数据库、客户端更新数据、DDNS-GO 配置和升级备份。
- `scbl-test-manager` 与兼容命令 `scbl-invite-test` 继续保留，命令行仍可直接校验、部署、查看状态和回滚本地测试包。

## 校验与安全

- 上传后的测试 ZIP 继续校验内部 `CHECKSUMS.sha256`、Hooks 与 dedicated server 来源 Commit、组件元数据、文件大小及 SHA256。
- 部署前仍要求输入 `DEPLOY-TEST`。
- dedicated server、数据库和 test 组件清单继续在部署前自动备份，失败时自动恢复。
- 不修改 EasyTier、Route Guard、防火墙、控制平面或 stable 正式组件通道。

## 配套版本

- Windows Client：v1.0.14
- Server Tool：v1.0.12
