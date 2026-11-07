//! Tauri commands: the online catalog on Nexus Mods and Mod Manager Download (nxm://) links
//! (kiln_manage::nexus; docs/kiln/launcher/catalog.md, contracts.md section 9 "nexus_*", event `kiln://nxm`).
//!
//! - The API key lives in the OS keyring, else `<data>/nexus/apikey.json` (0600); it never reaches the UI (the account
//!   answer says only whether one is set, where, the account name and premium) and is never logged.
//! - Downloads: premium accounts get them from the launcher (`nexus_download`); free accounts download through the
//!   website: the UI opens the file's page, whose "Mod Manager Download" makes an nxm link this launcher takes when the
//!   user turned that on in Settings (`nexus_handler_set`). Mods only: the Nexus Mods build never updates the launcher
//!   itself.
//! - nxm links reach the running launcher through `<data>/nexus/inbox/` (a second process started by the browser queues
//!   the link and exits; [`nxm_entry`]); links for other games go to the handler before Kiln.

use super::TaskId;
use super::app::{launcher_log, spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::nexus::api::{Account, RateState};
use kiln_manage::nexus::catalog::{self, Catalog, Details, InstalledMod, Page, Query};
use kiln_manage::nexus::handler::{self, HandlerStatus};
use kiln_manage::nexus::install::{self, InstallRequest};
use kiln_manage::nexus::keystore::{KeyPlace, KeyStore};
use kiln_manage::nexus::nxm::{self, NxmLink};
use kiln_manage::{Error, ErrorCode};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, LazyLock, Mutex, OnceLock};
use tauri::{AppHandle, Emitter, Manager};

/// Emitted when an Enshrouded nxm link arrived: `{count}` links wait (`nexus_links`).
pub const NXM_EVENT: &str = "kiln://nxm";

static APP_VERSION: OnceLock<String> = OnceLock::new();
static CATALOG: Mutex<Option<Arc<Catalog>>> = Mutex::new(None);
/// The account of the stored key, checked once per start (and when the key changes).
static ACCOUNT: Mutex<Option<Result<Account, Error>>> = Mutex::new(None);
static LINKS: LazyLock<Mutex<Vec<Pending>>> = LazyLock::new(|| Mutex::new(Vec::new()));
static INSTANCE: Mutex<Option<handler::Instance>> = Mutex::new(None);

struct Pending {
    id: String,
    link: NxmLink,
    received: String,
}

fn root() -> Result<std::path::PathBuf, Error> {
    Ok(catalog::root_of(&dirs()?))
}

fn version() -> String {
    APP_VERSION.get().cloned().unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string())
}

fn keystore() -> Result<KeyStore, Error> {
    Ok(KeyStore::new(&root()?))
}

fn the_catalog() -> Result<Arc<Catalog>, Error> {
    let mut c = CATALOG.lock().unwrap();
    if let Some(c) = c.as_ref() {
        return Ok(c.clone());
    }
    let key = keystore()?.get().ok().flatten().map(|(k, _)| k);
    let cat = Arc::new(Catalog::new(root()?, kiln_manage::nexus::client(key, &version())));
    *c = Some(cat.clone());
    Ok(cat)
}

fn reset() {
    *CATALOG.lock().unwrap() = None;
    *ACCOUNT.lock().unwrap() = None;
}

fn account_cached(cat: &Catalog) -> Option<Result<Account, Error>> {
    if !cat.client().has_key() {
        return None;
    }
    let mut a = ACCOUNT.lock().unwrap();
    if a.is_none() {
        *a = Some(cat.client().validate());
    }
    a.clone()
}

/// What Settings and the catalog show about the account. No key, ever.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusAccount {
    pub key_set: bool,
    /// "keyring" or "file"
    pub key_place: Option<KeyPlace>,
    pub name: Option<String>,
    pub premium: bool,
    /// The key was refused, or Nexus could not be reached.
    pub error: Option<Error>,
    pub rate: RateState,
    /// The page where users make a personal API key.
    pub key_page: String,
}

fn account_now() -> Result<NexusAccount, Error> {
    let place = keystore()?.get().ok().flatten().map(|(_, p)| p);
    let cat = the_catalog()?;
    let acc = account_cached(&cat);
    let (name, premium, error) = match acc {
        Some(Ok(a)) => (Some(a.name).filter(|n| !n.is_empty()), a.premium, None),
        Some(Err(e)) => (None, false, Some(e)),
        None => (None, false, None),
    };
    Ok(NexusAccount { key_set: place.is_some(), key_place: place, name, premium, error, rate: cat.client().rate(), key_page: "https://www.nexusmods.com/settings/api-keys".into() })
}

#[tauri::command]
pub async fn nexus_account() -> Result<NexusAccount, Error> {
    blocking(account_now).await
}

/// Checks the key with Nexus first; a refused key is not stored. When Nexus can't say (offline, the check endpoint
/// gone), the key is kept and the account counts as free (downloads through the site) until a check works.
#[tauri::command]
pub async fn nexus_key_set(key: String) -> Result<NexusAccount, Error> {
    blocking(move || {
        let key = key.trim().to_string();
        let probe = kiln_manage::nexus::client(Some(key.clone()), &version());
        let acc = match probe.validate() {
            Err(e) if e.code == ErrorCode::AuthFailed => return Err(e),
            other => other,
        };
        keystore()?.set(&key)?;
        reset();
        *ACCOUNT.lock().unwrap() = Some(acc);
        launcher_log("nexus", "API key saved");
        account_now()
    })
    .await
}

#[tauri::command]
pub async fn nexus_key_clear() -> Result<NexusAccount, Error> {
    blocking(|| {
        keystore()?.clear()?;
        reset();
        launcher_log("nexus", "API key removed");
        account_now()
    })
    .await
}

#[tauri::command]
pub async fn nexus_search(query: Query) -> Result<Page, Error> {
    blocking(move || the_catalog()?.search(&dirs()?, &query)).await
}

#[tauri::command]
pub async fn nexus_mod(mod_id: String) -> Result<Details, Error> {
    blocking(move || the_catalog()?.details(&dirs()?, &mod_id)).await
}

/// Mods installed from the catalog; `check`: look up their newest versions on Nexus (cached 30 minutes per file).
#[tauri::command]
pub async fn nexus_installed(check: bool) -> Result<Vec<InstalledMod>, Error> {
    blocking(move || {
        let cat = the_catalog()?;
        let d = dirs()?;
        if check && cat.client().has_key() { cat.check_updates(&d, false) } else { cat.installed(&d) }
    })
    .await
}

fn game_with_kiln(game_id: &str) -> Result<kiln_manage::game::GameInstall, Error> {
    let g = crate::state::game(game_id)?;
    if !g.path.join(kiln_manage::paths::game::KILN).is_dir() {
        return Err(Error::new(ErrorCode::Unsupported, "Kiln is not in this game yet: install it first."));
    }
    Ok(g)
}

fn run_install(app: &AppHandle, game: kiln_manage::game::GameInstall, req: InstallRequest, title: String) -> TaskId {
    let a = app.clone();
    let game_id = game.id.clone();
    spawn_task(app, "nexus_install", title, move |p| {
        let cat = the_catalog()?;
        launcher_log("nexus", &format!("installing mod {} file {} ({})", req.mod_id, req.file_id, req.source));
        let r = install::install(&cat, &dirs()?, &game, &req, None, p)?;
        if r.add.applied {
            super::profile::emit_status_later(a, game_id);
        }
        to_value(r)
    })
}

/// Premium accounts: download, check and add one file (a task; result `InstallReport`). Free accounts are refused
/// with `detail.url`, the file's page with its Mod Manager Download.
#[tauri::command]
pub async fn nexus_download(app: AppHandle, game_id: String, mod_id: String, file_id: String) -> Result<TaskId, Error> {
    let (g, title) = blocking(move || {
        catalog::refuse_kiln(&mod_id)?;
        let cat = the_catalog()?;
        if !cat.client().has_key() {
            return Err(Error::new(ErrorCode::AuthFailed, "Add your Nexus Mods API key in Settings first."));
        }
        let premium = account_cached(&cat).and_then(|a| a.ok()).is_some_and(|a| a.premium);
        if !premium {
            return Err(Error::new(ErrorCode::Refused, "Free accounts download through the Nexus Mods website.")
                .with_detail(serde_json::json!({"url": catalog::files_url(&mod_id, Some(&file_id))})));
        }
        let g = game_with_kiln(&game_id)?;
        Ok(((g, InstallRequest { mod_id, file_id, key: None, expires: None, source: "catalog".into() }), "Add from Nexus Mods".to_string()))
    })
    .await?;
    Ok(run_install(&app, g.0, g.1, title))
}

/// An nxm link waiting for the UI.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingLink {
    pub id: String,
    pub mod_id: String,
    pub file_id: String,
    pub expired: bool,
    pub received: String,
}

/// Enshrouded links that arrived and wait (the UI starts each with `nexus_link_install`).
#[tauri::command]
pub async fn nexus_links() -> Result<Vec<PendingLink>, Error> {
    let now = kiln_manage::util::now_unix();
    Ok(LINKS.lock().unwrap().iter().map(|p| PendingLink {
        id: p.id.clone(), mod_id: p.link.mod_id.clone(), file_id: p.link.file_id.clone(), expired: p.link.expired(now), received: p.received.clone(),
    }).collect())
}

/// Downloads, checks and adds the file an nxm link names (a task; result `InstallReport`).
#[tauri::command]
pub async fn nexus_link_install(app: AppHandle, game_id: String, link_id: String) -> Result<TaskId, Error> {
    let link = {
        let mut l = LINKS.lock().unwrap();
        let i = l.iter().position(|p| p.id == link_id).ok_or_else(|| Error::not_found("That link is gone. Click Mod Manager Download again."))?;
        l.remove(i).link
    };
    if link.expired(kiln_manage::util::now_unix()) {
        return Err(Error::new(ErrorCode::Refused, "The download link has expired. Click Mod Manager Download on the site again."));
    }
    let g = blocking(move || game_with_kiln(&game_id)).await?;
    let req = InstallRequest { mod_id: link.mod_id.clone(), file_id: link.file_id.clone(), key: link.key.clone(), expires: link.expires, source: "nxm".into() };
    Ok(run_install(&app, g, req, "Add from Nexus Mods".into()))
}

#[tauri::command]
pub async fn nexus_link_dismiss(link_id: String) -> Result<(), Error> {
    LINKS.lock().unwrap().retain(|p| p.id != link_id);
    Ok(())
}

#[tauri::command]
pub async fn nexus_handler_status() -> Result<HandlerStatus, Error> {
    blocking(|| Ok(handler::status(&root()?, handler::platform().as_ref()))).await
}

/// Settings' "Mod Manager Download links": on registers the launcher for Enshrouded's nxm links (other games go to
/// the handler before it), off gives that handler back. Only ever on the user's click.
#[tauri::command]
pub async fn nexus_handler_set(on: bool) -> Result<HandlerStatus, Error> {
    blocking(move || {
        let r = root()?;
        let p = handler::platform();
        let s = if on {
            let exe = launcher_exe()?;
            handler::register(&r, p.as_ref(), &exe)?
        } else {
            handler::unregister(&r, p.as_ref())?
        };
        launcher_log("nexus", &format!("nxm handler {} (previous: {})", if on { "on" } else { "off" }, s.previous.as_deref().unwrap_or("none")));
        Ok(s)
    })
    .await
}

/// The program the system should start for a link: the AppImage itself when run as one (not its temporary mount).
fn launcher_exe() -> Result<std::path::PathBuf, Error> {
    if let Some(a) = std::env::var_os("APPIMAGE").filter(|a| !a.is_empty()) {
        return Ok(a.into());
    }
    std::env::current_exe().map_err(|e| Error::new(ErrorCode::Io, format!("where is the launcher? {e}")))
}

// ---------------------------------------------------------------- links arriving

/// `main` calls this when the launcher was started with a link (`--nxm <url>` or a bare `nxm://` argument). Returns
/// whether this process should start the UI: false when the link went to another game's handler or to the launcher
/// that already runs.
pub fn nxm_entry(url: &str) -> bool {
    let Ok(r) = root() else { return true };
    match nxm::game_of(url) {
        None => return true,
        Some(g) if g != catalog::GAME => {
            let shown = url.split('?').next().unwrap_or(url);
            match handler::forward(&r, handler::platform().as_ref(), url) {
                Ok(to) => launcher_log("nexus", &format!("{shown}: another game, handed to {to}")),
                Err(e) => launcher_log("nexus", &format!("{shown}: another game, not handled: {}", e.message)),
            }
            return false;
        }
        _ => {}
    }
    if let Err(e) = handler::queue_link(&r, url) {
        launcher_log("nexus", &format!("could not queue a link: {}", e.message));
        return true;
    }
    match handler::acquire_instance(&r) {
        Ok(Some(i)) => {
            *INSTANCE.lock().unwrap() = Some(i);
            true
        }
        // the running launcher takes it from the inbox
        Ok(None) => false,
        Err(_) => true,
    }
}

/// At setup: holds the instance lock and watches the inbox (every 500 ms). Enshrouded links wait in memory for the UI
/// (`kiln://nxm`, the window comes to the front); others go to the previous handler.
pub fn start(app: &AppHandle) {
    let _ = APP_VERSION.set(app.package_info().version.to_string());
    let Ok(r) = root() else { return };
    {
        let mut inst = INSTANCE.lock().unwrap();
        if inst.is_none() {
            *inst = handler::acquire_instance(&r).ok().flatten();
        }
    }
    let app = app.clone();
    std::thread::spawn(move || loop {
        let urls = handler::take_links(&r);
        let mut added = 0;
        for url in urls {
            let shown = url.split('?').next().unwrap_or(&url).to_string();
            match nxm::parse(&url) {
                Ok(link) if link.game == catalog::GAME => {
                    launcher_log("nexus", &format!("link {}", link.redacted()));
                    let id = format!("nxm-{}-{}", link.file_id, kiln_manage::util::now_unix());
                    let mut l = LINKS.lock().unwrap();
                    l.retain(|p| p.link.file_id != link.file_id);
                    l.push(Pending { id, link, received: kiln_manage::util::now_rfc3339() });
                    added += 1;
                }
                Ok(_) | Err(_) if nxm::game_of(&url).is_some_and(|g| g != catalog::GAME) => {
                    let _ = handler::forward(&r, handler::platform().as_ref(), &url);
                    launcher_log("nexus", &format!("{shown}: another game, handed on"));
                }
                Ok(_) => {}
                Err(e) => launcher_log("nexus", &format!("{shown}: {}", e.message)),
            }
        }
        if added > 0 {
            let count = LINKS.lock().unwrap().len();
            let _ = app.emit(NXM_EVENT, serde_json::json!({"count": count}));
            if let Some(w) = app.get_webview_window("main") {
                let _ = w.unminimize();
                let _ = w.show();
                let _ = w.set_focus();
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(500));
    });
}
