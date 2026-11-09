// SPDX-License-Identifier: GPL-3.0-or-later
// From matenm/emf-enshrouded v0.6.0 src/content/src/portraits/raster.rs (MIT source).
// Copyright (c) 2026 EMF contributors; licenses/EMF-LICENSE.txt.
// Combined with the GPL-3.0-or-later EML archive reader only in this GPL component.
//! The portrait renderer: a small CPU rasteriser with no game I/O. It draws a textured model turning about its
//! vertical axis into square frames with a transparent background.
//!
//! Part of emf_content.dll, which as a whole is GPL-3.0-or-later (README.md).
//!
//! The model's space is the game's: left-handed, +Y up, the model's front facing +Z and its right side at +X (so
//! seen from the front its right side is on the viewer's left, as in the game). Every frame is an orthographic view
//! from a camera raised by `View::elevation_deg`; frame i shows the model turned by `start_yaw_deg + 360 * i /
//! frames` degrees about +Y (0: the front faces the viewer; a negative angle turns the front to the viewer's left).
//! The albedo is multiplied by the vertex colours when a mesh has them.
//! One scale and one centre hold for all frames, so the model neither grows nor wanders while it turns. The light
//! is fixed to the camera (from the upper left, in front), plus a sky and ground ambient.
//!
//! Samples are taken on a finer grid (2 to 4 per pixel side) and averaged in linear light; a frame's pixels are
//! sRGB with straight (not premultiplied) alpha.

/// The most frames one portrait may have (the window side's limit).
pub const MAX_FRAMES: u32 = 64;
/// The largest frame side in pixels (the window side's limit).
pub const MAX_SIZE: u32 = 512;
/// The smallest frame side in pixels.
pub const MIN_SIZE: u32 = 8;

/// An RGBA8 texture, sRGB-encoded, row 0 at the top (texture coordinate v = 0).
#[derive(Clone, Debug)]
pub struct Texture {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

/// How a mesh's surface is drawn.
#[derive(Clone, Debug, Default)]
pub struct Material {
    /// None: a plain light grey.
    pub texture: Option<Texture>,
    /// Alpha cut-out (fur cards, leaves): texels with alpha below 0.5 are holes. Without it the alpha is ignored.
    pub alpha_cut: bool,
    /// Both sides are drawn and lit. Without it, faces turned away from the camera are not drawn (only when the
    /// mesh has normals to tell which side is the front).
    pub double_sided: bool,
}

/// One mesh: triangles indexing its vertices, and the material they use.
#[derive(Clone, Debug, Default)]
pub struct Mesh {
    pub positions: Vec<[f32; 3]>,
    /// Per vertex, or empty: then each triangle is lit by its own face normal (and drawn from both sides).
    pub normals: Vec<[f32; 3]>,
    /// Per vertex, or empty: then the material's texture is not used.
    pub uvs: Vec<[f32; 2]>,
    /// Per vertex, or empty (white): a tint the albedo is multiplied by, linear 0 to 1.
    pub colors: Vec<[f32; 3]>,
    pub triangles: Vec<[u32; 3]>,
    /// Index into `Model::materials`.
    pub material: usize,
}

#[derive(Clone, Debug, Default)]
pub struct Model {
    pub meshes: Vec<Mesh>,
    pub materials: Vec<Material>,
}

/// What to draw: how many frames, their size, and the camera.
#[derive(Clone, Debug)]
pub struct View {
    pub frames: u32,
    /// The side of a square frame in pixels.
    pub size: u32,
    /// How far the camera is raised above the model's middle, in degrees.
    pub elevation_deg: f32,
    /// The model's turn in frame 0, in degrees.
    pub start_yaw_deg: f32,
}

impl View {
    /// `frames` frames of `size` x `size`: the camera 15 degrees up, frame 0 a three-quarter view with the
    /// model's front to the viewer's left.
    pub fn new(frames: u32, size: u32) -> Self {
        View { frames, size, elevation_deg: 15.0, start_yaw_deg: -40.0 }
    }
}

/// One drawn frame: `size` x `size` RGBA8, sRGB, straight alpha, row 0 at the top.
#[derive(Clone, Debug, PartialEq)]
pub struct Frame {
    pub size: u32,
    pub rgba: Vec<u8>,
}

impl Frame {
    /// The pixel at column x, row y (row 0 at the top).
    pub fn pixel(&self, x: u32, y: u32) -> [u8; 4] {
        let i = ((y * self.size + x) * 4) as usize;
        [self.rgba[i], self.rgba[i + 1], self.rgba[i + 2], self.rgba[i + 3]]
    }
}

/// Draws `view.frames` frames of `model` turning.
pub fn render_turntable(model: &Model, view: &View) -> Result<Vec<Frame>, String> {
    if view.frames == 0 || view.frames > MAX_FRAMES {
        return Err(format!("frames must be 1 to {MAX_FRAMES}, not {}", view.frames));
    }
    if view.size < MIN_SIZE || view.size > MAX_SIZE {
        return Err(format!("size must be {MIN_SIZE} to {MAX_SIZE}, not {}", view.size));
    }
    check_model(model)?;
    let surfaces: Vec<Surface> = model.materials.iter().map(Surface::new).collect();
    let plain = Surface::new(&Material::default());
    let pivot = pivot(model).ok_or("the model has no triangles")?;
    let turns: Vec<Turn> = (0..view.frames)
        .map(|i| Turn::new(view.start_yaw_deg + 360.0 * i as f32 / view.frames as f32, view.elevation_deg))
        .collect();
    let fit = Fit::new(model, pivot, &turns, view.size)?;
    let mut target = Target::new(view.size * fit.supersample);
    let mut frames = Vec::with_capacity(turns.len());
    for turn in &turns {
        target.clear();
        for mesh in &model.meshes {
            let surface = surfaces.get(mesh.material).unwrap_or(&plain);
            draw_mesh(&mut target, mesh, surface, pivot, turn, &fit);
        }
        frames.push(target.resolve(view.size, fit.supersample));
    }
    Ok(frames)
}

/// The frame as a PNG file's bytes: RGBA8 with straight alpha, marked sRGB.
pub fn encode_png(frame: &Frame) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    let mut encoder = png::Encoder::new(&mut bytes, frame.size, frame.size);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    encoder.set_source_srgb(png::SrgbRenderingIntent::Perceptual);
    let mut writer = encoder.write_header().map_err(|e| format!("PNG: {e}"))?;
    writer.write_image_data(&frame.rgba).map_err(|e| format!("PNG: {e}"))?;
    writer.finish().map_err(|e| format!("PNG: {e}"))?;
    Ok(bytes)
}

/// Every index in range and every per-vertex list as long as the positions: drawing never indexes out of bounds.
fn check_model(model: &Model) -> Result<(), String> {
    for (m, mesh) in model.meshes.iter().enumerate() {
        let n = mesh.positions.len();
        if !mesh.normals.is_empty() && mesh.normals.len() != n {
            return Err(format!("mesh {m}: {} normals for {n} vertices", mesh.normals.len()));
        }
        if !mesh.uvs.is_empty() && mesh.uvs.len() != n {
            return Err(format!("mesh {m}: {} UVs for {n} vertices", mesh.uvs.len()));
        }
        if !mesh.colors.is_empty() && mesh.colors.len() != n {
            return Err(format!("mesh {m}: {} vertex colours for {n} vertices", mesh.colors.len()));
        }
        if let Some(bad) = mesh.triangles.iter().flatten().find(|&&i| i as usize >= n) {
            return Err(format!("mesh {m}: vertex index {bad} out of range ({n} vertices)"));
        }
        let finite = |v: &[[f32; 3]]| v.iter().flatten().all(|c| c.is_finite());
        if !finite(&mesh.positions)
            || !finite(&mesh.normals)
            || !finite(&mesh.colors)
            || !mesh.uvs.iter().flatten().all(|c| c.is_finite())
        {
            return Err(format!("mesh {m}: a position, normal, UV or colour that is not a finite number"));
        }
    }
    Ok(())
}

/// The point the model turns about: the middle of its triangles' bounds on X and Z (its height stays).
fn pivot(model: &Model) -> Option<[f32; 3]> {
    let mut lo = [f32::INFINITY; 3];
    let mut hi = [f32::NEG_INFINITY; 3];
    for mesh in &model.meshes {
        for &i in mesh.triangles.iter().flatten() {
            let p = mesh.positions[i as usize];
            for k in 0..3 {
                lo[k] = lo[k].min(p[k]);
                hi[k] = hi[k].max(p[k]);
            }
        }
    }
    lo[0].is_finite().then(|| [(lo[0] + hi[0]) / 2.0, 0.0, (lo[2] + hi[2]) / 2.0])
}

/// One frame's rotation: the game's left-handed space mirrored in X into right-handed view space (x to the right,
/// y up, z towards the viewer), a turn about +Y, then the camera's tilt about +X.
struct Turn {
    cos_yaw: f32,
    sin_yaw: f32,
    cos_tilt: f32,
    sin_tilt: f32,
}

impl Turn {
    fn new(yaw_deg: f32, elevation_deg: f32) -> Self {
        let (sin_yaw, cos_yaw) = yaw_deg.to_radians().sin_cos();
        let (sin_tilt, cos_tilt) = elevation_deg.to_radians().sin_cos();
        Turn { cos_yaw, sin_yaw, cos_tilt, sin_tilt }
    }

    fn apply(&self, v: [f32; 3]) -> [f32; 3] {
        let mirrored = -v[0];
        let x = mirrored * self.cos_yaw + v[2] * self.sin_yaw;
        let z = -mirrored * self.sin_yaw + v[2] * self.cos_yaw;
        let y = v[1];
        [x, y * self.cos_tilt - z * self.sin_tilt, y * self.sin_tilt + z * self.cos_tilt]
    }
}

/// Where view space lands on the sample grid: one scale and one centre for every frame.
struct Fit {
    scale: f32,
    centre: [f32; 2],
    half: f32,
    supersample: u32,
}

/// Of the frame's side, this share stays clear on each edge.
const MARGIN: f32 = 0.04;

impl Fit {
    fn new(model: &Model, pivot: [f32; 3], turns: &[Turn], size: u32) -> Result<Self, String> {
        let mut used: Vec<Vec<bool>> = model.meshes.iter().map(|m| vec![false; m.positions.len()]).collect();
        for (mesh, used) in model.meshes.iter().zip(&mut used) {
            for &i in mesh.triangles.iter().flatten() {
                used[i as usize] = true;
            }
        }
        let (mut lo, mut hi) = ([f32::INFINITY; 2], [f32::NEG_INFINITY; 2]);
        for turn in turns {
            for (mesh, used) in model.meshes.iter().zip(&used) {
                for (p, _) in mesh.positions.iter().zip(used).filter(|(_, u)| **u) {
                    let v = turn.apply(sub(*p, pivot));
                    for k in 0..2 {
                        lo[k] = lo[k].min(v[k]);
                        hi[k] = hi[k].max(v[k]);
                    }
                }
            }
        }
        let extent = (hi[0] - lo[0]).max(hi[1] - lo[1]);
        if !(extent > 0.0 && extent.is_finite()) {
            return Err("the model has no extent: all its vertices are in one point".into());
        }
        // 2 to 4 samples per pixel side: a sample grid at least 768 wide, as long as that stays cheap.
        let supersample = 768u32.div_ceil(size).clamp(2, 4);
        let side = (size * supersample) as f32;
        Ok(Fit {
            scale: side * (1.0 - 2.0 * MARGIN) / extent,
            centre: [(lo[0] + hi[0]) / 2.0, (lo[1] + hi[1]) / 2.0],
            half: side / 2.0,
            supersample,
        })
    }

    /// View space to the sample grid: x to the right, y down from the top row; z kept (larger is nearer).
    fn place(&self, v: [f32; 3]) -> [f32; 3] {
        [(v[0] - self.centre[0]) * self.scale + self.half, self.half - (v[1] - self.centre[1]) * self.scale, v[2]]
    }
}

/// A material made ready to sample: its texture in linear light.
struct Surface {
    texels: Vec<[f32; 4]>,
    width: usize,
    height: usize,
    alpha_cut: bool,
    double_sided: bool,
}

/// The colour of a surface without a usable texture (linear light).
const PLAIN: [f32; 4] = [0.45, 0.45, 0.45, 1.0];

impl Surface {
    fn new(material: &Material) -> Self {
        let table = srgb_to_linear_table();
        let (texels, width, height) = match &material.texture {
            Some(t) if t.width > 0 && t.height > 0 && t.rgba.len() >= (t.width as usize * t.height as usize * 4) => {
                let texels = t.rgba[..t.width as usize * t.height as usize * 4]
                    .chunks_exact(4)
                    .map(|p| [table[p[0] as usize], table[p[1] as usize], table[p[2] as usize], p[3] as f32 / 255.0])
                    .collect();
                (texels, t.width as usize, t.height as usize)
            }
            _ => (vec![PLAIN], 1, 1),
        };
        Surface { texels, width, height, alpha_cut: material.alpha_cut, double_sided: material.double_sided }
    }

    /// Bilinear, wrapping at the edges.
    fn sample(&self, u: f32, v: f32) -> [f32; 4] {
        let x = u * self.width as f32 - 0.5;
        let y = v * self.height as f32 - 0.5;
        let (x0, y0) = (x.floor(), y.floor());
        let (fx, fy) = (x - x0, y - y0);
        let wrap = |i: f32, n: usize| (i as i64).rem_euclid(n as i64) as usize;
        let (xa, xb) = (wrap(x0, self.width), wrap(x0 + 1.0, self.width));
        let (ya, yb) = (wrap(y0, self.height), wrap(y0 + 1.0, self.height));
        let t = |x: usize, y: usize| self.texels[y * self.width + x];
        let (a, b, c, d) = (t(xa, ya), t(xb, ya), t(xa, yb), t(xb, yb));
        let mut out = [0.0; 4];
        for k in 0..4 {
            let top = a[k] + (b[k] - a[k]) * fx;
            let bottom = c[k] + (d[k] - c[k]) * fx;
            out[k] = top + (bottom - top) * fy;
        }
        out
    }
}

/// The sample grid of one frame: linear colour, depth (larger is nearer) and whether a surface covers the sample.
struct Target {
    side: u32,
    colour: Vec<[f32; 3]>,
    depth: Vec<f32>,
    covered: Vec<bool>,
}

impl Target {
    fn new(side: u32) -> Self {
        let n = (side * side) as usize;
        Target { side, colour: vec![[0.0; 3]; n], depth: vec![f32::NEG_INFINITY; n], covered: vec![false; n] }
    }

    fn clear(&mut self) {
        self.colour.fill([0.0; 3]);
        self.depth.fill(f32::NEG_INFINITY);
        self.covered.fill(false);
    }

    /// Averages each pixel's samples in linear light: alpha is the share of covered samples, the colour the mean
    /// of the covered ones (straight alpha), then sRGB.
    fn resolve(&self, size: u32, supersample: u32) -> Frame {
        let mut rgba = vec![0u8; (size * size * 4) as usize];
        let per_pixel = (supersample * supersample) as f32;
        for y in 0..size {
            for x in 0..size {
                let mut sum = [0.0f32; 3];
                let mut count = 0u32;
                for sy in 0..supersample {
                    let row = ((y * supersample + sy) * self.side) as usize;
                    for sx in 0..supersample {
                        let i = row + (x * supersample + sx) as usize;
                        if self.covered[i] {
                            count += 1;
                            for k in 0..3 {
                                sum[k] += self.colour[i][k];
                            }
                        }
                    }
                }
                if count == 0 {
                    continue;
                }
                let o = ((y * size + x) * 4) as usize;
                for k in 0..3 {
                    rgba[o + k] = linear_to_srgb8(sum[k] / count as f32);
                }
                rgba[o + 3] = (count as f32 / per_pixel * 255.0).round() as u8;
            }
        }
        Frame { size, rgba }
    }
}

/// The light, fixed to the camera: from the upper left, in front (view space, the direction towards the light).
const LIGHT: [f32; 3] = [-0.45, 0.65, 0.61];
const DIFFUSE: f32 = 1.0;
/// The ambient light on a normal pointing down, and on one pointing up.
const AMBIENT_GROUND: f32 = 0.28;
const AMBIENT_SKY: f32 = 0.5;

fn draw_mesh(target: &mut Target, mesh: &Mesh, surface: &Surface, pivot: [f32; 3], turn: &Turn, fit: &Fit) {
    let view: Vec<[f32; 3]> = mesh.positions.iter().map(|&p| turn.apply(sub(p, pivot))).collect();
    let normals: Vec<[f32; 3]> = mesh.normals.iter().map(|&n| turn.apply(n)).collect();
    let screen: Vec<[f32; 3]> = view.iter().map(|&v| fit.place(v)).collect();
    let light = normalize(LIGHT);
    let has_normals = !normals.is_empty();
    let textured = !mesh.uvs.is_empty();
    let tinted = !mesh.colors.is_empty();
    for tri in &mesh.triangles {
        let [a, b, c] = tri.map(|i| i as usize);
        // The face's normal in view space, turned to the side its vertex normals point to (the outside).
        let mut face = cross(sub(view[b], view[a]), sub(view[c], view[a]));
        if length(face) == 0.0 {
            continue;
        }
        if has_normals && dot(face, add(add(normals[a], normals[b]), normals[c])) < 0.0 {
            face = scale(face, -1.0);
        }
        // Seeing the back: not drawn for a single-sided surface, lit from the other side for a double-sided one.
        // Without vertex normals there is no outside: both sides are drawn, lit from the side that is seen.
        let back = face[2] < 0.0;
        if has_normals && back && !surface.double_sided {
            continue;
        }
        let flip = if back { -1.0 } else { 1.0 };
        let face_normal = normalize(scale(face, flip));
        let (pa, pb, pc) = (screen[a], screen[b], screen[c]);
        let area = edge(pa, pb, pc);
        if area == 0.0 || !area.is_finite() {
            continue;
        }
        let side = target.side as f32;
        let x0 = pa[0].min(pb[0]).min(pc[0]).floor().clamp(0.0, side) as u32;
        let x1 = pa[0].max(pb[0]).max(pc[0]).ceil().clamp(0.0, side) as u32;
        let y0 = pa[1].min(pb[1]).min(pc[1]).floor().clamp(0.0, side) as u32;
        let y1 = pa[1].max(pb[1]).max(pc[1]).ceil().clamp(0.0, side) as u32;
        for y in y0..y1 {
            let py = y as f32 + 0.5;
            for x in x0..x1 {
                let p = [x as f32 + 0.5, py, 0.0];
                let wa = edge(pb, pc, p) / area;
                let wb = edge(pc, pa, p) / area;
                let wc = 1.0 - wa - wb;
                if wa < 0.0 || wb < 0.0 || wc < 0.0 {
                    continue;
                }
                let i = (y * target.side + x) as usize;
                let z = wa * pa[2] + wb * pb[2] + wc * pc[2];
                if z <= target.depth[i] {
                    continue;
                }
                let albedo = if textured {
                    let (ua, ub, uc) = (mesh.uvs[a], mesh.uvs[b], mesh.uvs[c]);
                    surface.sample(wa * ua[0] + wb * ub[0] + wc * uc[0], wa * ua[1] + wb * ub[1] + wc * uc[1])
                } else {
                    surface.texels[0]
                };
                if surface.alpha_cut && albedo[3] < 0.5 {
                    continue;
                }
                let n = if has_normals {
                    let n = add(add(scale(normals[a], wa), scale(normals[b], wb)), scale(normals[c], wc));
                    if length(n) > 0.0 { scale(normalize(n), flip) } else { face_normal }
                } else {
                    face_normal
                };
                let ambient = AMBIENT_GROUND + (AMBIENT_SKY - AMBIENT_GROUND) * (n[1] * 0.5 + 0.5);
                let shade = ambient + DIFFUSE * dot(n, light).max(0.0);
                let tint = if tinted {
                    add(add(scale(mesh.colors[a], wa), scale(mesh.colors[b], wb)), scale(mesh.colors[c], wc))
                } else {
                    [1.0; 3]
                };
                target.depth[i] = z;
                target.covered[i] = true;
                target.colour[i] = [0, 1, 2].map(|k| albedo[k] * tint[k] * shade);
            }
        }
    }
}

/// Twice the signed area of the triangle (a, b, p) on the sample grid.
fn edge(a: [f32; 3], b: [f32; 3], p: [f32; 3]) -> f32 {
    (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
}

fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

fn add(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

fn scale(a: [f32; 3], s: f32) -> [f32; 3] {
    [a[0] * s, a[1] * s, a[2] * s]
}

fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
}

fn length(a: [f32; 3]) -> f32 {
    dot(a, a).sqrt()
}

fn normalize(a: [f32; 3]) -> [f32; 3] {
    let l = length(a);
    if l > 0.0 { scale(a, 1.0 / l) } else { a }
}

fn srgb_to_linear(c: f32) -> f32 {
    if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
}

fn srgb_to_linear_table() -> [f32; 256] {
    std::array::from_fn(|i| srgb_to_linear(i as f32 / 255.0))
}

fn linear_to_srgb8(c: f32) -> u8 {
    let c = c.clamp(0.0, 1.0);
    let s = if c <= 0.003_130_8 { c * 12.92 } else { 1.055 * c.powf(1.0 / 2.4) - 0.055 };
    (s * 255.0).round() as u8
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    pub(crate) fn solid(width: u32, height: u32, rgba: [u8; 4]) -> Texture {
        Texture { width, height, rgba: rgba.iter().copied().cycle().take((width * height * 4) as usize).collect() }
    }

    /// A unit quad in the XY plane at z, facing +Z (towards the viewer at yaw 0). Seen from the front, +X is on
    /// the viewer's left (the game's space is left-handed), so u grows towards -X: UV (0, 0) is at the viewer's top
    /// left.
    fn quad(z: f32, material: usize) -> Mesh {
        Mesh {
            positions: vec![[-0.5, -0.5, z], [0.5, -0.5, z], [0.5, 0.5, z], [-0.5, 0.5, z]],
            normals: vec![[0.0, 0.0, 1.0]; 4],
            uvs: vec![[1.0, 1.0], [0.0, 1.0], [0.0, 0.0], [1.0, 0.0]],
            colors: Vec::new(),
            triangles: vec![[0, 1, 2], [0, 2, 3]],
            material,
        }
    }

    /// A cube from -h to h on every axis, each face its own 4 vertices with outward normals.
    pub(crate) fn cube(h: f32, material: usize) -> Mesh {
        let mut mesh = Mesh { material, ..Default::default() };
        let faces: [([f32; 3], [f32; 3], [f32; 3]); 6] = [
            ([0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]),
            ([0.0, 0.0, -1.0], [-1.0, 0.0, 0.0], [0.0, 1.0, 0.0]),
            ([1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]),
            ([-1.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, 1.0, 0.0]),
            ([0.0, 1.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, -1.0]),
            ([0.0, -1.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 1.0]),
        ];
        for (n, u, v) in faces {
            let base = mesh.positions.len() as u32;
            for (su, sv) in [(-1.0f32, -1.0f32), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0)] {
                mesh.positions.push([0, 1, 2].map(|k| h * (n[k] + su * u[k] + sv * v[k])));
                mesh.normals.push(n);
                mesh.uvs.push([(su + 1.0) / 2.0, (1.0 - sv) / 2.0]);
            }
            mesh.triangles.push([base, base + 1, base + 2]);
            mesh.triangles.push([base, base + 2, base + 3]);
        }
        mesh
    }

    fn front_view(frames: u32, size: u32) -> View {
        View { frames, size, elevation_deg: 0.0, start_yaw_deg: 0.0 }
    }

    fn one_material(rgba: [u8; 4], alpha_cut: bool) -> Vec<Material> {
        vec![Material { texture: Some(solid(4, 4, rgba)), alpha_cut, double_sided: false }]
    }

    fn coverage(frame: &Frame) -> f32 {
        let opaque = frame.rgba.chunks(4).filter(|p| p[3] > 0).count();
        opaque as f32 / (frame.size * frame.size) as f32
    }

    #[test]
    fn frame_count_and_size() {
        let model = Model { meshes: vec![cube(1.0, 0)], materials: one_material([200, 60, 40, 255], false) };
        for (frames, size) in [(1, 8), (3, 64), (24, 96)] {
            let out = render_turntable(&model, &View::new(frames, size)).unwrap();
            assert_eq!(out.len(), frames as usize);
            for f in &out {
                assert_eq!(f.size, size);
                assert_eq!(f.rgba.len(), (size * size * 4) as usize);
            }
        }
    }

    #[test]
    fn limits_are_refused() {
        let model = Model { meshes: vec![cube(1.0, 0)], materials: one_material([200, 60, 40, 255], false) };
        for (frames, size) in [(0, 64), (MAX_FRAMES + 1, 64), (4, MIN_SIZE - 1), (4, MAX_SIZE + 1)] {
            let error = render_turntable(&model, &View::new(frames, size)).unwrap_err();
            assert!(error.contains("frames") || error.contains("size"), "{frames}x{size}: {error}");
        }
        assert!(render_turntable(&model, &View::new(MAX_FRAMES, 16)).is_ok());
    }

    #[test]
    fn an_empty_model_is_refused() {
        let error = render_turntable(&Model::default(), &View::new(4, 32)).unwrap_err();
        assert!(error.contains("triangle"), "{error}");
        let flat = Mesh { positions: vec![[0.0; 3]; 3], triangles: vec![[0, 1, 2]], ..Default::default() };
        let error = render_turntable(&Model { meshes: vec![flat], materials: vec![] }, &View::new(4, 32)).unwrap_err();
        assert!(error.contains("extent"), "{error}");
    }

    #[test]
    fn bad_indices_are_refused_not_a_panic() {
        let mut mesh = quad(0.0, 0);
        mesh.triangles.push([0, 1, 99]);
        let error = render_turntable(&Model { meshes: vec![mesh], materials: vec![] }, &View::new(2, 32)).unwrap_err();
        assert!(error.contains("99"), "{error}");
        let mut mesh = quad(0.0, 3);
        mesh.uvs.pop();
        let error = render_turntable(&Model { meshes: vec![mesh], materials: vec![] }, &View::new(2, 32)).unwrap_err();
        assert!(error.contains("uv") || error.contains("UV"), "{error}");
    }

    #[test]
    fn the_cube_covers_the_middle_and_leaves_the_corners_clear() {
        let model = Model { meshes: vec![cube(1.0, 0)], materials: one_material([200, 60, 40, 255], false) };
        for frame in render_turntable(&model, &View::new(8, 64)).unwrap() {
            assert_eq!(frame.pixel(32, 32)[3], 255, "the middle is opaque");
            for (x, y) in [(0, 0), (63, 0), (0, 63), (63, 63)] {
                assert_eq!(frame.pixel(x, y), [0, 0, 0, 0], "the corner ({x}, {y}) is clear");
            }
            let c = coverage(&frame);
            assert!(c > 0.3 && c < 0.95, "coverage {c}");
        }
    }

    #[test]
    fn the_model_fills_the_frame_at_any_scale() {
        for scale in [0.01f32, 1.0, 250.0] {
            let model = Model { meshes: vec![cube(scale, 0)], materials: one_material([90, 90, 90, 255], false) };
            let frame = &render_turntable(&model, &front_view(1, 64)).unwrap()[0];
            let c = coverage(frame);
            // The front view of a cube is a square: framed with a small margin it covers most of the frame.
            assert!(c > 0.75 && c < 0.97, "scale {scale}: coverage {c}");
        }
    }

    #[test]
    fn the_texture_shows() {
        let model = Model { meshes: vec![quad(0.0, 0)], materials: one_material([220, 30, 30, 255], false) };
        let frame = &render_turntable(&model, &front_view(1, 32)).unwrap()[0];
        let [r, g, b, a] = frame.pixel(16, 16);
        assert_eq!(a, 255);
        assert!(r > 120 && g < 60 && b < 60, "red, lit: {r} {g} {b}");
    }

    #[test]
    fn the_right_way_up_and_uvs_from_the_top() {
        // The texture's top half is red, its bottom half blue; v = 0 is the texture's top, and the quad's top
        // (+Y) has v = 0: red must be at the top of the frame.
        let mut rgba = Vec::new();
        for y in 0..8 {
            for _ in 0..8 {
                rgba.extend_from_slice(if y < 4 { &[230, 20, 20, 255] } else { &[20, 20, 230, 255] });
            }
        }
        let materials = vec![Material { texture: Some(Texture { width: 8, height: 8, rgba }), ..Default::default() }];
        let model = Model { meshes: vec![quad(0.0, 0)], materials };
        let frame = &render_turntable(&model, &front_view(1, 32)).unwrap()[0];
        let top = frame.pixel(16, 8);
        let bottom = frame.pixel(16, 24);
        assert!(top[0] > top[2], "red at the top: {top:?}");
        assert!(bottom[2] > bottom[0], "blue at the bottom: {bottom:?}");
    }

    #[test]
    fn frame_zero_at_yaw_zero_shows_the_front() {
        // Two quads: green at +Z (the front), red at -Z (the back), both facing outwards.
        let front = quad(0.5, 0);
        let mut back = quad(-0.5, 1);
        back.normals = vec![[0.0, 0.0, -1.0]; 4];
        back.triangles = vec![[0, 2, 1], [0, 3, 2]];
        let materials = vec![
            Material { texture: Some(solid(2, 2, [20, 220, 20, 255])), ..Default::default() },
            Material { texture: Some(solid(2, 2, [220, 20, 20, 255])), ..Default::default() },
        ];
        let model = Model { meshes: vec![back, front], materials };
        let frames = render_turntable(&model, &front_view(2, 32)).unwrap();
        let p = frames[0].pixel(16, 16);
        assert!(p[1] > p[0], "frame 0 shows the green front: {p:?}");
        let p = frames[1].pixel(16, 16);
        assert!(p[0] > p[1], "frame 1 (turned 180 degrees) shows the red back: {p:?}");
    }

    #[test]
    fn a_negative_start_yaw_turns_the_front_to_the_left() {
        // A cube with a small marker block on its front: in a three-quarter view the marker is left of the middle.
        let mut marker = cube(0.2, 1);
        for p in &mut marker.positions {
            p[2] += 1.2;
        }
        let materials = vec![
            Material { texture: Some(solid(2, 2, [200, 200, 200, 255])), ..Default::default() },
            Material { texture: Some(solid(2, 2, [20, 20, 230, 255])), ..Default::default() },
        ];
        let model = Model { meshes: vec![cube(1.0, 0), marker], materials };
        let view = View { frames: 1, size: 64, elevation_deg: 0.0, start_yaw_deg: -40.0 };
        let frame = &render_turntable(&model, &view).unwrap()[0];
        let blue: Vec<u32> = (0..64)
            .flat_map(|y| (0..64).map(move |x| (x, y)))
            .filter(|&(x, y)| {
                let p = frame.pixel(x, y);
                p[3] == 255 && p[2] as u32 > 2 * (p[0] as u32).max(1)
            })
            .map(|(x, _)| x)
            .collect();
        assert!(!blue.is_empty(), "the marker shows");
        let mean = blue.iter().sum::<u32>() as f32 / blue.len() as f32;
        assert!(mean < 28.0, "the marker is left of the middle: mean x {mean}");
    }

    /// The cube with a blue marker block on the model's right side (+X in the game's left-handed space).
    fn cube_with_right_marker() -> Model {
        let mut marker = cube(0.2, 1);
        for p in &mut marker.positions {
            p[0] += 1.2;
        }
        let materials = vec![
            Material { texture: Some(solid(2, 2, [200, 200, 200, 255])), ..Default::default() },
            Material { texture: Some(solid(2, 2, [20, 20, 230, 255])), ..Default::default() },
        ];
        Model { meshes: vec![cube(1.0, 0), marker], materials }
    }

    fn mean_blue_x(frame: &Frame) -> Option<f32> {
        let size = frame.size;
        let xs: Vec<u32> = (0..size)
            .flat_map(|y| (0..size).map(move |x| (x, y)))
            .filter(|&(x, y)| {
                let p = frame.pixel(x, y);
                p[3] == 255 && p[2] as u32 > 2 * (p[0] as u32).max(1)
            })
            .map(|(x, _)| x)
            .collect();
        (!xs.is_empty()).then(|| xs.iter().sum::<u32>() as f32 / xs.len() as f32)
    }

    #[test]
    fn the_models_right_is_on_the_viewers_left_from_the_front() {
        // The game's space is left-handed: facing the model's front, its right side (+X) is on the viewer's left.
        let frame = &render_turntable(&cube_with_right_marker(), &front_view(1, 64)).unwrap()[0];
        let x = mean_blue_x(frame).expect("the marker shows");
        assert!(x < 24.0, "the marker is on the left: mean x {x}");
    }

    #[test]
    fn vertex_colours_tint_the_albedo() {
        let white = vec![Material { texture: Some(solid(2, 2, [255, 255, 255, 255])), ..Default::default() }];
        let mut tinted = quad(0.0, 0);
        tinted.colors = vec![[1.0, 0.0, 0.0]; 4];
        let frame = &render_turntable(&Model { meshes: vec![tinted], materials: white.clone() }, &front_view(1, 32))
            .unwrap()[0];
        let [r, g, b, _] = frame.pixel(16, 16);
        assert!(r > 150 && g == 0 && b == 0, "red: {r} {g} {b}");
        let plain = &render_turntable(&Model { meshes: vec![quad(0.0, 0)], materials: white }, &front_view(1, 32))
            .unwrap()[0];
        let [r, g, b, _] = plain.pixel(16, 16);
        assert!(r > 150 && r == g && g == b, "no colours: white, lit: {r} {g} {b}");
        let mut uneven = quad(0.0, 0);
        uneven.colors = vec![[1.0; 3]; 3];
        let error = render_turntable(&Model { meshes: vec![uneven], materials: vec![] }, &front_view(1, 32)).unwrap_err();
        assert!(error.contains("colours"), "{error}");
    }

    #[test]
    fn the_nearer_surface_wins_whatever_the_order() {
        let materials = vec![
            Material { texture: Some(solid(2, 2, [20, 220, 20, 255])), ..Default::default() },
            Material { texture: Some(solid(2, 2, [220, 20, 20, 255])), ..Default::default() },
        ];
        for order in [[0usize, 1], [1, 0]] {
            let meshes = [quad(0.3, 0), quad(-0.3, 1)];
            let model = Model { meshes: order.iter().map(|&i| meshes[i].clone()).collect(), materials: materials.clone() };
            let frame = &render_turntable(&model, &front_view(1, 32)).unwrap()[0];
            let p = frame.pixel(16, 16);
            assert!(p[1] > p[0], "order {order:?}: the near green quad: {p:?}");
        }
    }

    #[test]
    fn alpha_cut_makes_holes_and_is_ignored_without_it() {
        // The texture's left half is clear (alpha 0).
        let mut rgba = Vec::new();
        for _ in 0..8 {
            for x in 0..8 {
                rgba.extend_from_slice(if x < 4 { &[200, 200, 200, 0] } else { &[200, 200, 200, 255] });
            }
        }
        let texture = Texture { width: 8, height: 8, rgba };
        for alpha_cut in [true, false] {
            let materials = vec![Material { texture: Some(texture.clone()), alpha_cut, double_sided: true }];
            let model = Model { meshes: vec![quad(0.0, 0)], materials };
            let frame = &render_turntable(&model, &front_view(1, 32)).unwrap()[0];
            let left = frame.pixel(9, 16)[3];
            let right = frame.pixel(23, 16)[3];
            assert_eq!(right, 255, "alpha_cut {alpha_cut}");
            assert_eq!(left, if alpha_cut { 0 } else { 255 }, "alpha_cut {alpha_cut}");
        }
    }

    #[test]
    fn single_sided_back_faces_are_not_drawn_double_sided_ones_are() {
        // The quad faces +Z; turned by 180 degrees its back is towards the camera.
        for double_sided in [false, true] {
            let materials = vec![Material { texture: Some(solid(2, 2, [200, 200, 200, 255])), alpha_cut: false, double_sided }];
            let model = Model { meshes: vec![quad(0.0, 0)], materials };
            let view = View { frames: 1, size: 32, elevation_deg: 0.0, start_yaw_deg: 180.0 };
            let frame = &render_turntable(&model, &view).unwrap()[0];
            let a = frame.pixel(16, 16)[3];
            assert_eq!(a, if double_sided { 255 } else { 0 }, "double_sided {double_sided}");
        }
    }

    #[test]
    fn edges_are_smoothed_with_straight_alpha() {
        let model = Model { meshes: vec![cube(1.0, 0)], materials: one_material([200, 60, 40, 255], false) };
        let frame = &render_turntable(&model, &View::new(1, 64)).unwrap()[0];
        let partial: Vec<[u8; 4]> =
            frame.rgba.chunks(4).filter(|p| p[3] > 0 && p[3] < 255).map(|p| [p[0], p[1], p[2], p[3]]).collect();
        assert!(!partial.is_empty(), "some edge pixels are partly covered");
        // Straight alpha: a partly covered edge pixel keeps the surface's colour, it is not darkened towards black.
        let reddish = partial.iter().filter(|p| p[0] as u32 > 2 * p[2] as u32 && p[0] > 40).count();
        assert!(reddish * 10 >= partial.len() * 9, "{reddish} of {}", partial.len());
    }

    #[test]
    fn the_output_is_deterministic() {
        let model = Model { meshes: vec![cube(1.0, 0), quad(1.2, 0)], materials: one_material([120, 160, 60, 255], false) };
        let a = render_turntable(&model, &View::new(6, 48)).unwrap();
        let b = render_turntable(&model, &View::new(6, 48)).unwrap();
        assert_eq!(a, b);
        let pa: Vec<Vec<u8>> = a.iter().map(|f| encode_png(f).unwrap()).collect();
        let pb: Vec<Vec<u8>> = b.iter().map(|f| encode_png(f).unwrap()).collect();
        assert_eq!(pa, pb);
        assert_ne!(a[0], a[1], "the frames differ: the model turns");
    }

    #[test]
    fn png_is_rgba8_srgb() {
        let model = Model { meshes: vec![cube(1.0, 0)], materials: one_material([200, 60, 40, 255], false) };
        let frame = &render_turntable(&model, &View::new(1, 24)).unwrap()[0];
        let bytes = encode_png(frame).unwrap();
        assert_eq!(&bytes[..8], b"\x89PNG\r\n\x1a\n");
        let decoder = png::Decoder::new(std::io::Cursor::new(&bytes));
        let mut reader = decoder.read_info().unwrap();
        let info = reader.info();
        assert_eq!((info.width, info.height), (24, 24));
        assert_eq!(info.color_type, png::ColorType::Rgba);
        assert_eq!(info.bit_depth, png::BitDepth::Eight);
        assert!(info.srgb.is_some(), "an sRGB chunk");
        let mut pixels = vec![0u8; reader.output_buffer_size()];
        reader.next_frame(&mut pixels).unwrap();
        assert_eq!(pixels, frame.rgba);
    }
}
