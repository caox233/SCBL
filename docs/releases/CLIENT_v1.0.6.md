# [CLIENT] Windows Client v1.0.6

## 修复内容

- 修复客户端完整包更新时 `tools/WinDivert64.sys` 仍被Windows内核驱动占用，更新中途失败且不会自动重启启动器的问题。
- 更新前由Launcher主动停止Route Guard和EasyTier；Updater再次清理运行时并停止指向当前客户端目录的WinDivert驱动服务。
- 正式ZIP使用 `tools/WinDivert64.payload.sys` 作为更新载荷，不再直接覆盖锁定路径；首次启动或更新后启动时，Launcher会校验SHA256并原子生成 `tools/WinDivert64.sys`。
- 该载荷设计兼容v1.0.5旧Updater，使本次自动更新不会再次卡在同一个驱动文件。
- 更新器跳过内容相同的文件，对文件占用执行最多约8秒重试，并使用同目录临时文件完成替换。
- 完整包复制失败时自动恢复更新前的Launcher和tools备份，并重新打开原启动器。
- SCBL.Updater增加独立的管理员权限清单；Launcher启动Updater和Updater重启Launcher时均显式使用管理员权限。
- 重启助手会确认新版Launcher至少持续运行3秒，过早退出时继续重试并记录退出码。

## 不变范围

- Server Tool继续保持v1.0.4。
- EasyTier版本、网络名称、端口、P2P策略、Hooks、游戏文件和服务端数据库均未修改。
