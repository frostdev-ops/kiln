//! The save editor's art (launcher/src/editor; upstream Enshrouded Save Editor, MIT): what it loads through
//! `kiln-asset://…/editor/` (src/ui.js, src/platform.js) and how this module makes each file from the game:
//!
//! | file | made from |
//! |---|---|
//! | `icons/<Item>_icon.webp` | every 2D `UiTextureResource` named `*_icon` (item icons are keyed by the item's GUID and named after its debug name), fitted into 96x96 like the editor's own set |
//! | `ui/<name>.webp` | every other 2D `UiTextureResource` up to 256 px, plus the larger ones the editor uses ([`EDITOR_LARGE_UI`]), at their own size (the editor names textures from its `data/ui.json`, `skills.json` and code) |
//! | `ui/tinted/<name>.webp` | 51 pre-tinted variants the editor's author made: a flat colour (the game's FbUiBundle colours, see [`TINTS`]) through a resized UI mask |
//! | `fonts/font<NN>.otf\|ttf` | `UiFontResource.fonts[NN]` (NN = index), the Latin/Cyrillic files up to 2 MiB (the editor registers 00, 04, 08, 24 Harmonique and 10, 26 Noto Serif) |
//!
//! The tint table was measured against the editor's `build-assets/game/ui/tinted` (51 files; every source matched with
//! alpha correlation 1.000, premultiplied error at most 3/255). The colours are the editor's `data/ui.json` values
//! (rarity, menu.tabHighlightColor, itemSlot.background, skilltree glow/tints), as rendered by its author.

use super::image::{self, Rgba};
use super::kfc::Kfc;
use super::res::{FontFile, UiTexture};
use super::texture;
use super::work::{RunReport, run_parallel, write_file};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};

/// Bump when the editor recipe changes: art made by an older recipe shows as outdated.
pub const RECIPE: u32 = 2;  // 2: writes .icon-index.json for the mod overlay
pub const ICON_SIZE: u32 = 96;
/// UI textures up to this size are all written (766 files, about 5 MB in client 26430625).
pub const UI_SMALL_SIDE: u32 = 256;

/// The editor's UI textures larger than [`UI_SMALL_SIDE`] (from its `build-assets/game/ui`, 27 of its 167). Check this
/// list when the editor is re-vendored (docs/kiln/launcher/gameart.md, "Editor updates").
pub const EDITOR_LARGE_UI: &[&str] = &[
    "attribute_tabIllustration_basics", "basic_widget_background", "basic_widget_header", "basic_widget_rarity_stripe",
    "fancy_box_background_pattern", "fancy_box_background_pattern_ancients", "fancy_box_background_shape",
    "fancy_box_background_shape_glassmask", "fancy_box_background_shape_halved",
    "fancy_box_background_shape_halved_glassmask", "fancy_box_frame", "fancy_box_frame_full",
    "fancy_box_frame_full_Simple", "fancy_box_frame_full_Simple_Bold", "fancy_box_frame_full_background",
    "fancy_box_frame_full_gradient_to_left", "fancy_box_frame_full_gradient_to_right",
    "fancy_box_frame_full_gradient_to_top", "fancy_box_frame_full_simple_background", "fancy_box_frame_minimal",
    "fancy_box_frame_minimal_background", "fancy_box_frame_simple", "levelup_ornament", "menu_toptab_background",
    "separator_line_horizontal", "skilltree_bg_layer_01", "skilltree_starter_ring",
];
const FONT_MAX_BYTES: u32 = 2 << 20;

/// One pre-tinted texture: (file under ui/tinted, source UiTexture name, output size, rgb, alpha factor).
pub struct Tint {
    pub out: &'static str,
    pub src: &'static str,
    pub w: u32,
    pub h: u32,
    pub rgb: [u8; 3],
    pub alpha: f32,
}

const fn t(out: &'static str, src: &'static str, w: u32, h: u32, rgb: [u8; 3], alpha: f32) -> Tint {
    Tint { out, src, w, h, rgb, alpha }
}

// rarity colours (ui.json rarity.undefined .. legendary) and the menu gold / light
const R0: [u8; 3] = [95, 95, 95];
const R1: [u8; 3] = [161, 174, 150];
const R2: [u8; 3] = [60, 155, 90];
const R3: [u8; 3] = [121, 161, 221];
const R4: [u8; 3] = [157, 132, 218];
const R5: [u8; 3] = [246, 160, 43];
const GOLD: [u8; 3] = [201, 173, 84];
const LIGHT: [u8; 3] = [237, 230, 214];

pub const TINTS: &[Tint] = &[
    t("gem_0", "item_rarity", 64, 64, R0, 1.0),
    t("gem_1", "item_rarity", 64, 64, R1, 1.0),
    t("gem_2", "item_rarity", 64, 64, R2, 1.0),
    t("gem_3", "item_rarity", 64, 64, R3, 1.0),
    t("gem_4", "item_rarity", 64, 64, R4, 1.0),
    t("gem_5", "item_rarity", 64, 64, R5, 1.0),
    t("stripe_0", "basic_widget_rarity_stripe", 512, 128, R0, 1.0),
    t("stripe_1", "basic_widget_rarity_stripe", 512, 128, R1, 1.0),
    t("stripe_2", "basic_widget_rarity_stripe", 512, 128, R2, 1.0),
    t("stripe_3", "basic_widget_rarity_stripe", 512, 128, R3, 1.0),
    t("stripe_4", "basic_widget_rarity_stripe", 512, 128, R4, 1.0),
    t("stripe_5", "basic_widget_rarity_stripe", 512, 128, R5, 1.0),
    t("slot_rarity_0", "itemslot_backpack_subtle", 128, 128, R0, 0.349),
    t("slot_rarity_1", "itemslot_backpack_subtle", 128, 128, R1, 0.349),
    t("slot_rarity_2", "itemslot_backpack_subtle", 128, 128, R2, 0.349),
    t("slot_rarity_3", "itemslot_backpack_subtle", 128, 128, R3, 0.349),
    t("slot_rarity_4", "itemslot_backpack_subtle", 128, 128, R4, 0.349),
    t("slot_rarity_5", "itemslot_backpack_subtle", 128, 128, R5, 0.349),
    t("slot_bg", "itemslot_backpack", 128, 128, [31, 28, 46], 0.275),
    t("slot_bg_hover", "itemslot_backpack", 128, 128, [255, 255, 255], 0.176),
    t("level_badge_gold", "character_level_badge_background", 100, 100, GOLD, 0.949),
    t("panel_bg", "fancy_box_frame_full_background", 512, 512, [37, 40, 92], 0.918),
    t("tab_mark", "tab_highlight", 201, 90, GOLD, 1.0),
    t("st_bg_0", "skilltree_bg_layer_01", 1024, 1024, [27, 24, 73], 1.0),
    t("st_bg_1", "skilltree_bg_layer_01", 1024, 1024, [118, 37, 103], 0.781),
    t("st_glow_mage", "skillnode_activeskill_glow", 128, 128, [84, 176, 254], 1.0),
    t("st_glow_neutral", "skillnode_activeskill_glow", 128, 128, [255, 177, 1], 1.0),
    t("st_glow_ranger", "skillnode_activeskill_glow", 128, 128, [154, 255, 67], 1.0),
    t("st_glow_warrior", "skillnode_activeskill_glow", 128, 128, [254, 42, 43], 1.0),
    t("recipe_category_ammo_gold", "recipe_category_ammo", 64, 64, GOLD, 1.0),
    t("recipe_category_ammo_light", "recipe_category_ammo", 64, 64, LIGHT, 0.749),
    t("recipe_category_armor_gold", "recipe_category_armor", 64, 64, GOLD, 1.0),
    t("recipe_category_armor_light", "recipe_category_armor", 64, 64, LIGHT, 0.749),
    t("recipe_category_blocks_gold", "recipe_category_blocks", 64, 64, GOLD, 1.0),
    t("recipe_category_blocks_light", "recipe_category_blocks", 64, 64, LIGHT, 0.749),
    t("recipe_category_essential_gold", "recipe_category_essential", 64, 64, GOLD, 1.0),
    t("recipe_category_essential_light", "recipe_category_essential", 64, 64, LIGHT, 0.749),
    t("recipe_category_magical_gold", "recipe_category_magical", 64, 64, GOLD, 1.0),
    t("recipe_category_magical_light", "recipe_category_magical", 64, 64, LIGHT, 0.749),
    t("recipe_category_resources_gold", "recipe_category_resources", 64, 64, GOLD, 1.0),
    t("recipe_category_resources_light", "recipe_category_resources", 64, 64, LIGHT, 0.749),
    t("recipe_category_supplies_gold", "recipe_category_supplies", 64, 64, GOLD, 1.0),
    t("recipe_category_supplies_light", "recipe_category_supplies", 64, 64, LIGHT, 0.749),
    t("recipe_category_survival_gold", "recipe_category_survival", 64, 64, GOLD, 1.0),
    t("recipe_category_survival_light", "recipe_category_survival", 64, 64, LIGHT, 0.749),
    t("recipe_category_upgrade_gold", "recipe_category_upgrade", 64, 64, GOLD, 1.0),
    t("recipe_category_upgrade_light", "recipe_category_upgrade", 64, 64, LIGHT, 0.749),
    t("recipe_category_vanity_gold", "recipe_category_vanity", 64, 64, GOLD, 1.0),
    t("recipe_category_vanity_light", "recipe_category_vanity", 64, 64, LIGHT, 0.749),
    t("recipe_category_weapon_gold", "recipe_category_weapon", 64, 64, GOLD, 1.0),
    t("recipe_category_weapon_light", "recipe_category_weapon", 64, 64, LIGHT, 0.749),
];

/// Extraction settings (defaults match the editor's own set).
#[derive(Debug, Clone)]
pub struct EditorOptions {
    pub icon_size: u32,
    /// Every UI texture up to this size is written...
    pub ui_small_side: u32,
    /// ...plus these, whatever their size.
    pub ui_extra: Vec<String>,
    /// Worker threads (None: the machine's parallelism, at most 8).
    pub threads: Option<usize>,
}

impl Default for EditorOptions {
    fn default() -> Self {
        EditorOptions {
            icon_size: ICON_SIZE,
            ui_small_side: UI_SMALL_SIDE,
            ui_extra: EDITOR_LARGE_UI.iter().map(|s| s.to_string()).collect(),
            threads: None,
        }
    }
}

/// A file name the art may use: the game's debug names are plain identifiers; anything else is skipped.
pub fn safe_name(n: &str) -> bool {
    !n.is_empty()
        && n.len() <= 200
        && !n.starts_with('.')
        && n.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'.' | b' '))
}

/// One output file.
pub enum Job {
    /// Fit into `size`x`size` (icons).
    Fit { tex: UiTexture, size: u32, out: PathBuf },
    /// As is (UI textures).
    Copy { tex: UiTexture, out: PathBuf },
    Tint { tex: UiTexture, tint: &'static Tint, out: PathBuf },
    Font { font: FontFile, out: PathBuf },
}

impl Job {
    fn group(&self) -> &'static str {
        match self {
            Job::Fit { .. } => "icons",
            Job::Copy { .. } => "ui",
            Job::Tint { .. } => "tinted",
            Job::Font { .. } => "fonts",
        }
    }
    fn label(&self) -> String {
        match self {
            Job::Fit { tex, .. } | Job::Copy { tex, .. } => tex.name.clone(),
            Job::Tint { tint, .. } => tint.out.to_string(),
            Job::Font { font, .. } => format!("font{:02}", font.index),
        }
    }
    /// For read ordering: (container, offset).
    fn sort_key(&self, kfc: &Kfc) -> (u16, u32) {
        let h = match self {
            Job::Fit { tex, .. } | Job::Copy { tex, .. } | Job::Tint { tex, .. } => &tex.content,
            Job::Font { font, .. } => &font.content,
        };
        kfc.content_location(h).map_or((u16::MAX, 0), |l| (l.container, l.offset))
    }
}

/// What the selection found: the jobs plus counts of what was left out (by reason).
pub struct Plan {
    pub jobs: Vec<Job>,
    pub skipped: BTreeMap<String, u64>,
    pub missing: Vec<String>,
}

/// Chooses the files to write into `staging` (the future `<data>/editor-assets/`).
pub fn plan(textures: &[UiTexture], fonts: &[FontFile], kfc: &Kfc, staging: &Path, o: &EditorOptions) -> Plan {
    let mut jobs = Vec::new();
    let mut skipped: BTreeMap<String, u64> = BTreeMap::new();
    let mut skip = |why: &str| *skipped.entry(why.into()).or_default() += 1;
    let mut seen = HashSet::new();
    let mut by_name: BTreeMap<&str, &UiTexture> = BTreeMap::new();
    let extra: HashSet<&str> = o.ui_extra.iter().map(String::as_str).collect();
    for tex in textures {
        if tex.key.part != 0 {
            skip("multi-part");
            continue;
        }
        if !tex.texture_2d {
            skip("not 2D");
            continue;
        }
        if !safe_name(&tex.name) {
            skip("unusable name");
            continue;
        }
        if tex.format.is_none() {
            skip(&format!("format {}", tex.format_name));
            continue;
        }
        if !seen.insert(tex.name.clone()) {
            skip("duplicate name");
            continue;
        }
        by_name.insert(&tex.name, tex);
        if tex.name.ends_with("_icon") {
            jobs.push(Job::Fit { tex: tex.clone(), size: o.icon_size, out: staging.join("icons").join(format!("{}.webp", tex.name)) });
        } else if tex.w.max(tex.h) <= o.ui_small_side || extra.contains(tex.name.as_str()) {
            jobs.push(Job::Copy { tex: tex.clone(), out: staging.join("ui").join(format!("{}.webp", tex.name)) });
        } else {
            skip("large UI image the editor does not use");
        }
    }
    let mut missing = Vec::new();
    for tint in TINTS {
        match by_name.get(tint.src) {
            Some(tex) => jobs.push(Job::Tint { tex: (*tex).clone(), tint, out: staging.join("ui/tinted").join(format!("{}.webp", tint.out)) }),
            None => missing.push(format!("ui/tinted/{}.webp (no texture {})", tint.out, tint.src)),
        }
    }
    for f in fonts {
        if f.content.size > FONT_MAX_BYTES {
            skip("font over 2 MiB (CJK)");
            continue;
        }
        jobs.push(Job::Font { font: f.clone(), out: staging.join("fonts").join(format!("font{:02}", f.index)) });
    }
    jobs.sort_by_key(|j| j.sort_key(kfc));
    Plan { jobs, skipped, missing }
}

/// The mip level to read so a downscale to `target` starts from at least twice its size (or level 0).
fn level_for(tex: &UiTexture, target: (u32, u32)) -> u32 {
    let mut l = 0;
    while l + 1 < tex.levels && (tex.w >> (l + 1)) >= target.0 * 2 && (tex.h >> (l + 1)) >= target.1 * 2 {
        l += 1;
    }
    l
}

pub(crate) fn load(kfc: &Kfc, tex: &UiTexture, level: u32) -> Result<Rgba> {
    let fmt = tex.format.ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("format {}", tex.format_name)))?;
    let (off, w, h) = fmt.level_offset(tex.w, tex.h, level);
    let data = kfc.read_content_prefix(&tex.content, Some(off + fmt.level_size(w, h)))?;
    texture::decode(fmt, w, h, data.get(off..).unwrap_or(&[]))
}

/// Runs one job; returns the bytes written.
fn run(kfc: &Kfc, job: &Job) -> Result<u64> {
    let (out, bytes): (PathBuf, Vec<u8>) = match job {
        Job::Fit { tex, size, out } => {
            let (w, h) = image::fit_within(tex.w, tex.h, *size, *size);
            let img = load(kfc, tex, level_for(tex, (w, h)))?;
            (out.clone(), image::encode_webp(&image::resize(&img, w, h))?)
        }
        Job::Copy { tex, out } => (out.clone(), image::encode_webp(&load(kfc, tex, 0)?)?),
        Job::Tint { tex, tint, out } => {
            let img = load(kfc, tex, level_for(tex, (tint.w, tint.h)))?;
            (out.clone(), image::encode_webp(&image::tint(&image::resize(&img, tint.w, tint.h), tint.rgb, tint.alpha))?)
        }
        Job::Font { font, out } => {
            let data = kfc.read_content(&font.content)?;
            let ext = match data.get(..4) {
                Some(b"OTTO") => "otf",
                Some([0, 1, 0, 0]) | Some(b"true") => "ttf",
                _ => return Err(Error::new(ErrorCode::Unsupported, "not an OpenType/TrueType font")),
            };
            (out.with_extension(ext), data)
        }
    };
    write_file(&out, &bytes)
}

/// Runs the plan's jobs (work.rs: threads, progress `base..base+span`, cancel).
pub fn run_jobs(kfc: &Kfc, jobs: &[Job], threads: Option<usize>, p: &mut dyn Progress, base: f32, span: f32) -> Result<RunReport> {
    run_parallel(jobs, threads, p, base, span, "Extracting game art", |j| run(kfc, j).map(|b| (j.group(), b)), |j| j.label())
}
