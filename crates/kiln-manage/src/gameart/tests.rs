//! Tests on synthetic fixtures only (no game data in the repository): a KFC4 directory + zstd resource
//! stream + `.dat` containers, a PE32+ with reflection records, the codecs, and a whole extraction into a temp folder.
//! `real_client` (ignored) runs the extractor on an installed client:
//! `KILN_GAMEART_GAME=<client folder> KILN_GAMEART_OUT=<scratch dir> cargo test -p kiln-manage --features gameart
//! --release --lib -- --ignored real_client --nocapture`.

use super::image::{self, Rgba};
use super::kfc::{ContentHash, Kfc, fnv1a32};
use super::texture::{self, Format};
use super::*;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};

// ---------------------------------------------------------------- fixtures

struct Tmp(PathBuf);
impl Tmp {
    fn new(tag: &str) -> Tmp {
        static N: AtomicUsize = AtomicUsize::new(0);
        let p = std::env::temp_dir().join(format!("kiln-gameart-{tag}-{}-{}", std::process::id(), N.fetch_add(1, Ordering::Relaxed)));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        Tmp(p)
    }
}
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

struct Res {
    guid: [u8; 16],
    type_hash: u32,
    part: u32,
    data: Vec<u8>,
}

fn guid(n: u8) -> [u8; 16] {
    let mut g = [0u8; 16];
    g[0] = n;
    g[15] = 0xA5;
    g
}

/// Writes `<dir>/<stem>.kfc`, `.kfc_resources`, `<stem>_000.dat`, `_001.dat` (layout in kfc.rs; same builder as
/// tools/kfc/tests). `layouts`: bundle layout hash per type. Small chunks so resources span chunk borders.
fn build_kfc(dir: &Path, stem: &str, version: &str, res: &[Res], contents: &[(ContentHash, Vec<u8>)], layouts: &[(u32, u32)]) {
    let chunk_size = 64;
    let mut stream = Vec::new();
    let mut rvals = Vec::new();
    for r in res {
        while stream.len() % 16 != 0 {
            stream.push(0);
        }
        rvals.push((stream.len() as u32, r.data.len() as u32));
        stream.extend_from_slice(&r.data);
    }
    let mut packed = Vec::new();
    let mut chunks = Vec::new();
    for off in (0..stream.len()).step_by(chunk_size) {
        let raw = &stream[off..(off + chunk_size).min(stream.len())];
        let z = zstd::bulk::compress(raw, 3).unwrap();
        let foff = packed.len();
        packed.extend_from_slice(&z);
        let stored = z.len().div_ceil(4096) * 4096;
        packed.resize(foff + stored, 0);
        chunks.push([foff as u32, stored as u32, z.len() as u32, off as u32, raw.len() as u32]);
    }
    std::fs::write(dir.join(format!("{stem}.kfc_resources")), &packed).unwrap();
    let mut dats = [Vec::new(), Vec::new()];
    let mut cvals = Vec::new();
    for (i, (_, c)) in contents.iter().enumerate() {
        let dat = &mut dats[i % 2];
        let off = dat.len();
        dat.extend_from_slice(c);
        dat.resize(dat.len().div_ceil(4096) * 4096, 0);
        cvals.push((off as u32, (i % 2) as u16));
    }
    for (i, d) in dats.iter().enumerate() {
        std::fs::write(dir.join(format!("{stem}_{i:03}.dat")), d).unwrap();
    }
    let mut types: Vec<u32> = res.iter().map(|r| r.type_hash).collect();
    types.sort();
    types.dedup();
    let mut order = Vec::new();
    let mut bundles = Vec::new();
    for th in &types {
        let first = order.len();
        order.extend(res.iter().enumerate().filter(|(_, r)| r.type_hash == *th).map(|(i, _)| i as u32));
        let layout = layouts.iter().find(|(t, _)| t == th).map_or(0xABC0_0000 | (th & 0xFFFF), |x| x.1);
        bundles.push((*th, layout, first as u32, (order.len() - first) as u32));
    }
    let le = |v: &[u32]| v.iter().flat_map(|x| x.to_le_bytes()).collect::<Vec<u8>>();
    let tables: Vec<(Vec<u8>, usize)> = vec![
        (version.as_bytes().to_vec(), version.len()),
        (dats.iter().flat_map(|d| [(d.len() as u64).to_le_bytes(), 1u64.to_le_bytes()].concat()).collect(), 2),
        (vec![], 0),
        (vec![], 0),
        (le(&[stream.len() as u32, packed.len() as u32, res.len() as u32, 0, chunks.len() as u32]), 1),
        (le(&order), order.len()),
        (le(&[0, contents.len() as u32]), 1),
        (contents.iter().flat_map(|(h, _)| h.to_bytes()).collect(), contents.len()),
        (cvals.iter().flat_map(|(o, c)| [o.to_le_bytes().to_vec(), 0u16.to_le_bytes().to_vec(), c.to_le_bytes().to_vec(), vec![0; 8]].concat()).collect(), contents.len()),
        (le(&[0, res.len() as u32]), 1),
        (res.iter().flat_map(|r| [r.guid.to_vec(), le(&[r.type_hash, r.part, 0, 0])].concat()).collect(), res.len()),
        (rvals.iter().flat_map(|(o, s)| le(&[*o, *s])).collect(), res.len()),
        (le(&[0, bundles.len() as u32]), 1),
        (le(&bundles.iter().map(|b| b.0).collect::<Vec<_>>()), bundles.len()),
        (bundles.iter().flat_map(|b| le(&[b.1, b.2, b.3])).collect(), bundles.len()),
        (chunks.iter().flat_map(|c| le(c)).collect(), chunks.len()),
    ];
    let mut head = b"KFC4".to_vec();
    head.resize(16 + 8 * tables.len(), 0);
    let mut body = Vec::new();
    let hl = head.len();
    for (i, (data, count)) in tables.iter().enumerate() {
        let at = 0x10 + 8 * i;
        let start = hl + body.len();
        let rel = if data.is_empty() { 0 } else { (start - at) as u32 };
        head[at..at + 4].copy_from_slice(&rel.to_le_bytes());
        head[at + 4..at + 8].copy_from_slice(&(*count as u32).to_le_bytes());
        body.extend_from_slice(data);
        while body.len() % 4 != 0 {
            body.push(0);
        }
    }
    let mut out = [head, body].concat();
    let n = out.len() as u32;
    out[4..8].copy_from_slice(&n.to_le_bytes());
    out[8..12].copy_from_slice(&12u32.to_le_bytes());
    std::fs::write(dir.join(format!("{stem}.kfc")), out).unwrap();
}

fn ch(size: usize, n: u32) -> ContentHash {
    ContentHash { size: size as u32, h: [0x1000 + n, 0x2000 + n, 0x3000 + n] }
}

/// A 56-byte `UiTextureResource` (built-in layout) + its debug name.
fn ui_tex_record(content: ContentHash, name: &str, w: u32, h: u32, format: u8, levels: u8) -> Vec<u8> {
    let mut b = vec![0u8; 56];
    b[..16].copy_from_slice(&content.to_bytes());
    b[0x20..0x24].copy_from_slice(&(56i32 - 0x20).to_le_bytes());
    b[0x24..0x28].copy_from_slice(&(name.len() as u32).to_le_bytes());
    b[0x28..0x2C].copy_from_slice(&w.to_le_bytes());
    b[0x2C..0x30].copy_from_slice(&h.to_le_bytes());
    b[0x30] = 1; // Texture2D
    b[0x31] = format;
    b[0x32] = levels;
    b.extend_from_slice(name.as_bytes());
    b
}

fn font_record(files: &[ContentHash]) -> Vec<u8> {
    let mut b = vec![0u8; 16];
    b[0..4].copy_from_slice(&16i32.to_le_bytes());
    b[4..8].copy_from_slice(&(files.len() as u32).to_le_bytes());
    for f in files {
        let mut e = f.to_bytes().to_vec();
        e.resize(32, 0);
        b.extend_from_slice(&e);
    }
    b
}

fn solid(w: u32, h: u32, p: [u8; 4]) -> Vec<u8> {
    (0..w * h).flat_map(|_| p).collect()
}

/// A BC1 block of one colour (RGB565 `c` as both endpoints, all indices 0).
fn bc1_block(c: u16) -> [u8; 8] {
    let mut b = [0u8; 8];
    b[0..2].copy_from_slice(&c.to_le_bytes());
    b[2..4].copy_from_slice(&c.to_le_bytes());
    b
}

const TEX: u32 = 0xf9e8_6997;
const FONT: u32 = 0x878a_a5ad;

/// A synthetic client: icons (RGBA8 and BC1 with mips), UI textures, every tint source, fonts (OTF, TTF, a junk blob).
fn fake_client(dir: &Path, version: &str, tex_layout: u32) -> usize {
    let mut res = Vec::new();
    let mut contents = Vec::new();
    let mut n = 0u32;
    let mut add_tex = |name: &str, w: u32, h: u32, fmt: u8, levels: u8, data: Vec<u8>, res: &mut Vec<Res>, contents: &mut Vec<(ContentHash, Vec<u8>)>| {
        n += 1;
        let h_ = ch(data.len(), n);
        contents.push((h_, data));
        res.push(Res { guid: guid(n as u8), type_hash: TEX, part: 0, data: ui_tex_record(h_, name, w, h, fmt, levels) });
    };
    add_tex("Sword_T1_icon", 256, 256, 37, 1, solid(256, 256, [200, 30, 30, 255]), &mut res, &mut contents);
    // BC1, 2 levels (8x4 + 4x2): level 0 green, level 1 blue
    let mut bc = Vec::new();
    bc.extend_from_slice(&bc1_block(0x07E0));
    bc.extend_from_slice(&bc1_block(0x07E0));
    bc.extend_from_slice(&bc1_block(0x001F));
    add_tex("Tall_Thing_icon", 8, 4, 133, 2, bc, &mut res, &mut contents);
    add_tex("compass_player", 64, 64, 37, 1, solid(64, 64, [10, 20, 30, 128]), &mut res, &mut contents);
    add_tex("Intro_Slide", 2048, 32, 37, 1, solid(2048, 32, [1, 2, 3, 255]), &mut res, &mut contents);
    add_tex("bad/name", 4, 4, 37, 1, solid(4, 4, [0; 4]), &mut res, &mut contents);
    let mut srcs: Vec<&str> = editor::TINTS.iter().map(|t| t.src).collect();
    srcs.sort();
    srcs.dedup();
    for s in &srcs {
        add_tex(s, 16, 16, 37, 1, solid(16, 16, [255, 255, 255, 200]), &mut res, &mut contents);
    }
    let fonts = [b"OTTO-font-data".to_vec(), [0u8, 1, 0, 0, 9, 9].to_vec(), b"junk".to_vec()];
    let mut fh = Vec::new();
    for f in fonts {
        n += 1;
        let h_ = ch(f.len(), n);
        fh.push(h_);
        contents.push((h_, f));
    }
    res.push(Res { guid: guid(250), type_hash: FONT, part: 0, data: font_record(&fh) });
    build_kfc(dir, "enshrouded", version, &res, &contents, &[(TEX, tex_layout), (FONT, 0xb396_5140)]);
    srcs.len()
}

// ---------------------------------------------------------------- archive

#[test]
fn kfc_reads_resources_across_chunks_and_contents() {
    let t = Tmp::new("kfc");
    let res = vec![
        Res { guid: guid(1), type_hash: 0xB5CE_8765, part: 0, data: (0..200u8).collect() },
        Res { guid: guid(2), type_hash: 0x1773_94C7, part: 0, data: vec![b'R'; 150] },
        Res { guid: guid(1), type_hash: TEX, part: 0, data: b"icon".to_vec() },
        Res { guid: guid(2), type_hash: 0x1773_94C7, part: 3, data: b"part three".to_vec() },
    ];
    let contents = vec![(ch(900, 0), b"blob zero".repeat(100)), (ch(3, 1), vec![1, 2, 3]), (ch(5000, 2), vec![b'x'; 5000])];
    build_kfc(&t.0, "test", "1|^/test|2026-11-04", &res, &contents, &[]);
    let k = Kfc::open(&t.0.join("test.kfc")).unwrap();
    assert_eq!(k.version, "1|^/test|2026-11-04");
    assert_eq!((k.resource_count(), k.content_count(), k.containers), (4, 3, 2));
    for r in &res {
        let (ix, _) = k.resources_of_type(r.type_hash).into_iter().find(|(_, key)| key.guid == r.guid && key.part == r.part).unwrap();
        assert_eq!(k.read(ix).unwrap(), r.data);
    }
    let parts: Vec<u32> = k.resources_of_type(0x1773_94C7).iter().map(|(_, k)| k.part).collect();
    assert_eq!(parts, vec![0, 3]);
    assert_eq!(k.bundle(0x1773_94C7).unwrap().layout, 0xABC0_94C7);
    for (h, c) in &contents {
        assert_eq!(&k.read_content(h).unwrap(), c);
    }
    assert_eq!(k.read_content_prefix(&contents[0].0, Some(4)).unwrap(), b"blob");
    assert_eq!(k.content_location(&contents[1].0).unwrap().container, 1);
    assert!(k.read_content(&ch(1, 99)).is_err());
    assert_eq!(kfc::read_version(&t.0.join("test.kfc")).unwrap(), "1|^/test|2026-11-04");
    assert_eq!(build_of("1287369|^/game38/branches/release_1.0|2026"), "1287369");
    assert_eq!(fnv1a32(b"keen::UiTextureResource"), TEX);
    assert_eq!(kfc::guid_text(&guid(1)), "00000001-0000-0000-0000-0000000000a5");
}

#[test]
fn kfc_refuses_garbage() {
    let t = Tmp::new("bad");
    std::fs::write(t.0.join("x.kfc"), b"KFC3....").unwrap();
    assert_eq!(Kfc::open(&t.0.join("x.kfc")).err().unwrap().code, ErrorCode::Parse);
    let mut b = b"KFC4".to_vec();
    b.resize(0x90, 0);
    b[0x10..0x14].copy_from_slice(&0x7fff_0000u32.to_le_bytes()); // version far outside
    b[0x14..0x18].copy_from_slice(&5u32.to_le_bytes());
    std::fs::write(t.0.join("y.kfc"), &b).unwrap();
    assert_eq!(Kfc::open(&t.0.join("y.kfc")).err().unwrap().code, ErrorCode::Parse);
}

// ---------------------------------------------------------------- reflection (synthetic PE, layout as refl.rs)

struct Rec {
    name: &'static str,
    size: u32,
    kind: u8,
    inner: Option<&'static str>,
    fields: Vec<(&'static str, &'static str, u64)>,
    values: Vec<(&'static str, u64)>,
    layout: u32,
}

fn rec(name: &'static str, size: u32, kind: u8) -> Rec {
    Rec { name, size, kind, inner: None, fields: vec![], values: vec![], layout: 0 }
}

fn build_pe(records: &[Rec]) -> Vec<u8> {
    let base: u64 = 0x1_4000_0000;
    let (rdata_rva, data_rva) = (0x2000u64, 0x10000u64);
    let mut rdata: Vec<u8> = Vec::new();
    let mut strs: HashMap<String, u64> = HashMap::new();
    let mut s = |t: &str, rdata: &mut Vec<u8>| -> u64 {
        if let Some(v) = strs.get(t) {
            return *v;
        }
        let va = base + rdata_rva + rdata.len() as u64;
        rdata.extend_from_slice(t.as_bytes());
        rdata.push(0);
        strs.insert(t.to_string(), va);
        va
    };
    let rec_va: HashMap<&str, u64> = records.iter().enumerate().map(|(i, r)| (r.name, base + data_rva + 0x80 * i as u64)).collect();
    let extra = 0x80 * records.len() + 0x100;
    let mut data = vec![0u8; extra];
    let mut tables: Vec<u8> = Vec::new();
    let put = |b: &mut Vec<u8>, o: usize, v: &[u8]| b[o..o + v.len()].copy_from_slice(v);
    for (i, r) in records.iter().enumerate() {
        let o = 0x80 * i;
        let qn = s(r.name, &mut rdata);
        put(&mut data, o + 0x20, &qn.to_le_bytes());
        put(&mut data, o + 0x28, &(r.name.len() as u64).to_le_bytes());
        put(&mut data, o + 0x38, &r.inner.map_or(0u64, |n| rec_va[n]).to_le_bytes());
        put(&mut data, o + 0x40, &r.size.to_le_bytes());
        put(&mut data, o + 0x48, &((r.fields.len().max(r.values.len())) as u32).to_le_bytes());
        put(&mut data, o + 0x4C, &(r.kind as u32).to_le_bytes());
        put(&mut data, o + 0x50, &fnv1a32(r.name.as_bytes()).to_le_bytes());
        put(&mut data, o + 0x54, &r.layout.to_le_bytes());
        if !r.fields.is_empty() {
            put(&mut data, o + 0x58, &(base + data_rva + (extra + tables.len()) as u64).to_le_bytes());
            for (fname, ftype, foff) in &r.fields {
                let np = s(fname, &mut rdata);
                for v in [np, fname.len() as u64, rec_va[ftype], *foff, 0, 0] {
                    tables.extend_from_slice(&v.to_le_bytes());
                }
            }
        }
        if !r.values.is_empty() {
            put(&mut data, o + 0x60, &(base + data_rva + (extra + tables.len()) as u64).to_le_bytes());
            for (vname, v) in &r.values {
                let np = s(vname, &mut rdata);
                for x in [np, vname.len() as u64, *v, 0, 0] {
                    tables.extend_from_slice(&x.to_le_bytes());
                }
            }
        }
    }
    data.extend_from_slice(&tables);
    let secs = [(".rdata", rdata_rva as u32, rdata), (".data", data_rva as u32, data)];
    let pe = 0x80usize;
    let mut raw = vec![0u8; 0x400];
    raw[0..2].copy_from_slice(b"MZ");
    raw[0x3C..0x40].copy_from_slice(&(pe as u32).to_le_bytes());
    raw[pe..pe + 4].copy_from_slice(b"PE\0\0");
    raw[pe + 4..pe + 6].copy_from_slice(&0x8664u16.to_le_bytes());
    raw[pe + 6..pe + 8].copy_from_slice(&(secs.len() as u16).to_le_bytes());
    raw[pe + 20..pe + 22].copy_from_slice(&240u16.to_le_bytes());
    raw[pe + 24..pe + 26].copy_from_slice(&0x20Bu16.to_le_bytes());
    raw[pe + 48..pe + 56].copy_from_slice(&base.to_le_bytes());
    let mut so = pe + 24 + 240;
    for (name, rva, blob) in &secs {
        let at = raw.len();
        let mut n8 = [0u8; 8];
        n8[..name.len()].copy_from_slice(name.as_bytes());
        raw[so..so + 8].copy_from_slice(&n8);
        for (k, v) in [blob.len() as u32, *rva, blob.len() as u32, at as u32].iter().enumerate() {
            raw[so + 8 + 4 * k..so + 12 + 4 * k].copy_from_slice(&v.to_le_bytes());
        }
        so += 40;
        raw.extend_from_slice(blob);
        raw.resize(raw.len().div_ceil(0x200) * 0x200, 0);
    }
    raw
}

/// Reflection for the two resource types, with the UiTextureResource fields moved around (a "future build").
fn fake_types(tex_layout: u32) -> Vec<Rec> {
    use super::refl::{K_BLOB_ARRAY, K_BLOB_STRING, K_ENUM, K_STRUCT, K_TYPEDEF};
    let mut v = vec![
        rec("keen::uint8", 1, 2),
        rec("keen::uint32", 4, 6),
        rec("keen::uint64", 8, 8),
        rec("keen::string", 8, K_BLOB_STRING),
    ];
    let mut hk = rec("keen::HashKey32", 4, K_TYPEDEF);
    hk.inner = Some("keen::uint32");
    v.push(hk);
    let mut c = rec("keen::ContentHash", 16, K_STRUCT);
    c.fields = vec![("size", "keen::uint32", 0), ("hash0", "keen::uint32", 4), ("hash1", "keen::uint32", 8), ("hash2", "keen::uint32", 12)];
    v.push(c);
    let mut r = rec("keen::ResourceContentReference", 32, K_STRUCT);
    r.fields = vec![("hash", "keen::ContentHash", 0), ("category", "keen::HashKey32", 16), ("bundleId", "keen::uint32", 20), ("sortKey", "keen::uint64", 24)];
    v.push(r);
    let mut u2 = rec("keen::uint2", 8, K_STRUCT);
    u2.fields = vec![("x", "keen::uint32", 0), ("y", "keen::uint32", 4)];
    v.push(u2);
    let mut tt = rec("keen::TextureType", 1, K_ENUM);
    tt.inner = Some("keen::uint8");
    tt.values = vec![("Texture1D", 0), ("Texture2D", 1)];
    v.push(tt);
    let mut pf = rec("keen::PixelFormat", 1, K_ENUM);
    pf.inner = Some("keen::uint8");
    pf.values = vec![("None", 0), ("R8G8B8A8_unorm", 37), ("BC1_RGBA_unorm_block", 133)];
    v.push(pf);
    // same offsets as the built-in layout, but a layout hash only reflection knows
    let mut t = rec("keen::UiTextureResource", 56, K_STRUCT);
    t.layout = tex_layout;
    t.fields = vec![
        ("data", "keen::ResourceContentReference", 0),
        ("debugName", "keen::string", 0x20),
        ("size", "keen::uint2", 0x28),
        ("type", "keen::TextureType", 0x30),
        ("format", "keen::PixelFormat", 0x31),
        ("levelCount", "keen::uint8", 0x32),
    ];
    v.push(t);
    let mut ttf = rec("keen::TtfFontResource", 32, K_STRUCT);
    ttf.fields = vec![("fontFileData", "keen::ResourceContentReference", 0)];
    v.push(ttf);
    let mut arr = rec("keen::BlobArray<keen::TtfFontResource>", 8, K_BLOB_ARRAY);
    arr.inner = Some("keen::TtfFontResource");
    v.push(arr);
    let mut f = rec("keen::UiFontResource", 16, K_STRUCT);
    f.layout = 0xb396_5140;
    f.fields = vec![("fonts", "keen::BlobArray<keen::TtfFontResource>", 0)];
    v.push(f);
    v
}

#[test]
fn reflection_finds_fields_by_name() {
    let pe = refl::PeFile::parse(build_pe(&fake_types(0x1234_5678))).unwrap();
    let t = refl::TypeDb::from_pe(&pe).unwrap();
    assert!(t.len() >= 14);
    assert_eq!(t.path(res::UI_TEXTURE, "data.hash").unwrap().0, 0);
    assert_eq!(t.path(res::UI_TEXTURE, "size").unwrap().0, 0x28);
    assert_eq!(t.path("keen::ResourceContentReference", "category").unwrap().1.map(|r| t.resolve(r).size), Some(4));
    assert_eq!(t.get(res::UI_TEXTURE).unwrap().layout, 0x1234_5678);
    assert!(t.by_hash(TEX).is_some());
    assert_eq!(t.enum_values("keen::PixelFormat").unwrap().iter().find(|v| v.0 == "BC1_RGBA_unorm_block").unwrap().1, 133);
    assert!(refl::PeFile::parse(b"MZ not really".to_vec()).is_err());
}

// ---------------------------------------------------------------- codecs and images

#[test]
fn texture_levels_and_decoders() {
    // a 256² BC7 icon with 9 levels is 87,408 bytes (measured in the client)
    assert_eq!(Format::Bc7.level_offset(256, 256, 9).0, 87_408);
    assert_eq!(Format::Bc7.level_offset(256, 256, 1), (65_536, 128, 128));
    assert_eq!(Format::Bc1.level_size(6, 5), 2 * 2 * 8);
    assert_eq!(Format::from_name("BC7_srgb_block"), Some(Format::Bc7));
    assert_eq!(Format::from_name("BC6H_ufloat_block"), None);
    let px = texture::decode(Format::Bgra8, 1, 1, &[1, 2, 3, 4]).unwrap();
    assert_eq!(px.px, vec![3, 2, 1, 4]);
    // BC1 pure red (565 0xF800) cropped to 3x2
    let img = texture::decode(Format::Bc1, 3, 2, &bc1_block(0xF800)).unwrap();
    assert_eq!((img.w, img.h), (3, 2));
    assert!(img.px.chunks_exact(4).all(|p| p == [255, 0, 0, 255]));
    assert!(texture::decode(Format::Rgba8, 2, 2, &[0; 15]).is_err());
}

#[test]
fn image_fit_resize_tint_webp() {
    assert_eq!(image::fit_within(256, 256, 96, 96), (96, 96));
    assert_eq!(image::fit_within(374, 388, 96, 96), (93, 96)); // as in the editor's set
    assert_eq!(image::fit_within(1299, 1314, 96, 96), (95, 96));
    assert_eq!(image::fit_within(35, 40, 96, 96), (35, 40)); // never upscaled
    // a solid colour stays exact through the premultiplied resampler, transparent pixels add no fringe
    let mut src = Rgba { w: 8, h: 8, px: solid(8, 8, [200, 100, 50, 255]) };
    for y in 0..8 {
        for x in 0..4 {
            src.px[(y * 8 + x) * 4..][..4].copy_from_slice(&[0, 0, 0, 0]);
        }
    }
    let r = image::resize(&src, 4, 4);
    for p in r.px.chunks_exact(4).filter(|p| p[3] > 0) {
        assert_eq!(&p[..3], &[200, 100, 50]);
    }
    let t = image::tint(&Rgba { w: 1, h: 2, px: vec![9, 9, 9, 200, 9, 9, 9, 0] }, [1, 2, 3], 0.5);
    assert_eq!(t.px, vec![1, 2, 3, 100, 0, 0, 0, 0]);
    // lossless round trip (alpha and opaque)
    let img = Rgba { w: 3, h: 2, px: vec![1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 255, 0, 0, 0, 0, 200, 201, 202, 203] };
    assert_eq!(image::decode_webp(&image::encode_webp(&img).unwrap()).unwrap(), img);
    let opaque = Rgba { w: 2, h: 1, px: vec![10, 20, 30, 255, 40, 50, 60, 255] };
    assert_eq!(image::decode_webp(&image::encode_webp(&opaque).unwrap()).unwrap(), opaque);
    assert!(editor::safe_name("Sword_T1_icon") && !editor::safe_name("../x") && !editor::safe_name("a/b") && !editor::safe_name(""));
}

// ---------------------------------------------------------------- whole extraction

fn files_under(d: &Path) -> Vec<String> {
    let mut v = Vec::new();
    fn walk(root: &Path, d: &Path, v: &mut Vec<String>) {
        for e in std::fs::read_dir(d).unwrap().flatten() {
            let p = e.path();
            if p.is_dir() {
                walk(root, &p, v);
            } else {
                v.push(p.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/"));
            }
        }
    }
    if d.exists() {
        walk(d, d, &mut v);
    }
    v.sort();
    v
}

#[test]
fn extracts_the_editor_layout_with_builtin_layouts() {
    let game = Tmp::new("game");
    let data = Tmp::new("data");
    let nsrc = fake_client(&game.0, "77|^/test|2026-11-04T00:00:00Z", 0x3c9e_b3fa);
    let dest = data.0.join("editor-assets");
    std::fs::create_dir_all(dest.join("icons")).unwrap();
    std::fs::write(dest.join("icons/stale_icon.webp"), b"old").unwrap();
    std::fs::write(dest.join("README.txt"), b"kept").unwrap();
    let mut reports = 0;
    let mut prog = |_f: Option<f32>, _m: &str| reports += 1;
    let info = extract_editor(&game.0, &dest, &EditorOptions { threads: Some(3), ..Default::default() }, Some("123".into()), &mut prog).unwrap();
    assert!(reports > 3);
    assert_eq!(info.source, "game");
    let g = info.game.as_ref().unwrap();
    assert_eq!((g.build.as_str(), g.steam_build.as_deref(), g.layouts), ("77", Some("123"), Some(LayoutSource::Builtin)));
    assert_eq!(info.counts.get("icons"), Some(&2));
    assert_eq!(info.counts.get("tinted"), Some(&(editor::TINTS.len() as u64)));
    assert_eq!(info.counts.get("ui"), Some(&(1 + nsrc as u64)));
    assert_eq!(info.counts.get("fonts"), Some(&2));
    assert_eq!(info.skipped.get("large UI image the editor does not use"), Some(&1));
    assert_eq!(info.skipped.get("unusable name"), Some(&1));
    assert_eq!(info.skipped.get("failed"), Some(&1)); // the junk "font"
    let files = files_under(&dest);
    assert!(files.contains(&"icons/Sword_T1_icon.webp".into()) && files.contains(&"icons/Tall_Thing_icon.webp".into()));
    assert!(!files.contains(&"icons/stale_icon.webp".into()), "old art replaced");
    assert!(files.contains(&"README.txt".into()), "other files kept");
    assert!(files.contains(&"ui/compass_player.webp".into()) && files.contains(&"ui/tinted/gem_3.webp".into()));
    assert!(files.contains(&"fonts/font00.otf".into()) && files.contains(&"fonts/font01.ttf".into()));
    assert!(!files.iter().any(|f| f.contains("Intro_Slide") || f.starts_with(".extract-") || f.starts_with(".old-")));
    // icons fitted into 96x96; the BC1 one keeps its 2:1 aspect and decodes level 0 (green)
    let sword = image::decode_webp(&std::fs::read(dest.join("icons/Sword_T1_icon.webp")).unwrap()).unwrap();
    assert_eq!((sword.w, sword.h), (96, 96));
    assert!(sword.px.chunks_exact(4).all(|p| p == [200, 30, 30, 255]));
    let tall = image::decode_webp(&std::fs::read(dest.join("icons/Tall_Thing_icon.webp")).unwrap()).unwrap();
    assert_eq!((tall.w, tall.h), (8, 4));
    assert!(tall.px.chunks_exact(4).all(|p| p == [0, 255, 0, 255]));
    let gem = image::decode_webp(&std::fs::read(dest.join("ui/tinted/gem_3.webp")).unwrap()).unwrap();
    assert_eq!((gem.w, gem.h), (64, 64));
    assert_eq!(&gem.px[..4], &[121, 161, 221, 200]);
    assert_eq!(std::fs::read(dest.join("fonts/font00.otf")).unwrap(), b"OTTO-font-data");
    // .source.json round trip and status
    assert_eq!(read_source(&dest).unwrap(), info);
    let st = status(&dest, Some(&game.0));
    assert_eq!((st.current, st.build.as_deref(), st.game_build.as_deref()), (Some(true), Some("77"), Some("77")));
    // a game update (new .kfc version) makes the art outdated
    fake_client(&game.0, "78|^/test|2026-11-05T00:00:00Z", 0x3c9e_b3fa);
    let st = status(&dest, Some(&game.0));
    assert_eq!((st.current, st.game_build.as_deref()), (Some(false), Some("78")));
    assert_eq!(status(&dest, None).current, None);
}

#[test]
fn follows_reflection_when_the_layout_hash_changes() {
    let game = Tmp::new("game-refl");
    let data = Tmp::new("data-refl");
    fake_client(&game.0, "90|^/test|x", 0x1234_5678);
    let dest = data.0.join("editor-assets");
    // without an exe, an unknown layout hash is refused
    let e = extract_editor(&game.0, &dest, &EditorOptions::default(), None, &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Unsupported);
    // with the exe's reflection (same layout hash), the fields are found by name
    std::fs::write(game.0.join("enshrouded.exe"), build_pe(&fake_types(0x1234_5678))).unwrap();
    let info = extract_editor(&game.0, &dest, &EditorOptions::default(), None, &mut crate::task::NoProgress).unwrap();
    assert_eq!(info.game.as_ref().unwrap().layouts, Some(LayoutSource::Reflection));
    assert_eq!(info.game.as_ref().unwrap().exe_sha256.as_ref().map(|s| s.len()), Some(64));
    assert_eq!(info.counts.get("icons"), Some(&2));
    // exe and data from different builds (mid-update): refused
    std::fs::write(game.0.join("enshrouded.exe"), build_pe(&fake_types(0x9999_9999))).unwrap();
    let e = extract_editor(&game.0, &dest, &EditorOptions::default(), None, &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict);
}

struct CancelAfter(usize);
impl crate::task::Progress for CancelAfter {
    fn report(&mut self, _f: Option<f32>, _m: &str) {
        self.0 = self.0.saturating_sub(1);
    }
    fn canceled(&self) -> bool {
        self.0 == 0
    }
}

#[test]
fn cancel_keeps_the_old_art_and_refuses_the_game_folder() {
    let game = Tmp::new("game-cancel");
    let data = Tmp::new("data-cancel");
    fake_client(&game.0, "5|^/t|x", 0x3c9e_b3fa);
    let dest = data.0.join("editor-assets");
    std::fs::create_dir_all(dest.join("icons")).unwrap();
    std::fs::write(dest.join("icons/keep_icon.webp"), b"old").unwrap();
    let e = extract_editor(&game.0, &dest, &EditorOptions { threads: Some(1), ..Default::default() }, None, &mut CancelAfter(4)).unwrap_err();
    assert_eq!(e.code, ErrorCode::Canceled);
    assert_eq!(files_under(&dest), vec!["icons/keep_icon.webp".to_string()]);
    let e = extract_editor(&game.0, &game.0.join("kiln/art"), &EditorOptions::default(), None, &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Refused);
    assert!(!game.0.join("kiln").exists());
    // a server folder has no UI art
    let srv = Tmp::new("server");
    std::fs::write(srv.0.join("enshrouded_server.kfc"), b"KFC4").unwrap();
    assert_eq!(game_build(&srv.0).unwrap_err().code, ErrorCode::Unsupported);
}

#[test]
fn source_json_reads_the_fetch_script_format() {
    let d = Tmp::new("src");
    std::fs::write(d.0.join(SOURCE_FILE), r#"{"from": "/x/build-assets/game", "copied": "2026-11-04T10:00:00Z"}"#).unwrap();
    let s = read_source(&d.0).unwrap();
    assert_eq!((s.source.as_str(), s.from.as_deref(), s.extracted.as_str()), ("copy", Some("/x/build-assets/game"), "2026-11-04T10:00:00Z"));
    assert_eq!(status(&d.0, None).source.as_deref(), Some("copy"));
}

// ---------------------------------------------------------------- the real client (opt-in, read-only)

#[test]
#[ignore]
fn real_client() {
    let game = std::env::var_os("KILN_GAMEART_GAME").map(PathBuf::from).expect("KILN_GAMEART_GAME");
    let out = std::env::var_os("KILN_GAMEART_OUT").map(PathBuf::from).expect("KILN_GAMEART_OUT");
    let mut last = std::time::Instant::now();
    let mut prog = |f: Option<f32>, m: &str| {
        if last.elapsed().as_secs_f32() > 2.0 {
            eprintln!("{:5.1}% {m}", f.unwrap_or(0.0) * 100.0);
            last = std::time::Instant::now();
        }
    };
    let info = extract_editor(&game, &out, &EditorOptions::default(), std::env::var("KILN_GAMEART_STEAM_BUILD").ok(), &mut prog).unwrap();
    println!("{}", serde_json::to_string_pretty(&info).unwrap());
    println!("{:?}", status(&out, Some(&game)));
}

#[test]
#[ignore]
fn real_client_map() {
    let game = std::env::var_os("KILN_GAMEART_GAME").map(PathBuf::from).expect("KILN_GAMEART_GAME");
    let out = std::env::var_os("KILN_GAMEART_OUT").map(PathBuf::from).expect("KILN_GAMEART_OUT");
    let info = extract_map(&game, &out, &MapOptions::default(), std::env::var("KILN_GAMEART_STEAM_BUILD").ok(), &mut crate::task::NoProgress).unwrap();
    println!("{}", serde_json::to_string_pretty(&info).unwrap());
    println!("{:?}", status(&out, Some(&game)));
}

#[test]
fn map_needs_the_world_map_textures() {
    let game = Tmp::new("game-map");
    let data = Tmp::new("data-map");
    fake_client(&game.0, "7|^/t|x", 0x3c9e_b3fa);
    let e = extract_map(&game.0, &data.0.join("admin-assets"), &MapOptions::default(), None, &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Unsupported);
    assert!(!data.0.join("admin-assets/map").exists());
    assert_eq!(recipe_of("admin"), map::MAP_RECIPE);
    assert_eq!(recipe_of("editor"), editor::RECIPE);
}
