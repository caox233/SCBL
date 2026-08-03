-- Remove persistent party membership only for a real lobby leave.
-- During a successful lobby-to-game transition the user is already present
-- in an active 113=0 room, so the party survives that transition.
CREATE TRIGGER scbl_party_member_leave_lobby
AFTER DELETE ON participants
WHEN EXISTS (
    SELECT 1
    FROM game_sessions AS lobby
    WHERE lobby.id = OLD.game_id
      AND (';' || replace(lobby.attributes, ' ', '') || ';') LIKE '%;113=>1;%'
)
AND NOT EXISTS (
    SELECT 1
    FROM participants AS current_participant
    INNER JOIN game_sessions AS current_session
        ON current_session.id = current_participant.game_id
    WHERE current_participant.user_id = OLD.user_id
      AND current_session.destroyed_at IS NULL
      AND (';' || replace(current_session.attributes, ' ', '') || ';') LIKE '%;113=>0;%'
)
BEGIN
    DELETE FROM parties WHERE leader_id = OLD.user_id;
    DELETE FROM party_members WHERE user_id = OLD.user_id;
END;
