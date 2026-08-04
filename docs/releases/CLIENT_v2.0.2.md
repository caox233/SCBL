# SCBL Windows Client 2.0.2

- Refresh the in-game friend list when another player comes online or goes
  offline after the game has already started.
- Ship Hooks as a uniquely versioned component so different DLL builds can no
  longer be accepted under the same client version.
- Improve first-launch EasyTier readiness detection and tolerate slower Route
  Guard heartbeats when the client is stored on a NAS.
- Keep the validated private-room invitation behavior unchanged while bringing
  DX9 and DX11 installations onto the same current Hooks build.
