ALTER TABLE invites ADD COLUMN session_type INTEGER;
ALTER TABLE invites ADD COLUMN session_id INTEGER;
ALTER TABLE invites ADD COLUMN delivered_at DATETIME;
ALTER TABLE invites ADD COLUMN consumed_at DATETIME;
ALTER TABLE invites ADD COLUMN expires_at DATETIME;

-- Legacy invitations had no room identity and cannot be joined safely.
DELETE FROM invites;

CREATE INDEX invites_pending_receiver
    ON invites(receiver, session_type, consumed_at, expires_at);
