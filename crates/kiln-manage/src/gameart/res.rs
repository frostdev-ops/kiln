//! Typed views of the resources the extractor needs: `keen::UiTextureResource` (5,706 in client 26430625: item icons
//! `<Item>_icon`, menu and HUD images) and `keen::UiFontResource` (one resource: the font files). Field offsets come
//! from the game's own reflection (refl.rs, by field name) when the exe's records match the `.kfc`'s layout hashes;
//! otherwise from built-in layouts, accepted only when the `.kfc` still has the layout hash they were read from.
//!
//! Layouts (reflection of clients 26404966 and 26430625; docs/re/resources.md section 4):
//! - `UiTextureResource` 56 B, layout 0x3c9eb3fa: `data: ResourceContentReference` (its `hash: ContentHash` at +0),
//!   `debugName: string` +0x20, `size: uint2` +0x28, `type: TextureType` +0x30, `format: PixelFormat` +0x31,
//!   `levelCount: u8` +0x32, `isTiled: bool` +0x33.
//! - `UiFontResource` 16 B, layout 0xb3965140: `fonts: BlobArray<TtfFontResource>` +0, `languageGroups` +8;
//!   `TtfFontResource` 32 B: `fontFileData: ResourceContentReference` +0 (an OTF/TTF file as one content blob).

use super::kfc::{ContentHash, Kfc, ResourceKey, fnv1a32, i32_at, u32_at};
use super::refl::{K_BLOB_ARRAY, TypeDb};
use super::texture::Format;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

pub const UI_TEXTURE: &str = "keen::UiTextureResource";
pub const UI_FONT: &str = "keen::UiFontResource";
const UI_TEXTURE_LAYOUT: u32 = 0x3c9e_b3fa;
const UI_FONT_LAYOUT: u32 = 0xb396_5140;

/// Where the field offsets came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum LayoutSource {
    Reflection,
    Builtin,
}

#[derive(Debug, Clone)]
pub struct UiTexLayout {
    pub record: u32,
    pub hash: u64,
    pub name: u64,
    pub dims: u64,
    pub ty: u64,
    pub format: u64,
    pub levels: u64,
}

#[derive(Debug, Clone)]
pub struct FontLayout {
    pub fonts: u64,
    pub stride: u32,
    pub file_hash: u64,
}

#[derive(Debug, Clone)]
pub struct Layouts {
    pub source: LayoutSource,
    pub ui_tex: Option<UiTexLayout>,
    pub font: Option<FontLayout>,
    /// `keen::PixelFormat` value -> name
    pub formats: HashMap<u64, String>,
    /// `keen::TextureType` value of Texture2D
    pub texture_2d: u64,
}

fn builtin() -> Layouts {
    Layouts {
        source: LayoutSource::Builtin,
        ui_tex: Some(UiTexLayout { record: 56, hash: 0, name: 0x20, dims: 0x28, ty: 0x30, format: 0x31, levels: 0x32 }),
        font: Some(FontLayout { fonts: 0, stride: 32, file_hash: 0 }),
        formats: (0..=255u64).filter_map(|v| Format::builtin_name(v).map(|n| (v, n.to_string()))).collect(),
        texture_2d: 1,
    }
}

fn from_reflection(t: &TypeDb) -> Option<Layouts> {
    let tex = t.get(UI_TEXTURE)?;
    let ch = t.get("keen::ContentHash")?;
    if ch.size != 16 {
        return None;
    }
    let (hash, _) = t.path(UI_TEXTURE, "data.hash")?;
    let (name, _) = t.path(UI_TEXTURE, "debugName")?;
    let (dims, dt) = t.path(UI_TEXTURE, "size")?;
    if dt.map(|d| t.resolve(d).size) != Some(8) {
        return None;
    }
    let (ty, _) = t.path(UI_TEXTURE, "type")?;
    let (format, _) = t.path(UI_TEXTURE, "format")?;
    let (levels, _) = t.path(UI_TEXTURE, "levelCount")?;
    let ui_tex = Some(UiTexLayout { record: tex.size, hash, name, dims, ty, format, levels });
    let font = (|| {
        let (fonts, arr) = t.path(UI_FONT, "fonts")?;
        let arr = t.resolve(arr?);
        if arr.kind != K_BLOB_ARRAY {
            return None;
        }
        let el = t.resolve(t.get(arr.inner.as_deref()?)?);
        let (file_hash, _) = t.path(&el.name, "fontFileData.hash")?;
        Some(FontLayout { fonts, stride: el.size, file_hash })
    })();
    let formats = t.enum_values("keen::PixelFormat")?.into_iter().map(|(n, v)| (v, n)).collect();
    let texture_2d = t.enum_values("keen::TextureType")?.into_iter().find(|(n, _)| n == "Texture2D").map(|(_, v)| v)?;
    Some(Layouts { source: LayoutSource::Reflection, ui_tex, font, formats, texture_2d })
}

/// The layouts to read `kfc` with. `types`: the game exe's reflection (None when it could not be read).
pub fn layouts(kfc: &Kfc, types: Option<&TypeDb>) -> Result<Layouts> {
    let tex_hash = fnv1a32(UI_TEXTURE.as_bytes());
    let font_hash = fnv1a32(UI_FONT.as_bytes());
    let bundle_tex = kfc.bundle(tex_hash).map(|b| b.layout);
    let bundle_font = kfc.bundle(font_hash).map(|b| b.layout);
    if bundle_tex.is_none() {
        return Err(Error::new(
            ErrorCode::Unsupported,
            "This game folder has no UI textures (a dedicated server?). Pick the Enshrouded client.",
        ));
    }
    if let Some(t) = types {
        let exe_tex = t.get(UI_TEXTURE).map(|r| r.layout);
        if exe_tex.is_some() && exe_tex != bundle_tex {
            return Err(Error::new(
                ErrorCode::Conflict,
                "The game's exe and data files are from different builds. Wait for Steam to finish the update.",
            )
            .with_detail(serde_json::json!({"exeLayout": exe_tex, "kfcLayout": bundle_tex})));
        }
        if let Some(mut l) = from_reflection(t) {
            if t.get(UI_FONT).map(|r| r.layout) != bundle_font {
                l.font = None;
            }
            return Ok(l);
        }
    }
    let mut l = builtin();
    if bundle_tex != Some(UI_TEXTURE_LAYOUT) {
        return Err(Error::new(
            ErrorCode::Unsupported,
            "This game build changed its UI texture format, and the game's exe could not be read to follow it.",
        )
        .with_detail(serde_json::json!({"kfcLayout": bundle_tex, "known": UI_TEXTURE_LAYOUT})));
    }
    if bundle_font != Some(UI_FONT_LAYOUT) {
        l.font = None;
    }
    Ok(l)
}

/// One `UiTextureResource`.
#[derive(Debug, Clone)]
pub struct UiTexture {
    pub key: ResourceKey,
    pub name: String,
    pub w: u32,
    pub h: u32,
    pub format_name: String,
    pub format: Option<Format>,
    pub levels: u32,
    pub texture_2d: bool,
    pub content: ContentHash,
}

fn blob_string(b: &[u8], o: usize) -> Option<String> {
    let rel = i32_at(b, o)?;
    let n = u32_at(b, o + 4)? as usize;
    if n == 0 {
        return Some(String::new());
    }
    let start = (o as i64 + rel as i64) as usize;
    b.get(start..start + n).map(|s| String::from_utf8_lossy(s).into_owned())
}

/// Every `UiTextureResource` (all parts), in archive order.
pub fn ui_textures(kfc: &Kfc, l: &Layouts) -> Result<Vec<UiTexture>> {
    let lt = l.ui_tex.as_ref().ok_or_else(|| Error::new(ErrorCode::Unsupported, "no UI texture layout"))?;
    let th = fnv1a32(UI_TEXTURE.as_bytes());
    let mut out = Vec::new();
    for (ix, key) in kfc.resources_of_type(th) {
        let b = kfc.read(ix)?;
        let bad = || Error::new(ErrorCode::Parse, format!("UiTextureResource {}: {} bytes", key.guid_text(), b.len()));
        if b.len() < lt.record as usize {
            return Err(bad());
        }
        let content = ContentHash::from_bytes(&b[lt.hash as usize..]).ok_or_else(bad)?;
        let name = blob_string(&b, lt.name as usize).ok_or_else(bad)?;
        let w = u32_at(&b, lt.dims as usize).ok_or_else(bad)?;
        let h = u32_at(&b, lt.dims as usize + 4).ok_or_else(bad)?;
        let fv = *b.get(lt.format as usize).ok_or_else(bad)? as u64;
        let format_name = l.formats.get(&fv).cloned().unwrap_or_else(|| format!("#{fv}"));
        out.push(UiTexture {
            key,
            format: Format::from_name(&format_name),
            format_name,
            name,
            w,
            h,
            levels: (*b.get(lt.levels as usize).ok_or_else(bad)?).max(1) as u32,
            texture_2d: *b.get(lt.ty as usize).ok_or_else(bad)? as u64 == l.texture_2d,
            content,
        });
    }
    Ok(out)
}

/// One font file of `UiFontResource.fonts` (`index` = its place in that list: the editor's `font<NN>` names).
#[derive(Debug, Clone)]
pub struct FontFile {
    pub index: usize,
    pub content: ContentHash,
}

pub fn fonts(kfc: &Kfc, l: &Layouts) -> Result<Vec<FontFile>> {
    let Some(lf) = l.font.as_ref() else { return Ok(Vec::new()) };
    let th = fnv1a32(UI_FONT.as_bytes());
    let mut out = Vec::new();
    for (ix, key) in kfc.resources_of_type(th).into_iter().filter(|(_, k)| k.part == 0).take(1) {
        let b = kfc.read(ix)?;
        let bad = || Error::new(ErrorCode::Parse, format!("UiFontResource {}: bad font list", key.guid_text()));
        let o = lf.fonts as usize;
        let rel = i32_at(&b, o).ok_or_else(bad)?;
        let n = u32_at(&b, o + 4).ok_or_else(bad)? as usize;
        let base = (o as i64 + rel as i64) as usize;
        if n > 1024 || base + n * lf.stride as usize > b.len() {
            return Err(bad());
        }
        for i in 0..n {
            let e = base + i * lf.stride as usize + lf.file_hash as usize;
            out.push(FontFile { index: i, content: ContentHash::from_bytes(&b[e..]).ok_or_else(bad)? });
        }
    }
    Ok(out)
}
