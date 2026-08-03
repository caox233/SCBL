ALTER TABLE invites ADD COLUMN kind INTEGER NOT NULL DEFAULT 1;
ALTER TABLE invites ADD COLUMN accepted_at DATETIME;
ALTER TABLE invites ADD COLUMN delivery_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN last_api_seen_at DATETIME;

CREATE INDEX invites_accepted_receiver
    ON invites(receiver, accepted_at, session_type, consumed_at, expires_at);
CREATE INDEX users_api_presence
    ON users(last_api_seen_at);
