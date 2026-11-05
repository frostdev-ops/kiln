//! The admin map's art (launcher/src/admin/map/gamemap.ts, engine.ts)
//! from the client's own data. The world map is one `UiTextureResource` GUID with 207 parts (client 26430625): a 1280²
//! base height and 10x10 tiles of detail height and Shroud zones, plus parameter textures the launcher has no use for.
//! Written under `<data>/admin-assets/map/`:
//!
//! | file | made from | check against the local copy |
//! |---|---|---|
//! | `gpu/base.bin` | the base height texture (R16, 1280²), gzip of the u16 LE samples | equal but 105 of 1,638,400 samples (the game update) |
//! | `gpu/d/x_y.webp` | `DetailHeight(x,y)` (BC4 1024²), grey | identical |
//! | `gpu/f/x_y.webp` | `FogZone(x,y)` (BC5 1024²), R/G halved to 512² (2x2 box) | mean difference 0.02/255 |
//! | `tiles/{z}/{x}_{y}.webp` (z 0..3, 138 tiles) | rendered here from the heights with Kiln's own ramp and hillshade, the low-detail fallback under the WebGL relief; transparent outside the world | Kiln's own |
//! | `i/g-<name>.webp` | every `mapmarker_*` texture and `player_position`; the world-object category icons ([`markers::Markers::icons`], fitted into 64 px; `icon_chest-*` tints of `icon_chest`) | |
//! | `fonts/harmonique.otf`, `fonts/notoserif-game.ttf` | `UiFontResource.fonts[0]` (Harmonique Display Regular), `[10]` (Noto Serif Regular), whole files | |
//! | `game/markers.json`, `game/lang/<code>.json` | [`markers`]: map markers, world objects, place names, every game language | a local `data/markers.json` (the map's fallback) |
//!
//! Not from the game (they stay on a local copy, when there is one): the finder data `data/{find,chests,ore}.json`, `pts.bin`
//! (creature spawns, gatherables, ore volumes, chest loot), the item-named marker icons `i/i-*`, `j-*`, `p-*` (named
//! after English item names), and the catalogs `data/{items,creature-templates}.json`.

pub mod markers;

use super::image::{self, Rgba};
use super::kfc::Kfc;
use super::res::UiTexture;
use super::texture::{self, Format};
use super::work::{RunReport, run_parallel, write_file};
use super::{GameData, SourceInfo, clean_leftovers, refuse_inside_game, res, swap_in, write_source};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// Bump when the map recipe changes (2: the markers in `game/`; 3: Kiln's own tile colours, no game gradient or noise textures).
pub const MAP_RECIPE: u32 = 3;
/// The folders under `<dest>/map/` this module replaces as a whole (`i/` only gets its `g-*` files written).
pub const MAP_DIRS: [&str; 4] = ["gpu", "tiles", "fonts", "game"];
/// World-object icons are fitted into this size (the map draws icons at 12-40 px).
const OBJECT_ICON: u32 = 64;
const N: usize = 10;
const DETAIL: usize = 1024;
const FOG: u32 = 512;
const BASE: usize = 1280;
const TILE: usize = 256;
/// The zoom-3 mosaic: 10 detail tiles of 256 px.
const MOSAIC: usize = N * TILE;
/// Metres per pixel of the zoom-3 mosaic.
const CELL: usize = DETAIL / TILE;
/// The height formats: metres between coarse nodes, the coarse texture's full range, the fine offsets' zero (a metre a step).
const NODE_METRES: f32 = 8.0;
const NODE_RANGE: f32 = 4096.0;
const FINE_MIDDLE: f32 = 127.5;
/// The fallback tiles use the same ramp and light as the launcher's relief (launcher/src/admin/map/reliefShader.ts),
/// without its contours and Shroud: (metres, sRGB colour).
const RAMP: [(f32, [f32; 3]); 9] = [
    (530.0, [0.098, 0.094, 0.096]),
    (610.0, [0.150, 0.134, 0.118]),
    (720.0, [0.238, 0.203, 0.154]),
    (860.0, [0.340, 0.272, 0.192]),
    (1040.0, [0.425, 0.315, 0.208]),
    (1300.0, [0.505, 0.362, 0.245]),
    (1650.0, [0.625, 0.508, 0.398]),
    (2050.0, [0.792, 0.742, 0.668]),
    (2420.0, [0.930, 0.908, 0.858]),
];
const SUN_HEIGHT: f32 = 0.6428;
const LIGHT_WRAP: f32 = 0.42;
const SHADOW: [f32; 3] = [0.30, 0.26, 0.32];
const HIGHLIGHT: [f32; 3] = [1.36, 1.28, 1.14];
/// Steepens the slopes of the 4 m lattice for the hillshade.
const RELIEF_GAIN: f32 = 1.5;

#[derive(Debug, Clone, Default)]
pub struct MapOptions {
    pub threads: Option<usize>,
}

enum Job {
    Detail { x: usize, y: usize, tex: UiTexture },
    Fog { x: usize, y: usize, tex: UiTexture },
    Icon { tex: UiTexture, out: PathBuf },
    /// A world-object category icon: fitted into [`OBJECT_ICON`], optionally tinted.
    ObjectIcon { tex: UiTexture, out: PathBuf, tint: Option<[u8; 3]> },
    Font { index: usize, content: super::kfc::ContentHash, out: PathBuf },
}

impl Job {
    fn label(&self) -> String {
        match self {
            Job::Detail { x, y, .. } => format!("DetailHeight({x},{y})"),
            Job::Fog { x, y, .. } => format!("FogZone({x},{y})"),
            Job::Icon { tex, .. } | Job::ObjectIcon { tex, .. } => tex.name.clone(),
            Job::Font { index, .. } => format!("font{index:02}"),
        }
    }
}

/// One detail tile as zoom-3 heights in metres, row by row: the mean of each 4 m block of the fine offsets on the coarse
/// height at the block's centre. `col0`, `row0`: the tile's first sample on the world's metre lattice.
fn tile_heights(base: &[u16], col0: usize, row0: usize, detail: &[u8]) -> Vec<f32> {
    let mut out = Vec::with_capacity(TILE * TILE);
    let centre = (CELL as f32 - 1.0) / 2.0;
    for j in 0..TILE {
        for i in 0..TILE {
            let mut sum = 0u32;
            for dy in 0..CELL {
                let row = &detail[(j * CELL + dy) * DETAIL + i * CELL..][..CELL];
                sum += row.iter().map(|&v| v as u32).sum::<u32>();
            }
            let offset = sum as f32 / (CELL * CELL) as f32 - FINE_MIDDLE;
            out.push(coarse_at(base, (col0 + i * CELL) as f32 + centre, (row0 + j * CELL) as f32 + centre) + offset);
        }
    }
    out
}

/// The coarse height in metres at a position on the metre lattice (column, row from the north-west). It is bilinear
/// between nodes, and node (i, k) sits on lattice sample (8i, 8k - 1): along those lines the fine offsets cancel the
/// kinks of the bilinear surface.
fn coarse_at(base: &[u16], col: f32, row: f32) -> f32 {
    let (x, y) = (col / NODE_METRES, (row + 1.0) / NODE_METRES);
    let (ix, iy) = (x.floor(), y.floor());
    let (fx, fy) = (x - ix, y - iy);
    let node = |x: f32, y: f32| {
        let (x, y) = ((x as i64).clamp(0, BASE as i64 - 1) as usize, (y as i64).clamp(0, BASE as i64 - 1) as usize);
        base[y * BASE + x] as f32 / 65535.0 * NODE_RANGE
    };
    (node(ix, iy) * (1.0 - fx) + node(ix + 1.0, iy) * fx) * (1.0 - fy) + (node(ix, iy + 1.0) * (1.0 - fx) + node(ix + 1.0, iy + 1.0) * fx) * fy
}

fn land_colour(metres: f32) -> [f32; 3] {
    for w in RAMP.windows(2) {
        let ((from, a), (to, b)) = (w[0], w[1]);
        if metres < to {
            let t = ((metres - from) / (to - from)).clamp(0.0, 1.0);
            return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
        }
    }
    RAMP[RAMP.len() - 1].1
}

/// Light on a slope (metres per metre, x east, y south), 1 on flat ground: a sun in the north-west and a weaker fill on
/// either side of it, wrapped past the point where a slope turns away from the sun.
fn sunlight(slope: [f32; 2]) -> f32 {
    let (x, y) = (-slope[0], -slope[1]);
    let len = (x * x + y * y + 1.0).sqrt();
    let facing = |l: [f32; 3]| (x * l[0] + y * l[1] + l[2]) / len;
    let raw = 0.62 * facing([-0.5417, -0.5417, SUN_HEIGHT]) + 0.19 * facing([-0.7077, 0.2932, SUN_HEIGHT]) + 0.19 * facing([0.2932, -0.7077, SUN_HEIGHT]);
    (raw + LIGHT_WRAP).max(0.0) / (SUN_HEIGHT + LIGHT_WRAP)
}

fn shade_land(colour: [f32; 3], slope: [f32; 2]) -> [f32; 3] {
    let light = sunlight([slope[0] * RELIEF_GAIN, slope[1] * RELIEF_GAIN]);
    let mut out = colour;
    for k in 0..3 {
        let tint = if light < 1.0 { SHADOW[k] + (1.0 - SHADOW[k]) * light } else { 1.0 + (HIGHLIGHT[k] - 1.0) * ((light - 1.0) * 2.0).min(1.0) };
        out[k] = colour[k] * tint;
    }
    out
}

/// Colours a square of zoom-3 heights (RGB). Slopes come from the whole square, so the tiles join without a seam.
fn shade_tiles(heights: &[f32], size: usize) -> Vec<u8> {
    let span = 2.0 * CELL as f32;
    let mut rgb = Vec::with_capacity(size * size * 3);
    for y in 0..size {
        let (north, south) = (y.saturating_sub(1), (y + 1).min(size - 1));
        for x in 0..size {
            let (west, east) = (x.saturating_sub(1), (x + 1).min(size - 1));
            let slope = [(heights[y * size + east] - heights[y * size + west]) / span, (heights[south * size + x] - heights[north * size + x]) / span];
            let c = shade_land(land_colour(heights[y * size + x]), slope);
            rgb.extend(c.map(|v| (v.clamp(0.0, 1.0) * 255.0 + 0.5) as u8));
        }
    }
    rgb
}

/// `size`x`size` box average of an RGB image (`n` x `n`, n divisible by size).
fn downsample(src: &[u8], n: usize, size: usize) -> Vec<u8> {
    let f = n / size;
    let mut out = vec![0u8; size * size * 3];
    for y in 0..size {
        for x in 0..size {
            let mut acc = [0u32; 3];
            for dy in 0..f {
                let row = (y * f + dy) * n;
                for dx in 0..f {
                    let s = (row + x * f + dx) * 3;
                    for k in 0..3 {
                        acc[k] += src[s + k] as u32;
                    }
                }
            }
            for k in 0..3 {
                out[(y * size + x) * 3 + k] = ((acc[k] + (f * f / 2) as u32) / (f * f) as u32) as u8;
            }
        }
    }
    out
}

/// Cuts an RGB image into tiles of 256 px; the part of a tile outside the image (the world) stays transparent.
fn cut_tiles(img: &[u8], size: usize, z: u32, dir: &Path) -> Result<(u64, u64)> {
    let count = size.div_ceil(TILE);
    let (mut files, mut bytes) = (0, 0);
    for ty in 0..count {
        for tx in 0..count {
            let mut t = Rgba::new(TILE as u32, TILE as u32);
            for j in 0..TILE {
                for i in 0..TILE {
                    let (px, py) = (tx * TILE + i, ty * TILE + j);
                    if px < size && py < size {
                        let c = &img[(py * size + px) * 3..][..3];
                        t.px[(j * TILE + i) * 4..][..4].copy_from_slice(&[c[0], c[1], c[2], 255]);
                    }
                }
            }
            bytes += write_file(&dir.join(format!("{z}/{tx}_{ty}.webp")), &image::encode_webp(&t)?)?;
            files += 1;
        }
    }
    Ok((files, bytes))
}

fn gzip(data: &[u8]) -> Result<Vec<u8>> {
    use std::io::Write;
    let mut e = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    e.write_all(data)?;
    Ok(e.finish()?)
}

fn grid_name(n: &str, prefix: &str) -> Option<(usize, usize)> {
    let inner = n.strip_prefix(prefix)?.strip_prefix('(')?.strip_suffix(')')?;
    let (a, b) = inner.split_once(',')?;
    let (x, y) = (a.trim().parse().ok()?, b.trim().parse().ok()?);
    (x < N && y < N).then_some((x, y))
}

/// Extracts the admin map's art from the client at `game_dir` into `dest` (`<data>/admin-assets/`): `map/gpu/`,
/// `map/tiles/`, `map/fonts/` (replaced), `map/i/g-*.webp` (written), `.source.json`. Read-only on the game; staging +
/// swap like [`super::extract_editor`].
pub fn extract_map(game_dir: &Path, dest: &Path, opts: &MapOptions, steam_build: Option<String>, p: &mut dyn Progress) -> Result<SourceInfo> {
    let t0 = std::time::Instant::now();
    refuse_inside_game(dest, game_dir)?;
    p.report(Some(0.0), "Reading the game's map data");
    let g = GameData::open(game_dir)?;
    let textures = res::ui_textures(&g.kfc, &g.layouts)?;
    let fonts = res::fonts(&g.kfc, &g.layouts)?;
    let mut by_name: HashMap<&str, &UiTexture> = HashMap::new();
    for t in &textures {
        by_name.entry(t.name.as_str()).or_insert(t);
    }
    let unsupported = |what: String| Error::new(ErrorCode::Unsupported, "This game build's map data is not in the expected form.").with_detail(what);
    // the game's own name of the base height texture
    const BASE_TEXTURE: &str = "_map_shader_parameters_baseHeight";
    let base_tex = by_name.get(BASE_TEXTURE).copied().ok_or_else(|| unsupported(format!("no {BASE_TEXTURE}")))?;
    if (base_tex.w, base_tex.h, base_tex.format) != (BASE as u32, BASE as u32, Some(Format::R16)) {
        return Err(unsupported(format!("base height {}x{} {}", base_tex.w, base_tex.h, base_tex.format_name)));
    }
    let base_raw = g.kfc.read_content_prefix(&base_tex.content, Some(BASE * BASE * 2))?;
    let base: Vec<u16> = base_raw.chunks_exact(2).map(|b| u16::from_le_bytes([b[0], b[1]])).collect();
    if p.canceled() {
        return Err(Error::new(ErrorCode::Canceled, "Canceled."));
    }
    std::fs::create_dir_all(dest).map_err(crate::util::io_at(dest, "creating"))?;
    clean_leftovers(dest);
    let staging = dest.join(format!(".extract-{}-{:x}", std::process::id(), crate::util::now_unix()));
    let map = staging.join("map");
    let result = (|| {
        // the jobs
        let mut jobs = Vec::new();
        let mut missing = Vec::new();
        for t in &textures {
            if let Some((x, y)) = grid_name(&t.name, "DetailHeight") {
                if t.format == Some(Format::Bc4) && (t.w, t.h) == (DETAIL as u32, DETAIL as u32) {
                    jobs.push(Job::Detail { x, y, tex: t.clone() });
                }
            } else if let Some((x, y)) = grid_name(&t.name, "FogZone") {
                if t.format.is_some() {
                    jobs.push(Job::Fog { x, y, tex: t.clone() });
                }
            } else if (t.name.starts_with("mapmarker_") || t.name == "player_position") && super::editor::safe_name(&t.name) && t.format.is_some() {
                jobs.push(Job::Icon { tex: t.clone(), out: map.join(format!("i/g-{}.webp", t.name)) });
            }
        }
        // the markers (fail soft: without them the map keeps a local copy, if any)
        p.report(Some(0.02), "Reading the map markers");
        let mut marker_counts: Vec<(&str, u64)> = Vec::new();
        match map_markers(&g, game_dir, &textures, &by_name, &map) {
            Ok((m, icon_jobs)) => {
                marker_counts = vec![("markers", m.report.points), ("markerCategories", m.report.categories), ("placeNames", m.report.labels), ("markerLanguages", m.report.languages)];
                jobs.extend(icon_jobs);
            }
            Err(e) => missing.push(format!("map/game/markers.json: {}{}", e.message, e.detail.map(|d| format!(" ({d})")).unwrap_or_default())),
        }
        let details = jobs.iter().filter(|j| matches!(j, Job::Detail { .. })).count();
        if details != N * N {
            return Err(unsupported(format!("{details} detail height tiles, expected {}", N * N)));
        }
        for (index, name) in [(0usize, "harmonique.otf"), (10, "notoserif-game.ttf")] {
            match fonts.get(index) {
                Some(f) => jobs.push(Job::Font { index, content: f.content, out: map.join("fonts").join(name) }),
                None => missing.push(format!("map/fonts/{name}")),
            }
        }
        let heights = Mutex::new(vec![0f32; MOSAIC * MOSAIC]);
        let kfc: &Kfc = &g.kfc;
        let run = |j: &Job| -> Result<(&'static str, u64)> {
            match j {
                Job::Detail { x, y, tex } => {
                    let img = texture::decode(Format::Bc4, tex.w, tex.h, &kfc.read_content_prefix(&tex.content, Some(Format::Bc4.level_size(tex.w, tex.h)))?)?;
                    let grey: Vec<u8> = img.px.chunks_exact(4).map(|c| c[0]).collect();
                    let b = write_file(&map.join(format!("gpu/d/{x}_{y}.webp")), &encode_grey(&grey, tex.w, tex.h)?)?;
                    let (tx, ty) = (*x, N - 1 - *y);
                    let tile = tile_heights(&base, tx * DETAIL, ty * DETAIL, &grey);
                    let mut m = heights.lock().unwrap();
                    for j in 0..TILE {
                        let dst = (ty * TILE + j) * MOSAIC + tx * TILE;
                        m[dst..dst + TILE].copy_from_slice(&tile[j * TILE..(j + 1) * TILE]);
                    }
                    Ok(("detail", b))
                }
                Job::Fog { x, y, tex } => {
                    let img = super::editor::load(kfc, tex, 0)?;
                    let small = box_half_to(&img, FOG);
                    let mut px = small.px;
                    for c in px.chunks_exact_mut(4) {
                        c[2] = 0;
                        c[3] = 255;
                    }
                    Ok(("fog", write_file(&map.join(format!("gpu/f/{x}_{y}.webp")), &image::encode_webp(&Rgba { w: FOG, h: FOG, px })?)?))
                }
                Job::Icon { tex, out } => Ok(("icons", write_file(out, &image::encode_webp(&super::editor::load(kfc, tex, 0)?)?)?)),
                Job::ObjectIcon { tex, out, tint } => {
                    let img = super::editor::load(kfc, tex, 0)?;
                    let (w, h) = image::fit_within(img.w, img.h, OBJECT_ICON, OBJECT_ICON);
                    let img = if (w, h) == (img.w, img.h) { img } else { image::resize(&img, w, h) };
                    let img = match tint {
                        Some(rgb) => image::tint(&img, *rgb, 1.0),
                        None => img,
                    };
                    Ok(("icons", write_file(out, &image::encode_webp(&img)?)?))
                }
                Job::Font { content, out, .. } => Ok(("fonts", write_file(out, &kfc.read_content(content)?)?)),
            }
        };
        let mut rep: RunReport = run_parallel(&jobs, opts.threads, p, 0.03, 0.85, "Extracting map art", run, |j| j.label())?;
        if rep.written.get("detail").copied().unwrap_or(0) != (N * N) as u64 {
            return Err(Error::new(ErrorCode::Internal, "Some map tiles could not be made.").with_detail(rep.failed.iter().take(10).cloned().collect::<Vec<_>>()));
        }
        // base height, tiles
        p.report(Some(0.88), "Rendering the map's tiles");
        rep.bytes += write_file(&map.join("gpu/base.bin"), &gzip(&base_raw)?)?;
        let m = shade_tiles(&heights.into_inner().unwrap(), MOSAIC);
        let mut tiles = 0u64;
        for (z, size) in [(3u32, MOSAIC), (2, MOSAIC / 2), (1, MOSAIC / 4), (0, MOSAIC / 8)] {
            if p.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "Canceled."));
            }
            let (f, b) = cut_tiles(&downsample(&m, MOSAIC, size), size, z, &map.join("tiles"))?;
            tiles += f;
            rep.bytes += b;
        }
        p.report(Some(0.98), "Putting the map art in place");
        let dest_map = dest.join("map");
        std::fs::create_dir_all(&dest_map).map_err(crate::util::io_at(&dest_map, "creating"))?;
        swap_in(&map, &dest_map, &MAP_DIRS)?;
        // marker icons go next to the copied ones
        let icons_from = map.join("i");
        let icons_to = dest_map.join("i");
        std::fs::create_dir_all(&icons_to).map_err(crate::util::io_at(&icons_to, "creating"))?;
        if let Ok(rd) = std::fs::read_dir(&icons_from) {
            for e in rd.flatten() {
                crate::util::rename_replace(&e.path(), &icons_to.join(e.file_name()))?;
            }
        }
        let mut counts = std::collections::BTreeMap::new();
        counts.insert("heightTiles".into(), rep.written.get("detail").copied().unwrap_or(0));
        counts.insert("fogTiles".into(), rep.written.get("fog").copied().unwrap_or(0));
        counts.insert("tiles".into(), tiles);
        counts.insert("icons".into(), rep.written.get("icons").copied().unwrap_or(0));
        counts.insert("fonts".into(), rep.written.get("fonts").copied().unwrap_or(0));
        for (k, v) in marker_counts {
            counts.insert(k.into(), v);
        }
        let mut problems = missing;
        problems.extend(rep.failed.iter().cloned());
        problems.truncate(50);
        let info = SourceInfo {
            format: 1,
            kind: "admin".into(),
            source: "game".into(),
            recipe: Some(MAP_RECIPE),
            game: Some(g.source(steam_build)),
            from: None,
            counts,
            skipped: Default::default(),
            bytes: rep.bytes,
            extracted: crate::util::now_rfc3339(),
            seconds: Some((t0.elapsed().as_secs_f64() * 10.0).round() / 10.0),
            tool: Some(format!("kiln-manage {} gameart", crate::VERSION)),
            problems,
        };
        write_source(dest, &info)?;
        Ok(info)
    })();
    let _ = std::fs::remove_dir_all(&staging);
    result
}

/// The markers into `map/game/` (staging) and the icon jobs their categories need beyond the `mapmarker_*` set. Reads
/// the exe's reflection a second time ([`GameData`] keeps only the texture layouts).
fn map_markers(
    g: &GameData,
    game_dir: &Path,
    textures: &[UiTexture],
    by_name: &HashMap<&str, &UiTexture>,
    map: &Path,
) -> Result<(markers::Markers, Vec<Job>)> {
    let types = super::refl::TypeDb::from_exe(&game_dir.join("enshrouded.exe"))
        .map_err(|e| Error::new(ErrorCode::Unsupported, "The game's exe could not be read for the marker layouts.").with_detail(e.message))?;
    let m = markers::extract(&g.kfc, &types, textures)?;
    let dir = map.join("game");
    let json = |v: &serde_json::Value| serde_json::to_vec(v).map_err(|e| Error::new(ErrorCode::Internal, format!("json: {e}")));
    write_file(&dir.join("markers.json"), &json(&m.markers)?)?;
    for (code, v) in &m.langs {
        write_file(&dir.join("lang").join(format!("{code}.json")), &json(v)?)?;
    }
    let mut jobs = Vec::new();
    for name in &m.icons {
        if name.starts_with("mapmarker_") || name == "player_position" {
            continue; // the marker icon jobs write these
        }
        let tint = markers::CHEST_TINTS.iter().find(|t| t.0 == name).map(|t| t.1);
        let src = if tint.is_some() { "icon_chest" } else { name.as_str() };
        if let Some(t) = by_name.get(src).filter(|t| t.format.is_some()) {
            jobs.push(Job::ObjectIcon { tex: (*t).clone(), out: map.join(format!("i/g-{name}.webp")), tint });
        }
    }
    Ok((m, jobs))
}

/// A grey image as lossless WebP (`L8`).
fn encode_grey(grey: &[u8], w: u32, h: u32) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    image_webp::WebPEncoder::new(&mut out)
        .encode(grey, w, h, image_webp::ColorType::L8)
        .map_err(|e| Error::new(ErrorCode::Internal, format!("webp: {e}")))?;
    Ok(out)
}

/// Box average down to `size`² (the source is a multiple of it).
fn box_half_to(img: &Rgba, size: u32) -> Rgba {
    if img.w == size && img.h == size {
        return img.clone();
    }
    if img.w < size || !img.w.is_multiple_of(size) || img.w != img.h {
        return image::resize(img, size, size);
    }
    let (f, n) = ((img.w / size) as usize, size as usize);
    let mut out = Rgba::new(size, size);
    for y in 0..n {
        for x in 0..n {
            let mut acc = [0u32; 4];
            for dy in 0..f {
                for dx in 0..f {
                    let s = (((y * f + dy) * img.w as usize) + x * f + dx) * 4;
                    for k in 0..4 {
                        acc[k] += img.px[s + k] as u32;
                    }
                }
            }
            for k in 0..4 {
                out.px[(y * n + x) * 4 + k] = ((acc[k] + (f * f / 2) as u32) / (f * f) as u32) as u8;
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn grid_names() {
        assert_eq!(grid_name("DetailHeight(3,7)", "DetailHeight"), Some((3, 7)));
        assert_eq!(grid_name("FogZone( 9, 0 )", "FogZone"), Some((9, 0)));
        assert_eq!(grid_name("FogZone(10,0)", "FogZone"), None);
        assert_eq!(grid_name("DetailHeight(1,2)", "FogZone"), None);
        assert_eq!(grid_name("DetailHeight1,2", "DetailHeight"), None);
    }

    #[test]
    fn the_ramp_climbs_and_stays_in_range() {
        let mut last = -1.0;
        for m in [0.0, 530.0, 700.0, 1000.0, 1500.0, 2000.0, 2420.0, 9000.0] {
            let c = land_colour(m);
            let bright = c[0] + c[1] + c[2];
            assert!(bright >= last, "{m} m: {bright} after {last}");
            last = bright;
        }
        assert_eq!(land_colour(0.0), RAMP[0].1);
        assert_eq!(land_colour(9000.0), RAMP[RAMP.len() - 1].1);
    }

    #[test]
    fn slopes_facing_the_sun_are_lit() {
        let c = [0.5, 0.4, 0.3];
        assert_eq!(shade_land(c, [0.0, 0.0]), c);
        let (lit, shaded) = (shade_land(c, [0.4, 0.4]), shade_land(c, [-0.4, -0.4]));
        for k in 0..3 {
            assert!(lit[k] > c[k] && c[k] > shaded[k], "channel {k}: {} {} {}", lit[k], c[k], shaded[k]);
        }
        // the steepest shaded ground keeps some colour, and is not brighter than a gentler one
        let steep = shade_land(c, [-50.0, -50.0]);
        assert!(steep[0] > 0.0 && steep[0] <= shaded[0]);
    }

    #[test]
    fn nodes_sit_on_their_lattice_lines() {
        // a ramp of 1/65535 per node eastwards, 2/65535 southwards
        let base: Vec<u16> = (0..BASE * BASE).map(|i| ((i % BASE) + 2 * (i / BASE)) as u16).collect();
        let unit = NODE_RANGE / 65535.0;
        assert!((coarse_at(&base, 8.0 * 10.0, 8.0 * 20.0 - 1.0) - unit * 50.0).abs() < 1e-3);
        assert!((coarse_at(&base, 8.0 * 10.0 + 4.0, 8.0 * 20.0 - 1.0) - unit * 50.5).abs() < 1e-3);
        assert!((coarse_at(&base, 8.0 * 10.0, 8.0 * 20.0 + 3.0) - unit * 51.0).abs() < 1e-3);
        // clamped at the world's edges
        assert!((coarse_at(&base, -100.0, -100.0) - 0.0).abs() < 1e-3);
        assert!((coarse_at(&base, 1e6, 1e6) - unit * (1279.0 * 3.0)).abs() < 1e-2);
    }

    #[test]
    fn tile_heights_add_the_block_mean() {
        let base = vec![16384u16; BASE * BASE]; // 1024.0 m (about)
        let coarse = 16384.0 / 65535.0 * NODE_RANGE;
        let mut detail = vec![100u8; DETAIL * DETAIL];
        detail[..CELL].fill(200); // the top row of the first block
        let h = tile_heights(&base, 0, 0, &detail);
        assert_eq!(h.len(), TILE * TILE);
        assert!((h[1] - (coarse + 100.0 - FINE_MIDDLE)).abs() < 1e-3);
        assert!((h[0] - (coarse + 125.0 - FINE_MIDDLE)).abs() < 1e-3);
    }

    #[test]
    fn tiles_join_without_a_seam() {
        // a plane across two tiles' width: one colour everywhere, across the tile border too
        let size = 2 * TILE;
        let heights: Vec<f32> = (0..size * size).map(|i| 800.0 + 0.2 * (i % size) as f32 + 0.1 * (i / size) as f32).collect();
        let rgb = shade_tiles(&heights, size);
        assert_eq!(rgb.len(), size * size * 3);
        let at = |y: usize, x: usize| &rgb[(y * size + x) * 3..][..3];
        for x in [TILE - 2, TILE - 1, TILE, TILE + 1] {
            let (a, b) = (at(TILE, x), at(TILE, x + 1));
            for k in 0..3 {
                assert!(a[k].abs_diff(b[k]) <= 1, "x {x}: {a:?} {b:?}");
            }
        }
        // that plane rises to the south-east, so it faces the sun: brighter than flat ground at the same height
        let flat = shade_tiles(&[heights[TILE * size + TILE]], 1);
        assert!(at(TILE, TILE)[0] > flat[0], "{:?} {:?}", at(TILE, TILE), &flat[..3]);
    }

    #[test]
    fn low_zoom_tiles_cover_the_world() {
        let t = std::env::temp_dir().join(format!("kiln-gameart-tiles-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&t);
        let mosaic = vec![200u8; MOSAIC * MOSAIC * 3];
        let mut total = 0;
        for (z, size, want) in [(2u32, MOSAIC / 2, 25u64), (1, MOSAIC / 4, 9), (0, MOSAIC / 8, 4)] {
            let small = downsample(&mosaic, MOSAIC, size);
            assert!(small.iter().all(|&v| v == 200));
            let (files, _) = cut_tiles(&small, size, z, &t).unwrap();
            assert_eq!(files, want);
            total += files;
        }
        assert_eq!(total + 100, 138); // the map's layout
        // zoom 0 is 320 px: tile 1_1 is mostly outside the world, which stays transparent
        let img = image::decode_webp(&std::fs::read(t.join("0/1_1.webp")).unwrap()).unwrap();
        assert_eq!(&img.px[..4], &[200, 200, 200, 255]);
        let last = (TILE * TILE - 1) * 4;
        assert_eq!(img.px[last + 3], 0);
        std::fs::remove_dir_all(&t).unwrap();
    }

    #[test]
    fn gzip_and_box() {
        let data: Vec<u8> = (0..5000u32).flat_map(|v| (v as u16).to_le_bytes()).collect();
        let z = gzip(&data).unwrap();
        let mut back = Vec::new();
        std::io::Read::read_to_end(&mut flate2::read::GzDecoder::new(&z[..]), &mut back).unwrap();
        assert_eq!(back, data);
        let four = Rgba { w: 2, h: 2, px: vec![0, 0, 0, 255, 100, 100, 100, 255, 0, 0, 0, 255, 100, 100, 100, 255] };
        assert_eq!(box_half_to(&four, 1).px, vec![50, 50, 50, 255]);
    }
}
