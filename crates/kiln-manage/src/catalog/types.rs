//! Kiln.Game's structs from the user's own game: the committed `managed/Kiln.Game/Generated.cs` is game-derived (a
//! reflection dump of ECS component layouts, `sigdb/types/server/<sha>.json`, through `tools/typegen/typegen.py`).
//! This module makes the same dump **offline from the installed exe** (gameart's reflection reader; no lab, no
//! running game) into `<catalog dir>/types/<side>-<exe sha256>.json`, and the same C# as typegen.py into
//! `<catalog dir>/Kiln.Game/Generated.cs`. Kiln.Game builds against the local file with
//! `-p:KilnLocalGameTypes=<that file>` (docs/kiln/modding/catalogs.md, "Kiln.Game from your game").

use crate::gameart::refl::TypeDb;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// The components tools/typegen/probe dumps (the committed Generated.cs has exactly these).
pub const DEFAULT_COMPONENTS: [&str; 9] =
    ["NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity", "TeleportCount", "NpcCommon"];

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DumpField {
    pub name: String,
    pub offset: u64,
    #[serde(rename = "type")]
    pub type_name: String,
    pub size: u32,
    pub kind: u8,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DumpType {
    pub name: String,
    pub qualified_name: String,
    /// `0x%08X` (as the probe writes it).
    pub hash: String,
    pub size: u32,
    pub kind: u8,
    pub fields: Vec<DumpField>,
}

/// The dump typegen.py reads (same shape as the lab probe's `kiln/data/typegen-probe/types.json`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Dump {
    pub comment: String,
    pub side: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exe_sha256: Option<String>,
    pub types: Vec<DumpType>,
}

/// The dump of `components` (`keen::ecs::<name>`) from reflection; components the exe lacks are left out.
pub fn dump(t: &TypeDb, side: &str, exe_sha256: Option<&str>, components: &[&str]) -> Dump {
    let mut types = Vec::new();
    for c in components {
        let q = format!("keen::ecs::{c}");
        let Some(rec) = t.get(&q) else { continue };
        // the record's own fields, as the lab probe walks them (a base struct's fields, e.g. the attribute header of
        // Health, are not part of the committed bindings)
        let fields = rec
            .fields
            .iter()
            .map(|f| {
                let ft = f.type_name.as_deref().and_then(|n| t.get(n));
                DumpField {
                    name: f.name.clone(),
                    offset: f.offset,
                    type_name: f.type_name.clone().unwrap_or_default(),
                    size: ft.map_or(0, |x| x.size),
                    kind: ft.map_or(0, |x| x.kind),
                }
            })
            .collect();
        types.push(DumpType { name: c.to_string(), qualified_name: q, hash: format!("0x{:08X}", rec.hash), size: rec.size, kind: rec.kind, fields });
    }
    Dump {
        comment: "Reflection dump of ECS component types, read offline from the game's exe (kiln-manage catalog::types).".into(),
        side: side.into(),
        exe_sha256: exe_sha256.map(str::to_string),
        types,
    }
}

/// `<dest>/types/<side>-<sha>.json` + `<dest>/Kiln.Game/Generated.cs`; returns the dump's path.
pub fn write_dump(t: &TypeDb, dest: &Path, side: &str, exe_sha256: &str, components: &[&str]) -> Result<PathBuf> {
    let d = dump(t, side, Some(exe_sha256), components);
    if d.types.is_empty() {
        return Err(Error::new(ErrorCode::Unsupported, "none of the Kiln.Game components is in this exe's reflection"));
    }
    let path = dest.join("types").join(format!("{side}-{exe_sha256}.json"));
    crate::util::write_json_atomic(&path, &d)?;
    let source = format!("types/{side}-{exe_sha256}.json");
    crate::util::write_atomic(&dest.join("Kiln.Game").join("Generated.cs"), generate_cs(&d, &source).as_bytes())?;
    Ok(path)
}

/// The newest existing dump for a side in `dest/types`.
pub fn dump_path_existing(dest: &Path, side: &str) -> Option<PathBuf> {
    let prefix = format!("{side}-");
    let mut found: Vec<(std::time::SystemTime, PathBuf)> = std::fs::read_dir(dest.join("types"))
        .ok()?
        .flatten()
        .filter(|e| e.file_name().to_str().is_some_and(|n| n.starts_with(&prefix) && n.ends_with(".json")))
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    found.sort();
    found.pop().map(|(_, p)| p)
}

// ---- C#: a port of tools/typegen/typegen.py (same output for the same dump, apart from the "Source:" line)

fn prim(kind: u8) -> Option<&'static str> {
    Some(match kind {
        1 | 2 => "byte",
        3 => "sbyte",
        4 => "ushort",
        5 => "short",
        6 => "uint",
        7 => "int",
        8 => "ulong",
        9 => "long",
        0xA => "float",
        0xB => "double",
        _ => return None,
    })
}

fn by_size(size: u32) -> Option<&'static str> {
    Some(match size {
        1 => "byte",
        2 => "ushort",
        4 => "uint",
        8 => "ulong",
        _ => return None,
    })
}

/// (C# type, raw byte buffer?) as typegen.py `cs_type`.
fn cs_type(f: &DumpField) -> (Option<&'static str>, bool) {
    if let Some(p) = prim(f.kind) {
        return (Some(p), false);
    }
    if f.kind == 0xC {
        return (by_size(f.size), by_size(f.size).is_none());
    }
    if f.kind == 0x11 {
        if f.type_name.contains("float") {
            return (Some(if f.size == 8 { "double" } else { "float" }), false);
        }
        return (by_size(f.size), by_size(f.size).is_none());
    }
    (None, true)
}

fn ident(name: &str) -> String {
    const KW: [&str; 11] = ["object", "string", "event", "params", "lock", "base", "class", "default", "fixed", "ref", "out"];
    if KW.contains(&name) { format!("@{name}") } else { name.to_string() }
}

/// Generated.cs for a dump; `source` is named in the header comment.
pub fn generate_cs(d: &Dump, source: &str) -> String {
    let mut out: Vec<String> = vec![
        "// SPDX-License-Identifier: MIT".into(),
        "// <auto-generated> by tools/typegen/typegen.py from a reflection dump. Do not edit by hand.".into(),
        format!("// Source: {source} (side: {}, {} types).", d.side, d.types.len()),
        "using System.Runtime.InteropServices;".into(),
        String::new(),
        "namespace Kiln.GameTypes;".into(),
        String::new(),
    ];
    for t in &d.types {
        out.push(format!("/// <summary>ECS component <c>{}</c> ({} bytes). Generated; exact engine layout.</summary>", t.qualified_name, t.size));
        out.push(format!("[StructLayout(LayoutKind.Explicit, Size = {})]", t.size));
        out.push(format!("public unsafe struct {}", t.name));
        out.push("{".into());
        out.push("    /// <summary>The engine type hash (FNV-1a-32 of the qualified name).</summary>".into());
        out.push(format!("    public const uint Hash = {};", t.hash));
        out.push("    /// <summary>The ECS component name used by kiln.world.</summary>".into());
        out.push(format!("    public const string Component = \"{}\";", t.name));
        for f in &t.fields {
            if f.name.is_empty() {
                continue;
            }
            let (cst, is_buf) = cs_type(f);
            let comment = format!("  // {}", f.type_name);
            match cst {
                Some(c) if !is_buf => out.push(format!("    [FieldOffset({})] public {c} {};{comment}", f.offset, ident(&f.name))),
                _ => out.push(format!("    [FieldOffset({})] public fixed byte {}[{}];{comment} (raw)", f.offset, ident(&f.name), f.size.max(1))),
            }
        }
        out.push("}".into());
        out.push(String::new());
    }
    out.push("/// <summary>Type-hash constants for every generated ECS component.</summary>".into());
    out.push("public static class ComponentTypes".into());
    out.push("{".into());
    for t in &d.types {
        out.push(format!("    public const uint {} = {};", t.name, t.hash));
    }
    out.push("}".into());
    out.push(String::new());
    out.push("internal static class ComponentRegistry".into());
    out.push("{".into());
    out.push("    public static readonly System.Collections.Generic.Dictionary<System.Type, string> Names = new()".into());
    out.push("    {".into());
    for t in &d.types {
        out.push(format!("        [typeof({})] = \"{}\",", t.name, t.name));
    }
    out.push("    };".into());
    out.push("}".into());
    out.push(String::new());
    out.join("\n")
}

/// Reads an exe's reflection and writes the dump + Generated.cs into `dest` (for an exe outside a game folder, e.g.
/// an older build in a cache). Returns the dump path.
pub fn from_exe(exe: &Path, side: &str, dest: &Path, components: &[&str]) -> Result<PathBuf> {
    let raw = std::fs::read(exe).map_err(crate::util::io_at(exe, "reading"))?;
    let sha = crate::hash::sha256_hex(&raw);
    let t = TypeDb::from_pe(&crate::gameart::refl::PeFile::parse(raw)?)?;
    write_dump(&t, dest, side, &sha, components)
}
