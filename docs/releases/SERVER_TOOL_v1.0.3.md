# [SERVER] Server Tool v1.0.3

## 修复内容

- 修复 Server Tool v1.0.2 完整包遗漏 `scbl_update_server.py`，导致部署在“生成 EasyTier、SCBL控制平面与systemd配置”阶段中止的问题。
- 一键安装器现在会检查并持久保存双栈更新服务脚本，临时解压目录清理后仍可正常执行 `SCBL`。
- 在线升级会同步、备份并在失败时恢复 `scbl_update_server.py`。
- 发布工作流在生成 Release 前强制检查安装包内包含该文件，并执行 Python 编译和归档内容校验。

## 版本边界

- Windows Client 继续保持 v1.0.2，无需重新发布客户端。
- EasyTier 网络策略、端口和 Route Guard 不变。
- 不修改 Hooks 源码或 `uplay_r1_loader.dll`。
- 不修改、不覆盖 `server/5th-echelon.db`、现有配置、客户端更新数据、备份和 DDNS-GO 配置。
