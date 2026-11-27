//! `launcher-endpoint.json` (the web panel mod writes it while its panel listens, docs/protocol-relay-broker.md §7):
//! `{"format":1,"host":"<address>","port":<port>,"sha256":"<certificate SHA-256 hex>"|null}`, in the mod's data folder:
//! Kilnwarden's (`kilnwarden`) or, before its rename, Kiln Web Admin's (`kiln-webadmin`), Kilnwarden first ([`super::ids`]).
//! That folder follows the profile's `dataDir` (`DataDirMode::Profile`): see [`crate::admin::datadir`].
//!
//! (0.2.0-dev.31.19) Kilnwarden 0.1.4 adds, still `format` 1, the panel's address on the internet while it really works
//! (hosted access `https://<name>.kiln.nexus/`, or an own address) and the certificate it serves there:
//! `"public":"https://<name>[:<port>]/","publicSha256":"<hex>"` ([`PublicAddress`]). Both or neither; a `public` this
//! launcher won't use (not `https://<DNS name>[:port]/`, no valid `publicSha256`) is left out and the panel opens on
//! this computer as before. Launchers before 31.19 ignore both fields.

use super::ids::{self, PanelMod};
use crate::admin::datadir;
use crate::server::instance::ServerInstance;
use crate::{Error, Result};
use serde::Deserialize;
use std::net::IpAddr;

pub const FILE: &str = ids::ENDPOINT_FILE;

#[derive(Debug, PartialEq, Eq)]
pub struct Endpoint {
    pub format: u8,
    pub host: IpAddr,
    pub port: u16,
    pub sha256: Option<String>,
    /// (0.2.0-dev.31.19) The panel's address on the internet, while it really works (None: the file names none, or one
    /// this launcher won't use).
    pub public: Option<PublicAddress>,
}

/// The file as written; `public` and `publicSha256` are checked apart, so a bad one never hides the local address.
#[derive(Deserialize)]
struct Raw {
    format: u8,
    host: IpAddr,
    port: u16,
    sha256: Option<String>,
    #[serde(default)]
    public: Option<serde_json::Value>,
    #[serde(default, rename = "publicSha256")]
    public_sha256: Option<serde_json::Value>,
}

/// (0.2.0-dev.31.19) The panel's address on the internet (`launcher-endpoint.json` `public`): `https://<host>[:<port>]/`
/// and the leaf certificate's SHA-256 served there.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublicAddress {
    /// `https://<host>/` or `https://<host>:<port>/` (the host in lower case, the port only when it isn't 443).
    pub url: String,
    pub host: String,
    pub port: u16,
    /// Lower-case hex, no colons.
    pub sha256: String,
}

impl PublicAddress {
    /// `https://` + a DNS name (no IP literal, no user, no query or fragment) + an optional port + `/`, and a SHA-256;
    /// None for anything else.
    pub fn parse(url: &str, sha256: &str) -> Option<PublicAddress> {
        let pin = sha256.replace(':', "").to_ascii_lowercase();
        if pin.len() != 64 || !pin.bytes().all(|b| b.is_ascii_hexdigit()) || url.len() > 300 {
            return None;
        }
        let authority = url.strip_prefix("https://")?.strip_suffix('/')?;
        let (host, port) = match authority.rsplit_once(':') {
            Some((h, p)) if !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()) => (h, p.parse::<u16>().ok().filter(|p| *p != 0)?),
            Some(_) => return None,
            None => (authority, 443),
        };
        let host = host.to_ascii_lowercase();
        let label_ok = |l: &str| !l.is_empty() && l.len() <= 63 && !l.starts_with('-') && !l.ends_with('-') && l.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-');
        // a DNS name: labels of letters, digits and '-'; not all digits and dots (an IPv4 literal); nothing else at all
        if host.is_empty() || host.len() > 253 || !host.split('.').all(label_ok) || host.bytes().all(|b| b.is_ascii_digit() || b == b'.') {
            return None;
        }
        let url = if port == 443 { format!("https://{host}/") } else { format!("https://{host}:{port}/") };
        Some(PublicAddress { url, host, port, sha256: pin })
    }
}

impl Endpoint {
    pub fn parse(bytes: &[u8]) -> Result<Self> {
        let invalid = || Error::invalid("invalid web panel listener description");
        if bytes.len() > 4096 {
            return Err(invalid());
        }
        let raw: Raw = serde_json::from_slice(bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(bytes)).map_err(|_| invalid())?;
        let public = match (&raw.public, &raw.public_sha256) {
            (Some(serde_json::Value::String(u)), Some(serde_json::Value::String(h))) => PublicAddress::parse(u, h),
            _ => None,
        };
        let mut endpoint = Endpoint { format: raw.format, host: raw.host, port: raw.port, sha256: raw.sha256, public };
        // `::ffff:a.b.c.d` is a.b.c.d: checked (and connected to) as that
        endpoint.host = endpoint.host.to_canonical();
        let broadcast = matches!(endpoint.host, IpAddr::V4(v4) if v4.is_broadcast());
        if endpoint.format != 1 || endpoint.port == 0 || endpoint.host.is_unspecified() || endpoint.host.is_multicast() || broadcast {
            return Err(invalid());
        }
        if let Some(pin) = &mut endpoint.sha256 {
            *pin = pin.replace(':', "").to_ascii_lowercase();
            if pin.len() != 64 || !pin.bytes().all(|b| b.is_ascii_hexdigit()) {
                return Err(invalid());
            }
        }
        Ok(endpoint)
    }
}

/// The server's data root as it booted ([`datadir`]): read once by [`super::WebPanels::open`], which takes both this
/// file and Kiln.Admin's from it.
pub fn root(server: &ServerInstance) -> Result<datadir::DataRoot> {
    match &server.remote {
        Some(spec) => Ok(datadir::DataRoot::Remote(datadir::remote_data_root(spec))),
        None => {
            let dir = server.dir.as_deref().ok_or_else(|| Error::invalid("server has no folder"))?;
            Ok(datadir::DataRoot::Local(datadir::local_data_root(dir, server.prefix.as_deref())))
        }
    }
}

/// The server's endpoint (`<data root>/<panel mod id>/launcher-endpoint.json`), locally or over ssh.
pub fn read(server: &ServerInstance) -> Result<Endpoint> {
    read_in(server, &root(server)?)
}

/// The endpoint in the data root `root`.
pub fn read_in(server: &ServerInstance, root: &datadir::DataRoot) -> Result<Endpoint> {
    find_in(server, root).map(|(_, ep)| ep)
}

/// (0.2.0-dev.31.14) The web panel mod with an endpoint in the data root `root` ([`ids::find`]: Kilnwarden first, the
/// old copy when both have one and the server's `kiln/state/mods.json` has the old copy loaded), and that endpoint: the
/// mod whose id the one-time login is minted for.
pub fn find_in(server: &ServerInstance, root: &datadir::DataRoot) -> Result<(PanelMod, Endpoint)> {
    let mods_json = crate::paths::game::MODS_JSON;
    let (panel, bytes) = match (&server.remote, root) {
        (Some(spec), datadir::DataRoot::Remote(_)) => ids::find(
            |m| {
                let dir = root.remote_mod(m.id).unwrap_or_default();
                crate::server::remote::read_abs_file(spec, &format!("{dir}/{FILE}"))
            },
            || crate::server::remote::read_file(spec, mods_json).is_ok_and(|b| ids::old_copy_in_mods_json(&b)),
        )?,
        (None, datadir::DataRoot::Local(_)) => ids::find(
            |m| std::fs::read(root.local_mod(m.id).unwrap_or_default().join(FILE)),
            || server.dir.as_deref().and_then(|d| std::fs::read(d.join(mods_json)).ok()).is_some_and(|b| ids::old_copy_in_mods_json(&b)),
        )?,
        _ => return Err(Error::invalid("a local server's data folder is on this machine, a remote server's on that one")),
    };
    Ok((panel, Endpoint::parse(&bytes)?))
}
