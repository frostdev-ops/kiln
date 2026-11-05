//! Texture data -> RGBA8. `keen::PixelFormat` names come from the game's reflection (built-in values as fallback);
//! the content blob holds the mip chain, largest level first, tightly packed (verified: a 256² BC7 icon with 9 levels
//! is 65536 + 16384 + ... + 16 = 87,408 bytes). BCn blocks decode with `bcdec_rs` (MIT).

use super::image::Rgba;
use crate::{Error, ErrorCode, Result};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Format {
    Rgba8,
    Bgra8,
    R8,
    Rg8,
    R16,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
}

impl Format {
    /// From a `keen::PixelFormat` enum name (`BC7_unorm_block`, `R8G8B8A8_srgb`, ...). sRGB and unorm decode alike:
    /// the bytes are passed through, the browser shows them as sRGB like the game's UI does.
    pub fn from_name(name: &str) -> Option<Format> {
        Some(match name {
            "R8G8B8A8_unorm" | "R8G8B8A8_srgb" | "A8B8G8R8_unorm_pack32" | "A8B8G8R8_srgb_pack32" => Format::Rgba8,
            "B8G8R8A8_unorm" | "B8G8R8A8_srgb" => Format::Bgra8,
            "R8_unorm" | "R8_srgb" => Format::R8,
            "R8G8_unorm" | "R8G8_srgb" => Format::Rg8,
            "R16_unorm" => Format::R16,
            "BC1_RGB_unorm_block" | "BC1_RGB_srgb_block" | "BC1_RGBA_unorm_block" | "BC1_RGBA_srgb_block" => Format::Bc1,
            "BC2_unorm_block" | "BC2_srgb_block" => Format::Bc2,
            "BC3_unorm_block" | "BC3_srgb_block" => Format::Bc3,
            "BC4_unorm_block" => Format::Bc4,
            "BC5_unorm_block" => Format::Bc5,
            "BC7_unorm_block" | "BC7_srgb_block" => Format::Bc7,
            _ => return None,
        })
    }

    /// Built-in `keen::PixelFormat` values (client 26404966 and 26430625, from reflection) for when the exe's records
    /// cannot be read.
    pub fn builtin_name(v: u64) -> Option<&'static str> {
        Some(match v {
            9 => "R8_unorm",
            15 => "R8_srgb",
            16 => "R8G8_unorm",
            22 => "R8G8_srgb",
            37 => "R8G8B8A8_unorm",
            43 => "R8G8B8A8_srgb",
            44 => "B8G8R8A8_unorm",
            50 => "B8G8R8A8_srgb",
            70 => "R16_unorm",
            131 => "BC1_RGB_unorm_block",
            132 => "BC1_RGB_srgb_block",
            133 => "BC1_RGBA_unorm_block",
            134 => "BC1_RGBA_srgb_block",
            135 => "BC2_unorm_block",
            136 => "BC2_srgb_block",
            137 => "BC3_unorm_block",
            138 => "BC3_srgb_block",
            139 => "BC4_unorm_block",
            140 => "BC4_snorm_block",
            141 => "BC5_unorm_block",
            142 => "BC5_snorm_block",
            143 => "BC6H_ufloat_block",
            144 => "BC6H_sfloat_block",
            145 => "BC7_unorm_block",
            146 => "BC7_srgb_block",
            _ => return None,
        })
    }

    fn block_bytes(self) -> Option<usize> {
        match self {
            Format::Bc1 | Format::Bc4 => Some(8),
            Format::Bc2 | Format::Bc3 | Format::Bc5 | Format::Bc7 => Some(16),
            _ => None,
        }
    }

    fn pixel_bytes(self) -> usize {
        match self {
            Format::Rgba8 | Format::Bgra8 => 4,
            Format::R8 => 1,
            Format::Rg8 | Format::R16 => 2,
            _ => 0,
        }
    }

    /// Bytes of one level of `w`x`h`.
    pub fn level_size(self, w: u32, h: u32) -> usize {
        let (w, h) = (w.max(1) as usize, h.max(1) as usize);
        match self.block_bytes() {
            Some(b) => w.div_ceil(4) * h.div_ceil(4) * b,
            None => w * h * self.pixel_bytes(),
        }
    }

    /// Offset of `level` in the mip chain and that level's size in pixels.
    pub fn level_offset(self, w: u32, h: u32, level: u32) -> (usize, u32, u32) {
        let mut off = 0;
        for l in 0..level {
            off += self.level_size((w >> l).max(1), (h >> l).max(1));
        }
        (off, (w >> level).max(1), (h >> level).max(1))
    }
}

/// Decodes one level (`data` starts at that level) into RGBA8.
pub fn decode(fmt: Format, w: u32, h: u32, data: &[u8]) -> Result<Rgba> {
    let need = fmt.level_size(w, h);
    if data.len() < need {
        return Err(Error::new(ErrorCode::Parse, format!("texture data: {} bytes, {w}x{h} {fmt:?} needs {need}", data.len())));
    }
    let (wu, hu) = (w as usize, h as usize);
    let mut px = vec![0u8; wu * hu * 4];
    match fmt {
        Format::Rgba8 => px.copy_from_slice(&data[..need]),
        Format::Bgra8 => {
            for (o, i) in px.chunks_exact_mut(4).zip(data.chunks_exact(4)) {
                o.copy_from_slice(&[i[2], i[1], i[0], i[3]]);
            }
        }
        Format::R8 => {
            for (o, &v) in px.chunks_exact_mut(4).zip(data.iter()) {
                o.copy_from_slice(&[v, v, v, 255]);
            }
        }
        Format::Rg8 => {
            for (o, i) in px.chunks_exact_mut(4).zip(data.chunks_exact(2)) {
                o.copy_from_slice(&[i[0], i[1], 0, 255]);
            }
        }
        Format::R16 => {
            for (o, i) in px.chunks_exact_mut(4).zip(data.chunks_exact(2)) {
                let v = (u16::from_le_bytes([i[0], i[1]]) >> 8) as u8;
                o.copy_from_slice(&[v, v, v, 255]);
            }
        }
        _ => decode_blocks(fmt, wu, hu, data, &mut px),
    }
    Ok(Rgba { w, h, px })
}

fn decode_blocks(fmt: Format, w: usize, h: usize, data: &[u8], px: &mut [u8]) {
    let bb = fmt.block_bytes().unwrap();
    let (bw, bh) = (w.div_ceil(4), h.div_ceil(4));
    let mut block = [0u8; 64];
    let mut one = [0u8; 16];
    let mut two = [0u8; 32];
    for by in 0..bh {
        for bx in 0..bw {
            let src = &data[(by * bw + bx) * bb..][..bb];
            match fmt {
                Format::Bc1 => bcdec_rs::bc1(src, &mut block, 16),
                Format::Bc2 => bcdec_rs::bc2(src, &mut block, 16),
                Format::Bc3 => bcdec_rs::bc3(src, &mut block, 16),
                Format::Bc7 => bcdec_rs::bc7(src, &mut block, 16),
                Format::Bc4 => {
                    bcdec_rs::bc4(src, &mut one, 4, false);
                    for i in 0..16 {
                        block[4 * i..4 * i + 4].copy_from_slice(&[one[i], one[i], one[i], 255]);
                    }
                }
                Format::Bc5 => {
                    bcdec_rs::bc5(src, &mut two, 8, false);
                    for i in 0..16 {
                        block[4 * i..4 * i + 4].copy_from_slice(&[two[2 * i], two[2 * i + 1], 0, 255]);
                    }
                }
                _ => unreachable!(),
            }
            for y in 0..4 {
                let py = by * 4 + y;
                if py >= h {
                    break;
                }
                let n = (w - bx * 4).min(4);
                let dst = (py * w + bx * 4) * 4;
                px[dst..dst + 4 * n].copy_from_slice(&block[16 * y..16 * y + 4 * n]);
            }
        }
    }
}
