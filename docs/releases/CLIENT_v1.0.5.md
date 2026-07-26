# [CLIENT] Windows Client v1.0.5

## 修复内容

- 房主识别只采用 dedicated_server 数据库生成的控制面权威会话，不再根据本地流量或唯一通信对象猜测房主。
- 控制面暂时不可用时不再把两人局中的另一名玩家误标为房主。
- 删除 Route Guard 本地房主流量采样、`game-route-status.json` 和 `game-route-history.jsonl` 生成逻辑。
- 删除自定义 `*.255`/有限广播/组播转换和最近玩家单播扇出，现有 `10.66.0.255` 广播原样交给 EasyTier 官方广播中继。
- 保留游戏进程授权、严格虚拟网卡路由、必要的源地址转换和返回流量恢复。
- 修复控制面 `tcpPort=null` 导致的 bootstrap JSON 解析失败。
- EasyTier 配置显式启用官方 underlay device binding。

## 不变范围

- Hooks、游戏文件和账号数据库结构不变。
- EasyTier版本、网络名称、端口和P2P/服务端兜底策略不变。
- 客户端仍在游戏启动前完成版本确认和网络准备。
