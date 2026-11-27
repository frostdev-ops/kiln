// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: the startup cache of the ShroudForge dialect: `kiln/eml/cache/sf-startup-<stem>.json`, one record
//! per archive, written through mod-loader-lua's `StartupRecord` (atomic replace, only while the install guard is held).
//!
//! - **Key** (canonical JSON, sorted keys): the record format, the pass schema and kiln-eml's version; the dialect and
//!   the archive (stem: the side); the executable's SHA-256; the ordered accepted set (`{id, version, contentSha256,
//!   settingsSha256}` per admitted package, in load order); the native identity (each admitted DLL package's terminal
//!   outcome kinds); the working baseline (each backup's size and SHA-256); the pass's features; the frozen locale
//!   (`shroudforge.locale` answers it, so a package's startup data may depend on it) and the runtime catalog's game
//!   build (the runtime registrar's static answers).
//! - **Value**: the outcome rows of the admitted packages (committed, failed, dependency-skipped), the receipt's
//!   SHA-256, the attempt, the first and the retry's error, and the warnings. A retried success is cached with the full
//!   key (the accepted set before the retry), so its warning repeats every start. Format 2: the
//!   SHA-256 of the generation's startup change journal (`sf_startup::journal`), written before this record.
//! - **Hit**: the key is equal, nothing exports (an export cannot be replayed), and the generation the value names is
//!   still live: the receipt's bytes are the recorded ones, its header is the one the start witnessed and every file
//!   it lists has its size (`startup_host::vouch_live`; nothing large is read); and its change journal is the recorded
//!   one byte for byte, so a hit keeps the journal of the generation it replays (a missing or other journal is a miss:
//!   the pass runs again and journals its publication).
//! - **Never written** after a failed preparation or publication, or a changed input (the startup controller writes it only
//!   once its one publication returned and the generation was read back). A record that cannot be read is a miss.
use std::collections::BTreeMap;

use mod_loader::lua::startup_host::{self, BaselineIdentity, InputWitness, StartupJournal, StartupRecord};
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};

use crate::{
    sf_pass::StartupDialect,
    sf_startup::plan::Features,
    startup::{Attempt, Data, PlanPackage, Row, Warning, dialect_word, hex},
};

/// The record's format; a new key or value shape gets a new number (2: the change journal's SHA-256; 3, 0.2.0-dev.32: the
/// journal it names is format 2 with the member paths each package changed, so a record of format 2 misses once and the
/// pass journals its publication anew).
const FORMAT: u64 = 3;
/// The SF pass's own schema: what one pass over the same key produces.
const PASS_SCHEMA: u64 = 1;

/// The cache key of one start (canonical JSON text).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Key(String);

impl Key {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        dialect: StartupDialect,
        stem: &str,
        exe_sha256: &str,
        accepted: &[&PlanPackage],
        dlls: &BTreeMap<String, Vec<String>>,
        baseline: &BaselineIdentity,
        features: Features,
        frozen: (Option<&str>, &str),
    ) -> Self {
        let (locale, build) = frozen;
        let accepted: Vec<Value> = accepted
            .iter()
            .map(|p| {
                json!({"id": p.id, "version": p.version, "contentSha256": hex(&p.content_sha256),
                       "settingsSha256": hex(&p.config_sha256)})
            })
            .collect();
        let ids: Vec<&str> = accepted.iter().filter_map(|a| a["id"].as_str()).collect();
        let native: BTreeMap<&str, &Vec<String>> = dlls
            .iter()
            .filter(|(id, _)| ids.contains(&id.as_str()))
            .map(|(id, kinds)| (id.as_str(), kinds))
            .collect();
        let baseline: BTreeMap<&str, Value> = baseline
            .files()
            .map(|(ext, d)| {
                (
                    ext,
                    d.map_or(Value::Null, |(size, sha)| json!({"size": size, "sha256": sha})),
                )
            })
            .collect();
        Self::canonical(json!({
            "format": FORMAT, "passSchema": PASS_SCHEMA, "kilnEml": crate::VERSION,
            "dialect": dialect_word(dialect), "stem": stem, "exeSha256": exe_sha256,
            "accepted": accepted, "native": native, "baseline": baseline,
            "features": {"patch": features.patch, "export": features.export},
            "locale": locale, "gameBuild": build,
        }))
    }
    fn canonical(mut value: Value) -> Self {
        value.sort_all_objects();
        Self(serde_json::to_string(&value).unwrap_or_default())
    }
}

/// What a hit replays.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Hit {
    pub receipt: String,
    pub attempt: Attempt,
    pub error: Option<String>,
    pub retry_error: Option<String>,
    pub rows: BTreeMap<String, Row>,
    pub warnings: Vec<Warning>,
}

/// The record's value for `key` when it is a hit (see the module docs); None on any miss.
pub fn lookup(
    record: &StartupRecord<'_>,
    journal: &StartupJournal<'_>,
    key: &Key,
    witness: &InputWitness,
    features: Features,
) -> Option<Hit> {
    if features.export {
        return None;
    }
    let bytes = record.read().ok()??;
    let doc: Value = serde_json::from_slice(&bytes).ok()?;
    if doc.get("format")?.as_u64()? != FORMAT || Key::canonical(doc.get("key")?.clone()) != *key {
        return None;
    }
    let value = doc.get("value")?;
    let text = |v: &Value| v.as_str().map(str::to_owned);
    let receipt = text(value.get("receiptSha256")?)?;
    // the generation keeps its change journal: the very bytes recorded with it
    let journaled = journal.read().ok()??;
    if format!("{:x}", Sha256::digest(&journaled)) != value.get("journalSha256")?.as_str()? {
        return None;
    }
    let rows = value
        .get("rows")?
        .as_array()?
        .iter()
        .map(|r| {
            let row = Row {
                id: text(r.get("id")?)?,
                version: text(r.get("version")?),
                data: Data::parse(r.get("data")?.as_str()?)?,
                archive_present: r.get("archivePresent")?.as_bool(),
                code: r.get("code").and_then(text),
                text: r.get("text").and_then(text),
                chain: r.get("chain")?.as_array()?.iter().map(text).collect::<Option<_>>()?,
            };
            Some((row.id.clone(), row))
        })
        .collect::<Option<BTreeMap<_, _>>>()?;
    let warnings = value
        .get("warnings")?
        .as_array()?
        .iter()
        .map(|w| {
            Some(Warning {
                code: text(w.get("code")?)?,
                text: text(w.get("text")?)?,
            })
        })
        .collect::<Option<Vec<_>>>()?;
    let hit = Hit {
        attempt: Attempt::parse(value.get("attempt")?.as_str()?)?,
        error: text(value.get("error")?),
        retry_error: text(value.get("retryError")?),
        rows,
        warnings,
        receipt,
    };
    startup_host::vouch_live(witness, &hit.receipt).ok()?.then_some(hit)
}

/// Records this start's generation for `key` (only after its one publication returned and it was read back).
#[allow(clippy::too_many_arguments)]
pub fn store(
    record: &StartupRecord<'_>,
    key: &Key,
    receipt: &str,
    attempt: Attempt,
    error: &Option<String>,
    retry_error: &Option<String>,
    rows: &[Row],
    warnings: &[Warning],
    journal_sha256: &str,
) -> std::io::Result<()> {
    let key: Value = serde_json::from_str(&key.0).map_err(std::io::Error::other)?;
    let rows: Vec<Value> = rows
        .iter()
        .map(|r| {
            json!({"id": r.id, "version": r.version, "data": r.data.word(), "archivePresent": r.archive_present,
                   "code": r.code, "text": r.text, "chain": r.chain})
        })
        .collect();
    let warnings: Vec<Value> = warnings
        .iter()
        .map(|w| json!({"code": w.code, "text": w.text}))
        .collect();
    let mut doc = json!({
        "format": FORMAT,
        "key": key,
        "value": {"receiptSha256": receipt, "attempt": attempt.word(), "error": error, "retryError": retry_error,
                  "rows": rows, "warnings": warnings, "journalSha256": journal_sha256},
    });
    doc.sort_all_objects();
    record.write(&serde_json::to_vec(&doc).map_err(std::io::Error::other)?)
}
