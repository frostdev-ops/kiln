// SPDX-License-Identifier: GPL-3.0-or-later
// From matenm/emf-enshrouded v0.6.0 src/content/src/portraits/game.rs (MIT source).
// Copyright (c) 2026 EMF contributors; licenses/EMF-LICENSE.txt.
// Combined with the GPL-3.0-or-later EML archive reader only in this GPL component.
//! The game's archive as a portrait source: records, render data and albedo mips read from `<game>\<name>.kfc` and
//! its containers, read-only, with EML's reader (third_party/eml, kfc).
//!
//! Part of emf_content.dll, which as a whole is GPL-3.0-or-later (README.md).

use std::collections::HashMap;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

use kfc::container::{KFCCursor, KFCFile, KFCReader};
use kfc::guid::{ContentHash, Guid, ResourceId};
use kfc::reflection::{LookupKey, TypeRegistry};
use kfc::resource::value::{ConversionOptions, Value};

use super::model::{self, MaterialSlot, RenderModelInfo, TextureInfo};
use super::{Inputs, Source, TEXTURE_SIDE, Target, raster};

const TEMPLATE: &str = "keen::ecs::TemplateResource";
const RENDER_MODEL: &str = "keen::RenderModel";
const MATERIAL: &str = "keen::RenderMaterialResource";

/// A render model's records, read for its inputs and kept for drawing it.
struct Records {
    info: RenderModelInfo,
    /// Per material slot: the slot, its material's flags, and its albedo texture.
    materials: Vec<(MaterialSlot, Vec<String>, Option<TextureInfo>)>,
}

/// The game's archive, opened read-only.
pub struct Archive {
    cursor: KFCCursor<KFCReader>,
    types: TypeRegistry,
    records: HashMap<String, Records>,
}

/// The game's types: from the game's exe (EML's reader, about 0.2 s), else from EML's own cache of them
/// (`.cache\types.json`) when it was made from this archive's version.
fn load_types(game_dir: &Path, file_name: &str) -> Result<TypeRegistry, String> {
    let exe = game_dir.join(format!("{file_name}.exe"));
    let from_exe = match TypeRegistry::load_from_executable(&exe) {
        Ok(types) => return Ok(types),
        Err(e) => format!("{}: {e}", exe.display()),
    };
    let cache = game_dir.join(".cache").join("types.json");
    let version = KFCFile::get_version_tag(game_dir.join(format!("{file_name}.kfc"))).ok();
    let cached = std::fs::read(&cache).ok().and_then(|bytes| serde_json::from_slice::<TypeRegistry>(&bytes).ok());
    match (cached, version) {
        (Some(types), Some(version)) if types.version == version => Ok(types),
        _ => Err(format!("the game's types could not be read ({from_exe}; no current {})", cache.display())),
    }
}

/// FNV-1a, 64 bits: the records' fingerprint in the index.
fn fnv64(parts: &[&[u8]]) -> String {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for part in parts {
        for &b in part.iter().chain((part.len() as u64).to_le_bytes().iter()) {
            hash ^= b as u64;
            hash = hash.wrapping_mul(0x0100_0000_01b3);
        }
    }
    format!("{hash:016x}")
}

impl Archive {
    /// Opens `<game_dir>\<file_name>.kfc` and reads the game's types.
    pub fn open(game_dir: &Path, file_name: &str) -> Result<Self, String> {
        let kfc = game_dir.join(format!("{file_name}.kfc"));
        if !kfc.is_file() {
            return Err(format!("No {file_name}.kfc in {}", game_dir.display()));
        }
        let reader = KFCReader::new(game_dir, file_name).map_err(|e| format!("{}: {e}", kfc.display()))?;
        let cursor = reader.into_cursor().map_err(|e| format!("{}: {e}", kfc.display()))?;
        let types = load_types(game_dir, file_name)?;
        Ok(Archive { cursor, types, records: HashMap::new() })
    }

    /// A record by type and GUID, as JSON (enums and flags by name, GUIDs as text), and its bytes.
    fn record(&mut self, type_name: &str, guid: &str) -> Result<(serde_json::Value, Vec<u8>), String> {
        let parsed = Guid::parse(guid).ok_or_else(|| format!("{guid} is not a GUID"))?;
        let ty = self
            .types
            .get_by_name(LookupKey::Qualified(type_name))
            .ok_or_else(|| format!("the game's types have no {type_name}"))?;
        let id = ResourceId::new(parsed, ty.qualified_hash, 0);
        let bytes = self
            .cursor
            .read_resource(&id)
            .map_err(|e| format!("{type_name} {guid}: {e}"))?
            .ok_or_else(|| format!("no {type_name} {guid} in the game's archive"))?;
        let value = Value::from_bytes_with_options(&self.types, ty, &bytes, ConversionOptions::HUMAN_READABLE)
            .map_err(|e| format!("{type_name} {guid}: {e}"))?;
        let json = serde_json::to_value(&value).map_err(|e| format!("{type_name} {guid}: {e}"))?;
        Ok((json, bytes))
    }

    /// The bytes of one mip level of a texture, read from its content without reading the rest.
    fn read_level(&self, texture: &TextureInfo, pick: model::MipPick) -> Result<Vec<u8>, String> {
        let name = &texture.debug_name;
        let content = self
            .cursor
            .open_content(&texture.content)
            .map_err(|e| format!("{name}: {e}"))?
            .ok_or_else(|| format!("{name}: its content {} is not in the game's archive", texture.content))?;
        let mut file = content.into_inner();
        file.seek(SeekFrom::Current(pick.offset as i64)).map_err(|e| format!("{name}: {e}"))?;
        let mut bytes = vec![0u8; pick.len as usize];
        file.read_exact(&mut bytes).map_err(|e| format!("{name}: {e}"))?;
        Ok(bytes)
    }
}

/// Reads one mip level's bytes from a texture's content.
type ReadLevel<'a> = dyn FnMut(&TextureInfo, model::MipPick) -> Result<Vec<u8>, String> + 'a;

/// A material's albedo, decoded at the mip level `model::pick_mip` gives for TEXTURE_SIDE (never over
/// `model::MAX_DECODED_SIDE` px), as sRGB RGBA8.
fn decode_albedo(texture: &TextureInfo, read: &mut ReadLevel) -> Result<raster::Texture, String> {
    let name = &texture.debug_name;
    let pick = model::pick_mip(texture, TEXTURE_SIDE).ok_or_else(|| {
        format!(
            "{name}: no mip level of at most {} px found in its content ({} x {} {})",
            model::MAX_DECODED_SIDE,
            texture.width,
            texture.height,
            texture.format
        )
    })?;
    let bytes = read(texture, pick)?;
    let format: kfc::content::image::PixelFormat =
        serde_json::from_value(serde_json::Value::String(texture.format.clone()))
            .map_err(|_| format!("{name}: an unknown pixel format {}", texture.format))?;
    let mut rgba = vec![0u8; pick.width as usize * pick.height as usize * 4];
    kfc::content::image::decode(format, pick.width as usize, pick.height as usize, &bytes, &mut rgba)
        .map_err(|e| format!("{name}: {e}"))?;
    Ok(raster::Texture { width: pick.width, height: pick.height, rgba })
}

/// Material slot `i` for the renderer: its albedo decoded, or plain with a note when it has none or the albedo was not
/// decoded.
fn material(
    i: usize,
    slot: &MaterialSlot,
    flags: &[String],
    albedo: Option<&TextureInfo>,
    read: &mut ReadLevel,
    notes: &mut Vec<String>,
) -> raster::Material {
    let has = |flag: &str| flags.iter().any(|f| f == flag);
    let texture = match albedo {
        None => {
            notes.push(format!("material {i} has no albedo texture: drawn plain"));
            None
        }
        Some(t) => match decode_albedo(t, read) {
            Ok(texture) => Some(texture),
            Err(e) => {
                notes.push(format!("material {i}'s albedo was not decoded, drawn plain: {e}"));
                None
            }
        },
    };
    raster::Material {
        texture,
        alpha_cut: slot.alpha_tested || has("AlphaTested"),
        double_sided: slot.double_sided || has("DoubleSided"),
    }
}

impl Source for Archive {
    fn inputs(&mut self, target: &Target) -> Result<Inputs, String> {
        let model_guid = match target {
            Target::Model(guid) => guid.clone(),
            Target::Template(guid) => {
                let (template, _) = self.record(TEMPLATE, guid)?;
                model::template_model(&template).map_err(|e| format!("template {guid}: {e}"))?
            }
        };
        let (record, record_bytes) = self.record(RENDER_MODEL, &model_guid)?;
        let info = model::parse_render_model(&record).map_err(|e| format!("render model {model_guid}: {e}"))?;
        if info.meshes.len()>1024 || info.materials.len()>256 || info.meshes.iter().map(|m|m.vertex_count as u64).sum::<u64>()>1_000_000 ||
            info.meshes.iter().map(|m|m.index_count as u64).sum::<u64>()>3_000_000 {
            return Err("portrait model exceeds the mesh/vertex/index budget".into());
        }
        let mut materials = Vec::with_capacity(info.materials.len());
        let mut material_bytes = Vec::new();
        for slot in &info.materials {
            let Some(guid) = &slot.material else {
                materials.push((slot.clone(), Vec::new(), None));
                continue;
            };
            let (material, bytes) = self.record(MATERIAL, guid)?;
            let flags = material
                .get("flags")
                .and_then(serde_json::Value::as_array)
                .map(|a| a.iter().filter_map(|f| f.as_str().map(str::to_string)).collect())
                .unwrap_or_default();
            let albedo = model::material_albedo(&material).map_err(|e| format!("material {guid}: {e}"))?;
            materials.push((slot.clone(), flags, albedo));
            material_bytes.push(bytes);
        }
        let mut parts: Vec<&[u8]> = vec![&record_bytes];
        parts.extend(material_bytes.iter().map(Vec::as_slice));
        let inputs = Inputs {
            model: model_guid.clone(),
            name: info.debug_name.clone(),
            render_data: info.render_data.to_string(),
            albedo: materials.iter().map(|(_, _, t)| t.as_ref().map(|t| t.content.to_string())).collect(),
            records: fnv64(&parts),
        };
        self.records.insert(model_guid, Records { info, materials });
        Ok(inputs)
    }

    fn model(&mut self, inputs: &Inputs) -> Result<(raster::Model, Vec<String>), String> {
        let records = self
            .records
            .remove(&inputs.model)
            .ok_or_else(|| format!("render model {}: its records were not read first", inputs.model))?;
        let hash: ContentHash = records.info.render_data;
        if hash.size()>256*1024*1024 {return Err("portrait render data exceeds 256 MiB".into());}
        let blob = self
            .cursor
            .read_content(&hash)
            .map_err(|e| format!("{}: render data: {e}", inputs.name))?
            .ok_or_else(|| format!("{}: its render data {hash} is not in the game's archive", inputs.name))?;
        let (meshes, mut notes) = model::decode_lod0(&records.info, &blob).map_err(|e| format!("{}: {e}", inputs.name))?;
        let mut read = |texture: &TextureInfo, pick| self.read_level(texture, pick);
        let materials = records
            .materials
            .iter()
            .enumerate()
            .map(|(i, (slot, flags, albedo))| material(i, slot, flags, albedo.as_ref(), &mut read, &mut notes))
            .collect();
        Ok((raster::Model { meshes, materials }, notes))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::portraits::{Request, Status, run};

    /// The game's wolf: its render model, and the template of the early-game wolf that uses it.
    const WOLF_MODEL: &str = "91e2d4cc-01c9-4203-a92e-b08769364522";
    const WOLF_TEMPLATE: &str = "478381c5-585f-4264-b373-dbb1ea596eca";

    #[test]
    fn a_folder_without_the_archive_is_an_error_that_names_it() {
        let dir = std::env::temp_dir().join(format!("emf_portraits_noarchive_{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let error = Archive::open(&dir, "enshrouded").err().expect("an error");
        let _ = std::fs::remove_dir_all(&dir);
        assert!(error.contains("enshrouded") && error.contains(dir.to_str().unwrap()), "{error}");
        let error = Archive::open(Path::new("Z:\\no\\such\\folder"), "enshrouded").err().expect("an error");
        assert!(error.contains("Z:\\no\\such\\folder"), "{error}");
    }

    fn plain_slot() -> MaterialSlot {
        MaterialSlot { material: None, alpha_tested: false, double_sided: false }
    }

    fn bc7_albedo(side: u32, content: u32, mip_chain: Vec<(u64, u64)>) -> TextureInfo {
        TextureInfo {
            debug_name: "synthetic_albedo_map_0".into(),
            width: side,
            height: side,
            format: "BC7_srgb_block".into(),
            content: ContentHash::new(content, 1, 2, 3),
            mip_chain,
        }
    }

    #[test]
    fn an_albedo_without_a_level_of_at_most_512_px_is_drawn_plain_with_a_note() {
        // One range that holds only a 4096 px level 0: 16 MB of BC7, 64 MB decoded, 256 MB as the renderer's texels.
        let texture = bc7_albedo(4096, 16777216, vec![(0, 16777216)]);
        let mut asked = Vec::new();
        let mut notes = Vec::new();
        let mut read = |_: &TextureInfo, pick: model::MipPick| -> Result<Vec<u8>, String> {
            asked.push(pick);
            Err("not read".into())
        };
        let drawn = material(0, &plain_slot(), &[], Some(&texture), &mut read, &mut notes);
        assert!(drawn.texture.is_none());
        assert!(asked.is_empty(), "no level is read: {asked:?}");
        assert_eq!(notes.len(), 1, "{notes:?}");
        let note = &notes[0];
        for part in ["synthetic_albedo_map_0", "512 px", "drawn plain"] {
            assert!(note.contains(part), "{note}");
        }
    }

    #[test]
    fn a_one_range_albedo_is_read_and_decoded_at_256_px() {
        let texture = bc7_albedo(1024, 1398128, vec![(0, 1398128)]);
        let mut asked = Vec::new();
        let mut notes = Vec::new();
        // BC7 mode 6 blocks (the low byte's bit 6 names the mode)
        let mut read = |_: &TextureInfo, pick: model::MipPick| -> Result<Vec<u8>, String> {
            asked.push(pick);
            Ok((0..pick.len).map(|i| if i % 16 == 0 { 0x40 } else { 0 }).collect())
        };
        let drawn = material(1, &plain_slot(), &["AlphaTested".into()], Some(&texture), &mut read, &mut notes);
        assert_eq!(asked, [model::MipPick { level: 2, width: 256, height: 256, offset: 1310720, len: 65536 }]);
        let decoded = drawn.texture.expect("decoded");
        assert_eq!((decoded.width, decoded.height, decoded.rgba.len()), (256, 256, 256 * 256 * 4));
        assert!(drawn.alpha_cut && !drawn.double_sided);
        assert!(notes.is_empty(), "{notes:?}");
    }

    /// The game folder for the tests that read the real game (read-only), or None: they are skipped.
    fn game_dir() -> Option<std::path::PathBuf> {
        match std::env::var("EMF_PORTRAIT_GAME") {
            Ok(dir) if !dir.is_empty() => Some(dir.into()),
            _ => {
                eprintln!("EMF_PORTRAIT_GAME is not set: skipped");
                None
            }
        }
    }

    #[test]
    fn the_game_wolf_is_found_by_template_and_by_model() {
        let Some(game) = game_dir() else { return };
        let mut archive = Archive::open(&game, "enshrouded").unwrap();
        let by_template = archive.inputs(&Target::Template(WOLF_TEMPLATE.into())).unwrap();
        let by_model = archive.inputs(&Target::Model(WOLF_MODEL.into())).unwrap();
        assert_eq!(by_template, by_model);
        assert_eq!(by_model.model, WOLF_MODEL);
        assert_eq!(by_model.name, "char_enemy_wolf_01");
        assert_eq!(by_model.albedo.len(), 2);
        assert!(by_model.albedo.iter().all(Option::is_some), "{:?}", by_model.albedo);
        assert_eq!(by_model.records.len(), 16);
        let error = archive.inputs(&Target::Model("00000000-1111-2222-3333-444444444444".into())).unwrap_err();
        assert!(error.contains("00000000-1111-2222-3333-444444444444"), "{error}");
        // A template's GUID is not a render model's.
        assert!(archive.inputs(&Target::Model(WOLF_TEMPLATE.into())).is_err());
    }

    #[test]
    fn the_game_wolf_decodes_with_its_textures() {
        let Some(game) = game_dir() else { return };
        let mut archive = Archive::open(&game, "enshrouded").unwrap();
        let inputs = archive.inputs(&Target::Model(WOLF_MODEL.into())).unwrap();
        let (model, notes) = archive.model(&inputs).unwrap();
        assert!(notes.is_empty(), "{notes:?}");
        assert_eq!(model.meshes.iter().map(|m| m.positions.len()).collect::<Vec<_>>(), [4170, 6287]);
        assert_eq!(model.materials.len(), 2);
        for material in &model.materials {
            let texture = material.texture.as_ref().expect("an albedo");
            assert_eq!((texture.width, texture.height), (256, 256));
            assert_eq!(texture.rgba.len(), 256 * 256 * 4);
        }
        // The body is opaque and single-sided; the fur cards are alpha-tested and double-sided.
        assert!(!model.materials[0].alpha_cut && !model.materials[0].double_sided);
        assert!(model.materials[1].alpha_cut && model.materials[1].double_sided);
        let fur = model.materials[1].texture.as_ref().unwrap();
        let clear = fur.rgba.chunks(4).filter(|p| p[3] < 128).count();
        assert!(clear > 1000 && clear < 256 * 256 - 1000, "the fur texture has holes and strands: {clear} clear");
    }

    /// Draws the game's wolf through the whole request path into EMF_PORTRAIT_OUT (or a folder in %TEMP%), outside
    /// the repository: game art never goes into it.
    #[test]
    fn the_game_wolf_is_drawn() {
        let Some(game) = game_dir() else { return };
        let out = std::env::var("EMF_PORTRAIT_OUT")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|_| std::env::temp_dir().join("emf_portraits_wolf"));
        let request = serde_json::json!({
            "game_dir": game.to_str().unwrap(), "cache_dir": out.to_str().unwrap(),
            "items": [{"key": "wolf", "template": WOLF_TEMPLATE, "frames": 24, "size": 256}]
        });
        let request: Request = serde_json::from_value(request).unwrap();
        let started = std::time::Instant::now();
        let reply = run(request, "enshrouded", Archive::open);
        eprintln!("{} in {} ms", reply.to_json(), started.elapsed().as_millis());
        assert_eq!(reply.code, crate::OK, "{}", reply.to_json());
        assert!(matches!(reply.items[0].status, Status::Drawn | Status::Current));
        for i in 0..24 {
            let path = out.join("wolf").join(format!("{i:02}.png"));
            let decoder = png::Decoder::new(std::io::BufReader::new(std::fs::File::open(&path).unwrap()));
            let mut reader = decoder.read_info().unwrap();
            let mut pixels = vec![0u8; reader.output_buffer_size()];
            reader.next_frame(&mut pixels).unwrap();
            let covered = pixels.chunks(4).filter(|p| p[3] > 0).count() as f32 / (256.0 * 256.0);
            assert!(covered > 0.04 && covered < 0.7, "frame {i}: {covered} covered");
            for corner in [0usize, 255, 255 * 256, 256 * 256 - 1] {
                assert_eq!(pixels[corner * 4 + 3], 0, "frame {i}: a clear corner");
            }
        }
    }
}
