# SCBL Server 2.0.5

正式二进制由 GitHub Actions 构建并发布，生产服务器不编译源码。
客户端单组件支持 GitHub 在线下载，并继续由服务端校验和发布。

- Publish the current server network bootstrap in the public client update
  manifest and refresh it whenever managed server configuration is applied.
- Preserve the network bootstrap when a client full package or announcement is
  published.
- Remove the retired 1.x server installer, invitation test menu, component
  manager, diagnostics collector, and their dedicated validation workflow.
- Keep the 2.0 manager, binary packaging, dedicated server, control plane,
  update server, rollback, and immutable component publishing paths only.
