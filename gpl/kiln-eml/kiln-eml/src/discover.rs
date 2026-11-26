// SPDX-License-Identifier: GPL-3.0-or-later
//! Which EML mods this start runs: the Kiln profile's `mods.eml` entries, folders other engine plugins hand in
//! (kiln.eml add_mod), then the drop-in folders `<game>/kiln/eml/mods/` and EML's own `<game>/mods/`. Every candidate
//! gets an entry in the state file, loaded or not, with the reason.

use std::io::Read;
use std::path::Component;

use camino::{Utf8Path, Utf8PathBuf};
use mod_loader::ModManifest;
use serde::Serialize;

/// One mod folder or zip and where it came from (`profile`, `kiln-dropin`, `game-mods`, or an engine's name).
#[derive(Clone, Debug)]
pub struct Candidate {
    pub path: Utf8PathBuf,
    pub source: String,
    pub enabled: bool,
}

/// A line of `<game>/kiln/state/eml.json` `mods[]`.
#[derive(Clone, Debug, Serialize)]
pub struct Entry {
    pub id: Option<String>,
    pub version: Option<String>,
    pub name: Option<String>,
    pub source: String,
    pub path: String,
    /// `loaded` | `failed` | `skipped` | `disabled` | `duplicate` | `pending`; while another loader runs the EML mods
    /// (phase `aside`): `own-loader` (in that loader's folder, it runs there) | `not-loaded` (only in the Library), and
    /// under ShroudForge `off-in-shroudforge` (ShroudForge's switch for it is off) | `other-side` (it targets the other
    /// side only)
    pub state: String,
    pub reason: Option<String>,
    pub capabilities: Vec<String>,
}

/// The `mods.eml` list of a Kiln profile (`[{ "id", "path", "enabled" }]`, the profile's folderMod shape).
pub fn profile_candidates(profile_json: &str, kiln_dir: &Utf8Path) -> Vec<Candidate> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return Vec::new();
    };
    let Some(list) = v.pointer("/mods/eml").and_then(|l| l.as_array()) else { return Vec::new() };
    list.iter()
        .filter_map(|e| {
            let path = e.get("path")?.as_str()?;
            Some(Candidate {
                path: resolve_profile_path(path, kiln_dir),
                source: "profile".into(),
                enabled: e.get("enabled").and_then(|b| b.as_bool()).unwrap_or(true),
            })
        })
        .collect()
}

/// Does the profile ask for EML's drop-in folders (`"dropins": true` or a list holding "eml")?
pub fn profile_dropins(profile_json: &str) -> bool {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return false;
    };
    match v.get("dropins") {
        Some(serde_json::Value::Bool(b)) => *b,
        Some(serde_json::Value::Array(a)) => a.iter().any(|k| k.as_str() == Some("eml")),
        _ => false,
    }
}

/// The `id` of each `mods.eml` entry of a Kiln profile (the launcher's Library ids), in order.
pub fn profile_ids(profile_json: &str) -> Vec<(String, bool)> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return Vec::new();
    };
    let Some(list) = v.pointer("/mods/eml").and_then(|l| l.as_array()) else { return Vec::new() };
    list.iter().filter_map(|e| Some((e.get("id")?.as_str()?.to_owned(),
        e.get("enabled").and_then(|b| b.as_bool()).unwrap_or(true)))).collect()
}

/// The marker the Kiln launcher writes into the copy it places in an EML loader's folder for that loader
/// (`.kiln-library.json`: `{format, kind, id, version, hash}`; `id` is the Library id, which need not be the mod's own
/// manifest id). Some(id) when `dir` is the launcher's copy of the EML mod `id`.
pub fn kiln_copy_of(dir: &Utf8Path) -> Option<String> {
    let file = dir.join(".kiln-library.json");
    let m = std::fs::symlink_metadata(&file).ok()?;
    if !m.is_file() || m.len() > 64 * 1024 { return None; }
    let v: serde_json::Value = serde_json::from_str(std::fs::read_to_string(&file).ok()?.trim_start_matches('\u{feff}')).ok()?;
    if v.get("kind")?.as_str()? != "eml" { return None; }
    v.get("id")?.as_str().map(str::to_owned)
}

fn bounded_json(path: &Utf8Path, limit: u64) -> Option<serde_json::Value> {
    let m = checked_metadata(path).ok()?;
    if !m.is_file() || m.len() > limit { return None; }
    let mut text = String::new();
    std::fs::File::open(path).ok()?.take(limit + 1).read_to_string(&mut text).ok()?;
    if text.len() as u64 > limit { return None; }
    serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()
}

fn placement_path_matches(recorded: &str, candidate: &Utf8Path) -> bool {
    placement_path_matches_with(recorded, candidate, dos_name)
}

/// `to_dos`: the Windows name Wine gives a Unix path (None off Wine).
fn placement_path_matches_with(recorded: &str, candidate: &Utf8Path, to_dos: impl Fn(&str) -> Option<String>) -> bool {
    let same = |root: &str| relative_under(candidate, &Utf8PathBuf::from(root.replace('\\', "/"))).is_some_and(|p| p.as_str().is_empty());
    if same(recorded) { return true; }
    // A Linux launcher writes Unix receipt paths; Wine exposes those same paths through its Z: drive, and through the
    // drive whose folder holds them: Proton maps the Steam library to S:, so a game in it runs as S:\steamapps\...
    recorded.starts_with('/') && ((candidate.as_str().get(..2).is_some_and(|s| s.eq_ignore_ascii_case("Z:")) && same(&format!("Z:{recorded}")))
        || to_dos(recorded).is_some_and(|dos| same(&dos)))
}

/// The Windows name Wine gives the Unix path `unix` (`kernel32!wine_get_dos_file_name`: the drive whose folder is
/// the deepest one holding it, the conversion that named the game's own path). None on Windows proper.
#[cfg(windows)]
fn dos_name(unix: &str) -> Option<String> {
    use std::ffi::c_void;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetModuleHandleW(name: *const u16) -> *mut c_void;
        fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
        fn GetProcessHeap() -> *mut c_void;
        fn HeapFree(heap: *mut c_void, flags: u32, memory: *mut c_void) -> i32;
    }
    // (the export is looked up once per process, in a kiln start's early phase; public: the
    // GetModuleHandleW(kernel32) + GetProcAddress("wine_get_dos_file_name") lookup made here at every call)
    let export = wine_dos_export(&|| {
        let kernel32: Vec<u16> = "kernel32.dll".encode_utf16().chain(Some(0)).collect();
        let module = unsafe { GetModuleHandleW(kernel32.as_ptr()) };
        if module.is_null() { return std::ptr::null_mut(); }
        unsafe { GetProcAddress(module, c"wine_get_dos_file_name".as_ptr().cast()) }
    });
    if export.is_null() { return None; }
    // SAFETY: Wine's export, WCHAR *CDECL wine_get_dos_file_name(LPCSTR): a heap string the caller frees, or null
    let convert: unsafe extern "C" fn(*const std::ffi::c_char) -> *mut u16 = unsafe { std::mem::transmute(export) };
    let unix = std::ffi::CString::new(unix).ok()?;
    let wide = unsafe { convert(unix.as_ptr()) };
    if wide.is_null() { return None; }
    let mut len = 0;
    while unsafe { *wide.add(len) } != 0 { len += 1; }
    let name = String::from_utf16(unsafe { std::slice::from_raw_parts(wide, len) }).ok();
    unsafe { HeapFree(GetProcessHeap(), 0, wide.cast()); }
    name
}

#[cfg(not(windows))]
fn dos_name(_unix: &str) -> Option<String> { None }

// (kiln mode's discovery under a held loader lock)
/// Wine's `kernel32!wine_get_dos_file_name` (null on Windows proper), looked up through `lookup` once per process.
/// Kiln mode's discovery runs in kiln-eml's on_ready, while a package DLL's initializer may still hold the loader lock,
/// and Wine's loader takes that lock in GetModuleHandleW and GetProcAddress (a receipt's Unix path then waited for the
/// initializer to return; native Windows does not wait): kiln-eml's early phase asks first ([`prime_dos_name`]), on the
/// boot thread before any package DLL loads.
#[cfg_attr(not(windows), allow(dead_code))]
fn wine_dos_export(lookup: &dyn Fn() -> *mut std::ffi::c_void) -> *mut std::ffi::c_void {
    static EXPORT: std::sync::OnceLock<usize> = std::sync::OnceLock::new();
    *EXPORT.get_or_init(|| lookup() as usize) as *mut std::ffi::c_void
}

/// The lookup [`dos_name`] needs, made now (kiln-eml's early phase of a kiln start).
pub fn prime_dos_name() {
    let _ = dos_name("/");
}

/// A selected marked copy is already represented by its profile entry (even when disabled). An unselected copy
/// retained after edits must not become a drop-in either, but only a matching install receipt establishes ownership:
/// a marker shipped in an archive alone does not prevent a genuine manual mod from running.
pub fn without_profile_copies(candidates: Vec<Candidate>, profile: &[(String, bool)], kiln: &Utf8Path) -> (Vec<Candidate>, Vec<Utf8PathBuf>) {
    let receipts = if checked_metadata(kiln).is_ok_and(|m| m.is_dir())
        && checked_metadata(&kiln.join("state")).is_ok_and(|m| m.is_dir()) {
        bounded_json(&kiln.join("state/library-placements.json"), 8 * 1024 * 1024)
    } else { None };
    let mut skipped = Vec::new();
    let kept = candidates.into_iter().filter(|c| {
        let ours = kiln_copy_of(&c.path).is_some_and(|id| {
            profile.iter().any(|(p, _)| p.eq_ignore_ascii_case(&id)) || receipts.as_ref().and_then(|v| v.as_array()).is_some_and(|rs| {
                let marker = bounded_json(&c.path.join(".kiln-library.json"), 64 * 1024);
                rs.iter().any(|r| r.get("path").and_then(|p| p.as_str()).is_some_and(|p| placement_path_matches(p, &c.path))
                    && marker.as_ref().is_some_and(|m| m.get("format").and_then(|v| v.as_u64()) == Some(1) && r.get("marker") == Some(m)))
            })
        });
        if ours { skipped.push(c.path.clone()); }
        !ours
    }).collect();
    (kept, skipped)
}

/// What becomes of the profile's EML mods while another loader runs the EML mods (EML's own dbghelp.dll, or
/// ShroudForge): that loader reads only its own mods folder (`roots`, the first is named in reasons), never the
/// Library. A mod with the launcher's copy there runs under it (`own-loader`); any other enabled mod does not load
/// (`not-loaded`). `shroudforge`: Some(this process is a dedicated server) when ShroudForge is that loader. ShroudForge
/// runs a package only while its switch is on and on the sides it targets ([`shroudforge_switch`]): a package there it
/// has off is `off-in-shroudforge`, one for the other side `other-side`.
pub fn aside_entries(profile_json: &str, kiln_dir: &Utf8Path, roots: &[Utf8PathBuf], who: &str, shroudforge: Option<bool>) -> Vec<Entry> {
    aside_entries_with(profile_json, kiln_dir, roots, who, shroudforge, None)
}

/// [`aside_entries`] while Kiln hosts ShroudForge (`skip`): a profile mod that is off but whose folder in ShroudForge's
/// mods folder ShroudForge would run (its own switch on: the player's own folder, which Kiln never writes into, or
/// Kiln's copy switched on since the last activation) is named to `skip` by its package id. True: the runtime skips it
/// this start (shroudforge_host_skip_packages), and its row is `disabled` with [`skipped_line`]. False (the runtime takes
/// no skip list), or no `skip` (Kiln does not host ShroudForge, or another loader runs the EML mods): it runs, and its
/// row says so (`own-loader`), why, and how to turn it off ([`off_but_present`]). Never `disabled` for what runs.
pub fn aside_entries_with(profile_json: &str, kiln_dir: &Utf8Path, roots: &[Utf8PathBuf], who: &str, shroudforge: Option<bool>,
                          mut skip: Option<&mut dyn FnMut(&str) -> bool>) -> Vec<Entry> {
    let mut ids = profile_ids(profile_json);
    // These entries are intentionally off on the kiln-eml route, including for older runners. SF's own switch
    // runs the package; report that delegation truthfully only while SF actually owns this start.
    if shroudforge.is_some() {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json) {
            if let Some(required) = v.get("emlRuntime").and_then(|v| v.as_array()) {
                for (id, enabled) in &mut ids {
                    if required.iter().any(|r| r.as_str() == Some(id.as_str()))
                        && v.get("emlRuntimeBlocked").and_then(|v| v.get(id.as_str())).is_none() { *enabled = true; }
                }
            }
        }
    }
    let paths = profile_candidates(profile_json, kiln_dir);
    let on = profile_on(profile_json, kiln_dir);
    let folder = roots.first().map(|r| r.to_string()).unwrap_or_else(|| "mods".into());
    ids.into_iter().enumerate().map(|(i, (id, enabled))| {
        let path = paths.get(i).map(|c| c.path.to_string()).unwrap_or_default();
        // `<root>/<id>`, or the entry's own path when the launcher placed it in that folder under the name a native
        // DLL looks for (`mods/XHL-Auto-Loot` for the Library id `xhl-auto-loot`)
        let own_path = paths.get(i).map(|c| c.path.clone())
            .filter(|p| p.parent().is_some_and(|parent| roots.iter().any(|r| is_root(parent, r))));
        // The entry's own path first: on Windows `<root>\<id>` also opens `<root>\XHL-Auto-Loot` (names are not case
        // sensitive there), and the report must name the folder as the launcher placed it.
        let copy = own_path.into_iter().chain(roots.iter().map(|r| r.join(&id))).find(|d| kiln_copy_of(d).is_some_and(|m| m == id));
        let wanted = paths.get(i).and_then(|c| classify(&c.path).ok()).map(|m| m.id).unwrap_or_else(|| id.clone());
        let manual = if copy.is_none() {
            roots.iter().flat_map(|r| folder_candidates(r, "own-loader")).map(|c| c.path)
                .find(|p| classify(p).is_ok_and(|m| m.id.eq_ignore_ascii_case(&wanted)))
        } else { None };
        let own = copy.clone().or(manual);
        let what = if copy.is_some() { "Kiln's copy" } else { "hand-installed mod" };
        // off in the profile: is its folder here one the loader runs anyway? (EML's own loader has no switch)
        let runs_anyway = |c: &Utf8PathBuf| shroudforge.is_none_or(|server| shroudforge_switch(c, server).is_ok());
        let (state, reason) = if !enabled {
            // (a package an entry that is on stands for runs for that entry: never skipped for this one)
            match own.as_ref().filter(|c| runs_anyway(c) && !stands_on(&on, &wanted) && !classify(c).is_ok_and(|m| stands_on(&on, &m.id))) {
                None => ("disabled", "switched off in the profile".to_string()),
                Some(c) => off_but_present(&id, &wanted, c, copy.is_some(), who, shroudforge, &folder, skip.as_deref_mut()),
            }
        } else if let Some(c) = &own {
            match shroudforge.map(|server| shroudforge_switch(c, server)) {
                None => ("own-loader", format!("runs under {who} ({what} at {c})")),
                Some(Ok(())) => ("own-loader", format!("runs under {who}: its ShroudForge switch is on ({what} at {c})")),
                Some(Err(Off::Side(why))) => ("other-side", format!("ShroudForge runs it on the other side only: {why} ({what} at {c})")),
                Some(Err(Off::Switch(why))) if copy.is_some() => ("off-in-shroudforge", format!("{who} runs this start, but ShroudForge has \
                    Kiln's copy at {c} switched off: {why}. Nothing applies it this start. Press Play in the Kiln launcher (or activate \
                    the profile again): Kiln switches it on in ShroudForge's settings")),
                // no switch helps a file ShroudForge refuses; without ShroudForge kiln-eml runs the mod (it reads only `launcher`)
                Some(Err(Off::Refused(why))) => ("off-in-shroudforge", format!("{who} runs this start, but ShroudForge does not run the \
                    {what} at {c}: {why}. Nothing applies it this start, and no switch changes that: a version of the mod whose file \
                    ShroudForge reads does, or a start without ShroudForge mods in the profile (Kiln then runs it itself)")),
                // the player's own folder: the words promised to a player on Nexus (2026-11-14), not ShroudForge's file
                // (that read as "Kiln wants a ShroudForge file from an EML mod"); a server has no in-game menu
                Some(Err(Off::Switch(_))) => ("off-in-shroudforge", format!("{who} runs this start. You put this mod into \
                    ShroudForge's folder yourself, and ShroudForge has it switched off. {} Your folder: {c}",
                    if shroudforge == Some(true) {
                        "A server has no in-game menu: remove the folder and activate the profile again, and Kiln places its own \
                        copy, switched on."
                    } else {
                        "Switch it on in Kiln's in-game menu (Mods > ShroudForge), or remove the folder and press Play in the Kiln \
                        launcher: the mod is in your profile, so Kiln then places its own copy, switched on."
                    })),
            }
        } else {
            ("not-loaded", format!("{who} runs this start and reads only {folder}; this mod is in Kiln's Library, so it does \
                not load. Activate the profile again in the Kiln launcher to copy it there, or remove that loader to let Kiln run it"))
        };
        Entry { id: Some(id), version: None, name: None, source: "profile".into(),
            path: own.map(|c| c.to_string()).unwrap_or(path), state: state.into(), reason: Some(reason), capabilities: Vec::new() }
    }).collect()
}

/// The words of a profile-off package Kiln had ShroudForge skip this start, in the player's own folder `path`: its row's
/// reason in eml.json and its kiln.log line. Kiln never writes into the folder.
pub fn skipped_line(id: &str, path: &Utf8Path) -> String {
    format!("{id} is off in the profile: your own folder {path} is skipped this start; Kiln leaves its files as they are")
}

/// In the reason of every skipped package ([`skipped_line`], and Kiln's copy).
const SKIPPED: &str = " is skipped this start";
/// In the reason of every package that runs although the profile has it off ([`off_but_present`]).
const RUNS_OFF: &str = " although it is off in the profile: ";

/// Has Kiln had this profile-off row skipped this start?
fn is_skipped(e: &Entry) -> bool {
    e.state == "disabled" && e.reason.as_deref().is_some_and(|r| r.contains(SKIPPED))
}

/// Does this row run although the profile has its mod off?
fn runs_off(e: &Entry) -> bool {
    e.state == "own-loader" && e.reason.as_deref().is_some_and(|r| r.contains(RUNS_OFF))
}

/// A profile mod that is off, with a folder `c` that `who` runs (`kilns`: Kiln's copy; else the player's own, which Kiln
/// never writes into). `package`: its mod.json id. Under ShroudForge (`shroudforge`: Some(server)) Kiln asks `skip` to
/// have it skipped this start; otherwise, or when the runtime refuses, the row says that it runs, why, and how to stop it.
fn off_but_present(id: &str, package: &str, c: &Utf8Path, kilns: bool, who: &str, shroudforge: Option<bool>, folder: &str,
                   skip: Option<&mut (dyn FnMut(&str) -> bool + '_)>) -> (&'static str, String) {
    let hosted = skip.is_some();
    let Some(server) = shroudforge else {
        // EML's own loader: every mod of its folder runs, there is no switch
        return ("own-loader", if kilns {
            format!("runs under {who}{RUNS_OFF}Kiln's copy {c} is in its folder, and {who} runs every mod in {folder} (it has no \
                switch). Press Play in the Kiln launcher (or activate the profile again): Kiln then takes its copy out")
        } else {
            format!("runs under {who}{RUNS_OFF}it is your own folder {c}, which Kiln never writes into, and {who} runs every mod \
                in {folder} (it has no switch). To turn it off, move the folder out of {folder}")
        });
    };
    // the runtime matches the folder's own mod.json id, exactly
    let package = classify(c).map(|m| m.id).unwrap_or_else(|_| package.to_owned());
    if let Some(skip) = skip {
        if skip(&package) {
            return ("disabled", if kilns {
                format!("{id} is off in the profile: Kiln's copy {c} has ShroudForge's switch on and{SKIPPED}; press Play in \
                    the Kiln launcher (or activate the profile again) and Kiln takes its copy out (or switches it off)")
            } else { skipped_line(id, c) });
        }
    }
    let why = if hosted {
        "this ShroudForge runtime takes no skip list from Kiln (Kiln's bundled runtime takes one from 0.2.0-dev.31.12 on, an \
            installed ShroudForge does not: launcher Settings, \"Use my installed ShroudForge\")"
    } else {
        "Kiln does not host ShroudForge this start, so it cannot have it skipped"
    };
    ("own-loader", if kilns {
        format!("runs under {who}{RUNS_OFF}ShroudForge's switch is on in Kiln's copy {c}, and {why}. Press Play in the Kiln \
            launcher (or activate the profile again): Kiln then takes its copy out (or switches it off)")
    } else if server {
        format!("runs under {who}{RUNS_OFF}it is your own folder {c}, which Kiln never writes into, and ShroudForge's own \
            switch for it is on; {why}. To turn it off, put \"enabled\": false into its extended.mod.json or move the folder \
            out of {folder}, then restart the server")
    } else {
        format!("runs under {who}{RUNS_OFF}it is your own folder {c}, which Kiln never writes into, and ShroudForge's own \
            switch for it is on; {why}. To turn it off, switch it off in Kiln's in-game menu (Mods > ShroudForge) or in \
            ShroudForge's Modloader, or move the folder out of {folder}")
    })
}

/// What the profile has on, by Library id and by package id (the entry's mod.json id): its EML entries that are on (and
/// those the launcher handed to ShroudForge's runtime, `emlRuntime`, unless `emlRuntimeBlocked` names them) and its
/// ShroudForge entries that are on. A package one of them stands for is never skipped for an entry that is off.
fn profile_on(profile_json: &str, kiln_dir: &Utf8Path) -> Vec<String> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else { return Vec::new() };
    let runtime = |id: &str| v.get("emlRuntime").and_then(|r| r.as_array()).is_some_and(|r| r.iter().any(|x| x.as_str() == Some(id)))
        && v.get("emlRuntimeBlocked").and_then(|b| b.get(id)).is_none();
    let mut on = Vec::new();
    for kind in ["eml", "shroudforge"] {
        for e in v.pointer(&format!("/mods/{kind}")).and_then(|l| l.as_array()).into_iter().flatten() {
            let Some(id) = e.get("id").and_then(|i| i.as_str()) else { continue };
            if !e.get("enabled").and_then(|b| b.as_bool()).unwrap_or(true) && !(kind == "eml" && runtime(id)) { continue; }
            on.push(id.to_owned());
            if let Some(package) = e.get("path").and_then(|p| p.as_str()).and_then(|p| manifest_id(&resolve_profile_path(p, kiln_dir))) {
                on.push(package);
            }
        }
    }
    on
}

/// Does an entry the profile has on ([`profile_on`]) stand for the package `id`?
fn stands_on(on: &[String], id: &str) -> bool {
    on.iter().any(|o| o.eq_ignore_ascii_case(id))
}

/// The `id` of the mod.json in the folder `dir` (any package format: a ShroudForge package is no EML mod for
/// [`classify`]).
fn manifest_id(dir: &Utf8Path) -> Option<String> {
    let v = bounded_json(&dir.join("mod.json"), 1024 * 1024)?;
    v.get("id")?.as_str().map(str::to_owned)
}

/// The profile's EML mods that are off (`mods.eml` entries with `enabled: false`), as (Library id, package id): the
/// package id from the entry's mod.json, else the Library id. A package the launcher handed to ShroudForge's runtime
/// (`emlRuntime`) is on, whatever its entry says (engines.md, runtime EML packages).
fn profile_off(profile_json: &str, kiln_dir: &Utf8Path) -> Vec<(String, String)> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else { return Vec::new() };
    let runtime = |id: &str| v.get("emlRuntime").and_then(|r| r.as_array()).is_some_and(|r| r.iter().any(|x| x.as_str() == Some(id)));
    let Some(list) = v.pointer("/mods/eml").and_then(|l| l.as_array()) else { return Vec::new() };
    list.iter().filter_map(|e| {
        let id = e.get("id")?.as_str()?;
        if e.get("enabled").and_then(|b| b.as_bool()).unwrap_or(true) || runtime(id) { return None; }
        let package = e.get("path").and_then(|p| p.as_str()).and_then(|p| classify(&resolve_profile_path(p, kiln_dir)).ok())
            .map(|m| m.id).unwrap_or_else(|| id.to_owned());
        Some((id.to_owned(), package))
    }).collect()
}

/// Kiln's own runner: a drop-in (`game-mods`, `kiln-dropin`) whose package is an EML mod the profile has off is the
/// player's own folder of that mod (Kiln's copies are no drop-ins, [`without_profile_copies`]). It ran beside the
/// profile's `disabled` row; now it is left out of `run` and its row is `disabled` with [`skipped_line`]. Kiln reads it
/// in place and never writes into it. Returns the kiln.log lines (info), one per folder.
pub fn honor_profile_off(profile_json: &str, kiln_dir: &Utf8Path, run: &mut Vec<Utf8PathBuf>, entries: &mut [Entry]) -> Vec<String> {
    let on = profile_on(profile_json, kiln_dir);
    let mut off = profile_off(profile_json, kiln_dir);
    off.retain(|(_, package)| !stands_on(&on, package));
    let mut lines = Vec::new();
    if off.is_empty() { return lines; }
    for e in entries.iter_mut().filter(|e| e.state == "pending" && matches!(e.source.as_str(), "game-mods" | "kiln-dropin")) {
        let Some((id, _)) = off.iter().find(|(_, package)| e.id.as_deref().is_some_and(|m| m.eq_ignore_ascii_case(package))) else { continue };
        let path = Utf8PathBuf::from(&e.path);
        run.retain(|p| p != &path);
        let line = skipped_line(id, &path);
        e.state = "disabled".into();
        e.reason = Some(line.clone());
        lines.push(format!("kiln-eml: {line}"));
    }
    lines
}

/// The extended.mod.json a dedicated server needs in an EML package of ShroudForge's mods folder: without it, or with
/// `enabled` not true, ShroudForge keeps the package off, and a server has no in-game menu to switch it on.
pub const SERVER_SWITCH_FILE: &str = r#"{"schemaVersion":1,"launcher":"EML","enabled":true,"targets":["client","server"],"settings":{}}"#;

/// The EML mods of the drop-in folders while ShroudForge runs the EML mods and kiln-eml stands aside, with drop-ins on
/// (no profile, or a profile whose drop-ins are on): what kiln-eml would have run from there itself, and nothing said
/// so (a dedicated server from a server pack, 2026-11-21). `root`: ShroudForge's mods folder; ShroudForge runs an EML
/// package there only while its switch is on ([`shroudforge_switch`]), and one it has off or refuses is listed as
/// `off-in-shroudforge` (one it runs, or runs on the other side only, is not listed: that is ShroudForge's choice).
/// `<kiln>/eml/mods`, kiln-eml's own drop-in folder, which no other loader reads: each EML mod there is `not-loaded`.
/// A Kiln-managed copy (a profile's, or one with a matching install receipt) is no drop-in ([`without_profile_copies`]),
/// and a folder `listed` already names is not listed twice. `server`: this process is a dedicated server. Listing only:
/// nothing here changes what runs.
pub fn aside_dropins(root: &Utf8Path, kiln_dir: &Utf8Path, profile: &[(String, bool)], listed: &[Entry], who: &str, server: bool) -> Vec<Entry> {
    let mut out = Vec::new();
    let named = |p: &Utf8Path| listed.iter().any(|e| is_root(p, Utf8Path::new(&e.path)));
    let own = kiln_dir.join("eml").join("mods");
    for (dir, source) in [(root.to_owned(), "game-mods"), (own.clone(), "kiln-dropin")] {
        // the same folder twice (ShroudForge's paths.mods set to kiln/eml/mods): ShroudForge's reading
        if source == "kiln-dropin" && is_root(&own, root) { continue; }
        let (kept, _) = without_profile_copies(folder_candidates(&dir, source), profile, kiln_dir);
        for c in kept {
            if named(&c.path) || out.iter().any(|e: &Entry| is_root(&c.path, Utf8Path::new(&e.path))) { continue; }
            // what ShroudForge runs, or runs on the other side only, is its choice: skipped before its manifest is read
            let off = if source == "game-mods" {
                match shroudforge_switch(&c.path, server) { Ok(()) | Err(Off::Side(_)) => continue, Err(off) => Some(off) }
            } else { None };
            // an EML mod (ShroudForge's own packages, Kiln mods and other folders are not kiln-eml's to report)
            let Ok(m) = classify(&c.path) else { continue };
            let (state, reason) = if source == "kiln-dropin" {
                ("not-loaded", format!("{who} runs this start and reads only {root}; kiln-eml stands aside, so nothing runs \
                    the EML mods of its own drop-in folder {own}. Move this mod to {root}{}", if server {
                        format!(" with an extended.mod.json that says \"enabled\": true ({SERVER_SWITCH_FILE}), and restart the server")
                    } else {
                        " and switch it on in Kiln's in-game menu (Mods > ShroudForge)".to_string()
                    }))
            } else {
                match off {
                    None | Some(Off::Side(_)) => continue,
                    Some(Off::Switch(why)) => ("off-in-shroudforge", format!("{who} runs this start, and ShroudForge has this EML \
                        package at {} switched off: {why}. kiln-eml stands aside, so nothing runs it. {}", c.path, if server {
                            format!("A server has no in-game menu: put an extended.mod.json into its folder that says \"enabled\": \
                                true ({SERVER_SWITCH_FILE}), then restart the server")
                        } else {
                            "Switch it on in Kiln's in-game menu (Mods > ShroudForge)".to_string()
                        })),
                    Some(Off::Refused(why)) => ("off-in-shroudforge", format!("{who} runs this start, but ShroudForge does not run \
                        the EML package at {}: {why}. kiln-eml stands aside, so nothing runs it, and no switch changes that: fix \
                        or remove its extended.mod.json", c.path)),
                }
            };
            out.push(Entry { id: Some(m.id), version: Some(m.version.to_string()), name: Some(m.name), source: source.into(),
                path: c.path.to_string(), state: state.into(), reason: Some(reason), capabilities: Vec::new() });
        }
    }
    out
}

/// The kiln.log line of [`aside_dropins`] (a warning), when there is anything to say: one line with the counts.
pub fn aside_dropins_log(entries: &[Entry], root: &Utf8Path, server: bool) -> Option<String> {
    let ids = |state: &str| entries.iter().filter(|e| e.state == state).map(|e| e.id.as_deref().unwrap_or("?")).collect::<Vec<_>>();
    let (off, lost) = (ids("off-in-shroudforge"), ids("not-loaded"));
    if off.is_empty() && lost.is_empty() { return None; }
    let mut parts = Vec::new();
    if !off.is_empty() {
        parts.push(format!("{} EML package(s) in {root} are switched off in ShroudForge or refused by it, so nothing runs them ({}); {}",
            off.len(), off.join(", "), if server {
                "a server needs an extended.mod.json that says \"enabled\": true in each (docs/kiln/dedicated-servers.md, Mods on the server)"
            } else {
                "switch them on in Kiln's in-game menu (Mods > ShroudForge)"
            }));
    }
    if !lost.is_empty() {
        parts.push(format!("{} EML mod(s) in kiln/eml/mods do not load while ShroudForge runs the EML mods ({}): move them to {root}",
            lost.len(), lost.join(", ")));
    }
    Some(format!("kiln-eml: {} (kiln/state/eml.json)", parts.join("; ")))
}

/// The EML packages of ShroudForge's mods folder `root` (Kiln's copies and the player's own; ShroudForge's own packages,
/// Kiln mods and other folders are not EML packages): the ids of those ShroudForge runs in this process (`server`: a
/// dedicated server), its switch on for this side ([`shroudforge_switch`]), in folder order, and how many there are.
/// The aside start says so in kiln.log ([`shroudforge_runs_log`]): sidelia's server (Nexus, 2026-11-21) ran its mods
/// fine, but the log only warned about the ones nothing ran. Listing only.
/// `skipped`: the folders Kiln had ShroudForge skip this start ([`aside_entries_with`]); they do not run.
pub fn shroudforge_runs(root: &Utf8Path, server: bool, skipped: &[Utf8PathBuf]) -> (Vec<String>, usize) {
    let (mut runs, mut total) = (Vec::new(), 0);
    for c in folder_candidates(root, "game-mods") {
        let Ok(m) = classify(&c.path) else { continue };
        total += 1;
        if shroudforge_switch(&c.path, server).is_ok() && !skipped.iter().any(|s| is_root(&c.path, s)) { runs.push(m.id); }
    }
    (runs, total)
}

/// The folders of the rows Kiln had ShroudForge skip this start (for [`shroudforge_runs`]) and their ids.
pub fn skipped_rows(entries: &[Entry]) -> (Vec<Utf8PathBuf>, Vec<String>) {
    entries.iter().filter(|e| is_skipped(e)).map(|e| (Utf8PathBuf::from(&e.path), e.id.clone().unwrap_or_default())).unzip()
}

/// The info line of [`shroudforge_runs`]: "kiln-eml: 3 of 5 EML packages in <root> run under ShroudForge: a, b, c";
/// nothing when the folder holds no EML package. At most 40 ids are named, then how many more.
/// `skipped`: the ids of the packages Kiln had ShroudForge skip this start, named after the ones that run.
pub fn shroudforge_runs_log(runs: &[String], total: usize, root: &Utf8Path, skipped: &[String]) -> Option<String> {
    if total == 0 { return None; }
    const NAMED: usize = 40;
    let named = |ids: &[String]| {
        let mut s = ids.iter().take(NAMED).map(String::as_str).collect::<Vec<_>>().join(", ");
        if ids.len() > NAMED { s.push_str(&format!(" and {} more", ids.len() - NAMED)); }
        s
    };
    let mut line = format!("kiln-eml: {} of {total} EML package{} in {root} run{} under ShroudForge", runs.len(),
        if total == 1 { "" } else { "s" }, if total == 1 { "s" } else { "" });
    if !runs.is_empty() {
        line.push_str(": ");
        line.push_str(&named(runs));
    }
    if !skipped.is_empty() {
        line.push_str(&format!("; {} {} off in the profile and skipped this start: {}", skipped.len(),
            if skipped.len() == 1 { "is" } else { "are" }, named(skipped)));
    }
    Some(line)
}

/// Is `dir` the loader's folder `root`, as the game's process names folders? On Windows (the game, also under Wine)
/// by Windows' rules ([`windows_same_folder`]), elsewhere (kiln-eml-run on Linux) part by part.
fn is_root(dir: &Utf8Path, root: &Utf8Path) -> bool {
    if cfg!(windows) { windows_same_folder(dir.as_str(), root.as_str()) } else { relative_under(dir, root).is_some_and(|rest| rest.as_str().is_empty()) }
}

/// Two spellings of one folder, told apart as Windows tells them: not by the verbatim prefix, the separator, an empty
/// or `.` part, or case. ShroudForge writes its `paths.mods` from a canonical path, `\\?\C:\…\mods` on Windows (plugin.rs
/// `loader_roots` names it as [`windows_plain_path`] does), while the launcher names its game-folder copies from the game
/// folder, `C:\…\mods\<folder>` (a player's Nexus report, 2026-11-14: kiln-eml took Kiln's copy there for a folder of
/// the player's). Strings in, so a test checks it on any system.
fn windows_same_folder(a: &str, b: &str) -> bool {
    fn key(p: &str) -> (usize, Vec<String>) {
        let p = windows_plain_path(p);
        let rooted = p.bytes().take_while(|&c| c == b'\\').count().min(2)
            + usize::from(p.as_bytes().get(1) == Some(&b':') && p.as_bytes().get(2) == Some(&b'\\')) * 3;
        // Windows' ordinal case-blind comparison: one upper-case character for each
        // (the export root's fold, which also joins the Greek letters with a subscript iota as
        // NTFS and Wine do; public: `to_uppercase` where it is one character)
        let fold = |s: &str| s.chars().map(crate::same_path::fold).collect::<String>();
        (rooted, p.split('\\').filter(|s| !s.is_empty() && *s != ".").map(fold).collect())
    }
    key(a) == key(b)
}

/// A Windows path as Windows names it to a player: `\` between its names, and no verbatim prefix where the plain
/// spelling names the same folder (`\\?\C:\…\mods` and `//?/C:/…/mods` are `C:\…\mods`, `\\?\UNC\nas\share` is
/// `\\nas\share`). ShroudForge writes its `paths.mods` from a canonical path; kiln-eml names the folders in it (kiln.log,
/// eml.json, the launcher's engine status) as that root joined with each folder's name, which must not read
/// `//?/C:/…/mods\bare`. Strings in, so a test checks it on any system.
pub(crate) fn windows_plain_path(p: &str) -> String {
    let p = p.replace('/', "\\");
    // `\\?\C:\x` names `C:\x`, `\\?\UNC\host\share` names `\\host\share`; other verbatim paths have no other name
    // Literal trailing dots/spaces need the extended namespace; an ordinary Win32 path
    // would trim them. Keep that namespace instead of aliasing another folder.
    let literal = p.split('\\').any(|name| name.ends_with('.') || name.ends_with(' '));
    if let Some(rest) = p.strip_prefix(r"\\?\UNC\").filter(|_| !literal) {
        format!(r"\\{rest}")
    } else if let Some(rest) = p.strip_prefix(r"\\?\").filter(|r| !literal && r.as_bytes().get(1) == Some(&b':')) {
        rest.to_owned()
    } else {
        p
    }
}

/// Why ShroudForge does not run a package ([`shroudforge_switch`]).
#[derive(Debug)]
pub enum Off {
    /// Its switch is off: no `extended.mod.json`, or `enabled` not true.
    Switch(String),
    /// ShroudForge refuses its `extended.mod.json` (or cannot read it): no switch makes it run.
    Refused(String),
    /// Its `targets` leave out this side.
    Side(String),
}

/// Does ShroudForge run the package at `dir` (a folder or a zip) in this process (`server`: a dedicated server)? Read
/// from ShroudForge's file `extended.mod.json`: its `enabled` must be `true` (ShroudForge keeps every package off until
/// then, and a legacy EML package has no such file until ShroudForge writes one at the first change of its switch).
/// `targets`, an array: "server" alone is the server, any other array the client, with "server" too both sides; none:
/// an EML package (`launcher: "EML"`) runs on both sides, another package on the client.
pub fn shroudforge_switch(dir: &Utf8Path, server: bool) -> Result<(), Off> {
    const NAME: &str = "extended.mod.json";
    let text = if dir.is_dir() {
        let file = dir.join(NAME);
        match checked_metadata(&file) {
            Err(_) if !file.exists() => None,
            Err(e) => return Err(Off::Refused(format!("its {NAME} cannot be read ({e})"))),
            Ok(m) if !m.is_file() || m.len() > 1024 * 1024 => return Err(Off::Refused(format!("its {NAME} is no file ShroudForge reads"))),
            Ok(_) => Some(std::fs::read_to_string(&file).map_err(|e| Off::Refused(format!("its {NAME} cannot be read ({e})")))?),
        }
    } else {
        let mut zip = std::fs::File::open(dir).ok().and_then(|f| zip::ZipArchive::new(f).ok())
            .ok_or_else(|| Off::Refused("it is not a readable zip".into()))?;
        match zip.by_name(NAME) {
            Err(_) => None,
            Ok(f) if f.size() > 1024 * 1024 => return Err(Off::Refused(format!("its {NAME} is larger than ShroudForge reads"))),
            Ok(mut f) => {
                let mut s = String::new();
                f.read_to_string(&mut s).map_err(|e| Off::Refused(format!("its {NAME} cannot be read ({e})")))?;
                Some(s)
            }
        }
    };
    let Some(text) = text else {
        return Err(Off::Switch(format!("it has no {NAME}, the file that holds ShroudForge's switch (ShroudForge keeps a package off until its switch is on)")));
    };
    // ShroudForge's own reader is strict JSON (no byte order mark, no comments)
    let v: serde_json::Value = serde_json::from_str(&text).map_err(|e| Off::Refused(format!("ShroudForge refuses its {NAME} ({e})")))?;
    let refused = |why: &str| Off::Refused(format!("ShroudForge refuses its {NAME}: {why}"));
    if !v.is_object() || v.get("schemaVersion").and_then(|v| v.as_f64()) != Some(1.0) {
        return Err(refused("schemaVersion must be the number 1"));
    }
    if v.get("launcher").is_some_and(|v| !matches!(v.as_str(), Some("EML" | "SF"))) {
        return Err(refused("launcher must be EML or SF"));
    }
    if let Some(targets) = v.get("targets") {
        let valid = targets.as_array().is_some_and(|a| !a.is_empty() && a.len() <= 2
            && a.iter().all(|v| matches!(v.as_str(), Some("client" | "server")))
            && (a.len() == 1 || a[0] != a[1]));
        if !valid { return Err(refused("targets must name client or server without duplicates")); }
    }
    if v.get("settings").is_some_and(|s| !s.is_object()) {
        return Err(refused("settings must be an object"));
    }
    if v.get("enabled").and_then(|b| b.as_bool()) != Some(true) {
        return Err(Off::Switch(format!("its {NAME}, the file that holds ShroudForge's switch, does not say enabled: true")));
    }
    let runs = match v.get("targets").and_then(|t| t.as_array()) {
        Some(targets) => {
            let has = |side: &str| targets.iter().any(|t| t.as_str() == Some(side));
            if server { has("server") } else { has("client") || !has("server") }
        }
        None => !server || v.get("launcher").and_then(|l| l.as_str()) == Some("EML"),
    };
    if runs { Ok(()) } else { Err(Off::Side(format!("its {NAME} targets the {} only", if server { "client" } else { "server" }))) }
}

/// The kiln.log lines of an `aside` start (level, text: 2 a warning, 3 info): one warning per profile mod that does not
/// run, then the count of what runs under `who`.
pub fn aside_log(entries: &[Entry], who: &str) -> Vec<(i32, String)> {
    let mut out = Vec::new();
    let count = |state: &str| entries.iter().filter(|m| m.state == state).count();
    let (runs, lost, off, other) = (count("own-loader"), count("not-loaded"), count("off-in-shroudforge"), count("other-side"));
    let anyway = entries.iter().filter(|m| runs_off(m)).count();
    for m in entries.iter().filter(|m| m.state == "not-loaded" || m.state == "off-in-shroudforge") {
        let verb = if m.state == "not-loaded" { "load" } else { "run" };
        out.push((2, format!("kiln-eml: profile EML mod {} does not {verb}: {}", m.id.as_deref().unwrap_or("?"), m.reason.as_deref().unwrap_or(""))));
    }
    // off in the profile: one line each for what Kiln had skipped (info) and for what runs all the same (a warning)
    for m in entries.iter().filter(|m| is_skipped(m)) {
        out.push((3, format!("kiln-eml: {}", m.reason.as_deref().unwrap_or(""))));
    }
    for m in entries.iter().filter(|m| runs_off(m)) {
        out.push((2, format!("kiln-eml: profile EML mod {} {}", m.id.as_deref().unwrap_or("?"), m.reason.as_deref().unwrap_or(""))));
    }
    let total = runs + lost + off + other;
    if total > 0 {
        let mut line = format!("kiln-eml: {runs} of the profile's {total} EML mod(s) run under {who}");
        if anyway > 0 { line.push_str(&format!("; {anyway} run although the profile has {} off", if anyway == 1 { "it" } else { "them" })); }
        if off > 0 { line.push_str(&format!("; {off} are switched off in ShroudForge, so nothing applies them")); }
        if other > 0 { line.push_str(&format!("; {other} run on the other side only")); }
        if lost > 0 { line.push_str(&format!("; {lost} do not load")); }
        if lost + off + anyway > 0 { line.push_str(" (kiln/state/eml.json)"); }
        out.push((if lost + off + anyway > 0 { 2 } else { 3 }, line));
    }
    out
}

/// The entries of a drop-in folder, sorted by name: folders and `.zip` files, without hidden ones (EML's rule).
pub fn folder_candidates(dir: &Utf8Path, source: &str) -> Vec<Candidate> {
    // A drop-in authorizes this directory, not each enumerated child as a new independent root.
    if !checked_metadata(dir).is_ok_and(|m| m.is_dir()) { return Vec::new(); }
    let mut out: Vec<Candidate> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| Utf8PathBuf::from_path_buf(e.path()).ok())
        .filter(|p| !p.file_name().unwrap_or_default().starts_with('.'))
        .filter(|p| p.is_dir() || p.extension().is_some_and(|x| x.eq_ignore_ascii_case("zip")))
        .map(|path| Candidate { path, source: source.into(), enabled: true })
        .collect();
    out.sort_by(|a, b| a.path.cmp(&b.path));
    out
}

/// Profile paths use the core's base (<game>/kiln), not the working folder or the profile file's parent.
/// A profile explicitly authorizes the resulting mod root, so normalize its spelling before checking that root.
pub fn resolve_profile_path(path: &str, kiln_dir: &Utf8Path) -> Utf8PathBuf {
    let path = Utf8PathBuf::from(path.replace('\\', "/"));
    if path.as_str().is_empty() || path.as_str().contains('\0') { return path; }
    let bytes = path.as_str().as_bytes();
    if bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && bytes.get(2) != Some(&b'/') {
        return path; // drive-relative paths depend on another working directory; validation must refuse them
    }
    #[cfg(windows)]
    if path.has_root() && !path.is_absolute() { return path; }
    let full = if path.is_absolute() { path } else { kiln_dir.join(path) };
    let full = std::path::absolute(&full).ok()
        .and_then(|p| Utf8PathBuf::from_path_buf(p).ok()).unwrap_or(full);
    let mut normalized = Utf8PathBuf::new();
    for part in full.components() {
        match part {
            camino::Utf8Component::CurDir => {},
            camino::Utf8Component::ParentDir => { normalized.pop(); },
            _ => normalized.push(part.as_str()),
        }
    }
    normalized
}

fn component_eq(a: &str, b: &str) -> bool {
    #[cfg(windows)] {
        // Match Windows' ordinal path comparison, including non-ASCII case pairs and either separator spelling.
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CompareStringOrdinal(a: *const u16, na: i32, b: *const u16, nb: i32, ignore_case: i32) -> i32;
        }
        let a: Vec<u16> = a.replace('\\', "/").encode_utf16().collect();
        let b: Vec<u16> = b.replace('\\', "/").encode_utf16().collect();
        let (Ok(na), Ok(nb)) = (i32::try_from(a.len()), i32::try_from(b.len())) else { return false; };
        unsafe { CompareStringOrdinal(a.as_ptr(), na, b.as_ptr(), nb, 1) == 2 }
    }
    #[cfg(not(windows))] { a == b }
}

/// Component comparison prevents a sibling named "mods-other" from matching the root "mods".
pub(crate) fn relative_under(path: &Utf8Path, root: &Utf8Path) -> Option<Utf8PathBuf> {
    let mut parts = path.components();
    for parent in root.components() {
        if !component_eq(parent.as_str(), parts.next()?.as_str()) { return None; }
    }
    let mut relative = Utf8PathBuf::new();
    for part in parts { relative.push(part.as_str()); }
    Some(relative)
}

fn checked_metadata(path: &Utf8Path) -> Result<std::fs::Metadata, String> {
    let m = std::fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if m.file_type().is_symlink() { return Err("symlinks are not allowed at or inside a mod root".into()); }
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        #[link(name = "kernel32")]
        unsafe extern "system" { fn GetFileAttributesW(path: *const u16) -> u32; }
        let name: Vec<u16> = path.as_str().encode_utf16().chain(Some(0)).collect();
        // Wine follows Unix symlinks despite OPEN_REPARSE_POINT. Query by name too: this exposes directory links.
        // Wine file links are invisible to Win32 queries; their exclusion is the installer's responsibility.
        let attributes = unsafe { GetFileAttributesW(name.as_ptr()) };
        if attributes == u32::MAX { return Err(std::io::Error::last_os_error().to_string()); }
        if (attributes | m.file_attributes()) & 0x400 != 0 { return Err("reparse points are not allowed at or inside a mod root".into()); }
    }
    if !m.is_dir() && !m.is_file() { return Err("special files are not allowed in mods".into()); }
    Ok(m)
}

fn validate_under(path: &Utf8Path, root: &Utf8Path, relative: &Utf8Path) -> Result<(), String> {
    if relative.components().any(|p| !matches!(p, camino::Utf8Component::Normal(_)) || p.as_str().contains(':')) {
        return Err("parent traversal is not allowed inside a mod root".into());
    }
    // Start at the authorized root. Its ancestors may include Steam library and Wine drive links.
    // The root itself, every component down to the candidate, and all candidate descendants must be regular.
    let mut current = root.to_owned();
    let mut m = checked_metadata(&current)?;
    for part in relative.components() {
        if !m.is_dir() { return Err("mod path descends through a file".into()); }
        current.push(part.as_str());
        m = checked_metadata(&current)?;
    }
    let resolved_root = root.canonicalize_utf8().map_err(|e| e.to_string())?;
    let mut todo = vec![path.to_owned()];
    let mut count = 0;
    while let Some(p) = todo.pop() {
        count += 1;
        if count > 50000 { return Err("mod tree exceeds 50000 entries".into()); }
        let m = checked_metadata(&p)?;
        let resolved = p.canonicalize_utf8().map_err(|e| e.to_string())?;
        if relative_under(&resolved, &resolved_root).is_none() {
            return Err("resolved mod path is outside the authorized root".into());
        }
        if m.is_dir() {
            for e in std::fs::read_dir(&p).map_err(|e| e.to_string())? {
                todo.push(Utf8PathBuf::from_path_buf(e.map_err(|e| e.to_string())?.path()).map_err(|_| "non UTF-8 mod path")?);
            }
        }
    }
    Ok(())
}

/// Reject traversal, links/reparse points and special files from an authorized root down, including the root.
/// Ancestors above that boundary belong to the player's layout and are not inspected. Final resolved paths must
/// remain contained. Wine hides Unix file links; the installer must exclude those (engines/README.md path policy).
pub fn validate_path(path: &Utf8Path, roots: &[Utf8PathBuf]) -> Result<(), String> {
    if !path.is_absolute() || path.as_str().contains('\0') || path.components().any(|p|
        matches!(p, camino::Utf8Component::Normal(s) if s.contains(':'))) {
        return Err("mod path must be absolute without NUL".into());
    }
    let mut error = "mod path is outside the authorized roots".to_owned();
    for root in roots {
        if !root.is_absolute() || root.as_str().contains('\0')
            || root.components().any(|p| matches!(p, camino::Utf8Component::ParentDir)) { continue; }
        let Some(relative) = relative_under(path, root) else { continue; };
        match validate_under(path, root, &relative) {
            Ok(()) => return Ok(()),
            Err(why) => error = why,
        }
    }
    Err(error)
}

fn manifest(text: &str, flame: bool, extended: Option<&str>) -> Result<ModManifest, String> {
    if flame { return Err("a Flame mod (flame.json)".into()); }
    let raw: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("mod.json: {e}"))?;
    if ["dll", "emf", "emfMin", "emf_api", "main"].iter().any(|k| raw.get(*k).is_some()) {
        return Err("an EMF mod (mod.json names a dll/main or an EMF API version)".into());
    }
    if let Some(text) = extended {
        let v: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("extended.mod.json: {e}"))?;
        if v.get("launcher").and_then(|v| v.as_str()) != Some("EML") {
            return Err("a ShroudForge package (extended.mod.json): ShroudForge's runtime runs it".into());
        }
    }
    serde_json::from_value(raw).map_err(|e| format!("not an EML manifest: {e}"))
}

/// Why a Kiln mod (a `kiln.mod.json`, no `mod.json`) dropped into EML's folders is skipped. Logged, unlike a plain
/// "no mod.json" folder: players unzip Kiln mods there by mistake (inbox 81).
const KILN_MOD: &str = "a Kiln mod (kiln.mod.json), not an EML mod: Kiln runs it from kiln/mods. Add its zip in the Kiln \
    Launcher (Library, Add mods; or, on a server, unzip it into kiln/mods/<mod>/) and remove it from this folder";
const KILN_MOD_LOOSE: &str = "part of a Kiln mod unzipped loose into EML's mods folder (a kiln.mod.json lies next to \
    it): remove the loose files there and add the mod's zip in the Kiln Launcher (Library, Add mods; on a server, unzip it into \
    kiln/mods/<mod>/)";

/// The manifest of an EML mod, or why the folder/zip is not one EML runs.
pub fn classify(path: &Utf8Path) -> Result<ModManifest, String> {
    if path.is_dir() {
        let has = |f: &str| path.join(f).is_file();
        if !has("mod.json") {
            if has("kiln.mod.json") {
                return Err(KILN_MOD.into());
            }
            // a Kiln mod's zip unpacked loose into EML's mods folder: its subfolders (sigdb/ ...) land here as entries
            if path.parent().is_some_and(|p| p.join("kiln.mod.json").is_file()) {
                return Err(KILN_MOD_LOOSE.into());
            }
            return Err("no mod.json (not an EML mod)".into());
        }
        fn text(path: &Utf8Path) -> Result<String, String> {
            let mut s = String::new();
            std::fs::File::open(path).map_err(|e| e.to_string())?.take(1024 * 1024 + 1)
                .read_to_string(&mut s).map_err(|e| e.to_string())?;
            if s.len() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            Ok(s)
        }
        let manifest_text = text(&path.join("mod.json"))?;
        let extended = if has("extended.mod.json") {
            Some(text(&path.join("extended.mod.json"))?)
        } else { None };
        let manifest = manifest(&manifest_text, has("flame.json"), extended.as_deref())?;
        if !has("src/mod.lua") {
            return Err("no src/mod.lua".into());
        }
        Ok(manifest)
    } else if path.is_file() {
        let file = std::fs::File::open(path).map_err(|e| format!("{e}"))?;
        let mut zip = zip::ZipArchive::new(file).map_err(|e| format!("not a readable zip: {e}"))?;
        if zip.len() > 50000 { return Err("zip exceeds 50000 entries".into()); }
        let mut total = 0u64;
        for i in 0..zip.len() {
            let f = zip.by_index(i).map_err(|e| e.to_string())?;
            let name = f.name();
            if name.contains('\\') || name.contains(':') || std::path::Path::new(name).components().any(|c| !matches!(c, Component::Normal(_)))
                || f.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000) {
                return Err("unsafe zip entry".into());
            }
            total = total.checked_add(f.size()).ok_or("zip size overflow")?;
            if total > 512 * 1024 * 1024 { return Err("zip exceeds 512 MiB uncompressed".into()); }
        }
        fn text(zip: &mut zip::ZipArchive<std::fs::File>, name: &str) -> Result<String, String> {
            let mut f = zip.by_name(name).map_err(|e| e.to_string())?;
            if f.size() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            let mut text = String::new();
            f.by_ref().take(1024 * 1024 + 1).read_to_string(&mut text).map_err(|e| e.to_string())?;
            if text.len() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            Ok(text)
        }
        if zip.by_name("mod.json").is_err()
            && zip.file_names().any(|n| n == "kiln.mod.json" || n.strip_suffix("/kiln.mod.json").is_some_and(|d| !d.contains('/'))) {
            return Err(KILN_MOD.into());
        }
        if zip.by_name("mod.json").is_err() {
            return Err("no mod.json in the zip (not an EML mod)".into());
        }
        let t = text(&mut zip, "mod.json")?;
        let flame = zip.by_name("flame.json").is_ok();
        let extended = if zip.by_name("extended.mod.json").is_ok() { Some(text(&mut zip, "extended.mod.json")?) } else { None };
        let manifest = manifest(&t, flame, extended.as_deref())?;
        if zip.by_name("src/mod.lua").is_err() {
            return Err("no src/mod.lua in the zip".into());
        }
        Ok(manifest)
    } else {
        Err("missing".into())
    }
}

fn capability_names(m: &ModManifest) -> Vec<String> {
    m.capabilities
        .iter()
        .map(|c| serde_json::to_value(c).ok().and_then(|v| v.as_str().map(str::to_owned)).unwrap_or_default())
        .collect()
}

/// Candidates in order -> the mod paths to run and one entry per candidate. The first mod with an id wins; later ones
/// are `duplicate`. Entries of folders that are not EML mods at all ("no mod.json") are kept, state `skipped`.
pub fn resolve(candidates: &[Candidate]) -> (Vec<Utf8PathBuf>, Vec<Entry>) {
    let mut run = Vec::new();
    let mut entries: Vec<Entry> = Vec::new();
    for c in candidates {
        let mut e = Entry {
            id: None,
            version: None,
            name: None,
            source: c.source.clone(),
            path: c.path.to_string(),
            state: "pending".into(),
            reason: None,
            capabilities: Vec::new(),
        };
        match validate_path(&c.path, &[c.path.clone()]).and_then(|_| classify(&c.path)) {
            Ok(m) => {
                e.id = Some(m.id.clone());
                e.version = Some(m.version.to_string());
                e.name = Some(m.name.clone());
                e.capabilities = capability_names(&m);
                if !c.enabled {
                    e.state = "disabled".into();
                    e.reason = Some("switched off in the profile".into());
                } else if let Some(first) = entries.iter().find(|o| o.id.as_deref() == Some(&m.id) && o.state == "pending") {
                    e.state = "duplicate".into();
                    e.reason = Some(format!("id {} is already loaded from {}", m.id, first.path));
                } else {
                    run.push(c.path.clone());
                }
            }
            Err(why) => {
                e.state = "skipped".into();
                e.reason = Some(why);
            }
        }
        entries.push(e);
    }
    (run, entries)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// ShroudForge's `paths.mods` on Windows after its first start (`\\?\C:\…\mods`, also with `/`), and the
    /// folder of the launcher's game-folder copy (`C:\…\mods`): one folder.
    #[test]
    fn windows_folders_compare_without_the_verbatim_prefix_or_case() {
        let mods = r"C:\Program Files (x86)\Steam\steamapps\common\Enshrouded\mods";
        assert!(windows_same_folder(&format!(r"\\?\{mods}").replace('\\', "/"), mods), "the verbatim prefix");
        assert!(windows_same_folder(&format!(r"\\?\{mods}"), &mods.replace('\\', "/")));
        assert!(windows_same_folder(&mods.to_uppercase(), &format!("{mods}\\")), "case, a trailing separator");
        assert!(windows_same_folder(r"D:\Spiele\Ä", r"d:\spiele\ä\."), "non-ASCII case");
        //
        assert!(windows_same_folder("D:\\Spiele \u{1FB3}\\mods", "d:\\SPIELE \u{1FBC}\\MODS"), "a subscript iota's two cases");
        assert!(windows_same_folder(r"\\?\UNC\nas\share\mods", r"\\NAS\share\mods"), "a network folder");
        assert!(!windows_same_folder(mods, &format!("{mods}-other")), "a sibling");
        assert!(!windows_same_folder(r"\\?\Volume{1}\mods", r"Volume{1}\mods"), "a volume path has no other spelling");
        assert!(!windows_same_folder(r"\mods", "mods"), "rooted is not relative");
    }

    /// What a player reads for ShroudForge's folder: Windows' own spelling, one separator, no verbatim prefix where the
    /// plain path names the same folder.
    #[test]
    fn windows_folders_are_named_without_the_verbatim_prefix() {
        let mods = r"C:\Program Files (x86)\Steam\steamapps\common\Enshrouded\mods";
        assert_eq!(windows_plain_path(&format!(r"\\?\{mods}")), mods);
        assert_eq!(windows_plain_path(&format!(r"\\?\{mods}").replace('\\', "/")), mods);
        assert_eq!(windows_plain_path(&mods.replace('\\', "/")), mods);
        assert_eq!(windows_plain_path(r"\\?\UNC\nas\share\mods"), r"\\nas\share\mods");
        assert_eq!(windows_plain_path("mods/sub"), r"mods\sub", "relative: the game folder's");
        // only the verbatim namespace has these: they keep it
        assert_eq!(windows_plain_path(r"\\?\C:\Game.\mods"), r"\\?\C:\Game.\mods");
        assert_eq!(windows_plain_path(r"\\?\Volume{1}\mods"), r"\\?\Volume{1}\mods");
    }

    #[test]
    fn windows_verbatim_literal_names_are_not_plain_names() {
        assert!(!windows_same_folder(r"\\?\C:\Game.\mods", r"C:\Game.\mods"));
        assert!(!windows_same_folder(r"\\?\UNC\nas\share\Game \mods", r"\\nas\share\Game \mods"));
        assert!(!windows_same_folder("C:", r"C:\"));
    }

    pub(super) fn tmp(name: &str) -> Utf8PathBuf {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-test-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    /// `dir` is `/`-separated; the folder is joined part by part, so its text is the one the product reads back from the
    /// folder (`…\mods\bare` on Windows, never `…\mods/bare`), and a test can compare the paths in reasons as text.
    pub(super) fn eml_mod(root: &Utf8Path, dir: &str, id: &str) -> Utf8PathBuf {
        let p = dir.split('/').fold(root.to_owned(), |p, part| p.join(part));
        std::fs::create_dir_all(p.join("src")).unwrap();
        std::fs::write(p.join("mod.json"), format!(r#"{{"id":"{id}","name":"N","version":"1.0.0","capabilities":["patch"]}}"#)).unwrap();
        std::fs::write(p.join("src/mod.lua"), "-- x").unwrap();
        p
    }

    #[test]
    fn classifies_eml_and_others() {
        let d = tmp("classify");
        let ok = eml_mod(&d, "a", "a");
        assert_eq!(classify(&ok).unwrap().id, "a");
        let emf = d.join("emf");
        std::fs::create_dir_all(&emf).unwrap();
        std::fs::write(emf.join("mod.json"), r#"{"id":"x","name":"x","version":"1.0.0","dll":"x.dll","emf":21}"#).unwrap();
        assert!(classify(&emf).unwrap_err().contains("EMF"));
        let sf = eml_mod(&d, "sf", "sf");
        std::fs::write(sf.join("extended.mod.json"), r#"{"launcher":"SF"}"#).unwrap();
        assert!(classify(&sf).unwrap_err().contains("ShroudForge"));
        let legacy = eml_mod(&d, "legacy", "legacy");
        std::fs::write(legacy.join("extended.mod.json"), r#"{"launcher":"EML"}"#).unwrap();
        assert!(classify(&legacy).is_ok());
        let bad_cap = eml_mod(&d, "cap", "cap");
        std::fs::write(bad_cap.join("mod.json"), r#"{"id":"cap","name":"c","version":"1.0.0","capabilities":["services"]}"#).unwrap();
        assert!(classify(&bad_cap).unwrap_err().contains("not an EML manifest"));
        let nolua = d.join("nolua");
        std::fs::create_dir_all(&nolua).unwrap();
        std::fs::write(nolua.join("mod.json"), r#"{"id":"n","name":"n","version":"1.0.0"}"#).unwrap();
        assert_eq!(classify(&nolua).unwrap_err(), "no src/mod.lua");
        let empty = d.join("empty");
        std::fs::create_dir_all(&empty).unwrap();
        assert!(classify(&empty).unwrap_err().starts_with("no mod.json"));
        // a Kiln mod in its own folder, and one unzipped loose (its sigdb/ becomes an entry of the mods folder)
        let kiln = d.join("kiln-mod");
        std::fs::create_dir_all(kiln.join("sigdb")).unwrap();
        std::fs::write(kiln.join("kiln.mod.json"), r#"{"id":"k","name":"K","version":"1.0.0"}"#).unwrap();
        assert_eq!(classify(&kiln).unwrap_err(), KILN_MOD);
        assert_eq!(classify(&kiln.join("sigdb")).unwrap_err(), KILN_MOD_LOOSE);
        assert!(!classify(&kiln).unwrap_err().starts_with("no mod.json"), "logged, not quiet");
        // a Kiln mod's zip, flat or with one top folder (the SDK's KilnPack layout)
        for (name, entry) in [("flat.zip", "kiln.mod.json"), ("nested.zip", "more-flame-altars/kiln.mod.json")] {
            let z = d.join(name);
            let mut w = zip::ZipWriter::new(std::fs::File::create(&z).unwrap());
            w.start_file(entry, zip::write::SimpleFileOptions::default()).unwrap();
            std::io::Write::write_all(&mut w, br#"{"id":"k"}"#).unwrap();
            w.finish().unwrap();
            assert_eq!(classify(&z).unwrap_err(), KILN_MOD, "{name}");
        }
        // any other zip without mod.json says so, not the zip crate's "specified file not found in archive"
        let other = d.join("other.zip");
        let mut w = zip::ZipWriter::new(std::fs::File::create(&other).unwrap());
        w.start_file("readme.txt", zip::write::SimpleFileOptions::default()).unwrap();
        w.finish().unwrap();
        assert_eq!(classify(&other).unwrap_err(), "no mod.json in the zip (not an EML mod)");
        let huge = eml_mod(&d, "huge", "huge");
        std::fs::write(huge.join("mod.json"), vec![b' '; 1024 * 1024 + 1]).unwrap();
        assert!(classify(&huge).unwrap_err().contains("exceeds 1 MiB"));
    }

    #[test]
    fn resolve_orders_dedupes_and_disables() {
        let d = tmp("resolve");
        let p1 = eml_mod(&d, "one", "same");
        let p2 = eml_mod(&d, "two", "same");
        let p3 = eml_mod(&d, "three", "other");
        let c = |p: &Utf8PathBuf, enabled| Candidate { path: p.clone(), source: "t".into(), enabled };
        let (run, entries) = resolve(&[c(&p1, true), c(&p2, true), c(&p3, false)]);
        assert_eq!(run, vec![p1.clone()]);
        assert_eq!(entries[1].state, "duplicate");
        assert_eq!(entries[2].state, "disabled");
    }

    #[test]
    fn profile_lists_and_dropins() {
        let j = r#"{"format":1,"id":"p","generation":1,"dropins":["emf","eml"],
                    "mods":{"eml":[{"id":"a","path":"Z:\\lib\\eml\\a"},{"id":"b","path":"Z:\\b","enabled":false}]}}"#;
        let c = profile_candidates(j, Utf8Path::new("/game/kiln"));
        assert_eq!(c.len(), 2);
        assert!(!c[1].enabled);
        assert!(profile_dropins(j));
        assert!(!profile_dropins(r#"{"dropins":["emf"]}"#));
        assert!(profile_dropins(r#"{"dropins":true}"#));
        assert!(profile_candidates("not json", Utf8Path::new("/game/kiln")).is_empty());
    }

    #[test]
    fn launcher_copies_of_profile_mods_are_not_drop_ins() {
        let d = tmp("copies");
        let copy = eml_mod(&d, "mods/hoarders-helper", "HoardersHelper");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"hoarders-helper","version":"1.0.0","hash":"h"}"#).unwrap();
        let other = eml_mod(&d, "mods/other", "other");
        std::fs::write(other.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"other","version":"1.0.0","hash":"h"}"#).unwrap();
        let sf = eml_mod(&d, "mods/sf", "sf");
        std::fs::write(sf.join(".kiln-library.json"), r#"{"format":1,"kind":"shroudforge","id":"hoarders-helper","version":"1.0.0","hash":"h"}"#).unwrap();
        let j = r#"{"mods":{"eml":[{"id":"hoarders-helper","path":"Z:\\lib\\eml\\hoarders-helper\\1.0.0+h"}]},"dropins":true}"#;
        let ids = profile_ids(j);
        assert_eq!(ids, [("hoarders-helper".to_string(), true)]);
        let (kept, skipped) = without_profile_copies(folder_candidates(&d.join("mods"), "game-mods"), &ids, &d.join("kiln"));
        assert_eq!(skipped, [copy]);
        // a marked copy the profile does not list, or a copy of another kind, stays an ordinary drop-in
        assert_eq!(kept.iter().map(|c| c.path.file_name().unwrap()).collect::<Vec<_>>(), ["other", "sf"]);
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn removed_receipt_owned_copies_never_become_dropins_but_shipped_markers_are_not_ownership() {
        let d = tmp("removed-owned-copy");
        let copy = eml_mod(&d, "mods/XHL-Edited", "XHL-Edited");
        let manual = eml_mod(&d, "mods/manual", "manual");
        let forged = eml_mod(&d, "mods/shipped-marker", "shipped-marker");
        let marker = serde_json::json!({"format":1,"kind":"eml","id":"xhl-edited","version":"1.0.0","hash":"hash"});
        std::fs::write(copy.join(".kiln-library.json"), marker.to_string()).unwrap();
        std::fs::write(forged.join(".kiln-library.json"), marker.to_string()).unwrap();
        std::fs::create_dir_all(d.join("kiln/state")).unwrap();
        let receipts = serde_json::json!([{"path":copy.as_str(),"marker":marker,"files":{},"game_folder":true}]);
        std::fs::write(d.join("kiln/state/library-placements.json"), receipts.to_string()).unwrap();
        // The profile has removed this mod; shipped Lua was edited, so placement correctly retained the folder.
        std::fs::write(copy.join("src/mod.lua"), "-- edited code\nreturn {}").unwrap();
        let json = r#"{"mods":{"eml":[]},"dropins":true}"#;
        let (kept, skipped) = without_profile_copies(folder_candidates(&d.join("mods"), "game-mods"), &profile_ids(json), &d.join("kiln"));
        assert_eq!(skipped, [copy]);
        assert_eq!(kept.iter().map(|c| c.path.clone()).collect::<Vec<_>>(), [manual, forged]);
        assert!(placement_path_matches("/game/mods/edited", Utf8Path::new("Z:/game/mods/edited")));
        assert!(!placement_path_matches("/game/mods/edited", Utf8Path::new("Z:/other/mods/edited")));
        assert!(!placement_path_matches("/game/mods/edited", Utf8Path::new("C:/game/mods/edited")));
        std::fs::remove_dir_all(d).unwrap();
    }

    // Proton maps the Steam library to S: (its prefix's dosdevices/s:), so a client runs from S:\steamapps\...
    // while the Linux launcher's receipt names the Unix path: a retained copy must still be recognised.
    #[test]
    fn a_receipt_path_matches_through_the_drive_wine_names_it_with() {
        let steam = |u: &str| u.strip_prefix("/home/u/.local/share/Steam/").map(|rest| format!("S:/{rest}"));
        let recorded = "/home/u/.local/share/Steam/steamapps/common/Enshrouded/mods/XHL-Edited";
        assert!(placement_path_matches_with(recorded, Utf8Path::new("S:/steamapps/common/Enshrouded/mods/XHL-Edited"), steam));
        if cfg!(windows) {
            assert!(placement_path_matches_with(recorded, Utf8Path::new("s:/STEAMAPPS/common/Enshrouded/mods/xhl-edited"), steam),
                "Windows names are case-insensitive");
        }
        assert!(!placement_path_matches_with(recorded, Utf8Path::new("S:/steamapps/common/Enshrouded/mods/other"), steam));
        assert!(!placement_path_matches_with(recorded, Utf8Path::new("S:/steamapps/common/Enshrouded/mods/XHL-Edited"), |_| None),
            "the Z: rule alone does not see it (the defect)");
        assert!(placement_path_matches_with(recorded, Utf8Path::new("Z:/home/u/.local/share/Steam/steamapps/common/Enshrouded/mods/XHL-Edited"), |_| None));
    }

    // The same under Wine with a real drive: the harness maps dosdevices/s: to $KILN_EML_S_DRIVE (a Unix folder).
    #[cfg(windows)]
    #[test]
    #[ignore = "needs KILN_EML_S_DRIVE: a Unix folder the Wine prefix maps as s: (Wine harness)"]
    fn wine_names_a_path_in_a_mapped_folder_with_that_drive() {
        let folder = std::env::var("KILN_EML_S_DRIVE").expect("run by the Wine harness, which maps s: and sets KILN_EML_S_DRIVE");
        let recorded = format!("{folder}/steamapps/common/Enshrouded/mods/XHL-Edited");
        let dos = dos_name(&recorded).expect("running under Wine");
        assert!(dos.to_ascii_uppercase().starts_with("S:\\STEAMAPPS"), "{dos}");
        assert!(placement_path_matches(&recorded, Utf8Path::new(r"S:\steamapps\common\Enshrouded\mods\XHL-Edited")));
        assert!(!placement_path_matches(&recorded, Utf8Path::new(r"S:\steamapps\common\Enshrouded\mods\other")));
    }

    #[test]
    fn aside_lists_the_profile_mods_truthfully() {
        let d = tmp("aside");
        let copy = eml_mod(&d, "mods/a", "A");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"a","version":"1.0.0","hash":"h"}"#).unwrap();
        let j = r#"{"mods":{"eml":[{"id":"a","path":"/lib/a"},{"id":"b","path":"/lib/b"},{"id":"c","path":"/lib/c","enabled":false}]}}"#;
        let e = aside_entries(j, &d.join("kiln"), &[d.join("mods")], "EML's own loader", None);
        let states: Vec<_> = e.iter().map(|x| (x.id.as_deref().unwrap(), x.state.as_str())).collect();
        assert_eq!(states, [("a", "own-loader"), ("b", "not-loaded"), ("c", "disabled")]);
        // the folder `copy` names, spelled with the system's separator (`mods\a` on Windows): compared as paths
        assert_eq!(Utf8Path::new(&e[0].path), copy);
        assert!(e[1].reason.as_deref().unwrap().starts_with("EML's own loader runs this start and reads only"));
        assert_eq!(e[1].path, "/lib/b");
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn shroudforge_refused_switch_shapes_never_claim_to_run() {
        let d = tmp("sf-invalid-switch");
        let p = eml_mod(&d, "mods/a", "a");
        for ext in [
            r#"{"enabled":true,"launcher":"EML"}"#,
            r#"{"schemaVersion":2,"enabled":true}"#,
            r#"{"schemaVersion":1,"enabled":true,"launcher":null}"#,
            r#"{"schemaVersion":1,"enabled":true,"launcher":"eml"}"#,
            r#"{"schemaVersion":1,"enabled":true,"targets":"server"}"#,
            r#"{"schemaVersion":1,"enabled":true,"targets":[]}"#,
            r#"{"schemaVersion":1,"enabled":true,"targets":["client",42]}"#,
            r#"{"schemaVersion":1,"enabled":true,"targets":["client","client"]}"#,
            r#"{"schemaVersion":1,"enabled":true,"settings":null}"#,
        ] {
            std::fs::write(p.join("extended.mod.json"), ext).unwrap();
            assert!(matches!(shroudforge_switch(&p, false), Err(Off::Refused(_))), "{ext}");
        }
        std::fs::remove_dir_all(d).unwrap();
    }

    /// ShroudForge runs a package of its mods folder only while its extended.mod.json says `enabled: true`, on the sides
    /// its `targets` name. A real client (2026-11-11) had four EML mods in ShroudForge's folder and one such file:
    /// kiln-eml said all four ran under ShroudForge, and three were applied by nobody.
    #[test]
    fn under_shroudforge_only_a_switched_on_package_for_this_side_runs() {
        let d = tmp("aside-sf");
        let copy = |dir: &str, ext: Option<&str>| {
            let p = eml_mod(&d, &format!("mods/{dir}"), dir);
            std::fs::write(p.join(".kiln-library.json"), format!(r#"{{"format":1,"kind":"eml","id":"{dir}","version":"1.0.0","hash":"h"}}"#)).unwrap();
            if let Some(ext) = ext { std::fs::write(p.join("extended.mod.json"), ext).unwrap(); }
            p
        };
        copy("on", Some(r#"{"schemaVersion":1,"enabled":true,"targets":["client","server"],"launcher":"EML","settings":{}}"#));
        copy("bare", None);
        copy("off", Some(r#"{"schemaVersion":1,"enabled":false,"launcher":"EML"}"#));
        copy("client", Some(r#"{"schemaVersion":1,"enabled":true,"targets":["client"],"launcher":"EML"}"#));
        copy("broken", Some(r#"{"schemaVersion":1,"enabled":tru"#));
        eml_mod(&d, "mods/Hand Made", "hand");  // the player's own folder: no marker, no extension
        let j = r#"{"mods":{"eml":[{"id":"on","path":"/lib/on"},{"id":"bare","path":"/lib/bare"},{"id":"off","path":"/lib/off"},
            {"id":"client","path":"/lib/client"},{"id":"broken","path":"/lib/broken"},{"id":"hand","path":"/lib/hand"},
            {"id":"lib","path":"/lib/lib"},{"id":"dis","path":"/lib/dis","enabled":false}]}}"#;
        let who = "ShroudForge (hosted by Kiln)";
        let states = |side: Option<bool>| aside_entries(j, &d.join("kiln"), &[d.join("mods")], who, side)
            .into_iter().map(|e| (e.id.unwrap(), e.state, e.reason.unwrap_or_default())).collect::<Vec<_>>();
        let client = states(Some(false));
        let state = |id: &str| client.iter().find(|x| x.0 == id).map(|x| (x.1.as_str(), x.2.as_str())).unwrap();
        assert_eq!(state("on").0, "own-loader");
        assert_eq!(state("client").0, "own-loader");
        for id in ["bare", "off", "broken", "hand"] {
            assert_eq!(state(id).0, "off-in-shroudforge", "{id}: {client:?}");
            assert!(!state(id).1.contains("runs under"), "{id}: {}", state(id).1);
        }
        assert!(state("bare").1.contains("no extended.mod.json") && state("bare").1.contains("Kiln launcher"), "{}", state("bare").1);
        assert!(state("off").1.contains("enabled: true"), "{}", state("off").1);
        // the player's own folder: the words promised to a player on Nexus (14 Nov 2026), nothing about ShroudForge's files
        assert!(state("hand").1.contains("You put this mod into ShroudForge's folder yourself, and ShroudForge has it switched off. Switch \
            it on in Kiln's in-game menu (Mods > ShroudForge), or remove the folder and press Play in the Kiln launcher: the mod is in your \
            profile, so Kiln then places its own copy, switched on."), "{}", state("hand").1);
        assert!(!state("hand").1.contains("extended.mod.json") && state("hand").1.contains("Hand Made"), "{}", state("hand").1);
        assert_eq!((state("lib").0, state("dis").0), ("not-loaded", "disabled"));
        // a dedicated server: the client-only package runs on the client only
        let server = states(Some(true));
        let on_server = |id: &str| server.iter().find(|x| x.0 == id).map(|x| x.1.clone()).unwrap();
        assert_eq!((on_server("on"), on_server("client"), on_server("bare")), ("own-loader".into(), "other-side".into(), "off-in-shroudforge".into()));
        let hand_on_server = server.iter().find(|x| x.0 == "hand").map(|x| x.2.clone()).unwrap();
        assert!(hand_on_server.contains("You put this mod into ShroudForge's folder yourself, and ShroudForge has it switched off. A server \
            has no in-game menu: remove the folder and activate the profile again, and Kiln places its own copy, switched on."), "{hand_on_server}");
        // EML's own loader has no switch: it runs every package of its folder
        let eml = aside_entries(j, &d.join("kiln"), &[d.join("mods")], "EML's own loader", None);
        assert!(eml.iter().filter(|e| e.id.as_deref() != Some("lib") && e.id.as_deref() != Some("dis")).all(|e| e.state == "own-loader"), "{eml:?}");
        // and kiln-eml itself, in a start without ShroudForge, reads only the launcher of that file: `enabled: false`
        // never switches a mod off for it (the profile is its switch)
        let off = d.join("mods/off");
        let (run, entries) = resolve(&[Candidate { path: off.clone(), source: "profile".into(), enabled: true }]);
        assert_eq!((run, entries[0].state.as_str()), (vec![off], "pending"));
        std::fs::remove_dir_all(d).unwrap();
    }

    /// pr-manage P5: a package whose extended.mod.json says `enabled: true` but that ShroudForge refuses (its
    /// schemaVersion, a broken file) does not run under ShroudForge, and no advice may promise what cannot help: the
    /// launcher cannot switch on a file ShroudForge refuses.
    #[test]
    fn a_package_shroudforge_refuses_is_not_counted_as_running_and_gets_no_switch_advice() {
        let d = tmp("aside-sf-refused");
        let copy = |dir: &str, ext: &str| {
            let p = eml_mod(&d, &format!("mods/{dir}"), dir);
            std::fs::write(p.join(".kiln-library.json"), format!(r#"{{"format":1,"kind":"eml","id":"{dir}","version":"1.0.0","hash":"h"}}"#)).unwrap();
            std::fs::write(p.join("extended.mod.json"), ext).unwrap();
        };
        copy("schema2", r#"{"schemaVersion":2,"enabled":true,"launcher":"EML"}"#);
        copy("broken", r#"{"schemaVersion":1,"enabled":tru"#);
        let hand = eml_mod(&d, "mods/Hand Made", "hand");
        std::fs::write(hand.join("extended.mod.json"), r#"{"schemaVersion":2,"enabled":true,"launcher":"EML"}"#).unwrap();
        let j = r#"{"mods":{"eml":[{"id":"schema2","path":"/lib/schema2"},{"id":"broken","path":"/lib/broken"},{"id":"hand","path":"/lib/hand"}]}}"#;
        let who = "ShroudForge (hosted by Kiln)";
        let entries = aside_entries(j, &d.join("kiln"), &[d.join("mods")], who, Some(false));
        for e in &entries {
            let reason = e.reason.as_deref().unwrap_or_default();
            assert_eq!(e.state, "off-in-shroudforge", "{e:?}");
            assert!(reason.contains("refuses") && !reason.contains("runs under"), "{e:?}");
            assert!(!reason.contains("Kiln switches it on") && !reason.contains("switch it on in"), "{e:?}");
        }
        let summary = aside_log(&entries, who).last().unwrap().1.clone();
        assert!(summary.starts_with("kiln-eml: 0 of the profile's 3 EML mod(s) run under"), "{summary}");
        std::fs::remove_dir_all(d).unwrap();
    }

    /// A dedicated server without a profile (a server pack in Docker, 2026-11-21): ShroudForge is hosted, kiln-eml stands
    /// aside, and the EML packages of ShroudForge's folder have no switched-on extended.mod.json. Each one ShroudForge has
    /// off or refuses is listed with what a server needs; what it runs, or runs on the other side only, is not; neither are
    /// ShroudForge's own packages, Kiln-managed copies or folders the profile's rows name. kiln/eml/mods: not loaded.
    #[test]
    fn aside_lists_the_dropins_shroudforge_has_off() {
        let d = tmp("aside-dropins");
        let kiln = d.join("kiln");
        let bare = eml_mod(&d, "mods/bare", "bare");
        let on = eml_mod(&d, "mods/on", "on");
        std::fs::write(on.join("extended.mod.json"), SERVER_SWITCH_FILE).unwrap();
        let client = eml_mod(&d, "mods/client", "client");
        std::fs::write(client.join("extended.mod.json"), r#"{"schemaVersion":1,"launcher":"EML","enabled":true,"targets":["client"]}"#).unwrap();
        let off = eml_mod(&d, "mods/off", "off");
        std::fs::write(off.join("extended.mod.json"), r#"{"schemaVersion":1,"launcher":"EML","enabled":false}"#).unwrap();
        let refused = eml_mod(&d, "mods/schema2", "schema2");
        std::fs::write(refused.join("extended.mod.json"), r#"{"schemaVersion":2,"launcher":"EML","enabled":true}"#).unwrap();
        let sf = eml_mod(&d, "mods/sf", "sf");
        std::fs::write(sf.join("extended.mod.json"), r#"{"schemaVersion":1,"launcher":"SF","enabled":false}"#).unwrap();
        let copy = eml_mod(&d, "mods/kept", "kept");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"kept","version":"1.0.0","hash":"h"}"#).unwrap();
        let named = eml_mod(&d, "mods/named", "named");
        std::fs::create_dir_all(d.join("mods/not-a-mod")).unwrap();
        eml_mod(&d, "kiln/eml/mods/drop", "drop");
        let listed = [Entry { id: Some("named".into()), version: None, name: None, source: "profile".into(), path: named.to_string(),
            state: "off-in-shroudforge".into(), reason: None, capabilities: Vec::new() }];
        let who = "ShroudForge (hosted by Kiln)";
        let rows = aside_dropins(&d.join("mods"), &kiln, &[("kept".into(), true)], &listed, who, true);
        let got: Vec<_> = rows.iter().map(|e| (e.id.as_deref().unwrap(), e.state.as_str(), e.source.as_str())).collect();
        assert_eq!(got, [("bare", "off-in-shroudforge", "game-mods"), ("off", "off-in-shroudforge", "game-mods"),
            ("schema2", "off-in-shroudforge", "game-mods"), ("drop", "not-loaded", "kiln-dropin")], "{rows:?}");
        let reason = |id: &str| rows.iter().find(|e| e.id.as_deref() == Some(id)).unwrap().reason.clone().unwrap();
        assert!(reason("bare").contains("no extended.mod.json") && reason("bare").contains("A server has no in-game menu")
            && reason("bare").contains(SERVER_SWITCH_FILE) && reason("bare").contains(bare.as_str()), "{}", reason("bare"));
        assert!(reason("schema2").contains("refuses") && !reason("schema2").contains("in-game menu"), "{}", reason("schema2"));
        assert!(reason("drop").contains("kiln-eml stands aside") && reason("drop").contains(SERVER_SWITCH_FILE), "{}", reason("drop"));
        assert_eq!(rows[0].version.as_deref(), Some("1.0.0"));
        // the file the docs tell a server to write is one ShroudForge runs, on the server too
        assert!(shroudforge_switch(&on, true).is_ok() && shroudforge_switch(&on, false).is_ok());
        let line = aside_dropins_log(&rows, &d.join("mods"), true).unwrap();
        assert!(line.starts_with(&format!("kiln-eml: 3 EML package(s) in {} are switched off in ShroudForge or refused by it, so \
            nothing runs them (bare, off, schema2); a server needs an extended.mod.json that says \"enabled\": true in each", d.join("mods"))), "{line}");
        assert!(line.contains("1 EML mod(s) in kiln/eml/mods do not load") && line.ends_with("(kiln/state/eml.json)"), "{line}");
        // a client: the in-game menu's words, the same rows
        let rows = aside_dropins(&d.join("mods"), &kiln, &[("kept".into(), true)], &listed, who, false);
        assert_eq!(rows.len(), 4, "{rows:?}");
        assert!(reason("bare").contains("A server") && rows[0].reason.as_deref().unwrap().contains("Kiln's in-game menu (Mods > ShroudForge)"));
        assert!(aside_dropins_log(&rows, &d.join("mods"), false).unwrap().contains("switch them on in Kiln's in-game menu"));
        // nothing off: nothing to say
        assert!(aside_dropins_log(&[], &d.join("mods"), true).is_none());
        std::fs::remove_dir_all(d).unwrap();
    }

    /// sidelia's server (Nexus, 2026-11-21): ShroudForge hosted, its mods ran, and kiln.log said nothing about them. The
    /// aside start counts the EML packages of ShroudForge's folder and names the ones ShroudForge runs on this side.
    #[test]
    fn the_aside_start_names_the_eml_packages_shroudforge_runs() {
        let d = tmp("sf-runs");
        let root = d.join("mods");
        for (dir, ext) in [("both", Some(SERVER_SWITCH_FILE)), ("bare", None),
            ("client", Some(r#"{"schemaVersion":1,"launcher":"EML","enabled":true,"targets":["client"]}"#)),
            ("off", Some(r#"{"schemaVersion":1,"launcher":"EML","enabled":false}"#)),
            ("legacy", Some(r#"{"schemaVersion":1,"launcher":"EML","enabled":true}"#)),
            ("sf", Some(r#"{"schemaVersion":1,"launcher":"SF","enabled":true}"#))] {
            let p = eml_mod(&d, &format!("mods/{dir}"), dir);
            if let Some(ext) = ext { std::fs::write(p.join("extended.mod.json"), ext).unwrap(); }
        }
        std::fs::create_dir_all(root.join("not-a-mod")).unwrap();
        let (runs, total) = shroudforge_runs(&root, true, &[]);
        // a server: "legacy" (an EML package without targets) runs on both sides, "client" only on the client
        assert_eq!((runs.as_slice(), total), (["both".to_string(), "legacy".to_string()].as_slice(), 5), "server");
        let line = shroudforge_runs_log(&runs, total, &root, &[]).unwrap();
        assert_eq!(line, format!("kiln-eml: 2 of 5 EML packages in {root} run under ShroudForge: both, legacy"));
        let (runs, total) = shroudforge_runs(&root, false, &[]);
        assert_eq!((runs.as_slice(), total), (["both".to_string(), "client".to_string(), "legacy".to_string()].as_slice(), 5), "client");
        // none running: the count alone; one package: singular; an empty folder: nothing to say
        assert_eq!(shroudforge_runs_log(&[], 5, &root, &[]).unwrap(), format!("kiln-eml: 0 of 5 EML packages in {root} run under ShroudForge"));
        assert_eq!(shroudforge_runs_log(&["a".into()], 1, &root, &[]).unwrap(), format!("kiln-eml: 1 of 1 EML package in {root} runs under ShroudForge: a"));
        assert!(shroudforge_runs_log(&[], 0, &root, &[]).is_none());
        assert!(shroudforge_runs(&d.join("missing"), true, &[]) == (Vec::new(), 0));
        let many: Vec<String> = (0..45).map(|i| format!("m{i}")).collect();
        assert!(shroudforge_runs_log(&many, 45, &root, &[]).unwrap().ends_with("m39 and 5 more"));
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn the_aside_log_counts_only_what_runs() {
        let entry = |id: &str, state: &str| Entry { id: Some(id.into()), version: None, name: None, source: "profile".into(),
            path: format!("/mods/{id}"), state: state.into(), reason: Some(format!("why {id}")), capabilities: Vec::new() };
        let lines = aside_log(&[entry("a", "own-loader"), entry("b", "off-in-shroudforge"), entry("c", "off-in-shroudforge"),
            entry("d", "not-loaded"), entry("e", "other-side"), entry("f", "disabled")], "ShroudForge (hosted by Kiln)");
        let summary = &lines.last().unwrap().1;
        assert_eq!(lines.last().unwrap().0, 2, "a warning while a profile mod does not run");
        assert!(summary.starts_with("kiln-eml: 1 of the profile's 5 EML mod(s) run under ShroudForge (hosted by Kiln)"), "{summary}");
        assert!(summary.contains("2 are switched off in ShroudForge") && summary.contains("1 do not load") && summary.contains("1 run on the other side only"), "{summary}");
        assert!(lines.iter().any(|(l, t)| *l == 2 && t == "kiln-eml: profile EML mod b does not run: why b"), "{lines:?}");
        let all = aside_log(&[entry("a", "own-loader")], "ShroudForge (hosted by Kiln)");
        assert_eq!(all, [(3, "kiln-eml: 1 of the profile's 1 EML mod(s) run under ShroudForge (hosted by Kiln)".to_string())]);
    }

    #[test]
    fn native_folder_runs_once_with_kiln_or_is_reported_under_the_loaded_foreign_loader() {
        let d = tmp("native-folder");
        let copy = eml_mod(&d, "mods/XHL-Native-Mod", "XHL-Native-Mod");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"xhl-native-mod"}"#).unwrap();
        let json = serde_json::json!({"mods":{"eml":[{"id":"xhl-native-mod","path":copy.as_str()}]},"dropins":true}).to_string();
        let (dropins, skipped) = without_profile_copies(folder_candidates(&d.join("mods"), "game-mods"), &profile_ids(&json), &d.join("kiln"));
        assert_eq!(skipped, [copy.clone()]);
        let mut candidates = profile_candidates(&json, &d.join("kiln"));
        candidates.extend(dropins);
        let (run, entries) = resolve(&candidates);
        assert_eq!(run, [copy.clone()]);
        assert_eq!(entries.len(), 1);
        let aside = aside_entries(&json, &d.join("kiln"), &[d.join("mods")], "EML's own loader", None);
        // as paths (Windows spells it with `\`), and with the case the launcher placed it with (`XHL-Native-Mod`, not the id)
        assert_eq!((aside[0].state.as_str(), Utf8Path::new(&aside[0].path)), ("own-loader", copy.as_path()));
        // No marker: a real hand installation is still loaded by the foreign loader, not missing in the Library.
        std::fs::remove_file(copy.join(".kiln-library.json")).unwrap();
        let aside = aside_entries(&json, &d.join("kiln"), &[d.join("mods")], "EML's own loader", None);
        assert_eq!(aside[0].state, "own-loader");
        assert!(aside[0].reason.as_deref().unwrap().contains("hand-installed"));
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn folder_candidates_skip_hidden_and_files() {
        let d = tmp("folder");
        eml_mod(&d, "b", "b");
        eml_mod(&d, "a", "a");
        eml_mod(&d, ".hidden", "h");
        std::fs::write(d.join("readme.txt"), "x").unwrap();
        std::fs::write(d.join("z.zip"), "x").unwrap();
        let names: Vec<_> = folder_candidates(&d, "s").iter().map(|c| c.path.file_name().unwrap().to_string()).collect();
        assert_eq!(names, ["a", "b", "z.zip"]);
    }
    #[test]
    fn rejects_outside_roots_and_links() {
        let d = tmp("roots");
        let m = eml_mod(&d, "mods/a", "a");
        assert!(validate_path(&m, &[d.join("mods")]).is_ok());
        assert!(validate_path(&m, &[d.join("missing")]).is_err());
        assert!(validate_path(&d.join("mods/../mods/a"), &[d.clone()]).is_err());
        #[cfg(unix)] {
            std::os::unix::fs::symlink(d.join("outside"), m.join("link")).unwrap();
            assert!(validate_path(&m, &[d.clone()]).is_err());
        }
        std::fs::remove_dir_all(d).unwrap();
    }
    #[test]
    fn zip_classification_matches_folder_rules() {
        use std::io::Write;
        let d = tmp("zip");
        for (marker, body) in [("extended.mod.json", "{\"launcher\":\"SF\"}"), ("flame.json", "{}")] {
            let path = d.join("mod.zip");
            let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
            let opt = zip::write::SimpleFileOptions::default();
            for (name, text) in [("mod.json", "{\"id\":\"a\",\"name\":\"a\",\"version\":\"1.0.0\"}"), ("src/mod.lua", ""), (marker, body)] {
                z.start_file(name, opt).unwrap(); z.write_all(text.as_bytes()).unwrap();
            }
            z.finish().unwrap();
            assert!(classify(&path).is_err());
        }
        std::fs::remove_dir_all(d).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn validates_only_from_the_authorized_root_down() {
        let d = tmp("root-boundary");
        let real = d.join("real-library");
        let m = eml_mod(&real, "mods/a", "a");
        let layout = d.join("steam-library");
        std::os::unix::fs::symlink(&real, &layout).unwrap();
        let root = layout.join("mods");
        let candidate = root.join("a");
        assert!(validate_path(&candidate, &[root.clone()]).is_ok(),
            "a linked Steam/Wine layout above the authorized root must be accepted");
        let (mods, entries) = resolve(&[Candidate { path: candidate.clone(), source: "profile".into(), enabled: true }]);
        assert_eq!(mods, [candidate.clone()]);
        assert_eq!(entries[0].state, "pending");
        assert!(validate_path(&root.join("a/../a"), &[root.clone()]).is_err());
        let root_link = layout.join("linked-mods");
        std::os::unix::fs::symlink(real.join("mods"), &root_link).unwrap();
        assert!(validate_path(&root_link.join("a"), &[root_link]).is_err(), "the root itself cannot be a link");
        let internal = root.join("internal-link");
        std::os::unix::fs::symlink(&m, &internal).unwrap();
        assert!(validate_path(&internal, &[root.clone()]).is_err(), "even an in-root directory link is refused");
        assert!(validate_path(&internal.join("src"), &[root.clone()]).is_err(), "intermediate directory links are refused");
        std::fs::remove_file(&internal).unwrap();
        let outside = eml_mod(&d, "outside", "outside");
        std::os::unix::fs::symlink(&outside, &internal).unwrap();
        assert!(validate_path(&internal, &[root.clone()]).is_err(), "an escaping directory link is refused");
        std::fs::remove_file(&internal).unwrap();
        std::os::unix::fs::symlink(outside.join("mod.json"), m.join("linked-file")).unwrap();
        assert!(validate_path(&candidate, &[root]).is_err(), "native Unix exposes file links; Wine cannot detect this case");
        std::fs::remove_dir_all(d).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn dropins_refuse_a_linked_root_but_allow_a_linked_ancestor() {
        let d = tmp("dropin-root");
        let real = d.join("real");
        eml_mod(&real, "mods/a", "a");
        let layout = d.join("layout");
        std::os::unix::fs::symlink(&real, &layout).unwrap();
        assert_eq!(folder_candidates(&layout.join("mods"), "game-mods").len(), 1);
        let linked_root = d.join("mods");
        std::os::unix::fs::symlink(real.join("mods"), &linked_root).unwrap();
        assert!(resolve(&folder_candidates(&linked_root, "game-mods")).0.is_empty());
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn profile_paths_resolve_against_kiln_and_remain_authorized() {
        let d = tmp("relative-profile");
        let kiln = d.join("game/kiln");
        let m = eml_mod(&kiln, "eml/mods/relative", "relative");
        let outside = eml_mod(&d, "game/mods/sibling", "sibling");
        let json = r#"{"mods":{"eml":[{"path":"eml\\mods\\relative"},{"path":"../mods/sibling"}]}}"#;
        let candidates = profile_candidates(json, &kiln);
        assert_eq!(candidates[0].path, m);
        assert_eq!(candidates[1].path, outside);
        assert!(validate_path(&m, &[resolve_profile_path("eml/mods/relative", &kiln)]).is_ok());
        let (mods, _) = resolve(&candidates);
        assert_eq!(mods, [m.clone(), outside]);
        #[cfg(unix)] {
            let alias = d.join("proton-layout");
            std::os::unix::fs::symlink(d.join("game"), &alias).unwrap();
            let candidates = profile_candidates(json, &alias.join("kiln"));
            assert_eq!(resolve(&candidates).0.len(), 2);
        }
        assert_eq!(resolve_profile_path(m.as_str(), &kiln), m);
        assert!(!resolve_profile_path("C:mods/a", &kiln).is_absolute());
        assert!(!resolve_profile_path("", &kiln).is_absolute());
        let wrong_root = eml_mod(&d, "game/mods-other/third", "third");
        assert!(validate_path(&wrong_root, &[d.join("game/mods")]).is_err());
        #[cfg(unix)] {
            let stream = eml_mod(&d, "game/mods/a:stream", "stream");
            assert!(validate_path(&stream, &[d.join("game/mods")]).is_err());
            assert!(validate_path(&stream, &[stream.clone()]).is_err());
        }
        std::fs::remove_dir_all(d).unwrap();
    }

}

/// The launcher deliberately disabled these entries because their required engine is unavailable. `fallback`: the
/// reason for an entry the launcher delegated to ShroudForge without a note of its own (runner::runtime_skip_reason).
pub fn apply_runtime_blocks(json: &str, kiln: &Utf8Path, entries: &mut [Entry], fallback: &str) {
    apply_runtime_blocks_with(json, kiln, entries, |_| fallback.to_owned())
}

/// [`apply_runtime_blocks`] with the reason of a delegated entry made from that entry (as discovery left it) when
/// Kiln's interpreter has the start (kiln_mode.rs: the launch plan decides it per package).
pub fn apply_runtime_blocks_with(json: &str, kiln: &Utf8Path, entries: &mut [Entry], fallback: impl Fn(&Entry) -> String) {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json) else { return };
    let blocked = v.get("emlRuntimeBlocked").and_then(|v| v.as_object());
    for ((id, _), candidate) in profile_ids(json).into_iter().zip(profile_candidates(json, kiln)) {
        if candidate.enabled { continue; }
        let delegated = v.get("emlRuntime").and_then(|v| v.as_array()).is_some_and(|a| a.iter().any(|v| v.as_str() == Some(id.as_str())));
        let launcher = blocked.and_then(|b| b.get(&id)).and_then(|v| v.as_str());
        if launcher.is_none() && !delegated { continue; }
        for e in entries.iter_mut().filter(|e| e.source == "profile" && e.path == candidate.path.as_str()) {
            let reason = launcher.map_or_else(|| fallback(e), str::to_owned);
            e.id = Some(id.clone());
            e.state = "skipped".into();
            e.reason = Some(reason);
        }
    }
}

/// The profile's `emlPartial` (Library and manifest ids): EML mods of which only part runs under Kiln, because a
/// modified EML loader in the mod's package carries the rest (Holster Toggle's dbghelp.dll, whose hotkey Kiln does not
/// run). Each such entry whose EML part runs this start (`loaded`, `pending`, or `own-loader` under another loader) gets
/// the note in its reason; returns the kiln.log lines (level, text), one warning per mod.
pub fn apply_partial(json: &str, entries: &mut [Entry]) -> Vec<(i32, String)> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json.trim_start_matches('\u{feff}')) else { return Vec::new() };
    let Some(partial) = v.get("emlPartial").and_then(|v| v.as_object()) else { return Vec::new() };
    let mut lines = Vec::new();
    for e in entries.iter_mut().filter(|e| matches!(e.state.as_str(), "loaded" | "pending" | "own-loader")) {
        let Some(id) = e.id.clone() else { continue };
        let Some(note) = partial.get(&id).and_then(|n| n.as_str()).map(str::trim).filter(|n| !n.is_empty()) else { continue };
        let mut chars = note.chars();
        let sentence = chars.next().map(|c| c.to_uppercase().chain(chars).collect::<String>()).unwrap_or_default();
        if e.reason.as_deref().is_some_and(|r| r.contains(note) || r.contains(&sentence)) { continue; }
        e.reason = Some(match e.reason.take().filter(|r| !r.trim().is_empty()) {
            Some(r) => format!("{}. {sentence}", r.trim_end_matches('.')),
            None => sentence,
        });
        lines.push((2, format!("kiln-eml: EML mod {id}: only its Lua part runs: {note}")));
    }
    lines
}

#[cfg(test)]
mod partial_tests {
    use super::*;

    fn entry(id: &str, state: &str, reason: Option<&str>) -> Entry {
        Entry { id: Some(id.into()), version: None, name: None, source: "profile".into(), path: format!("mods/{id}"),
            state: state.into(), reason: reason.map(str::to_owned), capabilities: Vec::new() }
    }

    /// Player report 2026-11-17 (Holster Toggle under hosted ShroudForge): the mod's line says it runs under ShroudForge,
    /// and now also that its hotkey part, in the package's modified dbghelp.dll, does not run.
    #[test]
    fn a_mod_whose_package_carried_a_modified_loader_says_which_part_runs() {
        let note = "part of this mod is in dbghelp.dll, a modified EML loader with code of its own (a weapon holster hotkey)";
        let json = format!(r#"{{"mods":{{"eml":[]}},"emlPartial":{{"holster-toggle":"{note}"}}}}"#);
        let mut entries = vec![
            entry("holster-toggle", "own-loader", Some("runs under ShroudForge (hosted by Kiln): its ShroudForge switch is on")),
            entry("stacks", "own-loader", Some("runs under ShroudForge (hosted by Kiln)")),
        ];
        let lines = apply_partial(&json, &mut entries);
        assert_eq!(entries[0].reason.as_deref(), Some("runs under ShroudForge (hosted by Kiln): its ShroudForge switch is on. \
            Part of this mod is in dbghelp.dll, a modified EML loader with code of its own (a weapon holster hotkey)"));
        assert_eq!(entries[1].reason.as_deref(), Some("runs under ShroudForge (hosted by Kiln)"));
        assert_eq!(lines, vec![(2, format!("kiln-eml: EML mod holster-toggle: only its Lua part runs: {note}"))]);
        // applied once, also when called again
        assert!(apply_partial(&json, &mut entries).is_empty());
        // kiln-eml ran it itself (no reason yet): the note is the reason; off or failed mods say why they do not run instead
        let mut ran = vec![entry("holster-toggle", "loaded", None)];
        apply_partial(&json, &mut ran);
        assert!(ran[0].reason.as_deref().unwrap().starts_with("Part of this mod is in dbghelp.dll"));
        let mut off = vec![entry("holster-toggle", "disabled", Some("switched off in the profile"))];
        assert!(apply_partial(&json, &mut off).is_empty());
        assert_eq!(off[0].reason.as_deref(), Some("switched off in the profile"));
        // older launchers write no emlPartial
        assert!(apply_partial(r#"{"mods":{}}"#, &mut ran).is_empty());
    }
}

#[cfg(test)]
mod runtime_block_tests {
    use super::*;
    #[test]
    fn unavailable_runtime_reason_survives_a_missing_placement() {
        let json = r#"{"mods":{"eml":[{"id":"quiver","path":"missing","enabled":false}]},"emlRuntimeBlocked":{"quiver":"needs ShroudForge's runtime: component missing"}}"#;
        let kiln = Utf8Path::new("/nonexistent-kiln-runtime-test");
        let (mods, mut entries) = resolve(&profile_candidates(json, kiln));
        assert!(mods.is_empty());
        apply_runtime_blocks(json, kiln, &mut entries, "not hosted");
        assert_eq!(entries[0].id.as_deref(), Some("quiver"));
        assert_eq!(entries[0].state, "skipped");
        assert!(entries[0].reason.as_deref().unwrap().contains("component missing"));
    }
}

#[cfg(test)]
mod runtime_delegation_tests {
    use super::*;
    #[test]
    fn delegated_entry_is_off_for_old_runners_but_reported_under_shroudforge() {
        let root = Utf8PathBuf::from_path_buf(std::env::temp_dir().join(format!("eml-delegated-{}", std::process::id()))).unwrap();
        let dir = root.join("quiver");
        std::fs::create_dir_all(dir.join("src")).unwrap();
        std::fs::write(dir.join("mod.json"), r#"{"id":"quiver","name":"Q","version":"1.0.0","capabilities":["runtime"]}"#).unwrap();
        std::fs::write(dir.join("src/mod.lua"), "return {}").unwrap();
        std::fs::write(dir.join("extended.mod.json"), r#"{"schemaVersion":1,"launcher":"EML","enabled":true,"targets":["client"]}"#).unwrap();
        std::fs::write(dir.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"quiver","version":"1.0.0","hash":"h"}"#).unwrap();
        let json = serde_json::json!({"mods":{"eml":[{"id":"quiver","path":dir.as_str(),"enabled":false}]},"emlRuntime":["quiver"]}).to_string();
        let candidates = profile_candidates(&json, &root);
        assert!(!candidates[0].enabled);
        let (mods, mut fallback) = resolve(&candidates);
        assert!(mods.is_empty());
        apply_runtime_blocks(&json, &root, &mut fallback, &crate::runner::skip_reason(None, || "adapter failed before reservation".into()));
        assert_eq!(fallback[0].state, "skipped");
        assert!(fallback[0].reason.as_deref().unwrap().contains("adapter failed before reservation"));
        let rows = aside_entries(&json, &root, &[root.clone()], "ShroudForge (hosted by Kiln)", Some(false));
        assert_eq!(rows[0].state, "own-loader", "{rows:?}");
        let rows = aside_entries(&json, &root, &[root.clone()], "ShroudForge (hosted by Kiln)", Some(true));
        assert_eq!(rows[0].state, "other-side", "{rows:?}");
        std::fs::remove_dir_all(root).unwrap();
    }

}

// The profile's off for a player's own folder (0.2.0-dev.31.12)
#[cfg(test)]
mod own_folder_off_tests {
    use super::*;
    use super::tests::{eml_mod, tmp};

    /// A player's report (Kiln 0.2.0-dev.31.9): the profile had an EML mod off, the player's own folder of it was in
    /// ShroudForge's mods folder with ShroudForge's own switch on, Kiln hosted ShroudForge and ShroudForge ran it, and
    /// eml.json said "disabled: switched off in the profile". Kiln never writes into such a folder. Hosted by Kiln,
    /// ShroudForge now skips it this start (Kiln names its package); where nothing skips it, the row says that it runs,
    /// why, and how to turn it off. A mod that is off with no folder of its own there, or one ShroudForge has off too,
    /// stays plainly "disabled".
    #[test]
    fn a_profile_off_own_folder_under_shroudforge_is_skipped_or_said_to_run() {
        let d = tmp("aside-off-own");
        let switched = |p: &Utf8Path, on: bool| std::fs::write(p.join("extended.mod.json"),
            format!(r#"{{"schemaVersion":1,"launcher":"EML","enabled":{on},"targets":["client","server"]}}"#)).unwrap();
        let own = eml_mod(&d, "mods/quiver-slots", "quiver-slots");
        switched(&own, true);
        let shut = eml_mod(&d, "mods/shut-own", "shut-own");
        switched(&shut, false);
        let copy = eml_mod(&d, "mods/stack-tweaks", "stack-tweaks");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"stack-tweaks","version":"1.0.0","hash":"h"}"#).unwrap();
        switched(&copy, true);
        let j = r#"{"mods":{"eml":[{"id":"quiver-slots","path":"/lib/quiver-slots","enabled":false},
            {"id":"shut-own","path":"/lib/shut-own","enabled":false},{"id":"stack-tweaks","path":"/lib/stack-tweaks","enabled":false},
            {"id":"elsewhere","path":"/lib/elsewhere","enabled":false}]}}"#;
        let who = "ShroudForge (hosted by Kiln)";
        let row = |rows: &[Entry], id: &str| rows.iter().find(|e| e.id.as_deref() == Some(id)).map(|e| (e.state.clone(), e.reason.clone().unwrap_or_default(), e.path.clone())).unwrap();

        // hosted by Kiln, a runtime that takes the list: skipped, one line each, the player's files untouched
        let mut asked = Vec::new();
        let mut skip = |package: &str| { asked.push(package.to_owned()); true };
        let rows = aside_entries_with(j, &d.join("kiln"), &[d.join("mods")], who, Some(false), Some(&mut skip));
        assert_eq!(asked, ["quiver-slots", "stack-tweaks"], "only the packages that would run");
        let (state, reason, path) = row(&rows, "quiver-slots");
        assert_eq!(state, "disabled");
        assert_eq!(reason, format!("quiver-slots is off in the profile: your own folder {own} is skipped this start; Kiln leaves its files as they are"));
        assert_eq!(Utf8Path::new(&path), own.as_path());
        let (state, reason, _) = row(&rows, "stack-tweaks");
        assert_eq!(state, "disabled");
        assert!(reason.starts_with(&format!("stack-tweaks is off in the profile: Kiln's copy {copy} has ShroudForge's switch on")), "{reason}");
        for id in ["shut-own", "elsewhere"] {
            assert_eq!(row(&rows, id).0, "disabled", "{id}");
            assert_eq!(row(&rows, id).1, "switched off in the profile", "{id}");
        }
        let lines = aside_log(&rows, who);
        assert!(lines.iter().any(|(level, line)| *level == 3 && line == &format!(
            "kiln-eml: quiver-slots is off in the profile: your own folder {own} is skipped this start; Kiln leaves its files as they are")), "{lines:?}");
        assert!(std::fs::read_to_string(own.join("extended.mod.json")).unwrap().contains(r#""enabled":true"#), "never written");

        // a runtime that takes no skip list (an installed ShroudForge): it runs, and the row says so, why and how to stop it
        let mut refuse = |_: &str| false;
        let rows = aside_entries_with(j, &d.join("kiln"), &[d.join("mods")], who, Some(false), Some(&mut refuse));
        let (state, reason, _) = row(&rows, "quiver-slots");
        assert_eq!(state, "own-loader", "it runs: never \"disabled\"");
        assert!(reason.starts_with("runs under ShroudForge (hosted by Kiln) although it is off in the profile: it is your own folder"), "{reason}");
        assert!(reason.contains("ShroudForge's own switch for it is on") && reason.contains("takes no skip list from Kiln"), "{reason}");
        assert!(reason.contains("Mods > ShroudForge") && reason.contains("ShroudForge's Modloader") && reason.contains("move the folder out of"), "{reason}");
        assert!(row(&rows, "stack-tweaks").1.contains("Press Play in the Kiln launcher"), "{}", row(&rows, "stack-tweaks").1);
        let summary = aside_log(&rows, who).last().unwrap().clone();
        assert_eq!(summary.0, 2, "a warning: a mod runs that the profile has off");
        assert!(summary.1.contains("2 run although the profile has them off"), "{summary:?}");
        // a dedicated server has no in-game menu
        let rows = aside_entries_with(j, &d.join("kiln"), &[d.join("mods")], who, Some(true), Some(&mut refuse));
        let reason = row(&rows, "quiver-slots").1;
        assert!(reason.contains(r#""enabled": false"#) && !reason.contains("Mods > ShroudForge"), "{reason}");

        // ShroudForge's own loader (no Kiln host): it runs
        let rows = aside_entries(j, &d.join("kiln"), &[d.join("mods")], "ShroudForge's loader", Some(false));
        let (state, reason, _) = row(&rows, "quiver-slots");
        assert_eq!(state, "own-loader");
        assert!(reason.contains("Kiln does not host ShroudForge this start, so it cannot have it skipped"), "{reason}");
        // EML's own loader runs every mod of its folder: the same, with the one way it has
        let rows = aside_entries(j, &d.join("kiln"), &[d.join("mods")], "EML's own loader", None);
        let (state, reason, _) = row(&rows, "shut-own");
        assert_eq!(state, "own-loader", "EML's own loader has no switch");
        assert!(reason.contains("although it is off in the profile") && reason.contains("move the folder out of"), "{reason}");
        std::fs::remove_dir_all(d).unwrap();
    }

    /// Review (0.2.0-dev.31.12): the runtime skips a package by its id, for every folder of it. An entry that is off must
    /// not have a package skipped that an entry the profile has on stands for (another Library entry of the same package,
    /// EML or ShroudForge, or the EML runtime list): that one would not run, while its row says it does. The lookup of
    /// the player's folder found Kiln's copy of the entry that is on, and named it "your own folder".
    #[test]
    fn a_package_that_an_entry_which_is_on_stands_for_is_never_skipped() {
        let d = tmp("aside-off-shared");
        let switched = |p: &Utf8Path| std::fs::write(p.join("extended.mod.json"),
            r#"{"schemaVersion":1,"launcher":"EML","enabled":true,"targets":["client","server"]}"#).unwrap();
        // the entry that is on: Kiln's copy in ShroudForge's folder
        let copy = eml_mod(&d, "mods/quiver-slots", "quiver-slots");
        std::fs::write(copy.join(".kiln-library.json"), r#"{"format":1,"kind":"eml","id":"quiver-slots","version":"1.0.0","hash":"h"}"#).unwrap();
        switched(&copy);
        let lib_on = eml_mod(&d, "lib/quiver-slots", "quiver-slots");
        // the entry that is off: an older Library entry of the same package
        let lib_old = eml_mod(&d, "lib/quiver-slots-old", "quiver-slots");
        // a package a ShroudForge entry that is on stands for, and one the EML runtime list does
        let own_sf = eml_mod(&d, "mods/shared-sf", "shared-sf");
        switched(&own_sf);
        let lib_sf = eml_mod(&d, "lib/shared-sf", "shared-sf");
        let own_rt = eml_mod(&d, "mods/runtime-pack", "runtime-pack");
        switched(&own_rt);
        let lib_rt = eml_mod(&d, "lib/runtime-pack-old", "runtime-pack");
        let j = serde_json::json!({"mods": {
            "eml": [{"id": "quiver-slots", "path": lib_on.as_str(), "enabled": true},
                    {"id": "quiver-slots-old", "path": lib_old.as_str(), "enabled": false},
                    {"id": "shared-sf-eml", "path": lib_sf.as_str(), "enabled": false},
                    {"id": "runtime-pack-old", "path": lib_rt.as_str(), "enabled": false},
                    {"id": "runtime-pack", "path": lib_rt.as_str(), "enabled": false}],
            "shroudforge": [{"id": "shared-sf", "path": lib_sf.as_str(), "enabled": true}]},
            "emlRuntime": ["runtime-pack"]}).to_string();
        let mut asked = Vec::new();
        let mut skip = |package: &str| { asked.push(package.to_owned()); true };
        let rows = aside_entries_with(&j, &d.join("kiln"), &[d.join("mods")], "ShroudForge (hosted by Kiln)", Some(false), Some(&mut skip));
        assert!(asked.is_empty(), "{asked:?}");
        let row = |id: &str| rows.iter().find(|e| e.id.as_deref() == Some(id)).unwrap().clone();
        assert_eq!(row("quiver-slots").state, "own-loader", "{:?}", row("quiver-slots"));
        for id in ["quiver-slots-old", "shared-sf-eml", "runtime-pack-old"] {
            assert_eq!((row(id).state.as_str(), row(id).reason.as_deref()), ("disabled", Some("switched off in the profile")), "{id}");
        }
        std::fs::remove_dir_all(d).unwrap();
    }

    /// The same, when ShroudForge skips the package: the count of what ShroudForge runs from its folder leaves it out and
    /// names it.
    #[test]
    fn the_shroudforge_runs_line_leaves_out_what_kiln_had_it_skip() {
        let d = tmp("sf-runs-skipped");
        let root = d.join("mods");
        for dir in ["kept", "quiver-slots"] {
            let p = eml_mod(&d, &format!("mods/{dir}"), dir);
            std::fs::write(p.join("extended.mod.json"), SERVER_SWITCH_FILE).unwrap();
        }
        let (runs, total) = shroudforge_runs(&root, false, &[root.join("quiver-slots")]);
        assert_eq!((runs.as_slice(), total), (["kept".to_string()].as_slice(), 2));
        let line = shroudforge_runs_log(&runs, total, &root, &["quiver-slots".to_string()]).unwrap();
        assert_eq!(line, format!("kiln-eml: 1 of 2 EML packages in {root} run under ShroudForge: kept; 1 is off in the profile and \
            skipped this start: quiver-slots"));
        std::fs::remove_dir_all(d).unwrap();
    }

    /// Kiln's own runner with drop-ins on: the player's own folder in the game's mods folder of an EML mod the profile has
    /// off ran as a drop-in beside the profile's "disabled" row. It is skipped now, with the same line; Kiln reads it in
    /// place and never writes into it. A package delegated to ShroudForge's runtime (emlRuntime) is not "off".
    #[test]
    fn kilns_runner_skips_a_dropin_of_a_mod_the_profile_has_off() {
        let d = tmp("runner-off-own");
        let lib = eml_mod(&d, "lib/quiver-slots", "quiver-slots");
        let delegated = eml_mod(&d, "lib/runtime-pack", "runtime-pack");
        let own = eml_mod(&d, "mods/Quiver Slots", "quiver-slots");
        let own_runtime = eml_mod(&d, "mods/runtime-pack", "runtime-pack");
        let other = eml_mod(&d, "mods/other", "other");
        let json = serde_json::json!({"dropins": true, "mods": {"eml": [
            {"id": "quiver-slots", "path": lib.as_str(), "enabled": false},
            {"id": "runtime-pack", "path": delegated.as_str(), "enabled": false}]}, "emlRuntime": ["runtime-pack"]}).to_string();
        let mut candidates = profile_candidates(&json, &d.join("kiln"));
        candidates.extend(folder_candidates(&d.join("mods"), "game-mods"));
        let (mut run, mut entries) = resolve(&candidates);
        assert!(run.contains(&own), "red before: the drop-in ran");
        let lines = honor_profile_off(&json, &d.join("kiln"), &mut run, &mut entries);
        assert!(!run.contains(&own) && run.contains(&other) && run.contains(&own_runtime), "{run:?}");
        let row = entries.iter().find(|e| Utf8Path::new(&e.path) == own.as_path()).unwrap();
        assert_eq!(row.state, "disabled");
        let line = format!("quiver-slots is off in the profile: your own folder {own} is skipped this start; Kiln leaves its files as they are");
        assert_eq!(row.reason.as_deref(), Some(line.as_str()));
        assert_eq!(lines, [format!("kiln-eml: {line}")]);
        std::fs::remove_dir_all(d).unwrap();
    }
}
