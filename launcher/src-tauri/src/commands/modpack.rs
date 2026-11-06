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

/// `<data>/modpacks/dismissed.json`: `{gameId: {serverId: key}}`, what the user said no to (`dismiss_key`).
fn dismissed_path() -> Result<std::path::PathBuf, Error> {
    Ok(dirs()?.root.join("modpacks").join("dismissed.json"))
}

fn dismissed() -> BTreeMap<String, BTreeMap<String, String>> {
    dismissed_path().ok().and_then(|p| kiln_manage::util::read_json(&p).ok().flatten()).unwrap_or_default()
}

/// What dismissing `last` remembers: its modpack's sha256 (that pack isn't offered again), or for a refusal without a
/// modpack the time of that refusal (the next one shows again).
fn dismiss_key(last: &LastServer) -> String {
    match &last.modpack {
        Some(pack) => pack.sha256.clone(),
        None => format!("refused {}", last.written),
    }
}

/// What Play shows from the last server answer: every refusal (its banner, with the offer when the server advertises
/// a modpack), and a welcome that still lists missing mods from a server with a modpack (joined despite a mismatch,
/// modSync warn); not one the user dismissed. `dismissed` is this game's part of dismissed.json.
fn worth_showing(last: &LastServer, dismissed: Option<&BTreeMap<String, String>>) -> bool {
    let server_id = last.server.id.clone().unwrap_or_default();
    if dismissed.and_then(|m| m.get(&server_id)).is_some_and(|key| *key == dismiss_key(last)) {
        return false;
    }
    match last.outcome {
        JoinOutcome::Rejected => true,
        JoinOutcome::Welcome => last.modpack.is_some() && !last.missing.is_empty(),
        JoinOutcome::Vanilla => false,
    }
}

fn pending_for(game_id: &str) -> Result<Option<LastServer>, Error> {
    let g = game(game_id)?;
    let Some(last) = kiln_manage::state::read_last_server(&g.path)? else { return Ok(None) };
    let wanted = worth_showing(&last, dismissed().get(game_id));
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
        // serverId "" is a server that sent no id (an older Kiln server)
        let key = kiln_manage::state::read_last_server(&g.path)?
            .filter(|l| l.server.id.as_deref().unwrap_or("") == server_id)
            .map(|l| dismiss_key(&l))
            .unwrap_or_default();
        let mut all = dismissed();
        all.entry(game_id).or_default().insert(server_id, key);
        kiln_manage::util::write_json_atomic(&dismissed_path()?, &all)
    })
    .await
}

#[cfg(test)]
mod tests {
    use super::*;
    use kiln_manage::state::ServerRef;

    /// last-server.json after a lab refused a client by the default policy: no modpack published.
    fn refusal(written: &str, modpack: bool) -> LastServer {
        let mut v = serde_json::json!({
            "format": 1, "written": written, "outcome": "rejected",
            "reason": "This server requires mods you do not have: npcsample 0.1.0 (missing), quests-sample 2.0.0 (missing)",
            "server": { "id": "srv-d09b032bfe63", "name": "Kiln lab mylab", "address": null, "kiln": "0.2.0-dev" },
            "modpack": null,
            "required": [
                { "id": "net-test", "version": "0.1.0", "hash": format!("kdh1:{}", "3".repeat(64)), "kind": "kiln" },
                { "id": "npcsample", "version": "0.1.0", "hash": format!("kdh1:{}", "b".repeat(64)), "kind": "kiln" },
            ],
            "truncated": false, "missing": ["npcsample", "quests-sample"],
            "history": [{ "format": 1, "written": "2026-11-06T05:32:08Z", "outcome": "welcome", "server": {}, "modpack": null, "required": [] }],
        });
        if modpack {
            v["modpack"] = serde_json::json!({ "url": "https://example.org/pack.zip", "sha256": "a".repeat(64) });
        }
        serde_json::from_value(v).expect("last-server.json shape")
    }

    fn dismissed_with(last: &LastServer) -> BTreeMap<String, String> {
        BTreeMap::from([(last.server.id.clone().unwrap_or_default(), dismiss_key(last))])
    }

    #[test]
    fn a_refusal_without_a_modpack_shows() {
        assert!(worth_showing(&refusal("2026-11-06T05:33:51Z", false), None));
    }

    #[test]
    fn a_refusal_with_a_modpack_shows() {
        assert!(worth_showing(&refusal("2026-11-06T05:33:51Z", true), None));
    }

    #[test]
    fn a_dismissed_refusal_stays_away_until_the_next_one() {
        let first = refusal("2026-11-06T05:33:51Z", false);
        let d = dismissed_with(&first);
        assert!(!worth_showing(&first, Some(&d)));
        assert!(worth_showing(&refusal("2026-11-06T06:00:00Z", false), Some(&d)), "a later refusal shows again");
    }

    #[test]
    fn a_dismissed_modpack_is_not_offered_again() {
        let first = refusal("2026-11-06T05:33:51Z", true);
        let d = dismissed_with(&first);
        assert!(!worth_showing(&refusal("2026-11-06T06:00:00Z", true), Some(&d)));
    }

    #[test]
    fn a_dismissal_for_another_server_changes_nothing() {
        let d = BTreeMap::from([("srv-other".to_string(), "refused 2026-11-06T05:33:51Z".to_string())]);
        assert!(worth_showing(&refusal("2026-11-06T05:33:51Z", false), Some(&d)));
    }

    #[test]
    fn a_welcome_shows_only_with_missing_mods_and_a_modpack() {
        let mut w = refusal("2026-11-06T05:33:51Z", true);
        w.outcome = JoinOutcome::Welcome;
        assert!(worth_showing(&w, None));
        w.missing.clear();
        assert!(!worth_showing(&w, None));
        let mut no_pack = refusal("2026-11-06T05:33:51Z", false);
        no_pack.outcome = JoinOutcome::Welcome;
        assert!(!worth_showing(&no_pack, None));
        no_pack.outcome = JoinOutcome::Vanilla;
        no_pack.server = ServerRef::default();
        assert!(!worth_showing(&no_pack, None));
    }
}
