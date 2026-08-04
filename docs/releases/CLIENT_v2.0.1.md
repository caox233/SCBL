# SCBL Windows Client 2.0.1

- Fetch the current server network bootstrap over the public update endpoint
  before starting EasyTier.
- Automatically synchronize the server endpoint, EasyTier network name, WSS
  port, and server-specific tunnel credential.
- Store the tunnel credential with Windows DPAPI instead of plaintext.
- Allow an existing client to reconnect after a clean server reinstall without
  requiring a manually edited configuration file.
