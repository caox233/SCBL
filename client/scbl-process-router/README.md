# SCBL Route Guard v0.5.20

本组件只为当前 SCBL Public 启动器会话明确授权的游戏 PID 提供严格导流。

## v0.5.20 心跳可靠性

- 读取 `route-guard-session.json` 时，对 Windows 文件共享冲突、短暂文件不存在和临时 JSON 解析失败快速重试 5 次。
- 保存最后一次通过身份、PID、时间戳和进程存活校验的有效心跳。
- 单次读写竞争不会退出；只有连续超过既有 2.5 秒超时仍无有效心跳才 fail-open。
- session ID 变化、launcher PID 不符、启动器进程死亡或无存活授权 PID 仍立即退出，不降低会话隔离。
- 常规统计输出到 stdout，避免被启动器误标为 ERROR；摘要间隔由 15 秒降噪为 30 秒。

## v0.5.20 房主判定边界

流量检测模式改名为：

```text
game-process-udp-13000-fallback-v4-strict-route
```

它仅作为公共控制面或游戏会话数据库暂不可用时的降级依据。权威房主由游戏服务端的活动会话 `creator_id` 决定，Route Guard 不修改游戏房主。

`game-route-history.jsonl` 只在角色、候选房主或活动玩家数变化时立即记录；无变化时每 10 秒保底记录，达到 1 MiB 后轮转。

## 严格导流规则

- `10.66.0.0/24` 单播强制使用当前 EasyTier IPv4 和接口。
- 游戏广播保留官方 EasyTier 广播，同时只向近期真实 UDP/13000 玩家进行有限单播补发。
- 其他远程 IPv4 TCP/UDP 阻断并记录，防止原始游戏流量绕过 EasyTier。
- 入站游戏流量必须来自 `10.66.0.0/24` 并到达 EasyTier 接口。

## 构建

```powershell
cd client\scbl-process-router
powershell -ExecutionPolicy Bypass -File .\build_windows.ps1
```
