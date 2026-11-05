//! The game's own reflection records, read offline from the executable (a port of Kiln's MIT `tools/kfc/kfc/refl.py`
//! and `pe.py`). Every `keen::` type has a static record in `.data`/`.rdata`:
//!
//! | at | what |
//! |---|---|
//! | +0x20 / +0x28 | qualified name pointer / length |
//! | +0x38 | inner type record (typedef target, enum underlying type, array/blob element, struct base) |
//! | +0x40 | u32 size; +0x44 u16 alignment; +0x48 u32 field / enum value / static array count |
//! | +0x4C | u32 kind (low byte) |
//! | +0x50 | u32 hash = FNV-1a(qualified name); +0x54 u32 layout hash (what the `.kfc` stores per type bundle) |
//! | +0x58 | fields (stride 0x30: name ptr, len, type record, u64 offset) |
//! | +0x60 | enum values (stride 0x28: name ptr, len, u64 value) |
//!
//! Pointers are absolute VAs at the preferred image base (static data, no relocation needed). A record counts only when
//! the hash at +0x50 equals FNV-1a of the name it points at. The SteamStub-wrapped client keeps `.data`/`.rdata` plain.

use super::kfc::{fnv1a32, u16_at, u32_at, u64_at};
use crate::{Error, ErrorCode, Result};
use std::collections::HashMap;
use std::path::Path;

pub const K_TYPEDEF: u8 = 0x11;
pub const K_STRUCT: u8 = 0x12;
pub const K_ENUM: u8 = 0x0C;
pub const K_BLOB_ARRAY: u8 = 0x18;
pub const K_BLOB_STRING: u8 = 0x19;

const REC_QNAME: usize = 0x20;
const REC_INNER: usize = 0x38;
const REC_SIZE: usize = 0x40;
const REC_COUNT: usize = 0x48;
const REC_KIND: usize = 0x4C;
const REC_HASH: usize = 0x50;
const REC_LAYOUT: usize = 0x54;
const REC_FIELDS: usize = 0x58;
const REC_ENUMS: usize = 0x60;
const REC_LEN: usize = 0x68;
const FIELD_STRIDE: usize = 0x30;
const ENUM_STRIDE: usize = 0x28;

/// A PE32+ file with its sections, read without mapping: an RVA is turned into a file offset on each access.
pub struct PeFile {
    raw: Vec<u8>,
    pub base: u64,
    /// (name, rva, virtual size, raw offset, raw size)
    sections: Vec<(String, u32, u32, u32, u32)>,
}

impl PeFile {
    pub fn parse(raw: Vec<u8>) -> Result<PeFile> {
        let err = |m: &str| Error::new(ErrorCode::Parse, format!("exe: {m}"));
        if raw.get(..2) != Some(b"MZ") {
            return Err(err("not a PE file"));
        }
        let pe = u32_at(&raw, 0x3C).ok_or_else(|| err("short"))? as usize;
        if raw.get(pe..pe + 4) != Some(b"PE\0\0") {
            return Err(err("no PE signature"));
        }
        let nsec = u16_at(&raw, pe + 6).ok_or_else(|| err("short"))? as usize;
        let optsz = u16_at(&raw, pe + 20).ok_or_else(|| err("short"))? as usize;
        if u16_at(&raw, pe + 24) != Some(0x20B) {
            return Err(err("not PE32+"));
        }
        let base = u64_at(&raw, pe + 24 + 24).ok_or_else(|| err("short"))?;
        let mut sections = Vec::with_capacity(nsec);
        let mut off = pe + 24 + optsz;
        for _ in 0..nsec {
            let name = raw.get(off..off + 8).ok_or_else(|| err("short section table"))?;
            let name = String::from_utf8_lossy(name).trim_end_matches('\0').to_string();
            let vsz = u32_at(&raw, off + 8).unwrap_or(0);
            let va = u32_at(&raw, off + 12).unwrap_or(0);
            let rsz = u32_at(&raw, off + 16).unwrap_or(0);
            let roff = u32_at(&raw, off + 20).unwrap_or(0);
            sections.push((name, va, vsz, roff, rsz));
            off += 40;
        }
        Ok(PeFile { raw, base, sections })
    }

    /// The file offset of `rva` and the bytes left in its section's raw data.
    fn file_off(&self, rva: u64) -> Option<(usize, usize)> {
        for &(_, va, vsz, roff, rsz) in &self.sections {
            let (va, end) = (va as u64, va as u64 + vsz.max(rsz) as u64);
            if rva >= va && rva < end {
                let inside = rva - va;
                if inside >= rsz as u64 {
                    return None; // uninitialised tail: zeros
                }
                let o = roff as usize + inside as usize;
                return Some((o, (rsz as u64 - inside) as usize));
            }
        }
        None
    }

    fn bytes_at_va(&self, va: u64, len: usize) -> Option<&[u8]> {
        let rva = va.checked_sub(self.base)?;
        let (o, left) = self.file_off(rva)?;
        if len > left {
            return None;
        }
        self.raw.get(o..o + len)
    }

    fn q(&self, rva: u64) -> u64 {
        self.file_off(rva).and_then(|(o, l)| if l >= 8 { u64_at(&self.raw, o) } else { None }).unwrap_or(0)
    }
    fn d(&self, rva: u64) -> u32 {
        self.file_off(rva).and_then(|(o, l)| if l >= 4 { u32_at(&self.raw, o) } else { None }).unwrap_or(0)
    }

    fn text(&self, va: u64, len: u64) -> Option<String> {
        if len == 0 || len > 4096 {
            return None;
        }
        self.bytes_at_va(va, len as usize).map(|b| String::from_utf8_lossy(b).into_owned())
    }
}

#[derive(Debug, Clone)]
pub struct Field {
    pub name: String,
    pub type_name: Option<String>,
    pub offset: u64,
}

#[derive(Debug, Clone)]
pub struct TypeRec {
    pub name: String,
    pub hash: u32,
    pub layout: u32,
    pub size: u32,
    pub kind: u8,
    pub count: u32,
    pub inner: Option<String>,
    pub fields: Vec<Field>,
    pub values: Vec<(String, u64)>,
}

/// Every validated reflection record of an executable, by qualified name and by hash.
pub struct TypeDb {
    pub by_name: HashMap<String, TypeRec>,
    by_hash: HashMap<u32, String>,
}

impl TypeDb {
    pub fn from_exe(path: &Path) -> Result<TypeDb> {
        let raw = std::fs::read(path).map_err(crate::util::io_at(path, "reading"))?;
        Self::from_pe(&PeFile::parse(raw)?)
    }

    pub fn from_pe(pe: &PeFile) -> Result<TypeDb> {
        // scan .data/.rdata for records whose +0x50 hash matches the name they point at
        let mut recs: HashMap<u64, String> = HashMap::new();
        for (name, va, vsz, roff, rsz) in &pe.sections {
            if name != ".data" && name != ".rdata" {
                continue;
            }
            let n = (*vsz).min(*rsz) as usize;
            let sec = match pe.raw.get(*roff as usize..*roff as usize + n) {
                Some(s) => s,
                None => continue,
            };
            let image_end = pe.base + pe.sections.iter().map(|s| s.1 as u64 + s.2.max(s.4) as u64).max().unwrap_or(0);
            let mut o = 0usize;
            while o + REC_LEN <= sec.len() {
                let p = u64_at(sec, o + REC_QNAME).unwrap();
                let len = u64_at(sec, o + REC_QNAME + 8).unwrap();
                if p >= pe.base
                    && p < image_end
                    && (1..=512).contains(&len)
                    && let Some(s) = pe.bytes_at_va(p, len as usize)
                    && u32_at(sec, o + REC_HASH) == Some(fnv1a32(s))
                {
                    recs.insert(*va as u64 + o as u64, String::from_utf8_lossy(s).into_owned());
                }
                o += 8;
            }
        }
        if recs.is_empty() {
            return Err(Error::new(ErrorCode::Parse, "exe: no reflection records found"));
        }
        let name_of = |va: u64| -> Option<String> {
            if va == 0 {
                return None;
            }
            recs.get(&(va.checked_sub(pe.base)?)).cloned()
        };
        let mut by_name = HashMap::with_capacity(recs.len());
        let mut by_hash = HashMap::with_capacity(recs.len());
        for (&rva, name) in &recs {
            let kind = (pe.d(rva + REC_KIND as u64) & 0xFF) as u8;
            let count = pe.d(rva + REC_COUNT as u64);
            let mut t = TypeRec {
                name: name.clone(),
                hash: pe.d(rva + REC_HASH as u64),
                layout: pe.d(rva + REC_LAYOUT as u64),
                size: pe.d(rva + REC_SIZE as u64),
                kind,
                count,
                inner: name_of(pe.q(rva + REC_INNER as u64)),
                fields: Vec::new(),
                values: Vec::new(),
            };
            let fp = pe.q(rva + REC_FIELDS as u64);
            if kind == K_STRUCT && fp != 0 && count < 4096 {
                let fr = fp.wrapping_sub(pe.base);
                for i in 0..count as u64 {
                    let e = fr + i * FIELD_STRIDE as u64;
                    t.fields.push(Field {
                        name: pe.text(pe.q(e), pe.q(e + 8)).unwrap_or_default(),
                        type_name: name_of(pe.q(e + 0x10)),
                        offset: pe.q(e + 0x18),
                    });
                }
            }
            let ep = pe.q(rva + REC_ENUMS as u64);
            if kind == K_ENUM && ep != 0 && count < 65536 {
                let er = ep.wrapping_sub(pe.base);
                for i in 0..count as u64 {
                    let e = er + i * ENUM_STRIDE as u64;
                    t.values.push((pe.text(pe.q(e), pe.q(e + 8)).unwrap_or_default(), pe.q(e + 0x10)));
                }
            }
            by_hash.insert(t.hash, name.clone());
            by_name.insert(name.clone(), t);
        }
        Ok(TypeDb { by_name, by_hash })
    }

    pub fn get(&self, name: &str) -> Option<&TypeRec> {
        self.by_name.get(name)
    }
    pub fn by_hash(&self, h: u32) -> Option<&TypeRec> {
        self.by_hash.get(&h).and_then(|n| self.by_name.get(n))
    }
    pub fn len(&self) -> usize {
        self.by_name.len()
    }
    pub fn is_empty(&self) -> bool {
        self.by_name.is_empty()
    }

    /// Follows typedefs to the final record.
    pub fn resolve<'a>(&'a self, mut t: &'a TypeRec) -> &'a TypeRec {
        for _ in 0..16 {
            if t.kind != K_TYPEDEF {
                break;
            }
            match t.inner.as_deref().and_then(|n| self.get(n)) {
                Some(n) => t = n,
                None => break,
            }
        }
        t
    }

    /// A struct's field (base chain included) by name: (offset, field type).
    pub fn field<'a>(&'a self, t: &'a TypeRec, name: &str) -> Option<(u64, Option<&'a TypeRec>)> {
        let mut cur = Some(t);
        while let Some(c) = cur {
            if c.kind != K_STRUCT {
                break;
            }
            if let Some(f) = c.fields.iter().find(|f| f.name == name) {
                return Some((f.offset, f.type_name.as_deref().and_then(|n| self.get(n))));
            }
            cur = c.inner.as_deref().and_then(|n| self.get(n));
        }
        None
    }

    /// The offset of a dotted field path from the start of `type_name` (`data.hash` in UiTextureResource), with the
    /// last field's type.
    pub fn path(&self, type_name: &str, path: &str) -> Option<(u64, Option<&TypeRec>)> {
        let mut t = self.get(type_name)?;
        let mut off = 0u64;
        let mut last = None;
        for (i, seg) in path.split('.').enumerate() {
            if i > 0 {
                t = self.resolve(last?);
            }
            let (o, ft) = self.field(t, seg)?;
            off += o;
            last = ft;
        }
        Some((off, last))
    }

    /// The values of an enum type (typedefs followed).
    pub fn enum_values(&self, name: &str) -> Option<Vec<(String, u64)>> {
        let t = self.resolve(self.get(name)?);
        (t.kind == K_ENUM).then(|| t.values.clone())
    }
}
