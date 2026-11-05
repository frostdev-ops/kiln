//! Tauri commands: modpack. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Client side of server modpacks: `kiln/state/last-server.json` (written by Kiln.Network on a join) is watched and
//! re-emitted as `kiln://last-server`; the consent modal calls modpack_inspect, then modpack_install with
//! `consent: true` (the crate refuses without it). Nothing downloads by itself.

use super::TaskId;
use crate::events::{LastServerEvent, LAST_SERVER};
use crate::state::{blocking, dirs, game};
use kiln_manage::modpack::{ModpackInstallRequest, ModpackPreview, ModpackSource};
use kiln_manage::state::{JoinOutcome, LastServer};
use kiln_manage::{Error, ErrorCode};
use std::collections::BTreeMap;
use std::sync::OnceLock;
use std::time::{Duration, SystemTime};
use tauri::{AppHandle, Emitter};

/// `<data>/modpacks/dismissed.json`: `{gameId: {serverId: modpack sha256 | ""}}`, offers the user said no to.
fn dismissed_path() -> Result<std::path::PathBuf, Error> {
    Ok(dirs()?.root.join("modpacks").join("dismissed.json"))
}

fn dismissed() -> BTreeMap<String, BTreeMap<String, String>> {
    dismissed_path().ok().and_then(|p| kiln_manage::util::read_json(&p).ok().flatten()).unwrap_or_default()
}

/// The last server answer worth an offer: a refusal, or a welcome that still lists missing mods, from a server that
/// advertises a modpack; not one the user dismissed for the same modpack.
fn pending_for(game_id: &str) -> Result<Option<LastServer>, Error> {
    let g = game(game_id)?;
    let Some(last) = kiln_manage::state::read_last_server(&g.path)? else { return Ok(None) };
    let Some(pack) = last.modpack.as_ref() else { return Ok(None) };
    let server_id = last.server.id.clone().unwrap_or_default();
    if dismissed().get(game_id).and_then(|m| m.get(&server_id)).is_some_and(|sha| sha == &pack.sha256) {
        return Ok(None);
    }
    let wanted = match last.outcome {
        JoinOutcome::Rejected => true,
        // joined despite a mismatch (modSync warn): offer what is still missing
        JoinOutcome::Welcome => !last.missing.is_empty(),
        JoinOutcome::Vanilla => false,
    };
    Ok(wanted.then_some(last))
}

static WATCH: OnceLock<()> = OnceLock::new();

/// Starts (once) the thread that re-emits `kiln://last-server` whenever a known game's last-server.json changes.
pub fn watch_last_server(app: &AppHandle) {
    WATCH.get_or_init(|| {
        let app = app.clone();
        let _ = std::thread::Builder::new().name("last-server watch".into()).spawn(move || {
            let mut seen: BTreeMap<String, SystemTime> = BTreeMap::new();
            loop {
                if let Ok(d) = dirs() {
                    for g in kiln_manage::game::known(&d).unwrap_or_default() {
                        let f = g.path.join("kiln/state/last-server.json");
                        let Ok(m) = std::fs::metadata(&f).and_then(|m| m.modified()) else { continue };
                        let first = !seen.contains_key(&g.id);
                        if seen.insert(g.id.clone(), m) == Some(m) || first {
                            continue;  // unchanged, or the state at launcher start (modpack_pending covers it)
                        }
                        if let Ok(Some(last)) = kiln_manage::state::read_last_server(&g.path) {
                            let _ = app.emit(LAST_SERVER, LastServerEvent { game_id: g.id.clone(), last_server: last });
                        }
                    }
                }
                std::thread::sleep(Duration::from_secs(2));
            }
        });
    });
}

#[tauri::command]
pub async fn modpack_pending(app: AppHandle, game_id: String) -> Result<Option<LastServer>, Error> {
    watch_last_server(&app);
    blocking(move || pending_for(&game_id)).await
}

#[tauri::command]
pub async fn modpack_inspect(app: AppHandle, game_id: String, source: Option<ModpackSource>) -> Result<ModpackPreview, Error> {
    watch_last_server(&app);
    blocking(move || {
        let g = game(&game_id)?;
        let last = kiln_manage::state::read_last_server(&g.path)?;
        let source = match source {
            Some(s) => s,
            None => {
                let pack = last
                    .as_ref()
                    .and_then(|l| l.modpack.clone())
                    .ok_or_else(|| Error::not_found("The last server you joined advertises no modpack."))?;
                ModpackSource::Url { url: pack.url, sha256: pack.sha256 }
            }
        };
        kiln_manage::modpack::inspect(&dirs()?, &source, last.as_ref())
    })
    .await
}

#[tauri::command]
pub async fn modpack_install(app: AppHandle, game_id: String, request: ModpackInstallRequest) -> Result<TaskId, Error> {
    if !request.consent {
        return Err(Error::new(ErrorCode::ConsentRequired, "Installing a modpack runs code from the server on your PC: confirm first."));
    }
    let settings = crate::state::read_settings()?;
    if request.allow_http && !settings.allow_http_modpacks {
        return Err(Error::new(ErrorCode::Refused, "http:// modpacks are off. Turn on \"Allow http modpacks\" in Settings first."));
    }
    let g = game(&game_id)?;
    let d = dirs()?;
    let title = match &request.source {
        ModpackSource::Url { url, .. } => format!("Installing modpack from {url}"),
        ModpackSource::Path { path, .. } => format!("Installing modpack {}", path.display()),
    };
    Ok(super::app::spawn_task(&app, "modpack_install", title, move |p| {
        let r = kiln_manage::modpack::install(&d, &g, &request, p)?;
        super::app::to_value(r)
    }))
}

#[tauri::command]
pub async fn modpack_dismiss(game_id: String, server_id: String) -> Result<(), Error> {
    blocking(move || {
        let g = game(&game_id)?;
        let sha = kiln_manage::state::read_last_server(&g.path)?
            .filter(|l| l.server.id.as_deref() == Some(server_id.as_str()))
            .and_then(|l| l.modpack)
            .map(|m| m.sha256)
            .unwrap_or_default();
        let mut all = dismissed();
        all.entry(game_id).or_default().insert(server_id, sha);
        kiln_manage::util::write_json_atomic(&dismissed_path()?, &all)
    })
    .await
}
