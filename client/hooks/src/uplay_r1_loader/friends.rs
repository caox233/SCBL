use std::ffi::c_char;
use std::ffi::c_void;

use hooks_proc::forwardable_export;
use tracing::debug;
use tracing::info;
use tracing::warn;

use super::UplayOverlapped;
use crate::uplay_r1_loader;

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_AddToBlackList() -> isize {
    0
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_DisableFriendMenuItem() -> isize {
    0
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_EnableFriendMenuItem() -> isize {
    0
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_GetFriendList(friend_list_filter: *mut c_void, out_friend_list: *mut uplay_r1_loader::List) -> bool {
    let list = uplay_r1_loader::UplayList::Friends(
        crate::api::list_friends()
            .unwrap_or_default()
            .into_iter()
            .map(|f| uplay_r1_loader::UplayFriend {
                id: f.id,
                username: f.username,
                is_online: f.is_online,
            })
            .collect(),
    );
    info!("Returning friends: {list:?}");
    let list: uplay_r1_loader::List = list.into();
    debug!("list = {list:?}");
    (*out_friend_list).count = list.count;
    (*out_friend_list).list = list.list;
    true
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_Init(flags: usize) -> bool {
    true
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_InviteToGame(account_id_utf8: *const c_char, overlapped: *mut UplayOverlapped) -> bool {
    const UPLAY_RESULT_FAILED: usize = 6;
    let result = if account_id_utf8.is_null() {
        Err(String::from("好友标识为空 / Missing friend account ID"))
    } else {
        std::ffi::CStr::from_ptr(account_id_utf8)
            .to_str()
            .map_err(|error| format!("好友标识无效 / Invalid friend account ID: {error}"))
            .and_then(|account_id| {
                crate::api::invite_friend(account_id).map_err(|error| match &error {
                    crate::api::Error::GRPCStatus(status) if status.code() == tonic::Code::FailedPrecondition => {
                        format!("当前没有可邀请加入的大厅或房间 / No joinable lobby or room: {}", status.message())
                    }
                    crate::api::Error::GRPCStatus(status) if status.code() == tonic::Code::NotFound => String::from("好友不存在 / Friend not found"),
                    _ => format!("邀请发送失败 / Invite failed: {error}"),
                })
            })
    };

    let success = result.is_ok();
    if !overlapped.is_null() && overlapped.is_aligned() {
        if success {
            (*overlapped).set_success();
        } else {
            (*overlapped).set_result(UPLAY_RESULT_FAILED);
        }
    }
    match result {
        Ok(()) => {
            info!("Friend game invitation sent successfully");
            crate::overlay::notify("邀请已发送 / Invite sent");
        }
        Err(message) => {
            warn!("Friend game invitation failed: {message}");
            crate::overlay::notify(message);
        }
    }
    success
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_IsBlackListed(account_id_utf8: *const c_char) -> bool {
    false
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_IsFriend(account_id_utf8: *const c_char) -> bool {
    false
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_RequestFriendship() -> isize {
    0
}

#[forwardable_export]
unsafe extern "cdecl" fn UPLAY_FRIENDS_ShowFriendSelectionUI() -> isize {
    0
}
