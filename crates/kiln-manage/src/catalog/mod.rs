//! Local game-data catalogs: the ids mods write (item, recipe, template, sequence, perk, buff, skill-node names,
//! config aliases, GUIDs and `0x` ids, sound cues, loca texts, the world's spires, shrines, roots, obelisks and bosses)
//! made from the user's **own installed game**, read-only, into the
//! launcher's data folder. Readers: the linter (`crate::lint`), the VS Code extension, `kiln catalog search`, and the
//! `Kiln.GameIds` source generator (IntelliSense constants for mods). The catalog is game data: it is never committed,
//! bundled or downloaded. Docs: docs/kiln/modding/catalogs.md.
//!
//! One file per side: `<dir>/client.json` (from `enshrouded.kfc` + `enshrouded.exe`) and `<dir>/server.json` (from a
//! dedicated server's `enshrouded_server.kfc`). `<dir>` = `$KILN_CATALOG` when it is a folder, else
//! `<launcher data dir>/catalog` ([`default_dir`]). The format (`kind: "kiln.catalog"`, `format: 1`) is shared with
//! the linter and the VS Code extension; the JSON Schema is `catalog.schema.json` next to this file, a synthetic fixture
//! `testdata/client.json`.
//!
//! This part (paths, the reader, search, status) is in the default build. Making a catalog reads the game's KFC4 data
//! and reflection and needs feature `gameart` ([`build`], [`build_default`], [`types`]).

#[cfg(feature = "gameart")]
mod decode;
#[cfg(feature = "gameart")]
mod extract;
#[cfg(feature = "gameart")]
pub mod types;
#[cfg(feature = "gameart")]
mod world;
#[cfg(test)]
mod tests;

#[cfg(feature = "gameart")]
pub use extract::{BuildInfo, BuildOptions, build, build_default, game_dirs};

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

/// `format` of the catalog files this module writes and reads.
pub const FORMAT: u32 = 1;
/// `kind` of a catalog file.
pub const KIND: &str = "kiln.catalog";
/// What gets extracted and how. Bump it when a section or field rule changes, so existing catalogs read as outdated
/// and `build_default` remakes them.
pub const RECIPE: u32 = 6; // 2: graphs. 3: template fields, configs, field kinds. 4: vfx. 5: materials. 6: world things
/// The sides, in lookup order for `side: None`.
pub const SIDES: [&str; 2] = ["client", "server"];
/// Every section name the format defines (all optional in a file).
pub const SECTIONS: [&str; 24] = [
    "items", "recipes", "loot", "drops", "templates", "arsenals", "graphs", "sequences", "buffs", "perks", "skills", "configs", "workshops",
    "itemCategories", "resources", "sounds", "vfx", "materials", "spires", "shrines", "shroudRoots", "obelisks", "bosses", "loca",
];

/// The game build a catalog was made from.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameInfo {
    /// "client" | "server"
    #[serde(default)]
    pub side: String,
    /// The game folder (host path).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dir: Option<String>,
    /// Steam's build id (`26430625`), when known.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub steam_build: Option<String>,
    /// The same as `steamBuild` (the linter's reader reads `build`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub build: Option<String>,
    /// Keen's data build (`1287369`): the first part of the `.kfc` version.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub svn: Option<String>,
    /// The whole `.kfc` version string: changes with every data build (what [`status`] compares).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kfc_version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exe_sha256: Option<String>,
}

/// One entry of a section. `name` (the key mods write), `id` (`0x%08x`) and `guid` (lower-case) when the game has
/// them; every other field is kept in `extra` (e.g. `displayName`, `description`, `kind`, `maxStack`, `icon`).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Entry {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub guid: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Entry {
    /// `displayName`, when the entry has one.
    pub fn display_name(&self) -> Option<&str> {
        self.extra.get("displayName").and_then(Value::as_str)
    }
    pub fn get(&self, key: &str) -> Option<&Value> {
        self.extra.get(key)
    }
}

/// One field a section's entries have (from the game's reflection): `items` -> `maxStackSize` (`uint16`).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct FieldInfo {
    /// Dotted path as data packs write it (`damageSetup.dmgMod`, `perkIds[2]`, `tags[*].tag`).
    pub path: String,
    /// The game's type name without `keen::` (`uint16`, `ItemRarity`, `Time`, `BlobArray<ItemTagWeight>`).
    #[serde(rename = "type")]
    pub type_name: String,
    /// false for identity fields data packs may not change (ids, GUIDs, flags); absent: not known.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub writable: Option<bool>,
    /// The value names of an enum or flag field.
    #[serde(default, rename = "enum", skip_serializing_if = "Option::is_none")]
    pub enum_values: Option<Vec<String>>,
    /// Their numbers, when they are not 0, 1, 2, ... (for a flag field: the bit).
    #[serde(default, rename = "enumNumbers", skip_serializing_if = "Option::is_none")]
    pub enum_numbers: Option<Vec<u64>>,
    /// What the value is: a scalar's name (`float32`, `uint16`, `bool`), `time`, `enum`, `flags`, `ref`, `guid`, `string`,
    /// `list`, `variant`, `optional`, `configs` or `struct`. Absent in catalogs made before this existed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<String>,
}

/// A catalog file (lenient: unknown keys are kept, every key is optional).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Catalog {
    #[serde(default)]
    pub format: u32,
    #[serde(default)]
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub generator: Option<String>,
    /// RFC 3339 UTC.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub generated: Option<String>,
    /// [`RECIPE`] of the generator that made it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recipe: Option<u32>,
    #[serde(default)]
    pub game: GameInfo,
    /// Language codes of the game's texts (`en-us`, `de-de`, ...); `displayName`s are `en-us`.
    #[serde(default)]
    pub languages: Vec<String>,
    /// The folder holding the item icons (`<data>/editor-assets/icons`, made by the launcher's game art), or null.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub icons: Option<String>,
    #[serde(default)]
    pub sections: BTreeMap<String, Vec<Entry>>,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub fields: BTreeMap<String, Vec<FieldInfo>>,
    /// Entries per section (written by the generator; recomputed by [`Catalog::counts`] when absent).
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub counts: BTreeMap<String, u64>,
    /// What could not be extracted (a section the game changed, ...); the rest of the catalog is still valid.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub problems: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seconds: Option<f64>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

impl Catalog {
    /// Reads a catalog file. A file that is not a catalog (`kind` set to something else, or `format` newer than this
    /// reader) is refused; everything inside is lenient.
    pub fn load(path: &Path) -> Result<Catalog> {
        let text = std::fs::read(path).map_err(crate::util::io_at(path, "reading"))?;
        Self::from_slice(&text).map_err(|e| Error::new(e.code, format!("{}: {}", path.display(), e.message)))
    }

    pub fn from_slice(text: &[u8]) -> Result<Catalog> {
        let text = text.strip_prefix(b"\xef\xbb\xbf").unwrap_or(text);
        let parse = |e: serde_json::Error| Error::new(ErrorCode::Parse, e.to_string());
        let mut v: Value = serde_json::from_slice(text).map_err(parse)?;
        let obj = v.as_object_mut().ok_or_else(|| Error::new(ErrorCode::Parse, "a catalog is a JSON object"))?;
        // lenient sections: an array of strings inside `sections` (older catalogs put `languages` there) moves
        // to the top level; non-object entries are dropped
        if let Some(Value::Object(secs)) = obj.get_mut("sections") {
            let langs = secs.remove("languages");
            for list in secs.values_mut() {
                match list {
                    Value::Array(a) => a.retain(Value::is_object),
                    other => *other = Value::Array(Vec::new()),
                }
            }
            if let Some(Value::Array(l)) = langs
                && !obj.contains_key("languages")
            {
                obj.insert("languages".into(), Value::Array(l.into_iter().filter(Value::is_string).collect()));
            }
        }
        let c: Catalog = serde_json::from_value(v).map_err(parse)?;
        if !c.kind.is_empty() && c.kind != KIND {
            return Err(Error::new(ErrorCode::Parse, format!("not a game-data catalog (kind '{}')", c.kind)));
        }
        if c.format > FORMAT {
            return Err(Error::new(ErrorCode::Unsupported, format!("catalog format {} is newer than this reader ({FORMAT})", c.format)));
        }
        Ok(c)
    }

    pub fn section(&self, name: &str) -> &[Entry] {
        self.sections.get(name).map(Vec::as_slice).unwrap_or(&[])
    }

    /// Entries per section.
    pub fn counts(&self) -> BTreeMap<String, u64> {
        self.sections.iter().map(|(k, v)| (k.clone(), v.len() as u64)).collect()
    }

    /// The entry of `section` a mod's key names: a name (case-sensitive, as the game has it), a `0x` id
    /// (case-insensitive), or a GUID (case-insensitive). Globs are the caller's business.
    pub fn lookup(&self, section: &str, key: &str) -> Option<&Entry> {
        let k = key.trim();
        let lower = k.to_ascii_lowercase();
        let key_is_id = lower.starts_with("0x");
        let key_is_guid = lower.len() == 36 && lower.chars().filter(|c| *c == '-').count() == 4;
        self.section(section).iter().find(|e| {
            e.name.as_deref() == Some(k)
                || (key_is_id && e.id.as_deref() == Some(lower.as_str()))
                || (key_is_guid && e.guid.as_deref() == Some(lower.as_str()))
        })
    }
}

/// A search result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Hit {
    pub section: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub guid: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
}

/// Entries whose name, display name, id or GUID contain `query` (case-insensitive; every whitespace-separated word
/// must match). Exact matches first, then prefix matches, then the rest; within a rank in section and file order.
/// `section`: only that one (else every section but `loca`, unless the query starts with `loca:`). `limit` 0 = 50.
pub fn search(cat: &Catalog, query: &str, section: Option<&str>, limit: usize) -> Vec<Hit> {
    let limit = if limit == 0 { 50 } else { limit };
    let words: Vec<String> = query.split_whitespace().map(str::to_lowercase).collect();
    let whole = query.trim().to_lowercase();
    let mut ranked: Vec<(u8, Hit)> = Vec::new();
    for (sname, entries) in &cat.sections {
        match section {
            Some(s) if s != sname => continue,
            None if sname == "loca" => continue,
            _ => {}
        }
        for e in entries {
            // a VFX has no name of its own: what else uses it finds it too
            let used_by = ["names", "usedBy"].into_iter().flat_map(|k| e.get(k).and_then(Value::as_array).into_iter().flatten()).filter_map(Value::as_str);
            let fields: Vec<String> = [e.name.as_deref(), e.display_name(), e.id.as_deref(), e.guid.as_deref(), e.get("text").and_then(Value::as_str)]
                .into_iter()
                .flatten()
                .chain(used_by)
                .map(str::to_lowercase)
                .collect();
            if fields.is_empty() || !words.iter().all(|w| fields.iter().any(|f| f.contains(w))) {
                continue;
            }
            let rank = if fields.contains(&whole) {
                0
            } else if fields.iter().any(|f| f.starts_with(&whole)) {
                1
            } else {
                2
            };
            ranked.push((
                rank,
                Hit {
                    section: sname.clone(),
                    name: e.name.clone(),
                    id: e.id.clone(),
                    guid: e.guid.clone(),
                    display_name: e.display_name().map(str::to_string).or_else(|| e.get("text").and_then(Value::as_str).map(str::to_string)),
                },
            ));
        }
    }
    ranked.sort_by_key(|(r, _)| *r); // stable: keeps section and file order within a rank
    ranked.into_iter().take(limit).map(|(_, h)| h).collect()
}

/// `<launcher data dir>/catalog` (Linux `~/.local/share/kiln/catalog`, Windows `%LOCALAPPDATA%\Kiln\catalog`;
/// `$KILN_DATA_DIR/catalog` when that is set). `$KILN_CATALOG` is not applied here: see [`catalog_dir`].
pub fn default_dir() -> Result<PathBuf> {
    Ok(crate::paths::DataDirs::locate()?.root.join("catalog"))
}

/// The folder catalogs are read from and written to: `$KILN_CATALOG` when it names a folder (or a file: its folder),
/// else [`default_dir`].
pub fn catalog_dir() -> Result<PathBuf> {
    if let Some(v) = std::env::var_os("KILN_CATALOG").filter(|v| !v.is_empty()) {
        let p = PathBuf::from(v);
        if p.extension().is_some_and(|x| x == "json") && !p.is_dir() {
            return Ok(p.parent().map(Path::to_path_buf).unwrap_or_default());
        }
        return Ok(p);
    }
    default_dir()
}

/// `<dir>/<side>.json`.
pub fn file_for(dir: &Path, side: &str) -> PathBuf {
    dir.join(format!("{side}.json"))
}

/// The catalog file to read, first hit wins:
/// 1. `explicit` (a file, or a folder holding `<side>.json`);
/// 2. `$KILN_CATALOG` (a file, or a folder);
/// 3. [`default_dir`].
///
/// `side` None = `client.json`, then `server.json`. None when no such file exists.
pub fn find(side: Option<&str>, explicit: Option<&Path>) -> Option<PathBuf> {
    let sides: Vec<&str> = match side {
        Some(s) => vec![s],
        None => SIDES.to_vec(),
    };
    let in_dir = |d: &Path| sides.iter().map(|s| file_for(d, s)).find(|f| f.is_file());
    let try_path = |p: &Path| if p.is_dir() { in_dir(p) } else { p.is_file().then(|| p.to_path_buf()) };
    if let Some(p) = explicit {
        return try_path(p);
    }
    if let Some(v) = std::env::var_os("KILN_CATALOG").filter(|v| !v.is_empty()) {
        return try_path(Path::new(&v));
    }
    default_dir().ok().and_then(|d| in_dir(&d))
}

/// A catalog file compared with the installed game.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogStatus {
    pub side: String,
    pub path: PathBuf,
    pub exists: bool,
    /// true: made from exactly the installed data build with the current [`RECIPE`]; false: the game (or the recipe)
    /// changed since, rebuild; None: unknown (no file, no game to compare with, or an unreadable file).
    pub current: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub game: Option<GameInfo>,
    /// The installed game's data build (`.kfc` version's first part), when a game was given.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub game_build: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub generated: Option<String>,
    #[serde(default)]
    pub counts: BTreeMap<String, u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

/// The side of a game folder: "client" (`enshrouded.kfc`) or "server" (`enshrouded_server.kfc`).
pub fn side_of(game_dir: &Path) -> Option<&'static str> {
    if game_dir.join("enshrouded.kfc").is_file() {
        Some("client")
    } else if game_dir.join("enshrouded_server.kfc").is_file() {
        Some("server")
    } else {
        None
    }
}

/// The `.kfc` version string of a game folder (header read only: cheap).
pub fn kfc_version(game_dir: &Path) -> Option<String> {
    let k = ["enshrouded.kfc", "enshrouded_server.kfc"].iter().map(|n| game_dir.join(n)).find(|p| p.is_file())?;
    read_kfc_version(&k).ok()
}

/// The version string at 0x10 of a `.kfc` (same as gameart::kfc::read_version, without needing feature `gameart`).
fn read_kfc_version(path: &Path) -> Result<String> {
    use std::io::{Read, Seek, SeekFrom};
    let bad = |m: &str| Error::new(ErrorCode::Parse, format!("{}: {m}", path.display()));
    let mut f = std::fs::File::open(path).map_err(crate::util::io_at(path, "reading"))?;
    let mut head = [0u8; 0x18];
    f.read_exact(&mut head).map_err(crate::util::io_at(path, "reading"))?;
    if &head[..4] != b"KFC4" {
        return Err(bad("not a KFC4 file"));
    }
    let rel = u32::from_le_bytes(head[0x10..0x14].try_into().unwrap()) as u64;
    let len = u32::from_le_bytes(head[0x14..0x18].try_into().unwrap()) as usize;
    if len == 0 || len > 4096 {
        return Err(bad("bad version string"));
    }
    let mut b = vec![0u8; len];
    f.seek(SeekFrom::Start(0x10 + rel)).map_err(crate::util::io_at(path, "reading"))?;
    f.read_exact(&mut b).map_err(crate::util::io_at(path, "reading"))?;
    Ok(String::from_utf8_lossy(&b).into_owned())
}

/// Per side: the catalog in `dir` (client.json, server.json) and, when `game_dir` is given, whether it is current for
/// that game (only the side of that game is compared; the other side's `current` is None).
pub fn status(dir: &Path, game_dir: Option<&Path>) -> Result<Vec<CatalogStatus>> {
    let game_side = game_dir.and_then(side_of);
    let game_version = game_dir.and_then(kfc_version);
    let mut out = Vec::new();
    for side in SIDES {
        let path = file_for(dir, side);
        let exists = path.is_file();
        let mut st = CatalogStatus {
            side: side.to_string(),
            path: path.clone(),
            exists,
            current: None,
            game: None,
            game_build: None,
            generated: None,
            counts: BTreeMap::new(),
            error: None,
        };
        if game_side == Some(side) {
            st.game_build = game_version.as_deref().map(|v| v.split('|').next().unwrap_or("").to_string());
        }
        if exists {
            match Catalog::load(&path) {
                Ok(c) => {
                    st.counts = if c.counts.is_empty() { c.counts() } else { c.counts.clone() };
                    st.generated = c.generated.clone();
                    if game_side == Some(side)
                        && let Some(v) = &game_version
                    {
                        st.current = Some(c.game.kfc_version.as_deref() == Some(v.as_str()) && c.recipe == Some(RECIPE));
                    }
                    st.game = Some(c.game);
                }
                Err(e) => st.error = Some(e.message),
            }
        }
        out.push(st);
    }
    Ok(out)
}

/// Writes a catalog: `<path>.tmp`, renamed over the file. The layout is diff-friendly and compact: header fields
/// pretty-printed, one entry per line.
pub fn write(path: &Path, cat: &Catalog) -> Result<u64> {
    let text = to_text(cat)?;
    crate::util::write_atomic(path, text.as_bytes())?;
    Ok(text.len() as u64)
}

/// The text [`write`] writes.
pub fn to_text(cat: &Catalog) -> Result<String> {
    let mut head = serde_json::to_value(cat)?;
    let obj = head.as_object_mut().ok_or_else(|| Error::new(ErrorCode::Internal, "catalog is not an object"))?;
    let sections = obj.remove("sections");
    let fields = obj.remove("fields");
    let mut s = String::from("{\n");
    let mut first = true;
    let mut sep = |s: &mut String| {
        if !first {
            s.push_str(",\n");
        }
        first = false;
    };
    for (k, v) in obj.iter() {
        sep(&mut s);
        s.push_str(&format!("  {}: {}", serde_json::to_string(k)?, serde_json::to_string(v)?));
    }
    // entries straight from the structs: `name`, `id`, `guid` lead each line (a Value would sort the keys)
    fn block<T: Serialize>(s: &mut String, key: &str, m: &BTreeMap<String, Vec<T>>) -> Result<()> {
        s.push_str(&format!("  \"{key}\": {{"));
        for (i, (name, list)) in m.iter().enumerate() {
            s.push_str(if i == 0 { "\n" } else { ",\n" });
            s.push_str(&format!("    {}: [", serde_json::to_string(name)?));
            for (j, e) in list.iter().enumerate() {
                s.push_str(if j == 0 { "\n      " } else { ",\n      " });
                s.push_str(&serde_json::to_string(e)?);
            }
            s.push_str(if list.is_empty() { "]" } else { "\n    ]" });
        }
        s.push_str(if m.is_empty() { "}" } else { "\n  }" });
        Ok(())
    }
    let _ = (sections, fields);
    sep(&mut s);
    block(&mut s, "sections", &cat.sections)?;
    if !cat.fields.is_empty() {
        sep(&mut s);
        block(&mut s, "fields", &cat.fields)?;
    }
    s.push_str("\n}\n");
    Ok(s)
}

/// `0x%08x`.
pub fn hex_id(v: u32) -> String {
    format!("0x{v:08x}")
}
