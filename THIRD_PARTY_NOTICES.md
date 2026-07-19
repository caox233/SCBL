# 第三方组件说明

## EasyTier

SCBL 将 EasyTier 作为独立进程调用。构建脚本下载官方未修改的 EasyTier 二进制。项目：EasyTier/EasyTier；许可证：LGPL-3.0。许可证副本位于 `THIRD_PARTY_LICENSES/EasyTier-LGPL-3.0.txt`。

## WinDivert

`scbl-process-router` 构建脚本从 WinDivert 官方 Release 下载驱动和 DLL。发布时应保留上游许可证与版权声明，并确认所使用版本的再分发要求。

## 5th Echelon dedicated_server

SCBL 仓库不直接包含该二进制。Linux 安装器从 `caox233/5th-echelon` 的专版 Release 下载。上游仓库当前许可证状态需要在正式公开分发前单独确认；公开可访问不等同于自动授予修改和二进制再分发权。

## 游戏相关文件

游戏本体、Ubisoft 版权资源及从游戏安装目录提取的文件不应提交到公开仓库。当前测试源码内的嵌入式文件必须在公开前逐项确认来源、必要性和分发授权。

## SCBL Hooks 源码与联机存档

`client/hooks-source/` 保存当前 SCBL 客户端使用的 Hooks 源码快照；`client/ScblPublicLauncher/EmbeddedFiles/` 中的 `uplay_r1_loader.dll` 和四个联机存档文件由项目维护者确认用于本项目公开发布。Hooks 源码依赖 `caox233/5th-echelon` 的完整 Rust 工作区才能编译。
