//! Cooking a mod: one implementation for `kiln cook`, `kiln build` and the launcher.
//!
//! A mod with a cook recipe (`<mod>/*/cook.json`, or looks and sounds declared in `blocks/*.json`) has files that are
//! made on each machine from that machine's game: models, animations, sounds, Impact programs. The cook tools
//! ([`crate::cooktools`]) make them, into the mod's `assets/cooked` folder and nowhere else, so a cooked mod keeps its
//! identity ([`crate::hash::mod_identity`]).
//!
//! - [`cook_folder`] cooks (or only checks) one mod folder: the sound blocks' steps, every recipe whose own check says
//!   a cook is needed, the looks of the blocks. It returns what each recipe did and everything the tools said.
//! - [`needs_cook`] is the one rule for when the launcher cooks a mod before the game starts: the mod was never cooked
//!   for this game (it was installed or switched on), its files changed (it was updated), the game's build changed
//!   since its last cook (cooked files are made from game data), its last cook failed, or its cooked files are gone.
//! - [`CookedState`] (`<game>/kiln/state/cooked.json`) is the record that rule reads: per mod, what was cooked for
//!   which game build, and how it ended.
//! - [`plan`] lists the mods of the game's profile that need a cook; [`cook_mods`] cooks them one after the other. One
//!   mod's failed cook stops neither the others nor the start of the game: it is reported as not cooked, with the
//!   tool's own text.

use crate::cooktools::CookTools;
use crate::game::GameInstall;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

// ---------------------------------------------------------------------------------------------- one folder ----

/// How [`cook_folder`] runs.
#[derive(Debug, Clone, Default)]
pub struct CookOptions {
    /// The game the cook reads (a client's folder, or a dedicated server's). None: only the tools that need no game
    /// run (sounds and animations of the mod's own files); a recipe that reads the game is `skipped` (or stops a
    /// strict run).
    pub game: Option<PathBuf>,
    /// Only say whether a cook is needed; nothing is written.
    pub check: bool,
    /// The mod's id for a project whose `kiln.mod.json` is made by its build (ids follow from it).
    pub mod_id: Option<String>,
    /// A problem that is not a recipe's own (no tool for it, no game to read, the sound blocks' steps) stops the run
    /// with a [`CookStop`] instead of being reported in the result: `kiln cook`. Off: `kiln build` and the launcher.
    pub strict: bool,
}

/// Why a strict [`cook_folder`] stopped: `code` is `kiln cook`'s error code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CookStop {
    pub code: &'static str,
    pub message: String,
}

/// One recipe of a [`CookReport`] (`kiln cook --json` prints these keys).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecipeResult {
    /// The recipe file, or the mod's `blocks` folder for the looks.
    pub recipe: PathBuf,
    pub tool: String,
    /// The cook's end (absent in a check).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ok: Option<bool>,
    /// Whether the tool ran (false: it was up to date).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cooked: Option<bool>,
    /// A check's answer.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub needed: Option<bool>,
    /// Why it did not run (no tool for it, no game to read).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub skipped: Option<String>,
}

/// What [`cook_folder`] did.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookReport {
    /// Every recipe ended well (a check: none needs a cook).
    pub ok: bool,
    pub recipes: Vec<RecipeResult>,
    /// Everything the tools said, in order: what a player reads when a cook fails.
    #[serde(skip)]
    pub text: String,
}

/// The cook recipes of a mod folder: every `<dir>/*/cook.json` (assets, impact, audio), sorted.
pub fn recipes(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path().join("cook.json")).filter(|p| p.is_file()).collect();
    v.sort();
    v
}

/// Whether a mod folder has anything a cook may have to make: a recipe, or blocks (their looks and sounds are cooked
/// without a recipe of the mod's own). The tools say the rest.
pub fn has_recipe(dir: &Path) -> bool {
    !recipes(dir).is_empty() || std::fs::read_dir(dir.join("blocks")).into_iter().flatten().flatten().any(|e| e.path().extension().is_some_and(|x| x == "json"))
}

fn read_jsonc(p: &Path) -> Option<serde_json::Value> {
    crate::jsonc::parse(std::fs::read_to_string(p).ok()?.trim_start_matches('\u{feff}')).ok()
}

/// Runs a tool and hands its lines (stdout and stderr, as they come) to `on_line`. -> (it ended well, what it said)
fn run_lines(cmd: &mut Command, on_line: &mut dyn FnMut(&str)) -> std::io::Result<(bool, String)> {
    let mut child = cmd.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn()?;
    let (tx, rx) = std::sync::mpsc::channel::<String>();
    let mut readers = Vec::new();
    for pipe in [child.stdout.take().map(|p| Box::new(p) as Box<dyn std::io::Read + Send>), child.stderr.take().map(|p| Box::new(p) as Box<dyn std::io::Read + Send>)].into_iter().flatten() {
        let tx = tx.clone();
        readers.push(std::thread::spawn(move || {
            for line in BufReader::new(pipe).split(b'\n').flatten() {
                let _ = tx.send(String::from_utf8_lossy(&line).trim_end_matches('\r').to_string());
            }
        }));
    }
    drop(tx);
    let mut text = String::new();
    for line in rx {
        on_line(&line);
        text.push_str(&line);
        text.push('\n');
    }
    for r in readers {
        let _ = r.join();
    }
    Ok((child.wait()?.success(), text))
}

/// Recipes with sound steps the blocks made, so that deleting the last sound block also removes its steps.
fn has_sound_blocks(dir: &Path) -> bool {
    let has_sounds = std::fs::read_dir(dir.join("blocks")).into_iter().flatten().flatten()
        .any(|e| e.path().extension().is_some_and(|x| x == "json") && read_jsonc(&e.path()).is_some_and(|d| d.get("sounds").is_some()));
    has_sounds || read_jsonc(&dir.join("assets/cook.json")).is_some_and(|d| d.get("blockSounds").is_some())
}

/// Whether a folder needs the cook tools at all: sound blocks, a recipe, or blocks that may hold looks.
pub fn wants_tools(dir: &Path) -> bool {
    has_sound_blocks(dir) || !recipes(dir).is_empty() || dir.join("blocks").is_dir()
}

/// The first step of a cook: the steps of the mod's sound blocks (`blocks/*.json` "sounds") written into its
/// `assets/cook.json` (soundcook `blocks`), or with `check` compared with it. -> whether the recipe is current. Nothing
/// runs for a mod without sound blocks. A failure stops a strict run and is a warning otherwise.
pub fn sound_blocks(tools: &CookTools, dir: &Path, opts: &CookOptions, say: &mut dyn FnMut(&str)) -> std::result::Result<bool, CookStop> {
    if !has_sound_blocks(dir) {
        return Ok(true);
    }
    let mut cmd = tools.script_command("tools/soundcook/soundcook.py");
    cmd.arg("blocks").arg(dir);
    if opts.check {
        cmd.arg("--check");
    }
    if let Some(id) = opts.mod_id.as_deref().filter(|id| !id.is_empty() && !dir.join("kiln.mod.json").is_file()) {
        cmd.arg("--mod").arg(id);
    }
    let error = match cmd.stdin(Stdio::null()).output() {
        Ok(o) if o.status.success() || (opts.check && o.status.code() == Some(1)) => {
            let said = String::from_utf8_lossy(&o.stdout);
            if opts.check || said.contains("written") || said.contains("removed") {
                say(&format!("kiln: {}", said.trim()));
            }
            return Ok(o.status.success());
        }
        Ok(o) => String::from_utf8_lossy(&o.stderr).trim().to_string(),
        Err(e) => e.to_string(),
    };
    if opts.strict {
        return Err(CookStop { code: "cook_failed", message: format!("sound blocks: {error}") });
    }
    say(&format!("kiln: warning: sound blocks: {error}"));
    Ok(false)
}

/// Cooks one mod folder (a mod project, or an installed mod's folder), or with `check` only says what needs a cook.
///
/// 1. the steps of the mod's sound blocks are written into `assets/cook.json` (soundcook `blocks`);
/// 2. every recipe (`*/cook.json`) is asked whether it needs a cook (the tool's own `--check`: the recipe, its
///    sources, its outputs and the report of the last cook) and cooked when it does, with `--update-manifest`;
/// 3. the looks of `blocks/*.json` are cooked for a mod with no modelcook recipe of its own.
///
/// Every line a tool prints goes to `on_line` as it comes and into [`CookReport::text`]. A recipe that fails does not
/// stop the others. Everything a cook writes from the game goes into the mod's `assets/cooked` (the tools refuse
/// anything else), so the mod's identity is the same before and after.
pub fn cook_folder(tools: &CookTools, dir: &Path, opts: &CookOptions, on_line: &mut dyn FnMut(&str)) -> std::result::Result<CookReport, CookStop> {
    let mut text = String::new();
    let mut say = |line: &str, text: &mut String| {
        on_line(line);
        text.push_str(line);
        text.push('\n');
    };
    let stop = |code: &'static str, message: String| CookStop { code, message };
    let mut all_ok = true;
    let mut results = Vec::new();
    let quiet = !opts.strict; // kiln build and the launcher: nothing is said about what is up to date

    // 1. sound blocks
    match sound_blocks(tools, dir, opts, &mut |l| say(l, &mut text)) {
        Ok(current) => all_ok &= current,
        Err(e) => return Err(e),
    }

    // 2. the recipes
    let list = recipes(dir);
    let looks = dir.join("blocks").is_dir()
        && read_jsonc(&dir.join("assets/cook.json")).map(|d| d["tool"].as_str().unwrap_or("modelcook").to_string()).as_deref() != Some("modelcook");
    if list.is_empty() && !looks {
        if opts.strict {
            say("no cook recipes (*/cook.json) in this project", &mut text);
        }
        return Ok(CookReport { ok: all_ok, recipes: results, text });
    }
    for r in &list {
        let tool = read_jsonc(r).and_then(|d| d["tool"].as_str().map(str::to_owned)).unwrap_or_else(|| "modelcook".into());
        let entry = |ok, cooked, needed, skipped: Option<String>| RecipeResult { recipe: r.clone(), tool: tool.clone(), ok, cooked, needed, skipped };
        let Some((_, verb)) = tools.script(&tool) else {
            let msg = format!("{}: no cook tool '{tool}' in {}", r.display(), tools.root.display());
            if opts.strict {
                return Err(stop("tool_missing", msg));
            }
            say(&format!("kiln: warning: {msg}"), &mut text);
            all_ok = false;
            results.push(entry(Some(false), None, None, Some(msg)));
            continue;
        };
        let mut chk = tools.command(&tool).expect("the tool's script was found above");
        chk.arg(verb).arg(r).arg("--check").stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
        let needed = !chk.status().map(|s| s.success()).unwrap_or(false);
        if opts.check {
            say(&format!("{}: {}", r.display(), if needed { "cook needed" } else { "up to date" }), &mut text);
            all_ok &= !needed;
            results.push(entry(None, None, Some(needed), None));
            continue;
        }
        if !needed {
            if !quiet {
                say(&format!("{}: up to date", r.display()), &mut text);
            }
            results.push(entry(Some(true), Some(false), None, None));
            continue;
        }
        let mut cmd = tools.command(&tool).expect("the tool's script was found above");
        match &opts.game {
            Some(g) => {
                cmd.arg("--game").arg(g);
            }
            None if tool == "soundcook" || tool == "animcook" => {
                cmd.arg("--no-game");
            }
            None => {
                let msg = format!("{}: the cook reads your installed game; none found (--game or KILN_GAME)", r.display());
                if opts.strict {
                    return Err(stop("not_found", msg));
                }
                say(&format!("kiln: warning: {msg}"), &mut text);
                all_ok = false;
                results.push(entry(Some(false), None, None, Some(msg)));
                continue;
            }
        }
        cmd.arg(verb).arg(r).arg("--update-manifest");
        say(&format!("kiln: cooking {} ({tool})", r.display()), &mut text);
        let ok = match run_lines(&mut cmd, &mut |l| say(l, &mut text)) {
            Ok((ok, _)) => ok,
            Err(e) if opts.strict => return Err(stop("tool_missing", format!("cannot run {cmd:?}: {e}"))),
            Err(e) => {
                say(&format!("kiln: cannot run the cook tool: {e}"), &mut text);
                false
            }
        };
        all_ok &= ok;
        results.push(entry(Some(ok), Some(true), None, None));
    }

    // 3. the looks of blocks/*.json, cooked with no recipe of the mod's own (`modelcook.py looks`)
    if looks {
        let script = "tools/modelcook/modelcook.py";
        let where_ = dir.join("blocks");
        let entry = |ok, cooked, needed, skipped: Option<String>| RecipeResult { recipe: where_.clone(), tool: "modelcook".into(), ok, cooked, needed, skipped };
        if let Ok(probe) = tools.script_command(script).arg("looks").arg(dir).arg("--check").stdin(Stdio::null()).output() {
            let said = String::from_utf8_lossy(&probe.stdout);
            let needed = !probe.status.success();
            if said.contains("no looks") {
                if opts.strict && list.is_empty() {
                    say("no cook recipes (*/cook.json) in this project, and no looks with files in blocks/*.json", &mut text);
                }
            } else if opts.check {
                say(&format!("{} (looks): {}", where_.display(), if needed { "cook needed" } else { "up to date" }), &mut text);
                all_ok &= !needed;
                results.push(entry(None, None, Some(needed), None));
            } else if !needed {
                if !quiet {
                    say(&format!("{} (looks): up to date", where_.display()), &mut text);
                }
                results.push(entry(Some(true), Some(false), None, None));
            } else if let Some(game) = &opts.game {
                let mut cmd = tools.script_command(script);
                cmd.arg("--game").arg(game).arg("looks").arg(dir).arg("--update-manifest");
                // a model's GUID follows from the mod's id: a project whose kiln.mod.json is made by the build says it
                if let Some(id) = opts.mod_id.as_deref().filter(|id| !id.is_empty() && !dir.join("kiln.mod.json").is_file()) {
                    cmd.arg("--mod").arg(id);
                }
                say(&format!("kiln: cooking the looks of {}", where_.display()), &mut text);
                let ok = match run_lines(&mut cmd, &mut |l| say(l, &mut text)) {
                    Ok((ok, _)) => ok,
                    Err(e) if opts.strict => return Err(stop("tool_missing", format!("cannot run {cmd:?}: {e}"))),
                    Err(e) => {
                        say(&format!("kiln: cannot run the cook tool: {e}"), &mut text);
                        false
                    }
                };
                all_ok &= ok;
                results.push(entry(Some(ok), Some(true), None, None));
            } else {
                let msg = format!("{} (looks): the cook reads your installed game; none found (--game or KILN_GAME)", where_.display());
                if opts.strict {
                    return Err(stop("not_found", msg));
                }
                say(&format!("kiln: warning: {msg}"), &mut text);
                all_ok = false;
                results.push(entry(Some(false), None, None, Some(msg)));
            }
        }
    }
    Ok(CookReport { ok: all_ok, recipes: results, text })
}

// ------------------------------------------------------------------------------------- what needs a cook ----

/// `<game>/kiln/state/cooked.json`: what was cooked for this game, by mod id. Written by the launcher after each
/// mod's cook; [`needs_cook`] reads it. It is about one game folder: a server and a client each have their own.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookedState {
    pub format: u32,
    #[serde(default)]
    pub mods: BTreeMap<String, CookedMod>,
}

/// The last cook of one mod for one game.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookedMod {
    /// The mod's identity then (`hash::mod_identity`: its files without its cook outputs).
    pub identity: String,
    /// The game's build then ([`game_build`]).
    pub game_build: String,
    /// When (RFC 3339).
    pub at: String,
    pub ok: bool,
    /// The cook left an `assets/cooked` folder (a mod whose blocks need nothing cooked has none, and is not cooked
    /// again for lacking it).
    #[serde(default)]
    pub files: bool,
    /// A failed cook: the last lines the tools said.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

/// Why a mod is cooked before the game starts ([`needs_cook`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CookReason {
    /// It was never cooked for this game: the mod was installed, or switched on for the first time.
    NotCooked,
    /// Its files changed since its last cook: the mod was updated.
    ModChanged,
    /// The game's build changed since its last cook: cooked files are made from the game's data.
    GameChanged,
    /// Its last cook failed.
    LastFailed,
    /// Its cooked files are gone (its last cook left an `assets/cooked` folder, and the folder the game loads has none).
    FilesGone,
    /// The player asked ("Cook again").
    Asked,
}

impl CookReason {
    /// The reason in a sentence's place: "… because …".
    pub fn text(self) -> &'static str {
        match self {
            CookReason::NotCooked => "it was not cooked for this game yet",
            CookReason::ModChanged => "the mod changed since its last cook",
            CookReason::GameChanged => "the game was updated since its last cook",
            CookReason::LastFailed => "its last cook failed",
            CookReason::FilesGone => "its cooked files are gone",
            CookReason::Asked => "you asked",
        }
    }
}

/// The one rule for "this mod needs a cook before the game starts". `last`: its record in [`CookedState`] (None: never
/// cooked for this game); `identity`: the mod's identity now; `game_build`: the game's build now ([`game_build`]);
/// `cooked_there`: the folder the game loads holds an `assets/cooked` folder.
///
/// In this order: never cooked (installed, or switched on for the first time); the mod's files changed (updated); the
/// game's build changed; the last cook failed (tried again at every start: it stops nothing); the cooked files its
/// last cook left are gone. Else None: the tools are not even started.
pub fn needs_cook(last: Option<&CookedMod>, identity: &str, game_build: &str, cooked_there: bool) -> Option<CookReason> {
    let Some(last) = last else { return Some(CookReason::NotCooked) };
    if last.identity != identity {
        Some(CookReason::ModChanged)
    } else if last.game_build != game_build {
        Some(CookReason::GameChanged)
    } else if !last.ok {
        Some(CookReason::LastFailed)
    } else if last.files && !cooked_there {
        Some(CookReason::FilesGone)
    } else {
        None
    }
}

/// What tells one build of the game from the next, cheaply: Steam's build id when the install has one, else the size
/// and the write time of the game's data file (a game update replaces it). Never a hash of 20 GB.
pub fn game_build(install: &GameInstall) -> String {
    if let Some(id) = install.build_id.as_deref().filter(|id| !id.is_empty()) {
        return format!("steam:{id}");
    }
    let stem = install.exe.strip_suffix(".exe").unwrap_or(&install.exe);
    for file in [format!("{stem}.kfc"), install.exe.clone()] {
        if let Ok(m) = std::fs::metadata(install.path.join(&file)) {
            let at = m.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_secs());
            return format!("file:{file}:{}:{at}", m.len());
        }
    }
    "unknown".into()
}

fn state_file(game_dir: &Path) -> PathBuf {
    game_dir.join("kiln/state/cooked.json")
}

/// The record of a game (empty when there is none yet, or it cannot be read: everything is cooked again then).
pub fn read_state(game_dir: &Path) -> CookedState {
    crate::util::read_json::<CookedState>(&state_file(game_dir)).ok().flatten().filter(|s| s.format == 1).unwrap_or(CookedState { format: 1, mods: BTreeMap::new() })
}

fn write_state(game_dir: &Path, state: &CookedState) -> Result<()> {
    let file = state_file(game_dir);
    crate::engine_mods::refuse_symlink_path(game_dir, &file)?;
    if let Some(p) = file.parent() {
        std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?;
    }
    crate::util::write_json_atomic(&file, state)
}

/// A mod of the game's profile that has something to cook.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookJob {
    pub id: String,
    /// The mod's name (its manifest's, else its id).
    pub name: String,
    /// The folder the game loads the mod from: where the cook writes `assets/cooked`.
    pub folder: PathBuf,
    pub identity: String,
    /// None: cooked for this build, nothing to do.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<CookReason>,
    /// Its last cook for this game, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last: Option<CookedMod>,
}

/// The enabled Kiln mods of the profile the game uses that have something to cook, each with whether it needs a cook
/// now ([`needs_cook`]) and its last cook. The folders are the ones the game's `kiln/profile.json` names (the Library
/// entry, or its copy in the game where the game cannot read the Library), so the game finds what the cook makes.
/// A mod whose folder is not there (not activated yet) is left out. Nothing is written.
pub fn plan(install: &GameInstall) -> Result<Vec<CookJob>> {
    let Some(file) = crate::profile::read_game_profile(&install.path)? else { return Ok(Vec::new()) };
    let state = read_state(&install.path);
    let build = game_build(install);
    let mut jobs = Vec::new();
    for m in file.mods.kiln.iter().filter(|m| m.enabled) {
        let folder = if crate::winpath::is_absolute_game_path(&m.path) || m.path.starts_with('/') {
            match crate::winpath::to_host(&m.path, install) {
                Ok(p) => p,
                Err(_) => continue,
            }
        } else {
            install.path.join(crate::paths::game::KILN).join(m.path.replace('\\', "/"))
        };
        if !folder.join(crate::hash::MOD_MANIFEST).is_file() || !has_recipe(&folder) {
            continue;
        }
        let Ok(identity) = crate::hash::mod_identity(&folder) else { continue };
        let name = crate::manifest::read_kiln(&folder).map(|k| k.name).ok().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| m.id.clone());
        let last = state.mods.get(&m.id).cloned();
        let cooked_there = folder.join("assets/cooked").is_dir();
        let reason = needs_cook(last.as_ref(), &identity, &build, cooked_there);
        jobs.push(CookJob { id: m.id.clone(), name, folder, identity, reason, last });
    }
    Ok(jobs)
}

/// How one mod's cook ended ([`cook_mods`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModCook {
    pub id: String,
    pub name: String,
    pub reason: CookReason,
    pub ok: bool,
    /// Not cooked: why, in the tools' own words (their last lines), or the one sentence that says why no tool ran.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

/// What [`cook_mods`] did. A failed mod never stops the others, and never the start of the game.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookRun {
    pub mods: Vec<ModCook>,
}

impl CookRun {
    pub fn failed(&self) -> impl Iterator<Item = &ModCook> {
        self.mods.iter().filter(|m| !m.ok)
    }
}

/// The end of what the tools said, as a player reads it: the last lines, without the lines that only count clips.
pub fn error_text(text: &str, why: &str) -> String {
    let lines: Vec<&str> = text.lines().map(str::trim_end).filter(|l| !l.trim().is_empty()).collect();
    let tail: Vec<&str> = lines.iter().rev().take(12).rev().copied().collect();
    if tail.is_empty() { why.to_string() } else { tail.join("\n") }
}

/// Cooks the jobs that have a reason, one after the other, and records each in the game's [`CookedState`]. `cook` runs
/// one mod's cook (the launcher: [`cook_folder`] with the tools it located; tests: anything) and returns whether it
/// ended well and what the tools said. `tools`: Err when no tool can run (no Python, no zstd): every mod is then
/// reported as not cooked with that one sentence, and nothing else happens. `on_event(index, total, job, line)` gets
/// the start of each mod (`line` None) and every line its tools print.
pub fn cook_mods(install: &GameInstall, jobs: &[CookJob], tools: std::result::Result<(), String>,
                 cook: &mut dyn FnMut(&CookJob, &mut dyn FnMut(&str)) -> (bool, String),
                 on_event: &mut dyn FnMut(usize, usize, &CookJob, Option<&str>)) -> Result<CookRun> {
    let due: Vec<&CookJob> = jobs.iter().filter(|j| j.reason.is_some()).collect();
    let mut run = CookRun::default();
    if due.is_empty() {
        return Ok(run);
    }
    let build = game_build(install);
    let mut state = read_state(&install.path);
    for (i, job) in due.iter().enumerate() {
        let reason = job.reason.expect("filtered above");
        on_event(i, due.len(), job, None);
        let (ok, error) = match &tools {
            Err(why) => (false, Some(why.clone())),
            Ok(()) => {
                let (ok, text) = cook(job, &mut |line| on_event(i, due.len(), job, Some(line)));
                (ok, (!ok).then(|| error_text(&text, "the cook failed without a word")))
            }
        };
        // the identity after the cook is the one before it (a cook writes only into assets/cooked); read again all the
        // same, so a tool that broke that rule shows as "the mod changed" at the next start instead of going unseen
        let identity = crate::hash::mod_identity(&job.folder).unwrap_or_else(|_| job.identity.clone());
        let files = job.folder.join("assets/cooked").is_dir();
        state.mods.insert(job.id.clone(), CookedMod { identity, game_build: build.clone(), at: crate::util::now_rfc3339(), ok, files, error: error.clone() });
        write_state(&install.path, &state)?;
        run.mods.push(ModCook { id: job.id.clone(), name: job.name.clone(), reason, ok, error });
    }
    Ok(run)
}

/// The launcher's and the tests' way to say "cook this mod now" whatever the record says ("Cook again").
pub fn asked(mut job: CookJob) -> CookJob {
    job.reason = Some(CookReason::Asked);
    job
}

/// A refusal for a game that is running: its files are in use, and it has read the mods already.
pub fn refuse_running(running: bool) -> Result<()> {
    if running {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first: a cook writes the files the game is using."));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn last(identity: &str, build: &str, ok: bool) -> CookedMod {
        CookedMod { identity: identity.into(), game_build: build.into(), at: "2026-11-12T10:00:00Z".into(), ok, files: true, error: (!ok).then(|| "boom".into()) }
    }

    #[test]
    fn needs_cook_is_the_one_rule() {
        // installed, or switched on for the first time: no record for this game
        assert_eq!(needs_cook(None, "kdh1:a", "steam:1", false), Some(CookReason::NotCooked));
        assert_eq!(needs_cook(None, "kdh1:a", "steam:1", true), Some(CookReason::NotCooked), "cooked files nobody recorded are not trusted");
        // cooked for this mod and this game: nothing to do
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:1", true), None);
        // updated: its files are others
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:b", "steam:1", true), Some(CookReason::ModChanged));
        // the game was updated
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:2", true), Some(CookReason::GameChanged));
        // both: the mod's change is named
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:b", "steam:2", true), Some(CookReason::ModChanged));
        // the last cook failed: again at the next start
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", false)), "kdh1:a", "steam:1", true), Some(CookReason::LastFailed));
        // the cooked folder is gone (a copy made again, a folder cleaned by hand)
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:1", false), Some(CookReason::FilesGone));
        // a mod whose cook had nothing to make (blocks without looks) left no folder: it is not cooked again for that
        let nothing = CookedMod { files: false, ..last("kdh1:a", "steam:1", true) };
        assert_eq!(needs_cook(Some(&nothing), "kdh1:a", "steam:1", false), None);
    }

    #[test]
    fn the_tools_last_lines_are_the_error_a_player_reads() {
        let text = (1..=30).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n") + "\n\nmodelcook: input bronze: not in the game (game update or wrong side?)\n";
        let e = error_text(&text, "x");
        assert!(e.ends_with("modelcook: input bronze: not in the game (game update or wrong side?)"), "{e}");
        assert_eq!(e.lines().count(), 12);
        assert_eq!(error_text("  \n", "the cook failed without a word"), "the cook failed without a word");
    }

    #[test]
    fn recipes_and_blocks_make_a_mod_cookable() {
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-has").unwrap();
        let m = d.path().join("m");
        std::fs::create_dir_all(m.join("assets")).unwrap();
        assert!(!has_recipe(&m));
        std::fs::create_dir_all(m.join("blocks")).unwrap();
        assert!(!has_recipe(&m), "an empty blocks folder");
        std::fs::write(m.join("blocks/items.json"), b"{}").unwrap();
        assert!(has_recipe(&m));
        std::fs::remove_dir_all(m.join("blocks")).unwrap();
        std::fs::create_dir_all(m.join("impact")).unwrap();
        std::fs::write(m.join("impact/cook.json"), b"{}").unwrap();
        std::fs::write(m.join("assets/cook.json"), b"{}").unwrap();
        assert_eq!(recipes(&m), [m.join("assets/cook.json"), m.join("impact/cook.json")]);
        assert!(has_recipe(&m));
    }
}
