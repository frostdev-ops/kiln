//! Tauri commands: the online catalog on Nexus Mods and Mod Manager Download (nxm://) links
//! (kiln_manage::nexus; docs/kiln/launcher/catalog.md, contracts.md section 9 "nexus_*", event `kiln://nxm`).
//!
//! - OAuth token material stays in the backend and OS keyring/private token file. Browser sign-in uses PKCE;
//!   public client registration is supplied at build time. No personal API key is accepted or used; at every start
//!   (also with Nexus off) the key an older build stored is removed ([`startup_cleanup`]).
//! - With Nexus off (`KILN_NEXUS_ENABLED=0`) nothing is queued, downloaded or signed in. Kiln only undoes its own
//!   leftovers: the nxm registration dev.19 made is given back to the handler before it at start, and a link that still
//!   reaches Kiln goes to that handler, or the user is told how to reset it ([`disabled_nxm_entry`]).
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
use kiln_manage::nexus::handler::{self, HandlerPlatform, HandlerStatus, Leftover};
use kiln_manage::nexus::install::{self, InstallRequest};
use kiln_manage::nexus::keystore::{self, SecretBackend, TokenPlace, TokenStore};
use kiln_manage::nexus::oauth::{self, Session};
use kiln_manage::nexus::nxm::{self, NxmLink};
use kiln_manage::{Error, ErrorCode};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, LazyLock, Mutex, OnceLock};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use tauri::{AppHandle, Emitter, Manager};

/// Emitted when an Enshrouded nxm link arrived: `{count}` links wait (`nexus_links`).
pub const NXM_EVENT: &str = "kiln://nxm";

static APP_VERSION: OnceLock<String> = OnceLock::new();
static AUTH: Mutex<Option<Arc<Session>>> = Mutex::new(None);
static AUTH_MUTATION: Mutex<()> = Mutex::new(());
static AUTH_EPOCH: AtomicU64 = AtomicU64::new(0);
static LEGACY_RETIRED: AtomicBool = AtomicBool::new(false);
static CATALOG: Mutex<Option<Arc<Catalog>>> = Mutex::new(None);
/// Authenticated account metadata, rechecked at most once per minute.
static ACCOUNT: Mutex<Option<CachedAccount>> = Mutex::new(None);
static LINKS: LazyLock<Mutex<Vec<Pending>>> = LazyLock::new(|| Mutex::new(Vec::new()));
static INSTANCE: Mutex<Option<handler::Instance>> = Mutex::new(None);

/// In a build with Nexus on, another launcher holds `nexus/instance.lock` of this data folder (one that takes no
/// data-folder lock: dev.24 and earlier). A move or a switch is refused then (commands/data_folder.rs).
pub fn other_instance(dirs: &kiln_manage::paths::DataDirs) -> bool {
    if !kiln_manage::nexus::enabled() || INSTANCE.lock().unwrap_or_else(|e| e.into_inner()).is_some() {
        return false;
    }
    // taken now (and let go at once): nobody else has it
    matches!(handler::acquire_instance(&catalog::root_of(dirs)), Ok(None))
}
static NEXT_LINK: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

struct Pending {
    id: String,
    link: NxmLink,
    received: String,
}

struct CachedAccount {
    catalog: usize,
    checked: std::time::Instant,
    result: Result<Account, Error>,
}

fn root() -> Result<std::path::PathBuf, Error> {
    Ok(catalog::root_of(&dirs()?))
}

fn version() -> String {
    APP_VERSION.get().cloned().unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_string())
}

fn auth_session() -> Result<Option<Arc<Session>>, Error> {
    kiln_manage::nexus::require_enabled()?;
    let mut auth = AUTH.lock().unwrap();
    if !LEGACY_RETIRED.load(Ordering::Acquire) {
        kiln_manage::nexus::keystore::retire_legacy(&root()?)?;
        LEGACY_RETIRED.store(true, Ordering::Release);
    }
    let Some(config) = oauth::Config::registered()? else { return Ok(None) };
    if let Some(auth) = auth.as_ref() { return Ok(Some(auth.clone())); }
    let transport = Arc::new(kiln_manage::nexus::api::UreqTransport::new(&format!("Kiln-Launcher/{}", version())));
    let session = Arc::new(Session::new(config, transport, TokenStore::new(&root()?))?);
    *auth = Some(session.clone());
    Ok(Some(session))
}

pub(crate) fn the_catalog() -> Result<Arc<Catalog>, Error> {
    let auth = auth_session()?;
    let mut catalog = CATALOG.lock().unwrap();
    if let Some(catalog) = catalog.as_ref() { return Ok(catalog.clone()); }
    let app = kiln_manage::nexus::api::AppId::kiln_launcher(&version());
    let transport = Arc::new(kiln_manage::nexus::api::UreqTransport::new(&app.user_agent()));
    let client = match auth {
        Some(auth) => kiln_manage::nexus::api::Client::with_credentials(transport, auth, app),
        None => kiln_manage::nexus::api::Client::new(transport, None, app),
    };
    let created = Arc::new(Catalog::new(root()?, client));
    *catalog = Some(created.clone());
    Ok(created)
}

fn reset() {
    *CATALOG.lock().unwrap() = None;
    *ACCOUNT.lock().unwrap() = None;
}

fn account_cached(cat: &Catalog) -> Option<Result<Account, Error>> {
    if !cat.client().has_credentials() {
        return None;
    }
    let mut a = ACCOUNT.lock().unwrap();
    let id = cat as *const Catalog as usize;
    let stale = a.as_ref().is_none_or(|a| a.catalog != id || a.checked.elapsed() >= std::time::Duration::from_secs(60));
    if stale {
        *a = Some(CachedAccount { catalog: id, checked: std::time::Instant::now(), result: cat.client().validate() });
    }
    a.as_ref().map(|a| a.result.clone())
}

/// Account metadata only; no OAuth token, verifier or authorization code reaches the UI.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NexusAccount {
    pub configured: bool,
    pub signed_in: bool,
    pub token_place: Option<TokenPlace>,
    pub name: Option<String>,
    pub premium: bool,
    pub error: Option<Error>,
    pub rate: RateState,
}

fn account_now() -> Result<NexusAccount, Error> {
    let auth = match auth_session() {
        Ok(auth) => auth,
        Err(error) => return Ok(NexusAccount { configured: oauth::Config::registered()?.is_some(), signed_in: false,
            token_place: None, name: None, premium: false, error: Some(error), rate: RateState::default() }),
    };
    let catalog = the_catalog()?;
    let account = account_cached(&catalog);
    let (name, premium, error) = match account {
        Some(Ok(a)) => (Some(a.name).filter(|n| !n.is_empty()), a.premium, None),
        Some(Err(e)) => (None, false, Some(e)),
        None => (None, false, None),
    };
    let token_place = auth.as_ref().map(|s| s.place()).transpose()?.flatten();
    let signed_in = token_place.is_some();
    Ok(NexusAccount { configured: auth.is_some(), signed_in, token_place, name: if signed_in { name } else { None },
        premium: signed_in && premium, error, rate: catalog.client().rate() })
}

#[tauri::command]
pub async fn nexus_account() -> Result<NexusAccount, Error> {
    kiln_manage::nexus::require_enabled()?; blocking(account_now).await }

/// Browser consent and a one-use loopback callback. The task runner supports cancellation.
#[tauri::command]
pub async fn nexus_sign_in(app: AppHandle) -> Result<TaskId, Error> {
    kiln_manage::nexus::require_enabled()?;
    let config = oauth::Config::registered()?.ok_or_else(|| Error::new(ErrorCode::Unsupported, "Nexus Mods sign-in is not available in this build."))?;
    let auth = auth_session()?.ok_or_else(|| Error::new(ErrorCode::Unsupported, "Nexus Mods sign-in is not available in this build."))?;
    let login = oauth::Login::bind(config)?;
    let epoch = {
        let _mutation = AUTH_MUTATION.lock().unwrap();
        AUTH_EPOCH.fetch_add(1, Ordering::AcqRel) + 1
    };
    Ok(spawn_task(&app, "nexus-sign-in", "Sign in to Nexus Mods".into(), move |progress| {
        progress.report(None, "Complete sign-in in your browser.");
        let url = login.authorize_url();
        crate::state::open_external(std::ffi::OsStr::new(&url))
            .map_err(|_| Error::new(ErrorCode::Unsupported, "The browser could not open Nexus Mods sign-in."))?;
        let transport = kiln_manage::nexus::api::UreqTransport::new(&format!("Kiln-Launcher/{}", version()));
        let tokens = login.wait(&transport, || progress.canceled() || AUTH_EPOCH.load(Ordering::Acquire) != epoch)?;
        let _mutation = AUTH_MUTATION.lock().unwrap();
        if progress.canceled() || AUTH_EPOCH.load(Ordering::Acquire) != epoch {
            return Err(Error::new(ErrorCode::Canceled, "Nexus Mods sign-in canceled."));
        }
        auth.save(tokens)?;
        if progress.canceled() {
            auth.clear()?;
            reset();
            return Err(Error::new(ErrorCode::Canceled, "Nexus Mods sign-in canceled."));
        }
        reset();
        launcher_log("nexus", "OAuth sign-in completed");
        to_value(account_now()?)
    }))
}

#[tauri::command]
pub async fn nexus_sign_out() -> Result<NexusAccount, Error> {
    kiln_manage::nexus::require_enabled()?;
    blocking(|| {
        let _mutation = AUTH_MUTATION.lock().unwrap();
        AUTH_EPOCH.fetch_add(1, Ordering::AcqRel);
        let auth = AUTH.lock().unwrap().clone();
        if let Some(auth) = auth { auth.clear()?; }
        else { TokenStore::new(&root()?).clear()?; }
        reset();
        launcher_log("nexus", "OAuth sign-out completed");
        account_now()
    }).await
}

#[tauri::command]
pub async fn nexus_search(query: Query) -> Result<Page, Error> {
    kiln_manage::nexus::require_enabled()?;
    blocking(move || the_catalog()?.search(&dirs()?, &query)).await
}

#[tauri::command]
pub async fn nexus_mod(mod_id: String) -> Result<Details, Error> {
    kiln_manage::nexus::require_enabled()?;
    blocking(move || the_catalog()?.details(&dirs()?, &mod_id)).await
}

/// Mods installed from the catalog; `check`: look up their newest versions on Nexus (cached 30 minutes per file).
#[tauri::command]
pub async fn nexus_installed(check: bool) -> Result<Vec<InstalledMod>, Error> {
    kiln_manage::nexus::require_enabled()?;
    blocking(move || {
        let cat = the_catalog()?;
        let d = dirs()?;
        if check && cat.client().has_credentials() { cat.check_updates(&d, false) } else { cat.installed(&d) }
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
    kiln_manage::nexus::require_enabled()?;
    let (g, title) = blocking(move || {
        catalog::refuse_kiln(&mod_id)?;
        nxm::parse(&format!("nxm://enshrouded/mods/{mod_id}/files/{file_id}"))?;
        let cat = the_catalog()?;
        if !cat.client().has_credentials() {
            return Err(Error::new(ErrorCode::AuthFailed, "Sign in to Nexus Mods in Settings first."));
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
    kiln_manage::nexus::require_enabled()?;
    let now = kiln_manage::util::now_unix();
    Ok(LINKS.lock().unwrap().iter().map(|p| PendingLink {
        id: p.id.clone(), mod_id: p.link.mod_id.clone(), file_id: p.link.file_id.clone(), expired: p.link.expired(now), received: p.received.clone(),
    }).collect())
}

/// Downloads, checks and adds the file an nxm link names (a task; result `InstallReport`).
#[tauri::command]
pub async fn nexus_link_install(app: AppHandle, game_id: String, link_id: String) -> Result<TaskId, Error> {
    kiln_manage::nexus::require_enabled()?;
    let link = {
        let l = LINKS.lock().unwrap();
        l.iter().find(|p| p.id == link_id).ok_or_else(|| Error::not_found("That link is gone. Click Mod Manager Download again."))?.link.clone()
    };
    if link.expired(kiln_manage::util::now_unix()) {
        return Err(Error::new(ErrorCode::Refused, "The download link has expired. Click Mod Manager Download on the site again."));
    }
    let g = blocking(move || {
        let g = game_with_kiln(&game_id)?;
        if !the_catalog()?.client().has_credentials() {
            return Err(Error::new(ErrorCode::AuthFailed, "Sign in to Nexus Mods in Settings first."));
        }
        Ok(g)
    }).await?;
    // Only claim the link once prerequisites are met; invalid game selections must not consume it. Two overlapping
    // commands may validate concurrently, but exactly one removes it and starts an install.
    {
        let mut l = LINKS.lock().unwrap();
        let i = l.iter().position(|p| p.id == link_id).ok_or_else(|| Error::not_found("That link is already being handled."))?;
        l.remove(i);
    }
    let req = InstallRequest { mod_id: link.mod_id.clone(), file_id: link.file_id.clone(), key: link.key.clone(), expires: link.expires, source: "nxm".into() };
    Ok(run_install(&app, g, req, "Add from Nexus Mods".into()))
}

#[tauri::command]
pub async fn nexus_link_dismiss(link_id: String) -> Result<(), Error> {
    kiln_manage::nexus::require_enabled()?;
    LINKS.lock().unwrap().retain(|p| p.id != link_id);
    Ok(())
}

#[tauri::command]
pub async fn nexus_handler_status() -> Result<HandlerStatus, Error> {
    kiln_manage::nexus::require_enabled()?;
    blocking(|| Ok(handler::status(&root()?, handler::platform().as_ref()))).await
}

/// Settings' "Mod Manager Download links": on registers the launcher for Enshrouded's nxm links (other games go to
/// the handler before it), off gives that handler back. Only ever on the user's click.
#[tauri::command]
pub async fn nexus_handler_set(on: bool) -> Result<HandlerStatus, Error> {
    kiln_manage::nexus::require_enabled()?;
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
    if !kiln_manage::nexus::enabled() { return disabled_nxm_entry(&LinkEnv::system(), url, &|line| launcher_log("nexus", line)); }
    let Ok(r) = root() else { return true };
    match nxm::game_of(url) {
        None => return true,
        Some(g) if g != catalog::GAME => {
            match handler::forward(&r, handler::platform().as_ref(), url) {
                Ok(_) => launcher_log("nexus", "another game's link handed to the previous handler"),
                Err(_) => launcher_log("nexus", "another game's link could not be handed to the previous handler"),
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

// ---------------------------------------------------------------- Nexus off: Kiln's own leftovers

/// What a build with Nexus off tells the user when a Mod Manager Download link still reaches Kiln and no other mod
/// manager is recorded to take it. No link, key or path in it.
pub const NEXUS_OFF_NOTICE: &str = "Kiln's Nexus Mods integration is turned off in this version, so Kiln did not take this \
Mod Manager Download link. Download the file in your browser and add it with Library > Import, or open the link in your \
mod manager.\n\nKiln is still set as the handler for nxm:// links on this computer. To reset it: on Linux, run \
`xdg-mime default <your mod manager>.desktop x-scheme-handler/nxm` (or pick another app in your desktop's default \
applications); on Windows, open Settings > Apps > Default apps > Choose defaults by link type and pick another app for NXM.";

/// The message waiting for the window (`NEXUS_OFF_NOTICE`), shown by [`start`].
static NOTICE: Mutex<Option<&'static str>> = Mutex::new(None);

/// Where the leftovers are: the Nexus folder of the data dir (None: the data dir cannot be located) and the system's
/// nxm handler side. Tests give it a temporary folder and a fake; nothing else reaches the real keyring, mimeapps.list
/// or registry.
struct LinkEnv {
    root: Option<std::path::PathBuf>,
    platform: Box<dyn HandlerPlatform>,
}

impl LinkEnv {
    fn system() -> LinkEnv {
        LinkEnv { root: crate::state::dirs().ok().map(|d| catalog::root_of(&d)), platform: handler::platform() }
    }
}

enum DisabledLink {
    /// The handler before Kiln (its name) took the link.
    HandedOn(String),
    /// Nobody to hand it to: tell the user.
    Message,
}

/// A link reaches a build with Nexus off only because an older build registered Kiln for nxm links. Give those links
/// back (so the next one goes to the manager before Kiln) and pass this one on to it; with no such manager, say so.
/// Never queues, downloads or signs in. Only Kiln's own registration is undone.
fn disabled_link(env: &LinkEnv, url: &str) -> DisabledLink {
    let (Some(root), platform) = (env.root.as_deref(), env.platform.as_ref()) else { return DisabledLink::Message };
    let previous = match handler::retire_leftover(root, platform) {
        Ok(Leftover::GivenBack { previous }) => previous,
        Ok(Leftover::Nothing | Leftover::Forgotten) => None,
        // the links could not be given back: the recorded manager can still take this one
        Err(_) => handler::recorded_previous(root),
    };
    previous
        .and_then(|previous| handler::forward_to_previous(platform, &previous, url).ok())
        .map_or(DisabledLink::Message, DisabledLink::HandedOn)
}

/// `nxm_entry` for a build with Nexus off. True: start the window, which shows [`NEXUS_OFF_NOTICE`]. The log lines never
/// carry the link (its query holds a download key).
fn disabled_nxm_entry(env: &LinkEnv, url: &str, log: &dyn Fn(&str)) -> bool {
    match disabled_link(env, url) {
        DisabledLink::HandedOn(name) => {
            log(&format!("Nexus Mods is off in this build: an nxm link was handed to {name}"));
            false
        }
        DisabledLink::Message => {
            log("Nexus Mods is off in this build: an nxm link reached Kiln and no other handler is recorded");
            *NOTICE.lock().unwrap() = Some(NEXUS_OFF_NOTICE);
            true
        }
    }
}

/// What a start does about the leftovers of older builds, as log lines (no key, link or path in them):
/// - every build: remove the personal API key dev.19 stored (`apikey.json`, the `nexusmods-api-key` keyring entry);
/// - Nexus off: give back the nxm registration dev.19 made, when Kiln is still the handler.
/// `ring` is the retired keyring slot (None: no keyring); only Kiln's own entries are named.
fn startup_cleanup(root: &std::path::Path, nexus_on: bool, platform: &dyn HandlerPlatform, ring: Option<&dyn SecretBackend>) -> Vec<String> {
    let mut log = Vec::new();
    // After one successful keyring check, later starts look at the file only (no unlock prompt at every start).
    let marker = root.parent().unwrap_or(root).join("state").join("nexus-personal-key-retired");
    match keystore::retire_personal_key_once(root, &marker, ring) {
        Ok(true) => log.push("removed the stored Nexus personal API key".to_string()),
        Ok(false) => {}
        Err(e) => log.push(format!("could not remove the stored Nexus personal API key: {}", e.message)),
    }
    if !nexus_on {
        match handler::retire_leftover(root, platform) {
            Ok(Leftover::GivenBack { previous: Some(previous) }) => log.push(format!("Nexus Mods is off in this build: gave the nxm links back to {}", previous.label())),
            Ok(Leftover::GivenBack { previous: None }) => log.push("Nexus Mods is off in this build: removed Kiln's nxm registration (no handler was set before it)".to_string()),
            Ok(Leftover::Forgotten) => log.push("Nexus Mods is off in this build: another program has the nxm links; dropped Kiln's old registration record".to_string()),
            Ok(Leftover::Nothing) => {}
            Err(e) => log.push(format!("Nexus Mods is off in this build: could not give the nxm links back: {}", e.message)),
        }
    }
    log
}

/// At setup: holds the instance lock and watches the inbox (every 500 ms). Enshrouded links wait in memory for the UI
/// (`kiln://nxm`, the window comes to the front); others go to the previous handler.
pub fn start(app: &AppHandle) {
    // Kiln's leftovers from older builds go in every build; the keyring and xdg-mime are asked off the setup thread.
    let nexus_on = kiln_manage::nexus::enabled();
    if let Ok(r) = root() {
        std::thread::spawn(move || {
            let _writing = crate::state::writing();
            if crate::folder::current().held_error().is_some() {
                return;
            }
            let ring = keystore::legacy_keyring();
            for line in startup_cleanup(&r, nexus_on, handler::platform().as_ref(), ring.as_deref()) {
                launcher_log("nexus", &line);
            }
        });
    }
    if !nexus_on {
        // a link that reached Kiln with nobody to hand it to (disabled_nxm_entry): the window says so
        if let Some(text) = NOTICE.lock().unwrap().take() {
            use tauri_plugin_dialog::{DialogExt, MessageDialogKind};
            app.dialog().message(text).title("Kiln: Nexus Mods links").kind(MessageDialogKind::Info).show(|_| {});
        }
        return;
    }
    let _ = APP_VERSION.set(app.package_info().version.to_string());
    let Ok(r) = root() else { return };
    {
        let mut inst = INSTANCE.lock().unwrap();
        if inst.is_none() {
            *inst = handler::acquire_instance(&r).ok().flatten();
        }
        // A normally-started second UI may remain open, but must not race the lock owner for queued links.
        if inst.is_none() {
            return;
        }
    }
    let app = app.clone();
    std::thread::spawn(move || loop {
        // the inbox is in the data folder: a writer (its files are removed once read) a move waits for, and not read
        // while the folder is held
        let writing = crate::state::writing();
        if crate::folder::current().held_error().is_some() {
            drop(writing);
            std::thread::sleep(std::time::Duration::from_millis(500));
            continue;
        }
        let urls = handler::take_links(&r);
        let mut added = 0;
        for url in urls {
            match nxm::parse(&url) {
                Ok(link) if link.game == catalog::GAME => {
                    launcher_log("nexus", &format!("link {}", link.redacted()));
                    let id = format!("nxm-{}-{}", kiln_manage::util::now_unix(), NEXT_LINK.fetch_add(1, std::sync::atomic::Ordering::Relaxed));
                    let mut l = LINKS.lock().unwrap();
                    l.retain(|p| p.link.file_id != link.file_id || p.link.mod_id != link.mod_id);
                    if l.len() >= 100 { l.remove(0); }
                    l.push(Pending { id, link, received: kiln_manage::util::now_rfc3339() });
                    added += 1;
                }
                Ok(_) | Err(_) if nxm::game_of(&url).is_some_and(|g| g != catalog::GAME) => {
                    let _ = handler::forward(&r, handler::platform().as_ref(), &url);
                    launcher_log("nexus", "another game's link handed on");
                }
                Ok(_) => {}
                Err(_) => launcher_log("nexus", "invalid Mod Manager Download link refused"),
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
        drop(writing);
        std::thread::sleep(std::time::Duration::from_millis(500));
    });
}

#[cfg(test)]
mod capability_tests {
    use super::*;
    use kiln_manage::nexus::handler::{HandlerState, Previous};
    use std::path::Path;

    /// The tests share the process-wide message slot (NOTICE): one at a time.
    static ONE_AT_A_TIME: Mutex<()> = Mutex::new(());

    fn unsupported<T>(value: Result<T, Error>) { assert!(matches!(value, Err(error) if error.code == ErrorCode::Unsupported)); }

    /// A stand-in for the system's nxm handler: nothing reaches this machine's mimeapps.list or registry.
    #[derive(Default)]
    struct FakeOs {
        current: Mutex<Option<String>>,
        forwarded: Mutex<Vec<(String, String)>>,
        fail_restore: bool,
    }
    impl HandlerPlatform for FakeOs {
        fn supported(&self) -> bool { panic!("Nexus off never asks whether it can take the links") }
        fn current(&self) -> Result<Option<Previous>, Error> { panic!("Nexus off never looks for a handler to replace") }
        fn is_ours(&self) -> bool { self.current.lock().unwrap().as_deref() == Some("kiln") }
        fn install(&self, _: &Path) -> Result<(), Error> { panic!("Nexus off never registers") }
        fn restore(&self, previous: Option<&Previous>) -> Result<(), Error> {
            if self.fail_restore { return Err(Error::new(ErrorCode::Io, "xdg-mime failed")); }
            *self.current.lock().unwrap() = previous.map(|p| match p { Previous::Desktop { id } => id.clone(), Previous::Command { command, .. } => command.clone() });
            Ok(())
        }
        fn forward(&self, previous: &Previous, url: &str) -> Result<(), Error> {
            self.forwarded.lock().unwrap().push((previous.label(), url.into()));
            Ok(())
        }
    }

    struct Ring(Mutex<Option<String>>);
    impl SecretBackend for Ring {
        fn get(&self) -> Result<Option<String>, Error> { Ok(self.0.lock().unwrap().clone()) }
        fn set(&self, _: &str) -> Result<(), Error> { panic!("the retired slot is never written") }
        fn clear(&self) -> Result<(), Error> { *self.0.lock().unwrap() = None; Ok(()) }
    }

    fn temp() -> kiln_manage::library::TempDir { kiln_manage::library::TempDir::new(&std::env::temp_dir(), "nexus-launcher-test").unwrap() }
    fn vortex() -> Previous { Previous::Desktop { id: "com.nexusmods.vortex.desktop".into() } }
    /// The state dev.19's "Mod Manager Download links" option wrote.
    fn dev19_record(root: &Path) {
        std::fs::create_dir_all(root).unwrap();
        let state = HandlerState { format: 2, registered: true, previous: Some(vortex()), exe: Some("/apps/Kiln.AppImage".into()),
            registered_at: Some("2026-10-01T00:00:00Z".into()), snapshot: None };
        std::fs::write(root.join("handler.json"), serde_json::to_vec_pretty(&state).unwrap()).unwrap();
    }
    fn env(root: &Path, os: FakeOs) -> LinkEnv { LinkEnv { root: Some(root.to_path_buf()), platform: Box::new(os) } }

    #[test]
    fn disabled_nexus_commands_and_direct_links_do_not_initialize_auth_or_inbox() {
        if kiln_manage::nexus::enabled() { return; }
        let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|e| e.into_inner());
        tauri::async_runtime::block_on(async {
            unsupported(nexus_account().await);
            unsupported(nexus_sign_out().await);
            unsupported(nexus_search(Query::default()).await);
            unsupported(nexus_mod("1".into()).await);
            unsupported(nexus_installed(true).await);
            unsupported(nexus_links().await);
            unsupported(nexus_link_dismiss("anything".into()).await);
            unsupported(nexus_handler_status().await);
            unsupported(nexus_handler_set(true).await);
            unsupported(nexus_handler_set(false).await);
        });
        // a link, in a private data folder and with a fake handler side: nothing is queued, no lock or inbox is made
        let t = temp();
        let root = t.path().join("nexus");
        let logged = Mutex::new(Vec::<String>::new());
        let log = |line: &str| logged.lock().unwrap().push(line.to_string());
        assert!(disabled_nxm_entry(&env(&root, FakeOs::default()), "nxm://enshrouded/mods/1/files/2?key=k&expires=9", &log));
        assert!(disabled_nxm_entry(&env(&root, FakeOs::default()), "nxm://another-game/mods/1/files/2", &log));
        assert!(NOTICE.lock().unwrap().take().is_some());
        assert!(!root.exists());
        assert!(AUTH.lock().unwrap().is_none());
        assert!(!LEGACY_RETIRED.load(Ordering::Acquire));
        assert!(INSTANCE.lock().unwrap().is_none());
        assert!(LINKS.lock().unwrap().is_empty());
    }

    #[test]
    fn disabled_nexus_link_goes_to_the_manager_before_kiln_or_shows_the_message() {
        let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|e| e.into_inner());
        let url = "nxm://enshrouded/mods/310/files/1001?key=SECRET-KEY&expires=9999999999";
        let logged = Mutex::new(Vec::<String>::new());
        let log = |line: &str| logged.lock().unwrap().push(line.to_string());

        // dev.19 registered Kiln over Vortex and it still is the handler: Vortex gets the link and the links back
        let t = temp();
        let root = t.path().join("nexus");
        dev19_record(&root);
        let os = FakeOs { current: Mutex::new(Some("kiln".into())), ..Default::default() };
        let e = env(&root, os);
        assert!(!disabled_nxm_entry(&e, url, &log));
        assert!(!root.join("handler.json").exists(), "the registration was given back");
        assert!(!root.join("inbox").exists() && !root.join("instance.lock").exists());
        assert!(NOTICE.lock().unwrap().is_none());
        assert!(logged.lock().unwrap().iter().any(|l| l.contains("handed to Vortex")));
        assert!(logged.lock().unwrap().iter().all(|l| !l.contains("SECRET-KEY") && !l.contains("nxm://")));

        // giving the links back fails (the OS refused): the recorded manager still gets this link, the record stays
        let t = temp();
        let root = t.path().join("nexus");
        dev19_record(&root);
        let os = FakeOs { current: Mutex::new(Some("kiln".into())), fail_restore: true, ..Default::default() };
        assert!(!disabled_nxm_entry(&env(&root, os), url, &log));
        assert!(root.join("handler.json").is_file());

        // nobody recorded: the message, and the window opens to show it
        let t = temp();
        let root = t.path().join("nexus");
        assert!(disabled_nxm_entry(&env(&root, FakeOs::default()), url, &log));
        assert_eq!(NOTICE.lock().unwrap().take(), Some(NEXUS_OFF_NOTICE));
        assert!(NEXUS_OFF_NOTICE.contains("xdg-mime default") && NEXUS_OFF_NOTICE.contains("Default apps"));

        // another manager has the links since: Kiln touches nothing of the system, and does not guess a handler
        let t = temp();
        let root = t.path().join("nexus");
        dev19_record(&root);
        let os = FakeOs { current: Mutex::new(Some("org.example.mm.desktop".into())), ..Default::default() };
        let e = env(&root, os);
        assert!(disabled_nxm_entry(&e, url, &log));
        assert!(NOTICE.lock().unwrap().take().is_some());
        assert!(!root.join("handler.json").exists());

        // the data folder cannot be located: the message
        assert!(disabled_nxm_entry(&LinkEnv { root: None, platform: Box::new(FakeOs::default()) }, url, &log));
        assert!(NOTICE.lock().unwrap().take().is_some());
    }

    #[test]
    fn disabled_nexus_start_removes_the_personal_key_and_gives_the_links_back_without_printing_either() {
        let t = temp();
        let root = t.path().join("nexus");
        dev19_record(&root);
        std::fs::write(root.join("apikey.json"), br#"{"key":"FIXTURE-PERSONAL-KEY"}"#).unwrap();
        let ring = Ring(Mutex::new(Some("FIXTURE-KEYRING-KEY".into())));
        let os = FakeOs { current: Mutex::new(Some("kiln".into())), ..Default::default() };
        let lines = startup_cleanup(&root, false, &os, Some(&ring));
        assert_eq!(lines, [
            "removed the stored Nexus personal API key".to_string(),
            "Nexus Mods is off in this build: gave the nxm links back to Vortex".to_string(),
        ]);
        assert!(!root.join("apikey.json").exists() && !root.join("handler.json").exists());
        assert!(ring.0.lock().unwrap().is_none());
        assert_eq!(os.current.lock().unwrap().as_deref(), Some("com.nexusmods.vortex.desktop"));
        assert!(lines.iter().all(|l| !l.contains("FIXTURE")));
        // the next start has nothing to do and says nothing
        assert!(startup_cleanup(&root, false, &os, Some(&ring)).is_empty());

        // a build with Nexus on removes the key too, and leaves a registration alone (it is the user's choice there)
        dev19_record(&root);
        std::fs::write(root.join("apikey.json"), b"{}").unwrap();
        let os = FakeOs { current: Mutex::new(Some("kiln".into())), ..Default::default() };
        assert_eq!(startup_cleanup(&root, true, &os, None), ["removed the stored Nexus personal API key".to_string()]);
        assert!(root.join("handler.json").is_file());
        assert_eq!(os.current.lock().unwrap().as_deref(), Some("kiln"));
    }
}
