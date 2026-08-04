//! Implements the `GameSessionExProtocolServer` for extended game session management,
//! including advanced session searching capabilities.

use std::collections::hash_map::Entry;
use std::collections::HashMap;
use std::sync::Arc;

use quazal::prudp::ClientRegistry;
use quazal::rmc::types::Property;
use quazal::rmc::types::QList;
use quazal::rmc::Error;
use quazal::rmc::Protocol;
use quazal::ClientInfo;
use quazal::Context;
use slog::Logger;

use crate::login_required;
use crate::protocols::game_session_ex_service::game_session_ex_protocol::GameSessionExProtocolServer;
use crate::protocols::game_session_ex_service::game_session_ex_protocol::GameSessionExProtocolServerTrait;
use crate::protocols::game_session_ex_service::game_session_ex_protocol::SearchSessionsRequest;
use crate::protocols::game_session_ex_service::game_session_ex_protocol::SearchSessionsResponse;
use crate::protocols::game_session_ex_service::types::GameSessionSearchResultEx;
use crate::protocols::game_session_service::types::GameSessionKey;
use crate::protocols::game_session_service::types::GameSessionParticipant;
use crate::protocols::game_session_service::types::GameSessionSearchResult;
use crate::storage::Storage;
use crate::storage::INVITE_KIND_LOBBY_PARTY;
use crate::storage::INVITE_KIND_LOBBY_RESTORE;
use crate::storage::INVITE_KIND_PRIVATE_ROOM;

/// Implementation of the `GameSessionExProtocolServerTrait` for extended game session operations.
struct GameSessionExProtocolServerImpl {
    storage: Arc<Storage>,
}

fn is_invitation_search_query(query_id: u32) -> bool {
    query_id == 8
}

/// Capacity-related values are authored by the unmodified game with the
/// retail limits (notably coop attribute 105 = 2). SCBL enforces the expanded
/// limit in the game process and records joins without a server-side capacity
/// gate, so these legacy values must not make an otherwise compatible room
/// disappear from matchmaking.
///
/// Public/private slot attributes 3 and 4 intentionally remain exact-match:
/// they describe room visibility and invitation semantics, not just capacity.
fn matchmaking_attribute_must_match(id: u32) -> bool {
    !matches!(id, 105 | 112)
}

impl<CI> GameSessionExProtocolServerTrait<CI> for GameSessionExProtocolServerImpl {
    /// Handles the `SearchSessions` request, providing extended search capabilities for game sessions.
    ///
    /// This function requires the client to be logged in. It filters sessions based on
    /// various attributes and returns detailed session information.
    fn search_sessions(
        &self,
        logger: &Logger,
        _ctx: &Context,
        ci: &mut ClientInfo<CI>,
        request: SearchSessionsRequest,
        _client_registry: &ClientRegistry<CI>,
        _socket: &std::net::UdpSocket,
    ) -> Result<SearchSessionsResponse, Error> {
        #![allow(clippy::unreadable_literal)]

        let user_id = login_required(&*ci)?;
        info!(logger, "Client searches for session: {:?}", request);
        let query_id = request.game_session_query.query_id;
        let session_type = request.game_session_query.type_id;
        let invited_session = if is_invitation_search_query(query_id) {
            let invited = rmc_err!(
                self.storage.find_accepted_manual_invited_session(user_id, session_type),
                logger,
                "Error resolving accepted manual invitation target"
            )?;
            if invited
                .as_ref()
                .is_some_and(|invite| matches!(invite.kind, INVITE_KIND_PRIVATE_ROOM | INVITE_KIND_LOBBY_PARTY | INVITE_KIND_LOBBY_RESTORE))
            {
                self.storage.note_manual_invitation_search(user_id, session_type);
            } else {
                self.storage.clear_manual_invitation_search(user_id, session_type);
            }
            invited
        } else {
            self.storage.clear_manual_invitation_search(user_id, session_type);
            None
        };
        let invitation_search = invited_session.is_some();
        let sessions = if let Some(invited) = invited_session {
            info!(
                logger,
                "InviteSearchMatched";
                "invite_id" => invited.invite_id,
                "kind" => invited.kind,
                "receiver_id" => user_id,
                "query_id" => query_id,
                "session_type" => invited.session.session_type,
                "session_id" => invited.session.session_id,
                "host_pid" => invited.session.creator_id,
            );
            vec![invited.session]
        } else {
            rmc_err!(self.storage.search_sessions(session_type, Some(user_id)), logger, "Error searching game sessions")?
        };
        // search svm
        // 103 => 2165463540
        // 106 => 3564829
        // 107 => 3909881133
        // 108 => 0
        // 109 => 0
        // 110 => 0
        // 112 => 1
        // search coop
        // 106 => 3564829
        // 107 => 3909881133
        // 108 => 0
        // 109 => 0
        // 110 => 0
        // coop: 113 => 0;109 => 0;110 => 0;106 => 3564829;107 => 3909881133;108 => 0;3 => 2;4 => 0;101 => 3578398534;102 => 3;103 => 0;105 => 2;112 => 2
        // svm:  113 => 0;109 => 0;110 => 0;106 => 3564829;107 => 3909881133;108 => 0;3 => 4;4 => 0;101 => 72621668;102 => 8;103 => 2165463540;105 => 0;112 => 2
        // coop: 113 => 0;109 => 0;110 => 0;106 => 3564829;107 => 3909881133;108 => 0;3 => 0;4 => 2;101 => 3578398534;102 => 3;103 => 0;105 => 2;112 => 2
        // coop: 113 => 0;109 => 0;110 => 0;106 => 3564829;107 => 3909881133;108 => 0;3 => 0;4 => 2;101 => 1328467886;102 => 5;103 => 0;105 => 2;112 => 2
        // coop: 113 => 0;109 => 0;110 => 0;106 => 3564829;107 => 3909881133;108 => 0;3 => 0;4 => 2;101 => 2573003522;102 => 4;103 => 0;105 => 2;112 => 2

        // 101 => might be map id
        // 102 => might be game mode
        // 4 => might be number of players? (svm)
        // 112 => might be number of players? (svm) first is set to 4, but 8 after opening and closening the match settings
        let mut req_attrs = request.game_session_query.parameters.0.into_iter().map(|p| (p.id, p.value)).collect::<HashMap<_, _>>();
        // if not set assume 0 (required for keeping coop and svm apart)
        if let Entry::Vacant(entry) = req_attrs.entry(103) {
            entry.insert(0);
        }
        let sessions: Vec<_> = sessions
            .into_iter()
            .filter_map(|session| {
                if invitation_search {
                    return Some(session);
                }
                let sess_attrs: QList<Property> = match session.attributes.parse() {
                    Ok(value) => value,
                    Err(_) => {
                        warn!(
                            logger,
                            "Ignoring game session with malformed attributes";
                            "session_id" => session.session_id,
                            "session_type" => session.session_type,
                        );
                        return None;
                    }
                };
                let sess_attrs = sess_attrs.0.into_iter().map(|p| (p.id, p.value)).collect::<HashMap<_, _>>();
                for (id, value) in &req_attrs {
                    if !matchmaking_attribute_must_match(*id) {
                        // 105 carries the retail coop capacity (2); 112 also
                        // differs between create/search requests. Neither may
                        // act as a server-side admission gate in SCBL.
                        continue;
                    }
                    if sess_attrs.get(id) != Some(value) {
                        return None;
                    }
                }
                Some(session)
            })
            .collect();
        info!(logger, "Found sessions {sessions:?}");
        Ok(SearchSessionsResponse {
            search_results: QList(
                sessions
                    .into_iter()
                    .filter_map(|session| {
                        let attributes = match session.attributes.parse() {
                            Ok(value) => value,
                            Err(_) => {
                                warn!(
                                    logger,
                                    "Ignoring game session whose response attributes cannot be encoded";
                                    "session_id" => session.session_id,
                                    "session_type" => session.session_type,
                                );
                                return None;
                            }
                        };
                        let host_urls = session
                            .participants
                            .iter()
                            .filter(|p| p.user_id == session.creator_id)
                            .flat_map(|p| p.station_urls.iter())
                            .filter_map(|url| {
                                let parsed = url.parse().ok();
                                if parsed.is_none() {
                                    warn!(
                                        logger,
                                        "Ignoring malformed host station URL";
                                        "session_id" => session.session_id,
                                        "host_pid" => session.creator_id,
                                        "url" => url.clone(),
                                    );
                                }
                                parsed
                            })
                            .collect();
                        let participants = session
                            .participants
                            .into_iter()
                            .filter_map(|participant| {
                                let station_urls = match participant.station_urls.try_into() {
                                    Ok(value) => value,
                                    Err(_) => {
                                        warn!(
                                            logger,
                                            "Ignoring participant with an invalid station URL list";
                                            "session_id" => session.session_id,
                                            "participant_pid" => participant.user_id,
                                        );
                                        return None;
                                    }
                                };
                                Some(GameSessionParticipant {
                                    pid: participant.user_id,
                                    name: participant.name,
                                    station_urls,
                                })
                            })
                            .collect();

                        Some(GameSessionSearchResultEx {
                            game_session_search_result: GameSessionSearchResult {
                                session_key: GameSessionKey {
                                    session_id: session.session_id,
                                    type_id: session.session_type,
                                },
                                host_pid: session.creator_id,
                                host_urls,
                                attributes,
                            },
                            participants: QList(participants),
                        })
                    })
                    .collect(),
            ),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::is_invitation_search_query;
    use super::matchmaking_attribute_must_match;

    #[test]
    fn only_query_eight_is_a_manual_invitation_lookup() {
        assert!(is_invitation_search_query(8));
        for query_id in [0, 1, 2, 7, 9, 10, u32::MAX] {
            assert!(!is_invitation_search_query(query_id));
        }
    }

    #[test]
    fn retail_capacity_attributes_do_not_gate_extended_matchmaking() {
        assert!(!matchmaking_attribute_must_match(105));
        assert!(!matchmaking_attribute_must_match(112));
    }

    #[test]
    fn visibility_and_mode_attributes_still_match_exactly() {
        assert!(matchmaking_attribute_must_match(3));
        assert!(matchmaking_attribute_must_match(4));
        assert!(matchmaking_attribute_must_match(101));
        assert!(matchmaking_attribute_must_match(103));
    }
}

pub fn new_protocol<T: 'static>(storage: Arc<Storage>) -> Box<dyn Protocol<T>> {
    Box::new(GameSessionExProtocolServer::new(GameSessionExProtocolServerImpl { storage }))
}
