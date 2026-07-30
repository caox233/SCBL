# SCBL Linux Server Tool v1.0.11

## 修复内容

- 修复“16. 测试管理”从 GitHub 下载测试候选后，在 `/tmp` 与 `/opt` 位于不同文件系统时出现 `Invalid cross-device link`、无法写入本地缓存的问题。
- `scbl-test-manager` 与兼容命令 `scbl-invite-test` 现在使用 `/opt/scbl-public/incoming/invite-test/.tmp` 作为下载临时目录。
- 下载临时文件与最终缓存保持在同一文件系统，完成外层 SHA256 校验后仍通过原子替换写入正式缓存。
- 临时目录权限固定为 `0700`，候选 ZIP、同名 `.sha256`、内部 `CHECKSUMS.sha256`、组件来源提交、大小和 SHA256 校验保持不变。

## 影响范围

- 仅修复 Server Tool 测试候选下载缓存路径。
- 不修改 Hooks DLL、dedicated server、数据库、账号、房间数据或客户端正式版本。
- 不修改 EasyTier、Route Guard、防火墙、控制平面和 stable 正式组件通道。
- 已发生该错误时，候选尚未进入部署阶段，不需要执行回滚；升级后重新选择同一测试候选即可。

## 保留能力

- 菜单15和 `scbl-server-diagnostics` 一键诊断继续保留。
- 菜单16的 GitHub 候选查看、选择下载、完整校验、部署、状态、回滚和日志收集继续保留。
- 在线升级继续保留现有服务端配置、数据库、客户端更新数据、DDNS-GO 配置和升级备份。

## 配套版本

- Windows Client: v1.0.14
