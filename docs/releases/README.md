# 正式发布说明

`main` 只保留 Windows Client 与 Linux Server Tool 当前版本及上一版本的正式发布说明。

- 当前 Client 说明由 `VERSION_CLIENT` 对应的 `CLIENT_vX.Y.Z.md` 提供；
- 当前 Server Tool 说明由 `VERSION_SERVER_TOOL` 对应的 `SERVER_TOOL_vX.Y.Z.md` 提供；
- 更早版本的完整说明保存在 GitHub Releases、对应 Git 标签和根目录 `CHANGELOG.md` 中。

发布工作流会拒绝缺少当前版本说明文件的正式发布。一次性验证结果和工作流状态应保存在 GitHub Actions 日志或 Artifact 中，不再提交到源码树。
