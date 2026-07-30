# SCBL Windows Client v1.0.14

## 主要更新

- 客户端组件更新从 Hooks 专用流程扩展为统一组件清单，可分别核对 Hooks、Route Guard、EasyTier 和 Updater。
- 启动时按版本、文件大小和 SHA256 逐项验证本地组件；文件一致时直接复用，只下载缺失或不一致的组件。
- Hooks、Route Guard、EasyTier 和 Updater 使用版本化缓存、临时文件、原子替换与失败回滚。
- Hooks 在游戏启动前部署；Route Guard、EasyTier 和 Updater 在下一次相同通道启动、相关进程尚未运行时应用。
- Hooks 不再嵌入 Launcher 单文件 EXE。正式完整包携带独立、经过 SHA256 校验的 bootstrap Hooks，Hooks 更新不再要求重新编译 Launcher。
- 新增 `--test` 快捷参数。在正式版快捷方式“目标”末尾增加 `--test`，即可使用测试组件通道；它等价于 `--update-channel test`，并会在 UAC 提权重启后保留。
- 日常构建拆分为 Launcher、Updater、Route Guard、EasyTier 独立流水线；完整客户端只在正式发布、首次安装或修复场景组装。

## 测试版快捷方式

普通快捷方式不带参数，默认使用正式 `stable` 通道：

```text
"D:\SCBL\SplinterCellCNLauncher.exe"
```

复制正式版快捷方式，并在“目标”末尾增加：

```text
"D:\SCBL\SplinterCellCNLauncher.exe" --test
```

关闭测试版启动器后，再运行不带参数的正式快捷方式，即恢复正式通道。测试通道状态不会写入长期配置。

## 安全边界

- 正式客户端版本门禁仍优先执行，组件清单不能绕过正式版本要求。
- `test` 通道组件不会被普通 `stable` 启动误用。
- stable 外置组件激活在签名清单验证完成前保持关闭；正式启动继续使用完整包内已校验的 bootstrap 组件。
- 本版本不改变 EasyTier 拓扑、Route Guard 数据包策略、虚拟网段或普通客户端中继边界。

## 配套版本

- Linux Server Tool: v1.0.9
- Hooks / dedicated server: 继续由 `caox233/5th-echelon` 独立构建和发布。
