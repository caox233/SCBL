# SCBL Server 2.0.3

正式二进制由 GitHub Actions 构建并发布，生产服务器不编译源码。
客户端单组件支持 GitHub 在线下载，并继续由服务端校验和发布。

- Treat EasyTier's first fast-restart WSS bind failure as recoverable only when
  systemd subsequently reports the tunnel active in two consecutive probes.
- Keep a bounded 15-second recovery window; persistent failures still abort the
  deployment and trigger rollback.
- Retain the deployment ordering, migration compatibility, component serving,
  and malformed HTTP request fixes from 2.0.1 and 2.0.2.
