# 更新记录

## v0.6.0

- 公开当前客户端使用的 Hooks Rust 源码快照；
- 使用项目维护者提供的新存档文件替换旧嵌入存档，并固定 SHA256 清单；

- 建立 GitHub 公开仓库结构，保留 `client/`、`server/` 主目录；
- Windows 客户端支持 GitHub Actions 自动编译和 Release 完整包；
- 仅提供客户端本地编译脚本，普通用户从 Release 下载预编译 ZIP；
- 新增 Linux 服务端 Release 一键部署入口；
- 服务端继续下载专版 `dedicated_server`，本地哈希一致时复用；
- 客户端更新检查提前到 EasyTier 启动前；
- 公网更新通过 TCP/18080，失败后通过原私网地址兜底；
- 保留现有更新公告、manifest、差异更新、Updater、回滚和服务端全量包发布流程；
- 第 11 项继续进入 `/opt/scbl-public/incoming/client`；
- 新增源码验证、客户端构建、正式 Release 和 GitHub Pages 工作流。
