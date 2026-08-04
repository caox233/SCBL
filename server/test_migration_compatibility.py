from __future__ import annotations

import hashlib
from pathlib import Path


MIGRATIONS = Path("server/dedicated-server/src/storage/migrations")
EXPECTED_SHA384 = {
    "20230917230530_init.sql": "fcf7f96828a732ab8fc85dbc1673c70f7df265f93fb4ce09aec3150d5791c02533785f700ee02e25bc7db71da8e10232",
    "20231129203012_add_sample.sql": "1de58bc3f10ef678e7818ac701fe5cea98f34fe1a0586d29d7f550e394d667d8fd122176fd03fdef78bf140012f28d73",
    "20231207005134_invites.sql": "5c8b6003dd0c114cedafffc8eb16836c3d9125c72e4e438774db43b0817642c9bff32c376563d9049644d9ad0c5a0fc9",
    "20231212002723_sessions.sql": "5b27844c4e3cc644a495157f1c5cdb046ad53a988239a585ce39ac2d380b68cf45ee06bec0995d85ac9df1d535a5f756",
    "20240108010100_user_logout.sql": "1dbd0afcb5849d33c00ac08a9119ff231f03df7e91f676999f2458f54fc0e9f99068ceb6daab6fb784e941faf2f845b3",
    "20260729130500_invite_session_context.sql": "7201b565b421fc50ba05ae8e8431e3498014a095417b4c999f460682d00b72894f678812a386fc47064eabcee89c86f8",
    "20260729163000_invite_delivery_presence.sql": "bb7e2368259478ed14e0da84c0e56caeef03cf5020034ac51a545a127356b8d9832086da8a5ff5ce66ee8c253b1fafcc",
    "20260730170000_party_follow.sql": "07ee4f7503a42ee50ec458f95d32f134def6bcee650e9bd3921fe8ecdeb0a59b69e03d4a8c4d17eb86e9c662ba0bd93c",
    "20260731130000_party_leave_cleanup.sql": "be8570c2c6e25e3e1d5ac7a510f31d6424e9c74363a9c739bd89038565151497b1c9e8da51dd84ed6d8af1fa1e29e1f6",
}


actual_names = {path.name for path in MIGRATIONS.glob("*.sql")}
assert actual_names == set(EXPECTED_SHA384), (
    "migration set changed; add new migrations without modifying or removing published ones"
)
for name, expected in EXPECTED_SHA384.items():
    content = (MIGRATIONS / name).read_bytes()
    assert b"\r\n" in content or b"\n" not in content, f"{name}: migrations must use CRLF"
    assert content.replace(b"\r\n", b"").find(b"\n") == -1, f"{name}: mixed line endings"
    actual = hashlib.sha384(content).hexdigest()
    assert actual == expected, f"{name}: published migration was modified ({actual})"

print("Dedicated Server migration compatibility checks passed.")
