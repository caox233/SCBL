# [SERVER] Server Tool v1.0.0

SCBL Linux 服务端管理工具首个正式版本。

## 主要内容

- 删除安装时手工填写的最低客户端版本。
- 控制平面自动读取服务器当前发布的客户端版本，并要求客户端精确匹配。
- 首次部署没有客户端包时，自动下载 GitHub 正式客户端完整包。
- 重新部署或升级时自动比较版本和 SHA256；相同版本不重复下载，现有高版本不自动降级。
- Server Tool 在线升级直接下载对应版本完整包，不再经过 Stable Release。
- Server Tool 版本从随包携带的 `VERSION_SERVER_TOOL` 自动读取。
- 用户提示改为正常操作语言，不再显示不必要的实现术语。

## 数据保护

- 不修改、不覆盖 `server/5th-echelon.db`。
- 保留 `scbl.env`、客户端更新数据、备份目录和 DDNS-GO 配置。
- Hooks 源码及 DLL 未修改。
