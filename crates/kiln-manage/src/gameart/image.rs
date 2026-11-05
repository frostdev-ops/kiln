//! RGBA8 images: a premultiplied-alpha resampler (separable bicubic, a = -0.5, widened for downscaling like Pillow's),
//! flat-colour tints, and lossless WebP through `image-webp` (MIT OR Apache-2.0).

use crate::{Error, ErrorCode, Result};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Rgba {
    pub w: u32,
    pub h: u32,
    pub px: Vec<u8>,
}

impl Rgba {
    pub fn new(w: u32, h: u32) -> Rgba {
        Rgba { w, h, px: vec![0; w as usize * h as usize * 4] }
    }
    pub fn opaque(&self) -> bool {
        self.px.chunks_exact(4).all(|p| p[3] == 255)
    }
}

/// The largest size that fits in `max_w` x `max_h` keeping the aspect ratio, never upscaled (Pillow's `thumbnail`).
pub fn fit_within(w: u32, h: u32, max_w: u32, max_h: u32) -> (u32, u32) {
    if w <= max_w && h <= max_h {
        return (w, h);
    }
    let s = (max_w as f64 / w as f64).min(max_h as f64 / h as f64);
    (((w as f64 * s).round() as u32).clamp(1, max_w), ((h as f64 * s).round() as u32).clamp(1, max_h))
}

fn cubic(x: f32) -> f32 {
    const A: f32 = -0.5;
    let x = x.abs();
    if x < 1.0 {
        ((A + 2.0) * x - (A + 3.0)) * x * x + 1.0
    } else if x < 2.0 {
        (((x - 5.0) * x + 8.0) * x - 4.0) * A
    } else {
        0.0
    }
}

/// For each output index: (first input index, weights).
fn weights(input: usize, output: usize) -> Vec<(usize, Vec<f32>)> {
    let scale = input as f32 / output as f32;
    let fs = scale.max(1.0);
    let support = 2.0 * fs;
    (0..output)
        .map(|i| {
            let center = (i as f32 + 0.5) * scale;
            let lo = ((center - support).floor().max(0.0)) as usize;
            let hi = ((center + support).ceil() as usize).min(input);
            let mut w: Vec<f32> = (lo..hi).map(|j| cubic((j as f32 + 0.5 - center) / fs)).collect();
            let sum: f32 = w.iter().sum();
            if sum.abs() > 1e-6 {
                w.iter_mut().for_each(|x| *x /= sum);
            }
            (lo, w)
        })
        .collect()
}

/// Resizes with premultiplied alpha (no dark fringes around transparent edges).
pub fn resize(src: &Rgba, w: u32, h: u32) -> Rgba {
    if src.w == w && src.h == h {
        return src.clone();
    }
    let (sw, sh, dw, dh) = (src.w as usize, src.h as usize, w as usize, h as usize);
    let pre: Vec<f32> = src
        .px
        .chunks_exact(4)
        .flat_map(|p| {
            let a = p[3] as f32 / 255.0;
            [p[0] as f32 * a, p[1] as f32 * a, p[2] as f32 * a, p[3] as f32]
        })
        .collect();
    let wx = weights(sw, dw);
    let mut tmp = vec![0f32; dw * sh * 4];
    for y in 0..sh {
        for (x, (lo, ws)) in wx.iter().enumerate() {
            let mut acc = [0f32; 4];
            for (k, &wt) in ws.iter().enumerate() {
                let s = (y * sw + lo + k) * 4;
                for c in 0..4 {
                    acc[c] += pre[s + c] * wt;
                }
            }
            tmp[(y * dw + x) * 4..][..4].copy_from_slice(&acc);
        }
    }
    let wy = weights(sh, dh);
    let mut out = Rgba::new(w, h);
    for (y, (lo, ws)) in wy.iter().enumerate() {
        for x in 0..dw {
            let mut acc = [0f32; 4];
            for (k, &wt) in ws.iter().enumerate() {
                let s = ((lo + k) * dw + x) * 4;
                for c in 0..4 {
                    acc[c] += tmp[s + c] * wt;
                }
            }
            let a = acc[3].clamp(0.0, 255.0);
            let o = &mut out.px[(y * dw + x) * 4..][..4];
            if a < 0.5 {
                o.copy_from_slice(&[0, 0, 0, 0]);
                continue;
            }
            // un-premultiply with the unclamped alpha: the cubic's overshoot rings colour and alpha alike
            let k = 255.0 / acc[3];
            for c in 0..3 {
                o[c] = (acc[c] * k).round().clamp(0.0, 255.0) as u8;
            }
            o[3] = a.round() as u8;
        }
    }
    out
}

/// A flat colour through the source's alpha: rgb = `rgb`, alpha = source alpha x `alpha` (how the game tints its white
/// UI masks in the shader).
pub fn tint(src: &Rgba, rgb: [u8; 3], alpha: f32) -> Rgba {
    let mut out = Rgba::new(src.w, src.h);
    for (o, p) in out.px.chunks_exact_mut(4).zip(src.px.chunks_exact(4)) {
        let a = (p[3] as f32 * alpha).round().clamp(0.0, 255.0) as u8;
        if a > 0 {
            o.copy_from_slice(&[rgb[0], rgb[1], rgb[2], a]);
        }
    }
    out
}

/// Lossless WebP. Fully transparent pixels get black RGB (smaller files, same picture); opaque images drop alpha.
pub fn encode_webp(img: &Rgba) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    let enc = image_webp::WebPEncoder::new(&mut out);
    let r = if img.opaque() {
        let rgb: Vec<u8> = img.px.chunks_exact(4).flat_map(|p| [p[0], p[1], p[2]]).collect();
        enc.encode(&rgb, img.w, img.h, image_webp::ColorType::Rgb8)
    } else {
        let mut px = img.px.clone();
        for p in px.chunks_exact_mut(4) {
            if p[3] == 0 {
                p[..3].fill(0);
            }
        }
        enc.encode(&px, img.w, img.h, image_webp::ColorType::Rgba8)
    };
    r.map_err(|e| Error::new(ErrorCode::Internal, format!("webp: {e}")))?;
    Ok(out)
}

/// Decodes a WebP (lossless or lossy) into RGBA8 (tests, comparisons).
pub fn decode_webp(data: &[u8]) -> Result<Rgba> {
    let mut d = image_webp::WebPDecoder::new(std::io::Cursor::new(data))
        .map_err(|e| Error::new(ErrorCode::Parse, format!("webp: {e}")))?;
    let (w, h) = d.dimensions();
    let alpha = d.has_alpha();
    let mut buf = vec![0u8; d.output_buffer_size().ok_or_else(|| Error::new(ErrorCode::Parse, "webp: too large"))?];
    d.read_image(&mut buf).map_err(|e| Error::new(ErrorCode::Parse, format!("webp: {e}")))?;
    let px = if alpha { buf } else { buf.chunks_exact(3).flat_map(|p| [p[0], p[1], p[2], 255]).collect() };
    Ok(Rgba { w, h, px })
}
