# SCBL Linux Server Tool v1.0.10

## 主要更新

- 主菜单第16项由“邀请 / 组队测试版本管理”升级为通用“测试管理”。
- 服务端可直接读取 `caox233/5th-echelon` GitHub Releases 中的测试候选，不再要求通过 Xshell/SFTP 手工上传测试 ZIP。
- 支持查看候选列表、按编号选择并自动下载部署，以及直接下载并部署最新候选。
- 新增首选命令 `scbl-test-manager`；旧命令 `scbl-invite-test` 继续保留兼容。

## 下载与部署校验

- 测试 Release 必须同时提供符合命名规则的 ZIP 和同名 `.sha256` 文件。
- 只允许受信任的 GitHub HTTPS 地址及重定向，并限制 API 响应、校验文件、压缩包和解压文件大小。
- 下载先写入临时目录，外层 ZIP SHA256 校验正确后才原子写入本地缓存。
- 部署前继续校验内部 `CHECKSUMS.sha256`、Hooks 与 dedicated server 来源提交、组件元数据、文件大小和 SHA256。
- 部署仍要求输入 `DEPLOY-TEST`，并自动备份 dedicated server、数据库和 test 组件清单；任何步骤失败都会自动恢复。

## 发布流程修复

- 修复 Server Tool Release 打包阶段在 `set -o pipefail` 下通过管道检查压缩包成员时可能产生 broken pipe、导致已经通过测试的发布任务失败的问题。
- 压缩包成员列表现在只生成一次，再逐项验证必需文件。
- Pull Request 只执行完整构建与校验，不创建正式 Release；正式发布仍只在版本提交合入 `main` 后进行。

## 保留能力与安全边界

- 菜单15和 `scbl-server-diagnostics` 一键诊断继续保留。
- 在线升级继续保留现有服务端配置、数据库、客户端更新数据、DDNS-GO 配置和升级备份。
- 本版本不修改 dedicated server 数据库结构、账号、密码或房间数据。
- 不修改 EasyTier、Route Guard、防火墙或控制平面配置。
- 测试候选只写入 `test` 组件通道，不修改 `stable` 正式通道。

## 配套版本

- Windows Client: v1.0.14
