//! Launcher events (contracts.md section 10). Emit with `app.emit(name, payload)`; the
//! TypeScript side is `KilnEvents` / `onKilnEvent` in launcher/src/api/commands.ts.

use serde::{Deserialize, Serialize};

pub const TASK: &str = "kiln://task";
pub const GAME: &str = "kiln://game";
pub const LOG: &str = "kiln://log";
pub const LAST_SERVER: &str = "kiln://last-server";
pub const SERVER: &str = "kiln://server";
pub const ADMIN: &str = "kiln://admin";
/// `kiln://activation {gameId}`: a profile was written again without a profile_activate call (after a profile edit, a
/// change outside the launcher, the watcher's start-up pass) and what it kept in the game folder waits for the window:
/// `activation_notices` hands those activations out once (commands::profile::queue_notices).
pub const ACTIVATION: &str = "kiln://activation";

/// `kiln://task` carries a whole [`crate::commands::app::Task`].
pub type TaskEvent = crate::commands::app::Task;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GameEventKind {
    Started,
    Stopped,
    Boot,
    Mods,
    Native,
    Status,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameEvent {
    pub game_id: String,
    pub kind: GameEventKind,
    pub pid: Option<u32>,
    pub status: Option<kiln_manage::game::GameStatus>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogEvent {
    pub subscription: String,
    pub target: crate::commands::logs::LogTarget,
    pub file: String,
    pub lines: Vec<String>,
    pub next: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LastServerEvent {
    pub game_id: String,
    pub last_server: kiln_manage::state::LastServer,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerEvent {
    pub server_id: String,
    pub status: kiln_manage::server::ServerStatus,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminEventPayload {
    pub session: String,
    pub server_id: String,
    pub event: kiln_manage::admin::client::AdminEvent,
}
