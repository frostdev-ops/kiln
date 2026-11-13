//! Tauri commands: cooking mods before the game starts (kiln_manage::cook; docs/kiln/launcher/app.md "Cooking mods").
//! A mod with a cook recipe has files that are made on this PC from this game (models, animations, sounds). `cook_plan`
//! says which mods of the game's profile need a cook now; `cook_run` cooks them (or one mod: "Cook again") as a task,
//! with each line the tools print as its progress. A mod whose cook fails is reported as not cooked: the others are
//! still cooked, and the game still starts.

use super::TaskId;
use super::app::{spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::cook::{self, CookJob, CookRun};
use kiln_manage::cooktools::Locate;
use tauri::{AppHandle, Manager};

/// The Python the Windows bundle carries (`<resources>/python/python.exe`, tools/launcher/build-windows.sh). None on
/// Linux and in a development build: the cook tools then run on the system's python3 (or `$KILN_PYTHON`).
fn bundled_python(app: &AppHandle) -> Option<std::path::PathBuf> {
    app.path().resolve("python/python.exe", tauri::path::BaseDirectory::Resource).ok().filter(|p| p.is_file())
}

/// The mods of the game's profile that have something to cook, each with whether it needs a cook now (`reason`:
/// never cooked for this game, the mod changed, the game was updated, its last cook failed, its cooked files are
/// gone) and how its last cook ended (`last`). Reads only: no tool is started.
#[tauri::command]
pub async fn cook_plan(game_id: String) -> Result<Vec<CookJob>, Error> {
    blocking(move || cook::plan(&crate::state::game(&game_id)?)).await
}

/// Cooks the mods of the game's profile that need it, or with `mod_id` that one mod whatever its record says ("Cook
/// again"), as one task (result: CookRun, one entry per mod cooked; kiln_manage::cook::cook_due). The active profile
/// is written again first when it changed since its activation (`activation` in the result), so Play cooks the mods
/// it then starts. Each due mod is cooked from nothing, every recipe: what made it due may be the game, which a
/// recipe's own check does not read. The game's lock is held meanwhile: no start of the game, no other cook of it.
/// Refused while the game runs ("Cook again" also right after a start). A tool that hangs is stopped within the
/// launcher's limits, and the task's Cancel stops the tool that runs: either with every process it started. A mod's
/// failed cook is in the result with the tools' last lines, never an error of the task: the others are cooked, and
/// the caller starts the game all the same. When no tool can run (no Python, no zstd), every due mod is reported as
/// not cooked with the one sentence that says what to install.
#[tauri::command]
pub async fn cook_run(app: AppHandle, game_id: String, mod_id: Option<String>) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let python = bundled_python(&app);
    let title = match &mod_id {
        Some(id) => format!("Cook {id}"),
        None => "Cook mods".to_string(),
    };
    Ok(spawn_task(&app, "cook_run", title, move |p| {
        cook::refuse_running(super::engines::game_running(&g)?)?;
        let dirs = dirs()?;
        // the tools: the copy this launcher carries, unpacked once under the data folder; never a checkout
        let locate = Locate::from_process(None, Some(dirs.root.join("cooktools")), python);
        // the task's progress takes the tools' lines, and its Cancel stops the tool that runs (with what it started)
        let p = std::cell::RefCell::new(p);
        let run: CookRun = cook::cook_due(&dirs, &g, mod_id.as_deref(), &locate, &mut |i, n, job, line| {
            let said = match line {
                None => format!("Cooking {} ({} of {n})", job.name, i + 1),
                Some(l) => format!("{}: {l}", job.name),
            };
            p.borrow_mut().report(Some(i as f32 / n.max(1) as f32), &said);
        }, &|| p.borrow().canceled())?;
        if !run.mods.is_empty() {
            p.borrow_mut().report(Some(1.0), &match run.failed().count() {
                0 => format!("{} cooked", run.mods.len()),
                f => format!("{} cooked, {f} not", run.mods.len() - f),
            });
        }
        to_value(run)
    }))
}
