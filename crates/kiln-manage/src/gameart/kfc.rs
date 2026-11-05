//! Read-only KFC4 reader: `<stem>.kfc` (directory), `<stem>.kfc_resources` (zstd chunks of the resource stream) and
//! `<stem>_NNN.dat` (content blobs). A Rust port of Kiln's clean-room MIT reader `tools/kfc/kfc/archive.py` (no
//! kfc-parser code; see docs/re/models.md, docs/re/resources.md). Nothing here ever opens a game file for writing.
//!
//! Layout (little-endian). A "location" is `{u32 offset relative to the location's own position, u32 count}`:
//!
//! | at | what |
//! |---|---|
//! | 0x00 | `"KFC4"`, u32 end of the tables, u32 12, u32 0 |
//! | 0x10 | version string (bytes), e.g. `1287369\|^/game38/branches/release_1.0\|2026-11-04T11:17:54Z` |
//! | 0x18 | containers `{u64 size, u64 content count}` |
//! | 0x30 | resource stream `{u32 raw size, u32 packed size, u32 resource count, u32 0, u32 chunk count}` |
//! | 0x38 | resource order (u32 per resource; bundles index this list) |
//! | 0x48 / 0x50 | content keys `ContentHash {u32 size, h0, h1, h2}` / values `{u32 offset, u16 flags, u16 container, u64 0}` |
//! | 0x60 / 0x68 | resource keys `{guid[16], u32 type hash, u32 part, u64 0}` / values `{u32 stream offset, u32 size}` |
//! | 0x78 / 0x80 | bundle keys (u32 type hash) / values `{u32 layout hash, u32 first (into the order), u32 count}` |
//! | 0x88 | chunks `{u32 file offset, u32 stored size, u32 packed size, u32 stream offset, u32 stream size}` |
//!
//! The bucket tables (0x40, 0x58, 0x70) are not needed: keys and values are parallel arrays.

use crate::{Error, ErrorCode, Result};
use std::collections::HashMap;
use std::fs::File;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub const MAGIC: &[u8; 4] = b"KFC4";

/// 32-bit FNV-1a: the hash of type names (`keen::UiTextureResource` -> 0xf9e86997).
pub fn fnv1a32(data: &[u8]) -> u32 {
    let mut h: u32 = 0x811C_9DC5;
    for &c in data {
        h = (h ^ c as u32).wrapping_mul(0x0100_0193);
    }
    h
}

/// `keen::ContentHash`: `{size, h0, h1, h2}`; the key of a content blob in the `.dat` containers.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ContentHash {
    pub size: u32,
    pub h: [u32; 3],
}

impl ContentHash {
    pub fn from_bytes(b: &[u8]) -> Option<ContentHash> {
        Some(ContentHash { size: u32_at(b, 0)?, h: [u32_at(b, 4)?, u32_at(b, 8)?, u32_at(b, 12)?] })
    }
    pub fn to_bytes(&self) -> [u8; 16] {
        let mut o = [0u8; 16];
        o[..4].copy_from_slice(&self.size.to_le_bytes());
        for i in 0..3 {
            o[4 + 4 * i..8 + 4 * i].copy_from_slice(&self.h[i].to_le_bytes());
        }
        o
    }
    pub fn is_null(&self) -> bool {
        self.size == 0 && self.h == [0; 3]
    }
}

/// A resource key: `{guid (keen::Guid memory order), type hash, part}`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ResourceKey {
    pub guid: [u8; 16],
    pub type_hash: u32,
    pub part: u32,
}

impl ResourceKey {
    /// Canonical lower-case GUID text (Windows `{u32, u16, u16, u8[8]}` layout, like tools/kfc `guid_text`).
    pub fn guid_text(&self) -> String {
        guid_text(&self.guid)
    }
}

pub fn guid_text(g: &[u8; 16]) -> String {
    let d1 = u32::from_le_bytes([g[0], g[1], g[2], g[3]]);
    let d2 = u16::from_le_bytes([g[4], g[5]]);
    let d3 = u16::from_le_bytes([g[6], g[7]]);
    format!(
        "{d1:08x}-{d2:04x}-{d3:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]
    )
}

/// Where a content blob lives.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ContentLoc {
    pub container: u16,
    pub offset: u32,
    pub flags: u16,
}

#[derive(Debug, Clone, Copy)]
pub struct Bundle {
    /// The type's layout hash (equals the reflection record's +0x54 of the build that cooked the data).
    pub layout: u32,
    pub first: u32,
    pub count: u32,
}

#[derive(Debug, Clone, Copy)]
struct Chunk {
    file_offset: u32,
    packed: u32,
    stream_offset: u32,
    stream_size: u32,
}

pub(crate) fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4).map(|x| u32::from_le_bytes([x[0], x[1], x[2], x[3]]))
}
pub(crate) fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o + 2).map(|x| u16::from_le_bytes([x[0], x[1]]))
}
pub(crate) fn u64_at(b: &[u8], o: usize) -> Option<u64> {
    b.get(o..o + 8).map(|x| u64::from_le_bytes(x.try_into().unwrap()))
}
pub(crate) fn i32_at(b: &[u8], o: usize) -> Option<i32> {
    u32_at(b, o).map(|v| v as i32)
}

fn bad(path: &Path, what: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Parse, format!("{}: {what}", path.display()))
}

/// Positional read that never moves a shared cursor (several threads read the same `.dat`).
pub(crate) fn read_exact_at(f: &File, buf: &mut [u8], offset: u64) -> std::io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::FileExt;
        f.read_exact_at(buf, offset)
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::FileExt;
        let mut done = 0usize;
        while done < buf.len() {
            let n = f.seek_read(&mut buf[done..], offset + done as u64)?;
            if n == 0 {
                return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "short read"));
            }
            done += n;
        }
        Ok(())
    }
}

/// An open KFC4 archive. `Sync`: content reads are positional, the chunk cache sits behind a mutex.
pub struct Kfc {
    pub path: PathBuf,
    stem: PathBuf,
    pub version: String,
    pub containers: usize,
    keys: Vec<ResourceKey>,
    values: Vec<(u32, u32)>,
    order: Vec<u32>,
    bundles: HashMap<u32, Bundle>,
    contents: HashMap<ContentHash, ContentLoc>,
    chunks: Vec<Chunk>,
    res_file: Mutex<Option<File>>,
    cache: Mutex<Vec<(usize, std::sync::Arc<Vec<u8>>)>>,
    dats: Mutex<HashMap<u16, std::sync::Arc<File>>>,
}

const CHUNK_CACHE: usize = 4;

/// The version string at 0x10 of a `.kfc` without reading the whole directory (the status check runs often).
pub fn read_version(path: &Path) -> Result<String> {
    use std::io::Read;
    let mut f = File::open(path).map_err(crate::util::io_at(path, "reading"))?;
    let mut head = vec![0u8; 64 * 1024];
    let n = f.read(&mut head).map_err(crate::util::io_at(path, "reading"))?;
    head.truncate(n);
    if head.get(..4) != Some(&MAGIC[..]) {
        return Err(bad(path, "not a KFC4 file"));
    }
    let rel = u32_at(&head, 0x10).ok_or_else(|| bad(path, "short header"))? as usize;
    let len = u32_at(&head, 0x14).ok_or_else(|| bad(path, "short header"))? as usize;
    let at = 0x10 + rel;
    if len > 4096 {
        return Err(bad(path, "version string too long"));
    }
    if at + len > head.len() {
        let mut b = vec![0u8; len];
        read_exact_at(&f, &mut b, at as u64).map_err(crate::util::io_at(path, "reading"))?;
        return Ok(String::from_utf8_lossy(&b).into_owned());
    }
    Ok(String::from_utf8_lossy(&head[at..at + len]).into_owned())
}

impl Kfc {
    /// Opens `path` (a `.kfc` file, or a folder holding exactly the game's `.kfc`).
    pub fn open(path: &Path) -> Result<Kfc> {
        let p = if path.is_dir() { find_kfc(path)? } else { path.to_path_buf() };
        let b = std::fs::read(&p).map_err(crate::util::io_at(&p, "reading"))?;
        Self::parse(&p, &b)
    }

    fn parse(p: &Path, b: &[u8]) -> Result<Kfc> {
        if b.get(..4) != Some(&MAGIC[..]) {
            return Err(bad(p, format!("magic {:?}, expected KFC4", b.get(..4).unwrap_or(&[]))));
        }
        let loc = |at: usize| -> Result<(usize, usize)> {
            let rel = u32_at(b, at).ok_or_else(|| bad(p, "short header"))? as usize;
            let n = u32_at(b, at + 4).ok_or_else(|| bad(p, "short header"))? as usize;
            Ok((at + rel, n))
        };
        let need = |o: usize, n: usize, stride: usize, what: &str| -> Result<()> {
            if o.checked_add(n.saturating_mul(stride)).is_none_or(|e| e > b.len()) {
                return Err(bad(p, format!("{what} table outside the file")));
            }
            Ok(())
        };
        let (o, n) = loc(0x10)?;
        need(o, n, 1, "version")?;
        let version = String::from_utf8_lossy(&b[o..o + n]).into_owned();
        let (_, containers) = loc(0x18)?;
        let (o, n) = loc(0x88)?;
        need(o, n, 20, "chunk")?;
        let chunks: Vec<Chunk> = (0..n)
            .map(|i| {
                let e = o + 20 * i;
                Chunk {
                    file_offset: u32_at(b, e).unwrap(),
                    packed: u32_at(b, e + 8).unwrap(),
                    stream_offset: u32_at(b, e + 12).unwrap(),
                    stream_size: u32_at(b, e + 16).unwrap(),
                }
            })
            .collect();
        let (o, n) = loc(0x38)?;
        need(o, n, 4, "order")?;
        let order: Vec<u32> = (0..n).map(|i| u32_at(b, o + 4 * i).unwrap()).collect();
        let (ko, kn) = loc(0x48)?;
        let (vo, vn) = loc(0x50)?;
        if kn != vn {
            return Err(bad(p, "content keys and values differ in count"));
        }
        need(ko, kn, 16, "content key")?;
        need(vo, vn, 16, "content value")?;
        let mut contents = HashMap::with_capacity(kn);
        for i in 0..kn {
            let key = ContentHash::from_bytes(&b[ko + 16 * i..]).unwrap();
            let e = vo + 16 * i;
            contents.insert(
                key,
                ContentLoc { offset: u32_at(b, e).unwrap(), flags: u16_at(b, e + 4).unwrap(), container: u16_at(b, e + 6).unwrap() },
            );
        }
        let (ko, kn) = loc(0x60)?;
        let (vo, vn) = loc(0x68)?;
        if kn != vn {
            return Err(bad(p, "resource keys and values differ in count"));
        }
        need(ko, kn, 32, "resource key")?;
        need(vo, vn, 8, "resource value")?;
        let mut keys = Vec::with_capacity(kn);
        let mut values = Vec::with_capacity(kn);
        for i in 0..kn {
            let e = ko + 32 * i;
            keys.push(ResourceKey {
                guid: b[e..e + 16].try_into().unwrap(),
                type_hash: u32_at(b, e + 16).unwrap(),
                part: u32_at(b, e + 20).unwrap(),
            });
            values.push((u32_at(b, vo + 8 * i).unwrap(), u32_at(b, vo + 8 * i + 4).unwrap()));
        }
        let (ko, kn) = loc(0x78)?;
        let (vo, vn) = loc(0x80)?;
        need(ko, kn, 4, "bundle key")?;
        need(vo, vn.min(kn), 12, "bundle value")?;
        let mut bundles = HashMap::new();
        for i in 0..kn.min(vn) {
            let th = u32_at(b, ko + 4 * i).unwrap();
            let e = vo + 12 * i;
            bundles.insert(th, Bundle { layout: u32_at(b, e).unwrap(), first: u32_at(b, e + 4).unwrap(), count: u32_at(b, e + 8).unwrap() });
        }
        Ok(Kfc {
            path: p.to_path_buf(),
            stem: p.with_extension(""),
            version,
            containers,
            keys,
            values,
            order,
            bundles,
            contents,
            chunks,
            res_file: Mutex::new(None),
            cache: Mutex::new(Vec::new()),
            dats: Mutex::new(HashMap::new()),
        })
    }

    pub fn resource_count(&self) -> usize {
        self.keys.len()
    }
    pub fn content_count(&self) -> usize {
        self.contents.len()
    }
    pub fn bundle(&self, type_hash: u32) -> Option<Bundle> {
        self.bundles.get(&type_hash).copied()
    }

    /// Every resource of a type (all parts), in the archive's order, with its index for [`Kfc::read`].
    pub fn resources_of_type(&self, type_hash: u32) -> Vec<(usize, ResourceKey)> {
        let Some(bd) = self.bundles.get(&type_hash) else { return Vec::new() };
        (0..bd.count as usize)
            .filter_map(|i| self.order.get(bd.first as usize + i))
            .map(|&ix| ix as usize)
            .filter(|&ix| ix < self.keys.len() && self.keys[ix].type_hash == type_hash)
            .map(|ix| (ix, self.keys[ix]))
            .collect()
    }

    fn chunk(&self, i: usize) -> Result<std::sync::Arc<Vec<u8>>> {
        {
            let mut c = self.cache.lock().unwrap();
            if let Some(pos) = c.iter().position(|(k, _)| *k == i) {
                let e = c.remove(pos);
                let data = e.1.clone();
                c.push(e);
                return Ok(data);
            }
        }
        let ch = self.chunks[i];
        let res = self.stem.with_extension("kfc_resources");
        let mut packed = vec![0u8; ch.packed as usize];
        {
            let mut f = self.res_file.lock().unwrap();
            if f.is_none() {
                *f = Some(File::open(&res).map_err(crate::util::io_at(&res, "reading"))?);
            }
            read_exact_at(f.as_ref().unwrap(), &mut packed, ch.file_offset as u64).map_err(crate::util::io_at(&res, "reading"))?;
        }
        let data = zstd::bulk::decompress(&packed, ch.stream_size as usize)
            .map_err(|e| bad(&res, format!("chunk {i}: zstd: {e}")))?;
        if data.len() != ch.stream_size as usize {
            return Err(bad(&res, format!("chunk {i}: {} bytes, expected {}", data.len(), ch.stream_size)));
        }
        let data = std::sync::Arc::new(data);
        let mut c = self.cache.lock().unwrap();
        c.push((i, data.clone()));
        if c.len() > CHUNK_CACHE {
            c.remove(0);
        }
        Ok(data)
    }

    /// Bytes `[offset, offset+size)` of the decompressed resource stream (may span chunks).
    pub fn read_stream(&self, offset: u32, size: u32) -> Result<Vec<u8>> {
        let mut out = Vec::with_capacity(size as usize);
        let (mut pos, end) = (offset as u64, offset as u64 + size as u64);
        // chunks are contiguous and sorted by stream offset
        let mut i = self.chunks.partition_point(|c| (c.stream_offset as u64) <= pos).saturating_sub(1);
        while pos < end {
            let ch = *self.chunks.get(i).ok_or_else(|| bad(&self.path, "read past the resource stream"))?;
            let (start, len) = (ch.stream_offset as u64, ch.stream_size as u64);
            if len == 0 || pos >= start + len {
                i += 1;
                continue;
            }
            if pos < start {
                return Err(bad(&self.path, "hole in the resource stream"));
            }
            let data = self.chunk(i)?;
            let a = (pos - start) as usize;
            let take = ((end - pos) as usize).min(len as usize - a);
            out.extend_from_slice(&data[a..a + take]);
            pos += take as u64;
            i += 1;
        }
        Ok(out)
    }

    /// The bytes of resource number `index` (from [`Kfc::resources_of_type`]).
    pub fn read(&self, index: usize) -> Result<Vec<u8>> {
        let (off, size) = *self.values.get(index).ok_or_else(|| Error::not_found(format!("resource #{index}")))?;
        self.read_stream(off, size)
    }

    pub fn content_location(&self, h: &ContentHash) -> Option<ContentLoc> {
        self.contents.get(h).copied()
    }

    pub fn dat_path(&self, container: u16) -> PathBuf {
        let mut s = self.stem.clone().into_os_string();
        s.push(format!("_{container:03}.dat"));
        PathBuf::from(s)
    }

    fn dat(&self, container: u16) -> Result<std::sync::Arc<File>> {
        let mut m = self.dats.lock().unwrap();
        if let Some(f) = m.get(&container) {
            return Ok(f.clone());
        }
        let p = self.dat_path(container);
        let f = std::sync::Arc::new(File::open(&p).map_err(crate::util::io_at(&p, "reading"))?);
        m.insert(container, f.clone());
        Ok(f)
    }

    /// The first `len` bytes (None: all `h.size`) of a content blob.
    pub fn read_content_prefix(&self, h: &ContentHash, len: Option<usize>) -> Result<Vec<u8>> {
        let loc = self
            .content_location(h)
            .ok_or_else(|| Error::not_found(format!("content {:08x}-{:08x} not in the directory", h.size, h.h[0])))?;
        let n = len.map_or(h.size as usize, |l| l.min(h.size as usize));
        let mut buf = vec![0u8; n];
        let f = self.dat(loc.container)?;
        read_exact_at(&f, &mut buf, loc.offset as u64).map_err(crate::util::io_at(&self.dat_path(loc.container), "reading"))?;
        Ok(buf)
    }

    pub fn read_content(&self, h: &ContentHash) -> Result<Vec<u8>> {
        self.read_content_prefix(h, None)
    }
}

/// The `.kfc` of a game folder: `enshrouded.kfc` (client) or `enshrouded_server.kfc`, else the only `.kfc` there.
pub fn find_kfc(dir: &Path) -> Result<PathBuf> {
    for n in ["enshrouded.kfc", "enshrouded_server.kfc"] {
        if dir.join(n).is_file() {
            return Ok(dir.join(n));
        }
    }
    let mut found: Vec<PathBuf> = std::fs::read_dir(dir)
        .map_err(crate::util::io_at(dir, "reading"))?
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "kfc") && p.is_file())
        .collect();
    found.sort();
    found.into_iter().next().ok_or_else(|| Error::not_found(format!("no .kfc in {}", dir.display())))
}
