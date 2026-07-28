# Windows Client v1.0.10

- 控制平面签名跟随当前 EasyTier 运行配置中的实际网络密钥，修复个别客户端虚拟网络正常但心跳、玩家列表和房间状态持续401的问题。
- 控制平面返回结构化鉴权原因；时钟偏差时客户端自动校正并重试一次。
- 诊断包增加启动器EXE SHA256，便于识别旧目录或混合安装。
- 修复 EasyTier node info/config JSON 中内嵌 TOML 网络密钥未脱敏的问题。

本版本不修改 EasyTier、Route Guard、WinDivert、房间容量策略或实时房主迁移。
