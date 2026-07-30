# SCBL Linux Server Tool v1.0.9

## 主要更新

- 服务端组件仓库从 Hooks 单组件扩展为 Hooks、Route Guard、EasyTier 和 Updater 四类客户端组件。
- 组件文件按版本保存为不可变产物，并记录组件名、版本、源提交、文件名、大小、SHA256、最低 Launcher 版本和更新时机。
- 支持发布到 `test`、将同一个已验证二进制提升到 `stable`、回滚到历史不可变版本，以及完整清单和文件校验。
- 禁止使用相同版本号覆盖为不同内容；test 提升 stable 时不重新编译组件。
- 服务端更新服务继续提供正式客户端完整包，同时提供 `stable` / `test` 组件清单和组件文件。
- 服务端管理与客户端构建边界进一步拆分，普通组件修改不再要求重新制作完整 Server Tool 包。

## 保留内容

- 首次安装、离线部署和灾难恢复仍可使用完整 Server Tool 压缩包。
- 一键安装和在线升级继续保留现有配置、数据库、客户端包、DDNS、日志、更新目录和备份。
- 菜单15和 `scbl-server-diagnostics` 一键诊断继续保留，并继续执行诊断脚本校验、备份和失败回滚。
- dedicated server 二进制继续从 `caox233/5th-echelon` 的确定 Release 获取，不在 SCBL 仓库重新编译。

## 安全边界

- 本版本不修改 dedicated server 数据库结构、账号、密码、房间数据或 EasyTier 网络拓扑。
- stable 外置客户端组件在签名清单验证完成前保持只读，不因本次 Server Tool 发布自动启用。
- 组件提升必须复用 test 阶段验证过的同一文件和同一 SHA256。

## 配套版本

- Windows Client: v1.0.14
