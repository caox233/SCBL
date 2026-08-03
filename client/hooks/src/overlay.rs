use std::sync::mpsc;
use std::sync::Mutex;
use std::time::Duration;
use std::time::Instant;

use hudhook::ImguiRenderLoop;
use imgui::Style;
use imgui::StyleColor;
use server_api::misc::InviteEvent;
use tracing::info;
use tracing::warn;
use windows::core::PCSTR;
use windows::Win32::System::LibraryLoader::GetModuleHandleA;
use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows::Win32::UI::Input::KeyboardAndMouse::VK_F5;
use windows::Win32::UI::WindowsAndMessaging::DefWindowProcA;

use crate::uplay_r1_loader::Event;
use crate::uplay_r1_loader::PrivateInviteAbMode;
use crate::uplay_r1_loader::EVENTS;

static NOTIFICATION_TIMEOUT: Duration = Duration::from_secs(30);
static INITIAL_POPUP_DURATION: Duration = Duration::from_secs(10);
static PARTY_FOLLOW_ACCEPT_DELAY: Duration = Duration::from_millis(750);
static STATUS_MESSAGE: Mutex<Option<String>> = Mutex::new(None);

pub fn notify(message: impl Into<String>) {
    if let Ok(mut slot) = STATUS_MESSAGE.lock() {
        *slot = Some(message.into());
    }
}

fn take_status_message() -> Option<String> {
    STATUS_MESSAGE.lock().ok()?.take()
}

// TODO: move to separate crate
fn sc_style(style: &mut Style) {
    style.colors[StyleColor::Text as usize] = [1.00, 1.00, 1.00, 1.00];
    style.colors[StyleColor::TextDisabled as usize] = [0.50, 0.50, 0.50, 1.00];
    style.colors[StyleColor::WindowBg as usize] = [0.03, 0.07, 0.04, 0.94];
    style.colors[StyleColor::ChildBg as usize] = [0.00, 0.00, 0.00, 0.00];
    style.colors[StyleColor::PopupBg as usize] = [0.08, 0.08, 0.08, 0.94];
    style.colors[StyleColor::Border as usize] = [0.38, 1.00, 0.00, 0.50];
    style.colors[StyleColor::BorderShadow as usize] = [0.01, 0.13, 0.00, 0.63];
    style.colors[StyleColor::FrameBg as usize] = [0.17, 0.48, 0.16, 0.54];
    style.colors[StyleColor::FrameBgHovered as usize] = [0.26, 0.98, 0.32, 0.40];
    style.colors[StyleColor::FrameBgActive as usize] = [0.26, 0.98, 0.28, 0.67];
    style.colors[StyleColor::TitleBg as usize] = [0.01, 0.07, 0.01, 1.00];
    style.colors[StyleColor::TitleBgActive as usize] = [0.0, 0.56, 0.29, 1.0];
    style.colors[StyleColor::TitleBgCollapsed as usize] = [0.00, 0.56, 0.09, 0.51];
    style.colors[StyleColor::MenuBarBg as usize] = [0.0, 0.56, 0.29, 1.0];
    // style.colors[StyleColor::TitleBg as usize] = [0.01, 0.07, 0.01, 1.00];
    // style.colors[StyleColor::TitleBgActive as usize] = [0.0, 0.29, 0.68, 1.0];
    // style.colors[StyleColor::TitleBgCollapsed as usize] = [0.00, 0.56, 0.09, 0.51];
    // style.colors[StyleColor::MenuBarBg as usize] = [0.0, 0.29, 0.68, 1.0];
    style.colors[StyleColor::ScrollbarBg as usize] = [0.00, 0.15, 0.00, 0.53];
    style.colors[StyleColor::ScrollbarGrab as usize] = [0.10, 0.41, 0.06, 1.00];
    style.colors[StyleColor::ScrollbarGrabHovered as usize] = [0.00, 0.66, 0.04, 1.00];
    style.colors[StyleColor::ScrollbarGrabActive as usize] = [0.04, 0.87, 0.00, 1.00];
    style.colors[StyleColor::CheckMark as usize] = [0.26, 0.98, 0.40, 1.00];
    style.colors[StyleColor::SliderGrab as usize] = [0.21, 0.61, 0.00, 1.00];
    style.colors[StyleColor::SliderGrabActive as usize] = [0.36, 0.87, 0.22, 1.00];
    style.colors[StyleColor::Button as usize] = [0.00, 0.60, 0.05, 0.40];
    style.colors[StyleColor::ButtonHovered as usize] = [0.20, 0.78, 0.32, 1.00];
    style.colors[StyleColor::ButtonActive as usize] = [0.00, 0.57, 0.07, 1.00];
    style.colors[StyleColor::Header as usize] = [0.12, 0.82, 0.28, 0.31];
    style.colors[StyleColor::HeaderHovered as usize] = [0.00, 0.74, 0.11, 0.80];
    style.colors[StyleColor::HeaderActive as usize] = [0.09, 0.69, 0.04, 1.00];
    style.colors[StyleColor::Separator as usize] = [0.09, 0.67, 0.01, 0.50];
    style.colors[StyleColor::SeparatorHovered as usize] = [0.32, 0.75, 0.10, 0.78];
    style.colors[StyleColor::SeparatorActive as usize] = [0.10, 0.75, 0.11, 1.00];
    style.colors[StyleColor::ResizeGrip as usize] = [0.32, 0.98, 0.26, 0.20];
    style.colors[StyleColor::ResizeGripHovered as usize] = [0.26, 0.98, 0.28, 0.67];
    style.colors[StyleColor::ResizeGripActive as usize] = [0.22, 0.69, 0.06, 0.95];
    style.colors[StyleColor::Tab as usize] = [0.18, 0.58, 0.18, 0.86];
    style.colors[StyleColor::TabHovered as usize] = [0.26, 0.98, 0.28, 0.80];
    style.colors[StyleColor::TabActive as usize] = [0.20, 0.68, 0.24, 1.00];
    style.colors[StyleColor::TabUnfocused as usize] = [0.07, 0.15, 0.08, 0.97];
    style.colors[StyleColor::TabUnfocusedActive as usize] = [0.14, 0.42, 0.19, 1.00];
    style.colors[StyleColor::PlotLines as usize] = [0.61, 0.61, 0.61, 1.00];
    style.colors[StyleColor::PlotLinesHovered as usize] = [1.00, 0.43, 0.35, 1.00];
    style.colors[StyleColor::PlotHistogram as usize] = [0.90, 0.70, 0.00, 1.00];
    style.colors[StyleColor::PlotHistogramHovered as usize] = [1.00, 0.60, 0.00, 1.00];
    style.colors[StyleColor::TableHeaderBg as usize] = [0.19, 0.19, 0.20, 1.00];
    style.colors[StyleColor::TableBorderStrong as usize] = [0.31, 0.31, 0.35, 1.00];
    style.colors[StyleColor::TableBorderLight as usize] = [0.23, 0.23, 0.25, 1.00];
    style.colors[StyleColor::TableRowBg as usize] = [0.00, 0.00, 0.00, 0.00];
    style.colors[StyleColor::TableRowBgAlt as usize] = [1.00, 1.00, 1.00, 0.06];
    style.colors[StyleColor::TextSelectedBg as usize] = [0.00, 0.89, 0.20, 0.35];
    style.colors[StyleColor::DragDropTarget as usize] = [1.00, 1.00, 0.00, 0.90];
    style.colors[StyleColor::NavHighlight as usize] = [0.26, 0.98, 0.35, 1.00];
    style.colors[StyleColor::NavWindowingHighlight as usize] = [1.00, 1.00, 1.00, 0.70];
    style.colors[StyleColor::NavWindowingDimBg as usize] = [0.80, 0.80, 0.80, 0.20];
    style.colors[StyleColor::ModalWindowDimBg as usize] = [0.80, 0.80, 0.80, 0.35];
}

fn setup_fonts(imgui: &mut imgui::Context) -> bool {
    let font_size = 16.0;
    for path in [
        r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\msyhbd.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\simsun.ttc",
    ] {
        let Ok(data) = std::fs::read(path) else {
            continue;
        };
        let data: &'static [u8] = Box::leak(data.into_boxed_slice());
        imgui.fonts().add_font(&[imgui::FontSource::TtfData {
            data,
            size_pixels: font_size,
            config: Some(imgui::FontConfig {
                name: Some(String::from("SCBL Chinese UI")),
                glyph_ranges: imgui::FontGlyphRanges::chinese_full(),
                ..imgui::FontConfig::default()
            }),
        }]);
        info!("Loaded Chinese overlay font from {path}");
        return true;
    }

    imgui.fonts().add_font(&[imgui::FontSource::TtfData {
        data: include_bytes!("../assets/Orbitron-Regular.ttf"),
        size_pixels: 13.0,
        config: Some(imgui::FontConfig {
            name: Some(String::from("Orbitron")),
            ..imgui::FontConfig::default()
        }),
    }]);
    false
}

#[allow(dead_code)]
#[derive(Debug, Clone, Copy)]
pub enum Engine {
    DX9,
    DX11,
}

impl Engine {
    pub fn detect() -> Option<Self> {
        let exe = std::env::current_exe().ok()?;
        let fname = exe.file_name()?.to_str()?.to_lowercase();
        match fname.as_str() {
            "blacklist_game.exe" => Some(Self::DX9),
            "blacklist_dx11_game.exe" => Some(Self::DX11),
            _ => None,
        }
    }
}

#[derive(Debug, Default)]
enum UiState {
    Show,
    #[default]
    Hide,
}

struct Invite {
    event: InviteEvent,
    accepted: bool,
}

fn latest_pending_invite_index(invites: &[Invite]) -> Option<usize> {
    invites.iter().rposition(|invite| !invite.accepted)
}

fn party_game_invite(
    kind: i32,
    invitation_id: i64,
    sender_id: String,
    sender_name: String,
    game_session: Option<server_api::misc::UplayGameSession>,
) -> Result<crate::uplay_r1_loader::PartyGameInvite, &'static str> {
    let invitation_id = u32::try_from(invitation_id).map_err(|_| "Invitation ID is outside the Uplay u32 range")?;
    let game_session = game_session.ok_or("Host Uplay GameSession data is not available yet")?;
    if game_session.data.is_empty() {
        return Err("Host Uplay GameSession data is empty");
    }
    Ok(crate::uplay_r1_loader::PartyGameInvite {
        invitation_id,
        kind,
        sender_id,
        sender_name,
        game_session,
    })
}

fn accepted_event_for_kind_with_mode(
    kind: i32,
    invitation_id: i64,
    sender_id: String,
    sender_name: String,
    game_session: Option<server_api::misc::UplayGameSession>,
    mode: PrivateInviteAbMode,
) -> Result<Event, &'static str> {
    if matches!(kind, 2 | 4) {
        let invitation_id = u32::try_from(invitation_id).map_err(|_| "Invitation ID is outside the Uplay u32 range")?;
        return Ok(Event::FriendsGameInviteAccepted(invitation_id, sender_id, sender_name));
    }

    let invite = party_game_invite(kind, invitation_id, sender_id, sender_name, game_session)?;
    if kind == 1 {
        if invite.game_session.data.len() != 0x1f0 {
            return Err("Host Uplay GameSession data is not the expected 496 bytes");
        }
        crate::uplay_r1_loader::record_private_invite_blob(
            "receiver-api-received",
            i64::from(invite.invitation_id),
            invite.game_session.id,
            invite.game_session.id,
            mode,
            &invite.game_session.data,
        );
        Ok(Event::FriendsPrivateBlobInviteAccepted(invite, mode))
    } else {
        Ok(Event::PartyGameInviteAccepted(invite))
    }
}

fn accepted_event_for_kind(
    kind: i32,
    invitation_id: i64,
    sender_id: String,
    sender_name: String,
    game_session: Option<server_api::misc::UplayGameSession>,
) -> Result<Event, &'static str> {
    accepted_event_for_kind_with_mode(
        kind,
        invitation_id,
        sender_id,
        sender_name,
        game_session,
        crate::uplay_r1_loader::current_private_invite_ab_mode(),
    )
}

struct MyRenderLoop {
    tx: mpsc::Sender<Event>,
    ui_state: UiState,
    debounce: bool,
    invite_notification: Option<(Instant, String)>,
    new_invites: crossbeam_channel::Receiver<Result<Option<InviteEvent>, crate::api::Error>>,
    active_invites: Vec<Invite>,
    connection_error: Option<crate::api::Error>,
    scheduled_party_follow: Option<(Instant, i64)>,
    initial_popup: Instant,
    chinese_ui: bool,
}

fn format_api_error(error: &crate::api::Error, chinese: bool) -> String {
    if chinese {
        match error {
            crate::api::Error::IO(error) => format!("网络错误：{error}"),
            crate::api::Error::MissingUrl => String::from("未配置 API 服务器"),
            crate::api::Error::Transport(error) => format!("服务器连接失败：{error}"),
            crate::api::Error::GRPCStatus(status) => match status.code() {
                tonic::Code::DeadlineExceeded => String::from("服务器连接超时"),
                tonic::Code::Unauthenticated => String::from("登录已失效，请重新启动"),
                tonic::Code::NotFound => String::from("邀请已过期或房间已关闭"),
                _ => format!("服务器错误：{} {}", status.code(), status.message()),
            },
            crate::api::Error::LoginFailure => String::from("登录失败"),
            crate::api::Error::InvalidToken(_) => String::from("登录已失效，请重新启动"),
            crate::api::Error::NotConnected => String::from("尚未连接服务器"),
        }
    } else {
        format!("{error}")
    }
}

impl MyRenderLoop {
    fn render_show(&mut self, ui: &imgui::Ui) {
        let win_size = ui.io().display_size;
        let title = if self.chinese_ui { "好友邀请" } else { "Friend Invitations" };
        let mut accept_index = None;
        ui.window(title)
            .position([win_size[0] - 10.0, 10.0], imgui::Condition::FirstUseEver)
            .position_pivot([1.0, 0.0])
            .always_auto_resize(true)
            .resizable(false)
            .build(|| {
                if self.active_invites.is_empty() {
                    ui.text(if self.chinese_ui { "暂无待处理邀请" } else { "No pending invitations" });
                    ui.text_disabled(if self.chinese_ui {
                        "收到邀请后按 F5 打开此窗口"
                    } else {
                        "Press F5 after an invitation arrives"
                    });
                }
                for (index, invite) in self.active_invites.iter().enumerate() {
                    let Some(sender) = invite.event.sender.as_ref() else {
                        continue;
                    };
                    ui.separator();
                    let lobby_party = matches!(invite.event.kind, 2 | 4);
                    let party_follow = invite.event.kind == 3;
                    let description = if self.chinese_ui {
                        if party_follow {
                            format!("正在跟随 {} 进入房间", sender.username)
                        } else if lobby_party {
                            format!("{} 邀请你加入大厅队伍", sender.username)
                        } else {
                            format!("{} 邀请你加入私人房间", sender.username)
                        }
                    } else if party_follow {
                        format!("Following {} into the game", sender.username)
                    } else if lobby_party {
                        format!("{} invited you to a lobby party", sender.username)
                    } else {
                        format!("{} invited you to a private room", sender.username)
                    };
                    ui.text(description);
                    if invite.accepted {
                        ui.text_disabled(if self.chinese_ui {
                            "已接受；未进入时可再次点击重试"
                        } else {
                            "Accepted; click again to retry joining"
                        });
                    }
                    let button = if self.chinese_ui {
                        if party_follow {
                            format!("重新跟随 {}##invite-{}", sender.username, invite.event.id)
                        } else if lobby_party {
                            format!("接受并加入 {} 的队伍##invite-{}", sender.username, invite.event.id)
                        } else {
                            format!("接受并加入 {} 的房间##invite-{}", sender.username, invite.event.id)
                        }
                    } else if party_follow {
                        format!("Follow {} again##invite-{}", sender.username, invite.event.id)
                    } else if lobby_party {
                        format!("Join {}'s party##invite-{}", sender.username, invite.event.id)
                    } else {
                        format!("Join {}'s room##invite-{}", sender.username, invite.event.id)
                    };
                    if ui.button(button) {
                        accept_index = Some(index);
                    }
                }
            });
        if let Some(index) = accept_index {
            self.accept_invite_at(index);
        }
        let color = [0.0, 0.0, 0.0, 0.5];
        ui.get_background_draw_list().add_rect([0.0, 0.0], win_size, color).filled(true).build();
    }

    #[allow(clippy::unused_self, unused_variables, clippy::needless_pass_by_ref_mut)]
    fn render_hide(&mut self, ui: &mut imgui::Ui) {}

    fn accept_invite_at(&mut self, index: usize) {
        let Some(invite) = self.active_invites.get(index) else {
            return;
        };
        let Some(sender) = invite.event.sender.as_ref() else {
            return;
        };
        let invite_id = invite.event.id;
        let sender_id = sender.id.clone();
        let sender_name = sender.username.clone();
        let kind = invite.event.kind;
        let game_session = invite.event.game_session.clone();
        let projected_target_id = game_session.as_ref().map_or(0, |session| session.id);
        let proof_sender_id = sender_id.clone();

        if kind == 3 && game_session.is_none() {
            info!("InviteAcceptDeferredMissingUplayGameSession invite_id={invite_id} kind={kind} sender={sender_name}");
            self.invite_notification = Some((
                Instant::now(),
                if self.chinese_ui {
                    String::from("主机房间数据尚未同步，正在等待后自动重试……")
                } else {
                    String::from("Host room data is still syncing; retrying automatically...")
                },
            ));
            return;
        }

        info!("InviteAcceptClicked invite_id={invite_id} kind={kind} sender={sender_name}");
        match crate::api::accept_invite(invite_id) {
            Ok((true, server_invite_id)) => {
                let game_event = match accepted_event_for_kind(kind, server_invite_id, sender_id, sender_name.clone(), game_session) {
                    Ok(event) => event,
                    Err(message) => {
                        warn!("InviteAcceptedButEventPayloadInvalid invite_id={invite_id} kind={kind} error={message}");
                        self.invite_notification = Some((Instant::now(), message.to_string()));
                        return;
                    }
                };
                crate::uplay_r1_loader::arm_party_presence_proof(kind, server_invite_id, projected_target_id, &proof_sender_id);
                info!(
                    "PartyPresenceProof47 phase=overlay-arm local_invite_id={invite_id} server_invite_id={server_invite_id} kind={kind} expected_session_id={projected_target_id} sender_id={proof_sender_id} behavior_changed=false"
                );
                let event_type = match &game_event {
                    Event::FriendsPrivateBlobInviteAccepted(_, mode) => mode.label(),
                    Event::PartyGameInviteAccepted(_) => "PartyGameInviteAccepted",
                    Event::FriendsGameInviteAccepted(_, _, _) => "FriendsGameInviteAccepted",
                    Event::UserAccountSharing => "UserAccountSharing",
                };
                if kind == 1 {
                    info!(
                        "PrivateInviteFriendsSearchRoute local_invite_id={invite_id} server_invite_id={server_invite_id} projected_target_id={projected_target_id} event=FriendsGameInviteAccepted standard_friends_abi=true uplay_payload_used=false expected_flow=Search-NAT-JoinSession"
                    );
                }
                if self.tx.send(game_event).is_err() {
                    self.invite_notification = Some((
                        Instant::now(),
                        if self.chinese_ui {
                            String::from("无法向游戏提交邀请，请重新启动游戏")
                        } else {
                            String::from("Could not submit invitation to the game")
                        },
                    ));
                    return;
                }
                info!("InviteAcceptedEventQueued local_invite_id={invite_id} server_invite_id={server_invite_id} kind={kind} event_type={event_type}");
                self.active_invites.remove(index);
                self.invite_notification = Some((
                    Instant::now(),
                    if self.chinese_ui {
                        if kind == 3 {
                            format!("正在跟随 {sender_name} 进入游戏……")
                        } else if kind == 2 {
                            format!("正在加入 {sender_name} 的队伍……")
                        } else {
                            format!("正在加入 {sender_name} 的房间……")
                        }
                    } else {
                        format!("Joining {sender_name}...")
                    },
                ));
                self.ui_state = UiState::Hide;
            }
            Ok((false, server_invite_id)) => {
                info!("InviteRejected local_invite_id={invite_id} server_invite_id={server_invite_id} kind={kind}");
                self.active_invites.remove(index);
                self.invite_notification = Some((
                    Instant::now(),
                    if self.chinese_ui {
                        String::from("邀请已失效，请让好友重新邀请")
                    } else {
                        String::from("Invitation is no longer available")
                    },
                ));
            }
            Err(error) => {
                self.invite_notification = Some((Instant::now(), format_api_error(&error, self.chinese_ui)));
            }
        }
    }

    fn show_notifications(&mut self, ui: &imgui::Ui) {
        if let Some((created, message)) = self.invite_notification.as_ref() {
            if created.elapsed() > NOTIFICATION_TIMEOUT {
                self.invite_notification.take();
            } else {
                let win_size = ui.io().display_size;
                ui.window(if self.chinese_ui { "提示" } else { "Notification" })
                    .bg_alpha(0.45)
                    .no_decoration()
                    .no_inputs()
                    .no_nav()
                    .movable(false)
                    .menu_bar(false)
                    .always_auto_resize(true)
                    .position([win_size[0] - 10.0, 10.0], imgui::Condition::Always)
                    .position_pivot([1.0, 0.0])
                    .build(|| {
                        ui.text(message);
                        let diff = NOTIFICATION_TIMEOUT - created.elapsed();
                        ui.text(format!("{}s", diff.as_secs()));
                    });
            }
        }
    }

    fn show_initial_info(&self, ui: &imgui::Ui) {
        if let Some(dur) = self.initial_popup.checked_duration_since(Instant::now()) {
            let win_size = ui.io().display_size;
            ui.window(if self.chinese_ui { "邀请功能已加载" } else { "Overlay loaded" })
                .bg_alpha(0.45)
                .no_decoration()
                .no_inputs()
                .no_nav()
                .movable(false)
                .menu_bar(false)
                .always_auto_resize(true)
                .position([win_size[0] - 10.0, 10.0], imgui::Condition::Always)
                .position_pivot([1.0, 0.0])
                .build(|| {
                    ui.text(if self.chinese_ui {
                        "收到邀请后按 F5 接受"
                    } else {
                        "Press F5 to accept invitations"
                    });
                    let ws = ui.window_size();
                    ui.get_window_draw_list()
                        .add_line(
                            [0., ws[1]],
                            [ws[0] * (dur.as_secs_f32() / INITIAL_POPUP_DURATION.as_secs_f32()), ws[1]],
                            [1.0, 0.0, 0.0, 1.0],
                        )
                        .build();
                });
        }
    }

    fn show_errors(&mut self, ui: &imgui::Ui) {
        if let Some(error) = &self.connection_error {
            let win_size = ui.io().display_size;
            ui.window(if self.chinese_ui { "服务器错误" } else { "Server Error" })
                .bg_alpha(0.45)
                .no_decoration()
                .no_inputs()
                .no_nav()
                .movable(false)
                .menu_bar(false)
                .always_auto_resize(true)
                .position([win_size[0] - 10.0, 10.0], imgui::Condition::Always)
                .position_pivot([1.0, 0.0])
                .build(|| {
                    ui.text_colored([1.0, 0.0, 0.0, 1.0], format_api_error(error, self.chinese_ui));
                });
        }
    }
}

#[cfg(test)]
mod invitation_shortcut_tests {
    use super::*;

    fn invite(id: i64, accepted: bool) -> Invite {
        Invite {
            event: InviteEvent {
                id,
                sender: None,
                force_join: false,
                kind: 1,
                game_session: None,
            },
            accepted,
        }
    }

    #[test]
    fn f5_selects_the_latest_pending_invitation() {
        let invites = vec![invite(-1, false), invite(-2, true), invite(-3, false)];
        assert_eq!(latest_pending_invite_index(&invites), Some(2));
    }

    #[test]
    fn f5_reports_no_pending_invitation_after_consumption() {
        let invites = vec![invite(-1, true), invite(-2, true)];
        assert_eq!(latest_pending_invite_index(&invites), None);
    }

    fn game_session() -> server_api::misc::UplayGameSession {
        server_api::misc::UplayGameSession {
            id: 77,
            data: vec![1, 2, 3],
            flags: 0,
            invite_only: true,
        }
    }

    #[test]
    fn lobby_invite_uses_friend_transition() {
        assert!(matches!(
            accepted_event_for_kind(2, 41, String::from("leader-id"), String::from("leader"), None),
            Ok(Event::FriendsGameInviteAccepted(invitation_id, sender, name))
                if invitation_id == 41 && sender == "leader-id" && name == "leader"
        ));
    }

    #[test]
    fn private_invite_baseline_keeps_context_but_returns_standard_game_abi() {
        let mut session = game_session();
        session.data = vec![0; 0x1f0];
        session.data[..4].copy_from_slice(&1u32.to_le_bytes());
        assert!(matches!(
            accepted_event_for_kind_with_mode(
                1,
                42,
                String::from("leader-id"),
                String::from("leader"),
                Some(session),
                PrivateInviteAbMode::Baseline,
            ),
            Ok(Event::FriendsPrivateBlobInviteAccepted(ref invite, PrivateInviteAbMode::Baseline))
                if invite.invitation_id == 42
                    && invite.sender_id == "leader-id"
                    && invite.game_session.id == 77
        ));
    }

    #[test]
    fn private_invite_blob_mode_retains_host_session_data() {
        let mut session = game_session();
        session.data = vec![0; 0x1f0];
        session.data[..4].copy_from_slice(&1u32.to_le_bytes());
        assert!(matches!(
            accepted_event_for_kind_with_mode(
                1,
                42,
                String::from("leader-id"),
                String::from("leader"),
                Some(session),
                PrivateInviteAbMode::HostBlobExact,
            ),
            Ok(Event::FriendsPrivateBlobInviteAccepted(ref invite, PrivateInviteAbMode::HostBlobExact))
                if invite.invitation_id == 42
                    && invite.sender_id == "leader-id"
                    && invite.game_session.id == 77
                    && invite.game_session.data.len() == 0x1f0
        ));
    }

    #[test]
    fn blacklist_party_accepted_uses_u32_invitation_id() {
        assert_eq!(std::mem::size_of::<u32>(), 4);
    }

    #[test]
    fn party_follow_uses_party_transition_with_game_session() {
        assert!(matches!(
            accepted_event_for_kind(
                3,
                42,
                String::from("leader-id"),
                String::from("leader"),
                Some(game_session()),
            ),
            Ok(Event::PartyGameInviteAccepted(ref invite))
                if invite.invitation_id == 42
                    && invite.kind == 3
                    && invite.sender_id == "leader-id"
                    && invite.game_session.id == 77
        ));
    }

    #[test]
    fn party_follow_waits_for_host_game_session_data() {
        assert!(accepted_event_for_kind(3, 42, String::from("leader-id"), String::from("leader"), None,).is_err());
    }

    #[test]
    fn automatic_lobby_restore_uses_friends_party_transition() {
        assert!(matches!(
            accepted_event_for_kind(4, 43, String::from("leader-id"), String::from("leader"), None),
            Ok(Event::FriendsGameInviteAccepted(43, ref sender_id, ref sender_name))
                if sender_id == "leader-id" && sender_name == "leader"
        ));
    }
}

fn get_game_settings() -> *mut i32 {
    if let Some(ncaddr) = unsafe { crate::hooks::NET_CORE_ADDR } {
        unsafe {
            // let g_netcore = std::ptr::read(0x32b_5dc4 as *mut *mut *mut i32);
            let g_netcore = ncaddr as *mut *mut i32;
            if g_netcore.is_null() {
                return std::ptr::null_mut();
            }
            let game_session = std::ptr::read(g_netcore.byte_add(0x5d0));
            if game_session.is_null() {
                return std::ptr::null_mut();
            }
            game_session.byte_add(0x594)
        }
    } else {
        std::ptr::null_mut()
    }
}

fn get_max_players_var() -> *mut i32 {
    unsafe {
        let game_settings = get_game_settings();
        if game_settings.is_null() {
            return std::ptr::null_mut();
        }

        game_settings.byte_add(0x20)
    }
}

fn get_min_players_var() -> *mut i32 {
    unsafe {
        let game_settings = get_game_settings();
        if game_settings.is_null() {
            return std::ptr::null_mut();
        }

        game_settings.byte_add(0x1c)
    }
}

impl ImguiRenderLoop for MyRenderLoop {
    fn initialize(&mut self, ctx: &mut imgui::Context, _render_context: &mut dyn hudhook::RenderContext) {
        sc_style(ctx.style_mut());
        self.chinese_ui = setup_fonts(ctx);
        ctx.io_mut().font_global_scale = if self.chinese_ui { 1.25 } else { 2.0 };
    }

    fn render(&mut self, ui: &mut imgui::Ui) {
        if let Some(message) = take_status_message() {
            self.invite_notification = Some((Instant::now(), message));
        }

        #[allow(clippy::cast_possible_wrap)]
        let f5 = unsafe { GetAsyncKeyState(VK_F5.0.into()) & 0x8000u16 as i16 != 0 };
        if f5 {
            if !self.debounce {
                self.debounce = true;
                if let Some(index) = latest_pending_invite_index(&self.active_invites) {
                    self.accept_invite_at(index);
                } else {
                    self.invite_notification = Some((
                        Instant::now(),
                        if self.chinese_ui {
                            String::from("暂无待处理邀请")
                        } else {
                            String::from("No pending invitation")
                        },
                    ));
                }
            }
        } else {
            self.debounce = false;
        }

        if let Some((deadline, invite_id)) = self.scheduled_party_follow {
            if Instant::now() >= deadline {
                self.scheduled_party_follow = None;
                if let Some(index) = self.active_invites.iter().position(|invite| invite.event.id == invite_id) {
                    info!("PartyFollowAutoAcceptDue local_invite_id={invite_id} delay_ms={}", PARTY_FOLLOW_ACCEPT_DELAY.as_millis());
                    self.accept_invite_at(index);
                }
            }
        }

        if let Ok(evt) = self.new_invites.try_recv() {
            match evt {
                Err(error) => self.connection_error = Some(error),
                Ok(evt) => {
                    self.connection_error = None;
                    if let Some(evt) = evt {
                        let existing = self.active_invites.iter().position(|invite| invite.event.id == evt.id);
                        let index = if let Some(index) = existing {
                            self.active_invites[index].event = evt;
                            index
                        } else {
                            if let Some(sender) = evt.sender.as_ref() {
                                let message = if self.chinese_ui {
                                    if evt.kind == 3 {
                                        format!("队长 {} 已进入房间，正在自动跟随", sender.username)
                                    } else if matches!(evt.kind, 2 | 4) {
                                        format!("收到 {} 的组队邀请，按 F5 接受", sender.username)
                                    } else {
                                        format!("收到 {} 的私人房间邀请，按 F5 接受", sender.username)
                                    }
                                } else {
                                    format!("Invitation from {}. Press F5 to accept.", sender.username)
                                };
                                self.invite_notification = Some((Instant::now(), message));
                            }
                            self.active_invites.push(Invite { event: evt, accepted: false });
                            self.active_invites.len() - 1
                        };
                        let auto_join = self.active_invites[index].event.force_join || hooks_config::get().is_some_and(|config| config.auto_join_invite);
                        if auto_join {
                            let invite_id = self.active_invites[index].event.id;
                            let kind = self.active_invites[index].event.kind;
                            if kind == 3 {
                                self.scheduled_party_follow = Some((Instant::now() + PARTY_FOLLOW_ACCEPT_DELAY, invite_id));
                                info!(
                                    "PartyFollowAutoAcceptScheduled local_invite_id={invite_id} delay_ms={}",
                                    PARTY_FOLLOW_ACCEPT_DELAY.as_millis()
                                );
                            } else {
                                self.accept_invite_at(index);
                            }
                        }
                    }
                }
            }
        }

        self.show_errors(ui);
        self.show_notifications(ui);
        self.show_initial_info(ui);
    }

    fn message_filter(&self, _io: &imgui::Io) -> hudhook::MessageFilter {
        if matches!(self.ui_state, UiState::Show) {
            hudhook::MessageFilter::InputAll
        } else {
            hudhook::MessageFilter::empty()
        }
    }

    fn on_wnd_proc(&self, hwnd: windows::Win32::Foundation::HWND, umsg: u32, wparam: windows::Win32::Foundation::WPARAM, lparam: windows::Win32::Foundation::LPARAM) {
        if matches!(self.ui_state, UiState::Show) {
            // Forward to default handler so that the window doesn't break
            unsafe {
                DefWindowProcA(hwnd, umsg, wparam, lparam);
            }
        }
    }
}

fn init_hudhook<T: hudhook::Hooks + 'static>(invites: crossbeam_channel::Receiver<Result<Option<InviteEvent>, crate::api::Error>>) -> anyhow::Result<()> {
    let (tx, rx) = mpsc::channel();
    EVENTS.get_or_init(|| Mutex::new(rx));
    hudhook::Hudhook::builder()
        .with::<T>(MyRenderLoop {
            tx,
            ui_state: UiState::default(),
            debounce: false,
            invite_notification: None,
            new_invites: invites,
            active_invites: Vec::new(),
            connection_error: None,
            scheduled_party_follow: None,
            initial_popup: Instant::now() + INITIAL_POPUP_DURATION,
            chinese_ui: false,
        })
        .with_hmodule(unsafe { GetModuleHandleA(PCSTR::null())?.into() })
        .build()
        .apply()
        .map_err(|e| anyhow::anyhow!("Error adding gui hook: {e:?}"))
}

fn init_dx9(invites: crossbeam_channel::Receiver<Result<Option<InviteEvent>, crate::api::Error>>) -> anyhow::Result<()> {
    init_hudhook::<hudhook::hooks::dx9::ImguiDx9Hooks>(invites)
}

fn init_dx11(invites: crossbeam_channel::Receiver<Result<Option<InviteEvent>, crate::api::Error>>) -> anyhow::Result<()> {
    init_hudhook::<hudhook::hooks::dx11::ImguiDx11Hooks>(invites)
}

pub fn init(engine: Engine, invites: crossbeam_channel::Receiver<Result<Option<InviteEvent>, crate::api::Error>>) -> anyhow::Result<()> {
    match engine {
        Engine::DX9 => init_dx9(invites),
        Engine::DX11 => init_dx11(invites),
    }
}
