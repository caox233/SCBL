# SCBL Server 2.0

The production server is a binary-only Linux deployment. It does not compile
source code and does not use the retired 1.x installer or invitation-test
management stack.

Current source ownership:

- `bootstrap/`: clean Linux bootstrap entry point.
- `manager/`: the `SCBL` management CLI, deployment, client publishing, DDNS,
  diagnostics, backup, and rollback logic.
- `dedicated-server/`: the Rust game backend.
- `runtime/`: runtime resources packaged with the server.
- `packaging/`: binary package builders.
- `quazal/`: protocol implementation shared by the dedicated server.
- `scbl_control_plane.py`: overlay-only control plane.
- `scbl_update_server.py`: public client update and component file server.

See [`manager/README.md`](manager/README.md) for the filesystem layout and
operator workflows.
