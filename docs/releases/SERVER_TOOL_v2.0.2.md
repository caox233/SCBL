# SCBL Server 2.0.2

正式二进制由 GitHub Actions 构建并发布，生产服务器不编译源码。
客户端单组件支持 GitHub 在线下载，并继续由服务端校验和发布。

- Fix component artifact directories created as `0700 root:root`; immutable
  component versions are now safely traversable by the `scbl-update` service.
- Repair permissions automatically when republishing an identical immutable
  component version.
- Keep the public update HTTP service responsive when malformed or TLS traffic
  reaches its plain HTTP listener before a request path has been parsed.
- Retain all 2.0.1 deployment lifecycle and SQL migration compatibility fixes.
