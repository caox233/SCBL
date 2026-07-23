# SCBL Server Tool v0.6.8

This is a server-tool-only release. The Windows client and Hooks source/binary are unchanged.

## Online mode repair

- Fixes fresh installations whose generated `service.toml` advertised `127.0.0.1` for the PRUDP authentication, secure and content endpoints.
- Generates an equivalent dedicated-server configuration with a unique random ticket key for each server.
- Backs up and repairs only the four client-facing loopback endpoints in existing affected configurations without replacing `5th-echelon.db`.
- Adds explicit status checks for TCP 80/8000/50051 and UDP 21126/21127.
- Corrects control-plane health checks to inspect PRUDP ports as UDP rather than TCP.

