// SPDX-License-Identifier: GPL-3.0-or-later
// From matenm/emf-enshrouded v0.6.0 src/content/src/portraits/model.rs (MIT source).
// Copyright (c) 2026 EMF contributors; licenses/EMF-LICENSE.txt.
// Combined with the GPL-3.0-or-later EML archive reader only in this GPL component.
//! A game model's records and render data, decoded for the renderer. Pure functions: the records come in as JSON
//! (the game's records as EML's reader maps them, enums and flags by name, GUIDs as strings) and the render data as
//! bytes; nothing here reads files.
//!
//! Part of emf_content.dll, which as a whole is GPL-3.0-or-later (README.md).
//!
//! A `keen::RenderModel`'s render data (one content blob), as checked on the game's models since build 1273043:
//!
//! - `vertexData`: per mesh, at `vertexDataByteOffset`, the mesh's n vertices as separate streams: positions n*8,
//!   normals n*4, tangents n*4, colours n*4, UVs n*4 ("PNTCT", 24 bytes a vertex). Every LOD of a model shares them.
//! - A position is two u32 w0, w1: x = w0 >> 11, y = (w0 & 0x7FF) << 10 | (w1 >> 21) & 0x3FF, z = w1 & 0x1FFFFF,
//!   each / (2^21 - 1) * positionScale + positionOffset ("x21y21z21_unorm"; bit 31 of w1 is not used).
//! - A normal is three signed 10-bit fields (x in the low bits) / 511; a colour RGBA8; a UV two f16 (u in the low
//!   half).
//! - `indexData`: triangle lists, mesh-local, `indexOffset` counted in indices; u16 (u32 when the data's size says so).
//! - The model's `lods[0]` names the meshes of the finest level.

use std::ops::Range;

use kfc::guid::ContentHash;
use serde_json::Value;

use super::raster;

/// One mesh entry of a RenderModel record.
#[derive(Clone, Debug, PartialEq)]
pub struct MeshInfo {
    pub material_index: usize,
    pub vertex_count: usize,
    pub index_offset: usize,
    pub index_count: usize,
    pub vertex_data_byte_offset: usize,
    pub vertex_format: String,
    pub position_quantization: String,
}

/// One material slot of a RenderModel record.
#[derive(Clone, Debug, PartialEq)]
pub struct MaterialSlot {
    /// The RenderMaterialResource's GUID, None when the slot is empty.
    pub material: Option<String>,
    pub alpha_tested: bool,
    pub double_sided: bool,
}

/// What the renderer needs from a RenderModel record.
#[derive(Clone, Debug, PartialEq)]
pub struct RenderModelInfo {
    pub debug_name: String,
    pub position_scale: [f32; 3],
    pub position_offset: [f32; 3],
    pub render_data: ContentHash,
    pub vertex_data: Range<usize>,
    pub index_data: Range<usize>,
    pub meshes: Vec<MeshInfo>,
    /// The meshes of LOD 0 (every mesh when the record has no LODs).
    pub lod0: Range<usize>,
    pub materials: Vec<MaterialSlot>,
}

/// One texture of a RenderMaterialResource record.
#[derive(Clone, Debug, PartialEq)]
pub struct TextureInfo {
    pub debug_name: String,
    pub width: u32,
    pub height: u32,
    /// The pixel format's name, as EML's `PixelFormat` spells it (`BC7_srgb_block`).
    pub format: String,
    pub content: ContentHash,
    /// Per mip level from 0: where that level's chain starts in the content and how long it is.
    pub mip_chain: Vec<(u64, u64)>,
}

/// A mip level picked from a texture's content.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MipPick {
    pub level: usize,
    pub width: u32,
    pub height: u32,
    pub offset: u64,
    pub len: u64,
}

fn field<'a>(record: &'a Value, name: &str) -> Result<&'a Value, String> {
    match record.get(name) {
        Some(v) if !v.is_null() => Ok(v),
        _ => Err(format!("the record has no {name}")),
    }
}

fn number(record: &Value, name: &str) -> Result<f64, String> {
    field(record, name)?.as_f64().ok_or_else(|| format!("{name} is not a number"))
}

fn count(record: &Value, name: &str) -> Result<usize, String> {
    field(record, name)?.as_u64().map(|v| v as usize).ok_or_else(|| format!("{name} is not a count"))
}

fn text(record: &Value, name: &str) -> Result<String, String> {
    field(record, name)?.as_str().map(str::to_string).ok_or_else(|| format!("{name} is not text"))
}

fn vec3(record: &Value, name: &str) -> Result<[f32; 3], String> {
    let v = field(record, name)?;
    let k = |axis: &str| number(v, axis).map(|n| n as f32).map_err(|e| format!("{name}: {e}"));
    Ok([k("x")?, k("y")?, k("z")?])
}

/// A content reference's hash: `{"size", "hash0", "hash1", "hash2"}`.
fn content_hash(hash: &Value) -> Result<ContentHash, String> {
    let k = |name: &str| -> Result<u32, String> {
        field(hash, name)?.as_u64().and_then(|v| u32::try_from(v).ok()).ok_or_else(|| format!("{name} is not a u32"))
    };
    Ok(ContentHash::new(k("size")?, k("hash0")?, k("hash1")?, k("hash2")?))
}

/// A `{"start", "count"}` range of bytes.
fn byte_range(record: &Value, name: &str) -> Result<Range<usize>, String> {
    let v = field(record, name)?;
    let start = count(v, "start").map_err(|e| format!("{name}: {e}"))?;
    let len = count(v, "count").map_err(|e| format!("{name}: {e}"))?;
    Ok(start..start + len)
}

fn flags(v: Option<&Value>) -> Vec<String> {
    v.and_then(Value::as_array)
        .map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect())
        .unwrap_or_default()
}

/// Reads what the renderer needs from a RenderModel record.
pub fn parse_render_model(record: &Value) -> Result<RenderModelInfo, String> {
    let meshes = field(record, "meshes")?
        .as_array()
        .ok_or("meshes is not a list")?
        .iter()
        .enumerate()
        .map(|(i, m)| {
            let mesh = || -> Result<MeshInfo, String> {
                Ok(MeshInfo {
                    material_index: count(m, "materialIndex")?,
                    vertex_count: count(m, "vertexCount")?,
                    index_offset: count(m, "indexOffset")?,
                    index_count: count(m, "indexCount")?,
                    vertex_data_byte_offset: count(m, "vertexDataByteOffset")?,
                    vertex_format: text(m, "vertexFormat")?,
                    position_quantization: text(m, "positionQuantization")?,
                })
            };
            mesh().map_err(|e| format!("meshes[{i}]: {e}"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    let lod0 = match record.get("lods").and_then(Value::as_array).and_then(|l| l.first()) {
        None => 0..meshes.len(),
        Some(lod) => {
            let start = count(lod, "meshOffset").map_err(|e| format!("lods[0]: {e}"))?;
            let n = count(lod, "meshCount").map_err(|e| format!("lods[0]: {e}"))?;
            if start + n > meshes.len() {
                return Err(format!("LOD 0 names meshes {start} to {} of {}", start + n, meshes.len()));
            }
            start..start + n
        }
    };
    let materials = match record.get("materials") {
        None | Some(Value::Null) => Vec::new(),
        Some(list) => list
            .as_array()
            .ok_or("materials is not a list")?
            .iter()
            .map(|m| {
                let flags = flags(m.get("materialFlags"));
                MaterialSlot {
                    material: m.get("material").and_then(Value::as_str).map(str::to_string),
                    alpha_tested: flags.iter().any(|f| f == "AlphaTested"),
                    double_sided: flags.iter().any(|f| f == "DoubleSided"),
                }
            })
            .collect(),
    };
    Ok(RenderModelInfo {
        debug_name: record.get("debugName").and_then(Value::as_str).unwrap_or_default().to_string(),
        position_scale: vec3(record, "positionScale")?,
        position_offset: vec3(record, "positionOffset")?,
        render_data: content_hash(field(field(record, "renderData")?, "hash").map_err(|e| format!("renderData: {e}"))?)
            .map_err(|e| format!("renderData: {e}"))?,
        vertex_data: byte_range(record, "vertexData")?,
        index_data: byte_range(record, "indexData")?,
        meshes,
        lod0,
        materials,
    })
}

/// The ECS component that names a template's render model.
const MODEL_RESOURCE: &str = "keen::ecs::ModelResource";

/// The render model a template uses: its `keen::ecs::ModelResource` component's `model`.
pub fn template_model(record: &Value) -> Result<String, String> {
    let components = field(record, "components")?.as_array().ok_or("components is not a list")?;
    components
        .iter()
        .filter(|c| c.get("$type").and_then(Value::as_str) == Some(MODEL_RESOURCE))
        .find_map(|c| c.get("$value")?.get("model")?.as_str().map(str::to_string))
        .ok_or_else(|| format!("the template has no {MODEL_RESOURCE} component with a model"))
}

/// A material's albedo texture: the first image whose debugName contains "albedo" (any case). None when it has none.
pub fn material_albedo(record: &Value) -> Result<Option<TextureInfo>, String> {
    let Some(images) = record.get("images").and_then(Value::as_array) else {
        return Ok(None);
    };
    for image in images {
        let Some(texture) = image.get("texture") else { continue };
        let name = texture.get("debugName").and_then(Value::as_str).unwrap_or_default();
        if !name.to_ascii_lowercase().contains("albedo") {
            continue;
        }
        let read = || -> Result<TextureInfo, String> {
            let mip_chain = match texture.get("mipChainContentRanges").and_then(Value::as_array) {
                None => Vec::new(),
                Some(ranges) => ranges
                    .iter()
                    .map(|r| Ok((count(r, "start")? as u64, count(r, "count")? as u64)))
                    .collect::<Result<Vec<_>, String>>()?,
            };
            Ok(TextureInfo {
                debug_name: name.to_string(),
                width: count(texture, "width")? as u32,
                height: count(texture, "height")? as u32,
                format: text(texture, "format")?,
                content: content_hash(field(field(texture, "content")?, "hash")?)?,
                mip_chain,
            })
        };
        return read().map(Some).map_err(|e| format!("texture {name}: {e}"));
    }
    Ok(None)
}

/// The bytes of one mip level of `width` x `height` in `format`; None for a format EML does not know.
pub fn level_size(format: &str, width: u32, height: u32) -> Option<u64> {
    let format: kfc::content::image::PixelFormat = serde_json::from_value(Value::String(format.to_string())).ok()?;
    Some(kfc::content::image::size_of_format(format, width as usize, height as usize) as u64)
}

/// The longest side of a mip level that is ever decoded. A level decodes to 4 bytes a pixel and the renderer keeps
/// 16 more a pixel (raster's linear texels): about 5 MB at 512 px, where a 4096 px level 0 would take 320 MB.
pub const MAX_DECODED_SIDE: u32 = 512;

/// Mip level `level`'s size, or None past 1 x 1 or for a format EML does not know.
fn level_at(texture: &TextureInfo, level: usize, offset: u64) -> Option<MipPick> {
    let side = |full: u32| full.checked_shr(level as u32).map(|s| s.max(1));
    let (width, height) = (side(texture.width)?, side(texture.height)?);
    let len = level_size(&texture.format, width, height)?;
    Some(MipPick { level, width, height, offset, len })
}

/// The mip levels found in a texture's content, from the largest. Range i of `mip_chain` starts at level i and runs
/// to the end of the chain, the levels stored one after another (every texture of the game's build 1285601 is laid
/// out so). Each range gives its own level; a chain of one range gives every level in it, level k after the sizes
/// of the levels before it, up to the first level that runs past the range or the content. A range whose own level
/// runs past it or the content gives none.
fn found_levels(texture: &TextureInfo) -> Vec<MipPick> {
    let content = texture.content.size() as u64;
    let fits = |pick: &MipPick, end: u64| pick.offset.saturating_add(pick.len) <= end.min(content);
    match texture.mip_chain.as_slice() {
        [(start, count)] => {
            let end = start.saturating_add(*count);
            let mut levels = Vec::new();
            let mut offset = *start;
            while let Some(pick) = level_at(texture, levels.len(), offset).filter(|p| fits(p, end)) {
                offset += pick.len;
                levels.push(pick);
                if pick.width == 1 && pick.height == 1 {
                    break;
                }
            }
            levels
        }
        chain => chain
            .iter()
            .enumerate()
            .filter_map(|(level, &(start, count))| {
                level_at(texture, level, start).filter(|p| fits(p, start.saturating_add(count)))
            })
            .collect(),
    }
}

/// The largest mip level whose sides are at most `max_side` among the levels found in the content (else the smallest
/// found), never one whose longer side is over MAX_DECODED_SIDE. None when no level is left.
pub fn pick_mip(texture: &TextureInfo, max_side: u32) -> Option<MipPick> {
    let levels: Vec<MipPick> =
        found_levels(texture).into_iter().filter(|m| m.width.max(m.height) <= MAX_DECODED_SIDE).collect();
    levels.iter().find(|m| m.width.max(m.height) <= max_side).or(levels.last()).copied()
}

/// What `positionQuantization` the decoder knows, and the vertex format.
const PNTCT: &str = "PNTCT";
const X21Y21Z21: &str = "x21y21z21_unorm";
const M21: f32 = ((1u32 << 21) - 1) as f32;

fn u32_at(blob: &[u8], at: usize) -> u32 {
    u32::from_le_bytes([blob[at], blob[at + 1], blob[at + 2], blob[at + 3]])
}

fn snorm10(bits: u32) -> f32 {
    let v = (bits & 0x3FF) as i32;
    (if v >= 512 { v - 1024 } else { v }) as f32 / 511.0
}

/// LOD 0's meshes, decoded from the render data. Meshes in a vertex format other than PNTCT with x21y21z21_unorm
/// positions are left out with a note in the second list.
pub fn decode_lod0(info: &RenderModelInfo, blob: &[u8]) -> Result<(Vec<raster::Mesh>, Vec<String>), String> {
    if info.vertex_data.end > blob.len() || info.index_data.end > blob.len() {
        return Err(format!(
            "the render data is {} bytes, the record names vertices to {} and indices to {}",
            blob.len(),
            info.vertex_data.end,
            info.index_data.end
        ));
    }
    let all_indices: usize = info.meshes.iter().map(|m| m.index_count).sum();
    let index_size = match info.index_data.len() {
        _ if all_indices == 0 => 2,
        n if n == all_indices * 2 => 2,
        n if n == all_indices * 4 => 4,
        n => return Err(format!("{n} bytes of index data for {all_indices} indices: neither u16 nor u32")),
    };
    let mut meshes = Vec::new();
    let mut notes = Vec::new();
    for i in info.lod0.clone() {
        let m = &info.meshes[i];
        if m.vertex_format != PNTCT || m.position_quantization != X21Y21Z21 {
            notes.push(format!("mesh {i} left out: vertex format {} with {} positions", m.vertex_format,
                m.position_quantization));
            continue;
        }
        let n = m.vertex_count;
        let base = info.vertex_data.start + m.vertex_data_byte_offset;
        if base + n * 24 > info.vertex_data.end {
            return Err(format!("mesh {i}: {n} vertices at {base} run past the vertex data"));
        }
        let (scale, offset) = (info.position_scale, info.position_offset);
        let positions = (0..n)
            .map(|v| {
                let w0 = u32_at(blob, base + v * 8);
                let w1 = u32_at(blob, base + v * 8 + 4);
                let q = [w0 >> 11, ((w0 & 0x7FF) << 10) | ((w1 >> 21) & 0x3FF), w1 & 0x1F_FFFF];
                [0, 1, 2].map(|k| offset[k] + q[k] as f32 / M21 * scale[k])
            })
            .collect();
        let normals_at = base + n * 8;
        let normals = (0..n)
            .map(|v| {
                let w = u32_at(blob, normals_at + v * 4);
                [snorm10(w), snorm10(w >> 10), snorm10(w >> 20)]
            })
            .collect();
        // The vertex colours tint the albedo (the fur cards' colour is in them); all black, no shader multiplies by
        // them, so they are left out.
        let colours_at = base + n * 16;
        let colour_bytes = &blob[colours_at..colours_at + n * 4];
        let colors = if colour_bytes.chunks_exact(4).all(|c| c[..3] == [0, 0, 0]) {
            Vec::new()
        } else {
            colour_bytes.chunks_exact(4).map(|c| [0, 1, 2].map(|k| c[k] as f32 / 255.0)).collect()
        };
        let uvs_at = base + n * 20;
        let uvs = (0..n)
            .map(|v| {
                let w = u32_at(blob, uvs_at + v * 4);
                [half::f16::from_bits(w as u16).to_f32(), half::f16::from_bits((w >> 16) as u16).to_f32()]
            })
            .collect();
        let first = info.index_data.start + m.index_offset * index_size;
        let end = first + m.index_count * index_size;
        if end > info.index_data.end {
            return Err(format!("mesh {i}: {} indices at {first} run past the index data", m.index_count));
        }
        let index = |k: usize| -> u32 {
            let at = first + k * index_size;
            if index_size == 2 { u16::from_le_bytes([blob[at], blob[at + 1]]) as u32 } else { u32_at(blob, at) }
        };
        let triangles = (0..m.index_count / 3)
            .map(|t| {
                let tri = [index(t * 3), index(t * 3 + 1), index(t * 3 + 2)];
                match tri.iter().find(|&&v| v as usize >= n) {
                    Some(bad) => Err(format!("mesh {i}: vertex index {bad} out of range ({n} vertices)")),
                    None => Ok(tri),
                }
            })
            .collect::<Result<Vec<_>, _>>()?;
        meshes.push(raster::Mesh { positions, normals, uvs, colors, triangles, material: m.material_index });
    }
    Ok((meshes, notes))
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use serde_json::json;

    const M21: f32 = ((1u32 << 21) - 1) as f32;

    pub(crate) struct Vertex {
        pub position: [f32; 3],
        pub normal: [f32; 3],
        pub uv: [f32; 2],
        pub colour: [u8; 4],
    }

    fn snorm10(v: f32) -> u32 {
        ((v.clamp(-1.0, 1.0) * 511.0).round() as i32 as u32) & 0x3FF
    }

    /// Render data as the game lays it out, for `meshes` (vertices, triangles); positions quantised in the box
    /// `offset` .. `offset + scale`. Gives the blob and the RenderModel record that describes it.
    pub(crate) fn encode(meshes: &[(Vec<Vertex>, Vec<[u16; 3]>, usize)], offset: [f32; 3], scale: [f32; 3]) -> (Vec<u8>, Value) {
        let mut vertex_bytes = Vec::new();
        let mut index_words: Vec<u16> = Vec::new();
        let mut mesh_records = Vec::new();
        for (vertices, triangles, material) in meshes {
            let start = vertex_bytes.len();
            for v in vertices {
                let q: Vec<u32> = (0..3).map(|k| ((v.position[k] - offset[k]) / scale[k] * M21).round() as u32).collect();
                let w0 = (q[0] << 11) | (q[1] >> 10);
                let w1 = ((q[1] & 0x3FF) << 21) | q[2];
                vertex_bytes.extend_from_slice(&w0.to_le_bytes());
                vertex_bytes.extend_from_slice(&w1.to_le_bytes());
            }
            for v in vertices {
                let n = snorm10(v.normal[0]) | (snorm10(v.normal[1]) << 10) | (snorm10(v.normal[2]) << 20);
                vertex_bytes.extend_from_slice(&n.to_le_bytes());
            }
            vertex_bytes.extend(std::iter::repeat_n(0u8, vertices.len() * 4)); // tangents
            for v in vertices {
                vertex_bytes.extend_from_slice(&v.colour);
            }
            for v in vertices {
                vertex_bytes.extend_from_slice(&half::f16::from_f32(v.uv[0]).to_le_bytes());
                vertex_bytes.extend_from_slice(&half::f16::from_f32(v.uv[1]).to_le_bytes());
            }
            let index_offset = index_words.len();
            index_words.extend(triangles.iter().flatten());
            mesh_records.push(json!({
                "materialIndex": material, "vertexCount": vertices.len(), "indexOffset": index_offset,
                "indexCount": triangles.len() * 3, "vertexDataByteOffset": start, "vertexFormat": "PNTCT",
                "vertexStride": 24, "positionQuantization": "x21y21z21_unorm"
            }));
        }
        let mut blob = vertex_bytes.clone();
        let index_start = blob.len();
        for i in &index_words {
            blob.extend_from_slice(&i.to_le_bytes());
        }
        let record = json!({
            "debugName": "synthetic",
            "positionScale": {"x": scale[0], "y": scale[1], "z": scale[2]},
            "positionOffset": {"x": offset[0], "y": offset[1], "z": offset[2]},
            "materials": [
                {"material": "daad25cc-afa3-41f0-bdef-77d002327d48", "renderEffectId": "Pbr", "materialFlags": ["CanDissolve"]},
                {"material": "a9b66c71-ddc2-45cb-9541-583f38e2e8dc", "renderEffectId": "Pbr",
                 "materialFlags": ["AlphaTested", "DoubleSided"]}
            ],
            "meshes": mesh_records,
            "lods": [{"meshOffset": 0, "meshCount": meshes.len(), "pixelSize": 0.0}],
            "renderData": {"hash": {"size": blob.len(), "hash0": 1, "hash1": 2, "hash2": 3}, "category": {"value": 0},
                           "bundleId": 0, "sortKey": 0},
            "vertexData": {"start": 0, "count": vertex_bytes.len()},
            "indexData": {"start": index_start, "count": index_words.len() * 2},
        });
        (blob, record)
    }

    pub(crate) fn tetrahedron(material: usize) -> (Vec<Vertex>, Vec<[u16; 3]>, usize) {
        let p = [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]];
        let vertices = p
            .iter()
            .enumerate()
            .map(|(i, &position)| Vertex {
                position,
                normal: [0.0, 1.0, 0.0],
                uv: [i as f32 * 0.25, 1.0 - i as f32 * 0.25],
                colour: [0, 0, 0, 255],
            })
            .collect();
        (vertices, vec![[0, 1, 2], [0, 2, 3], [0, 3, 1], [1, 3, 2]], material)
    }

    #[test]
    fn a_render_model_record_is_read() {
        let (_, record) = encode(&[tetrahedron(0), tetrahedron(1)], [-1.0, -2.0, -3.0], [4.0, 5.0, 6.0]);
        let info = parse_render_model(&record).unwrap();
        assert_eq!(info.debug_name, "synthetic");
        assert_eq!(info.position_offset, [-1.0, -2.0, -3.0]);
        assert_eq!(info.position_scale, [4.0, 5.0, 6.0]);
        assert_eq!(info.meshes.len(), 2);
        assert_eq!(info.lod0, 0..2);
        assert_eq!(info.meshes[1].material_index, 1);
        assert_eq!(info.meshes[1].vertex_data_byte_offset, 4 * 24);
        assert_eq!(info.materials.len(), 2);
        assert_eq!(info.materials[0].material.as_deref(), Some("daad25cc-afa3-41f0-bdef-77d002327d48"));
        assert!(!info.materials[0].alpha_tested && !info.materials[0].double_sided);
        assert!(info.materials[1].alpha_tested && info.materials[1].double_sided);
        assert_eq!(info.render_data, ContentHash::new(info.render_data.size(), 1, 2, 3));
    }

    #[test]
    fn a_record_without_a_needed_field_is_an_error_that_names_it() {
        let (_, record) = encode(&[tetrahedron(0)], [0.0; 3], [1.0; 3]);
        for field in ["positionScale", "meshes", "renderData", "indexData"] {
            let mut broken = record.clone();
            broken.as_object_mut().unwrap().remove(field);
            let error = parse_render_model(&broken).unwrap_err();
            assert!(error.contains(field), "{field}: {error}");
        }
    }

    #[test]
    fn lod0_is_the_first_lods_meshes() {
        let (_, mut record) = encode(&[tetrahedron(0), tetrahedron(1), tetrahedron(0)], [0.0; 3], [1.0; 3]);
        record["lods"] = json!([{"meshOffset": 1, "meshCount": 2}, {"meshOffset": 0, "meshCount": 1}]);
        assert_eq!(parse_render_model(&record).unwrap().lod0, 1..3);
        record["lods"] = json!([]);
        assert_eq!(parse_render_model(&record).unwrap().lod0, 0..3, "no LODs: every mesh");
        record["lods"] = json!([{"meshOffset": 2, "meshCount": 5}]);
        assert!(parse_render_model(&record).unwrap_err().contains("LOD"));
    }

    #[test]
    fn render_data_decodes_to_the_positions_normals_uvs_and_triangles() {
        let (offset, scale) = ([-1.0, -2.0, -3.0], [4.0, 5.0, 6.0]);
        let (blob, record) = encode(&[tetrahedron(0), tetrahedron(1)], offset, scale);
        let info = parse_render_model(&record).unwrap();
        let (meshes, notes) = decode_lod0(&info, &blob).unwrap();
        assert!(notes.is_empty(), "{notes:?}");
        assert_eq!(meshes.len(), 2);
        let (vertices, triangles, _) = tetrahedron(0);
        for (m, mesh) in meshes.iter().enumerate() {
            assert_eq!(mesh.material, m);
            assert_eq!(mesh.triangles, triangles.iter().map(|t| t.map(u32::from)).collect::<Vec<_>>());
            for (i, v) in vertices.iter().enumerate() {
                for k in 0..3 {
                    // One step of 21 bits over the box
                    assert!((mesh.positions[i][k] - v.position[k]).abs() <= scale[k] / M21, "{m} {i} {k}");
                    assert!((mesh.normals[i][k] - v.normal[k]).abs() < 0.002, "{m} {i} {k}");
                }
                assert_eq!(mesh.uvs[i], v.uv);
            }
        }
    }

    #[test]
    fn vertex_colours_decode_as_a_tint_unless_all_black() {
        let mut coloured = tetrahedron(0);
        for (i, v) in coloured.0.iter_mut().enumerate() {
            v.colour = [255, 51 * i as u8, 0, 7];
        }
        let black = tetrahedron(1);
        let (blob, record) = encode(&[coloured, black], [0.0; 3], [1.0; 3]);
        let (meshes, _) = decode_lod0(&parse_render_model(&record).unwrap(), &blob).unwrap();
        assert_eq!(meshes[0].colors.len(), 4);
        assert_eq!(meshes[0].colors[2], [1.0, 102.0 / 255.0, 0.0]);
        // Colours that are all black are no tint (a shader that multiplied by them would draw the mesh black).
        assert!(meshes[1].colors.is_empty());
    }

    #[test]
    fn negative_normals_decode_signed() {
        let mut t = tetrahedron(0);
        t.0[0].normal = [-1.0, -0.5, 0.25];
        let (blob, record) = encode(&[t], [0.0; 3], [1.0; 3]);
        let (meshes, _) = decode_lod0(&parse_render_model(&record).unwrap(), &blob).unwrap();
        let n = meshes[0].normals[0];
        assert!((n[0] + 1.0).abs() < 0.003 && (n[1] + 0.5).abs() < 0.003 && (n[2] - 0.25).abs() < 0.003, "{n:?}");
    }

    #[test]
    fn other_vertex_formats_are_left_out_with_a_note() {
        let (blob, mut record) = encode(&[tetrahedron(0), tetrahedron(1)], [0.0; 3], [1.0; 3]);
        record["meshes"][0]["vertexFormat"] = json!("P");
        let (meshes, notes) = decode_lod0(&parse_render_model(&record).unwrap(), &blob).unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(meshes[0].material, 1);
        assert_eq!(notes.len(), 1);
        assert!(notes[0].contains('P'), "{notes:?}");
    }

    #[test]
    fn short_or_wrong_render_data_is_an_error_not_a_panic() {
        let (blob, record) = encode(&[tetrahedron(0)], [0.0; 3], [1.0; 3]);
        let info = parse_render_model(&record).unwrap();
        for cut in [0, 10, blob.len() - 1] {
            assert!(decode_lod0(&info, &blob[..cut]).is_err(), "cut at {cut}");
        }
        let mut bad = blob.clone();
        let at = info.index_data.start;
        bad[at..at + 2].copy_from_slice(&40u16.to_le_bytes());
        let error = decode_lod0(&info, &bad).unwrap_err();
        assert!(error.contains("40"), "{error}");
    }

    #[test]
    fn u32_indices_when_the_index_data_says_so() {
        let (blob, record) = encode(&[tetrahedron(0)], [0.0; 3], [1.0; 3]);
        let info = parse_render_model(&record).unwrap();
        let mut wide = blob[..info.index_data.start].to_vec();
        for pair in blob[info.index_data.clone()].chunks(2) {
            wide.extend_from_slice(&(u16::from_le_bytes([pair[0], pair[1]]) as u32).to_le_bytes());
        }
        let mut record = record.clone();
        record["indexData"]["count"] = json!(info.index_data.len() * 2);
        let wide_info = parse_render_model(&record).unwrap();
        let (meshes, _) = decode_lod0(&wide_info, &wide).unwrap();
        assert_eq!(meshes[0].triangles, vec![[0, 1, 2], [0, 2, 3], [0, 3, 1], [1, 3, 2]]);
    }

    #[test]
    fn a_templates_model_is_its_model_resource() {
        let record = json!({"name": "Enemy_Wildbeast_wolf_earlygame", "components": [
            {"$type": "keen::ecs::Actor", "$value": {}},
            {"$type": "keen::ecs::ModelComponent", "$value": {"modelHandle": 0}},
            {"$type": "keen::ecs::ModelResource", "$value": {"model": "91e2d4cc-01c9-4203-a92e-b08769364522"}}
        ]});
        assert_eq!(template_model(&record).unwrap(), "91e2d4cc-01c9-4203-a92e-b08769364522");
        let none = json!({"name": "x", "components": [{"$type": "keen::ecs::Actor", "$value": {}}]});
        assert!(template_model(&none).unwrap_err().contains("ModelResource"));
        let empty = json!({"name": "x", "components": [{"$type": "keen::ecs::ModelResource", "$value": {"model": null}}]});
        assert!(template_model(&empty).unwrap_err().contains("ModelResource"));
    }

    pub(crate) fn wolf_fur_material() -> Value {
        json!({"effectId": "Pbr", "flags": ["AlphaTested", "DoubleSided"], "images": [
            {"dataOffset": 28, "texture": {"width": 1024, "height": 1024, "levelCount": 11, "format": "BC5_unorm_block",
             "content": {"hash": {"size": 1398128, "hash0": 1, "hash1": 2, "hash2": 3}},
             "mipChainContentRanges": [{"start": 0, "count": 1398128}],
             "debugName": "Material_char_enemy_wolf_01_fur_normal_map_0"}},
            {"dataOffset": 20, "texture": {"width": 1024, "height": 1024, "levelCount": 11, "format": "BC7_srgb_block",
             "content": {"hash": {"size": 1398128, "hash0": 799083884, "hash1": 2366324622u32, "hash2": 2759706436u32}},
             "mipChainContentRanges": [{"start": 0, "count": 1398128}, {"start": 1048576, "count": 349552},
                 {"start": 1310720, "count": 87408}, {"start": 1376256, "count": 21872}, {"start": 1392640, "count": 5488}],
             "debugName": "Material_char_enemy_wolf_01_fur_albedo_map_0"}}
        ], "debugName": "char_enemy_wolf_01_fur"})
    }

    #[test]
    fn a_materials_albedo_is_found_by_name() {
        let albedo = material_albedo(&wolf_fur_material()).unwrap().unwrap();
        assert_eq!(albedo.debug_name, "Material_char_enemy_wolf_01_fur_albedo_map_0");
        assert_eq!((albedo.width, albedo.height), (1024, 1024));
        assert_eq!(albedo.format, "BC7_srgb_block");
        assert_eq!(albedo.content, ContentHash::new(1398128, 799083884, 2366324622, 2759706436));
        assert_eq!(albedo.mip_chain.len(), 5);
        assert_eq!(albedo.mip_chain[2], (1310720, 87408));
        let mut none = wolf_fur_material();
        none["images"].as_array_mut().unwrap().pop();
        assert_eq!(material_albedo(&none).unwrap(), None);
    }

    #[test]
    fn the_mip_is_the_largest_that_fits() {
        let albedo = material_albedo(&wolf_fur_material()).unwrap().unwrap();
        // 1024 BC7 (one byte a pixel): 256 px is level 2, 64 KB at 1310720.
        assert_eq!(pick_mip(&albedo, 256), Some(MipPick { level: 2, width: 256, height: 256, offset: 1310720, len: 65536 }));
        assert_eq!(pick_mip(&albedo, 300).unwrap().level, 2);
        assert_eq!(pick_mip(&albedo, 512).unwrap().level, 1);
        // Smaller than every level the content holds: the smallest one (64 px, level 4).
        assert_eq!(pick_mip(&albedo, 16).unwrap().level, 4);
        let mut no_chain = albedo.clone();
        no_chain.mip_chain.clear();
        assert_eq!(pick_mip(&no_chain, 256), None);
        // A level that does not fit in the content
        let mut short = albedo.clone();
        short.mip_chain[2] = (1310720, 100);
        assert_eq!(pick_mip(&short, 256).unwrap().level, 3, "a level shorter than its data is skipped");
    }

    fn texture(width: u32, height: u32, format: &str, content: u32, mip_chain: Vec<(u64, u64)>) -> TextureInfo {
        TextureInfo {
            debug_name: "synthetic_albedo_map_0".into(),
            width,
            height,
            format: format.into(),
            content: ContentHash::new(content, 1, 2, 3),
            mip_chain,
        }
    }

    #[test]
    fn a_chain_of_one_range_is_walked_by_the_levels_sizes() {
        // 1024 BC7 (16 bytes a 4x4 block): levels of 1 MB, 256 KB, 64 KB, ... one after another, 1398128 in all.
        let bc7 = texture(1024, 1024, "BC7_srgb_block", 1398128, vec![(0, 1398128)]);
        assert_eq!(pick_mip(&bc7, 256), Some(MipPick { level: 2, width: 256, height: 256, offset: 1310720, len: 65536 }));
        assert_eq!(pick_mip(&bc7, 64), Some(MipPick { level: 4, width: 64, height: 64, offset: 1392640, len: 4096 }));
        // 2048 BC1 (8 bytes a block): 2 MB, 512 KB, 128 KB, then 256 px at 2752512.
        let bc1 = texture(2048, 2048, "BC1_RGB_unorm_block", 2796216, vec![(0, 2796216)]);
        assert_eq!(pick_mip(&bc1, 256), Some(MipPick { level: 3, width: 256, height: 256, offset: 2752512, len: 32768 }));
        // Not square: 512 x 1024 BC7, level 2 is 128 x 256.
        let tall = texture(512, 1024, "BC7_srgb_block", 699088, vec![(0, 699088)]);
        assert_eq!(pick_mip(&tall, 256), Some(MipPick { level: 2, width: 128, height: 256, offset: 655360, len: 32768 }));
        // The levels are counted from where the range starts.
        let later = texture(1024, 1024, "BC7_srgb_block", 1398128 + 4096, vec![(4096, 1398128)]);
        assert_eq!(pick_mip(&later, 256).unwrap().offset, 4096 + 1310720);
        // The walk stops where the range or the content ends: here after 1024 and 512 px, so 512 px is the smallest.
        let two = texture(1024, 1024, "BC7_srgb_block", 1398128, vec![(0, 1310720)]);
        assert_eq!(pick_mip(&two, 256), Some(MipPick { level: 1, width: 512, height: 512, offset: 1048576, len: 262144 }));
        let cut = texture(1024, 1024, "BC7_srgb_block", 1310720 + 100, vec![(0, 1398128)]);
        assert_eq!(pick_mip(&cut, 256).unwrap().level, 1);
    }

    #[test]
    fn no_level_over_512_px_is_picked() {
        // A range that holds only level 0 has no smaller levels to find.
        let bc7 = texture(4096, 4096, "BC7_srgb_block", 16777216, vec![(0, 16777216)]);
        assert_eq!(pick_mip(&bc7, 256), None);
        let bc1 = texture(4096, 4096, "BC1_RGB_unorm_block", 8388608, vec![(0, 8388608)]);
        assert_eq!(pick_mip(&bc1, 256), None);
        let one = texture(1024, 1024, "BC7_srgb_block", 1398128, vec![(0, 1048576)]);
        assert_eq!(pick_mip(&one, 256), None, "1024 px alone");
        // Ranges of their own for 4096 and 2048 px only.
        let big = texture(4096, 4096, "BC7_srgb_block", 22369648, vec![(0, 22369648), (16777216, 5592432)]);
        assert_eq!(pick_mip(&big, 256), None);
        // Asked for more than 512 px: 512 px.
        let albedo = material_albedo(&wolf_fur_material()).unwrap().unwrap();
        assert_eq!(pick_mip(&albedo, 1024).unwrap().width, 512);
    }

    /// The game's wolf, from a dump of its records and render data (game data: read from the private analysis
    /// folder named by EMF_PORTRAIT_DUMP, never copied into the repository). Skipped when it is not set.
    #[test]
    fn the_dumped_wolf_decodes() {
        let Ok(dir) = std::env::var("EMF_PORTRAIT_DUMP") else {
            eprintln!("EMF_PORTRAIT_DUMP is not set: skipped");
            return;
        };
        let dir = std::path::Path::new(&dir);
        // The dump is Lua's tostring of the record: JSON but for nil.
        let text = std::fs::read_to_string(dir.join("char_enemy_wolf_01.json")).unwrap();
        let text = text.replace(":nil", ":null").replace("[nil", "[null").replace(",nil", ",null");
        let record: Value = serde_json::from_str(&text).unwrap();
        let blob = std::fs::read(dir.join("char_enemy_wolf_01.render.bin")).unwrap();
        let info = parse_render_model(&record).unwrap();
        assert_eq!(info.debug_name, "char_enemy_wolf_01");
        assert_eq!(info.render_data.size() as usize, blob.len());
        let (meshes, notes) = decode_lod0(&info, &blob).unwrap();
        assert!(notes.is_empty(), "{notes:?}");
        assert_eq!(meshes.iter().map(|m| m.positions.len()).collect::<Vec<_>>(), [4170, 6287]);
        assert_eq!(meshes.iter().map(|m| m.triangles.len()).collect::<Vec<_>>(), [18195 / 3, 15972 / 3]);
        assert_eq!(meshes.iter().map(|m| m.material).collect::<Vec<_>>(), [0, 1]);
        // The decoded positions reproduce the record's bounding box.
        let bb = &record["boundingBox"];
        for (k, axis) in ["x", "y", "z"].iter().enumerate() {
            let lo = meshes.iter().flat_map(|m| &m.positions).map(|p| p[k]).fold(f32::INFINITY, f32::min);
            let hi = meshes.iter().flat_map(|m| &m.positions).map(|p| p[k]).fold(f32::NEG_INFINITY, f32::max);
            assert!((lo - bb["min"][axis].as_f64().unwrap() as f32).abs() < 1e-3, "{axis} min {lo}");
            assert!((hi - bb["max"][axis].as_f64().unwrap() as f32).abs() < 1e-3, "{axis} max {hi}");
        }
        for name in ["char_enemy_wolf_01.material1.json", "char_enemy_wolf_01.material2.json"] {
            let material: Value = serde_json::from_str(&std::fs::read_to_string(dir.join(name)).unwrap()).unwrap();
            let albedo = material_albedo(&material).unwrap().expect("an albedo");
            assert!(albedo.debug_name.contains("albedo_map"), "{}", albedo.debug_name);
            assert_eq!(pick_mip(&albedo, 256).unwrap().width, 256);
        }
    }
}
