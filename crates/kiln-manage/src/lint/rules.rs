//! The checks beyond the schemas: definitions and references across files and mods, the manifest against the
//! content, assets, cook recipes, natives, schematics and licences. Each finding carries a code of codes.rs.

use super::catalog::{Catalog, parse_id};
use super::json::{Node, ptr_seg};
use super::schema::Annot;
use super::{Diagnostic, Fix, LFile, LMod, Linter, Severity, Span, TextEdit, codes, display, suggest};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

// ------------------------------------------------------------------------------------------------- helpers ----

/// The GUID's 16 bytes in the game's resource-id order (.NET Guid.ToByteArray()).
pub fn guid_bytes(s: &str) -> Option<[u8; 16]> {
    let hex: String = s.chars().filter(|c| *c != '-' && *c != '{' && *c != '}').collect();
    if hex.len() != 32 || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let mut b = [0u8; 16];
    for (i, x) in b.iter_mut().enumerate() {
        *x = u8::from_str_radix(&hex[i * 2..i * 2 + 2], 16).ok()?;
    }
    Some([b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]])
}

pub fn fnv1a(b: &[u8]) -> u32 {
    let mut h: u32 = 0x811c9dc5;
    for x in b {
        h ^= u32::from(*x);
        h = h.wrapping_mul(0x01000193);
    }
    h
}

/// The id the game (and Kiln) give a definition: FNV-1a over the GUID's bytes.
pub fn id_of_guid(s: &str) -> Option<u32> {
    guid_bytes(s).map(|b| fnv1a(&b))
}

fn is_guid(s: &str) -> bool {
    let b = s.as_bytes();
    b.len() == 36 && [8, 13, 18, 23].iter().all(|&i| b[i] == b'-') && b.iter().enumerate().all(|(i, c)| [8, 13, 18, 23].contains(&i) || c.is_ascii_hexdigit())
}

fn quote(s: &str) -> String {
    serde_json::to_string(s).unwrap_or_else(|_| format!("\"{s}\""))
}

/// Reads the JSON header out of a .kschem file: (start, end) byte range, or the problem.
pub fn kschem_header(b: &[u8]) -> Result<(usize, usize), (Span, String)> {
    const MAGIC: &[u8] = b"KSCHEM\r\n";
    let u32_at = |o: usize| b.get(o..o + 4).map(|x| u32::from_le_bytes([x[0], x[1], x[2], x[3]]));
    if !b.starts_with(MAGIC) {
        return Err((Span::new(0, b.len().min(8)), "not a .kschem file (the magic KSCHEM\\r\\n is missing)".into()));
    }
    let v = u32_at(8).ok_or((Span::new(8, b.len()), "cut short before the version".to_string()))?;
    if v != 1 {
        return Err((Span::new(8, 12), format!(".kschem version {v} (this Kiln reads 1)")));
    }
    let h = u32_at(12).ok_or((Span::new(12, b.len()), "cut short before the header length".to_string()))? as usize;
    if h > 64 << 20 || 16 + h > b.len() {
        return Err((Span::new(12, 16), format!("header length {h} runs past the end of the file ({} bytes)", b.len())));
    }
    let c = u32_at(16 + h).ok_or((Span::new(16 + h, b.len()), "cut short before the cell data length".to_string()))? as usize;
    if 20 + h + c > b.len() {
        return Err((Span::new(16 + h, 20 + h), format!("cell data length {c} runs past the end of the file")));
    }
    Ok((16, 16 + h))
}

struct Out<'a> {
    diags: Vec<Diagnostic>,
    mods: &'a [LMod],
}

impl Out<'_> {
    fn push(&mut self, d: Diagnostic) {
        self.diags.push(d);
    }
}

fn manifest_root(m: &LMod) -> Option<&Node> {
    m.manifest().and_then(|f| f.root.as_ref())
}

/// side and clients as Kiln reads them (defaults from side).
fn side_clients(m: &LMod) -> (String, String, bool) {
    let r = manifest_root(m);
    let default_side = if m.native { "client" } else { "server" };
    let side = r.and_then(|r| r.str_of("side")).unwrap_or(default_side).to_string();
    let explicit = r.and_then(|r| r.str_of("clients"));
    let clients = explicit.map(str::to_string).unwrap_or_else(|| if side == "server" { "none".into() } else { "mod".into() });
    (side, clients, explicit.is_some())
}

fn deps_of(m: &LMod) -> HashSet<String> {
    let mut s = HashSet::new();
    let Some(r) = manifest_root(m) else { return s };
    if let Some(d) = r.get("dependencies") {
        if let Some(o) = d.as_object() {
            s.extend(o.iter().map(|x| x.key.clone()));
        }
        for e in d.as_array().into_iter().flatten() {
            if let Some(id) = e.str_of("id") {
                s.insert(id.to_string());
            }
        }
    }
    for k in ["loadAfter", "requires", "after"] {
        for e in r.get(k).and_then(Node::as_array).into_iter().flatten() {
            if let Some(id) = e.as_str() {
                s.insert(id.to_string());
            }
        }
    }
    s
}

// ------------------------------------------------------------------------------------------------- entry ----

pub(crate) fn run(l: &mut Linter) {
    let schema_errors: HashSet<String> = l.diags.iter().filter(|d| d.severity == Severity::Error && (d.code.starts_with("KL0") || d.code.starts_with("KL1"))).map(|d| d.file.clone()).collect();
    let mut out = Out { diags: vec![], mods: &l.mods };
    let cat = l.catalog.as_ref();
    let defs = Defs::collect(&l.mods);
    let code = Code::collect(&l.mods);
    let mut skipped_refs = 0usize;
    for (mi, m) in l.mods.iter().enumerate() {
        if !m.report {
            continue;
        }
        manifest_rules(&mut out, m, &defs, mi);
        manifest_reader(&mut out, m, &schema_errors);
        for f in &m.files {
            skipped_refs += refs(&mut out, m, mi, f, cat, &defs, &code);
            files(&mut out, Some(m), f);
            fields(&mut out, f, cat);
            match f.kind.as_str() {
                "assets" => assets(&mut out, m, f, cat),
                "cook" => cook(&mut out, m, f),
                "natives" => natives(&mut out, m, f),
                "schematic" => schematic(&mut out, f),
                "skills" => skills(&mut out, f),
                _ => {}
            }
        }
        crafted_rolled(&mut out, m, cat);
        quests(&mut out, m);
        blocks(&mut out, m, &code, cat);
        skipped_refs += vfx(&mut out, m, cat);
        surfaces(&mut out, m);
        quests_native(&mut out, m);
        licences(&mut out, m);
        locale_keys(&mut out, &mut l.texts, m);
    }
    for f in &l.loose {
        let alone = LMod::empty();
        skipped_refs += refs(&mut out, &alone, usize::MAX, f, cat, &defs, &code);
        files(&mut out, None, f);
        match f.kind.as_str() {
            "cook" => cook(&mut out, &alone, f),
            "natives" => natives(&mut out, &alone, f),
            "schematic" => schematic(&mut out, f),
            "skills" => skills(&mut out, f),
            _ => {}
        }
    }
    defs.check(&mut out, cat);
    profile(&mut out);
    let first = l.mods.iter().find(|m| m.report).and_then(|m| m.manifest().or(m.files.first())).or(l.loose.first()).map(|f| f.display.clone());
    if let Some(file) = first {
        if let Some(p) = &l.catalog_problem {
            out.push(Diagnostic::new(&file, codes::CATALOG_BAD, Span::default(), &format!("the game-data catalog cannot be used: {p}")));
        }
        if cat.is_none() && skipped_refs > 0 {
            out.push(Diagnostic::new(&file, codes::NO_CATALOG, Span::default(), &format!(
                "no game-data catalog: {skipped_refs} reference(s) to the game's items, templates, ... were not checked (make one with `kiln catalog`, or pass --catalog)"
            )));
        }
    }
    let diags = out.diags;
    // a data-pack section a mod's code registers (Content.RegisterSection("x", ...)) is not unknown
    l.diags.retain(|d| {
        let section = d.pointer.as_deref().and_then(|p| p.strip_prefix('/')).filter(|p| !p.contains('/'));
        !(d.code == codes::UNKNOWN_KEY && d.schema.as_deref() == Some("content-pack.schema.json") && section.is_some_and(|s| code.mentions(s)))
    });
    l.diags.extend(diags);
}

impl LMod {
    fn empty() -> LMod {
        LMod { root: PathBuf::new(), display: String::new(), id: None, native: false, report: true, files: vec![] }
    }
}

// --------------------------------------------------------------------------------------------- definitions ----

#[derive(Clone)]
struct DefRec {
    m: usize,
    file: String,
    ns: String,
    value: String,
    span: Span,
}

/// Every GUID and name the mods define (Def / DefName annotations), with derived ids.
struct Defs {
    guids: Vec<DefRec>,
    names: Vec<DefRec>,
    /// ns -> name -> mods
    by_name: HashMap<String, HashMap<String, Vec<usize>>>,
    by_guid: HashMap<String, HashMap<String, Vec<usize>>>,
    by_id: HashMap<String, HashMap<u32, Vec<usize>>>,
    /// Every GUID a mod makes exist (definitions, assets.json resources, cook outputs): for "resources" references.
    resources: HashMap<String, Vec<usize>>,
}

/// GUID namespaces that share one id space at run time.
fn guid_group(ns: &str) -> &str {
    match ns {
        "templates" | "sequences" | "perks" | "buffs" => "gamedata",
        x => x,
    }
}

impl Defs {
    fn collect(mods: &[LMod]) -> Defs {
        let mut d = Defs { guids: vec![], names: vec![], by_name: HashMap::new(), by_guid: HashMap::new(), by_id: HashMap::new(), resources: HashMap::new() };
        for (mi, m) in mods.iter().enumerate() {
            for f in &m.files {
                for a in &f.annots {
                    match a {
                        Annot::Def { ns, value, span, .. } => {
                            let g = value.to_ascii_lowercase();
                            d.by_guid.entry(ns.clone()).or_default().entry(g.clone()).or_default().push(mi);
                            if let Some(id) = id_of_guid(&g) {
                                d.by_id.entry(ns.clone()).or_default().entry(id).or_default().push(mi);
                            }
                            d.resources.entry(g.clone()).or_default().push(mi);
                            d.guids.push(DefRec { m: mi, file: f.display.clone(), ns: ns.clone(), value: g, span: *span });
                        }
                        Annot::DefName { ns, value, span, .. } => {
                            d.by_name.entry(ns.clone()).or_default().entry(value.clone()).or_default().push(mi);
                            d.names.push(DefRec { m: mi, file: f.display.clone(), ns: ns.clone(), value: value.clone(), span: *span });
                        }
                        _ => {}
                    }
                }
                // resources a mod serves or cooks
                if let Some(r) = &f.root {
                    match f.kind.as_str() {
                        "assets" => {
                            for e in r.get("entries").and_then(Node::as_array).into_iter().flatten() {
                                if let Some(g) = e.str_of("guid") {
                                    d.resources.entry(g.to_ascii_lowercase()).or_default().push(mi);
                                }
                            }
                        }
                        "cook" => {
                            for s in r.get("steps").and_then(Node::as_array).into_iter().flatten() {
                                if let Some(g) = s.str_of("guid").filter(|g| is_guid(g)) {
                                    d.resources.entry(g.to_ascii_lowercase()).or_default().push(mi);
                                }
                            }
                        }
                        _ => {}
                    }
                }
            }
        }
        d
    }

    fn check(&self, out: &mut Out, cat: Option<&Catalog>) {
        // GUIDs: twice in a mod, in two mods, the game's own, id collisions
        let mut seen: HashMap<(String, String), &DefRec> = HashMap::new();
        let mut ids: HashMap<(String, u32), &DefRec> = HashMap::new();
        for r in &self.guids {
            let reported = out.mods[r.m].report;
            let key = (guid_group(&r.ns).to_string(), r.value.clone());
            if let Some(first) = seen.get(&key) {
                if reported {
                    let (code, msg) = if first.m == r.m {
                        (codes::DUPLICATE_GUID, format!("GUID {} is defined twice in this mod (the later definition replaces the earlier one)", r.value))
                    } else {
                        (codes::PROFILE_DUPLICATE_GUID, format!("GUID {} is also defined by mod {}: one definition replaces the other", r.value, mod_name(out.mods, first.m)))
                    };
                    out.push(Diagnostic::new(&r.file, code, r.span, &msg).with_related(&first.file, first.span, "defined here first"));
                }
                continue;
            }
            seen.insert(key, r);
            if let Some(id) = id_of_guid(&r.value) {
                let k = (guid_group(&r.ns).to_string(), id);
                if let Some(first) = ids.get(&k) {
                    if reported && first.value != r.value {
                        out.push(
                            Diagnostic::new(&r.file, codes::ID_COLLISION, r.span, &format!("GUID {} hashes to id 0x{id:08x}, like {}: Kiln refuses the second one (pick a new GUID)", r.value, first.value))
                                .with_related(&first.file, first.span, "the other GUID"),
                        );
                    }
                } else {
                    ids.insert(k, r);
                }
                if reported
                    && let Some(s) = cat.and_then(|c| c.section(&r.ns))
                {
                    if s.guids.contains(&r.value) {
                        out.push(Diagnostic::new(&r.file, codes::GAME_GUID, r.span, &format!("GUID {} is the game's own {} (a definition needs a new GUID)", r.value, singular(&r.ns))));
                    } else if s.ids.contains(&id) {
                        out.push(Diagnostic::new(&r.file, codes::GAME_GUID, r.span, &format!("GUID {} hashes to id 0x{id:08x}, a game {}'s id: Kiln refuses it for every world (pick a new GUID)", r.value, singular(&r.ns))));
                    }
                }
            }
        }
        // names: twice, in two mods, the game's
        let mut seen: HashMap<(String, String), &DefRec> = HashMap::new();
        for r in &self.names {
            let reported = out.mods[r.m].report;
            let key = (r.ns.clone(), r.value.clone());
            if let Some(first) = seen.get(&key) {
                if reported {
                    let sev = if r.ns == "variants" { Severity::Warning } else { Severity::Error };
                    let (code, msg) = if first.m == r.m {
                        (codes::DUPLICATE_NAME, format!("{} \"{}\" is defined twice in this mod", name_kind(&r.ns), r.value))
                    } else {
                        (codes::PROFILE_DUPLICATE_NAME, format!("{} \"{}\" is also defined by mod {}", name_kind(&r.ns), r.value, mod_name(out.mods, first.m)))
                    };
                    out.push(Diagnostic::new(&r.file, code, r.span, &msg).severity(sev).with_related(&first.file, first.span, "defined here first"));
                }
                continue;
            }
            seen.insert(key, r);
            if reported
                && let Some(s) = cat.and_then(|c| c.section(&r.ns))
                && s.names.contains(&r.value)
            {
                let sev = if matches!(r.ns.as_str(), "items" | "recipes") { Severity::Error } else { Severity::Warning };
                out.push(
                    Diagnostic::new(&r.file, codes::GAME_NAME, r.span, &format!("\"{}\" is the name of a game {}: {}", r.value, singular(&r.ns), if sev == Severity::Error { "Kiln refuses the definition (use a prefix of your own, e.g. MyMod_...)" } else { "selectors that name it become ambiguous" }))
                        .severity(sev),
                );
            }
        }
    }
}

/// KL406: a recipe makes an item that rolls its rarity or level. The game's recipe tooltip shows `rarity` and the lowest
/// level; a craft passes neither to the game, so such an item comes out different (docs/re/items.md section 10). New items
/// are fixed by default, so this finds the opt-ins and the game's own loot weapons. Code definitions: kiln.log at run time.
fn crafted_rolled(out: &mut Out, m: &LMod, cat: Option<&Catalog>) {
    let content: Vec<&LFile> = m.files.iter().filter(|f| f.kind == "content").collect();
    let mut rolled: HashMap<String, String> = HashMap::new();
    let mut fixed_rule: HashSet<String> = HashSet::new();
    for f in &content {
        let Some(r) = &f.root else { continue };
        for it in r.pointer("/items/add").and_then(Node::as_array).into_iter().flatten() {
            let Some(name) = it.str_of("name") else { continue };
            let fields = it.get("fields");
            let num = |k: &str| fields.and_then(|x| x.get(k)).and_then(Node::as_f64);
            let mut why = vec![];
            if it.get("rolledLoot").and_then(Node::as_bool) == Some(true) {
                why.push("\"rolledLoot\": true");
            }
            if fields.and_then(|x| x.get("generateRarity")).and_then(Node::as_bool) == Some(true) {
                why.push("\"generateRarity\": true");
            }
            if let Some(hi) = num("itemLevelRange.maxLevel")
                && num("itemLevelRange.minLevel") != Some(hi)
            {
                why.push("a level range");
            }
            if !why.is_empty() {
                rolled.insert(name.to_ascii_lowercase(), why.join(", "));
            }
        }
        // a rule that turns a game item's rolling off answers the warning
        for mem in r.get("items").and_then(Node::as_object).into_iter().flatten() {
            if mem.value.get("generateRarity").and_then(Node::as_bool) == Some(false) {
                fixed_rule.insert(mem.key.to_ascii_lowercase());
            }
        }
    }
    let game = cat.and_then(|c| c.section("items"));
    for f in &content {
        let Some(r) = &f.root else { continue };
        let mut recipes: Vec<(String, &[Node])> = vec![];
        for rec in r.pointer("/recipes/add").and_then(Node::as_array).into_iter().flatten() {
            if let Some(outs) = rec.get("outputs").and_then(Node::as_array) {
                recipes.push((rec.str_of("name").unwrap_or("?").to_string(), outs));
            }
        }
        for mem in r.get("recipes").and_then(Node::as_object).into_iter().flatten() {
            if let Some(outs) = mem.value.get("outputs").and_then(Node::as_array) {
                recipes.push((mem.key.clone(), outs));
            }
        }
        for (recipe, outs) in recipes {
            for o in outs {
                let Some(item) = o.get("item").filter(|i| i.as_str().is_some()) else { continue };
                let name = item.as_str().unwrap_or("");
                let lower = name.to_ascii_lowercase();
                if fixed_rule.contains(&lower) {
                    continue;
                }
                let msg = if let Some(why) = rolled.get(&lower) {
                    format!("recipe \"{recipe}\" makes \"{name}\", which rolls ({why}): a craft comes out as the lowest allowed rarity or a random level while the recipe tooltip shows its rarity and lowest level; remove that for a fixed item")
                } else if game.is_some_and(|g| g.generated_rarity.contains(name)) {
                    format!("recipe \"{recipe}\" makes the game's loot item \"{name}\", which keeps generateRarity: a craft comes out as the lowest allowed rarity at a random level; add a new item based on it (fixed by default), or a rule with \"generateRarity\": false and one level")
                } else {
                    continue;
                };
                out.push(Diagnostic::new(&f.display, codes::CRAFTED_ROLLED, item.span, &msg));
            }
        }
    }
}

fn singular(ns: &str) -> &str {
    match ns {
        "items" => "item",
        "recipes" => "recipe",
        "templates" => "template",
        "sequences" => "sequence",
        "perks" => "perk",
        "buffs" => "buff",
        "resources" => "resource",
        "variants" => "variant",
        "natives" => "native",
        "skillNodes" => "skill-tree node",
        "talentTrees" => "talent tree",
        "drops" => "drop table",
        "skills" => "skill node",
        "arsenals" => "arsenal",
        "workshops" => "workshop",
        "itemCategories" => "item category",
        "loot" => "random-loot item",
        x => x,
    }
}

fn name_kind(ns: &str) -> String {
    match ns {
        "variants" => "variant id".into(),
        "natives" => "native".into(),
        x => format!("{} name", singular(x)),
    }
}

fn mod_name(mods: &[LMod], i: usize) -> String {
    mods.get(i).map(|m| m.id.clone().unwrap_or_else(|| m.display.clone())).unwrap_or_default()
}

/// What the mods' code may define: the text of their C# sources and the UTF-16 strings of their assemblies. A name
/// that only code registers (Content.Items.Register(...)) is not reported as unknown.
struct Code {
    sources: String,
    assemblies: Vec<Vec<u8>>,
}

impl Code {
    fn collect(mods: &[LMod]) -> Code {
        let mut c = Code { sources: String::new(), assemblies: vec![] };
        for m in mods {
            let mut all = Vec::new();
            walk_files(&m.root, 0, &mut all);
            for p in all {
                match p.extension().and_then(|x| x.to_str()).map(str::to_ascii_lowercase).as_deref() {
                    Some("cs") => {
                        if let Ok(t) = std::fs::read_to_string(&p) {
                            c.sources.push_str(&t);
                            c.sources.push('\n');
                        }
                    }
                    Some("dll") if p.metadata().is_ok_and(|x| x.len() < 32 << 20) => {
                        if let Ok(b) = std::fs::read(&p) {
                            c.assemblies.push(b);
                        }
                    }
                    _ => {}
                }
            }
        }
        c
    }

    fn mentions(&self, name: &str) -> bool {
        if name.len() < 4 {
            return false;
        }
        if self.sources.contains(&format!("\"{name}\"")) {
            return true;
        }
        let w: Vec<u8> = name.encode_utf16().flat_map(u16::to_le_bytes).collect();
        self.assemblies.iter().any(|a| a.windows(w.len()).any(|x| x == w.as_slice()))
    }
}

// ------------------------------------------------------------------------------------------------- locales ----

/// The dotted keys of a locale file: `{ "shop": { "buy": "Buy" } }` is `shop.buy` (Kiln.Api's `ModLocale` flattens it the same way). Strings, numbers and
/// booleans are texts; anything else is not a text and ModLocale ignores it.
fn locale_flatten(members: &[super::json::Member], prefix: &str, keys: &mut Vec<(String, Span)>) {
    for mem in members {
        let key = if prefix.is_empty() { mem.key.clone() } else { format!("{prefix}.{}", mem.key) };
        match &mem.value.value {
            super::json::Value::Object(inner) => locale_flatten(inner, &key, keys),
            super::json::Value::String(_) | super::json::Value::Number(..) | super::json::Value::Bool(_) => keys.push((key, mem.key_span)),
            _ => {}
        }
    }
}

/// KL212 over one mod: the files of `locales/` (`<language>.json`, a mod's texts for `Mod.Text`) against the English one. A player whose language lacks a key
/// reads English for it, so a gap is a warning: a translation can be late, but it should be known. A key only a translation has is a hint (a typo, or the English
/// file forgot it). A regional file (`pt-BR.json`) that sits beside its main language's (`pt.json`) only has to hold what differs: its gaps are the main file's too.
fn locale_keys(out: &mut Out, texts: &mut HashMap<String, Vec<u8>>, m: &LMod) {
    let Ok(rd) = std::fs::read_dir(m.root.join("locales")) else { return };
    let mut paths: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
    paths.sort();
    struct Loc {
        tag: String,
        display: String,
        root: Span,
        keys: Vec<(String, Span)>,
    }
    let mut locs: Vec<Loc> = vec![];
    for p in paths {
        let tag = p.file_stem().map(|s| s.to_string_lossy().to_lowercase().replace('_', "-")).unwrap_or_default();
        let Ok(bytes) = std::fs::read(&p) else { continue };
        let d = display(&p);
        texts.insert(d.clone(), bytes.clone());
        let Ok(text) = std::str::from_utf8(&bytes) else {
            out.push(Diagnostic::new(&d, codes::NOT_UTF8, Span::new(0, 1), "not valid UTF-8 (Kiln's readers take UTF-8)"));
            continue;
        };
        let parsed = super::json::parse(&d, text, 0, super::json::Dialect::JSONC);
        for x in parsed.diagnostics {
            out.push(x);
        }
        let Some(root) = parsed.root else { continue };
        let Some(members) = root.as_object() else {
            out.push(Diagnostic::new(&d, codes::TYPE, root.span, "a locale file holds one { ... } object of texts"));
            continue;
        };
        let mut keys = vec![];
        locale_flatten(members, "", &mut keys);
        locs.push(Loc { tag, display: d, root: Span::new(root.span.start, root.span.start + 1), keys });
    }
    if locs.is_empty() {
        return;
    }
    let english = locs.iter().find(|l| l.tag == "en").or_else(|| locs.iter().find(|l| l.tag == "en-us"));
    let Some(english) = english else {
        let first = &locs[0];
        out.push(Diagnostic::new(&first.display, codes::LOCALE_KEY, first.root, "this mod's locales/ has no en.json: a language without a text of its own falls back to English, so without it a player sees the key itself"));
        return;
    };
    let eng: HashSet<&str> = english.keys.iter().map(|(k, _)| k.as_str()).collect();
    let eng_list: Vec<&str> = english.keys.iter().map(|(k, _)| k.as_str()).collect();
    for l in locs.iter().filter(|l| !std::ptr::eq(*l, english) && l.tag != "en" && !l.tag.starts_with("en-")) {
        // what this language has: its own keys, and its main language's file when it is a regional one
        let mut have: HashSet<&str> = l.keys.iter().map(|(k, _)| k.as_str()).collect();
        if let Some((primary, _)) = l.tag.split_once('-')
            && let Some(main) = locs.iter().find(|x| x.tag == primary)
        {
            have.extend(main.keys.iter().map(|(k, _)| k.as_str()));
        }
        let missing: Vec<&str> = eng_list.iter().copied().filter(|k| !have.contains(k)).collect();
        if !missing.is_empty() {
            let shown: Vec<&str> = missing.iter().copied().take(5).collect();
            let more = if missing.len() > 5 { format!(" and {} more", missing.len() - 5) } else { String::new() };
            out.push(Diagnostic::new(
                &l.display,
                codes::LOCALE_KEY,
                l.root,
                &format!("{}.json has no text for {} key(s) that en.json has: {}{more}; a player in this language reads English for {}", l.tag, missing.len(), shown.join(", "), if missing.len() == 1 { "it" } else { "them" }),
            ));
        }
        for (key, span) in l.keys.iter().filter(|(k, _)| !eng.contains(k.as_str())) {
            let mut d = Diagnostic::new(&l.display, codes::LOCALE_KEY, *span, &format!("\"{key}\": en.json has no such key (a typo, or the English text is missing)")).severity(Severity::Hint);
            if let Some(sug) = suggest::best(key, &eng_list) {
                d.message.push_str(&format!("; did you mean \"{sug}\"?"));
            }
            out.push(d);
        }
    }
}

// --------------------------------------------------------------------------------------------- references ----

enum Form {
    Name(String),
    Guid(String),
    Id(u32),
    Glob(String),
    Lang(String),
    Skip,
}

fn classify(value: &str, forms: &[String], section: &str) -> (String, Form) {
    let has = |f: &str| forms.iter().any(|x| x == f);
    let sec = section.to_string();
    if section == "languages" || has("lang") {
        return ("languages".into(), if value == "*" { Form::Skip } else { Form::Lang(value.to_ascii_lowercase().replace('_', "-")) });
    }
    if matches!(value, "none" | "hand" | "*" | "add") {
        return (sec, Form::Skip);
    }
    for (prefix, target) in [("item:", "items"), ("like:", "items"), ("template:", "templates")] {
        if let Some(rest) = value.strip_prefix(prefix) {
            let name = rest.split('.').next().unwrap_or(rest).to_string();
            return (target.into(), if suggest::has_wild(&name) { Form::Glob(name) } else { Form::Name(name) });
        }
    }
    for prefix in ["buff_", "skill_"] {
        if let Some(h) = value.strip_prefix(prefix)
            && let Ok(id) = u32::from_str_radix(h, 16)
        {
            return (sec, Form::Id(id));
        }
    }
    if let Some((g, k)) = value.split_once('#')
        && is_guid(g)
        && k.bytes().all(|b| b.is_ascii_digit())
    {
        return (sec, Form::Guid(g.to_ascii_lowercase()));
    }
    if suggest::has_wild(value) {
        return (sec, Form::Glob(value.to_string()));
    }
    if is_guid(value) {
        return (sec, Form::Guid(value.to_ascii_lowercase()));
    }
    if (value.starts_with("0x") || value.starts_with("0X") || value.bytes().all(|b| b.is_ascii_digit()))
        && let Some(id) = parse_id(value)
    {
        return (sec, Form::Id(id));
    }
    (sec, Form::Name(value.to_string()))
}

/// Sections whose entries only mods define (no catalog needed).
fn local_only(section: &str) -> bool {
    matches!(section, "variants" | "natives" | "skillNodes" | "talentTrees")
}

/// Checks the references of one file. Returns how many could not be checked for want of a catalog.
fn refs(out: &mut Out, m: &LMod, mi: usize, f: &LFile, cat: Option<&Catalog>, defs: &Defs, code: &Code) -> usize {
    let mut skipped = 0;
    let deps = deps_of(m);
    for a in &f.annots {
        let Annot::Ref { section, forms, missing, value, span, pointer } = a else { continue };
        let (section, form) = classify(value, forms, section);
        let csec = cat.and_then(|c| c.section(&section));
        // local definitions of this section: which mods define it
        let local: Option<Vec<usize>> = match &form {
            Form::Name(n) => defs.by_name.get(&section).and_then(|x| x.get(n)).cloned(),
            Form::Guid(g) => {
                if section == "resources" {
                    defs.resources.get(g).cloned()
                } else {
                    defs.by_guid.get(&section).and_then(|x| x.get(g)).cloned()
                }
            }
            Form::Id(id) => defs.by_id.get(&section).and_then(|x| x.get(id)).cloned(),
            Form::Glob(p) => {
                let v: Vec<usize> = defs.by_name.get(&section).into_iter().flat_map(|x| x.iter()).filter(|(n, _)| suggest::glob_ci(p, n)).flat_map(|(_, ms)| ms.iter().copied()).collect();
                if v.is_empty() { None } else { Some(v) }
            }
            _ => None,
        };
        let in_game = match (&form, csec) {
            (Form::Skip, _) => Some(true),
            (Form::Lang(k), _) => {
                let langs = cat.map(|c| &c.languages).filter(|l| !l.is_empty());
                match langs {
                    None => Some(true),
                    Some(ls) => Some(ls.iter().any(|g| g == k || g.split('-').next() == Some(k.as_str()) || k.split('-').next() == Some(g.as_str()))),
                }
            }
            (_, None) => None,
            (Form::Name(n), Some(s)) => {
                if s.names.contains(n) || (section == "templates" && s.display.contains(n)) {
                    Some(true)
                } else if let Some(exact) = s.lower.get(&n.to_ascii_lowercase()) {
                    // Kiln's registries match names without regard to case
                    let new = value.replacen(n.as_str(), exact, 1);
                    out.push(
                        Diagnostic::new(&f.display, codes::CASE, *span, &format!("\"{n}\" matches the game's \"{exact}\" (Kiln ignores case); write it as the game names it"))
                            .pointer(pointer)
                            .with_fix(Fix::new(&format!("Write \"{exact}\""), vec![TextEdit::new(&f.display, *span, &quote(&new))])),
                    );
                    Some(true)
                } else {
                    Some(false)
                }
            }
            (Form::Guid(g), Some(s)) => Some(s.guids.contains(g) || (section == "resources" && cat.is_some_and(|c| c.sections.values().any(|x| x.guids.contains(g))))),
            (Form::Id(id), Some(s)) => Some(s.ids.contains(id)),
            (Form::Glob(p), Some(s)) => Some(if section == "drops" { s.guids.iter().any(|g| suggest::glob_ci(p, g)) } else { s.names_list.iter().any(|n| suggest::glob_ci(p, n)) }),
        };
        if let Some(ms) = &local {
            // defined by a mod: fine when it is this mod or one it depends on
            if in_game != Some(true) && m.report && mi != usize::MAX && !ms.contains(&mi) && !ms.iter().any(|o| out.mods[*o].id.as_ref().is_some_and(|id| deps.contains(id))) {
                let other = mod_name(out.mods, ms[0]);
                let mf = m.manifest().map(|x| x.display.clone()).unwrap_or_else(|| f.display.clone());
                out.push(
                    Diagnostic::new(&f.display, codes::UNDECLARED_DEPENDENCY, *span, &format!("{value} comes from mod {other}, which this mod does not depend on (rules apply in load order: add \"{other}\" to dependencies or loadAfter)"))
                        .pointer(pointer)
                        .with_related(&mf, Span::default(), "this mod's manifest"),
                );
            }
            continue;
        }
        // a name the mod's (or another mod's) code registers: C# sources or the built assemblies mention it
        if let Form::Name(n) = &form
            && in_game != Some(true)
            && code.mentions(n)
        {
            continue;
        }
        match in_game {
            Some(true) => continue,
            None if !local_only(&section) => {
                skipped += 1;
                continue;
            }
            _ => {}
        }
        // not found
        let what = singular(&section);
        let mut d = match &form {
            Form::Glob(p) => Diagnostic::new(&f.display, codes::GLOB_EMPTY, *span, &format!("\"{p}\" matches no {what} (Kiln warns \"matches nothing\")")),
            Form::Lang(k) => Diagnostic::new(&f.display, codes::UNKNOWN_LANGUAGE, *span, &format!("no game language is \"{k}\" (the game has {}): this text never shows", cat.map(|c| c.languages.join(", ")).unwrap_or_default())),
            Form::Name(n) => {
                let mut d = Diagnostic::new(&f.display, codes::UNKNOWN_REF, *span, &format!("no {what} \"{n}\" in the game{}", if local_only(&section) { "" } else { " or the mods" })).severity(*missing);
                let mut cands: Vec<&str> = csec.map(|s| s.names_list.iter().map(String::as_str).collect()).unwrap_or_default();
                if let Some(x) = defs.by_name.get(&section) {
                    cands.extend(x.keys().map(String::as_str));
                }
                if local_only(&section) {
                    d.message = format!("no {what} \"{n}\" is defined");
                }
                if let Some(s) = suggest::best(n, &cands) {
                    d.message.push_str(&format!("; did you mean \"{s}\"?"));
                    let new = value.replacen(n.as_str(), s, 1);
                    d.fixes.push(Fix::new(&format!("Replace with \"{s}\""), vec![TextEdit::new(&f.display, *span, &quote(&new))]));
                }
                d
            }
            Form::Guid(g) => Diagnostic::new(&f.display, codes::UNKNOWN_REF, *span, &format!("no {what} with GUID {g} in the game or the mods")).severity(*missing),
            Form::Id(id) => Diagnostic::new(&f.display, codes::UNKNOWN_REF, *span, &format!("no {what} with id 0x{id:08x} in the game or the mods")).severity(*missing),
            Form::Skip => continue,
        };
        d = d.pointer(pointer);
        out.push(d);
    }
    skipped
}

fn fields(out: &mut Out, f: &LFile, cat: Option<&Catalog>) {
    let Some(c) = cat else { return };
    for a in &f.annots {
        let Annot::FieldKey { section, key, span, pointer, value } = a else { continue };
        if let (Some(fd), Some(v)) = (c.field(section, key), value)
            && !pointer.contains("/@where/")
        {
            field_value(out, f, fd, v, pointer);
        }
        let first = key.split(['.', '[']).next().unwrap_or(key);
        // kiln.gamedata's paths on top of the reflection: config values by alias / 0x id, tooltip values, item lists
        let gamedata_path = matches!(first, "impactValues" | "configValues" | "uiValues" | "sequences" | "ImpactConfiguration")
            && key.len() > first.len();
        let known = match c.field_known(section, key) {
            Some(_) if gamedata_path => true,
            Some(k) => k,
            None if section == "templates" => {
                // without a template field list: a component type name must be one some template has
                let comps = c.section("templates").map(|s| &s.components).filter(|s| !s.is_empty());
                let first = key.split(['.', '[', '#']).next().unwrap_or(key);
                match comps {
                    Some(cs) if first.starts_with(|ch: char| ch.is_ascii_uppercase()) => cs.contains(first),
                    _ => true,
                }
            }
            None => true,
        };
        if !known {
            let mut d = Diagnostic::new(&f.display, codes::UNKNOWN_FIELD, *span, &format!("\"{key}\": no such {} field in the game's reflection (Kiln reports \"unknown field\")", singular(section))).pointer(pointer);
            let comp_sug = || {
                let first = key.split(['.', '[', '#']).next().unwrap_or(key);
                let comps: Vec<&str> = c.section("templates").map(|s| s.components.iter().map(String::as_str).collect()).unwrap_or_default();
                suggest::best(first, &comps).map(|b| key.replacen(first, b, 1))
            };
            if let Some(s) = c.field_suggestion(section, key).or_else(|| if section == "templates" { comp_sug() } else { None }) {
                d.message.push_str(&format!("; did you mean \"{s}\"?"));
                d.fixes.push(Fix::new(&format!("Rename to \"{s}\""), vec![TextEdit::new(&f.display, *span, &quote(&s))]));
            }
            out.push(d);
        }
    }
}

/// A value against the field's reflected type (when the catalog lists it): numbers for numeric fields, the enum's
/// names for enums and flag sets.
fn field_value(out: &mut Out, f: &LFile, fd: &super::catalog::Field, v: &Node, pointer: &str) {
    let t = fd.ty.trim_start_matches("keen::").to_ascii_lowercase();
    let numeric = t.starts_with("uint") || t.starts_with("sint") || t.starts_with("int") || t.starts_with("float") || t == "double" || t == "time" || t == "bool";
    match &v.value {
        super::json::Value::String(s) if numeric && fd.enums.is_empty() && !s.starts_with("0x") => {
            out.push(Diagnostic::new(&f.display, codes::TYPE, v.span, &format!("{} is a {}: expected a number (or an op like {{\"mul\": 2}}), found \"{s}\"", fd.path, fd.ty)).severity(Severity::Warning).pointer(pointer));
        }
        super::json::Value::String(s) if !fd.enums.is_empty() => {
            let names: Vec<&str> = fd.enums.iter().map(String::as_str).collect();
            let bare = s.trim_start_matches(['+', '-', '!']);
            if !names.iter().any(|n| n.eq_ignore_ascii_case(bare)) {
                let mut d = Diagnostic::new(&f.display, codes::ENUM, v.span, &format!("\"{s}\" is not a value of {} ({})", fd.ty, names.iter().take(12).copied().collect::<Vec<_>>().join(", "))).severity(Severity::Warning).pointer(pointer);
                if let Some(b) = suggest::best(bare, &names) {
                    d.message.push_str(&format!("; did you mean \"{b}\"?"));
                    d.fixes.push(Fix::new(&format!("Replace with \"{b}\""), vec![TextEdit::new(&f.display, v.span, &quote(&s.replacen(bare, b, 1)))]));
                }
                out.push(d);
            }
        }
        _ => {}
    }
}

// --------------------------------------------------------------------------------------------- files ----

const PNG: &[u8] = b"\x89PNG\r\n\x1a\n";

fn exists_ci(p: &Path) -> bool {
    if p.exists() {
        return true;
    }
    // Windows (where Kiln runs) does not care about case
    let (Some(dir), Some(name)) = (p.parent(), p.file_name()) else { return false };
    let n = name.to_string_lossy().to_lowercase();
    exists_ci(dir) && std::fs::read_dir(dir).is_ok_and(|rd| rd.flatten().any(|e| e.file_name().to_string_lossy().to_lowercase() == n))
}

/// Outputs the mod's cook recipes write (absolute paths).
fn cook_outputs(m: &LMod) -> Vec<PathBuf> {
    let mut v = Vec::new();
    for f in m.files.iter().filter(|f| f.kind == "cook") {
        let Some(r) = &f.root else { continue };
        let dir = f.path.parent().unwrap_or(Path::new("."));
        let impact = r.str_of("tool") == Some("impact");
        let out = dir.join(r.str_of("out").unwrap_or(if impact { "../assets/cooked/impact" } else { "cooked" }));
        for s in r.get("steps").and_then(Node::as_array).into_iter().flatten() {
            // a worn step's files are named after its model's path in the mod: models/hat.glb -> models_hat
            if s.str_of("op") == Some("worn")
                && let Some(model) = s.str_of("model")
            {
                let p = model.replace('\\', "/");
                let stem = match p.rfind('.') {
                    Some(dot) if dot > p.rfind('/').map_or(0, |x| x + 1) => &p[..dot],
                    _ => p.as_str(),
                }
                .replace('/', "_");
                for ext in ["bin", "blob", "model.json"] {
                    v.push(normalize(&out.join(format!("{stem}.{ext}"))));
                }
                continue;
            }
            if let Some(stem) = s.str_of("stem") {
                v.push(normalize(&out.join(format!("{stem}.bin"))));
                if s.str_of("op") == Some("model") {
                    v.push(normalize(&out.join(format!("{stem}.blob"))));
                }
            }
        }
        v.push(normalize(&out.join("cook-report.json")));
    }
    v
}

fn normalize(p: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in p.components() {
        match c {
            std::path::Component::ParentDir => {
                out.pop();
            }
            std::path::Component::CurDir => {}
            c => out.push(c),
        }
    }
    out
}

fn files(out: &mut Out, m: Option<&LMod>, f: &LFile) {
    let cooked = m.map(cook_outputs).unwrap_or_default();
    let has_cook = m.is_some_and(|m| m.files.iter().any(|x| x.kind == "cook"));
    for a in &f.annots {
        let Annot::File { base, kind, value, span, pointer } = a else { continue };
        let dir = match (base.as_str(), m) {
            ("mod", Some(m)) => m.root.clone(),
            ("assets", Some(m)) => m.root.join("assets"),
            ("assets", None) | ("self", _) | ("mod", None) => f.path.parent().unwrap_or(Path::new(".")).to_path_buf(),
            _ => continue,
        };
        let p = normalize(&dir.join(value.replace('\\', "/")));
        if !exists_ci(&p) {
            let made = cooked.contains(&p) || (has_cook && value.replace('\\', "/").split('/').any(|s| s == "cooked"));
            let d = if made {
                Diagnostic::new(&f.display, codes::COOK_OUTPUT_MISSING, *span, &format!("{value} is made by the cook and is not there yet: run the mod's cook recipe (tools/modelcook, tools/impact, or the launcher at install)"))
            } else {
                Diagnostic::new(&f.display, codes::FILE_MISSING, *span, &format!("{value}: no such file ({})", display(&p)))
            };
            out.push(d.pointer(pointer));
            continue;
        }
        if kind.as_deref() == Some("png")
            && let Ok(mut fh) = std::fs::File::open(&p)
        {
            use std::io::Read;
            let mut head = [0u8; 8];
            let n = fh.read(&mut head).unwrap_or(0);
            if n < 8 || head != PNG {
                out.push(Diagnostic::new(&f.display, codes::NOT_PNG, *span, &format!("{value} is not a PNG file (kiln.assets decodes PNG only)")).pointer(pointer));
            }
        }
    }
}

// --------------------------------------------------------------------------------------------- manifest ----

/// Facts about what a mod contains, for the manifest rules.
struct Contents {
    definition: Option<(String, Span, String)>,
    /// A node added to the game's own skill tree (every client must build the same tree).
    game_nodes: Option<(String, Span)>,
    native_journal: Option<(String, Span)>,
    voice_asset: Option<(String, Span)>,
    /// A Kiln talent tree (vanilla players still reach it through /talents).
    talent_tree: Option<(String, Span)>,
    /// A talent or talent tree block, which Kiln.Blocks refuses below clients "mod" (every player's game builds the tree from the mod's files).
    block_talents: Option<(String, Span)>,
    /// A VFX only the mod's own files can give a client: a block of the `vfx` section, or a `vfx` field that names one.
    new_vfx: Option<(String, Span, &'static str)>,
    client_assets: Option<(String, Span, String)>,
    png_icon: bool,
    anything: bool,
}

fn contents(m: &LMod) -> Contents {
    let mut c = Contents { definition: None, game_nodes: None, native_journal: None, voice_asset: None, talent_tree: None, block_talents: None, new_vfx: None, client_assets: None, png_icon: false, anything: false };
    for f in &m.files {
        if matches!(f.kind.as_str(), "content" | "assets" | "cook") {
            c.anything = true;
        }
        for a in &f.annots {
            if let Annot::Def { ns, span, .. } = a
                && matches!(ns.as_str(), "items" | "recipes" | "templates" | "sequences" | "perks" | "buffs")
                && f.kind == "content"
                && c.definition.is_none()
            {
                c.definition = Some((f.display.clone(), *span, format!("a new {}", singular(ns))));
            }
        }
        let Some(r) = &f.root else { continue };
        match f.kind.as_str() {
            "blocks" => {
                c.anything = true;
                // talents are nodes of a tree every client builds from the mod's own files, as skills/*.json
                if let Some(t) = r.get("talents").and_then(Node::as_object).and_then(|o| o.iter().find(|b| b.value.get("game").is_some())) {
                    c.game_nodes.get_or_insert((f.display.clone(), t.key_span));
                }
                for section in ["talents", "talentTrees"] {
                    if let Some(t) = r.get(section).and_then(Node::as_object).and_then(|o| o.first()) {
                        c.block_talents.get_or_insert((f.display.clone(), t.key_span));
                    }
                }
                // a quest block is in the game's journal and a dialogue block in its NPC menu, both built by every client from the mod's files
                for section in ["quests", "dialogues"] {
                    if c.native_journal.is_none()
                        && let Some(b) = r.get(section).and_then(Node::as_object).and_then(|o| o.first())
                    {
                        c.native_journal = Some((f.display.clone(), b.key_span));
                    }
                }
                // an ability that places something makes new templates; one with only effects on the caster asks nothing
                if c.definition.is_none()
                    && let Some(a) = r.get("abilities").and_then(Node::as_object).and_then(|o| o.iter().find(|b| b.value.get("delivery").is_some() || b.value.get("item").is_some()))
                {
                    c.definition = Some((f.display.clone(), a.key_span, "a new ability that places something".to_string()));
                }
                if c.new_vfx.is_none() {
                    if let Some(b) = r.get("vfx").and_then(Node::as_object).and_then(|o| o.first()) {
                        c.new_vfx = Some((f.display.clone(), b.key_span, "a new VFX"));
                    } else if let Some(b) = r.get("materials").and_then(Node::as_object).and_then(|o| o.first()) {
                        c.new_vfx = Some((f.display.clone(), b.key_span, "a new material"));
                    } else if let Some(u) = vfx_uses(r).into_iter().find(|u| !u.is_game()) {
                        c.new_vfx = Some((f.display.clone(), u.span, "a field that names a new VFX"));
                    }
                }
                for (key, what) in [("spells", "a new spell"), ("weapons", "a new weapon"), ("statuses", "a new status"), ("items", "a new item"), ("wands", "a new wand"), ("armor", "a new piece of armor"), ("stations", "a new station"), ("props", "a new prop"), ("cosmetics", "a new cosmetic"), ("perks", "a new perk"), ("containers", "a new container"), ("doors", "a new door"), ("beds", "a new bed"), ("seats", "a new seat"), ("hair", "a new hair style"), ("beards", "a new beard"), ("recipes", "a new recipe")] {
                    if c.definition.is_none()
                        && let Some(b) = r.get(key).and_then(Node::as_object).and_then(|o| o.first())
                    {
                        c.definition = Some((f.display.clone(), b.key_span, what.to_string()));
                    }
                }
                // what only the players' game can hold: a look's PNG icon or a model of the mod's own (Kiln.Blocks refuses these below clients "mod")
                fn own_look(n: &Node, out: &mut Option<(Span, &'static str)>) {
                    if out.is_some() {
                        return;
                    }
                    if let Some(o) = n.as_object() {
                        for mm in o {
                            // what a VFX needs has its own rule
                            if mm.key == "vfx" {
                                continue;
                            }
                            let text = mm.value.as_str().unwrap_or("");
                            if mm.key == "icon" && text.to_ascii_lowercase().ends_with(".png") {
                                *out = Some((mm.value.span, "a PNG item icon"));
                            } else if mm.key == "model" && text.len() == 36 && text.bytes().filter(|b| *b == b'-').count() == 4 {
                                *out = Some((mm.value.span, "a model of the mod's own"));
                            } else if matches!(mm.key.as_str(), "model" | "female") && { let t = text.to_ascii_lowercase(); t.ends_with(".glb") || t.ends_with(".gltf") } {
                                *out = Some((mm.value.span, "a model of the mod's own"));
                            } else if mm.key == "sound" && !text.is_empty() && !is_guid(text) && !text.contains(':') {
                                *out = Some((mm.value.span, "a sound of the mod's own"));
                            } else {
                                own_look(&mm.value, out);
                            }
                        }
                    } else if let Some(a) = n.as_array() {
                        for x in a {
                            own_look(x, out);
                        }
                    }
                }
                let mut look = None;
                own_look(r, &mut look);
                if let Some((span, what)) = look {
                    c.png_icon = true;
                    if c.client_assets.is_none() {
                        c.client_assets = Some((f.display.clone(), span, what.into()));
                    }
                }
            }
            "assets" => {
                for e in r.get("entries").and_then(Node::as_array).into_iter().flatten() {
                    let kind = e.str_of("kind").unwrap_or("");
                    let ty = e.str_of("type").unwrap_or("");
                    let drawn = matches!(kind, "icon" | "texture") || (kind == "resource" && (ty.contains("RenderModel") || ty.contains("UiTexture") || ty.contains("RenderMaterial")));
                    if drawn && c.client_assets.is_none() {
                        c.client_assets = Some((f.display.clone(), e.span, format!("assets.json {kind} {ty}").trim_end().to_string()));
                    }
                }
            }
            "content" => {
                for it in r.pointer("/items/add").and_then(Node::as_array).into_iter().flatten() {
                    if let Some(icon) = it.get("icon").filter(|i| i.as_str().is_some_and(|s| s.to_ascii_lowercase().ends_with(".png"))) {
                        c.png_icon = true;
                        if c.client_assets.is_none() {
                            c.client_assets = Some((f.display.clone(), icon.span, "a PNG item icon".into()));
                        }
                    }
                }
            }
            "cook" if r.str_of("tool") != Some("impact") => {
                for s in r.get("steps").and_then(Node::as_array).into_iter().flatten() {
                    let what = match s.str_of("op") {
                        Some("model") => "a cooked 3D model",
                        Some("sound") | Some("cue") => "cooked sounds (heard only on clients)",
                        Some("voiceover") => "cooked voice-overs (heard only on clients)",
                        _ => continue,
                    };
                    if c.client_assets.is_none() {
                        c.client_assets = Some((f.display.clone(), s.span, what.into()));
                    }
                }
            }
            "skills" => {
                c.anything = true;
                // "gameTree" is {"nodes": [...]} or the list itself (native/services/skills load_file)
                let nodes = r.pointer("/gameTree/nodes").or_else(|| r.get("gameTree")).and_then(Node::as_array);
                if c.game_nodes.is_none()
                    && let Some(n) = nodes.and_then(|a| a.first())
                {
                    c.game_nodes = Some((f.display.clone(), n.span));
                }
                if c.talent_tree.is_none()
                    && let Some(t) = r.get("trees").and_then(Node::as_array).and_then(|a| a.first())
                {
                    c.talent_tree = Some((f.display.clone(), t.span));
                }
            }
            "quests" => {
                c.anything = true;
                // the game's journal is the default: only "journal": "chat" stays out of it
                for q in r.get("quests").and_then(Node::as_object).into_iter().flatten() {
                    if c.native_journal.is_none() && q.value.str_of("journal") != Some("chat") {
                        c.native_journal = Some((f.display.clone(), q.value.get("journal").map_or(q.key_span, |j| j.span)));
                    }
                }
                // an NPC clone and a native dialogue are client data like the journal
                for n in r.get("npcs").and_then(Node::as_object).into_iter().flatten() {
                    if c.native_journal.is_none()
                        && let Some(cl) = n.value.get("clone")
                        && cl.as_bool() != Some(false)
                    {
                        c.native_journal = Some((f.display.clone(), cl.span));
                    }
                }
                // and so is the game's NPC menu: only "native": false stays out of it
                for d in r.get("dialogues").and_then(Node::as_object).into_iter().flatten() {
                    if c.native_journal.is_none() && d.value.get("native").and_then(Node::as_bool) != Some(false) {
                        c.native_journal = Some((f.display.clone(), d.value.get("native").map_or(d.key_span, |n| n.span)));
                    }
                }
                for d in r.get("dialogues").and_then(Node::as_object).into_iter().flatten() {
                    for n in d.value.get("nodes").and_then(Node::as_object).into_iter().flatten() {
                        if c.voice_asset.is_none()
                            && let Some(v) = n.value.get("voice")
                            && v.as_str().is_some_and(|s| !is_guid(s))
                        {
                            c.voice_asset = Some((f.display.clone(), v.span));
                        }
                    }
                }
            }
            _ => {}
        }
    }
    c
}

fn insert_member_fix(mf: &LFile, key: &str, value: &str) -> Option<Fix> {
    let r = mf.root.as_ref()?;
    let anchor = r.member("side").or_else(|| r.member("version")).or_else(|| r.member("id"))?;
    let at = anchor.value.span.end;
    Some(Fix::new(&format!("Add \"{key}\": \"{value}\""), vec![TextEdit::new(&mf.display, Span::new(at, at), &format!(",\n  \"{key}\": \"{value}\""))]))
}

fn manifest_rules(out: &mut Out, m: &LMod, _defs: &Defs, _mi: usize) {
    let Some(mf) = m.manifest() else { return };
    let Some(root) = &mf.root else { return };
    let (side, clients, explicit) = side_clients(m);
    let c = contents(m);
    let at = root.member("clients").map(|x| x.value.span).or_else(|| root.member("side").map(|x| x.key_span)).unwrap_or(Span::new(root.span.start, root.span.start + 1));
    let how = if explicit { format!("\"clients\": \"{clients}\"") } else { format!("no \"clients\" (side \"{side}\" means \"{clients}\")") };
    if let Some((file, span, what)) = &c.definition
        && clients == "none"
    {
        let mut d = Diagnostic::new(&mf.display, codes::CLIENTS_DEFINITIONS, at, &format!(
            "this mod adds {what} (and maybe more) but says {how}: vanilla clients cannot show it (\"[Invalid Item Stack]\"); declare \"clients\": \"kiln\" (the server sends the definitions to Kiln clients and refuses vanilla ones) or \"mod\" (players install the mod)"
        ))
        .with_related(file, *span, "defined here");
        if let Some(fx) = if explicit { Some(Fix::new("Set \"clients\": \"kiln\"", vec![TextEdit::new(&mf.display, at, "\"kiln\"")])) } else { insert_member_fix(mf, "clients", "kiln") } {
            d.fixes.push(fx);
        }
        out.push(d);
    }
    if let Some((file, span, what)) = &c.client_assets
        && clients != "mod"
    {
        let why = if clients == "kiln" {
            "the server does not send assets (PNG icons, textures, models): Kiln clients without the mod show the base's look"
        } else {
            "players without the mod never see them"
        };
        let mut d = Diagnostic::new(&mf.display, codes::CLIENTS_ASSETS, at, &format!("this mod ships {what} but says {how}: {why}; declare \"clients\": \"mod\" so the server's modpack delivers it")).with_related(file, *span, "here");
        if let Some(fx) = if explicit { Some(Fix::new("Set \"clients\": \"mod\"", vec![TextEdit::new(&mf.display, at, "\"mod\"")])) } else { insert_member_fix(mf, "clients", "mod") } {
            d.fixes.push(fx);
        }
        out.push(d);
    }
    if side == "server"
        && let Some((file, span, what)) = &c.client_assets
    {
        let sat = root.member("side").map(|x| x.value.span).unwrap_or(at);
        let mut d = Diagnostic::new(&mf.display, codes::SIDE_ASSETS, sat, &format!("{what} is drawn by clients, but side \"server\" mods do not load on a joined client (only when a player hosts); use side \"both\"")).with_related(file, *span, "here");
        if root.member("side").is_some() {
            d.fixes.push(Fix::new("Set \"side\": \"both\"", vec![TextEdit::new(&mf.display, sat, "\"both\"")]));
        } else if let Some(fx) = insert_member_fix(mf, "side", "both") {
            d.fixes.push(fx);
        }
        out.push(d);
    }
    // (kiln.skills reads gameTree natively for every mod in the load order, on any side: not a contradiction there)
    if side == "server" && clients == "mod" && explicit && c.client_assets.is_none() && c.game_nodes.is_none() {
        out.push(Diagnostic::new(&mf.display, codes::SIDE_CLIENTS, at, "\"clients\": \"mod\" makes players install this mod, but a side \"server\" mod never loads on a joined client: did you mean side \"both\", or \"clients\": \"kiln\" / \"none\"?"));
    }
    if let Some((file, span)) = &c.native_journal
        && clients != "mod"
    {
        // the client rebuilds its journal, knowledge and templates at boot from the mod's own files: no server sends them
        let mut d = Diagnostic::new(&mf.display, codes::CLIENTS_QUESTS, at, &format!(
            "this mod has a quest in the game's journal (the default), dialogue in the game's NPC menu or an NPC clone but says {how}: a client builds them at boot from the mod's files, so a player without those sees nothing; declare \"clients\": \"mod\" and side \"both\" (servers then refuse players without it). For a mod vanilla clients join, every quest says \"journal\": \"chat\" and every dialogue \"native\": false"
        ))
        .with_related(file, *span, "here");
        if let Some(fx) = if explicit { Some(Fix::new("Set \"clients\": \"mod\"", vec![TextEdit::new(&mf.display, at, "\"mod\"")])) } else { insert_member_fix(mf, "clients", "mod") } {
            d.fixes.push(fx);
        }
        out.push(d);
    }
    if let Some((file, span)) = &c.voice_asset
        && clients != "mod"
    {
        out.push(Diagnostic::new(&mf.display, codes::CLIENTS_ASSETS, at, &format!(
            "a conversation names a voice-over from this mod's cooked audio but the mod says {how}: only players with the mod's files hear it; declare \"clients\": \"mod\" and side \"both\", or use a game line's GUID"
        ))
        .with_related(file, *span, "here"));
    }
    if let Some((file, span, what)) = &c.new_vfx {
        if clients != "mod" {
            let mut d = Diagnostic::new(&mf.display, codes::CLIENTS_VFX, at, &format!(
                "this mod has {what} but says {how}: a client builds it at boot from the mod's own files, and the server never sends assets; declare \"clients\": \"mod\". A VFX of the game (\"game:<name>\") needs no such thing"
            ))
            .with_related(file, *span, "here");
            if let Some(fx) = if explicit { Some(Fix::new("Set \"clients\": \"mod\"", vec![TextEdit::new(&mf.display, at, "\"mod\"")])) } else { insert_member_fix(mf, "clients", "mod") } {
                d.fixes.push(fx);
            }
            out.push(d);
        }
        if side == "server" {
            let sat = root.member("side").map(|x| x.value.span).unwrap_or(at);
            let mut d = Diagnostic::new(&mf.display, codes::SIDE_ASSETS, sat, &format!("{what} is drawn by clients, but side \"server\" mods do not load on a joined client (only when a player hosts); use side \"both\"")).with_related(file, *span, "here");
            if root.member("side").is_some() {
                d.fixes.push(Fix::new("Set \"side\": \"both\"", vec![TextEdit::new(&mf.display, sat, "\"both\"")]));
            } else if let Some(fx) = insert_member_fix(mf, "side", "both") {
                d.fixes.push(fx);
            }
            out.push(d);
        }
    }
    // 0: nodes in the game's tree, 1: talent blocks, 2: a tree of skills/*.json
    let skills = c.game_nodes.as_ref().map(|(f, s)| (f, s, 0)).or(c.block_talents.as_ref().map(|(f, s)| (f, s, 1))).or(c.talent_tree.as_ref().map(|(f, s)| (f, s, 2)));
    if let Some((file, span, kind)) = skills
        && clients != "mod"
    {
        let mut d = if kind == 0 {
            Diagnostic::new(&mf.display, codes::CLIENTS_SKILLS, at, &format!(
                "this mod has nodes in the game's skill tree but says {how}: every client must build the same tree from the mod's own files (the server sends no tree definitions); declare \"clients\": \"mod\""
            ))
        } else if kind == 1 {
            Diagnostic::new(&mf.display, codes::CLIENTS_SKILLS, at, &format!(
                "this mod has talents but says {how}: every player's game builds the tree and draws it from the mod's own files, so Kiln.Blocks refuses them below \"clients\": \"mod\"; declare \"clients\": \"mod\" and side \"both\""
            ))
        } else {
            Diagnostic::new(&mf.display, codes::CLIENTS_SKILLS, at, &format!(
                "this mod has a talent tree but says {how}: players without the mod can only use it through the /talents chat command; to draw its screen on their game, declare \"clients\": \"mod\" and side \"both\" (vanilla clients are then refused, unless the server's modSync is off)"
            ))
            .severity(Severity::Info)
        }
        .with_related(file, *span, "here");
        if let Some(fx) = if explicit { Some(Fix::new("Set \"clients\": \"mod\"", vec![TextEdit::new(&mf.display, at, "\"mod\"")])) } else { insert_member_fix(mf, "clients", "mod") } {
            d.fixes.push(fx);
        }
        out.push(d);
    }
    // entryAssembly / exports in a built mod folder (a source folder with a .csproj builds them)
    if mf.kind == "manifest" {
        let source = std::fs::read_dir(&m.root).is_ok_and(|rd| rd.flatten().any(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("csproj"))));
        if !source {
            let mut check = |node: &Node| {
                if let Some(dll) = node.as_str()
                    && !dll.is_empty()
                    && !exists_ci(&m.root.join(dll))
                {
                    out.push(Diagnostic::new(&mf.display, codes::ENTRY_MISSING, node.span, &format!("{dll} is not in the mod folder (Kiln.Host fails to load the mod)")));
                }
            };
            if let Some(e) = root.get("entryAssembly") {
                check(e);
            }
            for e in root.get("exports").and_then(Node::as_array).into_iter().flatten() {
                check(e);
            }
        }
    }
}

/// The real manifest readers (kiln_manage::manifest, the parity-tested port of Manifest.cs / the native loader):
/// a refusal the schema did not already report (loadBefore and loadAfter, self-dependency, range syntax, ...), and
/// Manifest.cs's unknown keys. Those are checked here rather than taken from the reader's warnings: a refused manifest
/// returns no warnings, and a misspelled "dependecies" must not hide behind a wrong "version".
fn manifest_reader(out: &mut Out, m: &LMod, schema_errors: &HashSet<String>) {
    let Some(mf) = m.manifest() else { return };
    let Some(root) = &mf.root else { return };
    let Ok(text) = std::str::from_utf8(&mf.bytes) else { return };
    let err = if mf.kind == "manifest" { crate::manifest::parse_kiln(text).err() } else { crate::manifest::parse_native(text).err() }.map(|e| e.message);
    if let Some(msg) = err
        && !schema_errors.contains(&mf.display)
    {
        // underline the key the message names ('a', 'dependencies', apiVersion: ...)
        let named = root.as_object().into_iter().flatten().find(|x| msg.contains(&format!("'{}'", x.key)) || msg.contains(&format!(": {}:", x.key)) || msg.contains(&format!("{} ", x.key)));
        let span = named.map(|x| x.key_span.union(x.value.span)).unwrap_or(Span::new(root.span.start, root.span.start + 1));
        let who = if mf.kind == "manifest" { "Kiln.Host" } else { "the native loader" };
        out.push(Diagnostic::new(&mf.display, codes::MANIFEST_REFUSED, span, &format!("{who} refuses this manifest: {msg}")));
    }
    if mf.kind != "manifest" {
        return;
    }
    // Manifest.cs's Known set (manifest_fixtures_agree_with_kiln_host compares the findings with Kiln.Host's warnings)
    let known: Vec<&str> = vec![
        "$schema", "id", "name", "version", "authors", "author", "description", "license", "url", "apiVersion", "side", "clients",
        "dependencies", "loadBefore", "loadAfter", "incompatible", "features", "entryAssembly", "entryType", "exports", "advanced", "tests",
        "kilnBuild",
    ];
    for mem in root.as_object().into_iter().flatten().filter(|x| !known.contains(&x.key.as_str())) {
        let key = mem.key.as_str();
        let mut d = Diagnostic::new(&mf.display, codes::UNKNOWN_KEY, mem.key_span, &format!("\"{key}\": unknown key (Kiln.Host ignores it and logs a warning)")).pointer(&format!("/{}", super::json::ptr_seg(key)));
        if let Some(sug) = suggest::best(key, &known) {
            d.message.push_str(&format!("; did you mean \"{sug}\"?"));
            d.fixes.push(Fix::new(&format!("Rename to \"{sug}\""), vec![TextEdit::new(&mf.display, mem.key_span, &quote(sug))]));
        }
        out.push(d);
    }
    // a kilnBuild Kiln.Host can't read: ignored with a warning, as an unknown key is (never a reason to refuse the mod)
    if let Ok(parsed) = crate::manifest::parse_kiln(text)
        && parsed.warnings.iter().any(|w| w.starts_with("kilnBuild"))
        && let Some(mem) = root.as_object().into_iter().flatten().find(|x| x.key == "kilnBuild")
    {
        out.push(
            Diagnostic::new(&mf.display, codes::UNKNOWN_KEY, mem.key_span,
                "\"kilnBuild\": Kiln.Host ignores it: it is {\"version\", \"created\"} as Kiln.Sdk writes it; leave it out of a hand-written manifest")
                .pointer("/kilnBuild"),
        );
    }
}

// --------------------------------------------------------------------------------------------- assets ----

fn assets(out: &mut Out, m: &LMod, f: &LFile, cat: Option<&Catalog>) {
    let Some(r) = &f.root else { return };
    let dir = m.root.join("assets");
    let res = cat.and_then(|c| c.section("resources"));
    let mut seen: HashMap<(String, String, String, i64), Span> = HashMap::new();
    for e in r.get("entries").and_then(Node::as_array).into_iter().flatten() {
        let kind = e.str_of("kind").unwrap_or("");
        let file = e.str_of("file").unwrap_or("");
        let fpath = normalize(&dir.join(file.replace('\\', "/")));
        if let Some(g) = e.str_of("guid") {
            let ty = e.str_of("type").unwrap_or(if kind == "texture" { "keen::UiTextureResource" } else { "" }).to_string();
            let part = e.get("part").and_then(Node::as_f64).unwrap_or(0.0) as i64;
            let key = (kind.to_string(), g.to_ascii_lowercase(), ty.clone(), part);
            let gspan = e.get("guid").map(|x| x.span).unwrap_or(e.span);
            if let Some(first) = seen.get(&key) {
                out.push(Diagnostic::new(&f.display, codes::DUPLICATE_ASSET, gspan, &format!("{} is served twice in this file (the later entry wins)", format!("{kind} {g} {ty}").trim_end())).with_related(&f.display, *first, "first here"));
            } else {
                seen.insert(key, gspan);
            }
            if kind == "resource"
                && let Some(s) = res
            {
                let in_game = s.guids.contains(&g.to_ascii_lowercase());
                match e.str_of("mode") {
                    Some("override") if !in_game => out.push(Diagnostic::new(&f.display, codes::ASSET_MODE, gspan, &format!("\"mode\": \"override\" but the game has no resource {g}: kiln.assets refuses the entry"))),
                    Some("new") if in_game => out.push(Diagnostic::new(&f.display, codes::ASSET_MODE, gspan, &format!("\"mode\": \"new\" but {g} is a game resource: use \"override\" or a new GUID"))),
                    _ => {}
                }
            }
        }
        let len = std::fs::metadata(&fpath).ok().map(|x| x.len());
        if kind == "blob"
            && let (Some(h), Some(len)) = (e.get("hash"), len)
            && let Some(size) = h.as_str().and_then(|s| s.split('-').next()).and_then(|s| s.parse::<u64>().ok())
            && size != len
        {
            out.push(Diagnostic::new(&f.display, codes::BLOB_SIZE, h.span, &format!("the hash says {size} bytes, {file} has {len}: kiln.assets refuses the override")));
        }
        if kind == "resource"
            && let Some(len) = len
        {
            for p in e.get("patch").and_then(Node::as_array).into_iter().flatten() {
                if let Some(o) = p.get("offset")
                    && let Some(off) = o.as_f64()
                    && off as u64 + 16 > len
                {
                    out.push(Diagnostic::new(&f.display, codes::PATCH_OFFSET, o.span, &format!("offset {off} + 16 bytes runs past the end of {file} ({len} bytes)")));
                }
            }
        }
    }
}

// --------------------------------------------------------------------------------------------- cook ----

fn cook(out: &mut Out, m: &LMod, f: &LFile) {
    let Some(r) = &f.root else { return };
    let file_tool = r.str_of("tool").unwrap_or("modelcook");
    let impact = file_tool == "impact";
    let inputs: HashMap<&str, &str> = r.get("inputs").and_then(Node::as_object).into_iter().flatten().map(|x| (x.key.as_str(), x.value.str_of("type").unwrap_or(if impact { "keen::ImpactProgram" } else { "" }))).collect();
    let mut stems: HashMap<&str, Span> = HashMap::new();
    let mut names: HashMap<(&str, &str), Span> = HashMap::new();
    let dir = f.path.parent().unwrap_or(Path::new(".")).to_path_buf();
    let out_dir = dir.join(r.str_of("out").unwrap_or(if impact { "../assets/cooked/impact" } else { "cooked" }));
    let manifest_path = normalize(&if impact { dir.join(r.str_of("manifest").unwrap_or("../assets/assets.json")) } else { dir.join("assets.json") });
    let served: Option<HashSet<PathBuf>> = m.files.iter().find(|x| normalize(&x.path) == manifest_path).and_then(|x| x.root.as_ref()).map(|ar| {
        let base = manifest_path.parent().unwrap_or(Path::new(".")).to_path_buf();
        ar.get("entries").and_then(Node::as_array).into_iter().flatten().filter_map(|e| e.str_of("file")).map(|fl| normalize(&base.join(fl))).collect()
    });
    let steps: Vec<&Node> = r.get("steps").and_then(Node::as_array).into_iter().flatten().collect();
    fn tool_of<'n>(s: &'n Node, impact: bool, file_tool: &'n str) -> &'n str {
        if impact { "impact" } else { s.str_of("tool").unwrap_or(file_tool) }
    }
    let sound_names: HashSet<&str> = steps.iter().filter(|s| tool_of(s, impact, file_tool) == "soundcook" && s.str_of("op") == Some("sound")).filter_map(|s| s.str_of("name")).collect();
    let skeleton_names: HashSet<&str> = steps.iter().filter(|s| tool_of(s, impact, file_tool) == "animcook" && s.str_of("op") == Some("skeleton")).filter_map(|s| s.str_of("name")).collect();
    let input_type = |out: &mut Out, b: &Node, i: usize, op: &str, want: &str| {
        let Some(id) = b.as_str() else { return };
        match inputs.get(id) {
            None => out.push(Diagnostic::new(&f.display, codes::COOK_BASE, b.span, &format!("step {i} ({op}): base \"{id}\" is not an input (the cook stops)"))),
            Some(t) if !t.is_empty() && *t != want => out.push(Diagnostic::new(&f.display, codes::COOK_BASE, b.span, &format!("step {i} ({op}): base {id} is a {t}, needs {want}"))),
            _ => {}
        }
    };
    for (i, s) in steps.iter().enumerate() {
        let tool = tool_of(s, impact, file_tool);
        let op = s.str_of("op").unwrap_or("");
        if !matches!(tool, "modelcook" | "soundcook" | "animcook" | "impact") {
            continue;
        }
        // @input references anywhere in the step
        fn at_refs<'n>(n: &'n Node, acc: &mut Vec<&'n Node>) {
            match &n.value {
                super::json::Value::String(s) if s.starts_with('@') => acc.push(n),
                super::json::Value::Array(a) => a.iter().for_each(|x| at_refs(x, acc)),
                super::json::Value::Object(o) => o.iter().filter(|x| !matches!(x.key.as_str(), "edits" | "comment" | "subtitles")).for_each(|x| at_refs(&x.value, acc)),
                _ => {}
            }
        }
        let mut refs = Vec::new();
        if tool == "animcook" {
            // Animation and joint names may begin with '@'; only resource references name recipe inputs.
            for key in ["skeleton", "guid"] {
                if let Some(n) = s.get(key) { at_refs(n, &mut refs); }
            }
            for clip in s.get("clips").and_then(Node::as_object).into_iter().flatten() {
                if let Some(n) = clip.value.get("guid") { at_refs(n, &mut refs); }
            }
        } else {
            at_refs(s, &mut refs);
        }
        for n in refs {
            let id = &n.as_str().unwrap_or("")[1..];
            // @player is the player's skeleton, with no input to declare (tools/modelcook)
            let player = id == "player" && tool == "modelcook" && s.get("skeleton").is_some_and(|sk| std::ptr::eq(sk, n));
            if !inputs.contains_key(id) && !player {
                let mut d = Diagnostic::new(&f.display, codes::COOK_INPUT, n.span, &format!("@{id}: no such input"));
                let c: Vec<&str> = inputs.keys().copied().collect();
                if let Some(b) = suggest::best(id, &c) {
                    d.message.push_str(&format!("; did you mean @{b}?"));
                    d.fixes.push(Fix::new(&format!("Replace with @{b}"), vec![TextEdit::new(&f.display, n.span, &quote(&format!("@{b}")))]));
                }
                out.push(d);
            }
        }
        if tool == "soundcook" {
            if let Some(nm) = s.get("name")
                && let Some(name) = nm.as_str()
            {
                let mut claim = |out: &mut Out, kind: &'static str, span: Span| {
                    if let Some(first) = names.get(&(kind, name)) {
                        out.push(Diagnostic::new(&f.display, codes::COOK_STEM, span, &format!("{kind} \"{name}\" is defined twice in this recipe (soundcook stops)")).with_related(&f.display, *first, "first here"));
                    } else {
                        names.insert((kind, name), span);
                    }
                };
                match op {
                    "sound" => {
                        claim(out, "sound", nm.span);
                        if s.get("cue").is_some() {
                            claim(out, "cue", nm.span);
                        }
                    }
                    "cue" => claim(out, "cue", nm.span),
                    "voiceover" => claim(out, "voiceover", nm.span),
                    _ => {}
                }
            }
            if let Some(b) = s.get("base") {
                match op {
                    "cue" => input_type(out, b, i, op, "keen::SoundContainerResource"),
                    "voiceover" => input_type(out, b, i, op, "keen::VoiceoverResource"),
                    _ => {}
                }
            }
            for k in ["sounds", "addSounds"] {
                for t in s.get(k).and_then(Node::as_array).into_iter().flatten() {
                    let (node, name) = match t.as_str() {
                        Some(x) => (t, x),
                        None => match t.get("sound") {
                            Some(sn) => (sn, sn.as_str().unwrap_or("")),
                            None => continue,
                        },
                    };
                    if name.starts_with('@') || is_guid(name) || sound_names.contains(name) {
                        continue;
                    }
                    let mut d = Diagnostic::new(&f.display, codes::COOK_INPUT, node.span, &format!("take \"{name}\" is not a sound of this recipe, an @input or a GUID"));
                    let c: Vec<&str> = sound_names.iter().copied().collect();
                    if let Some(b) = suggest::best(name, &c) {
                        d.message.push_str(&format!("; did you mean \"{b}\"?"));
                        d.fixes.push(Fix::new(&format!("Replace with \"{b}\""), vec![TextEdit::new(&f.display, node.span, &quote(b))]));
                    }
                    out.push(d);
                }
            }
            continue; // soundcook writes its own assets.json entries
        }
        if ((tool == "modelcook" && op == "model") || (tool == "animcook" && matches!(op, "clips" | "graph")))
            && let Some(sk) = s.get("skeleton")
            && let Some(name) = sk.as_str()
        {
            if let Some(id) = name.strip_prefix('@') {
                if let Some(t) = inputs.get(id).filter(|t| !t.is_empty() && **t != "keen::ModelHierarchyResource") {
                    out.push(Diagnostic::new(&f.display, codes::COOK_SKELETON, sk.span, &format!("step {i} ({op}): skeleton @{id} is a {t}, needs keen::ModelHierarchyResource")));
                }
            } else if !skeleton_names.contains(name) {
                let mut d = Diagnostic::new(&f.display, codes::COOK_SKELETON, sk.span, &format!("step {i} ({op}): no skeleton step is named \"{name}\" in this recipe (a game skeleton is @input)"));
                let c: Vec<&str> = skeleton_names.iter().copied().collect();
                if let Some(b) = suggest::best(name, &c) {
                    d.message.push_str(&format!("; did you mean \"{b}\"?"));
                    d.fixes.push(Fix::new(&format!("Replace with \"{b}\""), vec![TextEdit::new(&f.display, sk.span, &quote(b))]));
                }
                out.push(d);
            }
        }
        if tool == "animcook" { continue; } // animcook writes its own assets.json entries
        if let Some(b) = s.get("base") {
            let want = match (impact, op) {
                (false, "model") => "keen::RenderModel",
                (false, "visual") => "keen::ecs::TemplateResource",
                _ => "keen::ImpactProgram",
            };
            input_type(out, b, i, op, want);
        }
        if let Some(st) = s.get("stem")
            && let Some(stem) = st.as_str()
        {
            if let Some(first) = stems.get(stem) {
                out.push(Diagnostic::new(&f.display, codes::COOK_STEM, st.span, &format!("stem \"{stem}\" is used by an earlier step too: the outputs overwrite each other")).with_related(&f.display, *first, "first here"));
            } else {
                stems.insert(stem, st.span);
            }
            let bin = normalize(&out_dir.join(format!("{stem}.bin")));
            match &served {
                Some(set) if !set.contains(&bin) => {
                    let rel = pathdiff(&bin, manifest_path.parent().unwrap_or(Path::new(".")));
                    out.push(Diagnostic::new(&f.display, codes::COOK_UNLISTED, st.span, &format!("this step writes {rel}, which {} does not list: kiln.assets will not serve it (cook with --update-manifest)", display_rel(&manifest_path, &m.root))));
                }
                None => out.push(Diagnostic::new(&f.display, codes::COOK_UNLISTED, st.span, &format!("this step writes {stem}.bin, but there is no {} to serve it", display_rel(&manifest_path, &m.root)))),
                _ => {}
            }
        }
    }
}

fn display_rel(p: &Path, root: &Path) -> String {
    p.strip_prefix(root).map(|x| x.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| display(p))
}

fn pathdiff(p: &Path, base: &Path) -> String {
    p.strip_prefix(base).map(|x| x.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| display(p))
}

// --------------------------------------------------------------------------------------------- natives ----

fn natives(out: &mut Out, m: &LMod, f: &LFile) {
    let Some(r) = &f.root else { return };
    for n in r.get("natives").and_then(Node::as_array).into_iter().flatten() {
        let Some(name_node) = n.get("name") else { continue };
        let Some(name) = name_node.as_str() else { continue };
        let want = fnv1a(name.as_bytes());
        if let Some(h) = n.get("hash") {
            let got = h.as_str().and_then(parse_id).or_else(|| h.as_f64().map(|x| x as u32));
            if got.is_some_and(|g| g != want) {
                out.push(
                    Diagnostic::new(&f.display, codes::NATIVE_HASH, h.span, &format!("hash 0x{:08x} is not FNV-1a(\"{name}\") = 0x{want:08x}: the ECALLs would call nothing", got.unwrap_or(0)))
                        .with_fix(Fix::new(&format!("Write \"0x{want:08x}\""), vec![TextEdit::new(&f.display, h.span, &format!("\"0x{want:08x}\""))])),
                );
            }
        }
        if let (Some(a), Some(args)) = (n.get("argc"), n.get("args").and_then(Node::as_array))
            && let Some(argc) = a.as_f64()
            && !args.is_empty()
            && args.len() != argc as usize
        {
            out.push(Diagnostic::new(&f.display, codes::NATIVE_HASH, a.span, &format!("argc {argc} but {} args are listed", args.len())));
        }
        let prefix = name.split('.').next().unwrap_or("");
        if prefix.eq_ignore_ascii_case("kiln") {
            out.push(Diagnostic::new(&f.display, codes::NATIVE_PREFIX, name_node.span, "mod id \"kiln\" is reserved for Kiln's built-in natives").severity(Severity::Error));
        } else if let Some(id) = &m.id
            && prefix != id
        {
            out.push(Diagnostic::new(&f.display, codes::NATIVE_PREFIX, name_node.span, &format!("a Kiln native is named \"<modid>.<Name>\": this mod's id is \"{id}\", not \"{prefix}\"")));
        }
    }
}

// --------------------------------------------------------------------------------------------- quests ----

/// References inside a mod's quests/*.json files: what Kiln.Quests would refuse or never find. Ids with a ':' name another
/// mod's entry and are not checked; C# definitions are invisible here, so these are warnings.
fn quests(out: &mut Out, m: &LMod) {
    // quests/*.json, and the quest, dialogue and NPC blocks of blocks/*.json (the same keys: a block's rewards are a bundle, and no key here reads them)
    let files: Vec<&LFile> = m.files.iter().filter(|f| f.kind == "quests" || (f.kind == "blocks" && ["quests", "dialogues", "npcs"].iter().any(|k| f.root.as_ref().is_some_and(|r| r.get(k).is_some())))).collect();
    if files.is_empty() {
        return;
    }
    let section = |f: &'_ LFile, key: &str| -> Vec<(String, Span)> {
        f.root.as_ref().and_then(|r| r.get(key)).and_then(Node::as_object).into_iter().flatten().map(|mm| (mm.key.clone(), mm.key_span)).collect()
    };
    let mut quest_steps: HashMap<String, Vec<String>> = HashMap::new();
    let (mut npcs, mut dialogues): (Vec<String>, Vec<String>) = (vec![], vec![]);
    let mut seen: HashMap<(&'static str, String), (String, Span)> = HashMap::new();
    for f in &files {
        for (kind, key) in [("quest", "quests"), ("NPC", "npcs"), ("dialogue", "dialogues")] {
            for (id, span) in section(f, key) {
                if let Some((file, first)) = seen.get(&(kind, id.clone())) {
                    out.push(Diagnostic::new(&f.display, codes::DUPLICATE_NAME, span, &format!("{kind} \"{id}\" is defined twice in this mod (the later one replaces the earlier)")).with_related(file, *first, "first here"));
                } else {
                    seen.insert((kind, id), (f.display.clone(), span));
                }
            }
        }
        npcs.extend(section(f, "npcs").into_iter().map(|x| x.0));
        dialogues.extend(section(f, "dialogues").into_iter().map(|x| x.0));
        for q in f.root.as_ref().and_then(|r| r.get("quests")).and_then(Node::as_object).into_iter().flatten() {
            let steps: Vec<String> = q.value.get("steps").and_then(Node::as_array).into_iter().flatten().filter_map(|s| s.str_of("id").map(str::to_string)).collect();
            quest_steps.insert(q.key.clone(), steps);
        }
    }
    let quest_ids: Vec<&str> = quest_steps.keys().map(String::as_str).collect();
    let npc_ids: Vec<&str> = npcs.iter().map(String::as_str).collect();
    let dialogue_ids: Vec<&str> = dialogues.iter().map(String::as_str).collect();
    let refer = |out: &mut Out, f: &LFile, n: &Node, what: &str, known: &[&str]| {
        let Some(name) = n.as_str() else { return };
        if name.contains(':') || known.contains(&name) {
            return;
        }
        let mut d = Diagnostic::new(&f.display, codes::QUEST_REF, n.span, &format!("no {what} \"{name}\" in this mod's quest files"));
        let mut sorted = known.to_vec();
        sorted.sort_unstable();
        sorted.dedup();
        if let Some(b) = suggest::best(name, &sorted) {
            d.message.push_str(&format!("; did you mean \"{b}\"?"));
            d.fixes.push(Fix::new(&format!("Replace with \"{b}\""), vec![TextEdit::new(&f.display, n.span, &quote(b))]));
        }
        out.push(d);
    };
    let step_ref = |out: &mut Out, f: &LFile, n: &Node| {
        let Some(name) = n.as_str() else { return };
        let (q, s) = name.rsplit_once('.').map_or((name, None), |(q, s)| (q, Some(s)));
        if q.contains(':') {
            return;
        }
        match (quest_steps.get(q), s) {
            (None, _) => refer(out, f, n, "quest", &quest_ids),
            (Some(steps), Some(s)) if !steps.iter().any(|x| x == s) => {
                out.push(Diagnostic::new(&f.display, codes::QUEST_REF, n.span, &format!("quest \"{q}\" has no step \"{s}\" (it has {})", steps.join(", "))));
            }
            _ => {}
        }
    };
    // walks every condition and action under a node
    fn walk(n: &Node, visit: &mut dyn FnMut(&str, &Node)) {
        if let Some(o) = n.as_object() {
            for mm in o {
                visit(&mm.key, &mm.value);
                walk(&mm.value, visit);
            }
        } else if let Some(a) = n.as_array() {
            for x in a {
                walk(x, visit);
            }
        }
    }
    for f in &files {
        let Some(r) = f.root.as_ref() else { continue };
        for n in r.get("npcs").and_then(Node::as_object).into_iter().flatten() {
            if let Some(d) = n.value.get("dialogue") {
                refer(out, f, d, "dialogue", &dialogue_ids);
            }
            // an NPC block's "talk": a dialogue block, or one of quests/*.json (a C# one is invisible here, so only a file that has dialogues is checked)
            if f.kind == "blocks"
                && let Some(t) = n.value.get("talk")
                && !dialogue_ids.is_empty()
            {
                refer(out, f, t, "dialogue", &dialogue_ids);
            }
        }
        for q in r.get("quests").and_then(Node::as_object).into_iter().flatten() {
            if let Some(g) = q.value.get("giver") {
                refer(out, f, g, "NPC", &npc_ids);
            }
            let mut step_seen: HashMap<&str, Span> = HashMap::new();
            for s in q.value.get("steps").and_then(Node::as_array).into_iter().flatten() {
                if let Some(idn) = s.get("id")
                    && let Some(id) = idn.as_str()
                {
                    if let Some(first) = step_seen.get(id) {
                        out.push(Diagnostic::new(&f.display, codes::DUPLICATE_NAME, idn.span, &format!("quest {}: step \"{id}\" twice (Kiln.Quests refuses the quest)", q.key)).severity(Severity::Error).with_related(&f.display, *first, "first here"));
                    } else {
                        step_seen.insert(id, idn.span);
                    }
                }
                let objectives: Vec<&Node> = s.get("objective").into_iter().chain(s.get("objectives").and_then(Node::as_array).into_iter().flatten()).collect();
                for o in objectives {
                    if let Some(to) = o.pointer("/deliver/to") {
                        refer(out, f, to, "NPC", &npc_ids);
                    }
                }
            }
        }
        for d in r.get("dialogues").and_then(Node::as_object).into_iter().flatten() {
            let nodes: Vec<&str> = d.value.get("nodes").and_then(Node::as_object).into_iter().flatten().map(|m| m.key.as_str()).collect();
            let start = d.value.get("start");
            match start {
                Some(s) => refer(out, f, s, "node", &nodes),
                None if !nodes.contains(&"start") => {
                    out.push(Diagnostic::new(&f.display, codes::QUEST_REF, d.key_span, &format!("dialogue \"{}\" has no \"start\" and no node called start", d.key)).severity(Severity::Error));
                }
                None => {}
            }
            let mut with_end = nodes.clone();
            with_end.push("end");
            for n in d.value.get("nodes").and_then(Node::as_object).into_iter().flatten() {
                if let Some(g) = n.value.get("goto") {
                    refer(out, f, g, "node", &with_end);
                }
                for o in n.value.get("options").and_then(Node::as_array).into_iter().flatten() {
                    if let Some(g) = o.get("goto") {
                        refer(out, f, g, "node", &with_end);
                    }
                }
            }
        }
        // conditions and actions, wherever they are: only keys the format gives these meanings
        for key in ["quests", "dialogues"] {
            let Some(sec) = r.get(key) else { continue };
            walk(sec, &mut |k, v| match k {
                "start" | "complete" | "fail" | "reset" | "quest" if v.as_str().is_some() && !(k == "start" && key == "dialogues" && !looks_like_action_start(sec, v)) => refer(out, f, v, "quest", &quest_ids),
                "step" | "advance" if v.as_str().is_some() => step_ref(out, f, v),
                "talk" if v.as_str().is_some() => refer(out, f, v, "dialogue", &dialogue_ids),
                _ => {}
            });
        }
    }
}

/// Names inside a mod's blocks/*.json: a block field takes a block inline or by name. A name with ':' is another mod's and is not checked; blocks the
/// mod's code defines count when the name appears in its code, so these are warnings. The rest is what Kiln.Blocks refuses at load, said earlier.
fn blocks(out: &mut Out, m: &LMod, code: &Code, cat: Option<&Catalog>) {
    let files: Vec<&LFile> = m.files.iter().filter(|f| f.kind == "blocks").collect();
    if files.is_empty() {
        return;
    }
    // every section of the schema (lint/sections_test.rs checks it)
    const SECTIONS: &[(&str, &str)] = &[
        ("sounds", "sound"), ("props", "prop"), ("placements", "placement"), ("comforts", "comfort"), ("processing", "processing"), ("menus", "menu"),
        ("vfx", "vfx"), ("materials", "material"), ("screenEffects", "screen effect"), ("materialTweaks", "material tweak"), ("buildingMaterials", "building material"), ("terrainMaterials", "terrain material"), ("surfaces", "surface"), ("strengths", "strength"), ("spells", "spell"), ("weapons", "weapon"), ("statuses", "status"), ("effects", "effect"), ("items", "item"), ("wands", "wand"), ("armor", "armor"), ("stations", "station"),
        ("looks", "look"), ("weaponStats", "weapon stats"), ("armorStats", "armor stats"), ("weaponClasses", "weapon class"), ("armorSlots", "armor slot"), ("armorSets", "armor set"), ("onHits", "on-hit"),
        ("cosmetics", "cosmetic"), ("wornLooks", "worn look"),
        ("containers", "container"), ("doors", "door"), ("beds", "bed"), ("seats", "seat"), ("interactions", "interaction"), ("blueprints", "blueprint"), ("hair", "hair"), ("beards", "beard"),
        ("perks", "perk"), ("abilities", "ability"), ("talents", "talent"), ("talentTrees", "talent tree"), ("triggers", "trigger"),
        ("creatures", "creature"), ("npcs", "NPC"), ("bodies", "body"), ("animations", "animation"), ("ais", "AI"), ("behaviours", "behaviour"), ("attacks", "attack"), ("rules", "rule"),
        ("lootTables", "loot table"), ("recipes", "recipe"),
        ("quests", "quest"), ("dialogues", "dialogue"),
    ];
    let mut known: HashMap<String, (&'static str, String, Span)> = HashMap::new();
    for f in &files {
        for &(key, kind) in SECTIONS {
            for b in f.root.as_ref().and_then(|r| r.get(key)).and_then(Node::as_object).into_iter().flatten() {
                if let Some((_, file, first)) = known.get(&b.key) {
                    out.push(Diagnostic::new(&f.display, codes::DUPLICATE_NAME, b.key_span, &format!("block \"{}\" is defined twice in this mod (the later one replaces the earlier)", b.key)).with_related(file, *first, "first here"));
                } else {
                    known.insert(b.key.clone(), (kind, f.display.clone(), b.key_span));
                }
            }
        }
    }
    // What a field takes when it is a name: the kinds it accepts, and the premades that are names too.
    fn wants(k: &str) -> Option<(&'static [&'static str], &'static str, &'static [&'static str])> {
        Some(match k {
            "onImpact" | "effects" | "onApply" | "onTick" | "onExpire" | "then" | "onUse" | "onEat" | "onRefused" | "onCast" | "onStart" | "do" | "onEnd" => (&["effect"], "an effect", &[]),
            "strength" => (&["strength"], "a strength", &[]),
            "onHit" | "onKill" | "onHitTaken" => (&["effect", "on-hit"], "an effect or an on-hit", &[]),
            "applies" | "status" | "feeds" | "removes" | "keeps" => (&["status"], "a status", &[]),
            "casts" | "unlocks" => (&["ability"], "an ability", &[]),
            "visual" => (&["look"], "a look", &[]),
            "head" | "chest" | "arms" | "legs" | "feet" => (&["look", "worn look"], "a look or a worn look", &[]),
            "stats" => (&["weapon stats", "armor stats"], "stats", &[]),
            "class" => (&["weapon class"], "a weapon class", &["sword", "axe", "mace", "greatsword", "greataxe", "hammer", "daggers", "bow", "staff", "shield", "wand"]),
            "slot" => (&["armor slot"], "an armor slot", &["head", "chest", "arms", "legs", "feet", "ring"]),
            "set" => (&["armor set"], "an armor set", &["warrior", "mage", "ranger"]),
            "look" => (&["worn look"], "a worn look", &[]),
            "station" => (&["station"], "a station", &["hand"]),
            "placement" => (&["placement"], "a placement", &[]),
            "comfort" => (&["comfort"], "a comfort", &[]),
            "processing" => (&["processing"], "a processing", &[]),
            "menu" => (&["menu"], "a menu", &[]),
            "onInteract" => (&["interaction"], "an interaction", &[]),
            "vfx" => (&["vfx"], "a VFX", &[]),
            "screen" => (&["screen effect"], "a screen effect", &[]),
            _ => return None,
        })
    }
    fn walk(n: &Node, field: Option<&str>, out: &mut Vec<(String, &'static [&'static str], &'static str, Span)>) {
        if let Some(obj) = n.as_object() {
            for mm in obj {
                // Status looks are premades. The same words in a cosmetic's look still name worn-look blocks.
                if mm.key == "statuses" {
                    for b in mm.value.as_object().into_iter().flatten() {
                        for x in b.value.as_object().into_iter().flatten() {
                            if x.key == "look" && x.value.as_str().is_some_and(|s| ["frost", "poison", "fire"].contains(&s)) {
                                continue;
                            }
                            walk(&x.value, Some(x.key.as_str()), out);
                        }
                    }
                    continue;
                }
                // a cosmetic's slot is a vanity slot, not an armor slot block
                if mm.key == "slot" && obj.iter().any(|x| x.key == "look") {
                    continue;
                }
                // a terrain material's like and look are game materials (world_tables checks them), not blocks
                if mm.key == "terrainMaterials" {
                    for b in mm.value.as_object().into_iter().flatten() {
                        for x in b.value.as_object().into_iter().flatten().filter(|x| x.key != "like" && x.key != "look") {
                            walk(&x.value, Some(x.key.as_str()), out);
                        }
                    }
                    continue;
                }
                // { "name": "burst", "slot": "chest" } says the same as "burst"
                if mm.key == "vfx"
                    && let Some(name) = mm.value.get("name").filter(|x| x.as_str().is_some())
                {
                    walk(name, Some("vfx"), out);
                }
                walk(&mm.value, Some(mm.key.as_str()), out);
            }
        } else if let Some(arr) = n.as_array() {
            for x in arr {
                walk(x, field, out);
            }
        } else if let (Some(text), Some((kinds, what, premades))) = (n.as_str(), field.and_then(wants)) {
            let workshop = field == Some("station") && text.starts_with("0x");
            if !workshop && !premades.contains(&text) {
                out.push((text.to_string(), kinds, what, n.span));
            }
        }
    }
    // Names of items: a block of this mod, or a game item. parent: the key this object is under.
    fn items(n: &Node, parent: &str, out: &mut Vec<(String, Span)>) {
        if let Some(obj) = n.as_object() {
            for mm in obj {
                if mm.key == "needs" || mm.key == "burns" {
                    for need in mm.value.as_object().into_iter().flatten() {
                        out.push((need.key.clone(), need.key_span));
                    }
                } else if let Some(text) = mm.value.as_str().filter(|_| matches!(mm.key.as_str(), "dropsLike" | "dropped" | "output") || (mm.key == "like" && matches!(parent, "visual" | "loot" | "look")) || (mm.key == "item" && parent == "entries")) {
                    out.push((text.to_string(), mm.value.span));
                } else {
                    items(&mm.value, &mm.key, out);
                }
            }
        } else if let Some(arr) = n.as_array() {
            for x in arr {
                items(x, parent, out);
            }
        }
    }
    // Names of items in the quest and dialogue sections: what an objective, a condition or an action says is "item", "take" or a build's "prop", and a reward's "items".
    fn quest_items(n: &Node, out: &mut Vec<(String, Span)>) {
        if let Some(obj) = n.as_object() {
            for mm in obj {
                if mm.key == "items" && mm.value.as_object().is_some() {
                    out.extend(mm.value.as_object().into_iter().flatten().map(|it| (it.key.clone(), it.key_span)));
                } else if let (true, Some(text)) = (matches!(mm.key.as_str(), "item" | "take" | "prop"), mm.value.as_str()) {
                    out.push((text.to_string(), mm.value.span));
                } else {
                    quest_items(&mm.value, out);
                }
            }
        } else if let Some(arr) = n.as_array() {
            for x in arr {
                quest_items(x, out);
            }
        }
    }
    fn a_or_an(word: &str) -> &'static str {
        if word.starts_with(['a', 'e', 'i', 'o', 'u']) { "an" } else { "a" }
    }
    let game = cat.and_then(|c| c.section("items"));
    for f in &files {
        let mut names = vec![];
        let mut item_names = vec![];
        if let Some(r) = &f.root {
            walk(r, None, &mut names);
            for c in r.get("creatures").and_then(Node::as_object).into_iter().flatten() {
                if let Some(d) = c.value.get("drops").filter(|d| d.as_str().is_some()) {
                    names.push((d.as_str().unwrap_or("").to_string(), &["loot table"], "a loot table", d.span));
                }
            }
            // a quest's and a step's reward "lootTable"
            for q in r.get("quests").and_then(Node::as_object).into_iter().flatten() {
                let steps = q.value.get("steps").and_then(Node::as_array).into_iter().flatten();
                for rewards in std::iter::once(&q.value).chain(steps).filter_map(|n| n.get("rewards")) {
                    if let Some(t) = rewards.get("lootTable").filter(|t| t.as_str().is_some()) {
                        names.push((t.as_str().unwrap_or("").to_string(), &["loot table"], "a loot table", t.span));
                    }
                }
            }
            items(r, "", &mut item_names);
            for section in ["quests", "dialogues"] {
                if let Some(s) = r.get(section) {
                    quest_items(s, &mut item_names);
                }
            }
            // the "looks" section's entries are looks: their "like" is an item too
            for look in r.get("looks").and_then(Node::as_object).into_iter().flatten() {
                items(&look.value, "visual", &mut item_names);
            }
            for look in r.get("wornLooks").and_then(Node::as_object).into_iter().flatten() {
                items(&look.value, "look", &mut item_names);
            }
        }
        for (name, kinds, what, span) in names {
            if name.contains(':') || code.mentions(&name) || (kinds.contains(&"vfx") && is_guid(&name)) {
                continue;
            }
            // a game status: its id or GUID. The catalog knows the ids.
            if kinds.contains(&"status") && (name.starts_with("0x") || name.starts_with("buff_") || is_guid(&name)) {
                let id = name.strip_prefix("0x").or_else(|| name.strip_prefix("buff_")).and_then(|x| u32::from_str_radix(x, 16).ok());
                if let (Some(buffs), Some(id)) = (cat.and_then(|c| c.section("buffs")), id)
                    && !buffs.ids.contains(&id)
                {
                    out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("the game has no status {name} (the catalog has {} of them)", buffs.ids.len())));
                }
                continue;
            }
            match known.get(&name) {
                None if kinds.contains(&"vfx") => out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("no vfx block \"{name}\" in this mod's blocks/*.json or code (Kiln.Blocks refuses it at load); another mod's is \"mod-id:{name}\", one of the game's \"game:{name}\""))),
                None => out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("no {} block \"{name}\" in this mod's blocks/*.json or code (Kiln.Blocks refuses it at load); another mod's is \"mod-id:{name}\"", kinds[0]))),
                Some((other, ..)) if !kinds.contains(other) => out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("\"{name}\" is {} {other} block; this field takes {what}", a_or_an(other)))),
                _ => {}
            }
        }
        // what an effect spawns: a game template by name, or a GUID
        fn spawned(n: &Node, out: &mut Vec<(String, Span)>) {
            if let Some(obj) = n.as_object() {
                for mm in obj {
                    if let (true, Some(text)) = (mm.key == "spawns", mm.value.as_str()) {
                        out.push((text.to_string(), mm.value.span));
                    } else {
                        spawned(&mm.value, out);
                    }
                }
            } else if let Some(arr) = n.as_array() {
                for x in arr {
                    spawned(x, out);
                }
            }
        }
        let mut spawns = vec![];
        if let Some(r) = &f.root {
            spawned(r, &mut spawns);
        }
        if let Some(templates) = cat.and_then(|c| c.section("templates")) {
            for (name, span) in spawns {
                if !is_guid(&name) && !templates.names.contains(&name) {
                    out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("the game has no template \"{name}\"")));
                }
            }
        }
        for (name, span) in item_names {
            if name.contains(':') || code.mentions(&name) {
                continue;
            }
            match known.get(&name) {
                Some((other, ..)) if !matches!(*other, "spell" | "weapon" | "item" | "wand" | "armor" | "station" | "prop") => out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("\"{name}\" is {} {other} block; this field takes an item", a_or_an(other)))),
                Some(_) => {}
                None => {
                    if game.is_some_and(|g| !g.names.contains(&name)) {
                        out.push(Diagnostic::new(&f.display, codes::BLOCK_REF, span, &format!("no item \"{name}\": not a block in this mod's blocks/*.json or code, and not an item of the game (the definition that names it is refused when content is ready)")));
                    }
                }
            }
        }
        // KL406: a rolled item's recipe tooltip shows what a craft does not make (Kiln.Blocks refuses it at load)
        for section in ["weapons", "wands", "armor", "items", "stations"] {
            for b in f.root.as_ref().and_then(|r| r.get(section)).and_then(Node::as_object).into_iter().flatten() {
                if let Some(rolled) = b.value.get("loot").and_then(|l| l.get("rolled")).filter(|x| x.as_bool() == Some(true))
                    && b.value.get("recipe").is_some()
                {
                    out.push(Diagnostic::new(&f.display, codes::CRAFTED_ROLLED, rolled.span, &format!("block \"{}\" has a recipe and \"rolled\": true: a craft comes out as the lowest allowed rarity or a random level while the recipe tooltip shows its rarity and lowest level; Kiln.Blocks refuses it. Give it a recipe or roll it, not both", b.key)));
                }
            }
        }
    }
}

/// One `vfx` field of a blocks file: "burst", "other-mod:burst", "game:<name>", a GUID, or { "name": ..., "parameters": ... }.
struct VfxUse<'a> {
    name: &'a str,
    span: Span,
    parameters: Option<&'a Node>,
    /// The field can't set parameters: only an effect's does, because anything else starts the VFX from data a server can't lay out.
    fixed_route: Option<&'static str>,
}

impl VfxUse<'_> {
    /// The game's own VFX: "game:<name>" or a bare GUID. Everything else has to be on the player's game through a mod.
    fn is_game(&self) -> bool {
        self.name.starts_with("game:") || is_guid(self.name)
    }
    /// A GUID or a name, without the prefix.
    fn game_name(&self) -> &str {
        self.name.strip_prefix("game:").unwrap_or(self.name)
    }
}

/// Every `vfx` field below a blocks file's root, leaving out the root's own `vfx` section of definitions.
fn vfx_uses(root: &Node) -> Vec<VfxUse<'_>> {
    fn route(key: &str, holder: &Node) -> Option<&'static str> {
        match key {
            "cast" => Some("a spell's cast"),
            "delivery" | "projectile" => Some("a projectile"),
            "visual" => Some("a look"),
            _ if holder.str_of("type") == Some("projectile") => Some("a projectile"),
            // a status has a name and its seconds, and an effect never does
            _ if holder.str_of("name").is_some() && holder.get("seconds").is_some() => Some("a status"),
            _ => None,
        }
    }
    fn go<'a>(n: &'a Node, key: &str, out: &mut Vec<VfxUse<'a>>) {
        if let Some(o) = n.as_object() {
            for mm in o {
                if mm.key == "vfx" {
                    let name = mm.value.as_str().map(|t| (t, mm.value.span)).or_else(|| mm.value.get("name").and_then(|x| x.as_str().map(|t| (t, x.span))));
                    if let Some((name, span)) = name {
                        out.push(VfxUse { name, span, parameters: mm.value.get("parameters"), fixed_route: route(key, n) });
                    }
                    continue;
                }
                go(&mm.value, &mm.key, out);
            }
        } else if let Some(a) = n.as_array() {
            for x in a {
                go(x, key, out);
            }
        }
    }
    let mut out = vec![];
    for mm in root.as_object().into_iter().flatten().filter(|m| m.key != "vfx") {
        // a section's blocks: the key is the block's name, which says nothing about what it is
        for b in mm.value.as_object().into_iter().flatten() {
            go(&b.value, if mm.key == "looks" { "visual" } else { "" }, &mut out);
        }
    }
    out
}

/// The game's numbers and what its VFX system holds (`GameCounts` and `Capacity` in native/services/vfx/cook/vfxcook.h, measured per build). Every limit is a
/// fatal abort in the game; Kiln refuses a mod that would reach one, and this says so before it is deployed.
mod vfx_limits {
    pub const GAME_VFX: u64 = 1575;
    pub const GAME_MESHES: u64 = 638;
    pub const GAME_SOUNDS: u64 = 6;
    pub const GAME_MESH_BYTES: u64 = 17_567_564;
    pub const VFX: u64 = 2048;
    pub const MESHES: u64 = 1024;
    pub const SOUNDS: u64 = 16;
    pub const MESH_BYTES: u64 = 0x110_0000;
    pub const MESH_MARGIN: u64 = 16_384;
    /// The size of a built-in mesh. `quad` and `quad-xz` are the only meshes a block can name today.
    pub const QUAD_BYTES: u64 = 140;
}

/// What a set of mods asks of the VFX system.
#[derive(Debug, Default, Clone, PartialEq)]
struct VfxNeed {
    vfx: u64,
    meshes: u64,
    mesh_bytes: u64,
    sounds: u64,
}

/// What is over, as sentences: empty when it all fits. `game_vfx`: the catalog's count when there is one.
fn vfx_over_budget(need: &VfxNeed, game_vfx: u64) -> Vec<String> {
    use vfx_limits::*;
    let mut over = vec![];
    let room = |cap: u64, used: u64| cap.saturating_sub(used);
    let vfx_room = room(VFX, game_vfx);
    if need.vfx > vfx_room {
        over.push(format!("{} new VFX, and the game has room for {vfx_room} ({VFX} less its {game_vfx})", need.vfx));
    }
    let mesh_room = room(MESHES, GAME_MESHES);
    if need.meshes > mesh_room {
        over.push(format!("{} new meshes, and the game has room for {mesh_room} ({MESHES} less its {GAME_MESHES})", need.meshes));
    }
    let bytes_room = room(MESH_BYTES, GAME_MESH_BYTES + MESH_MARGIN);
    if need.mesh_bytes > bytes_room {
        over.push(format!("{} bytes of VFX mesh memory, and all mods share {bytes_room}", need.mesh_bytes));
    }
    let sound_room = room(SOUNDS, GAME_SOUNDS);
    if need.sounds > sound_room {
        over.push(format!("{} sounds in VFX, and the game has room for {sound_room} ({SOUNDS} less its {GAME_SOUNDS})", need.sounds));
    }
    over
}

/// What one mod's blocks files ask for, by what is shared: a mesh or a sound counts once however many blocks name it.
fn vfx_need(mods: &[LMod]) -> VfxNeed {
    let mut blocks = 0u64;
    let mut meshes = HashSet::new();
    let mut sounds = HashSet::new();
    for m in mods {
        for f in m.files.iter().filter(|f| f.kind == "blocks") {
            for b in f.root.as_ref().and_then(|r| r.get("vfx")).and_then(Node::as_object).into_iter().flatten() {
                blocks += 1;
                if let Some(s) = b.value.str_of("sound") {
                    sounds.insert(s.to_string());
                }
                for p in b.value.get("particles").and_then(Node::as_array).into_iter().flatten() {
                    meshes.insert(p.str_of("mesh").unwrap_or("quad").to_string());
                }
            }
        }
    }
    let n = meshes.len() as u64;
    VfxNeed { vfx: blocks, meshes: n, mesh_bytes: n * vfx_limits::QUAD_BYTES, sounds: sounds.len() as u64 }
}

/// A PNG's size from its header. None: not a PNG, which other rules say.
fn png_size(p: &Path) -> Option<(u32, u32)> {
    use std::io::Read;
    let mut head = [0u8; 24];
    std::fs::File::open(p).ok()?.read_exact(&mut head).ok()?;
    if head[..8] != PNG[..] || head[12..16] != *b"IHDR" {
        return None;
    }
    Some((u32::from_be_bytes(head[16..20].try_into().ok()?), u32::from_be_bytes(head[20..24].try_into().ok()?)))
}

/// What a PNG's header chunks say: size, bits, colour type, and whether it's tagged sRGB or linear.
struct PngInfo {
    w: u32,
    h: u32,
    depth: u8,
    colour: u8,
    srgb: bool,
    linear: bool,
}

fn png_info(p: &Path) -> Option<PngInfo> {
    let data = std::fs::read(p).ok()?;
    if data.len() < 33 || data[..8] != PNG[..] || data[12..16] != *b"IHDR" {
        return None;
    }
    let be = |at: usize| u32::from_be_bytes(data[at..at + 4].try_into().unwrap_or_default());
    let mut info = PngInfo { w: be(16), h: be(20), depth: data[24], colour: data[25], srgb: false, linear: false };
    let mut at = 8;
    while at + 12 <= data.len() {
        let (len, kind) = (be(at) as usize, &data[at + 4..at + 8]);
        if kind == b"IDAT" || at + 12 + len > data.len() {
            break;
        }
        match kind {
            b"sRGB" | b"iCCP" => info.srgb = true,
            // gAMA is 1/gamma x 100000: 45455 is sRGB's, 100000 linear
            b"gAMA" if len == 4 => match be(at + 8) {
                40000..=50000 => info.srgb = true,
                90000..=110000 => info.linear = true,
                _ => {}
            },
            _ => {}
        }
        at += 12 + len;
    }
    Some(info)
}

/// KL333 over one mod: the PNGs of building surfaces, as kiln.surfaces will cook them on each client. A file that isn't there or isn't a PNG is
/// KL501 or KL503, from the schema.
fn surfaces(out: &mut Out, m: &LMod) {
    for f in m.files.iter().filter(|f| f.kind == "blocks") {
        let Some(root) = &f.root else { continue };
        let mut found: Vec<(&Node, String)> = Vec::new();
        for b in root.get("surfaces").and_then(Node::as_object).into_iter().flatten() {
            found.push((&b.value, format!("/surfaces/{}", ptr_seg(&b.key))));
        }
        for section in ["buildingMaterials", "materialTweaks"] {
            for b in root.get(section).and_then(Node::as_object).into_iter().flatten() {
                if let Some(s) = b.value.get("surface").filter(|s| s.as_object().is_some()) {
                    found.push((s, format!("/{section}/{}/surface", ptr_seg(&b.key))));
                }
            }
        }
        for (s, at) in found {
            for part in ["side", "top", "bottom", "edge", "edgeTop", "noise"] {
                let Some(layer) = s.get(part).filter(|l| l.as_object().is_some()) else { continue };
                for key in ["albedo", "emissive", "normal", "roughness", "metallic", "reflectance", "height", "ao"] {
                    let Some(v) = layer.get(key) else { continue };
                    let Some((name, span)) = v.as_str().map(|t| (t, v.span)).or_else(|| v.get("map").and_then(|x| x.as_str().map(|t| (t, x.span)))) else { continue };
                    if name.starts_with('#') {
                        continue;
                    }
                    let Some(p) = png_info(&normalize(&m.root.join(name.replace('\\', "/")))) else { continue };
                    let ptr = format!("{at}/{part}/{key}");
                    let colour = matches!(key, "albedo" | "emissive");
                    let a = if matches!(key, "albedo" | "emissive" | "ao") { "an" } else { "a" };
                    let mut say = |sev: Severity, text: String| out.push(Diagnostic::new(&f.display, codes::SURFACE_TEXTURE, span, &text).severity(sev).pointer(&ptr));
                    if !p.w.is_power_of_two() || !p.h.is_power_of_two() {
                        say(Severity::Error, format!("{name}: {}x{} is not a power of two on both sides (kiln.surfaces refuses this surface, and the material looks like Rough Stone)", p.w, p.h));
                        continue;
                    }
                    if p.w.min(p.h) < 4 || p.w.max(p.h) > 4096 {
                        say(Severity::Error, format!("{name}: {}x{} is outside 4..4096 (kiln.surfaces refuses this surface, and the material looks like Rough Stone)", p.w, p.h));
                        continue;
                    }
                    let grey = matches!(p.colour, 0 | 4);
                    if key == "normal" && grey {
                        say(Severity::Warning, format!("{name} is greyscale: a normal map's red and green are its direction, so this one is flat"));
                    } else if !colour && key != "normal" && !grey {
                        say(Severity::Info, format!("{name} is in colour: {a} {key} map's red channel is read and the rest is ignored; save it as greyscale"));
                    }
                    if colour && p.linear {
                        say(Severity::Warning, format!("{name} is tagged linear, but {a} {key} map is read as sRGB colour: it will look brighter than in your editor"));
                    } else if !colour && p.srgb {
                        say(Severity::Info, format!("{name} is tagged sRGB; {a} {key} map's values are read as they are, as linear data"));
                    }
                    if p.depth == 16 {
                        say(Severity::Info, format!("{name} has 16 bits a channel: it is read at 8"));
                    }
                }
            }
        }
    }
}

fn colour_text(t: &str) -> bool {
    let h = t.strip_prefix('#').unwrap_or(t);
    matches!(h.len(), 6 | 8) && h.bytes().all(|b| b.is_ascii_hexdigit())
}

/// A parameter's value as a block says it: a number is a float, `{"int": n}` an int, `[x, y, z]` a float3, `"#rrggbb"` a colour.
fn vfx_value_type(v: &Node) -> Option<&'static str> {
    if v.as_f64().is_some() {
        return Some("float");
    }
    if let Some(t) = v.as_str() {
        return colour_text(t).then_some("color");
    }
    if let Some(a) = v.as_array() {
        return (a.len() == 3 && a.iter().all(|x| x.as_f64().is_some())).then_some("float3");
    }
    v.get("int").filter(|x| x.as_f64().is_some()).map(|_| "int")
}

/// KL320 to KL324 over one mod. Returns how many references a missing catalog left unchecked.
fn vfx(out: &mut Out, m: &LMod, cat: Option<&Catalog>) -> usize {
    let mut skipped = 0;
    let files: Vec<&LFile> = m.files.iter().filter(|f| f.kind == "blocks").collect();
    for f in &files {
        let Some(root) = &f.root else { continue };
        for b in root.get("vfx").and_then(Node::as_object).into_iter().flatten() {
            for (i, p) in b.value.get("particles").and_then(Node::as_array).into_iter().flatten().enumerate() {
                let Some(tex) = p.get("texture") else { continue };
                let Some(file) = tex.as_str().map(|t| (t, tex.span)).or_else(|| tex.get("file").and_then(|x| x.as_str().map(|t| (t, x.span)))) else { continue };
                let (name, span) = file;
                let Some((w, h)) = png_size(&normalize(&m.root.join(name.replace('\\', "/")))) else { continue };
                let at = format!("/vfx/{}/particles/{i}/texture", ptr_seg(&b.key));
                if !w.is_power_of_two() || !h.is_power_of_two() {
                    out.push(Diagnostic::new(&f.display, codes::VFX_TEXTURE, span, &format!("{name}: {w}x{h} is not a power of two on both sides (Kiln refuses this block)")).pointer(&at));
                } else if w.min(h) < 4 || w.max(h) > 2048 {
                    out.push(Diagnostic::new(&f.display, codes::VFX_TEXTURE, span, &format!("{name}: {w}x{h} is outside 4..2048 (Kiln refuses this block)")).pointer(&at));
                } else if let Some(grid) = p.get("flipbook") {
                    let side = |k: &str| grid.get(k).and_then(Node::as_f64).map_or(1, |v| v as u32);
                    let (c, r) = (side("columns"), side("rows"));
                    if c > 0 && r > 0 && (w % c != 0 || h % r != 0) {
                        out.push(Diagnostic::new(&f.display, codes::VFX_FLIPBOOK, grid.span, &format!("{name}: {w}x{h} doesn't divide into {c} columns and {r} rows: frames will be cut")).pointer(&format!("/vfx/{}/particles/{i}/flipbook", ptr_seg(&b.key))));
                    }
                }
            }
        }
        // A material's maps: what the builder refuses, said earlier. Bigger than a particle's, as a surface is close to the camera.
        for b in root.get("materials").and_then(Node::as_object).into_iter().flatten() {
            let map = |key: &str| b.value.get(key).and_then(|v| v.as_str().map(|t| (t, v.span)).or_else(|| v.get("map").and_then(|x| x.as_str().map(|t| (t, x.span)))));
            for key in ["albedo", "normal", "params", "metallicRoughness", "occlusion", "emissive"] {
                let Some((name, span)) = map(key) else { continue };
                if name.starts_with('#') {
                    continue;
                }
                let Some((w, h)) = png_size(&normalize(&m.root.join(name.replace('\\', "/")))) else { continue };
                let at = format!("/materials/{}/{key}", ptr_seg(&b.key));
                if !w.is_power_of_two() || !h.is_power_of_two() {
                    out.push(Diagnostic::new(&f.display, codes::VFX_TEXTURE, span, &format!("{name}: {w}x{h} is not a power of two on both sides (Kiln refuses this block)")).pointer(&at));
                } else if w.min(h) < 4 || w.max(h) > 4096 {
                    out.push(Diagnostic::new(&f.display, codes::VFX_TEXTURE, span, &format!("{name}: {w}x{h} is outside 4..4096 (Kiln refuses this block)")).pointer(&at));
                }
            }
        }
        for u in vfx_uses(root) {
            if let Some(p) = u.parameters {
                if let Some(route) = u.fixed_route {
                    out.push(Diagnostic::new(&f.display, codes::VFX_PARAMETER, p.span, &format!("{route} cannot set a VFX's parameters: the game's data takes a block laid out by the VFX, which only clients have (Kiln.Blocks refuses it). Put the VFX on an effect, or leave them out")).severity(Severity::Error));
                } else if !u.is_game() {
                    out.push(Diagnostic::new(&f.display, codes::VFX_PARAMETER, p.span, &format!("\"{}\" is a VFX block, which has no named parameters yet (Kiln.Blocks refuses it). Only a game VFX takes them", u.name)).severity(Severity::Error));
                }
            }
            if !u.is_game() {
                continue;
            }
            let Some(c) = cat.filter(|c| c.vfx_count > 0) else {
                skipped += 1;
                continue;
            };
            let key = u.game_name();
            let lower = key.to_ascii_lowercase();
            let hit = c.vfx.get(if is_guid(key) { lower.as_str() } else { key });
            let Some(info) = hit else {
                let mut d = if is_guid(key) {
                    Diagnostic::new(&f.display, codes::VFX_REF, u.span, &format!("no game VFX with GUID {lower}: Kiln's guard ignores it, and nothing shows"))
                } else {
                    Diagnostic::new(&f.display, codes::VFX_REF, u.span, &format!("no game VFX \"{key}\" in the catalog"))
                };
                if !is_guid(key) {
                    let mut names: Vec<&str> = c.vfx.keys().map(String::as_str).filter(|n| !is_guid(n)).collect();
                    names.sort_unstable();
                    if let Some(b) = suggest::best(key, &names) {
                        d.message.push_str(&format!("; did you mean \"{b}\"?"));
                        d.fixes.push(Fix::new(&format!("Replace with \"game:{b}\""), vec![TextEdit::new(&f.display, u.span, &quote(&format!("game:{b}")))]));
                    }
                }
                out.push(d);
                continue;
            };
            if !is_guid(key) && !info.by_name {
                out.push(
                    Diagnostic::new(&f.display, codes::VFX_REF, u.span, &format!("\"{key}\" is the catalog's label for this VFX: nothing at run time finds it by that name; use its GUID"))
                        .with_fix(Fix::new("Replace with its GUID", vec![TextEdit::new(&f.display, u.span, &quote(&format!("game:{}", info.guid)))])),
                );
            }
            // the game silently ignores a name or type its VFX doesn't have, so this is the only place that says so
            if u.fixed_route.is_some() {
                continue;
            }
            for mm in u.parameters.and_then(Node::as_object).into_iter().flatten() {
                let hash = fnv1a(mm.key.as_bytes());
                let Some(param) = info.params.iter().find(|p| p.hash == hash) else {
                    let known: Vec<&str> = info.params.iter().filter_map(|p| p.name.as_deref()).collect();
                    let unnamed = info.params.len() - known.len();
                    let mut d = Diagnostic::new(&f.display, codes::VFX_PARAMETER, mm.key_span, &format!(
                        "{} has no parameter \"{}\", and the game ignores what it doesn't have; it has {}{}", info.name, mm.key,
                        if known.is_empty() { "no named ones".to_string() } else { known.join(", ") },
                        if unnamed > 0 { format!(" and {unnamed} without a known name") } else { String::new() }
                    ));
                    if let Some(b) = suggest::best(&mm.key, &known) {
                        d.message.push_str(&format!("; did you mean \"{b}\"?"));
                        d.fixes.push(Fix::new(&format!("Rename to \"{b}\""), vec![TextEdit::new(&f.display, mm.key_span, &quote(b))]));
                    }
                    out.push(d);
                    continue;
                };
                let Some(given) = vfx_value_type(&mm.value) else { continue };
                if given != param.ty {
                    let settable = matches!(param.ty.as_str(), "float" | "int" | "float3" | "color");
                    let how = match param.ty.as_str() {
                        "float" => "a number",
                        "int" => "{\"int\": n}",
                        "float3" => "[x, y, z]",
                        "color" => "\"#rrggbb\"",
                        _ => "",
                    };
                    let note = if settable { format!("write it as {how}") } else { format!("a block can't set {}", param.ty) };
                    let article = |t: &str| if t.starts_with(['a', 'e', 'i', 'o', 'u']) { "an" } else { "a" };
                    out.push(Diagnostic::new(&f.display, codes::VFX_PARAMETER, mm.value.span, &format!("\"{}\" of {} is {} {}, and this is {} {given}: the game ignores it; {note}", mm.key, info.name, article(&param.ty), param.ty, article(given))));
                }
            }
        }
    }
    world_tables(out, m, &files, cat);
    // the budget: this mod with everything it is checked against. The first block of the file says it, once per mod
    if let Some((f, b)) = files.iter().find_map(|f| f.root.as_ref().and_then(|r| r.get("vfx")).and_then(Node::as_object).and_then(|o| o.first()).map(|b| (*f, b))) {
        let game = cat.map(|c| c.vfx_count as u64).filter(|n| *n > 0).unwrap_or(vfx_limits::GAME_VFX);
        let what = if out.mods.iter().filter(|x| !x.files.is_empty()).count() > 1 { "Together, the mods checked here ask for" } else { "This mod asks for" };
        let over = vfx_over_budget(&vfx_need(out.mods), game);
        if !over.is_empty() {
            out.push(Diagnostic::new(&f.display, codes::VFX_BUDGET, b.key_span, &format!("{what} {}: past a limit the game aborts, so Kiln refuses the mod that reaches it and none of its VFX load", over.join("; "))));
        }
    }
    skipped
}

/// The building-material ids every mod shares (kiln.worldtables, which refuses past this at boot): 128 slots, 85 the game's, 6 of those deprecated.
const MATERIAL_ROOM: usize = 49;
/// From here on, a mod takes a real share of that room.
const MATERIAL_MANY: usize = 5;
/// The terrain-material ids every mod shares: 127 slots, 116 the game's. A room of its own, apart from the building materials'.
const TERRAIN_ROOM: usize = 11;

/// What kiln.worldtables refuses at boot, said earlier: past the room for new materials, a game material that isn't one, a material in a mod that doesn't ask players to have it.
fn world_tables(out: &mut Out, m: &LMod, files: &[&LFile], cat: Option<&Catalog>) {
    let of = |f: &LFile, key: &str| f.root.as_ref().and_then(|r| r.get(key)).and_then(Node::as_object).map(<[_]>::to_vec).unwrap_or_default();
    let (side, clients, _) = side_clients(m);
    let mine: Vec<(String, Span, String)> = files
        .iter()
        .flat_map(|f| of(f, "buildingMaterials").into_iter().map(|b| (b.key.clone(), b.key_span, f.display.clone())).collect::<Vec<_>>())
        .collect();
    if let Some((name, at, file)) = mine.first() {
        if clients != "mod" {
            out.push(Diagnostic::new(file, codes::CLIENTS_DEFINITIONS, *at, &format!("building material \"{name}\" needs \"clients\": \"mod\", and this mod says \"{clients}\": cells reach players as ids, so kiln.worldtables refuses it")));
        } else if side != "both" {
            out.push(Diagnostic::new(file, codes::CLIENTS_DEFINITIONS, *at, &format!("building material \"{name}\" needs \"side\": \"both\", and this mod says \"{side}\": both sides read the material's id from the same tables")));
        }
        if mine.len() >= MATERIAL_MANY {
            out.push(Diagnostic::new(file, codes::MATERIAL_COUNT, *at, &format!("this mod has {} building materials: all mods share {MATERIAL_ROOM} ids, and past that the game can't take more", mine.len())));
        }
    }
    // Every mod checked here takes ids in sorted GUID order, so which block is over depends on the others: all of them are named by the count.
    let total: usize = out.mods.iter().flat_map(|x| x.files.iter()).filter(|f| f.kind == "blocks").map(|f| of(f, "buildingMaterials").len()).sum();
    if total > MATERIAL_ROOM
        && let Some((name, at, file)) = mine.first()
    {
        let what = if out.mods.iter().filter(|x| !x.files.is_empty()).count() > 1 { "Together, the mods checked here have" } else { "This mod has" };
        out.push(Diagnostic::new(file, codes::MATERIAL_BUDGET, *at, &format!("{what} {total} building materials, and the game has ids for {MATERIAL_ROOM}: kiln.worldtables refuses those past that by name, starting from the last in sorted GUID order (\"{name}\" is one of this mod's)")));
    }
    // Terrain materials: the same rules, their own room.
    let terrain: Vec<(String, Span, String)> = files
        .iter()
        .flat_map(|f| of(f, "terrainMaterials").into_iter().map(|b| (b.key.clone(), b.key_span, f.display.clone())).collect::<Vec<_>>())
        .collect();
    if let Some((name, at, file)) = terrain.first() {
        if clients != "mod" {
            out.push(Diagnostic::new(file, codes::CLIENTS_DEFINITIONS, *at, &format!("terrain material \"{name}\" needs \"clients\": \"mod\", and this mod says \"{clients}\": cells reach players as ids, so kiln.worldtables refuses it")));
        } else if side != "both" {
            out.push(Diagnostic::new(file, codes::CLIENTS_DEFINITIONS, *at, &format!("terrain material \"{name}\" needs \"side\": \"both\", and this mod says \"{side}\": both sides read the material's id from the same tables")));
        }
        let total: usize = out.mods.iter().flat_map(|x| x.files.iter()).filter(|f| f.kind == "blocks").map(|f| of(f, "terrainMaterials").len()).sum();
        if total > TERRAIN_ROOM {
            let what = if out.mods.iter().filter(|x| !x.files.is_empty()).count() > 1 { "Together, the mods checked here have" } else { "This mod has" };
            out.push(Diagnostic::new(file, codes::MATERIAL_BUDGET, *at, &format!("{what} {total} terrain materials, and the game has ids for {TERRAIN_ROOM}: kiln.worldtables refuses those past that by name, starting from the last in sorted GUID order (\"{name}\" is one of this mod's)")));
        }
    }
    // A catalog older than the section has no word on it.
    let Some(section) = cat.and_then(|c| c.section("materials")).filter(|s| !s.names.is_empty()) else { return };
    let known: Vec<&str> = section.names_list.iter().map(String::as_str).collect();
    for f in files {
        for b in of(f, "terrainMaterials") {
            for key in ["like", "look"] {
                let Some(v) = b.value.get(key) else { continue };
                let Some(text) = v.as_str() else { continue };
                let cell = text.strip_prefix("Cell").is_some_and(|n| n.parse::<u8>().is_ok_and(|x| x > 0 && x < 128));
                if cell || section.names.contains(text) {
                    continue;
                }
                let mut d = Diagnostic::new(&f.display, codes::MATERIAL_REF, v.span, &format!("\"{text}\" is not a game terrain material the catalog knows; a block that names one is refused at boot"));
                if let Some(best) = suggest::best(text, &known) {
                    d.message.push_str(&format!("; did you mean \"{best}\"?"));
                    d.fixes.push(Fix::new(&format!("Replace with \"{best}\""), vec![TextEdit::new(&f.display, v.span, &quote(best))]));
                }
                out.push(d);
            }
        }
        for b in of(f, "materialTweaks") {
            let Some(v) = b.value.get("material") else { continue };
            let Some(text) = v.as_str() else { continue };
            let cell = text.strip_prefix("Cell").is_some_and(|n| n.parse::<u8>().is_ok_and(|x| x > 0));
            if cell || section.names.contains(text) || section.guids.contains(&text.to_ascii_lowercase()) {
                continue;
            }
            let mut d = Diagnostic::new(&f.display, codes::MATERIAL_REF, v.span, &format!("\"{text}\" is not a game material the catalog knows; a block that names one is refused at boot"));
            if let Some(best) = suggest::best(text, &known) {
                d.message.push_str(&format!("; did you mean \"{best}\"?"));
                d.fixes.push(Fix::new(&format!("Replace with \"{best}\""), vec![TextEdit::new(&f.display, v.span, &quote(best))]));
            }
            out.push(d);
        }
    }
}

/// What the boot build of native data refuses (native/services/quests/questpack.cpp): the whole mod's native data is left out,
/// so these are errors. docs/kiln/services/npcs.md.
fn quests_native(out: &mut Out, m: &LMod) {
    let files: Vec<&LFile> = m.files.iter().filter(|f| f.kind == "quests").collect();
    if files.is_empty() {
        return;
    }
    let voices: Vec<String> = m
        .files
        .iter()
        .filter(|f| f.kind == "cook")
        .filter_map(|f| f.root.as_ref())
        .filter_map(|r| r.get("steps").and_then(Node::as_array))
        .flatten()
        .filter(|s| s.str_of("op") == Some("voiceover"))
        .filter_map(|s| s.str_of("name").map(str::to_string))
        .collect();
    let mut names: Vec<String> = vec![];
    let mut npc_ids: Vec<String> = vec![];
    let mut game_dialogues: Vec<String> = vec![];
    for f in &files {
        let Some(r) = &f.root else { continue };
        names.extend(r.get("knowledge").and_then(Node::as_object).into_iter().flatten().map(|k| k.key.clone()));
        npc_ids.extend(r.get("npcs").and_then(Node::as_object).into_iter().flatten().map(|n| n.key.clone()));
        game_dialogues.extend(r.get("dialogues").and_then(Node::as_object).into_iter().flatten().filter(|d| d.value.get("native").and_then(Node::as_bool) != Some(false)).map(|d| d.key.clone()));
    }
    // "near": "player", a place, or an NPC of this mod
    let near = |out: &mut Out, f: &LFile, n: Option<&Node>, what: &str| {
        let Some(v) = n else { return };
        let Some(s) = v.as_str() else { return };
        if s != "player" && !s.contains(':') && !npc_ids.iter().any(|x| x == s) {
            out.push(Diagnostic::new(&f.display, codes::QUEST_REF, v.span, &format!("{what}: \"near\": no NPC \"{s}\" in this mod's quest files (\"player\", an id of npcs, or a place)")));
        }
    };
    // what has no form in the game's UI: it runs, and a player of a native quest does not see it
    let no_form = |out: &mut Out, f: &LFile, list: Option<&Node>, what: &str| {
        for a in list.and_then(Node::as_array).into_iter().flatten() {
            if let Some(t) = a.member("toast") {
                out.push(Diagnostic::new(&f.display, codes::NATIVE_QUEST, t.key_span, &format!("{what}: \"toast\" is a chat line now: the game has no free-text notification. Put it in a step's text or an NPC's reply")).severity(Severity::Warning));
            }
            if let Some(t) = a.member("talk") {
                out.push(Diagnostic::new(&f.display, codes::NATIVE_QUEST, t.key_span, &format!("{what}: \"talk\" opens a chat conversation: the game's menu opens only when a player talks to the NPC. Give the NPC a marker and a hint instead")).severity(Severity::Warning));
            }
        }
    };
    let bad = |out: &mut Out, f: &LFile, span: Span, msg: &str| out.push(Diagnostic::new(&f.display, codes::NATIVE_QUEST, span, msg));
    let voice = |out: &mut Out, f: &LFile, n: Option<&Node>, what: &str| {
        let Some(v) = n else { return };
        let Some(s) = v.as_str() else { return };
        if !is_guid(s) && !voices.iter().any(|x| x == s) {
            out.push(Diagnostic::new(&f.display, codes::NATIVE_QUEST, v.span, &format!("{what}: no voice-over \"{s}\" in this mod's assets/cook.json")));
        }
    };
    for f in &files {
        let Some(r) = &f.root else { continue };
        for q in r.get("quests").and_then(Node::as_object).into_iter().flatten() {
            if q.value.str_of("journal") == Some("chat") {
                continue;
            }
            if q.value.get("name").is_none() {
                bad(out, f, q.key_span, &format!("quest {}: a journal entry needs \"name\"", q.key));
            }
            let what = format!("quest {}", q.key);
            for key in ["rewards", "onStart", "onFail"] {
                no_form(out, f, q.value.get(key), &what);
            }
            for s in q.value.get("steps").and_then(Node::as_array).into_iter().flatten() {
                if s.get("text").is_none() && s.get("title").is_none() {
                    bad(out, f, s.span, &format!("quest {}: a step in the journal needs \"text\"", q.key));
                }
                voice(out, f, s.get("voice"), &what);
                for key in ["onEnter", "onDone"] {
                    no_form(out, f, s.get(key), &what);
                }
                near(out, f, s.get("site").and_then(|x| x.get("near")), &what);
                match s.get("spawn") {
                    Some(sp) if sp.as_array().is_some() => {
                        for one in sp.as_array().into_iter().flatten() {
                            near(out, f, one.get("near"), &what);
                        }
                    }
                    Some(sp) => near(out, f, sp.get("near"), &what),
                    None => {}
                }
                let objectives = s.get("objective").into_iter().chain(s.get("objectives").and_then(Node::as_array).into_iter().flatten());
                for o in objectives {
                    if let Some(site) = o.get("reach").and_then(|x| x.member("site"))
                        && s.get("site").is_none()
                    {
                        bad(out, f, site.key_span, &format!("quest {}: \"reach\": \"site\" needs the step's \"site\"", q.key));
                    }
                }
            }
        }
        for n in r.get("npcs").and_then(Node::as_object).into_iter().flatten() {
            let clone = n.value.get("clone").is_some_and(|c| c.as_bool() != Some(false));
            near(out, f, n.value.get("place").and_then(|p| p.get("near")), &format!("npc {}", n.key));
            if let Some(dn) = n.value.get("dialogue")
                && dn.as_str().is_some_and(|x| game_dialogues.iter().any(|g| g == x))
                && n.value.get("template").is_none()
            {
                bad(out, f, n.key_span, &format!("npc {}: its dialogue is in the game's menu, which belongs to an NPC's template: name one in \"template\" (or say \"native\": false on the dialogue)", n.key));
            }
            if let Some(roster) = n.value.member("roster") {
                if !clone {
                    bad(out, f, roster.key_span, &format!("npc {}: \"roster\" needs \"clone\": the game's own NPCs are in the roster already", n.key));
                }
                if let Some(u) = roster.value.get("unlock")
                    && let Some(s) = u.as_str()
                    && !s.starts_with("0x")
                    && !s.starts_with("npc:")
                    && !names.iter().any(|x| x == s)
                {
                    bad(out, f, u.span, &format!("npc {}: unknown knowledge \"{s}\" (declare it in \"knowledge\", or use 0x... or npc:<debugName>.unlock)", n.key));
                }
                // the game's Survivors list shows the summon tool's name and description for the NPC
                if let Some(tool) = roster.value.member("summonItem")
                    && tool.value.as_object().is_some()
                {
                    let per_language = |slot: &str| {
                        tool.value.get("texts").and_then(Node::as_object).into_iter().flatten().any(|l| l.value.get(slot).is_some())
                    };
                    if tool.value.get("displayName").is_some() || per_language("name") || (n.value.get("description").is_some() && (tool.value.get("description").is_some() || per_language("description"))) {
                        out.push(Diagnostic::new(&f.display, codes::SUMMON_ITEM_TEXT, tool.key_span, &format!("npc {}: the Survivors list shows this tool's name and description for the NPC, so the build uses the NPC's \"names\" and \"description\". Put what the tool is in its \"caption\" or \"lore\"", n.key)));
                    }
                }
            }
        }
        for d in r.get("dialogues").and_then(Node::as_object).into_iter().flatten() {
            if d.value.get("native").and_then(Node::as_bool) == Some(false) {
                continue;
            }
            let start = d.value.str_of("start").unwrap_or("start");
            let nodes = d.value.get("nodes");
            if nodes.and_then(|ns| ns.get(start)).is_none() {
                bad(out, f, d.key_span, &format!("dialogue {}: \"start\" must name a node (default \"start\")", d.key));
            }
            let what = format!("dialogue {}", d.key);
            for g in d.value.get("greetings").and_then(Node::as_array).into_iter().flatten() {
                voice(out, f, g.get("voice"), &what);
            }
            for key in ["farewell", "transition"] {
                for g in d.value.get(key).and_then(Node::as_array).into_iter().flatten() {
                    voice(out, f, g.get("voice"), &what);
                }
            }
            for node in nodes.and_then(Node::as_object).into_iter().flatten() {
                voice(out, f, node.value.get("voice"), &what);
                no_form(out, f, node.value.get("do"), &what);
                for o in node.value.get("options").and_then(Node::as_array).into_iter().flatten() {
                    voice(out, f, o.get("voice"), &what);
                    no_form(out, f, o.get("do"), &what);
                }
            }
        }
    }
}

/// "start" is both a dialogue's first node (a member of the dialogue object) and an action (a member of an action
/// object): an action's value is what this looks at, so a dialogue's own "start" is left out.
fn looks_like_action_start(dialogues: &Node, v: &Node) -> bool {
    !dialogues.as_object().into_iter().flatten().any(|d| d.value.get("start").is_some_and(|s| s.span.start == v.span.start))
}

// --------------------------------------------------------------------------------------------- skills ----

/// What Kiln.Skills' Validate (TreeRuntime.cs) refuses beyond the schema: the whole tree fails to register.
fn skills(out: &mut Out, f: &LFile) {
    let Some(r) = &f.root else { return };
    for t in r.get("trees").and_then(Node::as_array).into_iter().flatten() {
        let nodes: Vec<&Node> = t.get("nodes").and_then(Node::as_array).into_iter().flatten().collect();
        let ids: Vec<&str> = nodes.iter().filter_map(|n| n.str_of("id")).collect();
        let tree = t.str_of("id").unwrap_or("?");
        let mut seen: HashMap<&str, Span> = HashMap::new();
        let mut cells: HashMap<(i64, i64), Span> = HashMap::new();
        for n in &nodes {
            if let Some(idn) = n.get("id")
                && let Some(id) = idn.as_str()
            {
                if let Some(first) = seen.get(id) {
                    out.push(Diagnostic::new(&f.display, codes::DUPLICATE_NAME, idn.span, &format!("tree {tree}: node \"{id}\" twice (Kiln.Skills refuses the tree)")).with_related(&f.display, *first, "first here"));
                } else {
                    seen.insert(id, idn.span);
                }
            }
            // "col" / "row" win over "position"; a node with neither sits at 0, 0
            let at = |key: &str, i: usize| {
                n.get(key).and_then(Node::as_f64).or_else(|| n.get("position").and_then(Node::as_array).filter(|p| p.len() == 2).and_then(|p| p[i].as_f64())).unwrap_or(0.0) as i64
            };
            let cell = (at("col", 0), at("row", 1));
            if let Some(first) = cells.get(&cell) {
                out.push(Diagnostic::new(&f.display, codes::TALENT_CELL, n.span, &format!("tree {tree}: two nodes at column {}, row {} (Kiln.Skills refuses the tree; a node without \"col\" and \"row\" sits at 0, 0)", cell.0, cell.1)).with_related(&f.display, *first, "the other node"));
            } else {
                cells.insert(cell, n.span);
            }
            let req: Vec<&Node> = match n.get("requires") {
                Some(x) if x.as_str().is_some() => vec![x],
                Some(x) => x.as_array().into_iter().flatten().collect(),
                None => vec![],
            };
            for x in req {
                let Some(name) = x.as_str() else { continue };
                if ids.contains(&name) {
                    continue;
                }
                let mut d = Diagnostic::new(&f.display, codes::UNKNOWN_REF, x.span, &format!("tree {tree}: requires unknown node \"{name}\" (Kiln.Skills refuses the tree)")).severity(Severity::Error);
                // a node id given twice must not tie with itself
                let mut candidates = ids.clone();
                candidates.sort_unstable();
                candidates.dedup();
                if let Some(b) = suggest::best(name, &candidates) {
                    d.message.push_str(&format!("; did you mean \"{b}\"?"));
                    d.fixes.push(Fix::new(&format!("Replace with \"{b}\""), vec![TextEdit::new(&f.display, x.span, &quote(b))]));
                }
                out.push(d);
            }
        }
    }
}

// --------------------------------------------------------------------------------------------- schematics ----

fn schematic(out: &mut Out, f: &LFile) {
    let Some(r) = &f.root else { return };
    if let Some(sz) = r.get("size")
        && let Some(a) = sz.as_array()
        && a.len() == 3
    {
        let n: f64 = a.iter().map(|x| x.as_f64().unwrap_or(0.0)).product();
        if n > (1u64 << 22) as f64 {
            out.push(Diagnostic::new(&f.display, codes::SCHEMATIC_SIZE, sz.span, &format!("{n} cells: Kiln.Build reads schematics of at most 4,194,304 cells")));
        }
    }
}

// --------------------------------------------------------------------------------------------- licences ----

const SPDX: &[&str] = &[
    "0BSD", "AFL-3.0", "AGPL-3.0-only", "AGPL-3.0-or-later", "Apache-2.0", "Artistic-2.0", "BSD-1-Clause", "BSD-2-Clause", "BSD-3-Clause",
    "BSD-3-Clause-Clear", "BSL-1.0", "BlueOak-1.0.0", "CC-BY-3.0", "CC-BY-4.0", "CC-BY-NC-4.0", "CC-BY-NC-SA-4.0", "CC-BY-ND-4.0",
    "CC-BY-SA-3.0", "CC-BY-SA-4.0", "CC0-1.0", "CDDL-1.0", "ECL-2.0", "EPL-1.0", "EPL-2.0", "EUPL-1.2", "GPL-2.0-only",
    "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "ISC", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only",
    "LGPL-3.0-or-later", "MIT", "MIT-0", "MPL-2.0", "MS-PL", "MS-RL", "NCSA", "ODbL-1.0", "OFL-1.1", "OSL-3.0", "PostgreSQL",
    "Python-2.0", "Unlicense", "UPL-1.0", "Vim", "WTFPL", "X11", "Zlib", "zlib-acknowledgement", "LicenseRef-Proprietary",
];
const SPDX_DEPRECATED: &[(&str, &str)] = &[
    ("GPL-2.0", "GPL-2.0-only"), ("GPL-2.0+", "GPL-2.0-or-later"), ("GPL-3.0", "GPL-3.0-only"), ("GPL-3.0+", "GPL-3.0-or-later"),
    ("LGPL-2.1", "LGPL-2.1-only"), ("LGPL-3.0", "LGPL-3.0-only"), ("AGPL-3.0", "AGPL-3.0-only"), ("MIT License", "MIT"),
    ("Apache 2.0", "Apache-2.0"), ("Apache2", "Apache-2.0"), ("Apache-2", "Apache-2.0"), ("Apache License 2.0", "Apache-2.0"),
    ("GPLv3", "GPL-3.0-only"), ("GPLv2", "GPL-2.0-only"), ("BSD", "BSD-3-Clause"), ("CC0", "CC0-1.0"), ("Public Domain", "Unlicense"),
    ("MPL2", "MPL-2.0"), ("MPL 2.0", "MPL-2.0"),
];

/// Checks an SPDX expression: Some(problem, suggestion) when it is not one.
pub fn spdx_problem(expr: &str) -> Option<(String, Option<String>)> {
    let e = expr.trim();
    if let Some((_, good)) = SPDX_DEPRECATED.iter().find(|(bad, _)| bad.eq_ignore_ascii_case(e)) {
        return Some((format!("\"{e}\" is not an SPDX licence expression"), Some(good.to_string())));
    }
    let toks: Vec<String> = e.replace('(', " ( ").replace(')', " ) ").split_whitespace().map(str::to_string).collect();
    let mut want_id = true;
    let mut depth = 0i32;
    for t in &toks {
        match t.as_str() {
            "(" if want_id => depth += 1,
            ")" if !want_id => {
                depth -= 1;
                if depth < 0 {
                    return Some(("unbalanced parentheses".into(), None));
                }
            }
            "AND" | "OR" | "WITH" if !want_id => want_id = true,
            id if want_id => {
                let base = id.trim_end_matches('+');
                if !(SPDX.iter().any(|s| s.eq_ignore_ascii_case(base)) || base.starts_with("LicenseRef-") || base.starts_with("DocumentRef-") || base.ends_with("-exception")) {
                    let sug = SPDX.iter().find(|s| suggest::distance(s, base) <= 2).map(|s| s.to_string());
                    return Some((format!("\"{id}\" is not a licence id kiln lint knows (SPDX ids: MIT, Apache-2.0, GPL-3.0-only, ...; LicenseRef-<name> for your own)"), sug));
                }
                want_id = false;
            }
            other => return Some((format!("unexpected \"{other}\" in the licence expression"), None)),
        }
    }
    if want_id || depth != 0 {
        return Some(("an incomplete licence expression".into(), None));
    }
    None
}

const NOTICE_PREFIXES: &[&str] = &["license", "licence", "notice", "third-party", "third_party", "thirdparty", "copying", "credits"];

fn is_game_file(name: &str) -> bool {
    let n = name.to_ascii_lowercase();
    n.ends_with(".kfc") || n.ends_with(".kfc_resources") || n == "enshrouded.exe" || n == "enshrouded_server.exe" || (n.starts_with("enshrouded_") && n.ends_with(".dat"))
}

fn walk_files(root: &Path, depth: usize, out: &mut Vec<PathBuf>) {
    if depth > 6 {
        return;
    }
    let Ok(rd) = std::fs::read_dir(root) else { return };
    for e in rd.flatten() {
        let p = e.path();
        let name = e.file_name().to_string_lossy().into_owned();
        if p.is_dir() {
            if !super::SKIP_DIRS.contains(&name.as_str()) && !(depth > 0 && Linter::is_mod_root(&p)) {
                walk_files(&p, depth + 1, out);
            }
        } else {
            out.push(p);
        }
    }
}

fn licences(out: &mut Out, m: &LMod) {
    let Some(mf) = m.manifest() else { return };
    let Some(root) = &mf.root else { return };
    match root.get("license") {
        None => out.push(
            Diagnostic::new(&mf.display, codes::NO_LICENSE, Span::new(root.span.start, root.span.start + 1), "no \"license\": say how others may use the mod (an SPDX expression, e.g. MIT)")
                .with_fix(insert_member_fix(mf, "license", "MIT").unwrap_or_else(|| Fix::new("Add a licence", vec![]))),
        ),
        Some(l) => {
            if let Some(s) = l.as_str()
                && let Some((msg, sug)) = spdx_problem(s)
            {
                let mut d = Diagnostic::new(&mf.display, codes::BAD_LICENSE, l.span, &msg);
                if let Some(g) = sug {
                    d.message.push_str(&format!("; did you mean \"{g}\"?"));
                    d.fixes.push(Fix::new(&format!("Write \"{g}\""), vec![TextEdit::new(&mf.display, l.span, &quote(&g))]));
                }
                out.push(d);
            }
        }
    }
    let mut all = Vec::new();
    walk_files(&m.root, 0, &mut all);
    all.sort();
    // game files must never be in a mod
    for p in all.iter().filter(|p| p.file_name().is_some_and(|n| is_game_file(&n.to_string_lossy()))) {
        out.push(Diagnostic::new(&display(p), codes::GAME_DATA, Span::default(), "a game file inside the mod: mods ship sources and a cook recipe, never the game's data (docs/kiln/modding/models.md)").with_related(&mf.display, Span::default(), "the mod"));
    }
    // bundled third-party DLLs need a notice
    let notice = std::fs::read_dir(&m.root).is_ok_and(|rd| {
        rd.flatten().any(|e| {
            let n = e.file_name().to_string_lossy().to_ascii_lowercase();
            NOTICE_PREFIXES.iter().any(|p| n.starts_with(p)) || n == "licenses" || n == "licences"
        })
    });
    if !notice {
        let mut own: HashSet<String> = HashSet::new();
        for k in ["entryAssembly"] {
            if let Some(s) = root.str_of(k) {
                own.insert(s.to_ascii_lowercase());
            }
        }
        for k in ["exports", "dlls"] {
            for e in root.get(k).and_then(Node::as_array).into_iter().flatten() {
                if let Some(s) = e.as_str() {
                    own.insert(s.replace('\\', "/").to_ascii_lowercase());
                }
            }
        }
        let third: Vec<String> = all
            .iter()
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")))
            .filter_map(|p| p.strip_prefix(&m.root).ok().map(|r| r.to_string_lossy().replace('\\', "/")))
            .filter(|r| !own.contains(&r.to_ascii_lowercase()) && !r.rsplit('/').next().unwrap_or("").to_ascii_lowercase().starts_with("kiln."))
            .collect();
        if !third.is_empty() {
            out.push(Diagnostic::new(&mf.display, codes::THIRD_PARTY, Span::new(root.span.start, root.span.start + 1), &format!(
                "the mod bundles {} that it does not build ({}): add their licence texts (a LICENSE / THIRD-PARTY-NOTICES file or a licenses/ folder in the mod)",
                if third.len() == 1 { "a DLL".to_string() } else { format!("{} DLLs", third.len()) },
                third.iter().take(5).cloned().collect::<Vec<_>>().join(", ")
            )));
        }
    }
    // cooked game-derived files in a git work tree must be ignored
    let cooked: Vec<PathBuf> = [m.root.join("assets/cooked")].into_iter().filter(|p| p.is_dir()).collect();
    if !cooked.is_empty() && in_git(&m.root) && !gitignored(&m.root) {
        out.push(Diagnostic::new(&mf.display, codes::COOKED_COMMITTED, Span::default(), "assets/cooked/ holds files made from the game's data: add \"assets/cooked/\" to the mod's .gitignore (they must never be committed or shipped)"));
    }
}

fn in_git(dir: &Path) -> bool {
    dir.ancestors().any(|a| a.join(".git").exists())
}

fn gitignored(dir: &Path) -> bool {
    for a in dir.ancestors() {
        if let Ok(t) = std::fs::read_to_string(a.join(".gitignore"))
            && t.lines().any(|l| !l.trim_start().starts_with('#') && l.contains("cooked"))
        {
            return true;
        }
        if a.join(".git").exists() {
            break;
        }
    }
    false
}

// --------------------------------------------------------------------------------------------- profile ----

fn profile(out: &mut Out) {
    let mut seen: HashMap<&str, usize> = HashMap::new();
    for (i, m) in out.mods.iter().enumerate() {
        let Some(id) = m.id.as_deref() else { continue };
        if let Some(&j) = seen.get(id) {
            let (a, b) = if out.mods[i].report { (i, j) } else { (j, i) };
            if out.mods[a].report {
                let fa = out.mods[a].manifest().map(|f| f.display.clone()).unwrap_or_else(|| out.mods[a].display.clone());
                let fb = out.mods[b].manifest().map(|f| f.display.clone()).unwrap_or_else(|| out.mods[b].display.clone());
                let span = out.mods[a].manifest().and_then(|f| f.root.as_ref()).and_then(|r| r.get("id")).map(|n| n.span).unwrap_or_default();
                let d = Diagnostic::new(&fa, codes::PROFILE_DUPLICATE_MOD, span, &format!("mod id \"{id}\" is also used by {}: Kiln loads only one of them (the first folder in name order)", out.mods[b].display)).with_related(&fb, Span::default(), "the other mod");
                out.diags.push(d);
            }
        } else {
            seen.insert(id, i);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids() {
        // docs/kiln/services/content.md: placeholder-items' Kiln_PH_Sword is 0x3f56132f; known pairs from kiln.log
        assert_eq!(guid_bytes("54b131f3-525f-451c-9976-a4bbcd8393c6").unwrap()[0], 0xf3);
        assert_eq!(fnv1a(b"kiln.Log"), 0xc0b137a7);
        assert_eq!(fnv1a(b"kiln.Signal"), 0x38b05baf);
        assert!(is_guid("54b131f3-525f-451c-9976-a4bbcd8393c6"));
        assert!(!is_guid("54b131f3525f451c9976a4bbcd8393c6"));
    }

    #[test]
    fn vfx_budget_at_each_limit() {
        use vfx_limits::*;
        let need = |vfx, meshes, mesh_bytes, sounds| VfxNeed { vfx, meshes, mesh_bytes, sounds };
        let room = MESH_BYTES - GAME_MESH_BYTES - MESH_MARGIN;
        assert!(vfx_over_budget(&need(473, 386, room, 10), GAME_VFX).is_empty(), "exactly full fits");
        assert!(vfx_over_budget(&need(474, 0, 0, 0), GAME_VFX)[0].contains("474 new VFX"));
        assert!(vfx_over_budget(&need(0, 387, 0, 0), GAME_VFX)[0].contains("387 new meshes"));
        assert!(vfx_over_budget(&need(0, 0, room + 1, 0), GAME_VFX)[0].contains("VFX mesh memory"));
        assert!(vfx_over_budget(&need(0, 0, 0, 11), GAME_VFX)[0].contains("11 sounds"));
        assert_eq!(vfx_over_budget(&need(474, 387, room + 1, 11), GAME_VFX).len(), 4);
        // the catalog's count decides when there is one: a game with more VFX leaves less room
        assert!(vfx_over_budget(&need(473, 0, 0, 0), GAME_VFX + 1)[0].contains("room for 472"));
        assert!(room > 240_000 && room < 245_000, "about 240 KB for all mods: {room}");
    }

    /// The same numbers as the builder, which refuses at boot what this says first.
    #[test]
    fn vfx_limits_match_the_builder() {
        use vfx_limits::*;
        let h = include_str!("../../../../native/services/vfx/cook/vfxcook.h");
        assert!(h.contains(&format!("uint32_t vfx = {GAME_VFX}, meshes = {GAME_MESHES}, curves = 8, sounds = {GAME_SOUNDS};")));
        assert!(h.contains(&format!("uint64_t mesh_bytes = {GAME_MESH_BYTES};")));
        assert!(h.contains(&format!("uint32_t vfx = {VFX}, meshes = {MESHES}, curves = 16, sounds = {SOUNDS};")));
        assert!(h.contains(&format!("uint64_t mesh_bytes = 0x{MESH_BYTES:x};")));
        assert!(h.contains(&format!("uint64_t mesh_margin = {MESH_MARGIN};")));
    }

    #[test]
    fn a_vfx_value_says_its_type() {
        let p = |t: &str| super::super::json::parse("x", t, 0, super::super::json::Dialect::STRICT).root.unwrap();
        assert_eq!(vfx_value_type(&p("3")), Some("float"), "a plain integer is still a float");
        assert_eq!(vfx_value_type(&p("{\"int\": 7}")), Some("int"));
        assert_eq!(vfx_value_type(&p("[1, 2, 3]")), Some("float3"));
        assert_eq!(vfx_value_type(&p("\"#ff0000\"")), Some("color"));
        assert_eq!(vfx_value_type(&p("\"#ff0000aa\"")), Some("color"));
        assert_eq!(vfx_value_type(&p("\"red\"")), None);
        assert_eq!(vfx_value_type(&p("[1, 2]")), None);
    }

    #[test]
    fn spdx() {
        assert!(spdx_problem("MIT").is_none());
        assert!(spdx_problem("MIT OR Apache-2.0").is_none());
        assert!(spdx_problem("(MIT AND Zlib) OR LicenseRef-Mine").is_none());
        assert!(spdx_problem("GPL-2.0-or-later WITH Classpath-exception").is_none());
        assert_eq!(spdx_problem("MIT License").unwrap().1.as_deref(), Some("MIT"));
        assert_eq!(spdx_problem("GPL-3.0").unwrap().1.as_deref(), Some("GPL-3.0-only"));
        assert!(spdx_problem("MIT OR").is_some());
        assert_eq!(spdx_problem("Apche-2.0").unwrap().1.as_deref(), Some("Apache-2.0"));
    }

    #[test]
    fn kschem() {
        let h = br#"{"size":[1,1,1]}"#;
        let mut b = b"KSCHEM\r\n".to_vec();
        b.extend(1u32.to_le_bytes());
        b.extend((h.len() as u32).to_le_bytes());
        b.extend(h);
        b.extend(2u32.to_le_bytes());
        b.extend([0, 0]);
        assert_eq!(kschem_header(&b), Ok((16, 16 + h.len())));
        b.truncate(b.len() - 1);
        assert!(kschem_header(&b).is_err());
        assert!(kschem_header(b"nope").is_err());
    }
}
