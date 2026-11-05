//! A reflected resource ("blob": the struct, then its out-of-line data) as JSON values: a port of Kiln's MIT
//! `tools/kfc/kfc/blob.py` (`Decoder`) onto gameart's [`TypeDb`]. Out-of-line kinds hold a signed 32-bit offset relative
//! to the offset field itself: `blob_array {i32 rel, u32 count}`, `blob_string {i32 rel, u32 length}`,
//! `blob_optional {i32 rel}`, `blob_variant {u32 type hash, i32 rel (from +4), u32 size}`. `object_reference` and `guid`
//! are 16 bytes (keen::Guid memory order). Plus cheap offset helpers for the hot paths (12k template names).

use crate::gameart::kfc::guid_text;
use crate::gameart::refl::{Field, TypeDb, TypeRec};
use crate::{Error, ErrorCode, Result};
use serde_json::{Map, Value, json};

// kinds (low byte of the record's +0x4C), as tools/kfc/kfc/refl.py
pub const K_BOOL: u8 = 1;
pub const K_U8: u8 = 2;
pub const K_S8: u8 = 3;
pub const K_U16: u8 = 4;
pub const K_S16: u8 = 5;
pub const K_U32: u8 = 6;
pub const K_S32: u8 = 7;
pub const K_U64: u8 = 8;
pub const K_S64: u8 = 9;
pub const K_F32: u8 = 0x0A;
pub const K_F64: u8 = 0x0B;
pub const K_ENUM: u8 = 0x0C;
pub const K_BM8: u8 = 0x0D;
pub const K_BM64: u8 = 0x10;
pub const K_TYPEDEF: u8 = 0x11;
pub const K_STRUCT: u8 = 0x12;
pub const K_STATIC_ARRAY: u8 = 0x13;
pub const K_BLOB_ARRAY: u8 = 0x18;
pub const K_BLOB_STRING: u8 = 0x19;
pub const K_BLOB_OPTIONAL: u8 = 0x1A;
pub const K_BLOB_VARIANT: u8 = 0x1B;
pub const K_OBJECT_REF: u8 = 0x1C;
pub const K_GUID: u8 = 0x1D;

pub fn u8_at(b: &[u8], o: usize) -> Option<u8> {
    b.get(o).copied()
}
pub fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o + 2).map(|x| u16::from_le_bytes([x[0], x[1]]))
}
pub fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4).map(|x| u32::from_le_bytes([x[0], x[1], x[2], x[3]]))
}
pub fn i32_at(b: &[u8], o: usize) -> Option<i32> {
    u32_at(b, o).map(|v| v as i32)
}
pub fn u64_at(b: &[u8], o: usize) -> Option<u64> {
    b.get(o..o + 8).map(|x| u64::from_le_bytes(x.try_into().unwrap()))
}

/// The 16 bytes of a GUID / object reference at `o`, None when null or out of range.
pub fn guid_bytes_at(b: &[u8], o: usize) -> Option<[u8; 16]> {
    let g: [u8; 16] = b.get(o..o + 16)?.try_into().ok()?;
    (g != [0u8; 16]).then_some(g)
}

/// Lower-case GUID text at `o`, None when null.
pub fn guid_at(b: &[u8], o: usize) -> Option<String> {
    guid_bytes_at(b, o).map(|g| guid_text(&g))
}

/// A `BlobString` at `o` ("" when empty or out of range).
pub fn blob_string_at(b: &[u8], o: usize) -> String {
    let (Some(rel), Some(n)) = (i32_at(b, o), u32_at(b, o + 4)) else { return String::new() };
    if n == 0 {
        return String::new();
    }
    let start = o as i64 + rel as i64;
    if start < 0 {
        return String::new();
    }
    b.get(start as usize..start as usize + n as usize).map(|s| String::from_utf8_lossy(s).into_owned()).unwrap_or_default()
}

/// A `BlobArray` header at `o`: (absolute start, count); None when out of range.
pub fn blob_array_at(b: &[u8], o: usize) -> Option<(usize, usize)> {
    let rel = i32_at(b, o)?;
    let n = u32_at(b, o + 4)? as usize;
    if n == 0 {
        return Some((0, 0));
    }
    let start = o as i64 + rel as i64;
    (start >= 0).then_some((start as usize, n))
}

/// Field offsets by dotted path (base chains and typedefs followed), cached.
pub struct Offsets<'a> {
    pub t: &'a TypeDb,
    cache: std::cell::RefCell<std::collections::HashMap<(String, String), Option<usize>>>,
}

impl<'a> Offsets<'a> {
    pub fn new(t: &'a TypeDb) -> Self {
        Offsets { t, cache: Default::default() }
    }

    pub fn get(&self, type_name: &str, path: &str) -> Option<usize> {
        let key = (type_name.to_string(), path.to_string());
        if let Some(v) = self.cache.borrow().get(&key) {
            return *v;
        }
        let v = self.t.path(type_name, path).map(|(o, _)| o as usize);
        self.cache.borrow_mut().insert(key, v);
        v
    }

    /// Like [`Offsets::get`], an error naming the missing field.
    pub fn need(&self, type_name: &str, path: &str) -> Result<usize> {
        self.get(type_name, path)
            .ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{type_name}.{path} is not in this game's reflection")))
    }
}

/// Every field of a struct, base chain first (as refl.py `all_fields`).
pub fn all_fields<'a>(t: &'a TypeDb, rec: &'a TypeRec) -> Vec<&'a Field> {
    let mut chain = Vec::new();
    let mut cur = Some(rec);
    while let Some(c) = cur {
        if c.kind != K_STRUCT {
            break;
        }
        chain.push(c);
        cur = c.inner.as_deref().and_then(|n| t.get(n));
        if chain.len() > 32 {
            break;
        }
    }
    chain.iter().rev().flat_map(|c| c.fields.iter()).collect()
}

/// The JSON decoder. `enum_names`: enums as their value names (else numbers).
pub struct Decoder<'a> {
    pub t: &'a TypeDb,
    pub enum_names: bool,
    pub max_array: usize,
}

impl<'a> Decoder<'a> {
    pub fn new(t: &'a TypeDb) -> Self {
        Decoder { t, enum_names: true, max_array: 1 << 20 }
    }

    pub fn decode(&self, type_name: &str, b: &[u8], o: usize) -> Result<Value> {
        let t = self.t.get(type_name).ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("unknown type {type_name}")))?;
        self.value(t, b, o, 0)
    }

    fn err(&self, t: &TypeRec, o: usize, what: &str) -> Error {
        Error::new(ErrorCode::Parse, format!("{} at {o:#x}: {what}", t.name))
    }

    fn value(&self, t: &TypeRec, b: &[u8], o: usize, depth: u32) -> Result<Value> {
        if depth > 64 {
            return Err(self.err(t, o, "nesting too deep"));
        }
        let short = || self.err(t, o, "outside the blob");
        let k = t.kind;
        Ok(match k {
            K_BOOL => json!(u8_at(b, o).ok_or_else(short)? != 0),
            K_U8 => json!(u8_at(b, o).ok_or_else(short)?),
            K_S8 => json!(u8_at(b, o).ok_or_else(short)? as i8),
            K_U16 => json!(u16_at(b, o).ok_or_else(short)?),
            K_S16 => json!(u16_at(b, o).ok_or_else(short)? as i16),
            K_U32 => json!(u32_at(b, o).ok_or_else(short)?),
            K_S32 => json!(i32_at(b, o).ok_or_else(short)?),
            K_U64 => json!(u64_at(b, o).ok_or_else(short)?),
            K_S64 => json!(u64_at(b, o).ok_or_else(short)? as i64),
            K_F32 => json!(f32::from_bits(u32_at(b, o).ok_or_else(short)?)),
            K_F64 => json!(f64::from_bits(u64_at(b, o).ok_or_else(short)?)),
            K_TYPEDEF => match t.inner.as_deref().and_then(|n| self.t.get(n)) {
                Some(inner) => self.value(inner, b, o, depth)?,
                None => json!(hex(b.get(o..o + t.size as usize).ok_or_else(short)?)),
            },
            K_ENUM => {
                let v = self.uint(b, o, t.size).ok_or_else(short)?;
                let under = t.inner.as_deref().and_then(|n| self.t.get(n)).map(|u| self.t.resolve(u));
                let signed = under.is_some_and(|u| matches!(u.kind, K_S8 | K_S16 | K_S32 | K_S64));
                if self.enum_names
                    && let Some((n, _)) = t.values.iter().find(|(_, ev)| *ev == v)
                {
                    json!(n)
                } else if signed {
                    json!(sign_extend(v, t.size))
                } else {
                    json!(v)
                }
            }
            K_BM8..=K_BM64 => {
                let v = self.uint(b, o, t.size).ok_or_else(short)?;
                let en = t.inner.as_deref().and_then(|n| self.t.get(n));
                match en {
                    Some(en) if self.enum_names => {
                        let bits = (8 * t.size).min(64);
                        let names: Vec<Value> = (0..bits)
                            .filter(|i| (v >> i) & 1 == 1)
                            .map(|i| en.values.iter().find(|(_, ev)| *ev == i as u64).map_or(json!(i), |(n, _)| json!(n)))
                            .collect();
                        Value::Array(names)
                    }
                    _ => json!(v),
                }
            }
            K_STRUCT => {
                let mut out = Map::new();
                for f in all_fields(self.t, t) {
                    let v = match f.type_name.as_deref().and_then(|n| self.t.get(n)) {
                        Some(ft) => self.value(ft, b, o + f.offset as usize, depth + 1)?,
                        None => Value::Null,
                    };
                    out.insert(f.name.clone(), v);
                }
                Value::Object(out)
            }
            K_STATIC_ARRAY => {
                let el = t.inner.as_deref().and_then(|n| self.t.get(n)).ok_or_else(|| self.err(t, o, "no element type"))?;
                let n = t.count as usize;
                let es = if el.size > 0 { el.size as usize } else { (t.size as usize).checked_div(n).unwrap_or(0) };
                let mut v = Vec::with_capacity(n);
                for i in 0..n {
                    v.push(self.value(el, b, o + i * es, depth + 1)?);
                }
                Value::Array(v)
            }
            K_OBJECT_REF | K_GUID => {
                let g: [u8; 16] = b.get(o..o + 16).ok_or_else(short)?.try_into().unwrap();
                json!(guid_text(&g))
            }
            K_BLOB_ARRAY => {
                let (base, n) = blob_array_at(b, o).ok_or_else(short)?;
                if n == 0 {
                    return Ok(Value::Array(Vec::new()));
                }
                if n > self.max_array {
                    return Err(self.err(t, o, &format!("{n} elements")));
                }
                let el = t.inner.as_deref().and_then(|n| self.t.get(n)).ok_or_else(|| self.err(t, o, "no element type"))?;
                let es = el.size as usize;
                if base + n * es > b.len() {
                    return Err(self.err(t, o, &format!("[{base:#x}, +{n}x{es}) outside the blob ({})", b.len())));
                }
                if el.kind == K_U8 && n > 64 {
                    return Ok(json!({ "bytes": n }));
                }
                let mut v = Vec::with_capacity(n);
                for i in 0..n {
                    v.push(self.value(el, b, base + i * es, depth + 1)?);
                }
                Value::Array(v)
            }
            K_BLOB_STRING => json!(blob_string_at(b, o)),
            K_BLOB_OPTIONAL => {
                let rel = i32_at(b, o).ok_or_else(short)?;
                if rel == 0 {
                    return Ok(Value::Null);
                }
                let el = t.inner.as_deref().and_then(|n| self.t.get(n)).ok_or_else(|| self.err(t, o, "no element type"))?;
                let at = o as i64 + rel as i64;
                if at < 0 {
                    return Err(short());
                }
                self.value(el, b, at as usize, depth + 1)?
            }
            K_BLOB_VARIANT => {
                let h = u32_at(b, o).ok_or_else(short)?;
                let rel = i32_at(b, o + 4).ok_or_else(short)?;
                let size = u32_at(b, o + 8).ok_or_else(short)?;
                if h == 0 {
                    return Ok(Value::Null);
                }
                match self.t.by_hash(h) {
                    None => json!({ "$type": format!("0x{h:08x}"), "$size": size }),
                    Some(vt) => {
                        let at = o as i64 + 4 + rel as i64;
                        if at < 0 {
                            return Err(short());
                        }
                        json!({ "$type": vt.name, "value": self.value(vt, b, at as usize, depth + 1)? })
                    }
                }
            }
            _ => json!({ "$kind": k, "$size": t.size }),
        })
    }

    fn uint(&self, b: &[u8], o: usize, size: u32) -> Option<u64> {
        Some(match size {
            1 => u8_at(b, o)? as u64,
            2 => u16_at(b, o)? as u64,
            4 => u32_at(b, o)? as u64,
            8 => u64_at(b, o)?,
            _ => return None,
        })
    }
}

fn sign_extend(v: u64, size: u32) -> i64 {
    let bits = 8 * size.min(8);
    if bits == 64 {
        return v as i64;
    }
    let shift = 64 - bits;
    ((v << shift) as i64) >> shift
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

/// A decoded id as an integer: ids are typedefs of `keen::HashKey32` or one-field structs, so they decode as `123`,
/// `{"value": 123}` or `{"id": 123}` (cli.py `scalar`).
pub fn scalar(v: &Value) -> Option<u64> {
    let mut v = v;
    for _ in 0..4 {
        match v {
            Value::Bool(b) => return Some(*b as u64),
            Value::Number(n) => return n.as_u64().or_else(|| n.as_i64().map(|x| x as u64)),
            Value::Object(m) if m.len() == 1 => v = m.values().next().unwrap(),
            _ => return None,
        }
    }
    None
}

/// `scalar` as `0x%08x`.
pub fn hex_scalar(v: &Value) -> Option<String> {
    scalar(v).map(|n| format!("0x{:08x}", n as u32))
}

/// A GUID string value, None for the null GUID.
pub fn guid_value(v: &Value) -> Option<String> {
    v.as_str().filter(|s| !s.starts_with("00000000-0000-0000-0000-000000000000")).map(str::to_string)
}
