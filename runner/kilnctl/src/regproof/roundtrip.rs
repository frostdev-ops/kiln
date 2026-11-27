// SPDX-License-Identifier: MIT
//! The registry round-trip suite (registry proof step 3, BRIEF 6.1), kilnctl's half: the lab writes one file per side and
//! generation, kilnctl judges it. For every registry field a lab sample reads the value through the schema, writes a probe
//! value, reads it back through the schema and raw through reflection, reverts, and hashes the entry's resource bytes
//! before the write and after the revert. Live fields are proven in a live generation (kiln.content's store at world
//! load), boot fields in a boot generation (the boot resource patch before the game reads it), read-only fields by a
//! read. kilnctl compares the raw values with the Kiln value itself (per conversion; formulas in formula.rs) and fails
//! closed: a field without a passing sample in its generation, a failing sample, a missing generation or a suite of
//! another build leaves the field unproven.
//!
//! The file (`"kind": "registry-roundtrip"`, format 1; written by the lab's proof producer, which is not part of kilnctl):
//! ```jsonc
//! { "format": 1, "kind": "registry-roundtrip", "side": "server", "exeSha256": "<the exe's sha256>",
//!   "generation": "live",                       // live | boot
//!   "regmapDigest": "<sha256>",                 // the regmap/common the run used (regmap::digest); another one is set aside
//!   "constants": { "gear.seed": 10, "gear.creep": 0.15, "gear.armorOffset": 23, "gear.armorGradient": 15 },
//!   "kinds": { "items.manaCost": { "sint32": 138, "float": 0, "unknown": 0, "notInWorld": 55 } },   // impactConfig rows
//!   "notInWorld": { "items.manaCost": ["game:...", ...] },  // the entries counted notInWorld (in the id table, not the world)
//!   "samples": [ { "field": "items.maxStack", "entry": "game:...", "element": null,   // element: a list element's index
//!                  "read": 20, "probe": 21, "readBack": 21,                          // through the schema
//!                  "rawBefore": [20], "rawAfter": [21],                              // the row's paths, through reflection
//!                  "reverted": true, "bytesBefore": "<sha256>", "bytesAfter": "<sha256>", "error": null } ] }
//! ```
//! Raw values: numbers for int/float/bool members (a bool may be 0/1), a time member's nanoseconds, an enum member's game
//! name, a bitmask member's list of game names, an optional enum's [switch, name]. Entry ids name the game's entries:
//! suites are lab artefacts and stay outside the repository.
use super::formula;
use super::values;
use super::regmap::{RegMap, Row};
use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;

pub const KIND: &str = "registry-roundtrip";

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Suite {
    #[serde(default)]
    pub format: u32,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub side: String,
    #[serde(default)]
    pub exe_sha256: String,
    #[serde(default)]
    pub generation: String,
    /// the mapping the lab ran with ([`super::regmap::digest`] of its regmap/common)
    #[serde(default)]
    pub regmap_digest: String,
    #[serde(default)]
    pub constants: BTreeMap<String, f64>,
    /// impactConfig rows: how many of the build's entries hold the config in each kind (`sint32`, `float`), how many hold
    /// it in a kind the lab could not read (`unknown`), and how many of the build's id table the lab's world does not hold
    /// (`notInWorld`: nothing reads them there); only the kinds the build has need evidence (coordinator ruling,
    /// 2026-11-23 ~1:20 PM). kilnctl compares the counts with the values export (review3 S1).
    #[serde(default)]
    pub kinds: BTreeMap<String, BTreeMap<String, u64>>,
    /// impactConfig rows: the ids of the entries counted `notInWorld` (whether they hold the config is the values export's
    /// to say)
    #[serde(default)]
    pub not_in_world: BTreeMap<String, Vec<String>>,
    /// fields the producer could not sample, with why in Kiln words (players read it as the unproven reason): for example
    /// drop tables the lab's world had not loaded
    #[serde(default)]
    pub unsampled: BTreeMap<String, String>,
    #[serde(default)]
    pub samples: Vec<Sample>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Sample {
    pub field: String,
    #[serde(default)]
    pub entry: String,
    #[serde(default)]
    pub element: Option<u64>,
    #[serde(default)]
    pub read: Option<Value>,
    #[serde(default)]
    pub probe: Option<Value>,
    #[serde(default)]
    pub read_back: Option<Value>,
    #[serde(default)]
    pub raw_before: Option<Vec<Value>>,
    #[serde(default)]
    pub raw_after: Option<Vec<Value>>,
    #[serde(default)]
    pub reverted: Option<bool>,
    #[serde(default)]
    pub bytes_before: Option<String>,
    #[serde(default)]
    pub bytes_after: Option<String>,
    #[serde(default)]
    pub error: Option<String>,
    #[serde(default)]
    pub config_kind: Option<String>,
    #[serde(default)]
    pub expect_refused: bool,
    #[serde(default)]
    pub refused: bool,
    #[serde(default)]
    pub refusal_reason: String,
    #[serde(default)]
    pub refusal_code: String,
}

/// A suite file; refuses another kind or format.
#[cfg(test)]
pub fn parse(text: &str) -> Result<Suite> {
    from_value(serde_json::from_str(text).context("not JSON")?)
}

pub fn from_value(v: Value) -> Result<Suite> {
    let s: Suite = serde_json::from_value(v).context("not a round-trip suite")?;
    if s.kind != KIND || s.format != 1 {
        bail!("not a {KIND} file of format 1 (kind {:?}, format {})", s.kind, s.format);
    }
    Ok(s)
}

/// How the raw values of a field were compared.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Raw {
    /// kilnctl compared the raw member values with the Kiln value
    Compared,
    /// a formula row: formula.rs computes the formula over the raw values
    Formula,
    /// a list, reference or other structured value: proven through the schema, the revert and the bytes only
    SchemaOnly,
}

#[derive(Debug, Clone, Serialize)]
pub struct FieldResult {
    pub ok: bool,
    pub samples: usize,
    pub passed: usize,
    pub raw: Raw,
    /// a step that failed: the mapping is wrong on this build
    pub problems: Vec<String>,
    /// evidence the lab did not give (no sample in the field's generation): the field is unproven, not wrong
    pub missing: Vec<String>,
    /// impactConfig rows: per config kind, what the proof holds ("proven", "none on this build", "not proven")
    #[serde(skip_serializing_if = "BTreeMap::is_empty")]
    pub kinds: BTreeMap<String, String>,
    /// no sample: why, as the producer said it (Kiln words; the record's reason)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub lab_reason: Option<String>,
}

/// The impact-config kinds a proof knows, as the producer names them.
pub const CONFIG_KINDS: [&str; 2] = ["sint32", "float"];
/// The producer's count of id-table entries the lab's world does not hold (no live edit reaches them on this build).
pub const NOT_IN_WORLD: &str = "notInWorld";
/// A kind with no entry on the build: recorded, never a gap (a later build that has it must prove it).
pub const NONE_ON_THIS_BUILD: &str = "none on this build";
/// The `missing` texts of impactConfig rows (mod.rs turns them into the record's reasons).
pub const MISSING_SINT32: &str = "impact config: the build has Sint32 configs, but no Sint32 whole round trip and named fractional refusal";
pub const MISSING_FLOAT: &str = "impact config: the build has float configs, but no float fractional round trip";
pub const MISSING_UNKNOWN: &str = "impact config: the lab could not read the config kind of some entries that hold it";
pub const MISSING_NONE: &str = "impact config: no entry of the build holds it";
pub const MISSING_KINDS: &str = "impact config: the lab did not count the config kinds of the build; need a Sint32 whole round trip, a named \
                                 fractional refusal, and a float-config fractional round trip";

#[derive(Debug, Clone, Default, Serialize)]
pub struct Report {
    /// every generation the regmap needs has a suite of this build
    pub complete: bool,
    /// generations without a suite
    pub missing: Vec<String>,
    /// suites set aside (another side or build, an unknown generation)
    pub problems: Vec<String>,
    pub fields: BTreeMap<String, FieldResult>,
}

/// The suites of this side and exe with a known generation; the others are named in `problems`.
pub fn usable<'a>(side: &str, exe_sha256: &str, regmap_digest: &str, suites: &'a [Suite], problems: &mut Vec<String>) -> Vec<&'a Suite> {
    let mut out = Vec::new();
    for s in suites {
        if s.regmap_digest.is_empty() || !s.regmap_digest.eq_ignore_ascii_case(regmap_digest) {
            problems.push(format!("a {} suite of another mapping (regmapDigest {:?}, this proof's regmap/common is {}) was set aside: \
                                   it proves a mapping this proof does not judge", s.generation,
                                  s.regmap_digest.get(..12).unwrap_or(&s.regmap_digest), regmap_digest.get(..12).unwrap_or(regmap_digest)));
        } else if s.side != side {
            problems.push(format!("a {} suite of the {} side was set aside (this is the {side} side's proof)", s.generation, s.side));
        } else if !s.exe_sha256.eq_ignore_ascii_case(exe_sha256) {
            problems.push(format!("a {} suite of another exe ({}) was set aside", s.generation, s.exe_sha256.get(..12).unwrap_or(&s.exe_sha256)));
        } else if s.generation != "live" && s.generation != "boot" {
            problems.push(format!("a suite of generation {:?} was set aside (live or boot)", s.generation));
        } else {
            out.push(s);
        }
    }
    out
}

/// The gear constants a live suite carries (the balance registry's resolved values in the lab).
pub fn constants(suites: &[&Suite]) -> BTreeMap<String, f64> {
    suites.iter().filter(|s| s.generation == "live" && !s.constants.is_empty()).map(|s| s.constants.clone()).next().unwrap_or_default()
}

fn raw_kind(conv: &str) -> Raw {
    match conv {
        "int" | "float" | "impactConfig" | "bool" | "time" | "eventTime" | "enum" | "flags" | "optionalEnum" | "position" | "flag" => Raw::Compared,
        "formula" => Raw::Formula,
        _ => Raw::SchemaOnly,
    }
}

fn num(v: &Value) -> Option<f64> {
    v.as_f64()
}

fn truthy(v: &Value) -> Option<bool> {
    match v {
        Value::Bool(b) => Some(*b),
        Value::Number(n) => n.as_f64().map(|x| x != 0.0),
        _ => None,
    }
}

/// Within float precision (the game keeps most numbers as 32-bit floats).
fn close(a: f64, b: f64) -> bool {
    (a - b).abs() <= 1e-6 * a.abs().max(b.abs()).max(1.0)
}

pub(super) fn same_value(a: &Value, b: &Value) -> bool {
    match (a, b) {
        (Value::Number(x), Value::Number(y)) => x.as_f64().zip(y.as_f64()).is_some_and(|(x, y)| close(x, y)),
        (Value::Array(x), Value::Array(y)) => x.len() == y.len() && x.iter().zip(y).all(|(x, y)| same_value(x, y)),
        (Value::Object(x), Value::Object(y)) => x.len() == y.len() && x.iter().all(|(k, v)| y.get(k).is_some_and(|w| same_value(v, w))),
        _ => a == b,
    }
}

fn names(v: &Value) -> Option<std::collections::BTreeSet<String>> {
    v.as_array()?.iter().map(|x| x.as_str().map(str::to_string)).collect()
}

/// Two Kiln values of a field are the same (a set of flags in any order).
fn same(conv: &str, a: &Value, b: &Value) -> bool {
    if conv == "flags" {
        return names(a).is_some_and(|x| Some(x) == names(b));
    }
    same_value(a, b)
}

/// How the raw member values differ from a Kiln value, per conversion; None when they agree.
fn raw_mismatch(row: &Row, raw: &[Value], kiln: &Value) -> Option<String> {
    let first = raw.first().unwrap_or(&Value::Null);
    if first.is_null() && row.conv != "optionalEnum" { return None; } // missing comparison, not a wrong value
    let agree = match row.conv.as_str() {
        "int" => num(first).is_some() && num(first) == num(kiln),
        "float" | "impactConfig" => num(first).zip(num(kiln)).is_some_and(|(a, b)| close(a, b)),
        "bool" => truthy(first).is_some() && truthy(first) == kiln.as_bool(),
        // eventTime: the first event of a kind (dev.32 abilities.timing.*), a keen::Time like time
        "time" | "eventTime" => num(first).zip(num(kiln)).is_some_and(|(ns, s)| (ns / 1e9 - s).abs() <= 1e-9 * s.abs().max(1.0)),
        // a world position's axis: the game's 32.32 fixed point, the Kiln value in metres (dev.32, world.noBuildZones)
        "position" => num(first).zip(num(kiln)).is_some_and(|(fx, m)| (fx / 4294967296.0 - m).abs() <= 1e-6 * m.abs().max(1.0)),
        "enum" => first.as_str().is_some() && first.as_str() == kiln.as_str().and_then(|k| row.values.get(k)).map(String::as_str),
        "flags" => {
            let want: Option<std::collections::BTreeSet<String>> =
                kiln.as_array().and_then(|a| a.iter().map(|k| k.as_str().and_then(|k| row.values.get(k)).cloned()).collect());
            want.is_some() && names(first) == want
        }
        // one named bit of a bitmask (creatures.tameable): set exactly when the Kiln value is true
        "flag" => match (names(first), kiln.as_bool(), row.values.values().next()) {
            (Some(set), Some(on), Some(bit)) => set.contains(bit) == on,
            _ => false,
        },
        "optionalEnum" => match kiln.as_str() {
            Some("none") => first.is_null() || truthy(first) == Some(false),
            Some(k) => (first.is_null() || truthy(first) == Some(true)) &&
                raw.get(1).is_none_or(|v| v.is_null() || v.as_str() == row.values.get(k).map(String::as_str)),
            None => false,
        },
        _ => true,
    };
    (!agree).then(|| format!("values {} do not give {kiln}", Value::Array(raw.to_vec())))
}

fn show(v: &Option<Value>) -> String {
    v.as_ref().map(Value::to_string).unwrap_or_else(|| "nothing".into())
}

fn sample_problems(row: &Row, x: &Sample, raw: Raw) -> Vec<String> {
    let mut e = if x.entry.is_empty() { "(no entry)".to_string() } else { x.entry.clone() };
    if let Some(i) = x.element {
        e.push_str(&format!("[{i}]"));
    }
    let mut p = Vec::new();
    if let Some(err) = &x.error {
        p.push(format!("{e}: the lab reported an error: {err}"));
        return p;
    }
    let Some(read) = &x.read else {
        p.push(format!("{e}: no value was read through the schema"));
        return p;
    };
    let n = row.paths.len();
    let raw_check = |what: &str, v: &Option<Vec<Value>>, kiln: &Value, p: &mut Vec<String>| match v {
        None => p.push(format!("{e}: no raw values were read {what} the write")),
        Some(v) if v.len() != n => p.push(format!("{e}: {} raw values {what} the write, the row has {n} paths", v.len())),
        Some(v) => {
            if raw == Raw::Compared {
                if let Some(m) = raw_mismatch(row, v, kiln) {
                    p.push(format!("{e}: {what} the write the raw member {m}"));
                }
            }
        }
    };
    raw_check("before", &x.raw_before, read, &mut p);
    if !row.writable() {
        if x.probe.is_some() {
            p.push(format!("{e}: a read-only field was written"));
        }
        return p;
    }
    let Some(probe) = &x.probe else {
        p.push(format!("{e}: no probe value was written"));
        return p;
    };
    if x.expect_refused {
        if row.conv != "impactConfig" || x.config_kind.as_deref() != Some("sint32") ||
            !num(probe).is_some_and(|n| n.is_finite() && n.fract() != 0.0) {
            p.push(format!("{e}: not a fractional Sint32 refusal probe"));
        }
        if !x.refused || (x.refusal_code != "not_exact" || x.refusal_reason.is_empty()) {
            p.push(format!("{e}: a fractional Sint32 value was not refused with a named exactness reason"));
        }
        if x.read_back.as_ref().is_none_or(|b| !same(&row.conv, b, read)) {
            p.push(format!("{e}: the refused probe changed the schema value"));
        }
        raw_check("after", &x.raw_after, read, &mut p);
        if x.raw_before != x.raw_after || x.bytes_before.as_ref().is_none_or(String::is_empty) || x.bytes_before != x.bytes_after {
            p.push(format!("{e}: the refused probe changed the raw value or resource bytes"));
        }
        return p;
    }
    if same(&row.conv, probe, read) {
        p.push(format!("{e}: the probe equals the value before the write: the write proves nothing"));
    }
    match &x.read_back {
        Some(b) if same(&row.conv, b, probe) => {}
        b => p.push(format!("{e}: read back {} after writing {probe}", show(b))),
    }
    raw_check("after", &x.raw_after, probe, &mut p);
    if raw == Raw::Compared {
        if let (Some(before), Some(after)) = (&x.raw_before, &x.raw_after) {
            if before.iter().zip(after).any(|(a, b)| !a.is_null() && b.is_null()) {
                p.push(format!("{e}: a raw member became unreadable after the write"));
            }
        }
    }
    // structured values are proven through Kiln's own mapping both ways: the game's members must at least have moved
    if raw == Raw::SchemaOnly && !same(&row.conv, probe, read) {
        if let (Some(a), Some(b)) = (&x.raw_before, &x.raw_after) {
            if a.len() == n && a == b {
                p.push(format!("{e}: the raw members did not change after a write that changed the value"));
            }
        }
    }
    if x.reverted != Some(true) {
        p.push(format!("{e}: the write was not reverted"));
    }
    match (&x.bytes_before, &x.bytes_after) {
        (Some(a), Some(b)) if !a.is_empty() && a.eq_ignore_ascii_case(b) => {}
        (Some(_), Some(_)) => p.push(format!("{e}: the resource bytes after the revert differ from before")),
        _ => p.push(format!("{e}: the resource bytes were not hashed before the write and after the revert")),
    }
    p
}

/// Every mapped row of the regmap against the suites of this side and exe.
///
/// impactConfig rows are judged against the producer's kind counts, which must account for every entry `values` (the new
/// build's values export) holds the config for (review3 S1).
pub fn check(map: &RegMap, side: &str, exe_sha256: &str, regmap_digest: &str, suites: &[Suite], values: Option<&values::Export>) -> Report {
    let mut r = Report::default();
    let good = usable(side, exe_sha256, regmap_digest, suites, &mut r.problems);
    let rows: Vec<(String, &Row)> = super::regmap::qualified(map).into_iter().filter(|(_, _, _, row)| row.has_mapping()).map(|(q, _, _, row)| (q, row)).collect();
    let needs_boot = rows.iter().any(|(_, row)| row.backend == "boot" || row.backend == "hook");
    for g in ["live", "boot"] {
        if (g == "live" || needs_boot) && !good.iter().any(|s| s.generation == g) {
            r.missing.push(g.to_string());
        }
    }
    r.complete = r.missing.is_empty();
    for (q, row) in rows {
        let want = match row.backend.as_str() {
            // a hooks registry's value (limits): the producer samples Kiln's hook at resources-ready, with the boot fields
            "boot" | "hook" => Some("boot"),
            "live" => Some("live"),
            _ => None,
        };
        let raw = raw_kind(&row.conv);
        let (mut problems, mut samples, mut passed, mut in_generation) = (Vec::new(), 0, 0, false);
        let mut unread = false;
        let (mut sint_whole, mut sint_refusal, mut float_fraction) = (false, false, false);
        for s in &good {
            for x in s.samples.iter().filter(|x| x.field == q) {
                // a member the lab could not read raw: no evidence for the comparison (missing), not a wrong value
                let null = |v: &Option<Vec<Value>>| v.as_ref().is_some_and(|v| v.iter().any(Value::is_null));
                let incomplete = raw == Raw::Compared && (null(&x.raw_before) || null(&x.raw_after));
                unread |= incomplete;
                samples += 1;
                let p = sample_problems(row, x, raw); // still judge read-back, revert, bytes, and readable members
                if p.is_empty() && !incomplete {
                    passed += 1;
                    let right_generation = want.is_none_or(|g| g == s.generation);
                    in_generation |= right_generation;
                    if right_generation && row.conv == "impactConfig" {
                        let fractional = x.probe.as_ref().and_then(num).is_some_and(|n| n.fract() != 0.0);
                        sint_whole |= x.config_kind.as_deref() == Some("sint32") && !fractional && !x.expect_refused;
                        sint_refusal |= x.config_kind.as_deref() == Some("sint32") && fractional && x.expect_refused;
                        float_fraction |= x.config_kind.as_deref() == Some("float") && fractional && !x.expect_refused;
                    }
                }
                problems.extend(p.into_iter().map(|m| format!("{} generation: {m}", s.generation)));
            }
        }
        let where_ = want.map(|g| format!(" in a {g} generation")).unwrap_or_default();
        let mut missing = Vec::new();
        let mut lab_reason = None;
        if !in_generation && unread {
            missing.push(format!("the lab could read no raw value of the field's members{where_}: the field is not proven on this build"));
        } else if samples == 0 {
            missing.push(format!("no round-trip sample{where_}: the field is not proven on this build"));
            lab_reason = good.iter().filter(|s| want.is_none_or(|g| g == s.generation)).find_map(|s| s.unsampled.get(&q)).cloned();
        } else if !in_generation && problems.is_empty() {
            missing.push(format!("no round-trip sample{where_} (only in another generation): the field is not proven on this build"));
        }
        let mut kinds = BTreeMap::new();
        if row.conv == "impactConfig" {
            // the producer's count of the build's entries per config kind, from a suite of the row's generation
            let counts = good.iter().filter(|s| want.is_none_or(|g| g == s.generation)).find_map(|s| Some((s.kinds.get(&q)?, s)));
            match counts {
                None => {
                    if !(sint_whole && sint_refusal && float_fraction) {
                        missing.push(MISSING_KINDS.into());
                    }
                }
                Some((c, suite)) => {
                    let n = |k: &str| c.get(k).copied().unwrap_or(0);
                    // review3 S1: the counts decide "none on this build" only when they classify every entry the values
                    // export holds the config for (in a kind, or not in the lab's world); any other difference is unknown
                    let mut unexplained = 0;
                    match values.and_then(|v| values::holders(v, &q)) {
                        None => missing.push(format!("{MISSING_UNKNOWN} (no values export of this build to check the lab's counts against)")),
                        Some(holders) => {
                            let listed: std::collections::BTreeSet<&str> =
                                suite.not_in_world.get(&q).map(|v| v.iter().map(String::as_str).collect()).unwrap_or_default();
                            let off_world = holders.iter().filter(|h| listed.contains(h.as_str())).count() as u64;
                            let in_world = holders.len() as u64 - off_world;
                            let counted = n("sint32") + n("float") + n("unknown");
                            if listed.len() as u64 != n(NOT_IN_WORLD) {
                                missing.push(format!("{MISSING_UNKNOWN} (the lab counted {} entries outside its world but named {})",
                                                     n(NOT_IN_WORLD), listed.len()));
                            }
                            unexplained = in_world.abs_diff(counted);
                            if unexplained > 0 {
                                missing.push(format!("{MISSING_UNKNOWN} ({unexplained} entries: the values export holds it for {in_world} entries in the \
                                                      lab's world, the lab counted {counted})"));
                            }
                            if off_world > 0 && row.backend != "live" {
                                // a boot edit reaches entries the world has not loaded: there the counts prove nothing
                                missing.push(format!("{MISSING_UNKNOWN} ({off_world} entries outside the lab's world, which a boot edit reaches)"));
                                unexplained += off_world;
                            } else if off_world > 0 {
                                kinds.insert(NOT_IN_WORLD.into(), format!("{off_world} entries not in the lab's world (no edit reaches them on this build)"));
                            }
                        }
                    }
                    let proven = [("sint32", sint_whole && sint_refusal, MISSING_SINT32), ("float", float_fraction, MISSING_FLOAT)];
                    for (kind, have, gap) in proven {
                        let state = if n(kind) == 0 {
                            NONE_ON_THIS_BUILD.to_string()
                        } else if have {
                            format!("proven ({} entries on this build)", n(kind))
                        } else {
                            missing.push(gap.into());
                            format!("not proven ({} entries on this build)", n(kind))
                        };
                        kinds.insert(kind.to_string(), state);
                    }
                    if n("unknown") > 0 {
                        missing.push(format!("{MISSING_UNKNOWN} ({} entries)", n("unknown")));
                    }
                    // review3 S2: a kind kilnctl does not know (a later producer or build) is never proven by the others
                    for (kind, count) in c.iter().filter(|(k, v)| **v > 0 && !CONFIG_KINDS.contains(&k.as_str()) && *k != "unknown" && *k != NOT_IN_WORLD) {
                        missing.push(format!("{MISSING_UNKNOWN} ({count} entries of kind {kind:?}, which kilnctl does not know)"));
                        kinds.insert(kind.clone(), format!("not proven ({count} entries on this build; kilnctl does not know this kind)"));
                    }
                    if n("unknown") + unexplained > 0 {
                        kinds.insert("unknown".into(), format!("{} entries", n("unknown") + unexplained));
                    }
                    if CONFIG_KINDS.iter().all(|k| n(k) == 0) && n("unknown") == 0 {
                        missing.push(MISSING_NONE.into());
                    }
                }
            }
        }
        r.fields.insert(q, FieldResult { ok: problems.is_empty() && missing.is_empty(), samples, passed, raw, problems, missing, kinds, lab_reason });
    }
    r
}

/// The formula samples of a field (raw values as numbers), for formula.rs.
pub fn formula_samples(suites: &[&Suite], field: &str) -> Vec<formula::Sample> {
    let nums = |v: &Option<Vec<Value>>| v.as_ref().map(|v| v.iter().filter_map(Value::as_f64).collect()).unwrap_or_default();
    suites
        .iter()
        .flat_map(|s| s.samples.iter().filter(|x| x.field == field))
        .map(|x| formula::Sample {
            entry: x.entry.clone(),
            read: x.read.as_ref().and_then(Value::as_f64),
            probe: x.probe.as_ref().and_then(Value::as_f64),
            raw_before: nums(&x.raw_before),
            raw_after: nums(&x.raw_after),
        })
        .collect()
}


#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::regproof::regmap::tests::{DIGEST, sample};
    use serde_json::json;

    pub(crate) const SHA: &str = "1111111111111111111111111111111111111111111111111111111111111111";

    /// dev.32: an eventTime row (abilities.timing.*: the first event of a kind) compares its raw keen::Time (nanoseconds) with
    /// the Kiln seconds, as a time row does; it is a compared kind, not schema-only.
    #[test]
    fn event_time_rows_compare_nanoseconds_with_seconds() {
        let row = Row { paths: vec!["subSequences[0].events[keen::actor::SpawnEntityEvent#0].time".into()], conv: "eventTime".into(),
                        backend: "live".into(), ..Default::default() };
        assert_eq!(raw_kind("eventTime"), Raw::Compared);
        assert!(raw_mismatch(&row, &[json!(650000000)], &json!(0.65)).is_none());
        assert!(raw_mismatch(&row, &[json!(900000000)], &json!(0.65)).is_some(), "another event's time does not give the field's");
    }

    /// The check without a values export (the rows these tests judge have no kind counts).
    fn check(map: &RegMap, side: &str, exe_sha256: &str, regmap_digest: &str, suites: &[Suite]) -> Report {
        super::check(map, side, exe_sha256, regmap_digest, suites, None)
    }

    /// A values export where `holders` entries of plans hold `cap` (and one entry does not).
    fn cap_holders(holders: &[&str]) -> super::super::values::Export {
        let mut plans: serde_json::Map<String, Value> = holders.iter().map(|h| (h.to_string(), json!({"cap": 2}))).collect();
        plans.insert("game:capless".into(), json!({"other": 1}));
        super::super::values::tests::export(SHA, "1", "none", json!({"plans": plans}))
    }

    pub(crate) fn good_sample(field: &str) -> Value {
        let bytes = "ab".repeat(32);
        let base = json!({"field": field, "entry": "game:thing", "reverted": true, "bytesBefore": bytes, "bytesAfter": bytes});
        let mut s = base.as_object().unwrap().clone();
        let extra = match field {
            "gadgets.size" => json!({"read": 20, "probe": 21, "readBack": 21, "rawBefore": [20], "rawAfter": [21]}),
            "gadgets.tier" => json!({"read": "low", "probe": "high", "readBack": "high", "rawBefore": ["Low"], "rawAfter": ["High"]}),
            "gadgets.power" => json!({"read": 30, "probe": 44, "readBack": 44, "rawBefore": [1.525, 1.25, 4], "rawAfter": [2.225, 1.25, 4]}),
            "gadgets.label" => json!({"read": true, "rawBefore": [1], "reverted": null, "bytesAfter": null}),
            "plans.parts" => json!({"read": [{"item": "game:a", "count": 2}], "probe": [{"item": "game:a", "count": 3}],
                                    "readBack": [{"item": "game:a", "count": 3}], "rawBefore": [[7]], "rawAfter": [[7, 1]]}),
            "plans.parts[].count" => json!({"element": 0, "read": 2, "probe": 5, "readBack": 5, "rawBefore": [2], "rawAfter": [5]}),
            "plans.cap" => json!({"read": 1.5, "probe": 2.25, "readBack": 2.25, "rawBefore": [1.5], "rawAfter": [2.25]}),
            _ => json!({}),
        };
        for (k, v) in extra.as_object().unwrap() {
            s.insert(k.clone(), v.clone());
        }
        Value::Object(s)
    }

    pub(crate) fn suite(generation: &str, fields: &[&str]) -> Suite {
        let samples: Vec<Value> = fields.iter().map(|f| good_sample(f)).collect();
        parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": generation, "regmapDigest": DIGEST,
                      "constants": {"gear.seed": 10, "gear.creep": 0.15, "gear.armorOffset": 23, "gear.armorGradient": 15},
                      "samples": samples}).to_string()).unwrap()
    }

    pub(crate) fn full() -> Vec<Suite> {
        vec![suite("live", &["gadgets.size", "gadgets.tier", "gadgets.power", "gadgets.label", "plans.parts", "plans.parts[].count"]),
             suite("boot", &["plans.cap"])]
    }

    fn map_with_float_cap() -> RegMap {
        let mut m = sample();
        m.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().conv = "float".into();
        m
    }

    fn edit(suites: &mut [Suite], field: &str, f: impl Fn(&mut Sample)) {
        for s in suites {
            for x in &mut s.samples {
                if x.field == field {
                    f(x);
                }
            }
        }
    }

    #[test]
    fn a_suite_of_another_mapping_is_set_aside() {
        // a lab run with another regmap/common (an older deploy, a row fixed since) proves nothing about this one
        let mut other = full();
        other[0].regmap_digest = "e2".repeat(32);
        other[1].regmap_digest.clear();
        let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &other);
        assert!(!r.complete, "{r:?}");
        assert_eq!(r.problems.len(), 2, "{:?}", r.problems);
        assert!(r.problems.iter().all(|p| p.contains("another mapping")), "{:?}", r.problems);
        assert!(r.fields.values().all(|f| !f.ok));
    }

    #[test]
    fn parses_only_round_trip_suites() {
        assert!(parse(r#"{"format": 1, "kind": "registry-values"}"#).is_err());
        assert!(parse(r#"{"format": 2, "kind": "registry-roundtrip"}"#).is_err());
        assert!(parse("[").is_err());
        assert_eq!(full()[0].samples.len(), 6);
    }

    #[test]
    fn a_complete_suite_proves_every_mapped_field() {
        let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &full());
        assert!(r.complete, "{r:?}");
        for (q, f) in &r.fields {
            assert!(f.ok, "{q}: {f:?}");
        }
        assert_eq!(r.fields.len(), 7, "{:?}", r.fields.keys().collect::<Vec<_>>());
        assert_eq!(r.fields["gadgets.size"].raw, Raw::Compared);
        assert_eq!(r.fields["gadgets.power"].raw, Raw::Formula);
        assert_eq!(r.fields["plans.parts"].raw, Raw::SchemaOnly);
        assert!(!r.fields.contains_key("gadgets.glow"), "an unmapped row is not proven by a lab");
    }

    #[test]
    fn a_missing_generation_or_another_build_fails_closed() {
        let only_live = vec![full().remove(0)];
        let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &only_live);
        assert!(!r.complete);
        assert_eq!(r.missing, ["boot"]);
        assert!(!r.fields["plans.cap"].ok);
        assert!(r.fields["plans.cap"].missing.iter().any(|p| p.contains("boot generation")), "{:?}", r.fields["plans.cap"]);
        assert!(r.fields["plans.cap"].problems.is_empty(), "missing evidence is not a failed step: {:?}", r.fields["plans.cap"]);
        // the boot field proven in a live generation proves nothing about the boot back-end
        let mut wrong = full();
        wrong[1].generation = "live".into();
        assert!(!check(&map_with_float_cap(), "server", SHA, DIGEST, &wrong).fields["plans.cap"].ok);
        // a suite of another exe or side is set aside
        let mut other = full();
        other[0].exe_sha256 = "22".repeat(32);
        other[1].side = "client".into();
        let r2 = check(&map_with_float_cap(), "server", SHA, DIGEST, &other);
        assert!(!r2.complete && r2.problems.len() == 2, "{:?}", r2.problems);
        assert!(r2.fields.values().all(|f| !f.ok));
        // a field without a sample
        let mut thin = full();
        thin[0].samples.retain(|s| s.field != "gadgets.tier");
        let r3 = check(&map_with_float_cap(), "server", SHA, DIGEST, &thin);
        assert!(r3.complete);
        assert!(r3.fields["gadgets.tier"].missing.iter().any(|p| p.contains("no round-trip sample")));
        assert!(!r3.fields["gadgets.tier"].ok && r3.fields["gadgets.tier"].problems.is_empty());
    }

    #[test]
    fn each_failing_step_leaves_the_field_unproven() {
        type Edit = fn(&mut Sample);
        let cases: Vec<(&str, Edit, &str)> = vec![
            ("gadgets.size", |s| s.read_back = Some(json!(20)), "read back"),
            ("gadgets.size", |s| s.raw_after = Some(vec![json!(22)]), "raw"),
            ("gadgets.size", |s| s.raw_before = Some(vec![json!(19)]), "raw"),
            ("gadgets.size", |s| s.bytes_after = Some("cd".repeat(32)), "bytes"),
            ("gadgets.size", |s| s.reverted = Some(false), "revert"),
            ("gadgets.size", |s| s.error = Some("the write was refused".into()), "refused"),
            ("gadgets.size", |s| s.probe = Some(json!(20)), "proves nothing"),
            ("gadgets.size", |s| s.raw_after = None, "raw"),
            ("gadgets.tier", |s| s.raw_after = Some(vec![json!("Low")]), "raw"),
            ("gadgets.label", |s| s.probe = Some(json!(false)), "read-only"),
            ("gadgets.label", |s| s.raw_before = Some(vec![json!(0)]), "raw"),
            ("plans.parts", |s| s.bytes_before = None, "bytes"),
        ];
        for (field, f, word) in cases {
            let mut suites = full();
            edit(&mut suites, field, f);
            let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &suites);
            let x = &r.fields[field];
            assert!(!x.ok, "{field} ({word}) should fail: {x:?}");
            assert!(x.problems.iter().any(|p| p.contains(word)), "{field}: {word:?} not in {:?}", x.problems);
            assert!(r.fields.iter().filter(|(q, _)| q.as_str() != field).all(|(_, o)| o.ok), "only {field} fails");
        }
    }

    #[test]
    fn unreadable_raw_members_do_not_hide_failed_steps() {
        for step in ["readBack", "reverted", "bytes", "became unreadable"] {
            let mut suites = full();
            edit(&mut suites, "gadgets.size", |s| {
                s.raw_before = Some(vec![Value::Null]);
                s.raw_after = Some(vec![Value::Null]);
                match step {
                    "readBack" => s.read_back = Some(json!(999)),
                    "reverted" => s.reverted = Some(false),
                    "bytes" => s.bytes_after = Some("different".into()),
                    _ => s.raw_before = Some(vec![json!(20)]),
                }
            });
            let f = &check(&map_with_float_cap(), "server", SHA, DIGEST, &suites).fields["gadgets.size"];
            assert!(!f.problems.is_empty(), "{step} is a disproof even with unreadable raw values: {f:?}");
        }
    }

    /// The lab could not read a member raw (a null in rawBefore/rawAfter of a compared row): missing evidence, so the
    /// field is unproven, not changed (a wrong value read raw stays a failed step).
    #[test]
    fn a_member_the_lab_could_not_read_raw_leaves_the_field_unproven() {
        let mut suites = full();
        edit(&mut suites, "gadgets.size", |s| s.raw_before = Some(vec![Value::Null]));
        let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &suites);
        let f = &r.fields["gadgets.size"];
        assert!(!f.ok && f.problems.is_empty(), "{f:?}");
        assert!(f.missing.iter().any(|m| m.contains("no raw value")), "{f:?}");
        edit(&mut suites, "gadgets.size", |s| s.raw_before = Some(vec![json!(19)]));
        assert!(!check(&map_with_float_cap(), "server", SHA, DIGEST, &suites).fields["gadgets.size"].problems.is_empty());
    }

    /// A list or reference row is not compared value by value, so a write through a wrong conversion and a read back
    /// through the same one would pass: the game's members must at least have changed.
    #[test]
    fn a_schema_only_write_must_change_the_raw_members() {
        let mut suites = full();
        edit(&mut suites, "plans.parts", |s| s.raw_after = s.raw_before.clone());
        let r = check(&map_with_float_cap(), "server", SHA, DIGEST, &suites);
        assert!(!r.fields["plans.parts"].ok);
        assert!(r.fields["plans.parts"].problems.iter().any(|p| p.contains("did not change")), "{:?}", r.fields["plans.parts"]);
    }

    #[test]
    fn floats_compare_within_float_precision_and_formulas_go_to_formula_rs() {
        let mut suites = full();
        edit(&mut suites, "plans.cap", |s| s.raw_after = Some(vec![json!(2.25f32 as f64 + 1e-8)]));
        assert!(check(&map_with_float_cap(), "server", SHA, DIGEST, &suites).fields["plans.cap"].ok);
        edit(&mut suites, "plans.cap", |s| s.raw_after = Some(vec![json!(2.3)]));
        assert!(!check(&map_with_float_cap(), "server", SHA, DIGEST, &suites).fields["plans.cap"].ok);
        let owned = full();
        let mut why = Vec::new();
        let all = usable("server", SHA, DIGEST, &owned, &mut why);
        assert_eq!((all.len(), why.len()), (2, 0));
        assert_eq!(constants(&all)["gear.creep"], 0.15);
        let fs = formula_samples(&all, "gadgets.power");
        assert_eq!(fs, [formula::Sample { entry: "game:thing".into(), read: Some(30.0), probe: Some(44.0),
                                         raw_before: vec![1.525, 1.25, 4.0], raw_after: vec![2.225, 1.25, 4.0] }]);
    }

    #[test]
    fn impact_configs_require_whole_fractional_and_refusal_evidence() {
        let mut map = map_with_float_cap();
        map.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().conv = "impactConfig".into();
        map.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().backend = "live".into();
        let bytes = "ab".repeat(32);
        let mk = |extra: Value| {
            let mut s = json!({"field": "plans.cap", "entry": "game:cost", "read": 2, "rawBefore": [2],
                               "reverted": true, "bytesBefore": bytes, "bytesAfter": bytes});
            s.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
            s
        };
        let whole = mk(json!({"configKind": "sint32", "probe": 3, "readBack": 3, "rawAfter": [3]}));
        let float = mk(json!({"configKind": "float", "probe": 2.5, "readBack": 2.5, "rawAfter": [2.5]}));
        let refused = mk(json!({"configKind": "sint32", "probe": 2.5, "expectRefused": true, "refused": true,
                                "refusalReason": "not_exact: value cannot be represented exactly", "refusalCode": "not_exact", "readBack": 2, "rawAfter": [2]}));
        let run = |samples: Vec<Value>| {
            let suite = parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA,
                                     "generation": "live", "regmapDigest": DIGEST, "samples": samples}).to_string()).unwrap();
            check(&map, "server", SHA, DIGEST, &[suite]).fields.remove("plans.cap").unwrap()
        };
        assert!(!run(vec![whole.clone()]).ok, "a whole-only probe hides Sint32 rounding");
        assert!(run(vec![whole.clone(), float.clone(), refused.clone()]).ok);
        let mut rounded = refused.clone();
        rounded["refused"] = json!(false);
        rounded["readBack"] = json!(2);
        assert!(!run(vec![whole.clone(), float.clone(), rounded]).problems.is_empty(), "accepting a fraction is a disproof");
        let mut wrong_code = refused.clone();
        wrong_code["refusalCode"] = json!("unproven");
        assert!(!run(vec![whole.clone(), float.clone(), wrong_code]).ok);
        let mut unnamed = refused;
        unnamed["refusalReason"] = json!("");
        assert!(!run(vec![whole, float, unnamed]).ok, "refusals must name their reason");
    }

    /// Coordinator ruling (2026-11-23 ~1:20 PM): evidence is required only for the config kinds the build has; a kind with
    /// no entry is recorded "none on this build" and does not block the row.
    #[test]
    fn impact_configs_need_evidence_only_for_the_kinds_the_build_has() {
        let mut map = map_with_float_cap();
        map.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().conv = "impactConfig".into();
        map.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().backend = "live".into();
        let bytes = "ab".repeat(32);
        let mk = |extra: Value| {
            let mut s = json!({"field": "plans.cap", "entry": "game:cost", "read": 2, "rawBefore": [2],
                               "reverted": true, "bytesBefore": bytes, "bytesAfter": bytes});
            s.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
            s
        };
        let whole = mk(json!({"configKind": "sint32", "probe": 3, "readBack": 3, "rawAfter": [3]}));
        let float = mk(json!({"configKind": "float", "probe": 2.5, "readBack": 2.5, "rawAfter": [2.5]}));
        let refused = mk(json!({"configKind": "sint32", "probe": 2.5, "expectRefused": true, "refused": true,
                                "refusalReason": "not_exact: value cannot be represented exactly", "refusalCode": "not_exact", "readBack": 2, "rawAfter": [2]}));
        let run = |samples: Vec<Value>, kinds: Value| {
            // the values export holds as many entries as the lab counted (review3 S1: the counts must cover it)
            let n: u64 = kinds.as_object().unwrap().values().filter_map(Value::as_u64).sum();
            let names: Vec<String> = (0..n).map(|i| format!("game:e{i}")).collect();
            let values = cap_holders(&names.iter().map(String::as_str).collect::<Vec<_>>());
            let suite = parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": "live",
                                     "regmapDigest": DIGEST, "kinds": {"plans.cap": kinds}, "samples": samples}).to_string()).unwrap();
            super::check(&map, "server", SHA, DIGEST, &[suite], Some(&values)).fields.remove("plans.cap").unwrap()
        };
        // a build with Sint32 configs only (f0d824be): the float kind is recorded, not required
        let f = run(vec![whole.clone(), refused.clone()], json!({"sint32": 141, "float": 0, "unknown": 0}));
        assert!(f.ok, "{f:?}");
        assert_eq!(f.kinds["float"], NONE_ON_THIS_BUILD);
        assert!(f.kinds["sint32"].starts_with("proven"), "{f:?}");
        // the build has float configs too: their fractional round trip is required
        let f = run(vec![whole.clone(), refused.clone()], json!({"sint32": 141, "float": 3, "unknown": 0}));
        assert!(!f.ok && f.problems.is_empty() && f.missing.contains(&MISSING_FLOAT.to_string()), "{f:?}");
        // a float-only build needs no Sint32 evidence
        let f = run(vec![float.clone()], json!({"sint32": 0, "float": 4}));
        assert!(f.ok && f.kinds["sint32"] == NONE_ON_THIS_BUILD, "{f:?}");
        // Sint32 configs present: the whole round trip alone does not prove the row (the refusal is required)
        let f = run(vec![whole.clone()], json!({"sint32": 2, "float": 0}));
        assert!(!f.ok && f.missing.contains(&MISSING_SINT32.to_string()), "{f:?}");
        // entries whose config kind the lab could not read: never assumed absent
        let f = run(vec![whole.clone(), refused.clone()], json!({"sint32": 141, "float": 0, "unknown": 3}));
        assert!(!f.ok && f.missing.iter().any(|m| m.starts_with(MISSING_UNKNOWN)), "{f:?}");
        // no entry holds the config: nothing proves it
        let f = run(vec![], json!({"sint32": 0, "float": 0}));
        assert!(!f.ok && f.missing.contains(&MISSING_NONE.to_string()), "{f:?}");
        // a suite without counts: every kind is required, as before
        let suite = parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": "live",
                                  "regmapDigest": DIGEST, "samples": [whole, refused]}).to_string()).unwrap();
        let f = check(&map, "server", SHA, DIGEST, &[suite]).fields.remove("plans.cap").unwrap();
        assert!(!f.ok && f.missing.contains(&MISSING_KINDS.to_string()), "{f:?}");
    }

    /// review3 S1 (coordinator ruling): the counts never decide "none on this build" over a count with gaps. Every entry the
    /// values export holds the config for is classified: counted in a kind, or named by the producer as not in the lab's
    /// world (no live edit reaches it: recorded with its count, never blocking); any other difference is unknown and blocks.
    #[test]
    fn every_entry_that_holds_an_impact_config_is_classified() {
        let mut map = map_with_float_cap();
        let cap = map.get_mut("plans").unwrap().rows.get_mut("cap").unwrap();
        cap.conv = "impactConfig".into();
        cap.backend = "live".into();
        let bytes = "ab".repeat(32);
        let mk = |extra: Value| {
            let mut s = json!({"field": "plans.cap", "entry": "game:h0", "read": 2, "rawBefore": [2],
                               "reverted": true, "bytesBefore": bytes, "bytesAfter": bytes});
            s.as_object_mut().unwrap().extend(extra.as_object().unwrap().clone());
            s
        };
        let samples = vec![mk(json!({"configKind": "sint32", "probe": 3, "readBack": 3, "rawAfter": [3]})),
                           mk(json!({"configKind": "sint32", "probe": 2.5, "expectRefused": true, "refused": true, "refusalCode": "not_exact",
                                     "refusalReason": "not_exact: plans.cap is a whole number on this build", "readBack": 2, "rawAfter": [2]}))];
        // five entries hold the config; h3 and h4 are in the build's id table but not in the lab's world
        let values = cap_holders(&["game:h0", "game:h1", "game:h2", "game:h3", "game:h4"]);
        let run = |map: &RegMap, generation: &str, kinds: Value, not_in_world: Option<Value>, values: Option<&super::super::values::Export>| {
            let mut doc = json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": generation,
                                 "regmapDigest": DIGEST, "kinds": {"plans.cap": kinds}, "samples": samples.clone()});
            if let Some(n) = not_in_world {
                doc["notInWorld"] = json!({"plans.cap": n});
            }
            super::check(map, "server", SHA, DIGEST, &[parse(&doc.to_string()).unwrap()], values).fields.remove("plans.cap").unwrap()
        };
        let unknown = |f: &FieldResult| !f.ok && f.problems.is_empty() && f.missing.iter().any(|m| m.starts_with(MISSING_UNKNOWN));
        // 3 counted in the world + 2 not in it (the producer names 3 entries outside the world; one holds no config): ok
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 3}),
                    Some(json!(["game:h3", "game:h4", "game:capless"])), Some(&values));
        assert!(f.ok, "{f:?}");
        assert_eq!(f.kinds["float"], NONE_ON_THIS_BUILD);
        assert!(f.kinds["notInWorld"].starts_with("2 entries"), "recorded with their count: {f:?}");
        // the old producer (no notInWorld): the 2 entries it skipped are unaccounted for, so unknown
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0}), None, Some(&values));
        assert!(unknown(&f), "{f:?}");
        assert!(f.kinds["unknown"].starts_with("2 entries"), "{f:?}");
        // a count over the values export is no proof either
        let f = run(&map, "live", json!({"sint32": 4, "float": 0, "unknown": 0, "notInWorld": 2}), Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(unknown(&f), "{f:?}");
        // a notInWorld count that is not the entries it names
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 5}), Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(unknown(&f), "{f:?}");
        // the "none on this build" of a kind: only over a complete count (the float holders the lab skipped stay unknown)
        let f = run(&map, "live", json!({"sint32": 1, "float": 0, "unknown": 0, "notInWorld": 2}), Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(unknown(&f), "{f:?}");
        // no values export to compare with
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 2}), Some(json!(["game:h3", "game:h4"])), None);
        assert!(unknown(&f), "{f:?}");
        // a boot edit reaches entries outside the world: there they are not proven
        let mut boot = map.clone();
        boot.get_mut("plans").unwrap().rows.get_mut("cap").unwrap().backend = "boot".into();
        let f = run(&boot, "boot", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 2}), Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(unknown(&f), "{f:?}");
        // review3 S2: a kind kilnctl does not know is never proven (the counts alone match the export here)
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 2, "uint32": 1}),
                    Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(unknown(&f) && f.missing.iter().any(|m| m.contains("uint32")), "{f:?}");
        assert!(f.kinds["uint32"].starts_with("not proven"), "{f:?}");
        // a known name with no entry is no problem
        let f = run(&map, "live", json!({"sint32": 3, "float": 0, "unknown": 0, "notInWorld": 2, "uint32": 0}),
                    Some(json!(["game:h3", "game:h4"])), Some(&values));
        assert!(f.ok, "{f:?}");
    }

    // dev.32 TAME review: a flag row (creatures.tameable) is one bit of a bitmask, read-only: the lab's read is compared
    // with the bit in the raw bitmask
    #[test]
    fn a_flag_row_compares_its_one_bit_raw() {
        let rows = r#"{"regmap": 1, "registry": "kit",
            "entries": {"kind": "resource", "resourceType": "demo::Kit"},
            "rows": {"tame": {"paths": ["mask"], "conv": "flag", "read": "now", "backend": "none", "values": {"tame": "Tameable"}}}}"#;
        let mut map = RegMap::new();
        map.insert("kit".into(), crate::regproof::regmap::parse(rows, "kit").unwrap());
        let mk = |samples: Value| parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": "live",
                                                "regmapDigest": DIGEST, "samples": samples}).to_string()).unwrap();
        let ok = mk(json!([
            {"field": "kit.tame", "entry": "game:dog", "read": true, "rawBefore": [["Roaming", "Tameable"]]},
            {"field": "kit.tame", "entry": "game:deer", "read": false, "rawBefore": [["Roaming"]]}]));
        let r = check(&map, "server", SHA, DIGEST, &[ok]);
        assert!(r.fields["kit.tame"].ok, "{:?}", r.fields["kit.tame"]);
        let bad = mk(json!([{"field": "kit.tame", "entry": "game:deer", "read": true, "rawBefore": [["Roaming"]]}]));
        let r2 = check(&map, "server", SHA, DIGEST, &[bad]);
        assert!(!r2.fields["kit.tame"].ok, "a read true over a bitmask without the bit must fail: {:?}", r2.fields["kit.tame"]);
    }

    #[test]
    fn a_position_compares_its_fixed_point_raw_value_in_metres() {
        let rows = r#"{"regmap": 1, "registry": "zones", "entries": {"kind": "single", "resourceType": "demo::Scene", "resourceGuid": "509feadb-4c60-425f-9c7c-deeefd9b6920"},
            "rows": {"boxes": {"paths": ["boxes"], "conv": "list", "read": "worldLoad", "backend": "boot", "resize": true},
                     "boxes[].min.x": {"paths": ["lo.x"], "conv": "position", "read": "worldLoad", "backend": "boot"}}}"#;
        let mut map = RegMap::new();
        map.insert("zones".into(), crate::regproof::regmap::parse(rows, "zones").unwrap());
        let b = "ef".repeat(32);
        let mk = |raw_after: i64| parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": "boot", "regmapDigest": DIGEST,
            "samples": [{"field": "zones.boxes[].min.x", "entry": "game:world", "element": 0, "read": -364.0, "probe": -455.0, "readBack": -455.0,
                         "rawBefore": [-364i64 * 4294967296], "rawAfter": [raw_after], "reverted": true, "bytesBefore": b, "bytesAfter": b}]}).to_string()).unwrap();
        let r = check(&map, "server", SHA, DIGEST, &[mk(-455i64 * 4294967296)]);
        assert!(r.fields["zones.boxes[].min.x"].ok, "{:?}", r.fields["zones.boxes[].min.x"]);
        let r2 = check(&map, "server", SHA, DIGEST, &[mk(-455)]);  // the metres written as a plain number: a disproof
        assert!(!r2.fields["zones.boxes[].min.x"].ok, "{:?}", r2.fields["zones.boxes[].min.x"]);
    }

    #[test]
    fn a_hook_row_is_proven_by_a_boot_generation_sample() {
        // a hooks registry's value (limits, dev.32): the producer samples Kiln's hook at resources-ready, in the boot generation
        let rows = r#"{"regmap": 1, "registry": "caps", "entries": {"kind": "hooks", "unnamed": "caps"},
            "rows": {"most": {"paths": ["altarCap"], "conv": "int", "read": "now", "backend": "hook"}}}"#;
        let mut map = RegMap::new();
        map.insert("caps".into(), crate::regproof::regmap::parse(rows, "caps").unwrap());
        assert!(map["caps"].rows["most"].writable());
        let b = "cd".repeat(32);
        let mk = |generation: &str, samples: Value| parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": generation,
                                                                  "regmapDigest": DIGEST, "samples": samples}).to_string()).unwrap();
        let sample = json!([{"field": "caps.most", "entry": "game:caps", "read": 16, "probe": 17, "readBack": 17, "rawBefore": [16], "rawAfter": [17],
                             "reverted": true, "bytesBefore": b, "bytesAfter": b}]);
        let r = check(&map, "server", SHA, DIGEST, &[mk("boot", sample.clone())]);
        assert!(r.fields["caps.most"].ok, "{:?}", r.fields["caps.most"]);
        // the same sample in a live generation proves nothing: the value is taken at start
        let r2 = check(&map, "server", SHA, DIGEST, &[mk("live", sample)]);
        assert!(!r2.fields["caps.most"].ok, "{:?}", r2.fields["caps.most"]);
        // the game's function answering the old value after the probe is a disproof
        let bad = json!([{"field": "caps.most", "entry": "game:caps", "read": 16, "probe": 17, "readBack": 16, "rawBefore": [16], "rawAfter": [16],
                          "reverted": true, "bytesBefore": b, "bytesAfter": b}]);
        let r3 = check(&map, "server", SHA, DIGEST, &[mk("boot", bad)]);
        assert!(!r3.fields["caps.most"].ok, "{:?}", r3.fields["caps.most"]);
    }

    #[test]
    fn time_flags_and_optional_enums_compare_raw() {
        let rows = r#"{"regmap": 1, "registry": "kit",
            "entries": {"kind": "resource", "resourceType": "demo::Kit"},
            "rows": {
              "burn":  {"paths": ["burnTime"], "conv": "time", "read": "now", "backend": "live"},
              "kinds": {"paths": ["mask"], "conv": "flags", "read": "now", "backend": "live", "values": {"a": "A", "b": "B"}},
              "fixed": {"paths": ["on", "which"], "conv": "optionalEnum", "read": "now", "backend": "live", "values": {"none": "", "a": "A", "b": "B"}}}}"#;
        let mut map = RegMap::new();
        map.insert("kit".into(), crate::regproof::regmap::parse(rows, "kit").unwrap());
        let b = "ab".repeat(32);
        let mk = |samples: Value| parse(&json!({"format": 1, "kind": KIND, "side": "server", "exeSha256": SHA, "generation": "live",
                                                "regmapDigest": DIGEST, "samples": samples}).to_string()).unwrap();
        let ok = mk(json!([
            {"field": "kit.burn", "read": 1.5, "probe": 2, "readBack": 2, "rawBefore": [1500000000i64], "rawAfter": [2000000000i64], "reverted": true, "bytesBefore": b, "bytesAfter": b},
            {"field": "kit.kinds", "read": ["a"], "probe": ["a", "b"], "readBack": ["b", "a"], "rawBefore": [["A"]], "rawAfter": [["B", "A"]], "reverted": true, "bytesBefore": b, "bytesAfter": b},
            {"field": "kit.fixed", "read": "none", "probe": "b", "readBack": "b", "rawBefore": [false, "A"], "rawAfter": [true, "B"], "reverted": true, "bytesBefore": b, "bytesAfter": b}]));
        let r = check(&map, "server", SHA, DIGEST, &[ok]);
        assert_eq!(r.fields.len(), 3);
        for (q, f) in &r.fields {
            assert!(f.ok, "{q}: {f:?}");
        }
        let bad = mk(json!([
            {"field": "kit.burn", "read": 1.5, "probe": 2, "readBack": 2, "rawBefore": [1500000000], "rawAfter": [2000000001000i64], "reverted": true, "bytesBefore": b, "bytesAfter": b},
            {"field": "kit.kinds", "read": ["a"], "probe": ["a", "b"], "readBack": ["a", "b"], "rawBefore": [["A"]], "rawAfter": [["A"]], "reverted": true, "bytesBefore": b, "bytesAfter": b},
            {"field": "kit.fixed", "read": "none", "probe": "b", "readBack": "b", "rawBefore": [true, "A"], "rawAfter": [true, "B"], "reverted": true, "bytesBefore": b, "bytesAfter": b}]));
        let r2 = check(&map, "server", SHA, DIGEST, &[bad]);
        assert_eq!(r2.fields.len(), 3);
        for (q, f) in &r2.fields {
            assert!(!f.ok, "{q} should fail: {f:?}");
        }
    }
}
