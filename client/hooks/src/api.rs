use std::collections::HashMap;
use std::future::Future;
use std::sync::Mutex;
use std::sync::OnceLock;

use server_api::friends::friends_client::FriendsClient;
use server_api::friends::InviteRequest;
use server_api::friends::ListRequest;
use server_api::misc::misc_client::MiscClient;
use server_api::misc::AcceptInviteRequest;
use server_api::misc::ClearGameSessionRequest;
use server_api::misc::EventRequest;
use server_api::misc::EventResponse;
use server_api::misc::SetGameSessionRequest;
use server_api::misc::UplayGameSession;
use server_api::users::users_client::UsersClient;
use server_api::users::LoginRequest;
use tonic::metadata::Ascii;
use tonic::metadata::MetadataValue;
use tracing::debug;
use tracing::error;
use tracing::info;
use tracing::instrument;

static TOKEN: Mutex<Option<MetadataValue<Ascii>>> = Mutex::new(None);
static CREDS: Mutex<Option<(String, String)>> = Mutex::new(None);
static LOCAL_GAME_SESSION: Mutex<Option<UplayGameSession>> = Mutex::new(None);

struct InviteIdState {
    next_local_id: i64,
    server_to_current: HashMap<i64, (i64, bool)>,
    local_to_server: HashMap<i64, i64>,
}

impl InviteIdState {
    fn new() -> Self {
        Self {
            next_local_id: -1,
            server_to_current: HashMap::new(),
            local_to_server: HashMap::new(),
        }
    }

    fn allocate_local_id(&mut self) -> i64 {
        while self.local_to_server.contains_key(&self.next_local_id) {
            self.next_local_id = self.next_local_id.saturating_sub(1);
        }
        let local_id = self.next_local_id;
        self.next_local_id = self.next_local_id.saturating_sub(1);
        local_id
    }

    fn map_server_event(&mut self, server_id: i64) -> i64 {
        if let Some((local_id, retired)) = self.server_to_current.get(&server_id).copied() {
            if !retired {
                return local_id;
            }
        }

        let local_id = self.allocate_local_id();
        self.server_to_current.insert(server_id, (local_id, false));
        self.local_to_server.insert(local_id, server_id);
        local_id
    }

    fn server_id_for_local(&self, local_id: i64) -> i64 {
        self.local_to_server.get(&local_id).copied().unwrap_or(local_id)
    }

    fn retire_local(&mut self, local_id: i64) {
        let Some(server_id) = self.local_to_server.get(&local_id).copied() else {
            return;
        };
        if let Some((current_local_id, retired)) = self.server_to_current.get_mut(&server_id) {
            if *current_local_id == local_id {
                *retired = true;
            }
        }
    }
}

static INVITE_ID_STATE: OnceLock<Mutex<InviteIdState>> = OnceLock::new();

fn invite_id_state() -> &'static Mutex<InviteIdState> {
    INVITE_ID_STATE.get_or_init(|| Mutex::new(InviteIdState::new()))
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error: {0}")]
    IO(#[from] std::io::Error),
    #[error("Missing URL for the api server")]
    MissingUrl,
    #[error("Transport error: {0}")]
    Transport(#[from] tonic::transport::Error),
    #[error("gRPC error: {0}")]
    GRPCStatus(#[from] tonic::Status),
    #[error("Login failure")]
    LoginFailure,
    #[error("Login failure")]
    InvalidToken(#[from] tonic::metadata::errors::InvalidMetadataValue),
    #[error("Not connected")]
    NotConnected,
}

static CONNECTION: OnceLock<tonic::transport::Channel> = OnceLock::new();

async fn create_channel() -> std::result::Result<tonic::transport::Channel, Error> {
    if let Some(channel) = CONNECTION.get() {
        tracing::debug!("Reusing connection {channel:?}");
        return Ok(channel.clone());
    }

    let Some(url) = crate::config::URL.get() else {
        return Err(Error::MissingUrl);
    };
    tracing::debug!("Connecting to {url}");
    let channel = tonic::transport::Channel::from_shared(url.as_str())
        .unwrap()
        .connect_timeout(std::time::Duration::from_secs(1))
        .timeout(std::time::Duration::from_secs(10))
        .connect()
        .await?;
    tracing::debug!("Connected to {url}");
    if CONNECTION.set(channel).is_err() {
        tracing::warn!("API connection was already set before");
    }
    CONNECTION.get().cloned().ok_or(Error::NotConnected)
}

macro_rules! connect {
    ($client:ident) => {{
        let channel = create_channel().await?;
        $client::with_interceptor(channel, move |mut req: tonic::Request<_>| {
            let guard = TOKEN.lock().unwrap();
            if let Some(token) = (*guard).as_ref() {
                tracing::debug!("Adding auth token");
                req.metadata_mut().insert("authorization", token.clone());
            }
            Ok(req)
        })
    }};
}

pub struct Friend {
    pub id: String,
    pub username: String,
    pub is_online: bool,
}

static RUNTIME: OnceLock<tokio::runtime::Runtime> = OnceLock::new();

pub fn runtime() -> Result<&'static tokio::runtime::Runtime, Error> {
    if let Some(runtime) = RUNTIME.get() {
        return Ok(runtime);
    }
    let _ = RUNTIME.set(tokio::runtime::Runtime::new()?);
    Ok(RUNTIME.get().unwrap())
}

fn run<T>(future: impl Future<Output = Result<T, Error>>) -> Result<T, Error> {
    runtime()?.block_on(future)
}

pub fn invite_friend(id: &str) -> Result<(), Error> {
    run(async {
        let mut client = connect!(FriendsClient);
        let request = tonic::Request::new(InviteRequest { id: id.into() });
        client.invite(request).await?;
        Ok(())
    })
}

async fn accept_server_invite(server_id: i64) -> Result<bool, Error> {
    let mut client = connect!(MiscClient);
    let request = tonic::Request::new(AcceptInviteRequest { id: server_id });
    Ok(client.accept_invite(request).await?.into_inner().accepted)
}

pub fn accept_invite(local_id: i64) -> Result<(bool, i64), Error> {
    let server_id = invite_id_state().lock().unwrap().server_id_for_local(local_id);
    let result = run(accept_server_invite(server_id));
    if result.is_ok() {
        invite_id_state().lock().unwrap().retire_local(local_id);
    }
    result.map(|accepted| (accepted, server_id))
}

pub fn server_invite_id(local_id: i64) -> i64 {
    invite_id_state().lock().unwrap().server_id_for_local(local_id)
}

async fn publish_game_session_remote(session: UplayGameSession) -> Result<(), Error> {
    let mut client = connect!(MiscClient);
    client
        .set_game_session(tonic::Request::new(SetGameSessionRequest {
            id: session.id,
            data: session.data,
            flags: session.flags,
            invite_only: session.invite_only,
        }))
        .await?;
    Ok(())
}

async fn clear_game_session_remote() -> Result<(), Error> {
    let mut client = connect!(MiscClient);
    client.clear_game_session(tonic::Request::new(ClearGameSessionRequest {})).await?;
    Ok(())
}

pub fn publish_game_session(id: u64, data: Vec<u8>, flags: u32, invite_only: bool) {
    let session = UplayGameSession { id, data, flags, invite_only };
    *LOCAL_GAME_SESSION.lock().unwrap() = Some(session.clone());
    match runtime() {
        Ok(runtime) => {
            runtime.spawn(async move {
                match publish_game_session_remote(session).await {
                    Ok(()) => info!("UplayGameSessionPublishSucceeded"),
                    Err(error) => tracing::warn!("UplayGameSessionPublishDeferred error={error}"),
                }
            });
        }
        Err(error) => tracing::warn!("UplayGameSessionPublishRuntimeUnavailable error={error}"),
    }
}

pub fn clear_game_session() {
    *LOCAL_GAME_SESSION.lock().unwrap() = None;
    match runtime() {
        Ok(runtime) => {
            runtime.spawn(async {
                if let Err(error) = clear_game_session_remote().await {
                    tracing::warn!("UplayGameSessionClearDeferred error={error}");
                }
            });
        }
        Err(error) => tracing::warn!("UplayGameSessionClearRuntimeUnavailable error={error}"),
    }
}

async fn republish_local_game_session() {
    let session = LOCAL_GAME_SESSION.lock().unwrap().clone();
    if let Some(session) = session {
        match publish_game_session_remote(session).await {
            Ok(()) => info!("UplayGameSessionRepublishedAfterLogin"),
            Err(error) => tracing::warn!("UplayGameSessionRepublishFailed error={error}"),
        }
    }
}

pub fn list_friends() -> Result<Vec<Friend>, Error> {
    run(async {
        let mut client = connect!(FriendsClient);
        let request = tonic::Request::new(ListRequest {});
        let response = client.list(request).await?.into_inner();
        Ok(response
            .friends
            .into_iter()
            .map(|friend| Friend {
                id: friend.id,
                username: friend.username,
                is_online: friend.is_online,
            })
            .collect())
    })
}

async fn login_async(username: &str, password: &str) -> Result<(), Error> {
    {
        let mut guard = CREDS.lock().unwrap();
        *guard = Some((String::from(username), String::from(password)));
    }

    let mut client = connect!(UsersClient);
    let request = tonic::Request::new(LoginRequest {
        username: String::from(username),
        password: String::from(password),
    });

    debug!("logging in");
    let response = client.login(request).await?.into_inner();
    if !response.error.is_empty() {
        error!("Login error: {}", response.error);
        return Err(Error::LoginFailure);
    }
    if !response.token.is_empty() {
        info!("Login successful");
        let mut guard = TOKEN.lock().unwrap();
        *guard = Some(response.token.parse()?);
    }
    republish_local_game_session().await;
    Ok(())
}

#[instrument(skip(password))]
pub fn login(username: &str, password: &str) -> Result<(), Error> {
    let result = run(login_async(username, password));
    if let Err(error) = &result {
        error!("Initial login failed: {error}");
    }
    result
}

#[instrument]
pub async fn event() -> Result<EventResponse, Error> {
    let mut client = connect!(MiscClient);
    let request = tonic::Request::new(EventRequest {});
    let mut response = client.event(request).await?.into_inner();

    // Party-follow invitations must reach the overlay. The server marks them
    // force_join, and the overlay accepts them once before injecting the same
    // proven game-invitation event used by direct room and lobby invitations.
    if let Some(invite) = response.invite.as_mut() {
        let server_id = invite.id;
        let local_id = invite_id_state().lock().unwrap().map_server_event(server_id);
        debug!("InviteIdMapped server_id={server_id} local_id={local_id}");
        invite.id = local_id;
    }
    Ok(response)
}

#[instrument]
pub async fn relogin() -> bool {
    {
        let _ = TOKEN.lock().unwrap().take();
    }
    let (username, password) = {
        let Some((username, password)) = CREDS.lock().unwrap().clone() else {
            error!("Relogin skipped because credentials are unavailable");
            return false;
        };
        (username, password)
    };
    match login_async(&username, &password).await {
        Ok(()) => {
            info!("Relogin successful");
            true
        }
        Err(error) => {
            error!("Relogin failed: {error}");
            false
        }
    }
}

#[cfg(test)]
mod tests {
    use super::InviteIdState;

    #[test]
    fn repeated_delivery_reuses_local_id_until_acceptance() {
        let mut state = InviteIdState::new();
        let first = state.map_server_event(1);
        assert_eq!(first, state.map_server_event(1));
        assert_eq!(state.server_id_for_local(first), 1);
    }

    #[test]
    fn reused_server_id_gets_new_local_id_after_acceptance() {
        let mut state = InviteIdState::new();
        let first = state.map_server_event(1);
        state.retire_local(first);
        let second = state.map_server_event(1);
        assert_ne!(first, second);
        assert_eq!(state.server_id_for_local(second), 1);
        assert_eq!(second, state.map_server_event(1));
    }
}
