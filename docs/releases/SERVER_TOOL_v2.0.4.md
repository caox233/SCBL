# SCBL Server 2.0.4

This release completes the cold-boot reliability pass for the public server.

正式二进制由 GitHub Actions 构建并发布，生产服务器不编译源码。
客户端单组件支持 GitHub 在线下载，并继续由服务端校验和发布。

- Wait up to 45 seconds for a usable public IPv6 address before starting DDNS-Go.
- Avoid the boot-time race where `network-online.target` is reached before IPv6
  SLAAC or DHCPv6 address assignment finishes.
- Keep the wait bounded so an unavailable WAN cannot block startup indefinitely;
  systemd continues to retry DDNS-Go on failure.
- Retain all deployment lifecycle, migration compatibility, component serving,
  malformed HTTP handling, and EasyTier recovery fixes from 2.0.1 through 2.0.3.
