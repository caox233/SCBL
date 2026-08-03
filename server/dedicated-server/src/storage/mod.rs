use std::collections::HashMap;
use std::collections::HashSet;
use std::sync::Mutex;
use std::time::Duration;
use std::time::Instant;

use argon2::password_hash::rand_core::OsRng;
use argon2::password_hash::SaltString;
use argon2::Argon2;
use argon2::PasswordHash;
use argon2::PasswordHasher;
use argon2::PasswordVerifier;
use eyre::eyre;
use slog::Logger;
use sqlx::sqlite::SqlitePool;
use sqlx::Execute;

type Result<T> = eyre::Result<T>;

pub(crate) const INVITE_KIND_PRIVATE_ROOM: i32 = 1;
pub(crate) const INVITE_KIND_LOBBY_PARTY: i32 = 2;
pub(crate) const INVITE_KIND_PARTY_FOLLOW: i32 = 3;
pub(crate) const INVITE_KIND_LOBBY_RESTORE: i32 = 4;
const MANUAL_INVITATION_SEARCH_TTL: Duration = Duration::from_secs(15);

fn session_attribute_value(attributes: &str, attribute_id: u32) -> Option<u32> {
    attributes.split(';').find_map(|entry| {
        let (raw_id, raw_value) = entry.split_once("=>")?;
        let id = raw_id.trim().parse::<u32>().ok()?;
        if id != attribute_id {
            return None;
        }
        raw_value.trim().parse::<u32>().ok()
    })
}

fn is_lobby_session_attributes(attributes: &str) -> bool {
    session_attribute_value(attributes, 113) == Some(1)
}

fn is_game_room_attributes(attributes: &str) -> bool {
    session_attribute_value(attributes, 113) == Some(0)
}

/// Response-only compatibility projection for an accepted direct private invite.
/// Persistent attributes and every field except Session class `113` remain
/// byte-for-byte equivalent at the semantic attribute level.
fn project_private_invite_session_class_only(attributes: &str) -> String {
    let mut projected = Vec::new();
    let mut replaced = false;

    for entry in attributes.split(';') {
        let entry = entry.trim();
        if entry.is_empty() {
            continue;
        }
        if let Some((raw_id, _)) = entry.split_once("=>") {
            if raw_id.trim() == "113" {
                projected.push(String::from("113 => 1"));
                replaced = true;
                continue;
            }
        }
        projected.push(entry.to_string());
    }

    if !replaced {
        projected.push(String::from("113 => 1"));
    }
    projected.join(";")
}

fn run<F>(future: F) -> Result<F::Output>
where
    F: std::future::Future,
{
    Ok(tokio::runtime::Builder::new_current_thread().enable_time().build()?.block_on(future))
}

pub struct Storage {
    logger: Logger,
    pool: SqlitePool,
    manual_invitation_searches: Mutex<HashMap<(u32, u32), Instant>>,
}

pub enum LoginError {
    NotFound,
    InvalidPassword,
}

impl Storage {
    pub fn init(logger: Logger) -> Result<Self> {
        Self::init_with_database_url(logger, "sqlite://5th-echelon.db?mode=rwc")
    }

    fn init_with_database_url(logger: Logger, database_url: &str) -> Result<Self> {
        let pool = run(async {
            let pool = SqlitePool::connect(database_url).await?;
            // enable foreign key checks
            sqlx::query("PRAGMA foreign_keys=ON").execute(&pool).await?;
            sqlx::migrate!("src/storage/migrations").run(&pool).await?;
            Ok::<_, eyre::Error>(pool)
        })??;
        Ok(Self {
            logger,
            pool,
            manual_invitation_searches: Mutex::new(HashMap::new()),
        })
    }

    fn prune_manual_invitation_searches(searches: &mut HashMap<(u32, u32), Instant>) {
        searches.retain(|_, created| created.elapsed() <= MANUAL_INVITATION_SEARCH_TTL);
    }

    pub fn note_manual_invitation_search(&self, receiver_id: u32, session_type: u32) {
        if let Ok(mut searches) = self.manual_invitation_searches.lock() {
            Self::prune_manual_invitation_searches(&mut searches);
            searches.insert((receiver_id, session_type), Instant::now());
        }
    }

    pub fn clear_manual_invitation_search(&self, receiver_id: u32, session_type: u32) {
        if let Ok(mut searches) = self.manual_invitation_searches.lock() {
            searches.remove(&(receiver_id, session_type));
        }
    }

    pub fn manual_invitation_search_is_active(&self, receiver_id: u32, session_type: u32) -> bool {
        let Ok(mut searches) = self.manual_invitation_searches.lock() else {
            return false;
        };
        Self::prune_manual_invitation_searches(&mut searches);
        searches.contains_key(&(receiver_id, session_type))
    }

    pub async fn login_user_async(&self, username: &str, password: &str) -> Result<std::result::Result<u32, LoginError>> {
        let Some((id, db_password, password_hash)) = sqlx::query_as::<_, (u32, Option<String>, Option<String>)>("SELECT id, password, password_hash FROM users WHERE username = ?")
            .bind(username)
            .fetch_optional(&self.pool)
            .await?
        else {
            warn!(self.logger, "User {} not found", username);
            return Ok(Err(LoginError::NotFound));
        };

        let maybe_id = match (db_password, password_hash) {
            (None, None) => Err(eyre!("neither password or password_hash set for user {}", id)),
            (Some(_), Some(_)) => Err(eyre!("password and password_hash set for user {}", id)),
            (Some(db_password), None) => {
                info!(self.logger, "Verify plain password of {}", username);
                if db_password == password {
                    Ok(Ok(id))
                } else {
                    Ok(Err(LoginError::InvalidPassword))
                }
            }
            (None, Some(password_hash)) => {
                info!(self.logger, "Verify password hash of {}", username);
                let parsed_hash = PasswordHash::new(&password_hash).map_err(|_| eyre!("password hash parsing failed"))?;
                Ok(Argon2::default()
                    .verify_password(password.as_bytes(), &parsed_hash)
                    .map_err(|_| LoginError::InvalidPassword)
                    .and(Ok(id)))
            }
        }?;

        if let Ok(user_id) = maybe_id {
            sqlx::query("UPDATE users SET last_login = CURRENT_TIMESTAMP, is_online=1 WHERE id = ?")
                .bind(user_id)
                .execute(&self.pool)
                .await?;
        }

        Ok(maybe_id)
    }

    pub fn login_user(&self, username: &str, password: &str) -> Result<std::result::Result<u32, LoginError>> {
        run(self.login_user_async(username, password))?
    }

    /// Refreshes the API-side presence heartbeat. The overlay polls the event
    /// service every second while the game is running, so this heartbeat keeps
    /// a player visible even when the secure PRUDP connection is temporarily idle.
    pub async fn touch_api_presence_async(&self, user_id: u32) -> Result<()> {
        sqlx::query("UPDATE users SET last_api_seen_at = CURRENT_TIMESTAMP WHERE id = ?")
            .bind(user_id)
            .execute(&self.pool)
            .await?;
        Ok(())
    }

    pub fn register_user(&self, username: &str, password: &str, ubi_id: Option<&str>) -> Result<()> {
        run(self.register_user_async(username, password, ubi_id))?
    }

    pub async fn register_user_async(&self, username: &str, password: &str, ubi_id: Option<&str>) -> Result<()> {
        let salt = SaltString::try_from_rng(&mut OsRng).unwrap();
        let password_hash = Argon2::default()
            .hash_password(password.as_bytes(), salt.as_salt())
            .map_err(|_| eyre!("password hashing failed"))?
            .to_string();
        Ok(self.register_user_unsafe_async(username, &password_hash, ubi_id).await?)
    }

    async fn register_user_unsafe_async(&self, username: &str, password: &str, ubi_id: Option<&str>) -> sqlx::Result<()> {
        sqlx::query("INSERT INTO users (username, password_hash, ubi_id) VALUES (?, ?, ?)")
            .bind(username)
            .bind(password)
            .bind(ubi_id)
            .execute(&self.pool)
            .await?;
        Ok(())
    }

    pub fn find_password_for_user(&self, user_id: u32) -> Result<Option<String>> {
        let password = run(sqlx::query_as::<_, (Option<String>,)>("SELECT password FROM users WHERE id = ?")
            .bind(user_id)
            .fetch_optional(&self.pool))??
        .and_then(|row| row.0);
        Ok(password)
    }

    pub fn find_user_by_ubi_id(&self, ubi_id: &str) -> Result<Option<User>> {
        run(self.find_user_by_ubi_id_async(ubi_id))?
    }

    pub async fn find_user_by_ubi_id_async(&self, ubi_id: &str) -> Result<Option<User>> {
        Ok(sqlx::query_as("SELECT id, username, ubi_id, is_online FROM users WHERE ubi_id = ?")
            .bind(ubi_id)
            .fetch_optional(&self.pool)
            .await?)
    }

    pub fn find_user_by_id(&self, id: u32) -> Result<Option<User>> {
        run(self.find_user_by_id_async(id))?
    }

    pub async fn find_user_by_id_async(&self, id: u32) -> Result<Option<User>> {
        Ok(sqlx::query_as("SELECT id, username, ubi_id, is_online FROM users WHERE id = ?")
            .bind(id)
            .fetch_optional(&self.pool)
            .await?)
    }

    pub fn find_user_id_by_name(&self, username: &str) -> Result<Option<u32>> {
        let uid = run(sqlx::query_as::<_, (u32,)>("SELECT id FROM users WHERE username = ?")
            .bind(username)
            .fetch_optional(&self.pool))??
        .map(|row| row.0);
        Ok(uid)
    }

    pub fn find_ubi_id_by_user_id(&self, user_id: u32) -> Result<Option<String>> {
        run(self.find_ubi_id_by_user_id_async(user_id))?
    }

    pub async fn find_ubi_id_by_user_id_async(&self, user_id: u32) -> Result<Option<String>> {
        let ubi_id = sqlx::query_as::<_, (Option<String>,)>("SELECT ubi_id FROM users WHERE id = ?")
            .bind(user_id)
            .fetch_optional(&self.pool)
            .await?
            .and_then(|row| row.0);
        Ok(ubi_id)
    }

    pub fn find_username_by_user_id(&self, user_id: u32) -> Result<Option<String>> {
        run(self.find_username_by_user_id_async(user_id))?
    }

    pub async fn find_username_by_user_id_async(&self, user_id: u32) -> Result<Option<String>> {
        let ubi_id = sqlx::query_as::<_, (Option<String>,)>("SELECT username FROM users WHERE id = ?")
            .bind(user_id)
            .fetch_optional(&self.pool)
            .await?
            .and_then(|row| row.0);
        Ok(ubi_id)
    }

    pub fn find_user_id_by_ubi_id(&self, ubi_id: &str) -> Result<Option<u32>> {
        run(self.find_user_id_by_ubi_id_async(ubi_id))?
    }

    pub async fn find_user_id_by_ubi_id_async(&self, ubi_id: &str) -> Result<Option<u32>> {
        let uid = sqlx::query_as::<_, (u32,)>("SELECT id FROM users WHERE ubi_id = ?")
            .bind(ubi_id)
            .fetch_optional(&self.pool)
            .await?
            .map(|row| row.0);
        Ok(uid)
    }

    pub fn create_user_session(&self, user_id: u32, key: &[u8]) -> Result<()> {
        use std::fmt::Write;
        let mut s = String::new();
        for c in key {
            write!(&mut s, "{c:02X}")?;
        }
        run(async {
            sqlx::query("INSERT INTO user_sessions (id, user_id) VALUES (?, ?)")
                .bind(s)
                .bind(user_id)
                .execute(&self.pool)
                .await?;

            sqlx::query("UPDATE users SET is_online=1 WHERE id=?").bind(user_id).execute(&self.pool).await
        })??;

        Ok(())
    }

    pub fn delete_user_session(&self, user_id: u32) -> Result<()> {
        run(async {
            let mut transaction = self.pool.begin().await?;
            let affected_sessions = sqlx::query_as::<_, (u32, u32)>(
                r"
                SELECT DISTINCT g.type_id, g.id
                FROM game_sessions AS g
                LEFT JOIN participants AS p ON p.game_id = g.id
                WHERE g.destroyed_at IS NULL
                  AND (g.creator_id = ? OR p.user_id = ?)
                ",
            )
            .bind(user_id)
            .bind(user_id)
            .fetch_all(&mut *transaction)
            .await?;

            sqlx::query("DELETE FROM parties WHERE leader_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("DELETE FROM party_members WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("DELETE FROM participants WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("DELETE FROM station_urls WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("DELETE FROM user_sessions WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("UPDATE users SET is_online=0 WHERE id = ?").bind(user_id).execute(&mut *transaction).await?;

            for (type_id, session_id) in affected_sessions {
                Self::reconcile_game_session_async(&mut transaction, type_id, session_id).await?;
            }

            transaction.commit().await
        })??;

        Ok(())
    }

    /// Handles a secure PRUDP connection closing or expiring. When the
    /// in-game overlay has checked in recently, only the obsolete secure ticket
    /// is removed; the lobby/room membership and station URL stay available.
    /// The preserved state is cleaned after the API heartbeat becomes stale.
    /// Returns true when game state was preserved.
    pub fn close_secure_session(&self, user_id: u32) -> Result<bool> {
        let preserved = run(async {
            let mut transaction = self.pool.begin().await?;
            let (recent_api,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM users WHERE id = ? AND last_api_seen_at >= datetime('now', '-15 seconds'))")
                .bind(user_id)
                .fetch_one(&mut *transaction)
                .await?;

            sqlx::query("DELETE FROM user_sessions WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            sqlx::query("UPDATE users SET is_online = 0 WHERE id = ?").bind(user_id).execute(&mut *transaction).await?;

            if recent_api != 0 {
                transaction.commit().await?;
                return Ok::<bool, sqlx::Error>(true);
            }

            Self::cleanup_user_game_state_async(&mut transaction, user_id).await?;
            transaction.commit().await?;
            Ok::<bool, sqlx::Error>(false)
        })??;
        Ok(preserved)
    }

    async fn cleanup_user_game_state_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>, user_id: u32) -> sqlx::Result<()> {
        let affected_sessions = sqlx::query_as::<_, (u32, u32)>(
            r"
            SELECT DISTINCT g.type_id, g.id
            FROM game_sessions AS g
            LEFT JOIN participants AS p ON p.game_id = g.id
            WHERE g.destroyed_at IS NULL
              AND (g.creator_id = ? OR p.user_id = ?)
            ",
        )
        .bind(user_id)
        .bind(user_id)
        .fetch_all(&mut **transaction)
        .await?;

        sqlx::query("DELETE FROM parties WHERE leader_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("DELETE FROM party_members WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("DELETE FROM participants WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("DELETE FROM station_urls WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("DELETE FROM user_sessions WHERE user_id = ?").bind(user_id).execute(&mut **transaction).await?;
        sqlx::query("UPDATE users SET is_online = 0 WHERE id = ?").bind(user_id).execute(&mut **transaction).await?;

        for (type_id, session_id) in affected_sessions {
            Self::reconcile_game_session_async(transaction, type_id, session_id).await?;
        }
        Ok(())
    }

    /// Removes preserved lobby/room state after both the secure session and
    /// overlay heartbeat are gone. Active secure users have `is_online=1`
    /// and are never selected by this sweep.
    pub async fn cleanup_stale_api_sessions_async(&self) -> Result<()> {
        let mut transaction = self.pool.begin().await?;
        let stale_users: Vec<(u32,)> = sqlx::query_as(
            r"
            SELECT id
            FROM users
            WHERE is_online = 0
              AND (last_api_seen_at IS NULL OR last_api_seen_at < datetime('now', '-15 seconds'))
              AND (
                  EXISTS (SELECT 1 FROM station_urls WHERE station_urls.user_id = users.id)
                  OR EXISTS (SELECT 1 FROM participants WHERE participants.user_id = users.id)
              )
            ",
        )
        .fetch_all(&mut *transaction)
        .await?;

        for (user_id,) in stale_users {
            Self::cleanup_user_game_state_async(&mut transaction, user_id).await?;
        }
        transaction.commit().await?;
        Ok(())
    }

    pub fn invalidate_sessions(&self) -> Result<()> {
        run(async {
            sqlx::query("DELETE FROM parties").execute(&self.pool).await?;
            sqlx::query("DELETE FROM party_members").execute(&self.pool).await?;
            sqlx::query("DELETE FROM station_urls").execute(&self.pool).await?;
            sqlx::query("DELETE FROM user_sessions").execute(&self.pool).await?;
            sqlx::query("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE destroyed_at IS NULL")
                .execute(&self.pool)
                .await?;
            sqlx::query("UPDATE users SET is_online=0").execute(&self.pool).await
        })??;

        Ok(())
    }

    async fn ensure_party_for_leader_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>, leader_id: u32) -> sqlx::Result<i64> {
        let existing = sqlx::query_as::<_, (i64,)>("SELECT id FROM parties WHERE leader_id = ?")
            .bind(leader_id)
            .fetch_optional(&mut **transaction)
            .await?;
        let party_id = if let Some((party_id,)) = existing {
            party_id
        } else {
            sqlx::query("INSERT INTO parties (leader_id) VALUES (?)")
                .bind(leader_id)
                .execute(&mut **transaction)
                .await?
                .last_insert_rowid()
        };
        sqlx::query(
            "INSERT INTO party_members (user_id, party_id) VALUES (?, ?) \
             ON CONFLICT(user_id) DO UPDATE SET party_id = excluded.party_id, joined_at = CURRENT_TIMESTAMP",
        )
        .bind(leader_id)
        .bind(party_id)
        .execute(&mut **transaction)
        .await?;
        sqlx::query("UPDATE parties SET updated_at = CURRENT_TIMESTAMP WHERE id = ?")
            .bind(party_id)
            .execute(&mut **transaction)
            .await?;
        Ok(party_id)
    }

    async fn queue_party_follow_invites_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>, leader_id: u32, session_type: u32, session_id: u32) -> sqlx::Result<u64> {
        let Some((party_id,)) = sqlx::query_as::<_, (i64,)>("SELECT id FROM parties WHERE leader_id = ?")
            .bind(leader_id)
            .fetch_optional(&mut **transaction)
            .await?
        else {
            return Ok(0);
        };
        let (leader_joined,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM participants WHERE game_id = ? AND user_id = ?)")
            .bind(session_id)
            .bind(leader_id)
            .fetch_one(&mut **transaction)
            .await?;
        if leader_joined == 0 {
            return Ok(0);
        }

        let members = sqlx::query_as::<_, (u32,)>("SELECT user_id FROM party_members WHERE party_id = ? AND user_id != ? ORDER BY user_id")
            .bind(party_id)
            .bind(leader_id)
            .fetch_all(&mut **transaction)
            .await?;
        let mut created = 0;
        for (receiver_id,) in members {
            sqlx::query(
                r"
                DELETE FROM invites
                WHERE receiver = ?
                  AND kind = ?
                  AND consumed_at IS NULL
                  AND NOT (
                      party_id = ?
                      AND session_type = ?
                      AND session_id = ?
                      AND expires_at > CURRENT_TIMESTAMP
                  )
                ",
            )
            .bind(receiver_id)
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .bind(party_id)
            .bind(session_type)
            .bind(session_id)
            .execute(&mut **transaction)
            .await?;
            let result = sqlx::query(
                r"
                INSERT INTO invites (sender, receiver, kind, session_type, session_id, party_id, expires_at)
                SELECT ?, ?, ?, ?, ?, ?, datetime('now', '+5 minutes')
                WHERE NOT EXISTS (
                    SELECT 1 FROM invites
                    WHERE receiver = ?
                      AND kind = ?
                      AND party_id = ?
                      AND session_type = ?
                      AND session_id = ?
                      AND consumed_at IS NULL
                      AND expires_at > CURRENT_TIMESTAMP
                )
                ",
            )
            .bind(leader_id)
            .bind(receiver_id)
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .bind(session_type)
            .bind(session_id)
            .bind(party_id)
            .bind(receiver_id)
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .bind(party_id)
            .bind(session_type)
            .bind(session_id)
            .execute(&mut **transaction)
            .await?;
            created += result.rows_affected();
        }
        sqlx::query("UPDATE parties SET updated_at = CURRENT_TIMESTAMP WHERE id = ?")
            .bind(party_id)
            .execute(&mut **transaction)
            .await?;
        Ok(created)
    }

    pub fn create_game_session(&self, user_id: u32, type_id: u32, attributes: String) -> Result<u32> {
        let game_room = is_game_room_attributes(&attributes);
        let (id, follow_count) = run(async {
            let mut transaction = self.pool.begin().await?;
            let id = sqlx::query("INSERT INTO game_sessions (type_id, creator_id, attributes) VALUES (?, ?, ?)")
                .bind(type_id)
                .bind(user_id)
                .bind(&attributes)
                .execute(&mut *transaction)
                .await?
                .last_insert_rowid();
            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                .bind(id)
                .bind(user_id)
                .execute(&mut *transaction)
                .await?;
            let follow_count = if game_room {
                #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
                Self::queue_party_follow_invites_async(&mut transaction, user_id, type_id, id as u32).await?
            } else {
                0
            };
            transaction.commit().await?;
            Ok::<(i64, u64), sqlx::Error>((id, follow_count))
        })??;

        #[allow(clippy::cast_possible_truncation)]
        #[allow(clippy::cast_sign_loss)]
        let session_id = id as u32;
        if follow_count > 0 {
            info!(
                self.logger,
                "PartySessionBound leader_id={user_id} session_type={type_id} session_id={session_id} follow_invites={follow_count}"
            );
        }
        Ok(session_id)
    }

    pub fn game_session_is_active(&self, type_id: u32, session_id: u32) -> Result<bool> {
        let (active,): (i64,) = run(
            sqlx::query_as("SELECT EXISTS(SELECT 1 FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL)")
                .bind(session_id)
                .bind(type_id)
                .fetch_one(&self.pool),
        )??;
        Ok(active == 1)
    }

    /// Records a validated JoinSession caller as a participant without applying
    /// any capacity gate. The game may repeat JoinSession and AddParticipants,
    /// so INSERT OR IGNORE keeps the operation idempotent.
    ///
    /// A matching private-room invitation is consumed in the same transaction,
    /// after the participant has been persisted. Repeated JoinSession calls remain
    /// idempotent and cannot revive an expired invitation.
    ///
    /// Returns false only when the referenced session is missing or destroyed.
    pub fn join_game_session_unbounded(&self, user_id: u32, type_id: u32, session_id: u32) -> Result<bool> {
        let (joined, follow_count) = run(async {
            let mut transaction = self.pool.begin().await?;
            let session = sqlx::query_as::<_, (u32, String)>("SELECT creator_id, attributes FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .fetch_optional(&mut *transaction)
                .await?;
            let Some((_creator_id, attributes)) = session else {
                transaction.rollback().await?;
                return Ok::<(bool, u64), sqlx::Error>((false, 0));
            };

            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                .bind(session_id)
                .bind(user_id)
                .execute(&mut *transaction)
                .await?;

            let accepted_lobby_invite = sqlx::query_as::<_, (i64, u32)>(
                r"
                SELECT party_id, sender
                FROM invites
                WHERE receiver = ?
                  AND kind IN (?, ?)
                  AND session_type = ?
                  AND session_id = ?
                  AND party_id IS NOT NULL
                  AND accepted_at IS NOT NULL
                  AND consumed_at IS NULL
                  AND expires_at > CURRENT_TIMESTAMP
                ORDER BY created DESC, rowid DESC
                LIMIT 1
                ",
            )
            .bind(user_id)
            .bind(INVITE_KIND_LOBBY_PARTY)
            .bind(INVITE_KIND_LOBBY_RESTORE)
            .bind(type_id)
            .bind(session_id)
            .fetch_optional(&mut *transaction)
            .await?;
            if let Some((party_id, leader_id)) = accepted_lobby_invite {
                sqlx::query("DELETE FROM parties WHERE leader_id = ? AND id != ?")
                    .bind(user_id)
                    .bind(party_id)
                    .execute(&mut *transaction)
                    .await?;
                sqlx::query(
                    "INSERT INTO party_members (user_id, party_id) VALUES (?, ?) \
                     ON CONFLICT(user_id) DO UPDATE SET party_id = excluded.party_id, joined_at = CURRENT_TIMESTAMP",
                )
                .bind(user_id)
                .bind(party_id)
                .execute(&mut *transaction)
                .await?;
                sqlx::query("UPDATE parties SET updated_at = CURRENT_TIMESTAMP WHERE id = ? AND leader_id = ?")
                    .bind(party_id)
                    .bind(leader_id)
                    .execute(&mut *transaction)
                    .await?;
            }

            sqlx::query(
                "UPDATE invites SET consumed_at = CURRENT_TIMESTAMP WHERE receiver = ? AND session_type = ? AND session_id = ? AND accepted_at IS NOT NULL AND consumed_at IS NULL AND expires_at > CURRENT_TIMESTAMP",
            )
            .bind(user_id)
            .bind(type_id)
            .bind(session_id)
            .execute(&mut *transaction)
            .await?;

            let follow_count = if is_game_room_attributes(&attributes) {
                Self::queue_party_follow_invites_async(&mut transaction, user_id, type_id, session_id).await?
            } else {
                0
            };
            transaction.commit().await?;
            Ok::<(bool, u64), sqlx::Error>((true, follow_count))
        })??;
        if follow_count > 0 {
            info!(
                self.logger,
                "PartySessionBound leader_id={user_id} session_type={type_id} session_id={session_id} follow_invites={follow_count}"
            );
        }
        Ok(joined)
    }

    /// Returns a read-only snapshot used only for SCBL capacity diagnostics.
    /// This does not enforce a limit or change the database schema.
    pub fn game_session_diagnostics(&self, type_id: u32, session_id: u32) -> Result<Option<(u32, String, Vec<u32>)>> {
        let snapshot = run(async {
            let Some((creator_id, attributes)) =
                sqlx::query_as::<_, (u32, String)>("SELECT creator_id, attributes FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                    .bind(session_id)
                    .bind(type_id)
                    .fetch_optional(&self.pool)
                    .await?
            else {
                return Ok::<Option<(u32, String, Vec<u32>)>, sqlx::Error>(None);
            };

            let participant_ids = sqlx::query_as::<_, (u32,)>("SELECT user_id FROM participants WHERE game_id = ? ORDER BY user_id")
                .bind(session_id)
                .fetch_all(&self.pool)
                .await?
                .into_iter()
                .map(|row| row.0)
                .collect();

            Ok::<Option<(u32, String, Vec<u32>)>, sqlx::Error>(Some((creator_id, attributes, participant_ids)))
        })??;
        Ok(snapshot)
    }

    async fn reconcile_game_session_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>, type_id: u32, session_id: u32) -> sqlx::Result<Option<u32>> {
        let creator = sqlx::query_as::<_, (u32,)>("SELECT creator_id FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
            .bind(session_id)
            .bind(type_id)
            .fetch_optional(&mut **transaction)
            .await?;
        let Some((creator_id,)) = creator else {
            return Ok(None);
        };

        let participant_ids = sqlx::query_as::<_, (u32,)>("SELECT user_id FROM participants WHERE game_id = ? ORDER BY user_id")
            .bind(session_id)
            .fetch_all(&mut **transaction)
            .await?;

        if participant_ids.is_empty() {
            sqlx::query("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .execute(&mut **transaction)
                .await?;
            return Ok(None);
        }

        if participant_ids.iter().any(|(participant_id,)| *participant_id == creator_id) {
            return Ok(Some(creator_id));
        }

        let new_creator_id = participant_ids[0].0;
        sqlx::query("UPDATE game_sessions SET creator_id = ? WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
            .bind(new_creator_id)
            .bind(session_id)
            .bind(type_id)
            .execute(&mut **transaction)
            .await?;
        Ok(Some(new_creator_id))
    }

    /// Removes a user from an active game session without synthesizing a lobby
    /// transition. Party/lobby ownership belongs to the game client's state
    /// machine; in particular a non-host leaving a match must not be attached
    /// to the former host's Party.
    pub fn leave_game_session(&self, user_id: u32, type_id: u32, session_id: u32) -> Result<()> {
        run(async {
            let mut transaction = self.pool.begin().await?;
            sqlx::query(
                r"
                DELETE FROM participants
                WHERE game_id = ?
                  AND user_id = ?
                  AND EXISTS (
                      SELECT 1 FROM game_sessions
                      WHERE id = ? AND type_id = ? AND destroyed_at IS NULL
                  )
                ",
            )
            .bind(session_id)
            .bind(user_id)
            .bind(session_id)
            .bind(type_id)
            .execute(&mut *transaction)
            .await?;
            Self::reconcile_game_session_async(&mut transaction, type_id, session_id).await?;
            transaction.commit().await?;
            Ok::<(), sqlx::Error>(())
        })??;
        Ok(())
    }

    /// Transfers ownership of an active game session to a remaining
    /// participant. `SplitSession` does not carry a separate host PID, so the
    /// authenticated caller is the new host selected by the game client.
    ///
    /// Returns `false` when the session is not active or the caller is not a
    /// participant. Returning a boolean lets the protocol layer map an invalid
    /// migration request to `AccessDenied` without exposing storage details.
    pub fn migrate_game_session_host(&self, user_id: u32, type_id: u32, session_id: u32) -> Result<bool> {
        let migrated = run(async {
            let mut transaction = self.pool.begin().await?;

            let (is_participant,): (i64,) = sqlx::query_as(
                r"
                SELECT EXISTS(
                    SELECT 1
                    FROM game_sessions AS g
                    INNER JOIN participants AS p ON p.game_id = g.id
                    WHERE g.id = ?
                      AND g.type_id = ?
                      AND g.destroyed_at IS NULL
                      AND p.user_id = ?
                )
                ",
            )
            .bind(session_id)
            .bind(type_id)
            .bind(user_id)
            .fetch_one(&mut *transaction)
            .await?;

            if is_participant == 0 {
                transaction.rollback().await?;
                return Ok::<bool, sqlx::Error>(false);
            }

            let result = sqlx::query(
                r"
                UPDATE game_sessions
                SET creator_id = ?
                WHERE id = ?
                  AND type_id = ?
                  AND destroyed_at IS NULL
                ",
            )
            .bind(user_id)
            .bind(session_id)
            .bind(type_id)
            .execute(&mut *transaction)
            .await?;

            transaction.commit().await?;
            Ok::<bool, sqlx::Error>(result.rows_affected() == 1)
        })??;

        Ok(migrated)
    }

    /// Restores the caller's own just-abandoned session for the retail
    /// `AbandonSession -> SplitSession -> AddParticipants` migration sequence.
    /// Only the original creator may revive the exact destroyed key, so an
    /// arbitrary client cannot resurrect another player's stale session.
    pub fn restore_abandoned_game_session_for_split(&self, user_id: u32, type_id: u32, session_id: u32) -> Result<bool> {
        let restored = run(async {
            let mut transaction = self.pool.begin().await?;
            let result = sqlx::query(
                r"
                UPDATE game_sessions
                SET creator_id = ?, destroyed_at = NULL
                WHERE id = ?
                  AND type_id = ?
                  AND creator_id = ?
                  AND destroyed_at IS NOT NULL
                ",
            )
            .bind(user_id)
            .bind(session_id)
            .bind(type_id)
            .bind(user_id)
            .execute(&mut *transaction)
            .await?;

            if result.rows_affected() != 1 {
                transaction.rollback().await?;
                return Ok::<bool, sqlx::Error>(false);
            }

            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                .bind(session_id)
                .bind(user_id)
                .execute(&mut *transaction)
                .await?;
            transaction.commit().await?;
            Ok::<bool, sqlx::Error>(true)
        })??;
        Ok(restored)
    }

    pub fn update_game_session(&self, type_id: u32, game_id: u32, attributes: String) -> Result<()> {
        let _id = run(sqlx::query("UPDATE game_sessions SET attributes = ? WHERE id = ? AND type_id = ?")
            .bind(attributes)
            .bind(game_id)
            .bind(type_id)
            .execute(&self.pool))??;

        Ok(())
    }

    pub fn search_sessions(&self, type_id: u32, exclude_user: Option<u32>) -> Result<Vec<GameSession>> {
        let mut sessions: Vec<GameSession> = if let Some(uid) = exclude_user {
            run(sqlx::query_as(
                "SELECT type_id as session_type, id as session_id, creator_id, attributes FROM game_sessions WHERE type_id = ? AND creator_id != ? AND destroyed_at IS NULL",
            )
            .bind(type_id)
            .bind(uid)
            .fetch_all(&self.pool))??
        } else {
            run(
                sqlx::query_as("SELECT type_id as session_type, id as session_id, creator_id, attributes FROM game_sessions WHERE type_id = ? AND destroyed_at IS NULL")
                    .bind(type_id)
                    .fetch_all(&self.pool),
            )??
        };

        for session in &mut sessions {
            session.participants = run(
                sqlx::query_as("SELECT user_id, username as name FROM participants p, users u WHERE u.id = user_id AND game_id = ?")
                    .bind(session.session_id)
                    .fetch_all(&self.pool),
            )??;

            for participant in &mut session.participants {
                // Is this needed? Games seems to try to connect to itself
                if matches!(exclude_user, Some(pid) if pid == participant.user_id) {
                    continue;
                }
                participant.station_urls = run(sqlx::query_as("SELECT url FROM station_urls WHERE user_id = ?")
                    .bind(participant.user_id)
                    .fetch_all(&self.pool))??
                .into_iter()
                .map(|r: (String,)| r.0)
                .collect();
            }
        }

        // A joinable session must have a live host participant and at least one
        // registered endpoint. This hides the brief handover window after the old
        // host leaves and also prevents stale sessions from being returned.
        sessions.retain(|session| {
            session
                .participants
                .iter()
                .any(|participant| participant.user_id == session.creator_id && !participant.station_urls.is_empty())
        });

        Ok(sessions)
    }

    pub fn add_participants(&self, caller_id: u32, type_id: u32, session_id: u32, private_participants: Vec<u32>, public_participants: Vec<u32>) -> Result<()> {
        let private_participants = private_participants.into_iter().collect::<HashSet<_>>();
        let public_participants = public_participants.into_iter().collect::<HashSet<_>>();
        if private_participants.is_empty() && public_participants.is_empty() {
            warn!(self.logger, "Empty participant list");
            return Ok(());
        }

        let (mut deferred_private, mut host_admitted_private, mut persisted_private) = run(async {
            let mut transaction = self.pool.begin().await?;
            let session = sqlx::query_as::<_, (u32,)>("SELECT creator_id FROM game_sessions WHERE id = ? AND type_id = ? AND destroyed_at IS NULL")
                .bind(session_id)
                .bind(type_id)
                .fetch_optional(&mut *transaction)
                .await?;
            let Some((creator_id,)) = session else {
                transaction.rollback().await?;
                return Ok::<(Vec<u32>, Vec<u32>, Vec<u32>), sqlx::Error>((Vec::new(), Vec::new(), Vec::new()));
            };

            for user_id in public_participants {
                sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .execute(&mut *transaction)
                    .await?;
            }

            let mut deferred_private = Vec::new();
            let mut host_admitted_private = Vec::new();
            let mut persisted_private = Vec::new();
            for user_id in private_participants {
                let pending_invite_sender = sqlx::query_as::<_, (u32,)>(
                    r"
                    SELECT sender FROM invites
                    WHERE receiver = ? AND kind IN (?, ?) AND session_type = ? AND session_id = ?
                      AND accepted_at IS NOT NULL AND consumed_at IS NULL AND expires_at > CURRENT_TIMESTAMP
                    ORDER BY rowid DESC LIMIT 1
                    ",
                )
                .bind(user_id)
                .bind(INVITE_KIND_PRIVATE_ROOM)
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .bind(type_id)
                .bind(session_id)
                .fetch_optional(&mut *transaction)
                .await?;
                let (already_joined,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM participants WHERE game_id = ? AND user_id = ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .fetch_one(&mut *transaction)
                    .await?;

                if let Some((invite_sender,)) = pending_invite_sender {
                    if already_joined == 0 {
                        if caller_id == creator_id && invite_sender == caller_id {
                            sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                                .bind(session_id)
                                .bind(user_id)
                                .execute(&mut *transaction)
                                .await?;
                            host_admitted_private.push(user_id);
                        } else {
                            deferred_private.push(user_id);
                        }
                        continue;
                    }
                }
                sqlx::query("INSERT OR IGNORE INTO participants (game_id, user_id) VALUES (?, ?)")
                    .bind(session_id)
                    .bind(user_id)
                    .execute(&mut *transaction)
                    .await?;
                persisted_private.push(user_id);
            }
            Self::reconcile_game_session_async(&mut transaction, type_id, session_id).await?;
            transaction.commit().await?;
            Ok::<(Vec<u32>, Vec<u32>, Vec<u32>), sqlx::Error>((deferred_private, host_admitted_private, persisted_private))
        })??;

        deferred_private.sort_unstable();
        host_admitted_private.sort_unstable();
        persisted_private.sort_unstable();
        if !deferred_private.is_empty() {
            info!(
                self.logger,
                "PrivateParticipantsDeferredUntilJoin type_id={type_id} session_id={session_id} participant_ids={deferred_private:?} invitation_kinds=[1,3] formal_membership=JoinSession"
            );
        }
        if !host_admitted_private.is_empty() {
            info!(self.logger, "PrivateInviteHostAdmissionPersisted caller_id={caller_id} session_creator_id={caller_id} type_id={type_id} session_id={session_id} participant_ids={host_admitted_private:?} membership_source=host-AddParticipants-after-P2P invitation_kinds=[1,3]");
        }
        if !persisted_private.is_empty() {
            info!(
                self.logger,
                "PrivateParticipantsPersisted type_id={type_id} session_id={session_id} participant_ids={persisted_private:?} accepted_invite_pending=false"
            );
        }
        Ok(())
    }

    pub async fn remove_participants_async(&self, type_id: u32, session_id: u32, participants: Vec<u32>) -> Result<()> {
        let mut transaction = self.pool.begin().await?;
        for participant_id in participants.into_iter().collect::<HashSet<_>>() {
            sqlx::query("DELETE FROM participants WHERE game_id = ? AND user_id = ?")
                .bind(session_id)
                .bind(participant_id)
                .execute(&mut *transaction)
                .await?;
        }
        Self::reconcile_game_session_async(&mut transaction, type_id, session_id).await?;
        transaction.commit().await?;
        Ok(())
    }

    pub fn remove_participants(&self, type_id: u32, session_id: u32, participants: Vec<u32>) -> Result<()> {
        run(self.remove_participants_async(type_id, session_id, participants))?
    }

    /// Deletes a host-owned session. When the deleted session is a game room,
    /// every non-host participant still present is invited back to the host's
    /// live lobby Party. Players who left or were removed before deletion are
    /// absent from the snapshot and are therefore never pulled back in.
    pub fn delete_game_session(&self, creator_id: u32, type_id: u32, session_id: u32) -> Result<(u64, u64)> {
        let result = run(async {
            let mut transaction = self.pool.begin().await?;
            let deleted_attributes =
                sqlx::query_as::<_, (String,)>("SELECT attributes FROM game_sessions WHERE creator_id = ? AND type_id = ? AND id = ? AND destroyed_at IS NULL")
                    .bind(creator_id)
                    .bind(type_id)
                    .bind(session_id)
                    .fetch_optional(&mut *transaction)
                    .await?;
            let Some((deleted_attributes,)) = deleted_attributes else {
                transaction.rollback().await?;
                return Ok::<(u64, u64), sqlx::Error>((0, 0));
            };

            let remaining_participants = sqlx::query_as::<_, (u32,)>("SELECT user_id FROM participants WHERE game_id = ? AND user_id != ? ORDER BY user_id")
                .bind(session_id)
                .bind(creator_id)
                .fetch_all(&mut *transaction)
                .await?;

            let deleted = sqlx::query("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE creator_id = ? AND type_id = ? AND id = ? AND destroyed_at IS NULL")
                .bind(creator_id)
                .bind(type_id)
                .bind(session_id)
                .execute(&mut *transaction)
                .await?
                .rows_affected();

            if deleted == 0 || !is_game_room_attributes(&deleted_attributes) || remaining_participants.is_empty() {
                transaction.commit().await?;
                return Ok((deleted, 0));
            }

            let lobby_candidates = sqlx::query_as::<_, (u32, String)>(
                r"
                SELECT g.id, g.attributes
                FROM game_sessions AS g
                INNER JOIN participants AS host
                    ON host.game_id = g.id AND host.user_id = g.creator_id
                WHERE g.creator_id = ?
                  AND g.type_id = ?
                  AND g.id != ?
                  AND g.destroyed_at IS NULL
                  AND EXISTS (SELECT 1 FROM station_urls AS endpoint WHERE endpoint.user_id = g.creator_id)
                ORDER BY g.id DESC
                ",
            )
            .bind(creator_id)
            .bind(type_id)
            .bind(session_id)
            .fetch_all(&mut *transaction)
            .await?;
            let Some((lobby_session_id, _)) = lobby_candidates.into_iter().find(|(_, attributes)| is_lobby_session_attributes(attributes)) else {
                transaction.commit().await?;
                return Ok((deleted, 0));
            };

            let party_id = Self::ensure_party_for_leader_async(&mut transaction, creator_id).await?;
            let mut queued = 0;
            for (receiver_id,) in remaining_participants {
                let (already_in_lobby,): (i64,) = sqlx::query_as("SELECT EXISTS(SELECT 1 FROM participants WHERE game_id = ? AND user_id = ?)")
                    .bind(lobby_session_id)
                    .bind(receiver_id)
                    .fetch_one(&mut *transaction)
                    .await?;
                if already_in_lobby != 0 {
                    continue;
                }
                sqlx::query("UPDATE invites SET consumed_at = CURRENT_TIMESTAMP WHERE receiver = ? AND kind = ? AND consumed_at IS NULL")
                    .bind(receiver_id)
                    .bind(INVITE_KIND_LOBBY_RESTORE)
                    .execute(&mut *transaction)
                    .await?;
                queued += sqlx::query(
                    r"
                    INSERT INTO invites (sender, receiver, kind, session_type, session_id, party_id, expires_at)
                    VALUES (?, ?, ?, ?, ?, ?, datetime('now', '+1 minute'))
                    ",
                )
                .bind(creator_id)
                .bind(receiver_id)
                .bind(INVITE_KIND_LOBBY_RESTORE)
                .bind(type_id)
                .bind(lobby_session_id)
                .bind(party_id)
                .execute(&mut *transaction)
                .await?
                .rows_affected();
            }
            transaction.commit().await?;
            Ok((deleted, queued))
        })??;
        if result.1 > 0 {
            info!(
                self.logger,
                "LobbyRestoreInvitesQueued host_id={creator_id} deleted_game_session_id={session_id} session_type={type_id} count={} kind={INVITE_KIND_LOBBY_RESTORE}", result.1
            );
        }
        Ok(result)
    }

    pub fn register_urls(&self, user_id: u32, urls: Vec<String>) -> Result<()> {
        let urls = urls.into_iter().collect::<HashSet<_>>();
        run(async {
            let mut transaction = self.pool.begin().await?;
            sqlx::query("DELETE FROM station_urls WHERE user_id = ?").bind(user_id).execute(&mut *transaction).await?;
            for url in urls {
                sqlx::query("INSERT OR IGNORE INTO station_urls (user_id, url) VALUES (?, ?)")
                    .bind(user_id)
                    .bind(url)
                    .execute(&mut *transaction)
                    .await?;
            }
            transaction.commit().await
        })??;
        Ok(())
    }

    pub async fn list_users_async(&self) -> Result<Vec<User>> {
        self.cleanup_stale_api_sessions_async().await?;
        Ok(sqlx::query_as(
            r"
            SELECT
                id,
                username,
                ubi_id,
                CASE
                    WHEN is_online = 1
                      OR last_api_seen_at >= datetime('now', '-15 seconds')
                    THEN 1
                    ELSE 0
                END AS is_online
            FROM users
            WHERE ubi_id IS NOT NULL
            ",
        )
        .fetch_all(&self.pool)
        .await?)
    }

    /// Returns active sessions currently hosted by the user, newest first.
    pub async fn find_host_sessions_async(&self, user_id: u32) -> Result<Vec<GameSession>> {
        Ok(sqlx::query_as(
            r"
            SELECT DISTINCT
                g.type_id AS session_type,
                g.id AS session_id,
                g.creator_id,
                g.attributes
            FROM game_sessions AS g
            INNER JOIN participants AS p
                ON p.game_id = g.id AND p.user_id = g.creator_id
            WHERE g.creator_id = ?
              AND g.destroyed_at IS NULL
              AND EXISTS (SELECT 1 FROM station_urls AS s WHERE s.user_id = g.creator_id)
            ORDER BY g.id DESC
            ",
        )
        .bind(user_id)
        .fetch_all(&self.pool)
        .await?)
    }

    /// Creates one pending invitation bound to the inviter's exact current
    /// session. `kind=1` is a private-room direct invite and `kind=2` is a
    /// lobby-party invite. Multiple receivers may point at the same session.
    pub async fn add_invite_async(&self, sender_id: u32, receiver_id: u32, kind: i32, session_type: u32, session_id: u32) -> Result<Option<i64>> {
        if !matches!(kind, INVITE_KIND_PRIVATE_ROOM | INVITE_KIND_LOBBY_PARTY) {
            return Ok(None);
        }
        info!(
            self.logger,
            "InviteCreated sender_id={sender_id} receiver_id={receiver_id} kind={kind} session_type={session_type} session_id={session_id}"
        );
        let mut transaction = self.pool.begin().await?;
        let party_id = if kind == INVITE_KIND_LOBBY_PARTY {
            Some(Self::ensure_party_for_leader_async(&mut transaction, sender_id).await?)
        } else {
            None
        };
        sqlx::query("DELETE FROM invites WHERE consumed_at IS NOT NULL OR expires_at IS NULL OR expires_at <= CURRENT_TIMESTAMP")
            .execute(&mut *transaction)
            .await?;
        sqlx::query("DELETE FROM invites WHERE receiver = ? AND kind IN (?, ?) AND consumed_at IS NULL")
            .bind(receiver_id)
            .bind(INVITE_KIND_PRIVATE_ROOM)
            .bind(INVITE_KIND_LOBBY_PARTY)
            .execute(&mut *transaction)
            .await?;
        let result = sqlx::query(
            r"
            INSERT INTO invites (sender, receiver, kind, session_type, session_id, party_id, expires_at)
            SELECT ?, ?, ?, ?, ?, ?, datetime('now', '+5 minutes')
            WHERE EXISTS (
                SELECT 1
                FROM game_sessions AS g
                INNER JOIN participants AS p
                    ON p.game_id = g.id AND p.user_id = g.creator_id
                WHERE g.id = ?
                  AND g.type_id = ?
                  AND g.creator_id = ?
                  AND g.destroyed_at IS NULL
                  AND EXISTS (SELECT 1 FROM station_urls AS s WHERE s.user_id = ?)
            )
            ",
        )
        .bind(sender_id)
        .bind(receiver_id)
        .bind(kind)
        .bind(session_type)
        .bind(session_id)
        .bind(party_id)
        .bind(session_id)
        .bind(session_type)
        .bind(sender_id)
        .bind(sender_id)
        .execute(&mut *transaction)
        .await?;
        if result.rows_affected() == 0 {
            transaction.rollback().await?;
            return Ok(None);
        }
        let invite_id = result.last_insert_rowid();
        transaction.commit().await?;
        Ok(Some(invite_id))
    }

    async fn cleanup_invalid_invites_async(transaction: &mut sqlx::Transaction<'_, sqlx::Sqlite>) -> sqlx::Result<()> {
        sqlx::query(
            r"
            DELETE FROM invites
            WHERE consumed_at IS NOT NULL
               OR expires_at IS NULL
               OR expires_at <= CURRENT_TIMESTAMP
               OR NOT EXISTS (
                   SELECT 1
                   FROM game_sessions AS g
                   INNER JOIN participants AS p
                       ON p.game_id = g.id AND p.user_id = g.creator_id
                   WHERE g.id = invites.session_id
                     AND g.type_id = invites.session_type
                     AND (
                         (invites.kind IN (1, 2, 4) AND g.creator_id = invites.sender)
                         OR (
                             invites.kind = 3
                             AND EXISTS (
                                 SELECT 1 FROM participants AS sender_participant
                                 WHERE sender_participant.game_id = g.id
                                   AND sender_participant.user_id = invites.sender
                             )
                         )
                     )
                     AND g.destroyed_at IS NULL
                     AND EXISTS (SELECT 1 FROM station_urls AS s WHERE s.user_id = g.creator_id)
               )
            ",
        )
        .execute(&mut **transaction)
        .await?;
        Ok(())
    }

    /// Delivers an unaccepted invitation repeatedly at a controlled interval.
    /// Delivery is not treated as acknowledgement; only AcceptInvite marks the
    /// invitation accepted, and only JoinSession consumes it.
    pub async fn take_invite_async(&self, user_id: u32) -> Result<Option<Invite>> {
        let mut transaction = self.pool.begin().await?;
        Self::cleanup_invalid_invites_async(&mut transaction).await?;
        let redundant_party_follows = sqlx::query(
            r"
            UPDATE invites
            SET consumed_at = CURRENT_TIMESTAMP
            WHERE receiver = ?
              AND kind = ?
              AND accepted_at IS NULL
              AND consumed_at IS NULL
              AND expires_at > CURRENT_TIMESTAMP
              AND EXISTS (
                  SELECT 1
                  FROM participants AS receiver_participant
                  WHERE receiver_participant.game_id = invites.session_id
                    AND receiver_participant.user_id = invites.receiver
              )
            ",
        )
        .bind(user_id)
        .bind(INVITE_KIND_PARTY_FOLLOW)
        .execute(&mut *transaction)
        .await?
        .rows_affected();
        let mut invite: Option<Invite> = sqlx::query_as(
            r"
            SELECT
                rowid AS id,
                sender,
                receiver,
                kind,
                session_type,
                session_id,
                COALESCE(delivery_count, 0) AS delivery_count
            FROM invites
            WHERE receiver = ?
              AND accepted_at IS NULL
              AND consumed_at IS NULL
              AND expires_at > CURRENT_TIMESTAMP
              AND (delivered_at IS NULL OR delivered_at <= datetime('now', '-3 seconds'))
              AND (kind != ? OR created <= datetime('now', '-2 seconds'))
            ORDER BY created DESC, rowid DESC
            LIMIT 1
            ",
        )
        .bind(user_id)
        .bind(INVITE_KIND_PARTY_FOLLOW)
        .fetch_optional(&mut *transaction)
        .await?;
        if let Some(invite_mut) = invite.as_mut() {
            sqlx::query("UPDATE invites SET delivered_at = CURRENT_TIMESTAMP, delivery_count = COALESCE(delivery_count, 0) + 1 WHERE rowid = ?")
                .bind(invite_mut.id)
                .execute(&mut *transaction)
                .await?;
            invite_mut.delivery_count += 1;
        }
        transaction.commit().await?;
        if redundant_party_follows > 0 {
            info!(
                self.logger,
                "PartyFollowInviteSuppressedAlreadyJoined receiver_id={user_id} count={redundant_party_follows} fallback_delay_seconds=2"
            );
        }
        Ok(invite)
    }

    /// Acknowledges a specific invitation for the authenticated receiver. The
    /// operation is idempotent so a retrying overlay cannot corrupt state.
    pub async fn accept_invite_async(&self, user_id: u32, invite_id: i64) -> Result<Option<Invite>> {
        let mut transaction = self.pool.begin().await?;
        Self::cleanup_invalid_invites_async(&mut transaction).await?;
        sqlx::query(
            r"
            UPDATE invites
            SET accepted_at = COALESCE(accepted_at, CURRENT_TIMESTAMP)
            WHERE rowid = ?
              AND receiver = ?
              AND consumed_at IS NULL
              AND expires_at > CURRENT_TIMESTAMP
            ",
        )
        .bind(invite_id)
        .bind(user_id)
        .execute(&mut *transaction)
        .await?;
        let invite: Option<Invite> = sqlx::query_as(
            r"
            SELECT
                rowid AS id,
                sender,
                receiver,
                kind,
                session_type,
                session_id,
                COALESCE(delivery_count, 0) AS delivery_count
            FROM invites
            WHERE rowid = ?
              AND receiver = ?
              AND accepted_at IS NOT NULL
              AND consumed_at IS NULL
              AND expires_at > CURRENT_TIMESTAMP
            ",
        )
        .bind(invite_id)
        .bind(user_id)
        .fetch_optional(&mut *transaction)
        .await?;

        let authorized_target = if let Some(invite) = invite.as_ref().filter(|invite| matches!(invite.kind, INVITE_KIND_PRIVATE_ROOM | INVITE_KIND_PARTY_FOLLOW)) {
            let (valid_target,): (i64,) = sqlx::query_as(
                r"
                SELECT EXISTS(
                    SELECT 1
                    FROM game_sessions AS g
                    INNER JOIN participants AS host
                        ON host.game_id = g.id AND host.user_id = g.creator_id
                    WHERE g.id = ?
                      AND g.type_id = ?
                      AND g.destroyed_at IS NULL
                      AND EXISTS (
                          SELECT 1 FROM station_urls AS endpoint
                          WHERE endpoint.user_id = g.creator_id
                      )
                      AND (
                          (? = ? AND g.creator_id = ?)
                          OR (
                              ? = ?
                              AND EXISTS (
                                  SELECT 1 FROM participants AS sender_participant
                                  WHERE sender_participant.game_id = g.id
                                    AND sender_participant.user_id = ?
                              )
                          )
                      )
                )
                ",
            )
            .bind(invite.session_id)
            .bind(invite.session_type)
            .bind(invite.kind)
            .bind(INVITE_KIND_PRIVATE_ROOM)
            .bind(invite.sender)
            .bind(invite.kind)
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .bind(invite.sender)
            .fetch_one(&mut *transaction)
            .await?;
            if valid_target == 0 {
                transaction.rollback().await?;
                return Ok(None);
            }

            // Accepting a private-room or party-follow invitation authorizes exact
            // discovery only. Persisting either lane before the authenticated receiver
            // completes its own JoinSession can emit PlayerJoin too early and leave the
            // retail client waiting forever in RDV StateJoin.
            let participant_persisted = false;
            Some((invite.kind, invite.session_type, invite.session_id, participant_persisted))
        } else {
            None
        };

        transaction.commit().await?;
        if let Some((kind, session_type, session_id, participant_persisted)) = authorized_target {
            info!(
                self.logger,
                "InviteParticipantAuthorized receiver_id={user_id} kind={kind} session_type={session_type} session_id={session_id} participant_persisted={participant_persisted}"
            );
        }
        Ok(invite)
    }

    /// Resolves the exact session associated with an accepted invitation. This
    /// deliberately ignores normal matchmaking attributes and query IDs: the
    /// authenticated receiver explicitly accepted this exact target.
    pub fn find_accepted_invited_session(&self, receiver_id: u32, session_type: u32) -> Result<Option<InvitedGameSession>> {
        run(self.find_accepted_invited_session_async(receiver_id, session_type))?
    }

    pub fn find_accepted_manual_invited_session(&self, receiver_id: u32, session_type: u32) -> Result<Option<InvitedGameSession>> {
        run(self.find_accepted_invited_session_filtered_async(receiver_id, session_type, true))?
    }

    pub async fn find_accepted_invited_session_async(&self, receiver_id: u32, session_type: u32) -> Result<Option<InvitedGameSession>> {
        self.find_accepted_invited_session_filtered_async(receiver_id, session_type, false).await
    }

    async fn find_accepted_invited_session_filtered_async(&self, receiver_id: u32, session_type: u32, manual_only: bool) -> Result<Option<InvitedGameSession>> {
        let row: Option<(i64, i32, u32, u32, u32, u32, String)> = sqlx::query_as(
            r"
            SELECT
                i.rowid AS invite_id,
                i.kind,
                i.sender,
                g.type_id AS session_type,
                g.id AS session_id,
                g.creator_id,
                g.attributes
            FROM invites AS i
            INNER JOIN game_sessions AS g
                ON g.id = i.session_id AND g.type_id = i.session_type
            INNER JOIN participants AS p
                ON p.game_id = g.id AND p.user_id = g.creator_id
            WHERE i.receiver = ?
              AND i.session_type = ?
              AND (? = 0 OR i.kind IN (1, 2, 4))
              AND i.accepted_at IS NOT NULL
              AND i.consumed_at IS NULL
              AND i.expires_at > CURRENT_TIMESTAMP
              AND (
                  (i.kind IN (1, 2, 4) AND g.creator_id = i.sender)
                  OR (
                      i.kind = 3
                      AND EXISTS (
                          SELECT 1 FROM participants AS sender_participant
                          WHERE sender_participant.game_id = g.id
                            AND sender_participant.user_id = i.sender
                      )
                  )
              )
              AND g.destroyed_at IS NULL
              AND EXISTS (SELECT 1 FROM station_urls AS s WHERE s.user_id = g.creator_id)
            ORDER BY i.created DESC, i.rowid DESC
            LIMIT 1
            ",
        )
        .bind(receiver_id)
        .bind(session_type)
        .bind(if manual_only { 1_i64 } else { 0_i64 })
        .fetch_optional(&self.pool)
        .await?;

        let Some((invite_id, kind, sender_id, session_type, session_id, creator_id, attributes)) = row else {
            return Ok(None);
        };
        let mut session = GameSession {
            session_type,
            session_id,
            creator_id,
            attributes,
            participants: sqlx::query_as(
                r"
                SELECT user_id, username AS name
                FROM participants AS p, users AS u
                WHERE u.id = user_id AND game_id = ?
                ORDER BY user_id
                ",
            )
            .bind(session_id)
            .fetch_all(&self.pool)
            .await?,
        };
        if kind == INVITE_KIND_PRIVATE_ROOM {
            let stored_attributes = session.attributes.clone();
            session.attributes = project_private_invite_session_class_only(&stored_attributes);
            info!(
                self.logger,
                "PrivateInviteClassOnlyCleanup receiver_id={receiver_id} session_type={session_type} session_id={session_id} public_slot_projection=false uplay_target_projection=false response_only=true"
            );
            info!(
                self.logger,
                "PrivateInviteClassProbe receiver_id={receiver_id} session_type={session_type} session_id={session_id} stored_113=0 projected_113=1 stored_attributes={stored_attributes} projected_attributes={} public_slot_unchanged=true uplay_id_unchanged=true response_only=true",
                session.attributes
            );
        }

        for participant in &mut session.participants {
            participant.station_urls = sqlx::query_as("SELECT url FROM station_urls WHERE user_id = ? ORDER BY url")
                .bind(participant.user_id)
                .fetch_all(&self.pool)
                .await?
                .into_iter()
                .map(|row: (String,)| row.0)
                .collect();
        }
        Ok(Some(InvitedGameSession {
            invite_id,
            kind,
            sender_id,
            session,
        }))
    }

    pub fn search_sessions_with_participants(&self, type_id: u32, participant_ids: &[u32]) -> Result<Vec<GameSession>> {
        run(self.search_sessions_with_participants_async(type_id, participant_ids))?
    }

    pub async fn search_sessions_with_participants_async(&self, type_id: u32, participant_ids: &[u32]) -> Result<Vec<GameSession>> {
        let mut sessions: Vec<GameSession> = if participant_ids.is_empty() {
            let query = sqlx::query_as(
                r"SELECT
                        g.type_id as session_type,
                        g.id as session_id,
                        g.creator_id,
                        g.attributes
                    FROM game_sessions AS g
                    WHERE g.type_id = ? AND g.destroyed_at IS NULL
                    ORDER BY g.id",
            )
            .bind(type_id);
            info!(self.logger, "Searching all active sessions: {}", query.sql());
            query.fetch_all(&self.pool).await?
        } else {
            let placeholders = std::iter::repeat('?').take(participant_ids.len()).intersperse(',').collect::<String>();
            let sql = format!(
                r"SELECT
                        g.type_id as session_type,
                        g.id as session_id,
                        g.creator_id,
                        g.attributes
                    FROM game_sessions AS g
                    WHERE g.type_id = ? AND g.destroyed_at IS NULL AND g.id IN (
                        SELECT game_id FROM participants WHERE user_id IN ({placeholders})
                    )
                    ORDER BY g.id"
            );
            let mut query = sqlx::query_as(&sql).bind(type_id);
            for participant_id in participant_ids {
                query = query.bind(participant_id);
            }
            info!(self.logger, "Searching sessions with participants: {}", query.sql());
            query.fetch_all(&self.pool).await?
        };

        for session in &mut sessions {
            session.participants = sqlx::query_as(
                r"
                SELECT user_id, username as name
                FROM participants p, users u
                WHERE u.id = user_id AND game_id = ?
                ORDER BY user_id
                ",
            )
            .bind(session.session_id)
            .fetch_all(&self.pool)
            .await?;

            for participant in &mut session.participants {
                participant.station_urls = sqlx::query_as("SELECT url FROM station_urls WHERE user_id = ? ORDER BY url")
                    .bind(participant.user_id)
                    .fetch_all(&self.pool)
                    .await?
                    .into_iter()
                    .map(|row: (String,)| row.0)
                    .collect();
            }
        }

        sessions.retain(|session| {
            session
                .participants
                .iter()
                .any(|participant| participant.user_id == session.creator_id && !participant.station_urls.is_empty())
        });
        Ok(sessions)
    }

    pub async fn delete_user_async(&self, user_id: u32) -> Result<()> {
        sqlx::query("DELETE FROM users WHERE id = ?").bind(user_id).execute(&self.pool).await?;
        Ok(())
    }

    pub async fn list_urls(&self, user_id: u32) -> Result<Vec<String>> {
        Ok(sqlx::query_as("SELECT url FROM station_urls WHERE user_id = ?")
            .bind(user_id)
            .fetch_all(&self.pool)
            .await?
            .into_iter()
            .map(|r: (String,)| r.0)
            .collect())
    }

    pub async fn list_game_sessions_async(&self) -> Result<Vec<GameSession>> {
        let mut sessions: Vec<GameSession> = sqlx::query_as(
            r"
        SELECT
            g.type_id as session_type,
            g.id as session_id,
            g.creator_id,
            g.attributes
        FROM game_sessions AS g
        WHERE destroyed_at IS NULL
        ",
        )
        .fetch_all(&self.pool)
        .await?;

        for session in &mut sessions {
            session.participants = sqlx::query_as(
                r"
                SELECT
                    user_id,
                    username as name
                FROM participants p, users u
                WHERE u.id = user_id AND game_id = ?
                ",
            )
            .bind(session.session_id)
            .fetch_all(&self.pool)
            .await?;

            for participant in &mut session.participants {
                participant.station_urls = sqlx::query_as(
                    r"
                    SELECT url
                    FROM station_urls
                    WHERE user_id = ?
                    ",
                )
                .bind(participant.user_id)
                .fetch_all(&self.pool)
                .await?
                .into_iter()
                .map(|r: (String,)| r.0)
                .collect();
            }
        }
        Ok(sessions)
    }

    pub async fn delete_game_session_by_id_async(&self, session_id: u32) -> Result<()> {
        sqlx::query("UPDATE game_sessions SET destroyed_at=CURRENT_TIMESTAMP WHERE id = ?")
            .bind(session_id)
            .execute(&self.pool)
            .await?;
        Ok(())
    }
}

#[derive(Debug, sqlx::FromRow)]
pub struct User {
    pub id: u32,
    pub username: String,
    pub ubi_id: String,
    pub is_online: bool,
}

#[derive(Debug, sqlx::FromRow)]
pub struct GameSession {
    pub session_type: u32,
    pub session_id: u32,
    pub creator_id: u32,
    pub attributes: String,
    #[sqlx(skip)]
    pub participants: Vec<Participant>,
}

#[derive(Debug, sqlx::FromRow)]
pub struct Participant {
    pub user_id: u32,
    pub name: String,
    #[sqlx(skip)]
    pub station_urls: Vec<String>,
}

#[derive(Debug)]
pub struct InvitedGameSession {
    pub invite_id: i64,
    pub kind: i32,
    pub sender_id: u32,
    pub session: GameSession,
}

#[derive(Debug, Clone, Copy, sqlx::FromRow)]
pub struct Invite {
    pub id: i64,
    pub sender: u32,
    pub receiver: u32,
    pub kind: i32,
    pub session_type: u32,
    pub session_id: u32,
    pub delivery_count: i64,
}

#[cfg(test)]
mod tests {
    use std::time::SystemTime;
    use std::time::UNIX_EPOCH;

    use super::*;

    fn create_test_storage(name: &str) -> Storage {
        let unique = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let path = std::env::temp_dir().join(format!("5th-echelon-{name}-{}-{unique}.db", std::process::id()));
        let url = format!("sqlite://{}?mode=rwc", path.display());
        Storage::init_with_database_url(Logger::root(slog::Discard, slog::o!()), &url).unwrap()
    }

    fn insert_users(storage: &Storage, count: u32) {
        run(async {
            for user_id in 1..=count {
                sqlx::query("INSERT OR REPLACE INTO users (id, username, password_hash, ubi_id) VALUES (?, ?, ?, ?)")
                    .bind(user_id)
                    .bind(format!("user-{user_id}"))
                    .bind("test-password-hash")
                    .bind(format!("ubi-{user_id}"))
                    .execute(&storage.pool)
                    .await?;
            }
            Ok::<(), sqlx::Error>(())
        })
        .unwrap()
        .unwrap();
    }

    #[test]
    fn join_session_records_eight_participants_without_capacity_gate() {
        let storage = create_test_storage("join-eight-participants");
        insert_users(&storage, 8);
        for user_id in 1..=8 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let session_id = storage.create_game_session(1, 42, "101 => 1".to_string()).unwrap();
        for user_id in 2..=8 {
            assert!(storage.join_game_session_unbounded(user_id, 42, session_id).unwrap());
        }
        // A repeated JoinSession and a later AddParticipants callback must not
        // inflate the participant count.
        assert!(storage.join_game_session_unbounded(8, 42, session_id).unwrap());
        storage.add_participants(1, 42, session_id, Vec::new(), vec![8]).unwrap();

        let (_, _, participant_ids) = storage.game_session_diagnostics(42, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, (1..=8).collect::<Vec<_>>());
        let sessions_with_eighth = storage.search_sessions_with_participants(42, &[8]).unwrap();
        assert_eq!(sessions_with_eighth.len(), 1);
        assert_eq!(sessions_with_eighth[0].participants.len(), 8);

        assert!(!storage.join_game_session_unbounded(8, 42, session_id + 10_000).unwrap());
    }

    #[test]
    fn supports_eight_participants_without_capacity_gate() {
        let storage = create_test_storage("eight-participants");
        insert_users(&storage, 8);
        for user_id in 1..=8 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let session_id = storage.create_game_session(1, 42, "101 => 1".to_string()).unwrap();
        for user_id in 2..=8 {
            storage.add_participants(1, 42, session_id, Vec::new(), vec![user_id]).unwrap();
        }

        let (_, _, participant_ids) = storage.game_session_diagnostics(42, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, (1..=8).collect::<Vec<_>>());
        assert!(storage.game_session_is_active(42, session_id).unwrap());

        let all_sessions = storage.search_sessions_with_participants(42, &[]).unwrap();
        assert_eq!(all_sessions.len(), 1);
        assert_eq!(all_sessions[0].participants.len(), 8);
        let sessions_with_eighth = storage.search_sessions_with_participants(42, &[8]).unwrap();
        assert_eq!(sessions_with_eighth.len(), 1);
    }

    #[test]
    fn leave_migrates_host_and_final_leave_destroys_session() {
        let storage = create_test_storage("host-migration");
        insert_users(&storage, 3);
        let session_id = storage.create_game_session(1, 7, String::new()).unwrap();
        storage.add_participants(1, 7, session_id, Vec::new(), vec![2, 3]).unwrap();

        storage.leave_game_session(1, 7, session_id).unwrap();
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 2);
        assert_eq!(participant_ids, vec![2, 3]);

        storage.leave_game_session(2, 7, session_id).unwrap();
        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(7, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 3);
        assert_eq!(participant_ids, vec![3]);

        storage.leave_game_session(3, 7, session_id).unwrap();
        assert!(!storage.game_session_is_active(7, session_id).unwrap());
        assert!(storage.game_session_diagnostics(7, session_id).unwrap().is_none());
    }

    #[test]
    fn split_restores_callers_own_just_abandoned_session() {
        let storage = create_test_storage("split-restore-abandoned");
        insert_users(&storage, 2);
        let session_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        storage.leave_game_session(1, 1, session_id).unwrap();
        assert!(!storage.game_session_is_active(1, session_id).unwrap());
        assert!(!storage.restore_abandoned_game_session_for_split(2, 1, session_id).unwrap());
        assert!(storage.restore_abandoned_game_session_for_split(1, 1, session_id).unwrap());

        let (creator_id, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(creator_id, 1);
        assert_eq!(participant_ids, vec![1]);
    }

    #[test]
    fn deleting_hosted_game_queues_remaining_player_back_to_live_lobby() {
        let storage = create_test_storage("host-delete-lobby-restore");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());

        assert_eq!(storage.delete_game_session(1, 1, game_id).unwrap(), (1, 1));
        let restore = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(restore.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(restore.session_id, lobby_id);
        assert!(run(storage.accept_invite_async(2, restore.id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.kind, INVITE_KIND_LOBBY_RESTORE);
        assert_eq!(target.session.session_id, lobby_id);
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let (_, _, participant_ids) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2]);
    }

    #[test]
    fn deleting_hosted_game_does_not_restore_player_who_already_left() {
        let storage = create_test_storage("host-delete-no-restore-after-leave");
        insert_users(&storage, 2);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.1;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let game_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, game_id).unwrap());
        storage.leave_game_session(2, 1, game_id).unwrap();

        assert_eq!(storage.delete_game_session(1, 1, game_id).unwrap(), (1, 0));
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn registering_urls_replaces_stale_endpoints() {
        let storage = create_test_storage("replace-urls");
        insert_users(&storage, 1);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.5;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(1, vec!["prudp:/address=10.66.0.6;port=13000;sid=14;type=2".to_string()]).unwrap();
        let urls = run(storage.list_urls(1)).unwrap().unwrap();
        assert_eq!(urls, vec!["prudp:/address=10.66.0.6;port=13000;sid=14;type=2"]);
    }

    #[test]
    fn private_participant_add_persists_private_membership() {
        let storage = create_test_storage("private-participant-membership");
        insert_users(&storage, 3);
        let session_id = storage.create_game_session(1, 1, "113 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();

        storage.add_participants(1, 1, session_id, vec![2], Vec::new()).unwrap();
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2]);

        storage.add_participants(1, 1, session_id, Vec::new(), vec![3]).unwrap();
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, session_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3]);
    }

    #[test]
    fn private_room_invite_survives_delivery_acceptance_and_repeated_search_until_join() {
        let storage = create_test_storage("private-room-invite");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;3 => 0;4 => 8;112 => 4".to_string()).unwrap();
        let host_sessions = run(storage.find_host_sessions_async(1)).unwrap().unwrap();
        assert_eq!(host_sessions.iter().map(|session| session.session_id).collect::<Vec<_>>(), vec![private_id, lobby_id]);

        let invite_id = run(storage.add_invite_async(1, 2, 1, 1, private_id)).unwrap().unwrap().unwrap();
        let delivered = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(delivered.id, invite_id);
        assert_eq!(delivered.kind, 1);
        assert_eq!(delivered.delivery_count, 1);
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());

        let accepted = run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().unwrap();
        assert_eq!(accepted.receiver, 2);
        let (_, _, authorized_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(authorized_participants, vec![1]);
        storage.add_participants(1, 1, private_id, vec![2], Vec::new()).unwrap();
        let (_, _, deferred_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(deferred_participants, vec![1, 2]);
        let first = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        let second = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        assert_eq!(first.invite_id, invite_id);
        assert_eq!(first.kind, 1);
        assert_eq!(first.sender_id, 1);
        assert_eq!(first.session.session_id, private_id);
        assert_eq!(first.session.participants.iter().map(|participant| participant.user_id).collect::<Vec<_>>(), vec![1, 2]);
        assert_eq!(second.session.session_id, private_id);
        assert_eq!(session_attribute_value(&first.session.attributes, 113), Some(1));
        assert_eq!(session_attribute_value(&second.session.attributes, 113), Some(1));
        assert_eq!(session_attribute_value(&first.session.attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&second.session.attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&first.session.attributes, 4), Some(8));
        assert_eq!(session_attribute_value(&second.session.attributes, 4), Some(8));
        let (_, stored_attributes, stored_participants) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(session_attribute_value(&stored_attributes, 113), Some(0));
        assert_eq!(session_attribute_value(&stored_attributes, 3), Some(0));
        assert_eq!(session_attribute_value(&stored_attributes, 4), Some(8));
        assert_eq!(stored_participants, vec![1, 2]);
        assert!(run(storage.find_accepted_invited_session_async(3, 1)).unwrap().unwrap().is_none());

        assert!(storage.join_game_session_unbounded(2, 1, private_id).unwrap());
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2]);
    }

    #[test]
    fn lobby_invites_allow_many_receivers_to_join_the_same_party_session() {
        let storage = create_test_storage("lobby-party-invite");
        insert_users(&storage, 4);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();

        for receiver in 2..=4 {
            let invite_id = run(storage.add_invite_async(1, receiver, 2, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert_eq!(run(storage.take_invite_async(receiver)).unwrap().unwrap().unwrap().kind, 2);
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            let target = run(storage.find_accepted_invited_session_async(receiver, 1)).unwrap().unwrap().unwrap();
            assert_eq!(target.kind, 2);
            assert_eq!(target.session.session_id, lobby_id);
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let (_, _, participant_ids) = storage.game_session_diagnostics(1, lobby_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3, 4]);
    }

    #[test]
    fn invite_rejects_wrong_host_and_expires_safely() {
        let storage = create_test_storage("invite-validation");
        insert_users(&storage, 3);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        storage.register_urls(3, vec!["prudp:/address=10.66.0.4;port=13000;sid=14;type=2".to_string()]).unwrap();
        let other_host_session = storage.create_game_session(3, 1, "113 => 0".to_string()).unwrap();
        assert!(run(storage.add_invite_async(1, 2, 1, 1, other_host_session)).unwrap().unwrap().is_none());

        let own_session = storage.create_game_session(1, 1, "113 => 0".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, 1, 1, own_session)).unwrap().unwrap().unwrap();
        run(async {
            sqlx::query("UPDATE invites SET expires_at = datetime('now', '-1 second') WHERE receiver = 2")
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_none());
        assert!(run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().is_none());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());
    }

    #[test]
    fn api_heartbeat_preserves_idle_secure_session_until_heartbeat_stops() {
        let storage = create_test_storage("api-heartbeat");
        insert_users(&storage, 1);
        storage.register_urls(1, vec!["prudp:/address=10.66.0.2;port=13000;sid=14;type=2".to_string()]).unwrap();
        let lobby_id = storage.create_game_session(1, 1, "113 => 1".to_string()).unwrap();
        run(storage.touch_api_presence_async(1)).unwrap().unwrap();

        assert!(storage.close_secure_session(1).unwrap());
        assert!(storage.game_session_is_active(1, lobby_id).unwrap());
        assert!(!run(storage.list_urls(1)).unwrap().unwrap().is_empty());
        let users = run(storage.list_users_async()).unwrap().unwrap();
        assert!(users.iter().any(|user| user.id == 1 && user.is_online));

        run(async {
            sqlx::query("UPDATE users SET last_api_seen_at = datetime('now', '-30 seconds') WHERE id = 1")
                .execute(&storage.pool)
                .await?;
            storage.cleanup_stale_api_sessions_async().await?;
            Ok::<(), eyre::Error>(())
        })
        .unwrap()
        .unwrap();
        assert!(!storage.game_session_is_active(1, lobby_id).unwrap());
        assert!(run(storage.list_urls(1)).unwrap().unwrap().is_empty());
    }

    #[test]
    fn lobby_party_follows_leader_into_created_private_room() {
        let storage = create_test_storage("party-follow-private");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        for receiver in 2..=3 {
            let invite_id = run(storage.add_invite_async(1, receiver, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
            assert!(run(storage.take_invite_async(receiver)).unwrap().unwrap().is_some());
            assert!(run(storage.accept_invite_async(receiver, invite_id)).unwrap().unwrap().is_some());
            assert!(storage.join_game_session_unbounded(receiver, 1, lobby_id).unwrap());
        }

        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        run(async {
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE kind = ?")
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();
        for receiver in 2..=3 {
            let follow = run(storage.take_invite_async(receiver)).unwrap().unwrap().unwrap();
            assert_eq!(follow.kind, INVITE_KIND_PARTY_FOLLOW);
            assert!(run(storage.accept_invite_async(receiver, follow.id)).unwrap().unwrap().is_some());
            storage.add_participants(1, 1, private_id, vec![receiver], Vec::new()).unwrap();
            let target = run(storage.find_accepted_invited_session_async(receiver, 1)).unwrap().unwrap().unwrap();
            assert!(target.session.participants.iter().any(|participant| participant.user_id == receiver));
            assert_eq!(target.kind, INVITE_KIND_PARTY_FOLLOW);
            assert_eq!(target.session.session_id, private_id);
            assert!(storage.join_game_session_unbounded(receiver, 1, private_id).unwrap());
        }
        let (_, _, participant_ids) = storage.game_session_diagnostics(1, private_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 3]);
    }

    #[test]
    fn lobby_party_follows_leader_into_quick_match_hosted_by_another_user() {
        let storage = create_test_storage("party-follow-quick-match");
        insert_users(&storage, 4);
        for user_id in 1..=4 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let invite_id = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_some());
        assert!(run(storage.accept_invite_async(2, invite_id)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let match_id = storage.create_game_session(4, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(1, 1, match_id).unwrap());
        run(async {
            sqlx::query("UPDATE invites SET created = datetime('now', '-3 seconds') WHERE kind = ?")
                .bind(INVITE_KIND_PARTY_FOLLOW)
                .execute(&storage.pool)
                .await
        })
        .unwrap()
        .unwrap();

        let follow = run(storage.take_invite_async(2)).unwrap().unwrap().unwrap();
        assert_eq!(follow.kind, INVITE_KIND_PARTY_FOLLOW);
        assert_eq!(follow.sender, 1);
        assert!(run(storage.accept_invite_async(2, follow.id)).unwrap().unwrap().is_some());
        let target = run(storage.find_accepted_invited_session_async(2, 1)).unwrap().unwrap().unwrap();
        assert_eq!(target.session.session_id, match_id);
        assert_eq!(target.session.creator_id, 4);
        assert!(storage.join_game_session_unbounded(2, 1, match_id).unwrap());

        let (_, _, participant_ids) = storage.game_session_diagnostics(1, match_id).unwrap().unwrap();
        assert_eq!(participant_ids, vec![1, 2, 4]);
    }

    #[test]
    fn native_party_follow_suppresses_redundant_fallback_invite() {
        let storage = create_test_storage("party-follow-native-suppression");
        insert_users(&storage, 2);
        for user_id in 1..=2 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }
        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let private_id = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(storage.join_game_session_unbounded(2, 1, private_id).unwrap());
        assert!(run(storage.take_invite_async(2)).unwrap().unwrap().is_none());

        let (consumed,): (i64,) = run(sqlx::query_as("SELECT COUNT(*) FROM invites WHERE receiver = 2 AND kind = ? AND consumed_at IS NOT NULL")
            .bind(INVITE_KIND_PARTY_FOLLOW)
            .fetch_one(&storage.pool))
        .unwrap()
        .unwrap();
        assert_eq!(consumed, 1);
    }

    #[test]
    fn manual_and_party_follow_invites_use_independent_lanes() {
        let storage = create_test_storage("invite-lane-isolation");
        insert_users(&storage, 3);
        for user_id in 1..=3 {
            storage
                .register_urls(user_id, vec![format!("prudp:/address=10.66.0.{user_id};port=13000;sid=14;type=2")])
                .unwrap();
        }

        let lobby_id = storage.create_game_session(1, 1, "113 => 1;3 => 8;4 => 0".to_string()).unwrap();
        let lobby_invite = run(storage.add_invite_async(1, 2, INVITE_KIND_LOBBY_PARTY, 1, lobby_id)).unwrap().unwrap().unwrap();
        assert!(run(storage.accept_invite_async(2, lobby_invite)).unwrap().unwrap().is_some());
        assert!(storage.join_game_session_unbounded(2, 1, lobby_id).unwrap());

        let friend_room = storage.create_game_session(3, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_PRIVATE_ROOM, 1, friend_room)).unwrap().unwrap().is_some());

        let leader_room = storage.create_game_session(1, 1, "113 => 0;103 => 0;112 => 4".to_string()).unwrap();
        let pending =
            run(sqlx::query_as::<_, (i32, u32)>("SELECT kind, session_id FROM invites WHERE receiver = 2 AND consumed_at IS NULL ORDER BY kind").fetch_all(&storage.pool))
                .unwrap()
                .unwrap();
        assert_eq!(pending, vec![(INVITE_KIND_PRIVATE_ROOM, friend_room), (INVITE_KIND_PARTY_FOLLOW, leader_room)]);

        assert!(run(storage.add_invite_async(3, 2, INVITE_KIND_PRIVATE_ROOM, 1, friend_room)).unwrap().unwrap().is_some());
        let pending =
            run(sqlx::query_as::<_, (i32, u32)>("SELECT kind, session_id FROM invites WHERE receiver = 2 AND consumed_at IS NULL ORDER BY kind").fetch_all(&storage.pool))
                .unwrap()
                .unwrap();
        assert_eq!(pending, vec![(INVITE_KIND_PRIVATE_ROOM, friend_room), (INVITE_KIND_PARTY_FOLLOW, leader_room)]);
    }

    #[test]
    fn manual_invitation_search_intent_can_be_armed_and_cleared() {
        let storage = create_test_storage("manual-invite-search-intent");
        assert!(!storage.manual_invitation_search_is_active(2, 1));
        storage.note_manual_invitation_search(2, 1);
        assert!(storage.manual_invitation_search_is_active(2, 1));
        storage.clear_manual_invitation_search(2, 1);
        assert!(!storage.manual_invitation_search_is_active(2, 1));
    }
}
