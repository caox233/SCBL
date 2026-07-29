# 更新记录

## Windows Client v1.0.11 / Server Tool v1.0.7

- 修复 dedicated_server PRUDP 分片顺序判定错误导致游戏首次进入“线上模式”可能长期停在“连线中”的配套发布；专用服务端二进制继续由 `caox233/5th-echelon` 独立滚动 Release 提供。
- 控制平面允许单人房间创建者通过 `/v1/game-session` 获取本机房主状态；全局玩家拓扑仍要求至少两名参与者和两个虚拟 IP，不恢复客户端本地猜房主。
- 客户端控制平面请求明确使用 `Connection: close`，与服务端响应语义一致，避免复用已关闭连接后的超时重试。
- 网络已通过静默只读快检时，启动游戏前不再重复执行 PowerShell 路由绑定；失败时仍保留现有修复路径，游戏运行期间保护边界不变。
- 修复 Server Tool 在线升级遗漏 `scbl_server_diagnostics.sh`，菜单15和 `scbl-server-diagnostics` 会被校验、备份、安装并在失败时回滚。

## Windows Client v1.0.10 / Server Tool v1.0.6

- 控制平面签名改为读取当前 EasyTier 运行配置中的实际网络密钥，修复部分客户端已进入虚拟网但 `/v1/heartbeat`、`/v1/peers`、`/v1/game-session` 持续 HTTP 401 的问题。
- 控制平面401响应增加 `invalid_signature`、`clock_skew` 等原因及服务器时间；客户端可自动校正时钟偏差并重试，服务端按来源和原因限频记录拒绝日志。
- 客户端诊断包补充启动器可执行文件 SHA256，并修复 EasyTier JSON 内嵌 TOML 导致 `network_secret` 未脱敏的问题。
- Server Tool 新增 `scbl-server-diagnostics` 和菜单15，一键收集服务、网络、数据库元数据、崩溃记录和二进制哈希；不复制账号数据库并统一脱敏。
- 7人联机已验证创建、搜索和加入正常；实时房主退出后所有人回大厅仍是已知限制，未宣称完成对局状态接管。

## Linux Server Tool v1.0.5

- 控制平面HTTP响应改为每次请求后主动关闭连接，避免15秒玩家列表轮询与8秒服务端空闲超时反复创建等待线程、刷出大量 `Request timed out` 日志并抬高长期内存占用。
- 增加控制平面内部签名健康自检：每15秒完整访问一次 `/v1/health`；连续两次无法完成TCP、鉴权和HTTP响应时，以失败状态退出，由systemd自动拉起。
- 控制平面systemd策略改为 `Restart=always`，增加任务数和内存安全上限；启动、重启和配置保存时等待 `10.66.0.1:19080` 真正监听后再报告结果。
- 修复 `scbl-server-status` 把 `0.0.0.0:50051` 误报为账号服务未监听；控制平面状态增加最多2秒的启动就绪等待。
- 修复服务端工具在线升级只更新备用控制平面文件、未同步正在运行的 `/opt/scbl-public/control-plane/scbl_control_plane.py` 的问题；v1.0.5首次启动会自动迁移并重启实际控制平面。

## Windows Client v1.0.9

- 服务端路线显示增加三次连续采样确认：首次结果立即显示，后续 UDP/WSS 或 IPv4/IPv6 变化需连续稳定约30秒才更新界面；实际 EasyTier 选路与备用连接不受影响。
- 路线候选变化仍写入日志，但不会让状态栏每个采样周期闪动；单次查询失败也不会清空当前已确认路线。
- 启动和导出诊断包时清理旧版本遗留的 `game-route-status.json`、`game-route-history.jsonl` 与轮转文件；这些本地流量推断记录自 v1.0.5 起已停用，不再混入当前会话诊断。
- 诊断摘要明确标记旧房间路线历史未包含，避免把数日前的多人测试记录误判为本次会话。

## Windows Client v1.0.8

- 修复 v1.0.7 服务端路径查询把 `10.66.0.1` 误判为非客户端地址，导致 IPv4/IPv6 与 UDP/WSS 实际链路信息长期为空的问题。
- 启动器初始化时把 Updater 与 WinDivert 文件哈希、替换检查移出 WPF UI 线程，并显示“正在准备启动器”，改善开机后首次启动的短暂无响应。
- 游戏运行期间若服务器快速检测瞬时失败，先进行一次只读重试；仍失败时仅重新绑定现有 EasyTier 路由并复检，不重启 EasyTier、不重建虚拟网卡，降低进入线上模式时短暂路径抖动留下半失效会话的概率。
- 增加启动准备耗时和游戏内轻量自愈日志，方便后续区分本机冷启动、Windows 路由与服务端/运营商瞬时波动。

## Windows Client v1.0.7

- 控制平面心跳、在线列表、房间查询和启动信息使用独立连接池；超时或瞬时网络错误会重建对应连接并自动重试一次，不再由一个半失效长连接同时拖慢全部接口。
- 心跳改为固定5秒节拍，不再把单次请求耗时叠加到下一次心跳；短暂的 EasyTier 路径切换不会轻易触发20秒在线TTL误下线。
- 在线玩家列表合并服务器心跳注册表、EasyTier 当前路由和玩家探测结果；注册表暂时漏报时仍可从虚拟网络发现玩家。
- 修复 EasyTier 2.6.x verbose JSON 中数值型 `ipv4_addr` 无法解析，导致明明存在路由却退化为扫描整个 `/24` 的问题。
- 不再把 peer 表中的“可用协议集合”误判为当前 WSS 路径；增加 verbose 路径查询时间，避免状态栏长期错误显示 TCP/WSS 兜底。
- Route Guard 在游戏正常结束时不再先记录一条“意外退出”错误，减少诊断日志误报；严格导流行为不变。

## Windows Client v1.0.6

- 修复更新时 `tools/WinDivert64.sys` 被内核驱动占用，导致完整包复制中断且无法自动重启的问题。
- 启动更新程序前先完整关闭 Route Guard 与 EasyTier；Updater 也会停止属于当前客户端目录的 WinDivert 驱动服务并等待文件句柄释放。
- Release 不再直接携带易被锁定的 `tools/WinDivert64.sys`，改为携带 `WinDivert64.payload.sys`，新版启动器在网络初始化前完成校验和原子安装；v1.0.5 的旧更新器也可以顺利升级到本版本。
- 更新器跳过哈希完全一致的文件，对锁定文件执行重试和临时文件原子替换，并在复制失败时恢复启动器/工具备份。
- 更新器增加独立管理员权限清单；更新失败会重新打开原启动器，不再让用户停留在无窗口状态。
- 自动重启不再只判断 `Process.Start` 成功，而会确认新版启动器至少持续运行3秒。

## Windows Client v1.0.5

- 删除基于本地游戏流量和唯一通信对象猜测房主的回退逻辑，房主状态只采用服务端权威会话信息。
- 删除 Route Guard 的房主流量采样、状态文件和历史文件代码。
- 删除对任意 `x.x.x.255`、`255.255.255.255` 和组播地址的自定义转换，以及按最近玩家复制广播为多份单播的逻辑。
- `10.66.0.255` 数据包保持原样交给 EasyTier 官方 UDP 广播中继处理；严格进程路由和源地址恢复继续保留。
- 控制面客户端允许 `tcpPort=null`，修复关闭裸 TCP 入口后 bootstrap JSON 解析失败的问题。
- EasyTier 配置显式启用官方 `bind_device=true`，并刷新运行配置标记。

## Server Tool v1.0.4

- 与客户端 v1.0.5 同步控制面兼容修复和版本元数据。
- dedicated_server 二进制继续由 `caox233/5th-echelon` 的独立滚动 Release 提供；本版本不修改数据库结构和 Hooks。

## Windows Client v1.0.4

- 客户端版本检查在显示失败弹窗前自动执行最多3次，每次都创建新的 HTTP 连接。
- 单次版本检查使用4秒连接超时和7秒总超时，并对超时、网络错误、HTTP 408/429/5xx执行短退避重试。
- 日志新增尝试次数、耗时、错误类型和重试恢复记录，便于定位间歇性双栈或链路故障。

## Windows Client v1.0.3

- 修复客户端更新完成后偶尔没有自动重新打开启动器的问题。
- 更新器改为先启动独立重启等待阶段，等待原更新进程完全退出后再启动新版客户端。
- 重启路径固定优先使用客户端根目录的 `SplinterCellCNLauncher.exe`，并增加20次重试及详细日志。

## Server Tool v1.0.3

- 修复 v1.0.2 服务端完整包遗漏 `scbl_update_server.py`，导致首次部署生成双栈更新服务时中止的问题。
- 一键安装和在线升级现在会校验、保存、备份并恢复双栈更新服务脚本。
- 发布流程增加安装包必需文件和归档内容检查，禁止再次发布残缺 Server Tool 包。

## v1.0.2

- 客户端连接固定服务器时删除裸 TCP 入口，使用 UDP 主入口和 WSS 兜底；玩家间仍保留 UDP 与 TCP 打洞。
- WSS 默认复用 11010/TCP，EasyTier UDP 使用 11010/UDP，减少家庭宽带需要开放的端口号。
- 客户端更新服务改为 IPv4/IPv6 双栈监听，提升监听队列并关闭目录浏览。
- 控制平面监听队列提升到 128，增加连接超时保护并降低游戏会话数据库刷新频率。
- 配合专用 dedicated_server 增加只读会话容量诊断日志，不改变房间人数规则或数据库结构。

## v1.0.1

- 玩家之间继续优先使用 P2P 直连，普通客户端不再承担第三方游戏流量中继。
- 固定服务器保留为唯一中继兜底，并提示客户端优先建立到服务器的直连路径。
- 关闭延迟优先路由，避免为了很小的延迟差异切换到不稳定的多跳路径。
- 房主延迟状态简化为“与房主连接 XXms 延时”，详细路径、抖动和丢包仍保留在诊断日志中。

## v1.0.0 正式版

- 客户端版本确认成为启动器首要流程，版本不一致时必须更新或退出。
- 客户端和服务端统一使用完整包更新，版本由发布文件自动识别。
- 删除客户端和服务端的 Stable Release，正式版本使用清晰的 `[CLIENT]`、`[SERVER]` 标题。
- 删除人工最低客户端版本配置，控制平面自动要求服务器当前发布的客户端版本。
- 首次部署和 Server Tool 升级后自动检查并同步正式客户端。

## Server Tool v0.6.10

- 修复 v0.6.9 DDNS-GO 原生化迁移中 `write_ddns_go_service` 无限递归导致 `SCBL` 卡住的问题。
- 增加 DDNS-GO 服务生成函数的递归回归检查。

# 更新记录

## Server Tool v0.6.9

- DDNS-GO 改为官方原生配置：A / AAAA、DNS 服务商、域名、网卡和 IPv6 匹配规则全部由官方 Web 页面管理。
- 删除 SCBL 自建 IPv4 / IPv6 取址命令、模式强制器和配置监视服务；升级时只迁移引用这些旧命令的配置项，并先生成备份。
- Web 管理自动绑定服务器局域网私有 IPv4 的 9876 端口；没有私有 IPv4 时回退到 127.0.0.1，拒绝 0.0.0.0、公共 IPv4 和 IPv6 监听。
- DDNS-GO 菜单仅保留安装、更新、启动、状态、密码重置和保留配置的卸载。
- Server Tool v0.6.8 已在真实 Ubuntu 26 Server 上完成原地升级验证，Windows 客户端 v0.6.3 可进入线上模式；客户端和 Hooks 保持不变。

## Server Tool v0.6.8

- 修复全新服务器缺少 `service.toml.template` 时，dedicated_server 自动配置把 `SandboxUrl`、`SandboxUrlWS`、`secure_server_addr` 和 `storage_host` 保留为 `127.0.0.1`，导致客户端账号登录成功但游戏无法进入线上模式的问题。
- 首次安装生成与 dedicated_server 默认结构一致且带独立随机票据密钥的配置；升级已有服务器时自动备份，并只修复四个错误的客户端服务地址。
- 服务端状态新增在线配置、内容服务、PRUDP 认证与安全端口检查。
- 控制平面按 UDP 协议检查 21126/21127，不再用 TCP 检测 PRUDP 服务而误报 degraded。
- 不修改、不重编译 Hooks 源码；Windows 客户端版本保持不变。

## Server Tool v0.6.5

- Fixed the piped bootstrap installer appearing to hang after downloading checksums.
- Added bounded download and validation timeouts with visible stage messages.
- Reattached the downloaded manager explicitly to `/dev/tty` before entering the interactive menu.


## Server Tool v0.6.4

- Added explicit rollback for the retained previous Windows client package.
- Added manual rollback to the most recent server-tool upgrade backup with a pre-rollback safety copy.
- Hardened component manifest/file matching and safe bootstrap extraction.
- Added embedded-Python validation before installing a downloaded manager script.

## Client 0.6.3 / Server Tool 0.6.3

- Split Windows client and Linux server-tool versions and release workflows.
- Added GitHub client Release import through the existing package watcher.
- Added verified server-tool online self-upgrade with backup and rollback.
- Retained the newest two locally published client packages.
- Changed Route Guard TCP/UDP fallback owner resolution to precomputed O(1) indexes.


## v0.6.1

- 修复 Linux 服务端菜单选择“1. 首次安装 / 重新安装”后短时间无输出的问题；
- 移除 `set_defaults` 中静默执行的公网 IP 网络探测；
- 进入安装流程后立即显示阶段提示；
- 仅在需要填写公网入口时检测公网 IPv4，并显示最长等待时间与检测结果；
- 保持客户端更新协议、EasyTier、WinDivert 和 Route Guard 逻辑不变。

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
