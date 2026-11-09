//! Nexus Mods: the launcher's online mod catalog and "Mod Manager Download" (nxm://) support.
//! Docs: docs/kiln/launcher/catalog.md (how it works, the rules it follows), catalog-authors.md (how a mod shows up).
//!
//! | module | what |
//! |---|---|
//! | [`api`] | the API client (v3 + v1 validate), app headers, rate limits, a [`api::Transport`] for tests |
//! | [`catalog`] | the mod list, the Kiln-mod rule, details, requirements, installs and updates |
//! | [`install`] | download, check against Nexus's archive manifest, `quick::add`, the install record |
//! | [`nxm`] | `nxm://` link parsing |
//! | [`handler`] | registering the launcher as the nxm handler (opt-in), forwarding other games, the link inbox |
//! | [`keystore`] | OAuth credentials: OS keyring, else a 0600 file |
//! | [`oauth`] | Public-client PKCE browser sign-in and serialized token refresh/logout |
//! | [`version`] | comparing Nexus's free-text versions |
//!
//! Files: `<data>/nexus/` holds `oauth.json` (only without a keyring), `handler.json`, `verdicts.json`,
//! `installs.json`, `latest.json`, `inbox/`, `downloads/`, `instance.lock`, and state transaction locks.

pub mod api;
pub mod catalog;
pub mod handler;
pub mod install;
pub mod keystore;
pub mod oauth;
pub mod nxm;
pub mod version;

#[cfg(all(test, feature = "zip"))]
mod tests;

/// The largest file Kiln downloads (as for modpacks).
pub const MAX_DOWNLOAD: u64 = 2 * 1024 * 1024 * 1024;

/// A client with the real transport (feature `http`).
#[cfg(feature = "http")]
pub fn client(token: Option<String>, app_version: &str) -> api::Client {
    let app = api::AppId::kiln_launcher(app_version);
    let t = std::sync::Arc::new(api::UreqTransport::new(&app.user_agent()));
    api::Client::new(t, token, app)
}
