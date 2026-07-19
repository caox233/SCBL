# SCBL v0.6.1

这是 v0.6.0 的安装流程修复版，客户端联机、更新协议、WinDivert 和 Route Guard 行为保持不变。

## 修复内容

- 修复 Linux 服务端管理脚本选择 `1. 首次安装 / 重新安装服务端` 后短时间没有任何输出的问题；
- 菜单进入安装流程后立即显示可见状态；
- 公网 IP 自动检测不再发生在安装提示之前；
- 仅在公网入口没有配置时进行检测，并明确提示最长约 8 秒；
- 检测失败后继续允许手工填写，不会静默卡住。

## Windows 客户端

客户端完整包仍包含 WinDivert 2.2.2，用于严格路由、广播转换和数据包重写。少数安全软件可能基于驱动能力产生风险提示，请只从本仓库正式 Release 下载并核对 SHA256。

## Linux 一键安装

```bash
curl -fsSL https://github.com/caox233/SCBL/releases/latest/download/install-server.sh | sudo bash
```
