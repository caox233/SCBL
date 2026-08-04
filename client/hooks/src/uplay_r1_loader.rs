use std::collections::BTreeMap;
use std::ffi::c_char;
use std::ffi::c_void;
#[cfg(feature = "diagnostic-evidence")]
use std::fs;
#[cfg(feature = "diagnostic-evidence")]
use std::fs::OpenOptions;
#[cfg(feature = "diagnostic-evidence")]
use std::io::Write;
#[cfg(feature = "diagnostic-evidence")]
use std::path::PathBuf;
#[cfg(feature = "diagnostic-evidence")]
use std::sync::atomic::AtomicU64;
#[cfg(feature = "diagnostic-evidence")]
use std::sync::atomic::Ordering;
use std::sync::mpsc;
use std::sync::Mutex;
use std::sync::OnceLock;
use std::time::Duration;
use std::time::Instant;
#[cfg(feature = "diagnostic-evidence")]
use std::time::SystemTime;
#[cfg(feature = "diagnostic-evidence")]
use std::time::UNIX_EPOCH;

use hooks_proc::forwardable_export;
#[cfg(feature = "diagnostic-evidence")]
use sha2::Digest;
#[cfg(feature = "diagnostic-evidence")]
use sha2::Sha256;
use tracing::error;
use tracing::info;
use tracing::warn;
use windows::core::s;
use windows::core::PCSTR;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::System::LibraryLoader::GetProcAddress;
use windows::Win32::System::LibraryLoader::LoadLibraryA;
use windows::Win32::UI::WindowsAndMessaging::MessageBoxA;
use windows::Win32::UI::WindowsAndMessaging::MB_OK;

mod ach;
mod avatar;
mod friends;
mod overlay;
mod party;
mod presence;
mod save;
mod types;
mod user;
mod win;

use types::List;
use types::UplayDataBlob;
use types::UplayFriend;
use types::UplayGameSession;
use types::UplayList;
use types::UplayOverlapped;
use types::UplaySave;

use self::types::UplayEvent;
use self::types::UplayEventType;

type Result<T> = std::result::Result<T, anyhow::Error>;

fn get_proc(name: PCSTR) -> Option<unsafe extern "system" fn() -> isize> {
    static DLL_HANDLE: OnceLock<windows::core::Result<HMODULE>> = OnceLock::new();
    let handle = DLL_HANDLE
        .get_or_init(|| unsafe { LoadLibraryA(s!("uplay_r1_loader.orig.dll")) })
        .as_ref()
        .inspect_err(|&e| unsafe {
            error!("Library loading error: {e:?}");
            let mut s = format!("{e:?}");
            let v = s.as_mut_vec();
            v.push(b'\0');
            MessageBoxA(None, PCSTR(v.as_ptr()), s!("Error"), MB_OK);
        })
        .inspect(|_l| {
            info!("Library loaded");
        })
        .unwrap();
    unsafe { GetProcAddress(*handle, name) }
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_GetLastError(out_error_string: *mut *const c_char) -> bool {
    false
}

#[derive(Clone, Debug)]
pub struct PartyGameInvite {
    pub invitation_id: u32,
    pub kind: i32,
    pub sender_id: String,
    pub sender_name: String,
    pub game_session: server_api::misc::UplayGameSession,
}

const BLACKLIST_FRIEND_INVITE_DATA_BYTES: usize = 0x1f0;
const BLACKLIST_ACCOUNT_ID_BYTES: usize = 128;
const BLACKLIST_FRIEND_REMAINDER_BYTES: usize = BLACKLIST_FRIEND_INVITE_DATA_BYTES - 8 - BLACKLIST_ACCOUNT_ID_BYTES;

/// Retail Blacklist validates a 496-byte Friends invite object, requires the
/// first DWORD to be 1, and reads the account id beginning at byte offset 8.
#[repr(C)]
struct BlacklistFriendInviteData {
    version: u32,
    reserved: u32,
    account_id: [u8; BLACKLIST_ACCOUNT_ID_BYTES],
    remainder: [u8; BLACKLIST_FRIEND_REMAINDER_BYTES],
}

#[repr(C)]
struct BlacklistFriendIdentityRef {
    reserved: [usize; 2],
    identity: *const BlacklistFriendInviteData,
    data_bytes: usize,
}

#[repr(C)]
struct BlacklistFriendAccepted {
    identity: *const BlacklistFriendIdentityRef,
}

/// Both Blacklist retail executables read `game_session` from payload +4.
/// On their 32-bit ABI this requires a u32 invitation id, not u64.
#[repr(C)]
struct BlacklistPartyAccepted {
    invitation_id: u32,
    game_session: *const UplayGameSession,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PrivateInviteAbMode {
    Baseline = 0,
    HostBlobExact = 1,
    HostPayloadOnly = 2,
    HostBlobAccountPatched = 3,
}

impl PrivateInviteAbMode {
    pub fn label(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::HostBlobExact => "host_blob_exact",
            Self::HostPayloadOnly => "host_payload_only",
            Self::HostBlobAccountPatched => "host_blob_account_patched",
        }
    }
}

#[cfg(feature = "diagnostic-evidence")]
static PRIVATE_INVITE_EVIDENCE_SEQUENCE: AtomicU64 = AtomicU64::new(1);
#[cfg(feature = "diagnostic-evidence")]
static PRIVATE_INVITE_EVIDENCE_WRITE_LOCK: OnceLock<Mutex<()>> = OnceLock::new();
#[cfg(feature = "diagnostic-evidence")]
static GET_NEXT_EVENT_CALLS: AtomicU64 = AtomicU64::new(0);
#[cfg(feature = "diagnostic-evidence")]
static GET_NEXT_EVENT_POLL_STATE: OnceLock<Mutex<(Instant, u64)>> = OnceLock::new();

const PRIVATE_INVITE_BLOB_BYTES: usize = 0x1f0;
const PRIVATE_INVITE_ACCOUNT_OFFSET: usize = 0x08;
const PRIVATE_INVITE_ACCOUNT_BYTES: usize = 0x80;
const PRIVATE_INVITE_PAYLOAD_OFFSET: usize = 0x88;
const PRIVATE_INVITE_PAYLOAD_CAPACITY: usize = 0x164;
const PRIVATE_INVITE_PAYLOAD_SIZE_OFFSET: usize = 0x1ec;

pub(crate) fn current_private_invite_ab_mode() -> PrivateInviteAbMode {
    PrivateInviteAbMode::Baseline
}

#[cfg(feature = "diagnostic-evidence")]
fn evidence_directory() -> Option<PathBuf> {
    let directory = std::env::current_exe().ok()?.parent()?.join("scbl48-evidence");
    fs::create_dir_all(&directory).ok()?;
    Some(directory)
}

#[cfg(feature = "diagnostic-evidence")]
fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect()
}

fn read_u32_le(bytes: &[u8], offset: usize) -> Option<u32> {
    let raw: [u8; 4] = bytes.get(offset..offset + 4)?.try_into().ok()?;
    Some(u32::from_le_bytes(raw))
}

fn blob_account_id(bytes: &[u8]) -> String {
    let Some(account) = bytes.get(PRIVATE_INVITE_ACCOUNT_OFFSET..PRIVATE_INVITE_ACCOUNT_OFFSET + PRIVATE_INVITE_ACCOUNT_BYTES) else {
        return String::from("<unavailable>");
    };
    let length = account.iter().position(|byte| *byte == 0).unwrap_or(account.len());
    String::from_utf8_lossy(&account[..length]).into_owned()
}

#[cfg(feature = "diagnostic-evidence")]
pub(crate) fn record_private_invite_blob(stage: &str, invitation_id: i64, outer_session_id: u64, expected_session_id: u64, mode: PrivateInviteAbMode, bytes: &[u8]) {
    let sequence = PRIVATE_INVITE_EVIDENCE_SEQUENCE.fetch_add(1, Ordering::AcqRel);
    let unknown1 = read_u32_le(bytes, 0);
    let checksum = read_u32_le(bytes, 4);
    let payload_size = read_u32_le(bytes, PRIVATE_INVITE_PAYLOAD_SIZE_OFFSET).and_then(|value| usize::try_from(value).ok());
    let payload = payload_size
        .filter(|size| *size <= PRIVATE_INVITE_PAYLOAD_CAPACITY)
        .and_then(|size| bytes.get(PRIVATE_INVITE_PAYLOAD_OFFSET..PRIVATE_INVITE_PAYLOAD_OFFSET + size));
    let blob_sha256 = sha256_hex(bytes);
    let payload_sha256 = payload.map_or_else(|| String::from("invalid"), sha256_hex);
    let account_id = blob_account_id(bytes);
    let timestamp_ms = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |duration| duration.as_millis());
    let safe_stage: String = stage.chars().map(|character| if character.is_ascii_alphanumeric() { character } else { '-' }).collect();
    let filename = format!("{timestamp_ms}-{sequence:06}-{safe_stage}-invite-{invitation_id}-outer-{outer_session_id}-expected-{expected_session_id}.bin");
    let mut written = false;
    if let Some(directory) = evidence_directory() {
        let _guard = PRIVATE_INVITE_EVIDENCE_WRITE_LOCK
            .get_or_init(|| Mutex::new(()))
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let binary_path = directory.join(&filename);
        written = fs::write(&binary_path, bytes).is_ok();
        let metadata = format!(
            "stage={stage}
sequence={sequence}
timestamp_ms={timestamp_ms}
mode={}
mode_id={}
invitation_id={invitation_id}
outer_session_id={outer_session_id}
expected_session_id={expected_session_id}
data_bytes={}
unknown1={:?}
checksum={:?}
account_id={account_id}
payload_size={:?}
blob_sha256={blob_sha256}
payload_sha256={payload_sha256}
",
            mode.label(),
            mode as u8,
            bytes.len(),
            unknown1,
            checksum,
            payload_size,
        );
        let _ = fs::write(binary_path.with_extension("txt"), metadata);
        if let Ok(mut index) = OpenOptions::new().create(true).append(true).open(directory.join("evidence-index.tsv")) {
            let _ = writeln!(
                index,
                "{timestamp_ms}	{sequence}	{stage}	{}	{invitation_id}	{outer_session_id}	{expected_session_id}	{}	{:?}	{:?}	{}	{:?}	{blob_sha256}	{payload_sha256}	{filename}",
                mode.label(),
                bytes.len(),
                unknown1,
                checksum,
                account_id,
                payload_size,
            );
        }
    }
    info!(
        "PrivateInviteFullEvidence48 phase=blob stage={stage} sequence={sequence} mode={} mode_id={} invitation_id={invitation_id} outer_session_id={outer_session_id} expected_session_id={expected_session_id} data_bytes={} unknown1={unknown1:?} checksum={checksum:?} account_id={account_id} payload_size={payload_size:?} blob_sha256={blob_sha256} payload_sha256={payload_sha256} file={filename} written={written}",
        mode.label(),
        mode as u8,
        bytes.len(),
    );
}

#[cfg(not(feature = "diagnostic-evidence"))]
pub(crate) fn record_private_invite_blob(_stage: &str, _invitation_id: i64, _outer_session_id: u64, _expected_session_id: u64, _mode: PrivateInviteAbMode, _bytes: &[u8]) {}

#[cfg(feature = "diagnostic-evidence")]
pub(crate) fn record_uplay_set_game_session(identifier: u64, flags: usize, invite_only: bool, bytes: &[u8]) {
    let mode = current_private_invite_ab_mode();
    record_private_invite_blob("uplay-set-game-session", -1, identifier, 0, mode, bytes);
    info!(
        "PrivateInviteFullEvidence48 phase=uplay-capture identifier={identifier} flags={flags} invite_only={invite_only} mode={} mode_id={}",
        mode.label(),
        mode as u8,
    );
}

#[cfg(not(feature = "diagnostic-evidence"))]
pub(crate) fn record_uplay_set_game_session(_identifier: u64, _flags: usize, _invite_only: bool, _bytes: &[u8]) {}

#[cfg(feature = "diagnostic-evidence")]
fn trace_get_next_event_poll(returned_event: bool) {
    let total = GET_NEXT_EVENT_CALLS.fetch_add(1, Ordering::AcqRel) + 1;
    let mode = current_private_invite_ab_mode();
    let mut state = GET_NEXT_EVENT_POLL_STATE
        .get_or_init(|| Mutex::new((Instant::now(), 0)))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    state.1 += 1;
    if returned_event || state.0.elapsed() >= Duration::from_secs(1) {
        info!(
            "PrivateInviteFullEvidence48 phase=get-next-event-poll calls_total={total} calls_window={} returned_event={returned_event} mode={} mode_id={}",
            state.1,
            mode.label(),
            mode as u8,
        );
        state.0 = Instant::now();
        state.1 = 0;
    }
}

#[cfg(not(feature = "diagnostic-evidence"))]
fn trace_get_next_event_poll(_returned_event: bool) {}

#[allow(dead_code)]
#[derive(Clone, Debug)]
pub enum Event {
    UserAccountSharing,
    FriendsFriendListUpdated,
    FriendsGameInviteAccepted(u32, String, String),
    FriendsPrivateBlobInviteAccepted(PartyGameInvite, PrivateInviteAbMode),
    PartyGameInviteAccepted(PartyGameInvite),
}

pub static EVENTS: OnceLock<Mutex<mpsc::Receiver<Event>>> = OnceLock::new();
static EVENT_SENDER: OnceLock<mpsc::Sender<Event>> = OnceLock::new();

pub(crate) fn event_sender() -> mpsc::Sender<Event> {
    EVENT_SENDER
        .get_or_init(|| {
            let (sender, receiver) = mpsc::channel();
            EVENTS.set(Mutex::new(receiver)).unwrap_or_else(|_| panic!("Uplay event receiver was initialized twice"));
            sender
        })
        .clone()
}

type FriendPresenceSnapshot = BTreeMap<String, (String, bool)>;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum FriendSnapshotChange {
    None,
    Initial,
    Roster,
    Presence,
}

fn update_friend_presence_snapshot(snapshot: &mut Option<FriendPresenceSnapshot>, friends: Vec<crate::api::Friend>) -> FriendSnapshotChange {
    let next = friends
        .into_iter()
        .map(|friend| (friend.id, (friend.username, friend.is_online)))
        .collect::<FriendPresenceSnapshot>();
    // The game's first GetFriendList call can race this background poll. Emit
    // one initial refresh so a player who came online during startup is not
    // missed, then emit only when the snapshot actually changes.
    let change = match snapshot.as_ref() {
        None => FriendSnapshotChange::Initial,
        Some(current) if current == &next => FriendSnapshotChange::None,
        Some(current)
            if current.len() != next.len()
                || current
                    .iter()
                    .any(|(id, (username, _))| next.get(id).is_none_or(|(next_username, _)| next_username != username)) =>
        {
            FriendSnapshotChange::Roster
        }
        Some(_) => FriendSnapshotChange::Presence,
    };
    *snapshot = Some(next);
    change
}

const PRIVATE_INVITE_REPLAY_DELAY: Duration = Duration::from_secs(5);

#[derive(Debug)]
struct PendingPrivateInviteReplay {
    due: Instant,
    expected_session_id: u64,
    event: Event,
}

static PENDING_PRIVATE_INVITE_REPLAY: OnceLock<Mutex<Option<PendingPrivateInviteReplay>>> = OnceLock::new();

fn private_invite_expected_session_id(event: &Event) -> Option<u64> {
    match event {
        Event::FriendsPrivateBlobInviteAccepted(invite, _) => Some(invite.game_session.id),
        _ => None,
    }
}

fn schedule_private_invite_replay(event: &Event) {
    let Some(expected_session_id) = private_invite_expected_session_id(event) else {
        return;
    };
    *PENDING_PRIVATE_INVITE_REPLAY
        .get_or_init(|| Mutex::new(None))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(PendingPrivateInviteReplay {
        due: Instant::now() + PRIVATE_INVITE_REPLAY_DELAY,
        expected_session_id,
        event: event.clone(),
    });
    info!(
        "PrivateInviteColdStartReplay49 phase=armed expected_session_id={expected_session_id} delay_ms={} behavior_changed=true",
        PRIVATE_INVITE_REPLAY_DELAY.as_millis()
    );
}

fn take_due_private_invite_replay() -> Option<Event> {
    let mut pending = PENDING_PRIVATE_INVITE_REPLAY
        .get_or_init(|| Mutex::new(None))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if pending.as_ref().is_some_and(|replay| replay.due <= Instant::now()) {
        let replay = pending.take()?;
        info!(
            "PrivateInviteColdStartReplay49 phase=replay expected_session_id={} behavior_changed=true",
            replay.expected_session_id
        );
        Some(replay.event)
    } else {
        None
    }
}

pub(crate) fn acknowledge_private_invite_match_target(expected_session_id: u64) {
    let mut pending = PENDING_PRIVATE_INVITE_REPLAY
        .get_or_init(|| Mutex::new(None))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if pending.as_ref().is_some_and(|replay| replay.expected_session_id == expected_session_id) {
        pending.take();
        info!("PrivateInviteColdStartReplay49 phase=cancel reason=match-controller-selected expected_session_id={expected_session_id} behavior_changed=true");
    }
    party::acknowledge_private_invite_match_target(expected_session_id);
}

pub(crate) fn arm_party_presence_proof(kind: i32, invitation_id: i64, expected_session_id: u64, sender_id: &str) {
    party::arm_party_presence_proof(kind, invitation_id, expected_session_id, sender_id);
}

pub(crate) fn take_private_invite_match_target() -> Option<u64> {
    party::take_private_invite_match_target()
}

fn copy_account_id(target: &mut [u8], account_id: &str) {
    let range = PRIVATE_INVITE_ACCOUNT_OFFSET..PRIVATE_INVITE_ACCOUNT_OFFSET + PRIVATE_INVITE_ACCOUNT_BYTES;
    target[range.clone()].fill(0);
    let account = account_id.as_bytes();
    let length = account.len().min(PRIVATE_INVITE_ACCOUNT_BYTES - 1);
    target[range.start..range.start + length].copy_from_slice(&account[..length]);
}

fn build_private_friend_blob(mode: PrivateInviteAbMode, account_id: &str, host_blob: Option<&[u8]>) -> std::result::Result<[u8; PRIVATE_INVITE_BLOB_BYTES], &'static str> {
    let mut output = [0u8; PRIVATE_INVITE_BLOB_BYTES];
    output[..4].copy_from_slice(&1u32.to_le_bytes());
    copy_account_id(&mut output, account_id);

    if mode == PrivateInviteAbMode::Baseline {
        return Ok(output);
    }
    let host = host_blob.ok_or("host Uplay SessionData missing")?;
    if host.len() != PRIVATE_INVITE_BLOB_BYTES {
        return Err("host Uplay SessionData is not 496 bytes");
    }
    if read_u32_le(host, 0) != Some(1) {
        return Err("host Uplay SessionData first DWORD is not 1");
    }
    let payload_size = read_u32_le(host, PRIVATE_INVITE_PAYLOAD_SIZE_OFFSET)
        .and_then(|value| usize::try_from(value).ok())
        .ok_or("host Uplay SessionData payload size missing")?;
    if payload_size > PRIVATE_INVITE_PAYLOAD_CAPACITY {
        return Err("host Uplay SessionData payload size exceeds 356 bytes");
    }

    match mode {
        PrivateInviteAbMode::Baseline => {}
        PrivateInviteAbMode::HostBlobExact => output.copy_from_slice(host),
        PrivateInviteAbMode::HostPayloadOnly => {
            output[PRIVATE_INVITE_PAYLOAD_OFFSET..].copy_from_slice(&host[PRIVATE_INVITE_PAYLOAD_OFFSET..]);
        }
        PrivateInviteAbMode::HostBlobAccountPatched => {
            output.copy_from_slice(host);
            copy_account_id(&mut output, account_id);
        }
    }
    Ok(output)
}

unsafe fn return_standard_friend_event(
    event: *mut UplayEvent,
    invitation_id: u32,
    account_id: &str,
    mode: PrivateInviteAbMode,
    host_blob: Option<&[u8]>,
    outer_session_id: u64,
    expected_session_id: u64,
) -> bool {
    #![allow(static_mut_refs)]

    static mut IDENTITY: BlacklistFriendInviteData = BlacklistFriendInviteData {
        version: 1,
        reserved: 0,
        account_id: [0u8; BLACKLIST_ACCOUNT_ID_BYTES],
        remainder: [0u8; BLACKLIST_FRIEND_REMAINDER_BYTES],
    };
    static mut IDENTITY_REF: BlacklistFriendIdentityRef = BlacklistFriendIdentityRef {
        reserved: [0usize; 2],
        identity: std::ptr::addr_of!(IDENTITY),
        data_bytes: BLACKLIST_FRIEND_INVITE_DATA_BYTES,
    };
    static mut FRIEND_ACCEPTED: BlacklistFriendAccepted = BlacklistFriendAccepted {
        identity: std::ptr::addr_of!(IDENTITY_REF),
    };

    let bytes = match build_private_friend_blob(mode, account_id, host_blob) {
        Ok(bytes) => bytes,
        Err(reason) => {
            warn!(
                "PrivateInviteFullEvidence48 phase=event-rejected invitation_id={invitation_id} mode={} reason={reason}",
                mode.label(),
            );
            return false;
        }
    };
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), std::ptr::addr_of_mut!(IDENTITY).cast::<u8>(), bytes.len());
    IDENTITY_REF.identity = std::ptr::addr_of!(IDENTITY);
    IDENTITY_REF.data_bytes = BLACKLIST_FRIEND_INVITE_DATA_BYTES;
    FRIEND_ACCEPTED.identity = std::ptr::addr_of!(IDENTITY_REF);
    (*event).event_type = UplayEventType::FriendsGameInviteAccepted;
    (*event).unknown = std::ptr::addr_of!(FRIEND_ACCEPTED) as usize;
    party::trace_invite_event_payload("FriendsGameInviteAccepted", invitation_id, account_id, (*event).unknown);
    record_private_invite_blob("game-event-returned", i64::from(invitation_id), outer_session_id, expected_session_id, mode, &bytes);
    info!(
        "PrivateInviteFullEvidence48 phase=event-returned event=FriendsGameInviteAccepted invitation_id={invitation_id} mode={} mode_id={} standard_friends_abi=true data_bytes={} account_offset=8 outer_session_id={outer_session_id} expected_session_id={expected_session_id}",
        mode.label(),
        mode as u8,
        bytes.len(),
    );
    true
}

unsafe fn into_friend_invite_accepted(event: *mut UplayEvent, invitation_id: u32, ubi_name: String) {
    let _ = return_standard_friend_event(event, invitation_id, &ubi_name, PrivateInviteAbMode::Baseline, None, 0, 0);
}

unsafe fn into_friend_private_blob_invite_accepted(event: *mut UplayEvent, invite: &PartyGameInvite, mode: PrivateInviteAbMode) -> bool {
    return_standard_friend_event(
        event,
        invite.invitation_id,
        &invite.sender_id,
        mode,
        Some(&invite.game_session.data),
        invite.game_session.id,
        invite.game_session.id,
    )
}

unsafe fn into_party_invite_accepted(event: *mut UplayEvent, invite: &PartyGameInvite) -> bool {
    #![allow(static_mut_refs)]

    const MAX_EVENT_DATA: usize = 16 * 1024;

    static mut SESSION_BYTES: [u8; MAX_EVENT_DATA] = [0; MAX_EVENT_DATA];
    static mut GAME_SESSION: UplayGameSession = UplayGameSession {
        id: 0,
        data: UplayDataBlob {
            data: std::ptr::null(),
            num_bytes: 0,
        },
    };
    static mut PARTY_ACCEPTED: BlacklistPartyAccepted = BlacklistPartyAccepted {
        invitation_id: 0,
        game_session: std::ptr::addr_of!(GAME_SESSION),
    };

    let size = invite.game_session.data.len();
    if size == 0 || size > MAX_EVENT_DATA {
        warn!(
            "PartyGameInvitePayloadRejected invitation_id={} game_session_id={} data_bytes={size}",
            invite.invitation_id, invite.game_session.id
        );
        return false;
    }
    SESSION_BYTES[..size].copy_from_slice(&invite.game_session.data);
    GAME_SESSION.id = invite.game_session.id;
    GAME_SESSION.data.data = SESSION_BYTES.as_ptr().cast::<c_void>();
    GAME_SESSION.data.num_bytes = size as u32;
    PARTY_ACCEPTED.invitation_id = invite.invitation_id;
    PARTY_ACCEPTED.game_session = std::ptr::addr_of!(GAME_SESSION);

    (*event).event_type = UplayEventType::PartyGameInviteAccepted;
    (*event).unknown = std::ptr::addr_of!(PARTY_ACCEPTED) as usize;
    party::trace_invite_event_payload("PartyGameInviteAccepted", invite.invitation_id, &invite.sender_id, (*event).unknown);
    info!(
        "BlacklistPartyAcceptedAbiBuilt invitation_id={} kind={} game_session_id={} data_bytes={} game_session_offset=4 flags={} invite_only={}",
        invite.invitation_id, invite.kind, invite.game_session.id, size, invite.game_session.flags, invite.game_session.invite_only,
    );
    true
}

#[cfg(test)]
mod blacklist_uplay_abi_tests {
    use super::*;

    #[test]
    fn friends_invite_data_matches_retail_blacklist() {
        assert_eq!(std::mem::size_of::<BlacklistFriendInviteData>(), 0x1f0);
        assert_eq!(std::mem::offset_of!(BlacklistFriendInviteData, version), 0);
        assert_eq!(std::mem::offset_of!(BlacklistFriendInviteData, account_id), 8);
    }

    #[test]
    fn private_invite_ab_modes_copy_only_the_intended_regions() {
        let mut host = [0u8; PRIVATE_INVITE_BLOB_BYTES];
        host[..4].copy_from_slice(&1u32.to_le_bytes());
        host[4..8].copy_from_slice(&0x1122_3344u32.to_le_bytes());
        copy_account_id(&mut host, "host-account");
        host[PRIVATE_INVITE_PAYLOAD_OFFSET..PRIVATE_INVITE_PAYLOAD_OFFSET + 4].copy_from_slice(&[1, 2, 3, 4]);
        host[PRIVATE_INVITE_PAYLOAD_SIZE_OFFSET..].copy_from_slice(&4u32.to_le_bytes());

        let baseline = build_private_friend_blob(PrivateInviteAbMode::Baseline, "sender", None).unwrap();
        assert_eq!(read_u32_le(&baseline, 0), Some(1));
        assert_eq!(read_u32_le(&baseline, 4), Some(0));
        assert_eq!(read_u32_le(&baseline, PRIVATE_INVITE_PAYLOAD_SIZE_OFFSET), Some(0));

        let exact = build_private_friend_blob(PrivateInviteAbMode::HostBlobExact, "sender", Some(&host)).unwrap();
        assert_eq!(exact, host);

        let payload = build_private_friend_blob(PrivateInviteAbMode::HostPayloadOnly, "sender", Some(&host)).unwrap();
        assert_eq!(read_u32_le(&payload, 4), Some(0));
        assert_eq!(&payload[PRIVATE_INVITE_PAYLOAD_OFFSET..], &host[PRIVATE_INVITE_PAYLOAD_OFFSET..]);

        let patched = build_private_friend_blob(PrivateInviteAbMode::HostBlobAccountPatched, "sender", Some(&host)).unwrap();
        assert_eq!(read_u32_le(&patched, 4), Some(0x1122_3344));
        assert_eq!(blob_account_id(&patched), "sender");
    }

    #[test]
    fn production_private_invite_mode_is_fixed_to_baseline() {
        assert_eq!(current_private_invite_ab_mode(), PrivateInviteAbMode::Baseline);
    }

    #[test]
    fn party_accepted_matches_retail_blacklist_x86() {
        #[cfg(target_pointer_width = "32")]
        {
            assert_eq!(std::mem::size_of::<BlacklistPartyAccepted>(), 8);
            assert_eq!(std::mem::offset_of!(BlacklistPartyAccepted, invitation_id), 0);
            assert_eq!(std::mem::offset_of!(BlacklistPartyAccepted, game_session), 4);
            assert_eq!(std::mem::size_of::<BlacklistFriendIdentityRef>(), 16);
            assert_eq!(std::mem::offset_of!(BlacklistFriendIdentityRef, identity), 8);
            assert_eq!(std::mem::offset_of!(BlacklistFriendIdentityRef, data_bytes), 12);
        }
    }
}

#[forwardable_export(log = false)]
unsafe extern "cdecl" fn UPLAY_GetNextEvent(event: *mut UplayEvent) -> bool {
    if event.is_null() {
        return false;
    }

    let channel_event = EVENTS
        .get()
        .map(Mutex::lock)
        .map(std::result::Result::unwrap)
        .as_deref()
        .map(mpsc::Receiver::try_recv)
        .and_then(std::result::Result::ok);
    let (evt, replayed) = if let Some(evt) = channel_event {
        (Some(evt), false)
    } else {
        (take_due_private_invite_replay(), true)
    };

    if let Some(evt) = evt {
        info!("New event {evt:?}");
        party::trace_event_dispatch("dequeued", &format!("{evt:?}"), 0);
        if !replayed {
            schedule_private_invite_replay(&evt);
        }
        match evt {
            Event::UserAccountSharing => {
                (*event).event_type = UplayEventType::UserAccountSharing;
                (*event).unknown = 0;
            }
            Event::FriendsFriendListUpdated => {
                (*event).event_type = UplayEventType::FriendsFriendListUpdated;
                (*event).unknown = 0;
            }
            Event::FriendsGameInviteAccepted(invitation_id, user, username) => {
                party::observe_friend_game_invite_accepted(invitation_id, &user, &username);
                into_friend_invite_accepted(event, invitation_id, user);
            }
            Event::FriendsPrivateBlobInviteAccepted(invite, mode) => {
                party::observe_friend_game_invite_accepted(invite.invitation_id, &invite.sender_id, &invite.sender_name);
                if !into_friend_private_blob_invite_accepted(event, &invite, mode) {
                    return false;
                }
            }
            Event::PartyGameInviteAccepted(invite) => {
                party::observe_party_game_invite_accepted(invite.kind, invite.invitation_id, &invite.sender_id, &invite.sender_name);
                if !into_party_invite_accepted(event, &invite) {
                    return false;
                }
            }
        }
        party::trace_event_dispatch("returned", "UplayEvent", (*event).unknown);
        trace_get_next_event_poll(true);
        true
    } else {
        trace_get_next_event_poll(false);
        false
    }
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_GetOverlappedOperationResult_(overlapped: *mut UplayOverlapped, result: *mut usize) -> bool {
    let overlapped = unsafe { overlapped.as_ref() };
    let result = unsafe { result.as_mut() };
    if let Some((overlapped, result)) = overlapped.filter(|o| o.is_completed != 0).zip(result) {
        *result = overlapped.result;
        true
    } else {
        false
    }
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_GetOverlappedOperationResult(overlapped: *mut UplayOverlapped, result: *mut usize) -> bool {
    let overlapped = unsafe { overlapped.as_ref() };
    let result = unsafe { result.as_mut() };
    if let Some((overlapped, result)) = overlapped.filter(|o| o.is_completed != 0).zip(result) {
        *result = overlapped.result;
        true
    } else {
        false
    }
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_HasOverlappedOperationCompleted(overlapped: *mut UplayOverlapped) -> bool {
    let overlapped = unsafe { overlapped.as_ref() };
    overlapped.is_some_and(|o| o.is_completed != 0)
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_Quit() -> bool {
    false
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_Release(ptr: *mut List) -> bool {
    if !ptr.is_null() {
        let list = *Box::from_raw(ptr);
        let list: UplayList = list.try_into().unwrap();
        drop(list);
    }
    true
}

#[forwardable_export(always_call)]
unsafe extern "cdecl" fn UPLAY_Startup(uplay_id: usize, game_version: usize, language_country_code_utf8: *const c_char) -> isize {
    if let Some(config) = crate::config::get() {
        if config.enable_overlay {
            info!("Initializing overlay");
            let (tx, rx) = crossbeam_channel::unbounded();
            let game_event_tx = event_sender();
            // needs to be done in a separate thread, otherwise it'll not work
            std::thread::Builder::new()
                .name(String::from("overlay-thread"))
                .spawn(|| {
                    let Some(engine) = crate::overlay::Engine::detect() else {
                        error!("Couldn't identify DX version");
                        return;
                    };
                    info!("Detected DX version {engine:?}");
                    // TODO: blocks game if running in fullscreen mode. Waiting 10s seems to do the trick
                    std::thread::sleep(std::time::Duration::from_secs(10));
                    if let Err(err) = crate::overlay::init(engine, rx) {
                        error!("Couldn't initialize overlay: {err}");
                    } else {
                        info!("Overlay initialized");
                    }
                })
                .unwrap();

            std::thread::Builder::new()
                .name(String::from("updates-thread"))
                .spawn(move || {
                    crate::api::runtime().unwrap().block_on(async {
                        let mut failures = 0;
                        let mut friend_presence = None;
                        let mut friend_poll_countdown = 0u8;
                        let mut friend_roster_replay_countdown = None::<u8>;
                        let mut friend_roster_replays_remaining = 0u8;
                        loop {
                            tokio::time::sleep(Duration::from_secs(1)).await;
                            let event: Option<std::result::Result<_, _>> = crate::api::event().await.map(|resp| resp.invite).transpose();
                            if let Some(invite) = event {
                                if invite.is_err() {
                                    failures += 1;
                                } else {
                                    failures = 0;
                                }
                                let invite = invite.map(Some);
                                tx.send(invite).unwrap();
                                if failures > 0 && failures % 10 == 0 && crate::api::relogin().await {
                                    // signal successful relogin
                                    tx.send(Ok(None)).unwrap();
                                }
                            }

                            if friend_poll_countdown == 0 {
                                friend_poll_countdown = 1;
                                match crate::api::list_friends_async().await {
                                    Ok(friends) => {
                                        let change = update_friend_presence_snapshot(&mut friend_presence, friends);
                                        if change != FriendSnapshotChange::None {
                                            info!("FriendSnapshotChanged change={change:?} event=FriendsFriendListUpdated");
                                            if game_event_tx.send(Event::FriendsFriendListUpdated).is_err() {
                                                warn!("FriendSnapshotChanged event queue is unavailable");
                                            }
                                            // A newly registered account can appear while Uplay and the
                                            // game's ShadowNet friend cache are still starting. The first
                                            // event is then consumed correctly but too early to populate
                                            // the visible friend roster. Replay roster changes after the
                                            // surrounding game systems have finished initializing.
                                            if matches!(change, FriendSnapshotChange::Initial | FriendSnapshotChange::Roster) {
                                                friend_roster_replay_countdown = Some(5);
                                                friend_roster_replays_remaining = 2;
                                            }
                                        }
                                    }
                                    Err(error) => warn!("FriendPresenceRefreshFailed error={error}"),
                                }
                            } else {
                                friend_poll_countdown -= 1;
                            }

                            if let Some(countdown) = friend_roster_replay_countdown.as_mut() {
                                if *countdown > 0 {
                                    *countdown -= 1;
                                } else {
                                    info!("FriendRosterRefreshReplay event=FriendsFriendListUpdated remaining={friend_roster_replays_remaining}");
                                    if game_event_tx.send(Event::FriendsFriendListUpdated).is_err() {
                                        warn!("FriendRosterRefreshReplay event queue is unavailable");
                                    }
                                    friend_roster_replays_remaining = friend_roster_replays_remaining.saturating_sub(1);
                                    if friend_roster_replays_remaining == 0 {
                                        friend_roster_replay_countdown = None;
                                    } else {
                                        *countdown = 9;
                                    }
                                }
                            }
                        }
                    });
                })
                .unwrap();
        } else {
            info!("Overlay is disabled");
        }
    } else {
        warn!("config not loaded!");
    }
    0 // 0 = all good, 1 = error occured, 2 = ??? (), 3 = ??? (potentially offline mode)
}

#[forwardable_export(log = false)]
unsafe extern "cdecl" fn UPLAY_Update() -> bool {
    true
}

#[cfg(test)]
mod friend_presence_tests {
    use super::*;

    fn friend(id: &str, online: bool) -> crate::api::Friend {
        crate::api::Friend {
            id: id.into(),
            username: id.into(),
            is_online: online,
        }
    }

    #[test]
    fn initial_friend_snapshot_emits_a_synchronization_update() {
        let mut snapshot = None;
        assert_eq!(update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", false)]), FriendSnapshotChange::Initial);
    }

    #[test]
    fn later_online_change_emits_one_update() {
        let mut snapshot = None;
        assert_eq!(update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", false)]), FriendSnapshotChange::Initial);
        assert_eq!(update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", true)]), FriendSnapshotChange::Presence);
        assert_eq!(update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", true)]), FriendSnapshotChange::None);
    }

    #[test]
    fn newly_registered_friend_is_a_roster_change() {
        let mut snapshot = None;
        assert_eq!(update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", true)]), FriendSnapshotChange::Initial);
        assert_eq!(
            update_friend_presence_snapshot(&mut snapshot, vec![friend("test2", true), friend("new-player", true)]),
            FriendSnapshotChange::Roster
        );
    }
}
