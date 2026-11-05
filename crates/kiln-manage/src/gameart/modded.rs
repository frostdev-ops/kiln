//! Items added by Kiln mods, for the save editor: an overlay next to the extracted art,
//! `<data>/editor-assets/modded/{items.json, icons/*.webp, source.json}`, that the editor merges into its item tables.
//!
//! Definitions (first source wins per GUID):
//! 1. runtime exports: `<game>/kiln/state/content-definitions.json`, written by Kiln.Content whenever its definitions
//!    change (every mod's code and data packs, `@definitions` synced from a server, placeholders), plus the files in
//!    `$KILN_CONTENT_DEFINITIONS` (a path list; tests and tools);
//! 2. data packs of the mods in the active profile, the launcher library and `<game>/kiln/mods/`: `content/*.json`
//!    `items.add`.
//!
//! The item id is FNV-1a-32 over the GUID's 16 bytes in memory order (kiln.content's `NewContent.IdOf`, the game's
//! own ItemId rule). Icons (first that exists): a mod's `assets/assets.json` `{"kind": "icon", guid, file}`; the
//! definition's PNG `icon` (relative to the mod folder in packs, `iconFile` in exports); an `item:<name>` or GUID
//! `icon` alias; the base item's extracted icon. Model or other kitbash references never change the icon (icons are
//! baked per item GUID). `assets.json` retextures of vanilla `UiTextureResource`s that are item icons become icon
//! overrides of those (vanilla) items. PNGs are fitted into 96x96 like the extracted icons.

use super::image::{self, Rgba};
use super::work::write_file;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap};
use std::path::{Path, PathBuf};

/// Bump when the overlay's format or rules change.
pub const MODDED_RECIPE: u32 = 1;
pub const DIR: &str = "modded";
/// guid -> icon file stem (`<debugName>`) of every extracted item icon, written by the editor extraction.
pub const ICON_INDEX: &str = ".icon-index.json";

/// A mod folder to read data packs and `assets/assets.json` from.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ModSource {
    pub id: String,
    pub dir: PathBuf,
    /// "profile" | "library" | "game"
    pub origin: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ModdedInputs {
    pub exports: Vec<PathBuf>,
    pub mods: Vec<ModSource>,
    /// For Windows-side paths in exports (`Z:\…` under Proton).
    #[serde(skip)]
    pub game: Option<crate::game::GameInstall>,
}

/// One item of the overlay (`modded/items.json` `items[]`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModdedItem {
    pub id: u32,
    pub guid: String,
    /// The item's internal name (`Kiln_Sample_Glacier`).
    pub debug: String,
    /// The base item's internal name: the editor copies its category, slot, stack, levels.
    pub base: Option<String>,
    /// Names by language (`en`, `pt`, ...).
    pub names: BTreeMap<String, String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rarity: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_stack: Option<u32>,
    /// Relative to the editor art root (`modded/icons/x.webp`, `icons/<base>_icon.webp`); None: no icon found.
    pub icon: Option<String>,
    pub mod_id: Option<String>,
    /// "runtime" | "pack"
    pub source: String,
    #[serde(default)]
    pub placeholder: bool,
}

/// A vanilla item whose icon a mod retextures.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IconOverride {
    pub id: u32,
    pub guid: String,
    pub icon: String,
    pub mod_id: Option<String>,
}

/// `modded/items.json`
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Overlay {
    pub format: u32,
    pub generated: String,
    pub fingerprint: String,
    pub items: Vec<ModdedItem>,
    #[serde(default)]
    pub icons: Vec<IconOverride>,
    #[serde(default)]
    pub problems: Vec<String>,
}

/// `modded/source.json`: what the overlay was made from.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModdedInfo {
    pub format: u32,
    pub recipe: u32,
    pub fingerprint: String,
    pub generated: String,
    pub items: usize,
    pub icons: usize,
    pub mods: Vec<String>,
    pub exports: Vec<String>,
    #[serde(default)]
    pub problems: Vec<String>,
}

pub fn read_info(editor_dest: &Path) -> Option<ModdedInfo> {
    crate::util::read_json(&editor_dest.join(DIR).join("source.json")).ok().flatten()
}

// ---------------------------------------------------------------- inputs

fn mod_id_of(dir: &Path) -> String {
    std::fs::read_to_string(dir.join("kiln.mod.json"))
        .ok()
        .and_then(|t| parse_jsonc(&t).ok())
        .and_then(|v| v.get("id").and_then(Value::as_str).map(String::from))
        .unwrap_or_else(|| dir.file_name().unwrap_or_default().to_string_lossy().into_owned())
}

fn has_content(dir: &Path) -> bool {
    dir.join("content").is_dir() || dir.join("assets/assets.json").is_file()
}

/// The inputs for a client: its runtime export and mods (active profile, `kiln/mods`), plus the library.
pub fn collect_inputs(dirs: &crate::paths::DataDirs, game: Option<&crate::game::GameInstall>) -> ModdedInputs {
    let mut inp = ModdedInputs { game: game.cloned(), ..Default::default() };
    if let Some(g) = game {
        let exp = g.path.join("kiln/state/content-definitions.json");
        if exp.is_file() {
            inp.exports.push(exp);
        }
    }
    if let Some(list) = std::env::var_os("KILN_CONTENT_DEFINITIONS") {
        inp.exports.extend(std::env::split_paths(&list).filter(|p| p.is_file()));
    }
    let mut push = |dir: PathBuf, origin: &str| {
        if has_content(&dir) && !inp.mods.iter().any(|m| m.dir == dir) {
            inp.mods.push(ModSource { id: mod_id_of(&dir), dir, origin: origin.into() });
        }
    };
    if let Some(g) = game {
        if let Ok(Some(p)) = crate::profile::read_game_profile(&g.path) {
            for m in p.mods.kiln.iter().filter(|m| m.enabled) {
                let host = crate::winpath::to_host(&m.path, g).unwrap_or_else(|_| g.path.join("kiln").join(&m.path));
                push(host, "profile");
            }
        }
        if let Ok(rd) = std::fs::read_dir(g.path.join("kiln/mods")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_dir() && !p.join(".disabled").exists()).collect();
            v.sort();
            v.into_iter().for_each(|d| push(d, "game"));
        }
    }
    // library: library/kiln/<id>/<version>+<hash8>/
    if let Ok(rd) = std::fs::read_dir(dirs.library().join("kiln")) {
        let mut v: Vec<PathBuf> = rd
            .flatten()
            .filter(|e| e.path().is_dir())
            .flat_map(|e| std::fs::read_dir(e.path()).into_iter().flatten().flatten().map(|x| x.path()).filter(|p| p.is_dir()))
            .collect();
        v.sort();
        v.into_iter().for_each(|d| push(d, "library"));
    }
    inp
}

fn stat_line(p: &Path) -> Option<String> {
    let m = std::fs::metadata(p).ok()?;
    let t = m.modified().ok().map(crate::util::unix).unwrap_or(0);
    Some(format!("{}|{}|{}\n", p.display(), m.len(), t))
}

fn files_of(dir: &Path, sub: &str, ext: &str) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir.join(sub))
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case(ext)))
        .collect();
    v.sort();
    v
}

/// A cheap fingerprint of everything the overlay depends on (paths, sizes, mtimes; the editor art's build).
pub fn fingerprint(inp: &ModdedInputs, editor_dest: &Path) -> String {
    let mut s = format!("modded {MODDED_RECIPE}\n");
    if let Some(src) = super::read_source(editor_dest) {
        s.push_str(&format!("art {} {}\n", src.extracted, src.game.map(|g| g.kfc_version).unwrap_or_default()));
    }
    for e in &inp.exports {
        s.push_str(&stat_line(e).unwrap_or_default());
    }
    for m in &inp.mods {
        s.push_str(&format!("mod {} {}\n", m.id, m.dir.display()));
        for f in files_of(&m.dir, "content", "json") {
            s.push_str(&stat_line(&f).unwrap_or_default());
        }
        for f in [m.dir.join("assets/assets.json"), m.dir.join("kiln.mod.json")] {
            s.push_str(&stat_line(&f).unwrap_or_default());
        }
        for sub in ["icons", "assets", "content"] {
            for f in files_of(&m.dir, sub, "png") {
                s.push_str(&stat_line(&f).unwrap_or_default());
            }
        }
    }
    format!("mdf1:{}", crate::hash::sha256_hex(s.as_bytes()))
}

// ---------------------------------------------------------------- parsing

/// Kiln's JSON: `//` and `/* */` comments, trailing commas.
pub fn parse_jsonc(text: &str) -> Result<Value> {
    let b = text.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(b.len());
    let (mut i, mut in_str) = (0, false);
    while i < b.len() {
        let c = b[i];
        if in_str {
            out.push(c);
            if c == b'\\' && i + 1 < b.len() {
                out.push(b[i + 1]);
                i += 2;
                continue;
            }
            if c == b'"' {
                in_str = false;
            }
            i += 1;
            continue;
        }
        if c == b'"' {
            in_str = true;
        } else if c == b'/' && b.get(i + 1) == Some(&b'/') {
            while i < b.len() && b[i] != b'\n' {
                i += 1;
            }
            continue;
        } else if c == b'/' && b.get(i + 1) == Some(&b'*') {
            i += 2;
            while i + 1 < b.len() && !(b[i] == b'*' && b[i + 1] == b'/') {
                i += 1;
            }
            i += 2;
            continue;
        } else if c == b',' {
            let mut j = i + 1;
            while j < b.len() && b[j].is_ascii_whitespace() {
                j += 1;
            }
            if j < b.len() && (b[j] == b'}' || b[j] == b']') {
                i += 1;
                continue;
            }
        }
        out.push(c);
        i += 1;
    }
    serde_json::from_slice(&out).map_err(|e| Error::new(ErrorCode::Parse, e.to_string()))
}

/// GUID text -> its 16 bytes in memory order (Windows `{u32, u16, u16, u8[8]}` little-endian), as kiln.content hashes.
pub fn guid_bytes(text: &str) -> Option<[u8; 16]> {
    let h: String = text.trim().trim_matches(['{', '}']).chars().filter(|c| *c != '-').collect();
    if h.len() != 32 || !h.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    let byte = |i: usize| u8::from_str_radix(&h[2 * i..2 * i + 2], 16).unwrap();
    let mut g = [0u8; 16];
    for (dst, src) in [(0, 3), (1, 2), (2, 1), (3, 0), (4, 5), (5, 4), (6, 7), (7, 6)] {
        g[dst] = byte(src);
    }
    for i in 8..16 {
        g[i] = byte(i);
    }
    Some(g)
}

/// The item id of a GUID (kiln.content `NewContent.IdOf`).
pub fn item_id(guid: &str) -> Option<u32> {
    guid_bytes(guid).map(|g| super::kfc::fnv1a32(&g))
}

#[derive(Debug, Clone)]
enum IconRef {
    Png(PathBuf),
    Alias(String),
}

#[derive(Debug, Clone)]
struct Def {
    guid: String,
    name: String,
    base: Option<String>,
    names: BTreeMap<String, String>,
    rarity: Option<String>,
    max_stack: Option<u32>,
    icon: Option<IconRef>,
    mod_id: Option<String>,
    mod_dir: Option<PathBuf>,
    source: &'static str,
    placeholder: bool,
}

fn str_of<'a>(v: &'a Value, k: &str) -> Option<&'a str> {
    v.get(k).and_then(Value::as_str).filter(|s| !s.is_empty())
}

/// The editor's name fields from a definition: `texts.<lang>.name`, else `displayName`, else the internal name.
fn names_of(def: &Value, display: Option<&str>, name: &str) -> BTreeMap<String, String> {
    let mut m = BTreeMap::new();
    if let Some(t) = def.get("texts").and_then(Value::as_object) {
        for (lang, x) in t {
            if let Some(n) = str_of(x, "name") {
                let key = lang.to_ascii_lowercase().replace('_', "-");
                let key = if key.starts_with("pt") { "pt".to_string() } else { key.split('-').next().unwrap_or(&key).to_string() };
                m.entry(key).or_insert_with(|| n.to_string());
            }
        }
    }
    let en = m.get("en").cloned().or_else(|| display.map(String::from)).unwrap_or_else(|| name.to_string());
    m.insert("en".into(), en);
    m
}

fn def_from(def: &Value, guid: &str, name: &str, source: &'static str) -> Def {
    let fields = def.get("fields");
    let display = str_of(def, "displayName");
    Def {
        guid: guid.to_ascii_lowercase(),
        name: name.to_string(),
        base: str_of(def, "base").map(String::from),
        names: names_of(def, display, name),
        rarity: fields.and_then(|f| str_of(f, "rarity")).map(String::from),
        max_stack: fields.and_then(|f| f.get("maxStackSize")).and_then(Value::as_u64).map(|v| v.min(u32::MAX as u64) as u32),
        icon: str_of(def, "icon").map(|i| if i.to_ascii_lowercase().ends_with(".png") { IconRef::Png(PathBuf::from(i)) } else { IconRef::Alias(i.to_string()) }),
        mod_id: None,
        mod_dir: None,
        source,
        placeholder: false,
    }
}

/// A path from the game's side (`Z:\home\…` under Proton) as a host path.
fn host_path(p: &str, game: Option<&crate::game::GameInstall>) -> PathBuf {
    if let Some(g) = game
        && let Ok(h) = crate::winpath::to_host(p, g)
    {
        return h;
    }
    crate::winpath::z_to_unix(p).unwrap_or_else(|| PathBuf::from(p))
}

fn read_exports(inp: &ModdedInputs, problems: &mut Vec<String>) -> Vec<Def> {
    let mut out = Vec::new();
    for f in &inp.exports {
        let v = match std::fs::read_to_string(f).map_err(Error::from).and_then(|t| parse_jsonc(&t)) {
            Ok(v) => v,
            Err(e) => {
                problems.push(format!("{}: {}", f.display(), e.message));
                continue;
            }
        };
        let list = v.get("definitions").or(Some(&v)).and_then(Value::as_array).cloned().unwrap_or_default();
        for e in list {
            if str_of(&e, "kind") != Some("item") {
                continue;
            }
            let (Some(guid), Some(name)) = (str_of(&e, "guid"), str_of(&e, "name")) else { continue };
            let body = e.get("definition").cloned().unwrap_or(Value::Null);
            let mut d = def_from(&body, guid, name, "runtime");
            if d.base.is_none() {
                d.base = str_of(&e, "base").map(String::from);
            }
            if let Some(disp) = str_of(&e, "displayName")
                && !body.get("texts").is_some_and(|t| t.get("en").is_some())
            {
                d.names.insert("en".into(), disp.to_string());
            }
            if let Some(icon) = str_of(&e, "iconFile") {
                d.icon = Some(IconRef::Png(host_path(icon, inp.game.as_ref())));
            } else if let Some(IconRef::Png(p)) = &d.icon {
                d.icon = Some(IconRef::Png(host_path(&p.to_string_lossy(), inp.game.as_ref())));
            }
            d.mod_id = str_of(&e, "mod").map(String::from);
            d.placeholder = e.get("placeholder").and_then(Value::as_bool).unwrap_or(false);
            out.push(d);
        }
    }
    out
}

fn read_packs(m: &ModSource, problems: &mut Vec<String>) -> Vec<Def> {
    let mut out = Vec::new();
    for f in files_of(&m.dir, "content", "json") {
        let v = match std::fs::read_to_string(&f).map_err(Error::from).and_then(|t| parse_jsonc(&t)) {
            Ok(v) => v,
            Err(e) => {
                problems.push(format!("{}: {}", f.display(), e.message));
                continue;
            }
        };
        for e in v.pointer("/items/add").and_then(Value::as_array).cloned().unwrap_or_default() {
            let (Some(guid), Some(name)) = (str_of(&e, "guid"), str_of(&e, "name")) else { continue };
            let mut d = def_from(&e, guid, name, "pack");
            if let Some(IconRef::Png(p)) = &d.icon
                && p.is_relative()
            {
                d.icon = Some(IconRef::Png(m.dir.join(p)));
            }
            d.mod_id = Some(m.id.clone());
            d.mod_dir = Some(m.dir.clone());
            out.push(d);
        }
    }
    out
}

/// `assets/assets.json` entries: (kind, guid, PNG path).
fn read_assets(m: &ModSource) -> Vec<(String, String, PathBuf)> {
    let Ok(t) = std::fs::read_to_string(m.dir.join("assets/assets.json")) else { return Vec::new() };
    let Ok(v) = parse_jsonc(&t) else { return Vec::new() };
    let mut out = Vec::new();
    for e in v.get("entries").and_then(Value::as_array).cloned().unwrap_or_default() {
        let (Some(kind), Some(guid), Some(file)) = (str_of(&e, "kind"), str_of(&e, "guid"), str_of(&e, "file")) else { continue };
        if kind == "texture" && str_of(&e, "type").is_some_and(|t| t != super::res::UI_TEXTURE) {
            continue;
        }
        if !file.to_ascii_lowercase().ends_with(".png") {
            continue;
        }
        let p = [m.dir.join("assets").join(file), m.dir.join(file)].into_iter().find(|p| p.is_file());
        if let Some(p) = p {
            out.push((kind.to_string(), guid.to_ascii_lowercase(), p));
        }
    }
    out
}

/// Decodes a PNG into RGBA8.
pub fn decode_png(path: &Path) -> Result<Rgba> {
    let f = std::fs::File::open(path).map_err(crate::util::io_at(path, "reading"))?;
    let mut d = png::Decoder::new(std::io::BufReader::new(f));
    d.set_transformations(png::Transformations::normalize_to_color8());
    let mut r = d.read_info().map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", path.display())))?;
    let mut buf = vec![0; r.output_buffer_size().ok_or_else(|| Error::new(ErrorCode::Parse, "png: too large"))?];
    let info = r.next_frame(&mut buf).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", path.display())))?;
    let (w, h) = (info.width, info.height);
    let px: Vec<u8> = match info.color_type {
        png::ColorType::Rgba => buf[..(w * h * 4) as usize].to_vec(),
        png::ColorType::Rgb => buf.chunks_exact(3).take((w * h) as usize).flat_map(|p| [p[0], p[1], p[2], 255]).collect(),
        png::ColorType::GrayscaleAlpha => buf.chunks_exact(2).take((w * h) as usize).flat_map(|p| [p[0], p[0], p[0], p[1]]).collect(),
        png::ColorType::Grayscale => buf.iter().take((w * h) as usize).flat_map(|&g| [g, g, g, 255]).collect(),
        png::ColorType::Indexed => return Err(Error::new(ErrorCode::Unsupported, "indexed PNG not expanded")),
    };
    Ok(Rgba { w, h, px })
}

fn safe_file(name: &str) -> String {
    name.chars().map(|c| if c.is_ascii_alphanumeric() || c == '_' || c == '-' { c } else { '_' }).collect()
}

/// Builds `<editor_dest>/modded/` (replaced as a whole). `editor_dest` must hold the extracted art (base icons).
pub fn build(inp: &ModdedInputs, editor_dest: &Path, p: &mut dyn Progress) -> Result<ModdedInfo> {
    p.report(None, "Reading mod items");
    let mut problems = Vec::new();
    let index: HashMap<String, String> = crate::util::read_json(&editor_dest.join(ICON_INDEX)).ok().flatten().unwrap_or_default();
    let base_icon = |name: &str| -> Option<String> {
        let rel = format!("icons/{name}_icon.webp");
        editor_dest.join(&rel).is_file().then_some(rel)
    };
    // definitions, first source per GUID wins: exports, then packs (profile, game, library order)
    let mut defs: Vec<Def> = read_exports(inp, &mut problems);
    let mods_by_id: HashMap<&str, &ModSource> = inp.mods.iter().map(|m| (m.id.as_str(), m)).collect();
    for m in &inp.mods {
        defs.extend(read_packs(m, &mut problems));
    }
    let mut seen = std::collections::HashSet::new();
    defs.retain(|d| seen.insert(d.guid.clone()));
    // icons from assets.json (by GUID)
    let mut asset_icons: HashMap<String, (PathBuf, String)> = HashMap::new();
    let mut retex: Vec<(String, PathBuf, String)> = Vec::new();
    for m in &inp.mods {
        for (kind, guid, file) in read_assets(m) {
            match kind.as_str() {
                "icon" => {
                    asset_icons.entry(guid).or_insert((file, m.id.clone()));
                }
                "texture" if index.contains_key(&guid) => retex.push((guid, file, m.id.clone())),
                _ => {}
            }
        }
    }
    let tmp = editor_dest.join(format!(".{DIR}-{}-{:x}", std::process::id(), crate::util::now_unix()));
    let result = (|| {
        let icons_dir = tmp.join("icons");
        let mut n_icons = 0usize;
        let mut png_icon = |path: &Path, stem: &str, problems: &mut Vec<String>| -> Option<String> {
            let rel = format!("{DIR}/icons/{}.webp", safe_file(stem));
            match decode_png(path).and_then(|img| {
                let (w, h) = image::fit_within(img.w, img.h, super::editor::ICON_SIZE, super::editor::ICON_SIZE);
                image::encode_webp(&image::resize(&img, w, h))
            }) {
                Ok(b) => match write_file(&icons_dir.join(format!("{}.webp", safe_file(stem))), &b) {
                    Ok(_) => {
                        n_icons += 1;
                        Some(rel)
                    }
                    Err(e) => {
                        problems.push(e.message);
                        None
                    }
                },
                Err(e) => {
                    problems.push(format!("{}: {}", path.display(), e.message));
                    None
                }
            }
        };
        let mut items = Vec::new();
        for (i, d) in defs.iter().enumerate() {
            if p.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "Canceled."));
            }
            p.report(Some(i as f32 / defs.len().max(1) as f32), "Preparing mod items");
            let Some(id) = item_id(&d.guid) else {
                problems.push(format!("{}: bad GUID {}", d.name, d.guid));
                continue;
            };
            let mod_id = d.mod_id.clone();
            let mod_dir = d.mod_dir.clone().or_else(|| mod_id.as_deref().and_then(|m| mods_by_id.get(m)).map(|m| m.dir.clone()));
            let mut icon = None;
            if let Some((f, _)) = asset_icons.get(&d.guid) {
                icon = png_icon(f, &d.name, &mut problems);
            }
            if icon.is_none() {
                icon = match &d.icon {
                    Some(IconRef::Png(f)) => {
                        let f = if f.is_relative() { mod_dir.as_ref().map(|m| m.join(f)).unwrap_or_else(|| f.clone()) } else { f.clone() };
                        if f.is_file() { png_icon(&f, &d.name, &mut problems) } else { None }
                    }
                    Some(IconRef::Alias(a)) => {
                        let a = a.strip_prefix("item:").unwrap_or(a);
                        base_icon(a).or_else(|| index.get(&a.to_ascii_lowercase()).and_then(|n| base_icon(n)))
                    }
                    None => None,
                };
            }
            if icon.is_none() {
                icon = d.base.as_deref().and_then(base_icon);
            }
            items.push(ModdedItem {
                id,
                guid: d.guid.clone(),
                debug: d.name.clone(),
                base: d.base.clone(),
                names: d.names.clone(),
                rarity: d.rarity.clone(),
                max_stack: d.max_stack,
                icon,
                mod_id,
                source: d.source.into(),
                placeholder: d.placeholder,
            });
        }
        let mut overrides = Vec::new();
        for (guid, file, mod_id) in &retex {
            let (Some(id), Some(stem)) = (item_id(guid), index.get(guid)) else { continue };
            if let Some(icon) = png_icon(file, &format!("vanilla_{stem}"), &mut problems) {
                overrides.push(IconOverride { id, guid: guid.clone(), icon, mod_id: Some(mod_id.clone()) });
            }
        }
        problems.truncate(50);
        let fp = fingerprint(inp, editor_dest);
        let generated = crate::util::now_rfc3339();
        let overlay = Overlay { format: 1, generated: generated.clone(), fingerprint: fp.clone(), items, icons: overrides, problems: problems.clone() };
        std::fs::create_dir_all(&tmp).map_err(crate::util::io_at(&tmp, "creating"))?;
        crate::util::write_json_atomic(&tmp.join("items.json"), &overlay)?;
        let info = ModdedInfo {
            format: 1,
            recipe: MODDED_RECIPE,
            fingerprint: fp,
            generated,
            items: overlay.items.len(),
            icons: n_icons,
            mods: inp.mods.iter().map(|m| format!("{} ({})", m.id, m.origin)).collect(),
            exports: inp.exports.iter().map(|e| e.display().to_string()).collect(),
            problems,
        };
        crate::util::write_json_atomic(&tmp.join("source.json"), &info)?;
        // swap in
        let cur = editor_dest.join(DIR);
        let old = editor_dest.join(format!(".old-{DIR}-{}", std::process::id()));
        if cur.exists() {
            std::fs::rename(&cur, &old).map_err(crate::util::io_at(&cur, "moving"))?;
        }
        if let Err(e) = std::fs::rename(&tmp, &cur) {
            let _ = std::fs::rename(&old, &cur);
            return Err(crate::util::io_at(&cur, "replacing")(e));
        }
        let _ = std::fs::remove_dir_all(&old);
        Ok(info)
    })();
    let _ = std::fs::remove_dir_all(&tmp);
    result
}

/// true when `<editor_dest>/modded/` was made from exactly these inputs.
pub fn current(inp: &ModdedInputs, editor_dest: &Path) -> bool {
    read_info(editor_dest).is_some_and(|i| i.recipe == MODDED_RECIPE && i.fingerprint == fingerprint(inp, editor_dest))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!("kiln-modded-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    fn png(path: &Path, w: u32, h: u32, rgba: [u8; 4]) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut out = Vec::new();
        {
            let mut e = png::Encoder::new(&mut out, w, h);
            e.set_color(png::ColorType::Rgba);
            e.set_depth(png::BitDepth::Eight);
            e.write_header().unwrap().write_image_data(&(0..w * h).flat_map(|_| rgba).collect::<Vec<u8>>()).unwrap();
        }
        std::fs::write(path, out).unwrap();
    }

    #[test]
    fn ids_guids_and_jsonc() {
        // kiln.content's rule (NewContent.IdOf), checked against the lab export: Kiln_Kitbash_Sword = 0x68338301
        assert_eq!(item_id("0f8d4b7e-61a3-4c0f-9b9a-7e3a52c1d0a4"), Some(0x6833_8301));
        assert_eq!(guid_bytes("00000001-0002-0003-0405-060708090a0b").unwrap(), [1, 0, 0, 0, 2, 0, 3, 0, 4, 5, 6, 7, 8, 9, 10, 11]);
        assert!(guid_bytes("nope").is_none());
        let v = parse_jsonc("{ // c\n \"a\": [1, 2,], /* x */ \"s\": \"a // not a comment, é\", }").unwrap();
        assert_eq!(v["a"].as_array().unwrap().len(), 2);
        assert_eq!(v["s"], "a // not a comment, é");
    }

    #[test]
    fn overlay_from_export_packs_and_assets() {
        let root = tmp("overlay");
        let art = root.join("editor-assets");
        std::fs::create_dir_all(art.join("icons")).unwrap();
        for n in ["Material_T1_Resin", "Weapon_T1_1H_SwordEpic_01", "Weapon_T4_1H_SwordEpic_02"] {
            std::fs::write(art.join(format!("icons/{n}_icon.webp")), b"x").unwrap();
        }
        // the vanilla Powder Ball icon's GUID, for a retexture
        std::fs::write(art.join(ICON_INDEX), r#"{"010932d7-557d-4dd5-9960-e538ff6e1c9e": "Consumable_Grenade"}"#).unwrap();
        // a mod with a data pack (JSONC), a pack icon, an assets.json icon and a retexture
        let m = root.join("mods/sample");
        std::fs::create_dir_all(m.join("content")).unwrap();
        std::fs::write(m.join("kiln.mod.json"), r#"{"id": "sample", "version": "1.0.0"}"#).unwrap();
        std::fs::write(m.join("content/items.json"), r#"{
            // a comment
            "items": {"add": [
                {"guid": "7b7c5f14-2a9c-4b7e-9a3f-10e2bb6e12c9", "name": "Kiln_Ember", "base": "Material_T1_Resin",
                 "displayName": "Emberseal", "fields": {"maxStackSize": 50, "rarity": "Rare"}},
                {"guid": "11111111-2222-3333-4444-555555555555", "name": "Kiln_PackIcon", "base": "Material_T1_Resin",
                 "icon": "icons/pack.png", "texts": {"en": {"name": "Pack Icon"}, "pt-br": {"name": "Ícone"}}},
                {"guid": "22222222-2222-3333-4444-555555555555", "name": "Kiln_Alias", "base": "Material_T1_Resin",
                 "icon": "item:Weapon_T4_1H_SwordEpic_02"},
            ]}
        }"#).unwrap();
        png(&m.join("icons/pack.png"), 200, 100, [10, 20, 30, 255]);
        std::fs::create_dir_all(m.join("assets")).unwrap();
        png(&m.join("assets/ember.png"), 64, 64, [200, 10, 10, 255]);
        png(&m.join("assets/retex.png"), 32, 32, [1, 2, 3, 255]);
        std::fs::write(m.join("assets/assets.json"), r#"{"entries": [
            {"kind": "icon", "guid": "7b7c5f14-2a9c-4b7e-9a3f-10e2bb6e12c9", "file": "ember.png"},
            {"kind": "texture", "guid": "010932d7-557d-4dd5-9960-e538ff6e1c9e", "type": "keen::UiTextureResource", "file": "retex.png"}]}"#).unwrap();
        // a runtime export: a code-defined item with an absolute PNG icon (Z: path under Proton), and one also in the pack
        png(&root.join("glacier.png"), 96, 96, [0, 0, 255, 255]);
        // the game's view of the file: Z:\… under Proton, the path as it is on Windows
        let z = if cfg!(windows) {
            root.join("glacier.png").display().to_string()
        } else {
            format!("Z:{}", root.join("glacier.png").display()).replace('/', "\\")
        };
        let export = root.join("content-definitions.json");
        std::fs::write(&export, serde_json::json!({"format": 1, "definitions": [
            {"kind": "item", "guid": "7d3e9b52-1c84-4f0a-a6d7-5e2b8c9f1a03", "id": "0x0", "name": "Kiln_Sample_Glacier",
             "base": "Weapon_T1_1H_SwordEpic_01", "mod": "newitems", "iconFile": z,
             "definition": {"name": "Kiln_Sample_Glacier", "base": "Weapon_T1_1H_SwordEpic_01", "model": "item:Weapon_T4_1H_SwordEpic_02",
                            "texts": {"en": {"name": "Glacier"}, "de": {"name": "Gletscher"}}, "fields": {"rarity": "Epic"}}},
            {"kind": "item", "guid": "b84f2c6e-9a17-4d3b-8e50-c1f7a2d6e9b4", "name": "Kiln_Sample_FrostMail", "base": "Armor_T1_Warrior_DPS_Chest",
             "mod": "newitems", "definition": {"model": "item:Armor_T2_Warrior_Tank_Chest", "texts": {"en": {"name": "Frost Mail"}}}},
            {"kind": "recipe", "guid": "e2a6c8f4-3b19-4d75-9c02-7f5e1a8b4d36", "name": "Kiln_Sample_Glacier_Recipe"},
            {"kind": "item", "guid": "7b7c5f14-2a9c-4b7e-9a3f-10e2bb6e12c9", "name": "Kiln_Ember", "base": "Material_T1_Resin",
             "mod": "sample", "displayName": "Emberseal", "definition": {"base": "Material_T1_Resin", "fields": {"rarity": "Rare", "maxStackSize": 50}}}]}).to_string()).unwrap();
        let inp = ModdedInputs {
            exports: vec![export.clone()],
            mods: vec![ModSource { id: "sample".into(), dir: m.clone(), origin: "library".into() }],
            game: None,
        };
        assert!(!current(&inp, &art));
        let info = build(&inp, &art, &mut crate::task::NoProgress).unwrap();
        assert_eq!(info.items, 5, "{:?}", info.problems);
        assert!(current(&inp, &art));
        let ov: Overlay = crate::util::read_json(&art.join("modded/items.json")).unwrap().unwrap();
        let by = |n: &str| ov.items.iter().find(|i| i.debug == n).unwrap().clone();
        let g = by("Kiln_Sample_Glacier");
        assert_eq!(g.id, item_id("7d3e9b52-1c84-4f0a-a6d7-5e2b8c9f1a03").unwrap());
        assert_eq!((g.names["en"].as_str(), g.rarity.as_deref(), g.source.as_str()), ("Glacier", Some("Epic"), "runtime"));
        assert_eq!(g.icon.as_deref(), Some("modded/icons/Kiln_Sample_Glacier.webp"), "the export's PNG, not the model");
        // kitbash model ref, no icon of its own: the base item's icon (none extracted here)
        assert_eq!(by("Kiln_Sample_FrostMail").icon, None);
        let e = by("Kiln_Ember");
        assert_eq!((e.source.as_str(), e.max_stack, e.names["en"].as_str()), ("runtime", Some(50), "Emberseal"));
        assert_eq!(e.icon.as_deref(), Some("modded/icons/Kiln_Ember.webp"), "assets.json icon");
        let pk = by("Kiln_PackIcon");
        assert_eq!((pk.names["pt"].as_str(), pk.mod_id.as_deref()), ("Ícone", Some("sample")));
        let pi = image::decode_webp(&std::fs::read(art.join(pk.icon.unwrap())).unwrap()).unwrap();
        assert_eq!((pi.w, pi.h), (96, 48));
        assert_eq!(by("Kiln_Alias").icon.as_deref(), Some("icons/Weapon_T4_1H_SwordEpic_02_icon.webp"));
        assert_eq!(ov.icons.len(), 1);
        assert_eq!(ov.icons[0].id, item_id("010932d7-557d-4dd5-9960-e538ff6e1c9e").unwrap());
        // a changed pack makes it stale
        std::thread::sleep(std::time::Duration::from_millis(1100));
        std::fs::write(m.join("content/more.json"), r#"{"items": {"add": []}}"#).unwrap();
        assert!(!current(&inp, &art));
        std::fs::remove_dir_all(&root).unwrap();
    }
}
