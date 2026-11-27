//! "Open web panel" (Servers Overview): opens a server's web panel (Kilnwarden, or Kiln Web Admin, its old name:
//! [`ids`]) in the browser, signed in as the owner.
//!
//! (0.2.0-dev.31.12) Every panel opens through its own gateway ([`gateway`]) on its own loopback address
//! ([`origins`]), so:
//! - panels of different servers never share cookies (a browser keeps cookies per host, not per port);
//! - the browser's connection takes the path the launcher pinned the certificate on: to an address of this machine,
//!   or through ssh to the remote server; a panel on another machine's address is refused;
//! - the launcher binds the port and holds it (no free-port race), for this user's programs only (Linux);
//! - the one-time login never appears on a command line: the browser gets a one-use bootstrap address instead;
//! - the gateway (and its ssh connections) closes when the panel's tab is closed or idle, when "Close" is pressed, or
//!   when the launcher exits;
//! - `launcher-endpoint.json` is read from the profile's data folder when the server booted with `dataDir: profile`,
//!   and the one-time login is minted by the Kiln.Admin of that same folder (the data root is read once per open);
//! - a server's origin belongs to its panel's certificate too: a panel with another certificate (another panel the
//!   server's files point at) gets another address, so cookies one panel set never reach another.
//!
//! (0.2.0-dev.31.19) When `launcher-endpoint.json` names the panel's address on the internet (`public`, Kilnwarden 0.1.4:
//! hosted access `https://<name>.kiln.nexus/` or an own address, only while it really works), [`Route::Auto`] opens that
//! instead, after a check of a few seconds ([`public`]: the certificate the file names, the owner exists, the launcher's
//! login is on). The one-time login is minted the same way and goes there through a one-use bootstrap address of its own
//! ([`gateway::offer_once`]); no gateway is started. If the check fails, the panel opens on this computer as before, and
//! [`WebPanelInfo::fallback`] says why. [`Route::Local`] ("Open on this computer instead") always opens it here. Remote
//! servers work the same: the file comes over ssh, the check goes straight to the public address, the login is minted
//! through the ssh tunnel. (0.2.0-dev.31.20) A server's "Always open the web panel on this computer" ([`settings`],
//! kept in the launcher's data folder) makes [`Route::Auto`] [`Route::Local`] for it, with no check at all.

pub mod endpoint;
pub mod gateway;
pub mod ids;
pub mod origins;
pub mod pin;
pub mod public;
pub mod settings;
/// The same-user check ([`crate::admin::peer`]; the SSH tunnel uses it too).
pub use crate::admin::peer;

use crate::admin::client::AdminClient;
use crate::server::instance::ServerInstance;
use crate::{Error, ErrorCode, Result};
use gateway::{Gateway, Limits, Upstream};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::PathBuf;

/// How the browser reaches the panel.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Via {
    /// Through the gateway, to an address of this machine.
    Local,
    /// Through the gateway and `ssh -W`.
    Ssh,
    /// (0.2.0-dev.31.19) Straight to the panel's address on the internet (no gateway).
    Hosted,
}

/// (0.2.0-dev.31.19) Which address "Open web panel" opens.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Route {
    /// The panel's address on the internet when its file names one and the check passes, else this computer's.
    #[default]
    Auto,
    /// This computer's, through the gateway ("Open on this computer instead").
    Local,
}

/// An open panel, as the window shows it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebPanelInfo {
    pub server_id: String,
    /// `https://127.x.y.z:port` (or `http://` for a panel without TLS): this server's own origin.
    pub origin: String,
    /// The pinned certificate's SHA-256 (hex), None for a panel without TLS.
    pub fingerprint: Option<String>,
    pub via: Via,
    /// False when this server's own loopback address could not be used (127.0.0.1 then: cookies shared).
    pub isolated: bool,
    /// The gateway closes this long after the last browser connection ends.
    pub idle_close_s: u64,
    /// Browser connections open now.
    pub connections: usize,
    /// (0.2.0-dev.31.19) The address on the internet this open sent the browser to (`via: hosted`); None for a gateway.
    pub public: Option<String>,
    /// (0.2.0-dev.31.19) The panel's file named an address on the internet, but its check failed, so this open went to
    /// this computer instead: why. None otherwise (and for [`Route::Local`]).
    pub fallback: Option<public::Fallback>,
}

/// What `open` did: the address to hand the browser (one use; it holds no secret), and the panel.
#[derive(Debug, Clone)]
pub struct Opened {
    pub url: String,
    pub info: WebPanelInfo,
}

struct Open {
    gateway: Gateway,
    fingerprint: Option<String>,
}

/// The launcher's open panels, one gateway per server.
pub struct WebPanels {
    registry: PathBuf,
    /// (0.2.0-dev.31.20) `web-panel-settings.json`, beside the registry ([`settings::FILE`]).
    settings: PathBuf,
    limits: Limits,
    open: HashMap<String, Open>,
    public_timeout: std::time::Duration,
}

impl WebPanels {
    /// `registry`: the launcher data folder's `web-panel-origins.json` ([`origins::FILE`]); each server's web panel
    /// settings are read from the same folder ([`settings::FILE`]).
    pub fn new(registry: PathBuf) -> WebPanels {
        Self::with_limits(registry, Limits::default())
    }

    pub fn with_limits(registry: PathBuf, limits: Limits) -> WebPanels {
        let settings = registry.with_file_name(settings::FILE);
        WebPanels { registry, settings, limits, open: HashMap::new(), public_timeout: public::TIMEOUT }
    }

    fn info(&self, id: &str, o: &Open) -> WebPanelInfo {
        let addr = o.gateway.addr();
        WebPanelInfo {
            server_id: id.to_string(),
            origin: format!("{}://{addr}", if o.fingerprint.is_some() { "https" } else { "http" }),
            fingerprint: o.fingerprint.clone(),
            via: if matches!(o.gateway.upstream, Upstream::Ssh(..)) { Via::Ssh } else { Via::Local },
            isolated: o.gateway.isolated,
            idle_close_s: self.limits.no_connection.as_secs(),
            connections: o.gateway.connections(),
            public: None,
            fallback: None,
        }
    }

    /// [`Self::open_via`] with [`Route::Auto`].
    pub fn open(&mut self, server: &ServerInstance) -> Result<Opened> {
        self.open_via(server, Route::Auto)
    }

    /// Opens the server's panel: at its address on the internet when `route` allows it, the file names one and its
    /// check passes; else through the server's gateway (opened or reused), with the panel's certificate checked through
    /// it. Mints the one-time login and returns the bootstrap address for the browser. (0.2.0-dev.31.20) While the
    /// server's "Always open the web panel on this computer" is on ([`settings`]), [`Route::Auto`] is [`Route::Local`]:
    /// the address on the internet is not even checked.
    pub fn open_via(&mut self, server: &ServerInstance, route: Route) -> Result<Opened> {
        self.sweep();
        let route = if route == Route::Auto && self.settings(&server.id).always_local { Route::Local } else { route };
        // one data root for everything below: the panel's listener and the Kiln.Admin that mints its login
        let root = endpoint::root(server)?;
        let (panel, ep) = endpoint::find_in(server, &root)?;
        let mut fallback = None;
        if let (Route::Auto, Some(address)) = (route, &ep.public) {
            match public::check(address, self.public_timeout) {
                Ok(()) => {
                    let secret = mint(server, &root, panel)?;
                    let url = gateway::offer_once(format!("{}launcher#secret={secret}", address.url), self.limits.handle_ttl)?;
                    let info = WebPanelInfo {
                        server_id: server.id.clone(),
                        origin: address.url.trim_end_matches('/').to_string(),
                        fingerprint: Some(address.sha256.clone()),
                        via: Via::Hosted,
                        isolated: true,
                        idle_close_s: 0,
                        connections: 0,
                        public: Some(address.url.clone()),
                        fallback: None,
                    };
                    return Ok(Opened { url, info });
                }
                Err(why) => fallback = Some(why),
            }
        }
        let mut opened = self.open_local(server, &root, panel, &ep)?;
        opened.info.fallback = fallback;
        Ok(opened)
    }

    fn open_local(&mut self, server: &ServerInstance, root: &crate::admin::datadir::DataRoot, panel: ids::PanelMod, ep: &endpoint::Endpoint) -> Result<Opened> {
        let target = SocketAddr::new(ep.host, ep.port);
        let upstream = match &server.remote {
            Some(spec) => Upstream::Ssh(spec.clone(), target),
            None if gateway::is_local_address(target.ip()) => Upstream::Direct(target),
            None => return Err(gateway::not_local(target)),
        };
        let reuse = self.open.get(&server.id).is_some_and(|o| o.gateway.alive() && o.gateway.upstream == upstream && o.fingerprint == ep.sha256);
        if !reuse {
            if let Some(old) = self.open.remove(&server.id) {
                old.gateway.close();
            }
            let key = origins::key(&server.id, ep.sha256.as_deref());
            let slot = origins::slot(&self.registry, &key)?;
            let gw = Gateway::start(slot.ip, slot.port, upstream, self.limits)?;
            if gw.isolated && gw.addr().port() != slot.port {
                let _ = origins::remember_port(&self.registry, &key, gw.addr().port());
            }
            self.open.insert(server.id.clone(), Open { gateway: gw, fingerprint: ep.sha256.clone() });
        }
        let o = self.open.get(&server.id).expect("just opened");
        let addr = o.gateway.addr();
        let Some(pin) = ep.sha256.clone() else {
            // a panel without TLS: only ever reached on this machine or through ssh, and no secret in the URL
            let info = self.info(&server.id, o);
            return Ok(Opened { url: format!("http://{addr}/signin"), info });
        };
        if let Err(e) = pin::verify(addr, &pin) {
            self.close(&server.id);
            return Err(e);
        }
        let secret = mint(server, root, panel)?;
        let o = self.open.get(&server.id).expect("open");
        let url = o.gateway.offer(format!("https://{addr}/launcher#secret={secret}"), self.limits.handle_ttl)?;
        Ok(Opened { url, info: self.info(&server.id, o) })
    }

    /// (0.2.0-dev.31.20) The server's web panel settings ([`settings::get`]).
    pub fn settings(&self, server_id: &str) -> settings::WebPanelSettings {
        settings::get(&self.settings, server_id)
    }

    /// (0.2.0-dev.31.20) Changes the server's web panel settings ([`settings::set`]); what they are now.
    pub fn set_settings(&self, server_id: &str, s: settings::WebPanelSettings) -> Result<settings::WebPanelSettings> {
        settings::set(&self.settings, server_id, s)
    }

    /// (0.2.0-dev.31.19) How long the check of a panel's address on the internet may take (tests shorten it).
    pub fn set_public_timeout(&mut self, t: std::time::Duration) {
        self.public_timeout = t;
    }

    /// Closes the server's gateway; whether one was open.
    pub fn close(&mut self, server_id: &str) -> bool {
        match self.open.remove(server_id) {
            Some(o) => {
                let alive = o.gateway.alive();
                o.gateway.close();
                alive
            }
            None => false,
        }
    }

    pub fn close_all(&mut self) {
        for (_, o) in self.open.drain() {
            o.gateway.close();
        }
    }

    /// The gateways still open (those that closed by themselves are forgotten).
    pub fn list(&mut self) -> Vec<WebPanelInfo> {
        self.sweep();
        let mut v: Vec<_> = self.open.iter().map(|(id, o)| self.info(id, o)).collect();
        v.sort_by(|a, b| a.server_id.cmp(&b.server_id));
        v
    }

    fn sweep(&mut self) {
        let dead: Vec<String> = self.open.iter().filter(|(_, o)| !o.gateway.alive()).map(|(k, _)| k.clone()).collect();
        for d in dead {
            if let Some(o) = self.open.remove(&d) {
                o.gateway.close();
            }
        }
    }
}

/// The one-time owner login for `panel`, minted by the Kiln.Admin of the data root its file came from.
fn mint(server: &ServerInstance, root: &crate::admin::datadir::DataRoot, panel: ids::PanelMod) -> Result<String> {
    let admin = AdminClient::connect_at(server, root)?;
    let ticket = admin
        .call("login_mint", panel.login_mint_args())
        .map_err(|_| Error::new(ErrorCode::AdminUnavailable, "Kiln.Admin could not issue a one-time web panel login."))?;
    ticket["secret"]
        .as_str()
        .filter(|s| s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit()))
        .map(str::to_string)
        .ok_or_else(|| Error::new(ErrorCode::Parse, "Kiln.Admin returned an invalid one-time login."))
}

#[cfg(test)]
mod public_tests;
#[cfg(test)]
mod review_tests;
#[cfg(test)]
mod tests;
