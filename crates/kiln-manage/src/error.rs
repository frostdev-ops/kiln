//! One error type for the crate and for every Tauri command: `{code, message, detail}`.
//! The serialized shape and the code list are a contract: docs/kiln/launcher/contracts.md.

use serde::{Deserialize, Serialize};
use std::fmt;

/// Machine-readable error kinds. Serialized in snake_case (`"not_implemented"`). Add new codes at the end.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    /// The function exists but has no implementation yet.
    NotImplemented,
    /// A bad argument from the caller (id format, empty path, ...).
    InvalidArgument,
    /// The thing named does not exist (profile, game, mod, file, server, session).
    NotFound,
    /// Creating something that exists already.
    AlreadyExists,
    /// File system error (detail: the OS error).
    Io,
    /// A file could not be parsed (JSON, INI, VDF, PE, zip).
    Parse,
    /// A file parsed but broke its format's rules (detail: the violations).
    Schema,
    /// Concurrent change: e.g. a profile saved with an old generation.
    Conflict,
    /// Refused because the game is running.
    GameRunning,
    /// Refused because Steam is updating or validating the game.
    SteamUpdating,
    /// The OS refused access.
    Permission,
    /// Not possible on this platform or setup (e.g. no Z: drive and copy mode impossible).
    Unsupported,
    /// Network failure (download, TCP, SSH).
    Network,
    /// A sha256 / kdh1 did not match.
    HashMismatch,
    /// An action that runs remote code needs the user's explicit consent first.
    ConsentRequired,
    /// Refused on purpose (e.g. a ShroudForge proxy, a non-https URL, a modpack without the mods it lists).
    Refused,
    Timeout,
    Canceled,
    /// Too many requests / tasks in flight.
    Busy,
    /// KAP/1 handshake failed (wrong token).
    AuthFailed,
    /// No admin transport reaches the server (not running, no Kiln.Admin, no endpoint).
    AdminUnavailable,
    /// A Kiln.Admin op answered ok=false (message: its error text).
    OpFailed,
    /// A bug.
    Internal,
    /// The launcher's data folder cannot be used: the pointer file (`data-folder.json` in the default root) cannot be
    /// read, is newer, or names a folder that is missing, unreadable or not a Kiln data folder (detail: `{kind,
    /// pointer, path}`). Never answered with the default root or an empty folder.
    DataFolder,
}

impl ErrorCode {
    pub fn as_str(self) -> &'static str {
        match self {
            ErrorCode::NotImplemented => "not_implemented",
            ErrorCode::InvalidArgument => "invalid_argument",
            ErrorCode::NotFound => "not_found",
            ErrorCode::AlreadyExists => "already_exists",
            ErrorCode::Io => "io",
            ErrorCode::Parse => "parse",
            ErrorCode::Schema => "schema",
            ErrorCode::Conflict => "conflict",
            ErrorCode::GameRunning => "game_running",
            ErrorCode::SteamUpdating => "steam_updating",
            ErrorCode::Permission => "permission",
            ErrorCode::Unsupported => "unsupported",
            ErrorCode::Network => "network",
            ErrorCode::HashMismatch => "hash_mismatch",
            ErrorCode::ConsentRequired => "consent_required",
            ErrorCode::Refused => "refused",
            ErrorCode::Timeout => "timeout",
            ErrorCode::Canceled => "canceled",
            ErrorCode::Busy => "busy",
            ErrorCode::AuthFailed => "auth_failed",
            ErrorCode::AdminUnavailable => "admin_unavailable",
            ErrorCode::OpFailed => "op_failed",
            ErrorCode::Internal => "internal",
            ErrorCode::DataFolder => "data_folder",
        }
    }
}

/// `{code, message, detail}`: what every command rejects with (Tauri serializes it as is).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Error {
    pub code: ErrorCode,
    /// One sentence for the user.
    pub message: String,
    /// Anything that helps (paths, OS error text, a list of violations). Omitted when none.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<serde_json::Value>,
}

pub type Result<T, E = Error> = std::result::Result<T, E>;

impl Error {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Error { code, message: message.into(), detail: None }
    }

    /// What wave-0 stubs return: `what` names the function (e.g. `"profile::activate"`).
    pub fn not_implemented(what: &str) -> Self {
        Error::new(ErrorCode::NotImplemented, format!("{what} is not implemented yet"))
    }

    pub fn with_detail(mut self, detail: impl Into<serde_json::Value>) -> Self {
        self.detail = Some(detail.into());
        self
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Error::new(ErrorCode::InvalidArgument, message)
    }

    pub fn not_found(message: impl Into<String>) -> Self {
        Error::new(ErrorCode::NotFound, message)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code.as_str(), self.message)
    }
}

impl std::error::Error for Error {}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        let code = match e.kind() {
            std::io::ErrorKind::NotFound => ErrorCode::NotFound,
            std::io::ErrorKind::PermissionDenied => ErrorCode::Permission,
            std::io::ErrorKind::AlreadyExists => ErrorCode::AlreadyExists,
            std::io::ErrorKind::TimedOut => ErrorCode::Timeout,
            _ => ErrorCode::Io,
        };
        Error::new(code, e.to_string())
    }
}

impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::new(ErrorCode::Parse, e.to_string())
    }
}
