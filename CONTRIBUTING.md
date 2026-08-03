# 参与贡献

1. 从 `main` 创建短期功能分支，通过 Pull Request 合并，不直接向 `main` 提交；
2. 不提交运行数据、密钥、数据库、日志、诊断包、工作流状态快照和编译产物；
3. Windows Client 版本只由根目录 `VERSION_CLIENT` 管理，Linux Server Tool 版本只由 `VERSION_SERVER_TOOL` 管理；
4. 客户端源码放入 `client/`，服务端源码放入 `server/`，跨端协议放入 `shared/`；Hooks 与 dedicated server 必须由同一个 SCBL Commit 构建；
5. 提交前运行受影响组件的最小测试，并确保 GitHub Actions 的 Validate source 通过；
6. Shell、Python、YAML 使用 UTF-8 与 LF；PowerShell、C# 可使用 CRLF；
7. 正式版本创建新语义版本标签，不覆盖已有 Release；历史发布说明保存在 GitHub Releases、标签与 `CHANGELOG.md`。
