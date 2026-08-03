# Private matchmaking repair invariants

This repair follows the membership semantics emitted by the retail game client instead of forcing private sessions through the public-lobby JoinSession path.

1. `private_participant_ids` sent by `GameSession.AddParticipants` are real private-session members and are persisted together with public participants.
2. Accepting a direct private-room invitation (`kind = 1`) or party-follow invitation (`kind = 3`) authorizes and persists the receiver in the exact target Session before P2P transport starts.
3. Public lobby invitations (`kind = 2`) continue to use participant search, NAT traversal and `JoinSession`; this successful path is not changed.
4. Private Session attributes remain exactly as the host published them (`113 = 0`, public slots `3 = 0`, private slots `4 > 0`). Search responses must not rewrite a private room into a public-capacity room.
5. Accepted invitations continue to route participant-specific lookup to their exact Session. Membership insertion is idempotent, and later `JoinSession`, `AddParticipants`, `LeaveSession` and `RemoveParticipants` calls remain safe.
6. Manual invitations and party-follow invitations stay in independent lanes, and normal/quick matchmaking never consumes either lane.

Automated coverage verifies private `AddParticipants` persistence, private invitation membership authorization, party-follow membership, exact invitation routing, public-lobby behavior and lane isolation.


## 2026.08.01.22 Uplay GameSession transport repair

Live `.21` diagnostics proved that Quazal private membership and participant search were correct, but the game still abandoned the lobby after receiving a full private-game search result. Retail Uplay's `PartyGameInviteAccepted` payload contains an invitation ID and a pointer to `UPLAY_USER_GameSession`, not an account pointer. Hooks now capture the host's exact `UPLAY_USER_SetGameSession(uint64, UPLAY_DataBlob*, uint32)` payload, publish it to the authenticated dedicated API, deliver it with kind 1/3 events, and build the real Party accepted event. Lobby kind 2 retains the working friend-search transition. Party export signatures, member flags, leader state and member-list outputs are aligned with the Uplay R1 ABI.
