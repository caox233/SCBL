# SCBL Server 2.0.1

正式二进制由 GitHub Actions 构建并发布，生产服务器不编译源码。

This maintenance release fixes a systemd restart race during server install,
repair, update, and rollback operations. The manager now stops services that
depend on the EasyTier tunnel before restarting it, then starts the dedicated
server and control plane in dependency order.

Dedicated Server SQL migrations now retain the byte-for-byte CRLF form used by
existing SCBL databases. CI pins every published migration SHA-384 so a line
ending conversion or accidental edit cannot break future upgrades.

Installed component detection now reads the version of the manager actually
installed on the server, allowing a newer bootstrap manager to upgrade an older
installation without reporting a false downgrade.
