// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: the startup controller of a kiln-mode start, as a library (no plugin wiring and no ABI: the plugin wiring
//! wires it into the plugin and the kiln.eml.startup seam, kiln-sfi's outcome reader reads its outcome in kiln-sfi).
//!
//! Two steps, both on the boot main thread, both under the install lock the caller holds for the process's life:
//! - [`witness`], in kiln-eml's early phase BEFORE any package DLL loads: the archive inspector's own classification
//!   (the inspector, so the boot decision and the pre-Play inspection agree), plan freshness, the working baseline (captured or
//!   refreshed here, never later), its BASE origin, then the stamped input witness. A refusal means no DLL
//!   loads and no plan package runs.
//! - [`run`], in kiln-eml's early on_ready once the native side concluded: the admission set, the dialect's pass (the
//!   ShroudForge dialect with its one retry, the EML dialect, or no pass), ONE publication through the sole publisher
//!   (or a verified restore of the BASE when no plan contribution was committed), the published generation's change
//!   journal, the startup cache, and the [`StartupOutcome`] with the live generation's grant inputs
//!   ([`LiveGeneration`]).
//!
//! Every attempt opens its host with `StartupHost::open_matching` over the start's witness: an input changed since the
//! witness (a package DLL wrote the game's files) is never published, retried or recaptured. The install lock is never
//! released or taken again in between, no DLL is initialized again, ShroudForge's folder is never touched
//! (`SfHandoff::Deferred`: the handoff it owes is recorded instead), ShroudForge's startup lease is never taken, and
//! nothing here writes eml.json or the seam. The VM, its `Rc` handles and the hosts never leave the calling
//! thread; only [`Witnessed`] is shared, read-only.
//!
//! The SF-dialect parts are adapted from ShroudForge (Kiln's fork at 2df1de68, VERSION 1.7.9; MIT, Copyright (c) 2026
//! bonsaibauer, notice in sf_pass.rs and sf_startup/mod.rs): the one retry and its record (sf_pass.rs), the retry's
//! re-planned order (sf_startup/plan.rs `fork_order`), and a double failure starting the game without the pregame
//! pass's mods (apply_asset_mods.rs; here a verified restore of the BASE, F9).
use std::{
    collections::BTreeMap,
    fs::File,
    panic::{AssertUnwindSafe, catch_unwind},
    path::Path,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
    time::Duration,
};

use camino::Utf8Path;
use kfc::{container::KFCFile, reflection::TypeRegistry};
use kiln_abi::services::kiln_eml_archive::KilnEmlArchiveIdentityV1;
use mod_loader::{
    ModEnvironment,
    lua::startup_host::{self, BaseOrigin, InputWitness, SfHandoff, StartupJournal, StartupRecord},
};
use sfi_core::package::discover::Registry;
use sha2::{Digest as _, Sha256};

use crate::{
    archive_service::{ArchiveBacking, RegistrySnapshot},
    inspection,
    sf_pass::StartupDialect,
    sf_startup::{
        admission, cache,
        journal::{self, Journal},
        plan::{Features, RuntimeCatalog, StartupPlan},
        retry,
        runner::{self, Step},
    },
};

/// The startup dialect a launch plan froze (`startupDialect`).
pub type Dialect = StartupDialect;

/// The launch this start is: kiln.launch's launch id and plan hash, and the plan's id (RFC 4122 byte order).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LaunchKey {
    pub launch_id: u64,
    pub plan_sha256: [u8; 32],
    pub plan_id: [u8; 16],
}

/// A package's `kind` in the launch plan.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PackageKind {
    Shroudforge,
    Eml,
    NativeSelection,
}

/// One `dependencies[]` record of a plan package.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlanDependency {
    pub id: String,
    pub optional: bool,
    /// The target is an admitted candidate ordered before the package.
    pub included: bool,
}

/// One package of the frozen launch plan, the members the startup controller uses.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlanPackage {
    pub id: String,
    pub version: String,
    pub kind: PackageKind,
    /// Game-relative `mods/<one folder>`.
    pub root: String,
    /// The package folder's content digest when the plan was written (sfi-policy's `package_content_sha256`).
    pub content_sha256: [u8; 32],
    /// Its settings document's digest (`config_sha256`).
    pub config_sha256: [u8; 32],
    /// `decision: "candidate"`: Kiln admits it this start.
    pub candidate: bool,
    /// `data.required`: it has startup data (patch, or export without the runtime).
    pub data_required: bool,
    /// It ships native code (a planned DLL, or the capability to register one).
    pub native: bool,
    pub dependencies: Vec<PlanDependency>,
}

/// What the startup controller reads of the frozen launch plan. The plugin wiring fills it from sfi-policy's `LaunchPlan` once kiln.launch's
/// plan hash matched; the plan's own validation is sfi-policy's (this slice adds no MIT policy crate to kiln-eml).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlanFacts {
    pub plan_id: [u8; 16],
    pub dialect: Dialect,
    /// `game.exe_sha256`, `game.archiveBase_sha256`, `game.archiveInput_sha256` (lower-case hex).
    pub exe_sha256: String,
    pub archive_base_sha256: String,
    pub archive_input_sha256: String,
    /// The launch plan's `game.archiveBaseOrigin` token, when the plan has one; before that the boot decision stands.
    pub archive_base_origin: Option<String>,
    /// In the plan's listing order.
    pub packages: Vec<PlanPackage>,
    /// `loadOrder`: every candidate once, dependencies first.
    pub load_order: Vec<String>,
}
impl PlanFacts {
    pub fn package(&self, id: &str) -> Option<&PlanPackage> {
        self.packages.iter().find(|p| p.id == id)
    }
    /// The candidates the dialect's pass registers, in load order: ShroudForge's pass registers every candidate of
    /// either kind (its EML-kind ones too), the EML pass the EML-kind ones; no pass, none.
    pub fn pass_members(&self) -> Vec<&PlanPackage> {
        self.load_order
            .iter()
            .filter_map(|id| self.package(id))
            .filter(|p| {
                p.candidate
                    && match self.dialect {
                        StartupDialect::Shroudforge => p.kind != PackageKind::NativeSelection,
                        StartupDialect::Eml => p.kind == PackageKind::Eml,
                        StartupDialect::None => false,
                    }
            })
            .collect()
    }
    /// The outcome's rows: the pass members with startup data, in load order.
    pub fn participants(&self) -> Vec<&PlanPackage> {
        self.pass_members().into_iter().filter(|p| p.data_required).collect()
    }
}

/// Why kiln-sfi or kiln-eml leaves a package out: a stable word, its text, and the cause chain nearest first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refusal {
    pub code: String,
    pub text: String,
    pub chain: Vec<String>,
}

/// What kiln-eml knows of the native side when its pass begins (the plugin wiring fills it from the seam).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NativeFacts {
    /// kiln-sfi attached to the seam (false: kiln-sfi is absent, refused early or could not write its state).
    pub attached: bool,
    /// Its native terminal arrived (and was valid) before kiln-eml's wait ended.
    pub received: bool,
    /// The packages the terminal withdrew, with its code, text and chain.
    pub withdrawn: BTreeMap<String, Refusal>,
    /// The terminal's `coherent`: the witnessed input was unchanged once every DLL was terminal.
    pub coherent: Result<(), String>,
    /// Per DLL package, its DLLs' terminal outcome kinds in plan order (part of the startup cache's key).
    pub dlls: BTreeMap<String, Vec<String>>,
}

/// The BASE origin a start builds from (tokens and labels: inspection.rs ORIGINS).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Origin {
    FirstManaged,
    Rebaselined,
    Unrecorded,
}
impl Origin {
    pub fn token(self) -> &'static str {
        match self {
            Origin::FirstManaged => inspection::FIRST_MANAGED,
            Origin::Rebaselined => inspection::REBASELINED,
            Origin::Unrecorded => inspection::UNRECORDED,
        }
    }
    pub fn label(self) -> &'static str {
        match self {
            Origin::FirstManaged => inspection::FIRST_MANAGED_LABEL,
            Origin::Rebaselined => inspection::REBASELINED_LABEL,
            Origin::Unrecorded => inspection::UNRECORDED_LABEL,
        }
    }
    fn of(token: &str) -> Self {
        match token {
            inspection::FIRST_MANAGED => Origin::FirstManaged,
            inspection::REBASELINED => Origin::Rebaselined,
            _ => Origin::Unrecorded,
        }
    }
    /// What a receipt records for it, keyed to the baseline captured; None for unrecorded.
    pub(crate) fn recorded(self, baseline: startup_host::BaselineIdentity) -> Option<BaseOrigin> {
        if self == Origin::Unrecorded {
            return None;
        }
        BaseOrigin::recorded(self.token(), self.label(), baseline).ok()
    }
}

/// The early witness of a start: the launch, kiln-eml's opaque revision of it, the stamped input witness and the BASE
/// origin. `Send + Sync`: the plugin wiring's seam answers kiln-sfi's loader thread from it, read-only.
#[derive(Debug, Clone)]
pub struct Witnessed {
    pub key: LaunchKey,
    pub revision: u64,
    pub input: InputWitness,
    pub origin: Origin,
    exe_sha256: String,
}
impl Witnessed {
    /// The canonical game folder the witness covers.
    pub fn game(&self) -> &Path {
        self.input.game()
    }
    pub fn stem(&self) -> &str {
        self.input.stem()
    }
    /// The inspected executable's SHA-256 (lower-case hex), the plan's.
    pub fn exe_sha256(&self) -> &str {
        &self.exe_sha256
    }
}

/// A witness refused: no DLL loads and no plan package runs. `code` is the inspector's or `plan-stale`,
/// `lock-busy`, `baseline-shroudforge-output`, `baseline-stale-data`, `baseline-capture-failed`, `changed`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refused {
    pub code: String,
    pub text: String,
}

/// A point the startup controller reached, for the caller's observer (progress; the tests' kill points).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Point<'s> {
    /// The admission set is known.
    Admitted,
    /// An attempt of the pass begins (1 or 2).
    Attempt(u8),
    /// A step of the running attempt (`runner::Step`).
    Step(Step<'s>),
    /// The one publication returned.
    Published,
    /// The published generation's change journal is written beside its receipt.
    Journaled,
    /// The startup cache recorded this start's generation.
    CacheWritten,
    /// The verified BASE is live.
    Restored,
}

/// Which EML packages the EML pass runs: the plan's admitted EML-kind packages (dialect `eml`), or the unlisted EML
/// contributors only (dialect `none`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EmlSelection<'s> {
    Admitted(&'s [String]),
    Unlisted,
}

/// What `run` needs beyond the witness, the plan and the native facts.
pub struct Ports<'a> {
    /// The validated type registry of this game build.
    pub types: Rc<TypeRegistry>,
    /// Every discovered package (sfi-core's C3 discovery): the SF pass's registry and `loader.has_mod`'s membership.
    pub registry: Registry,
    /// The build's runtime operation catalog (the runtime registrar's static support).
    pub catalog: Rc<dyn RuntimeCatalog>,
    /// The start's UI locale, read once.
    pub locale: Option<String>,
    /// The pass VM's clock and log.
    pub vm: runner::Ports,
    /// A package folder's content digest (production: sfi-policy's `package_content_sha256`).
    pub digest: &'a dyn Fn(&Path) -> Result<[u8; 32], String>,
    /// The EML pass's environment for a selection; Ok(None) when it has nothing to run.
    pub eml: &'a dyn Fn(EmlSelection<'_>) -> anyhow::Result<Option<ModEnvironment>>,
    /// kiln.log warnings (target `startup-assets`: `sf_pass::log_warning` in production).
    pub log: &'a dyn Fn(&str),
    pub observe: &'a dyn Fn(Point<'_>),
}

/// The start's outcome `state` (startup-outcome.schema.json).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    Committed,
    Cached,
    Unchanged,
    Restored,
    Refused,
    Failed,
}
impl State {
    pub fn word(self) -> &'static str {
        match self {
            State::Committed => "committed",
            State::Cached => "cached",
            State::Unchanged => "unchanged",
            State::Restored => "restored",
            State::Refused => "refused",
            State::Failed => "failed",
        }
    }
    /// The live generation is proven: a grant exists.
    pub fn grants(self) -> bool {
        matches!(
            self,
            State::Committed | State::Cached | State::Unchanged | State::Restored
        )
    }
}

/// Which attempt of the pass the outcome is from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Attempt {
    First,
    Retry,
    None,
}
impl Attempt {
    pub fn word(self) -> &'static str {
        match self {
            Attempt::First => "first",
            Attempt::Retry => "retry",
            Attempt::None => "none",
        }
    }
    pub(crate) fn parse(word: &str) -> Option<Self> {
        [Attempt::First, Attempt::Retry, Attempt::None]
            .into_iter()
            .find(|a| a.word() == word)
    }
}

/// A row's `data` (sfi-state's package data values).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Data {
    Committed,
    CachedVerified,
    Failed,
    Excluded,
}
impl Data {
    pub fn word(self) -> &'static str {
        match self {
            Data::Committed => "committed",
            Data::CachedVerified => "cached-verified",
            Data::Failed => "failed",
            Data::Excluded => "excluded",
        }
    }
    pub(crate) fn parse(word: &str) -> Option<Self> {
        [Data::Committed, Data::CachedVerified, Data::Failed, Data::Excluded]
            .into_iter()
            .find(|d| d.word() == word)
    }
}

/// One plan-listed package of the pass with startup data.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    pub id: String,
    pub version: Option<String>,
    pub data: Data,
    /// Its data is in the live generation: true, false (proven absent) or None (unknown).
    pub archive_present: Option<bool>,
    pub code: Option<String>,
    pub text: Option<String>,
    /// Nearest first: the dependency it needed first, the failed package last; never its own id.
    pub chain: Vec<String>,
}
impl Row {
    pub(crate) fn excluded(id: &str, version: &str, refusal: &Refusal) -> Self {
        Row {
            id: id.to_owned(),
            version: version_of(version),
            data: Data::Excluded,
            archive_present: None,
            code: Some(refusal.code.clone()),
            text: Some(refusal.text.clone()),
            chain: refusal.chain.iter().filter(|c| *c != id).cloned().collect(),
        }
    }
}

/// One `warnings[]` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Warning {
    pub code: String,
    pub text: String,
}

/// What kiln-eml knew of the native side (the outcome's `native`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NativeSummary {
    pub received: bool,
    /// None: coherent; Some(text): not (no terminal is never coherent).
    pub incoherent: Option<String>,
}
impl NativeSummary {
    pub fn of(native: &NativeFacts) -> Self {
        let incoherent = if !native.received {
            Some(if native.attached {
                "no native terminal arrived before kiln-eml's startup pass".to_owned()
            } else {
                "kiln-sfi did not attach to kiln-eml's startup seam".to_owned()
            })
        } else {
            native.coherent.clone().err()
        };
        Self {
            received: native.received,
            incoherent,
        }
    }
}

/// The grant inputs of the generation proven live (published, cache-vouched, unchanged or the restored BASE): the plugin wiring
/// authorizes it once (`archive_service::authorize_with_archive`, phase PUBLISHED) and retires it, or gives the next one
/// a fresh backing, when its reads are done.
pub struct LiveGeneration {
    /// SHA-256 of the ownership receipt listing it; None for the BASE.
    pub receipt_sha256: Option<[u8; 32]>,
    /// The kiln-eml generation digest ([`generation_digest`]): the identity's `archive_sha256`.
    pub archive_sha256: [u8; 32],
    pub backing: Arc<ArchiveBacking>,
    pub snapshot: Arc<RegistrySnapshot>,
    pub registry: Arc<TypeRegistry>,
}
impl std::fmt::Debug for LiveGeneration {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LiveGeneration")
            .field("receipt_sha256", &self.receipt_sha256.map(|r| hex(&r)))
            .field("archive_sha256", &hex(&self.archive_sha256))
            .field("backing", &self.backing)
            .finish()
    }
}

/// The startup outcome (startup-outcome.schema.json v1) and the grant inputs.
#[derive(Debug)]
pub struct StartupOutcome {
    pub key: LaunchKey,
    pub state: State,
    pub dialect: Dialect,
    pub attempt: Attempt,
    pub error: Option<String>,
    pub retry_error: Option<String>,
    pub packages: Vec<Row>,
    pub warnings: Vec<Warning>,
    pub native: NativeSummary,
    pub live: Option<LiveGeneration>,
    /// The BASE origin the live generation's receipt records (unrecorded after a restore).
    pub origin: Origin,
}

/// The kiln-eml generation digest (the identity's and the outcome's `archive_sha256`): SHA-256 of
/// `"kiln-eml generation v1\0"`, the stem's length (u64 LE) and bytes, the receipt's SHA-256 (32 zero bytes for the
/// BASE), then the working baseline's backups in the fixed order `kfc.bak`, `kfc_resources.bak`, `kfc.bak.integrity`,
/// each a presence byte (0 absent, 1 present) and, present, its length (u64 LE) and raw SHA-256.
pub fn generation_digest(
    stem: &str,
    receipt: Option<&[u8; 32]>,
    baseline: &startup_host::BaselineIdentity,
) -> [u8; 32] {
    let mut h = Sha256::new();
    h.update(b"kiln-eml generation v1\0");
    h.update((stem.len() as u64).to_le_bytes());
    h.update(stem.as_bytes());
    h.update(receipt.copied().unwrap_or([0; 32]));
    let files: BTreeMap<&str, Option<(u64, &str)>> = baseline.files().collect();
    for ext in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
        match files.get(ext).copied().flatten() {
            None => h.update([0u8]),
            Some((size, sha)) => {
                h.update([1u8]);
                h.update(size.to_le_bytes());
                h.update(unhex(sha).unwrap_or([0; 32]));
            }
        }
    }
    h.finalize().into()
}

pub(crate) fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
pub(crate) fn unhex(text: &str) -> Option<[u8; 32]> {
    if text.len() != 64 {
        return None;
    }
    let mut out = [0u8; 32];
    for (i, b) in out.iter_mut().enumerate() {
        *b = u8::from_str_radix(text.get(i * 2..i * 2 + 2)?, 16).ok()?;
    }
    Some(out)
}
fn uuid(b: &[u8; 16]) -> String {
    format!(
        "{}-{}-{}-{}-{}",
        hex(&b[..4]),
        hex(&b[4..6]),
        hex(&b[6..8]),
        hex(&b[8..10]),
        hex(&b[10..])
    )
}
/// `text` cut to at most `max` characters (the schema's maxLength counts characters), at a character boundary.
pub(crate) fn clamp(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}
/// A version as a row carries it: 1 to 64 characters, else none.
fn version_of(version: &str) -> Option<String> {
    (!version.is_empty()).then(|| clamp(version, 64))
}

/// The bounds the outcome schema sets (writers clamp free text and cause lists, never rows or ids).
const ROW_TEXT: usize = 4096;
const NATIVE_TEXT: usize = 4096;
const ERROR_TEXT: usize = 65536;
const WARNING_TEXT: usize = 8192;
const WARNINGS: usize = 64;
const CHAIN: usize = 64;

fn json_str(text: &str) -> String {
    serde_json::to_string(text).unwrap_or_else(|_| "\"\"".into())
}
fn json_opt(text: Option<&str>) -> String {
    text.map_or_else(|| "null".into(), json_str)
}

impl StartupOutcome {
    /// The outcome record (startup-outcome.schema.json v1), compact, every field in the schema's order: a typed
    /// serializer, not serde_json's map order (this graph has preserve_order). `identity`: the PUBLISHED
    /// identity the plugin wiring authorized for the live generation; its `archive_sha256` is the record's (they are the same value,
    /// [`generation_digest`]). Free text and cause lists are clamped to the schema's bounds; rows and ids are not.
    pub fn json(&self, identity: Option<&KilnEmlArchiveIdentityV1>) -> String {
        let grant = self.state.grants() && self.live.is_some();
        let archive = match (grant, identity) {
            (false, _) => None,
            (true, Some(identity)) => Some(identity.archive_sha256),
            (true, None) => self.live.as_ref().map(|l| l.archive_sha256),
        };
        let receipt = if grant {
            self.live.as_ref().and_then(|l| l.receipt_sha256)
        } else {
            None
        };
        // a BASE origin is a fact about the live generation: without a grant nothing is proven live, so a start that
        // failed or was refused records none (null), whatever its witness found
        let origin = match self.origin {
            _ if !grant => "null".to_owned(),
            Origin::Unrecorded => "null".to_owned(),
            o => format!(
                "{{\"token\":{},\"label\":{}}}",
                json_str(o.token()),
                json_str(o.label())
            ),
        };
        let native = match &self.native.incoherent {
            None => format!("{{\"received\":{},\"coherent\":true}}", self.native.received),
            Some(text) => format!(
                "{{\"received\":{},\"coherent\":false,\"text\":{}}}",
                self.native.received,
                json_str(&clamp(nonempty(text), NATIVE_TEXT))
            ),
        };
        let pass = match self.dialect {
            StartupDialect::Eml => "eml",
            _ => "shroudforge",
        };
        let rows: Vec<String> = self
            .packages
            .iter()
            .map(|r| {
                let present = match (grant, r.archive_present) {
                    (true, Some(p)) => p.to_string(),
                    _ => "null".into(),
                };
                let mut row = format!(
                    "{{\"id\":{},\"version\":{},\"pass\":\"{pass}\",\"data\":\"{}\",\"archivePresent\":{present}",
                    json_str(&r.id),
                    json_opt(r.version.as_deref()),
                    r.data.word()
                );
                if let Some(code) = &r.code {
                    let text = r.text.as_deref().map_or(code.as_str(), nonempty);
                    row += &format!(
                        ",\"code\":{},\"text\":{}",
                        json_str(code),
                        json_str(&clamp(text, ROW_TEXT))
                    );
                }
                let chain: Vec<String> = r
                    .chain
                    .iter()
                    .filter(|c| **c != r.id)
                    .take(CHAIN)
                    .map(|c| json_str(c))
                    .collect();
                row + &format!(",\"chain\":[{}]}}", chain.join(","))
            })
            .collect();
        let warnings: Vec<String> = self
            .warnings
            .iter()
            .take(WARNINGS)
            .map(|w| {
                format!(
                    "{{\"code\":{},\"text\":{}}}",
                    json_str(&w.code),
                    json_str(&clamp(nonempty(&w.text), WARNING_TEXT))
                )
            })
            .collect();
        let error = |e: &Option<String>| e.as_deref().map(|e| clamp(nonempty(e), ERROR_TEXT));
        format!(
            "{{\"format\":1,\"launchId\":\"{:016x}\",\"planId\":\"{}\",\"planSha256\":\"{}\",\"dialect\":\"{}\",\
             \"state\":\"{}\",\"grant\":{grant},\"receiptSha256\":{},\"archiveSha256\":{},\"baseOrigin\":{origin},\
             \"native\":{native},\"attempt\":\"{}\",\"error\":{},\"retryError\":{},\"packages\":[{}],\"warnings\":[{}]}}",
            self.key.launch_id,
            uuid(&self.key.plan_id),
            hex(&self.key.plan_sha256),
            dialect_word(self.dialect),
            self.state.word(),
            receipt.map_or("null".into(), |r| format!("\"{}\"", hex(&r))),
            archive.map_or("null".into(), |a| format!("\"{}\"", hex(&a))),
            self.attempt.word(),
            json_opt(error(&self.error).as_deref()),
            json_opt(error(&self.retry_error).as_deref()),
            rows.join(","),
            warnings.join(",")
        )
    }
}

/// A text the schema needs non-empty.
fn nonempty(text: &str) -> &str {
    if text.is_empty() { "(no text)" } else { text }
}

pub(crate) fn dialect_word(dialect: Dialect) -> &'static str {
    match dialect {
        StartupDialect::Shroudforge => "shroudforge",
        StartupDialect::Eml => "eml",
        StartupDialect::None => "none",
    }
}

static REVISION: AtomicU64 = AtomicU64::new(1);

/// The longest a witness waits for an input that changed just before it to leave the change cache's racy window (the stamped witness
/// note 2), so that every later check stamps it instead of hashing it in full.
const RECAPTURE_WAIT: Duration = Duration::from_secs(4);

/// The record the startup cache keeps for `stem` (`kiln/eml/cache/sf-startup-<stem>.json`).
pub fn cache_record(stem: &str) -> String {
    format!("sf-startup-{stem}.json")
}

/// The early witness (kiln-eml's early phase, before any package DLL loads; the caller holds the install lock
/// `guard` for the process's life). In order:
/// 1. `guard` is the held install lock of `game` (else `lock-busy`).
/// 2. The archive inspector classifies `stem`'s archive set (the inspector's rules, so the boot decision is the pre-Play one):
///    any refusal is this witness's, with the inspector's code and text (`baseline-missing` included: Kiln's own output
///    is live under a receipt whose backups are gone).
/// 3. Plan freshness: the live input's frame is the plan's `archiveInput_sha256`, or the live set is the receipt's
///    owned output and its BASE frame is `archiveBase_sha256`; the inspected executable is the plan's; a plan that
///    names a BASE origin names this one. Else `plan-stale`.
/// 4. The working baseline: none yet (first managed), one of another game build, or one a receipt names that is gone
///    over game files that are not Kiln's output (rebaselined; the rebaseline rule: the inspector's note `receipt-stale`,
///    and that receipt is first set aside as a recovery copy, never deleted) is captured now, Deferred (ShroudForge's
///    folder untouched); its origin is the inspector's. A recorded origin keyed to the backups
///    present is kept; anything else is unrecorded. A live set that is Kiln's bundled ShroudForge's output (its record
///    names the live header) is never captured: `baseline-shroudforge-output`, verify the game files in Steam.
/// 5. The stamped input witness over the baseline's containers; an input hashed only because it changed within
///    the racy window is captured again once that window passed (at most [`RECAPTURE_WAIT`]), so the checks of this
///    start stay byte-free. The witnessed header and working baseline must be the bytes the inspector classified (else
///    `changed`); the live resource file and the containers are held by their stamps from this capture on (a write
///    between the inspector's read and the capture, by a program that ignores the install lock, is not seen).
pub fn witness(
    game: &Utf8Path,
    stem: &str,
    guard: &File,
    plan: &PlanFacts,
    key: LaunchKey,
) -> Result<Witnessed, Refused> {
    let refuse = |code: &str, text: String| Refused {
        code: code.to_owned(),
        text,
    };
    let side = match stem {
        "enshrouded" => inspection::Side::Client,
        "enshrouded_server" => inspection::Side::Server,
        _ => return Err(refuse("invalid", format!("{stem} is not a game archive Kiln manages"))),
    };
    if key.plan_id != plan.plan_id {
        return Err(refuse(
            "plan-stale",
            "the launch plan read again is not this start's".into(),
        ));
    }
    // 1. the held install lock (the record is the startup cache's; binding it proves the guard)
    StartupRecord::bind(game.as_std_path(), &cache_record(stem), guard).map_err(|e| {
        refuse(
            "lock-busy",
            format!("the game's install lock is not held by this start ({e:#}); start the game again"),
        )
    })?;
    // 2. the inspector's classification
    let inspected = inspection::inspect(
        game.as_std_path(),
        &inspection::Options {
            side,
            hash_containers: false,
        },
    );
    if inspected.status == inspection::Status::Refused {
        let reason = inspected.reasons.first().cloned().unwrap_or(inspection::Reason {
            code: "unreadable",
            file: None,
            text: "the game's archive could not be inspected".into(),
        });
        return Err(refuse(reason.code, reason.text));
    }
    let (Some(input), Some(base)) = (&inspected.input, &inspected.base) else {
        return Err(refuse("unreadable", "the game's archive could not be inspected".into()));
    };
    // 3. plan freshness
    let stale = |why: &str| {
        refuse(
            "plan-stale",
            format!(
                "{why} since the launcher made this start's launch plan; start the game again from the Kiln launcher"
            ),
        )
    };
    let exe = inspected
        .target
        .executable
        .as_ref()
        .map(|e| e.sha256.clone())
        .unwrap_or_default();
    if exe != plan.exe_sha256 {
        return Err(stale("the game executable changed"));
    }
    let fresh = input.sha256 == plan.archive_input_sha256
        || (input.class == "owned-output" && base.sha256 == plan.archive_base_sha256);
    if !fresh {
        return Err(stale("the game's archive files changed"));
    }
    if plan
        .archive_base_origin
        .as_deref()
        .is_some_and(|token| token != base.origin.token)
    {
        return Err(stale(
            "where the game files Kiln builds from came from changed (base-origin-changed)",
        ));
    }
    let origin = Origin::of(base.origin.token);
    // 4. the working baseline, captured or refreshed now (Deferred), never after a DLL ran
    let header = game.join(format!("{stem}.kfc"));
    if base.evidence == "current-set" {
        // the live set becomes the working baseline: never when it is Kiln's bundled ShroudForge's output (its record
        // names the live header), whose backups are gone or are of another game build: that is a modded set, and only
        // Steam's file check gives the game's own data back
        match startup_host::hosted_shroudforge_output(game.as_std_path(), stem) {
            Ok(startup_host::HostedShroudForge::NotProven) => {}
            Ok(_) => {
                return Err(refuse(
                    "baseline-shroudforge-output",
                    format!(
                        "the live {stem}.kfc is what Kiln's bundled ShroudForge prepared, and Kiln has no backup of this \
                         game build's own data to start from. {}",
                        startup_host::VERIFY_GAME_FILES
                    ),
                ));
            }
            Err(e) => {
                return Err(refuse(
                    "unreadable",
                    format!("the game's archive could not be checked before Kiln takes its working baseline: {e}"),
                ));
            }
        }
    }
    // after lost backups, the receipt that names them goes aside first (only the one the inspector judged, under the
    // held lock), so the new baseline is never taken with Kiln's ownership of an older one still in place
    if inspected.notes.iter().any(|n| n.code == inspection::RECEIPT_STALE) {
        let judged = inspected.receipt.sha256.as_deref().unwrap_or_default();
        startup_host::set_aside_stale_receipt(game.as_std_path(), stem, guard, judged).map_err(|e| {
            refuse(
                "baseline-missing",
                format!(
                    "Kiln could not keep its ownership receipt, whose working baseline is gone, as a recovery copy \
                    ({e:#}); start the game again"
                ),
            )
        })?;
    }
    if base.evidence == "current-set" {
        // kiln-eml (stale game data, review): game files that hold a known mod's program never become the working
        // baseline (a loader's first copy of patched game files kept that mod in every start after it was removed)
        if let Ok(live) = KFCFile::from_path(&header, false)
            && let Some(text) = startup_host::known_leftover_refusal(stem, &live, true)
        {
            return Err(refuse("baseline-stale-data", text));
        }
        startup_host::capture_baseline(header.as_std_path(), guard, SfHandoff::Deferred).map_err(|e| {
            refuse(
                "baseline-capture-failed",
                format!("Kiln could not take the working baseline of the game files: {e:#}"),
            )
        })?;
    }
    // 5. the stamped input witness over the baseline's containers
    let changed = |e: &dyn std::fmt::Display| {
        refuse(
            "changed",
            format!(
                "the game files changed while Kiln was checking them ({e}); close programs that change the game files, then start again"
            ),
        )
    };
    let reference = KFCFile::from_path(game.join(format!("{stem}.kfc.bak")), false).map_err(|e| changed(&e))?;
    // kiln-eml (stale game data, review): nothing is built from a working baseline that holds a known mod's program
    // (only its header is looked at: no extra read of the game data)
    if let Some(text) = startup_host::known_leftover_refusal(stem, &reference, false) {
        return Err(refuse("baseline-stale-data", text));
    }
    // The reference index the pass reads by resource type: a bundle range outside the index is refused
    // here, not met as a panic in an attempt
    if !reference.bundles_in_range() {
        return Err(refuse(
            "invalid",
            format!(
                "{stem}.kfc.bak is not a game archive Kiln can read (its resource type index is out of range): \
                verify the game files in Steam, then start again"
            ),
        ));
    }
    let containers = reference.containers().len();
    let mut witnessed = InputWitness::capture(game.as_std_path(), stem, containers).map_err(|e| changed(&e))?;
    if let Some(wait) = witnessed.settles_in().filter(|w| *w <= RECAPTURE_WAIT) {
        std::thread::sleep(wait + Duration::from_millis(20));
        witnessed.check(game.as_std_path()).map_err(|e| changed(&e))?;
        let again = InputWitness::capture(game.as_std_path(), stem, containers).map_err(|e| changed(&e))?;
        if again.baseline() != witnessed.baseline() || again.parsed("kfc") != witnessed.parsed("kfc") {
            return Err(changed(&"captured twice, they differ"));
        }
        witnessed = again;
    }
    // the witnessed header and working baseline are the bytes the inspector classified
    let entry = |role: &str| {
        input
            .files
            .iter()
            .find(|e| e.role == role)
            .map(|e| (e.size, e.sha256.clone()))
    };
    let live_header = entry("header");
    if witnessed.parsed("kfc").map(|(s, h)| (s, Some(h.to_owned()))) != live_header {
        return Err(changed(&"the live header"));
    }
    let baseline: BTreeMap<&str, Option<(u64, String)>> = witnessed
        .baseline()
        .files()
        .map(|(ext, d)| (ext, d.map(|(s, h)| (s, h.to_owned()))))
        .collect();
    let expected: BTreeMap<&str, Option<(u64, String)>> = match &base.baseline {
        Some(recorded) => ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"]
            .into_iter()
            .map(|ext| {
                let hashed = recorded.get(&format!("{stem}.{ext}")).cloned().flatten();
                (ext, hashed.map(|h| (h.size, h.sha256)))
            })
            .collect(),
        // just captured from the live pair the inspector read
        None => [
            ("kfc.bak", live_header.clone().map(|(s, h)| (s, h.unwrap_or_default()))),
            (
                "kfc_resources.bak",
                entry("resources").map(|(s, h)| (s, h.unwrap_or_default())),
            ),
        ]
        .into_iter()
        .chain(std::iter::once((
            "kfc.bak.integrity",
            baseline.get("kfc.bak.integrity").cloned().flatten(),
        )))
        .collect(),
    };
    if baseline != expected {
        return Err(changed(&"the working baseline"));
    }
    Ok(Witnessed {
        key,
        revision: REVISION.fetch_add(1, Ordering::Relaxed),
        input: witnessed,
        origin,
        exe_sha256: exe,
    })
}

/// The startup controller's inputs, for one `run`.
struct Ctx<'a, 'g> {
    w: &'a Witnessed,
    plan: &'a PlanFacts,
    native: &'a NativeFacts,
    guard: &'g File,
    types: Rc<TypeRegistry>,
    catalog: Rc<dyn RuntimeCatalog>,
    locale: Option<String>,
    vm: runner::Ports,
    digest: &'a dyn Fn(&Path) -> Result<[u8; 32], String>,
    eml: &'a dyn Fn(EmlSelection<'_>) -> anyhow::Result<Option<ModEnvironment>>,
    log: &'a dyn Fn(&str),
    observe: &'a dyn Fn(Point<'_>),
}

/// The pass of a kiln-mode start (kiln-eml's early on_ready, on the boot main thread, after the native side concluded;
/// `guard` is the install lock the caller has held since `witness`). In order:
/// 1. The launch and the input: the witness must be this plan's, and the witnessed input unchanged (the terminal's
///    `coherent` when its run loaded a DLL, then the witness's own check). A changed input (a package DLL wrote the game's files) publishes
///    nothing, retries nothing and grants nothing: every row `startup-input-changed`, a warning naming the DLL
///    packages that loaded.
/// 2. Admission ([`admission::admit`]): the plan's pass members minus native refusals (`native-refused`, or every one
///    `backend-unavailable` when kiln-sfi never attached, the native ones `native-terminal-missing` without a terminal),
///    changed packages (`package-changed`), and every package that needs a left-out one.
/// 3. The dialect: `shroudforge` checks the startup cache, else runs the SF pass with its one retry (each attempt a new
///    host over the witness, `open_matching`; the retry in the fork's re-planned order); `eml` runs EML's pass on the
///    witnessed host; `none` runs the EML pass for unlisted contributors only.
/// 4. One publication (Deferred) of what the pass prepared, then (SF dialect) the generation's change journal beside its
///    receipt (`sf_startup::journal`: per package, the resources its entrypoint wrote or created and the contents
///    it created) and the startup cache naming it, or EML's change cache (no journal: the EML dialect's publication
///    removes the previous one), still under the guard; or, when no plan contribution was committed (a failed pass,
///    nothing admitted), the verified BASE (F9). A failed publication grants nothing.
/// 5. The grant inputs of whatever generation is proven live.
///
/// Never panics: a panic anywhere in the pass is contained and the start is `failed` without a grant.
pub fn run(w: &Witnessed, plan: &PlanFacts, native: &NativeFacts, guard: &File, ports: Ports<'_>) -> StartupOutcome {
    let Ports {
        types,
        registry,
        catalog,
        locale,
        vm,
        digest,
        eml,
        log,
        observe,
    } = ports;
    let ctx = Ctx {
        w,
        plan,
        native,
        guard,
        types,
        catalog,
        locale,
        vm,
        digest,
        eml,
        log,
        observe,
    };
    let mut out = StartupOutcome {
        key: w.key,
        state: State::Failed,
        dialect: plan.dialect,
        attempt: Attempt::None,
        error: None,
        retry_error: None,
        packages: Vec::new(),
        warnings: Vec::new(),
        native: NativeSummary::of(native),
        live: None,
        origin: w.origin,
    };
    if let Err(panic) = catch_unwind(AssertUnwindSafe(|| ctx.run(&mut out, registry))) {
        let text = format!(
            "kiln-eml's startup pass stopped on an internal error: {}",
            panic_text(panic)
        );
        out.live = None;
        ctx.fail(&mut out, "startup-internal-error", &text);
    }
    if out.state.grants() && out.live.is_none() {
        ctx.fail(
            &mut out,
            "startup-grant-missing",
            "the live generation could not be described for kiln-sfi",
        );
    }
    out
}

/// The outcome of a refused witness: no pass ran, nothing is granted, every row carries the refusal.
pub fn refused(plan: &PlanFacts, key: LaunchKey, native: &NativeFacts, refusal: &Refused) -> StartupOutcome {
    let row = Refusal {
        code: refusal.code.clone(),
        text: refusal.text.clone(),
        chain: Vec::new(),
    };
    StartupOutcome {
        key,
        state: State::Refused,
        dialect: plan.dialect,
        attempt: Attempt::None,
        error: None,
        retry_error: None,
        packages: plan
            .participants()
            .iter()
            .map(|p| Row::excluded(&p.id, &p.version, &row))
            .collect(),
        warnings: vec![Warning {
            code: refusal.code.clone(),
            text: refusal.text.clone(),
        }],
        native: NativeSummary::of(native),
        live: None,
        origin: Origin::Unrecorded,
    }
}

/// The outcome of a start whose pass could not begin or whose grant could not be made: no grant, every row of
/// the pass carries the reason, which is a warning too. `attempt` and the errors of a pass that ran are `outcome`'s.
pub fn without_grant(mut outcome: StartupOutcome, plan: &PlanFacts, code: &str, text: &str) -> StartupOutcome {
    let refusal = Refusal {
        code: code.into(),
        text: text.into(),
        chain: Vec::new(),
    };
    outcome.state = State::Failed;
    outcome.live = None;
    outcome.packages = plan
        .participants()
        .iter()
        .map(|p| Row::excluded(&p.id, &p.version, &refusal))
        .collect();
    outcome.warnings.push(Warning {
        code: code.into(),
        text: text.into(),
    });
    outcome
}

fn panic_text(panic: Box<dyn std::any::Any + Send>) -> String {
    panic
        .downcast_ref::<&str>()
        .map(|s| s.to_string())
        .or_else(|| panic.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "a panic".into())
}

impl<'g> Ctx<'_, 'g> {
    fn game(&self) -> &Path {
        self.w.game()
    }
    fn stem(&self) -> &str {
        self.w.stem()
    }

    /// Every row excluded with `code`, no grant, the reason as a warning too (a dialect without rows keeps it there).
    fn fail(&self, out: &mut StartupOutcome, code: &str, text: &str) {
        let refusal = Refusal {
            code: code.into(),
            text: text.into(),
            chain: Vec::new(),
        };
        out.state = State::Failed;
        out.live = None;
        out.packages = self
            .plan
            .participants()
            .iter()
            .map(|p| Row::excluded(&p.id, &p.version, &refusal))
            .collect();
        out.warnings.push(Warning {
            code: code.into(),
            text: text.into(),
        });
    }

    fn run(&self, out: &mut StartupOutcome, registry: Registry) {
        if self.w.key.plan_id != self.plan.plan_id {
            return self.fail(
                out,
                "plan-stale",
                "the witness was taken for another launch plan than this pass's",
            );
        }
        // the terminal's coherence counts for a run that loaded a DLL; one that loaded none changed nothing, so a
        // coherence it could not confirm (a recheck kiln-eml no longer answered, or one that did not answer in time:
        // kiln-sfi's RECHECK_GONE, RECHECK_UNANSWERED) is not a change, as kiln-sfi reads it (plan_source.rs
        // `incoherent`). kiln-eml's own check below stays the authority either way.
        let any_loaded = self.native.dlls.values().flatten().any(|k| k == "loaded");
        let changed = match (&self.native.coherent, self.native.received && any_loaded) {
            (Err(text), true) => Some(text.clone()),
            _ => None,
        }
        .or_else(|| self.w.input.check(self.game()).err().map(|e| e.to_string()));
        if let Some(text) = changed {
            let loaded: Vec<&str> = self
                .native
                .dlls
                .iter()
                .filter(|(_, kinds)| kinds.iter().any(|k| k == "loaded"))
                .map(|(id, _)| id.as_str())
                .collect();
            let text = format!(
                "the game's archive files changed after Kiln checked them, while package DLLs were loading ({text}): \
                 Kiln wrote no game data and the ShroudForge mods' startup data and Lua do not run this start{}",
                if loaded.is_empty() {
                    String::new()
                } else {
                    format!(". DLL packages that loaded: {}", loaded.join(", "))
                }
            );
            (self.log)(&text);
            return self.fail(out, "startup-input-changed", &text);
        }
        let admission = admission::admit(
            self.plan,
            self.native,
            self.digest,
            self.game(),
            Some(&registry),
            self.stem(),
        );
        (self.observe)(Point::Admitted);
        match self.plan.dialect {
            StartupDialect::Shroudforge => self.shroudforge(out, admission, registry),
            StartupDialect::Eml => self.eml_pass(out, &admission, true),
            StartupDialect::None => self.eml_pass(out, &admission, false),
        }
    }

    /// The rows of the pass's participants: the admitted ones by `row`, the others as admission left them out (data
    /// proven absent once a generation is proven live).
    fn rows(&self, admission: &admission::Admission, mut row: impl FnMut(&PlanPackage) -> Row) -> Vec<Row> {
        self.plan
            .participants()
            .into_iter()
            .map(|p| match admission.excluded.get(&p.id) {
                Some(refusal) => Row {
                    archive_present: Some(false),
                    ..Row::excluded(&p.id, &p.version, refusal)
                },
                None => row(p),
            })
            .collect()
    }

    fn committed(p: &PlanPackage, data: Data) -> Row {
        Row {
            id: p.id.clone(),
            version: version_of(&p.version),
            data,
            archive_present: Some(true),
            code: None,
            text: None,
            chain: Vec::new(),
        }
    }

    fn baseline(&self) -> Result<Rc<KFCFile>, String> {
        KFCFile::from_path(self.game().join(format!("{}.kfc.bak", self.stem())), false)
            .map(Rc::new)
            .map_err(|e| format!("the working baseline cannot be read: {e}"))
    }

    /// A host over the witness for one generation step (Deferred, with the BASE origin a receipt records).
    fn host(&self) -> anyhow::Result<startup_host::StartupHost<'g>> {
        let baseline = self.baseline().map_err(anyhow::Error::msg)?;
        let host = startup_host::StartupHost::open_matching(
            self.game(),
            self.stem(),
            self.types.clone(),
            baseline,
            self.guard,
            &self.w.input,
        )?
        .with_handoff(SfHandoff::Deferred);
        match self.w.origin.recorded(self.w.input.baseline().clone()) {
            Some(origin) => host.with_origin(origin),
            None => Ok(host),
        }
    }

    /// The registry snapshot and the shared registry of this build, for a generation's grant.
    fn grant_parts(&self, backing: ArchiveBacking) -> LiveGeneration {
        let registry = Arc::new((*self.types).clone());
        let snapshot = RegistrySnapshot::new(&registry, backing.game_version());
        LiveGeneration {
            receipt_sha256: None,
            archive_sha256: [0; 32],
            backing: Arc::new(backing),
            snapshot,
            registry,
        }
    }

    /// The published generation the receipt with SHA-256 `receipt` (hex) lists, read back as it is live.
    fn live_published(&self, receipt: &str) -> Result<LiveGeneration, String> {
        let receipt = unhex(receipt).ok_or("the ownership receipt's digest is not a SHA-256")?;
        let backing = ArchiveBacking::open_live(self.game(), self.stem())
            .map_err(|e| format!("the published archive cannot be read back: {e:#}"))?;
        Ok(LiveGeneration {
            receipt_sha256: Some(receipt),
            archive_sha256: generation_digest(self.stem(), Some(&receipt), self.w.input.baseline()),
            ..self.grant_parts(backing)
        })
    }

    /// The verified BASE (F9): no plan contribution is committed this start. Restored when owned output was live (or a
    /// receipt was left from one), unchanged when the BASE was live already; refused (no grant) for output Kiln cannot
    /// prove it published.
    fn settle_base(&self, out: &mut StartupOutcome) -> bool {
        let restored = self.host().and_then(|mut host| host.restore_base());
        let restored = match restored {
            Ok(restored) => restored,
            Err(e) => {
                let text = format!("Kiln could not put the game's own data back: {e:#}");
                (self.log)(&text);
                out.state = State::Failed;
                out.live = None;
                out.warnings.push(Warning {
                    code: "base-not-restored".into(),
                    text,
                });
                return false;
            }
        };
        (self.observe)(Point::Restored);
        let backing = match ArchiveBacking::open(self.game(), self.stem(), std::iter::empty()) {
            Ok(backing) => backing,
            Err(e) => {
                out.state = State::Failed;
                out.live = None;
                out.warnings.push(Warning {
                    code: "base-not-restored".into(),
                    text: format!("the restored game data cannot be read back: {e:#}"),
                });
                return false;
            }
        };
        out.state = if restored.copied || restored.receipt_removed {
            State::Restored
        } else {
            State::Unchanged
        };
        out.origin = Origin::Unrecorded;
        out.live = Some(LiveGeneration {
            archive_sha256: generation_digest(self.stem(), None, self.w.input.baseline()),
            ..self.grant_parts(backing)
        });
        true
    }

    /// The ShroudForge dialect (see `run`).
    fn shroudforge(&self, out: &mut StartupOutcome, admission: admission::Admission, registry: Registry) {
        let stem = self.stem();
        let admitted = self
            .plan
            .participants()
            .iter()
            .filter(|p| !admission.excluded.contains_key(&p.id))
            .count();
        let excluded_only = |out: &mut StartupOutcome| {
            out.packages = self.rows(&admission, |p| Self::committed(p, Data::Committed));
        };
        if admitted == 0 {
            self.settle_base(out);
            return excluded_only(out);
        }
        let features = Features {
            patch: true,
            export: false,
        };
        let members: Vec<&PlanPackage> = admission.order.iter().filter_map(|id| self.plan.package(id)).collect();
        let key = cache::Key::new(
            self.plan.dialect,
            stem,
            &self.w.exe_sha256,
            &members,
            &self.native.dlls,
            self.w.input.baseline(),
            features,
            (self.locale.as_deref(), self.catalog.game_build()),
        );
        let record = match StartupRecord::bind(self.game(), &cache_record(stem), self.guard)
            .and_then(|record| Ok((record, StartupJournal::bind(self.game(), stem, self.guard)?)))
        {
            Ok(record) => record,
            Err(e) => {
                return self.fail(
                    out,
                    "lock-busy",
                    &format!("the game's install lock is no longer held: {e:#}"),
                );
            }
        };
        let (record, journal_file) = record;
        // the startup cache: the same key, the generation it recorded still live with its change journal, nothing
        // exported
        if let Some(hit) = cache::lookup(&record, &journal_file, &key, &self.w.input, features)
            && let Ok(live) = self.live_published(&hit.receipt)
        {
            out.state = State::Cached;
            out.attempt = hit.attempt;
            out.error = hit.error;
            out.retry_error = hit.retry_error;
            out.warnings.extend(hit.warnings);
            out.packages = self.rows(&admission, |p| match hit.rows.get(&p.id) {
                Some(row) if row.data == Data::Committed => Row {
                    data: Data::CachedVerified,
                    ..row.clone()
                },
                Some(row) => row.clone(),
                None => Self::committed(p, Data::CachedVerified),
            });
            out.live = Some(live);
            return;
        }
        let side = if stem == "enshrouded_server" {
            sfi_core::host::Side::Server
        } else {
            sfi_core::host::Side::Client
        };
        let startup = match StartupPlan::new(
            registry,
            admission.order.clone(),
            side,
            sfi_core::host::SourceVersion::Sf179,
            features,
            None,
            self.catalog.clone(),
            self.locale.clone(),
        ) {
            Ok(startup) => startup,
            Err(e) => return self.pass_failed(out, &admission, Attempt::None, format!("{e:#}"), None, Vec::new()),
        };
        let lines = std::cell::RefCell::new(Vec::new());
        let log = |line: &str| {
            (self.log)(line);
            lines.borrow_mut().push(line.to_owned());
        };
        let pass = retry::run(&startup, &|| self.host(), &self.vm, self.observe, &log);
        let lines = lines.into_inner();
        let attempt = if pass.attempts > 1 {
            Attempt::Retry
        } else {
            Attempt::First
        };
        let first_error = pass.errors.first().cloned();
        let prepared = match pass.result {
            Ok(prepared) => prepared,
            Err(error) => {
                let retry_error = pass.retry.error.clone();
                return self.pass_failed(out, &admission, attempt, error.to_string(), retry_error, lines);
            }
        };
        // the attempt and the first attempt's error stand whatever the publication does (a retry's record always
        // names the error it retried after: startup-outcome.schema.json)
        out.attempt = attempt;
        out.error = if attempt == Attempt::Retry { first_error } else { None };
        // the committed attempt's change journal: a failed first attempt's changes are never in it
        let journal = prepared.journal().clone();
        // the one publication
        let published = prepared.publish();
        (self.observe)(Point::Published);
        let published = match published {
            Ok(Some(published)) => published,
            Ok(None) => {
                return self.publication_failed(out, &admission, attempt, "the pass prepared no generation".into());
            }
            Err(e) => return self.publication_failed(out, &admission, attempt, format!("{e}")),
        };
        // the rows: the failed package and what the retry left out with it, the rest committed
        let skipped = retry::skipped_rows(&pass.retry, &startup, &pass.first_plan);
        out.packages = self.rows(&admission, |p| {
            skipped
                .get(&p.id)
                .cloned()
                .unwrap_or_else(|| Self::committed(p, Data::Committed))
        });
        if attempt == Attempt::Retry
            && let Some(line) = lines.last()
        {
            out.warnings.push(Warning {
                code: "startup-retried".into(),
                text: line.clone(),
            });
        }
        let Some(receipt) = published.receipt_sha256().map(str::to_owned) else {
            return self.publication_failed(
                out,
                &admission,
                attempt,
                "the publication left no ownership receipt".into(),
            );
        };
        let journal_sha256 = match self.live_published(&receipt) {
            Ok(live) => {
                // the change journal of this generation, under the guard, bound to its receipt, before the cache
                // record that names it
                let written = self.write_journal(&journal_file, &journal, &live, attempt, &out.packages);
                out.state = State::Committed;
                out.live = Some(live);
                written
            }
            Err(text) => return self.publication_failed(out, &admission, attempt, text),
        };
        let journal_sha256 = match journal_sha256 {
            Ok(sha) => {
                (self.observe)(Point::Journaled);
                sha
            }
            Err(e) => {
                // the publication stands; without its journal the generation is not cached, so the next start runs the
                // pass again and journals its publication
                out.warnings.push(Warning {
                    code: "startup-journal-not-saved".into(),
                    text: format!(
                        "the startup change journal was not saved ({e}); the next start runs the startup pass again"
                    ),
                });
                return;
            }
        };
        // the startup cache, still under the guard: a retried success with the full key (its warning repeats)
        let rows: Vec<Row> = out
            .packages
            .iter()
            .filter(|r| !admission.excluded.contains_key(&r.id))
            .cloned()
            .collect();
        match cache::store(
            &record,
            &key,
            &receipt,
            attempt,
            &out.error,
            &out.retry_error,
            &rows,
            &out.warnings,
            &journal_sha256,
        ) {
            Ok(()) => (self.observe)(Point::CacheWritten),
            Err(e) => out.warnings.push(Warning {
                code: "startup-cache-not-saved".into(),
                text: format!("the startup cache was not saved ({e}); the next start runs the pass again"),
            }),
        }
    }

    /// The change journal of the generation `live` just published from `journal`, the committed attempt's: written
    /// beside its receipt under the guard, only while that receipt is still the one listing it. Returns the record's
    /// SHA-256 (the startup cache keeps it).
    /// Format 2 (0.2.0-dev.32: the ShroudForge dialect with the member paths each package changed, and kiln mode's EML
    /// dialect) lists the plan's packages the committed attempt did not run (`rows`' failed and excluded ones, with their
    /// texts) as `notApplied`: effect replay (the registry's opt-in D6) reads it.
    fn write_journal(
        &self,
        file: &StartupJournal<'_>,
        journal: &Journal,
        live: &LiveGeneration,
        attempt: Attempt,
        rows: &[Row],
    ) -> Result<String, String> {
        let receipt = live.receipt_sha256.ok_or("the published generation has no receipt")?;
        let header = journal::Header {
            stem: self.stem(),
            generation: live.archive_sha256,
            receipt_sha256: receipt,
            dialect: dialect_word(self.plan.dialect),
            attempt: attempt.word(),
        };
        let kind = |p: &PlanPackage| match p.kind {
            PackageKind::Eml => "eml",
            _ => "shroudforge",
        };
        let identity = |id: &str| {
            self.plan.package(id).map(|p| journal::Identity {
                version: version_of(&p.version),
                kind: kind(p),
                content_sha256: p.content_sha256,
                settings_sha256: p.config_sha256,
            })
        };
        let not_applied: Vec<journal::NotApplied> = rows
            .iter()
            .filter(|r| matches!(r.data, Data::Failed | Data::Excluded))
            .filter_map(|r| {
                let p = self.plan.package(&r.id)?;
                Some(journal::NotApplied {
                    id: r.id.clone(),
                    version: r.version.clone(),
                    kind: kind(p),
                    content_sha256: p.content_sha256,
                    reason: r.text.clone().or_else(|| r.code.clone()).unwrap_or_else(|| "not applied".into()),
                })
            })
            .collect();
        let bytes = journal.json_with(&header, &identity, &journal::Tail { writer: None, not_applied: Some(&not_applied) });
        file.write_for(&hex(&receipt), bytes.as_bytes())
            .map_err(|e| e.to_string())?;
        Ok(hex(&Sha256::digest(bytes.as_bytes())))
    }

    /// The pass failed (a package twice, or anything no package raised): the verified BASE (F9) and every admitted row
    /// left out with the failure.
    fn pass_failed(
        &self,
        out: &mut StartupOutcome,
        admission: &admission::Admission,
        attempt: Attempt,
        error: String,
        retry_error: Option<String>,
        lines: Vec<String>,
    ) {
        let (code, text) = match &retry_error {
            Some(second) => (
                "startup-retry-failed",
                format!(
                    "the startup pass failed twice: {} / {}",
                    crate::sf_pass::one_line(&error),
                    crate::sf_pass::one_line(second)
                ),
            ),
            None => (
                "startup-pass-failed",
                format!("the startup pass failed: {}", crate::sf_pass::one_line(&error)),
            ),
        };
        (self.log)(&text);
        out.attempt = attempt;
        out.error = (attempt != Attempt::None).then_some(error).filter(|e| !e.is_empty());
        out.retry_error = if attempt == Attempt::Retry { retry_error } else { None };
        if out.attempt == Attempt::Retry && out.error.is_none() {
            out.error = Some(text.clone());
        }
        if out.attempt == Attempt::None {
            out.warnings.push(Warning {
                code: code.into(),
                text: text.clone(),
            });
        } else {
            out.warnings.push(Warning {
                code: code.into(),
                text: lines.last().cloned().unwrap_or_else(|| text.clone()),
            });
        }
        let refusal = Refusal {
            code: code.into(),
            text,
            chain: Vec::new(),
        };
        self.settle_base(out);
        out.packages = self.rows(admission, |p| Row {
            archive_present: Some(false),
            ..Row::excluded(&p.id, &p.version, &refusal)
        });
    }

    /// The publication failed: what is live is not proven (old, BASE or new), so nothing is granted and nothing cached.
    fn publication_failed(
        &self,
        out: &mut StartupOutcome,
        admission: &admission::Admission,
        attempt: Attempt,
        error: String,
    ) {
        let text = format!("Kiln could not publish the startup data: {error}");
        (self.log)(&text);
        out.state = State::Failed;
        out.live = None;
        out.attempt = attempt;
        match attempt {
            Attempt::First => out.error = Some(text.clone()),
            // the first attempt's error, which the caller recorded; never none for a retry
            Attempt::Retry if out.error.is_none() => out.error = Some(text.clone()),
            _ => {}
        }
        let refusal = Refusal {
            code: "startup-publication-failed".into(),
            text: text.clone(),
            chain: Vec::new(),
        };
        out.packages = self.rows(admission, |p| Row::excluded(&p.id, &p.version, &refusal));
        out.warnings.push(Warning {
            code: "startup-publication-failed".into(),
            text,
        });
    }

    /// The EML pass: dialect `eml` (`listed`: the plan's admitted EML packages), or `none` (the unlisted contributors
    /// only, no rows). One attempt (EML's dialect has no retry); EML's change cache is its cache.
    fn eml_pass(&self, out: &mut StartupOutcome, admission: &admission::Admission, listed: bool) {
        let record: Option<Journal>;
        let admitted = admission.order.clone();
        let selection = if listed {
            EmlSelection::Admitted(&admitted)
        } else {
            EmlSelection::Unlisted
        };
        let env = match (self.eml)(selection) {
            Ok(Some(env)) => env,
            Ok(None) => {
                self.settle_base(out);
                out.packages = self.rows(admission, |p| Self::committed(p, Data::Committed));
                return;
            }
            Err(e) => {
                return self.pass_failed(
                    out,
                    admission,
                    Attempt::None,
                    format!("the EML pass could not load its mods: {e:#}"),
                    None,
                    Vec::new(),
                );
            }
        };
        let mut host = match self.host() {
            Ok(host) => host,
            Err(e) => return self.pass_failed(out, admission, Attempt::None, format!("{e:#}"), None, Vec::new()),
        };
        (self.observe)(Point::Attempt(1));
        let args = mod_loader::lua::RunArgs {
            file_name: self.stem().into(),
            options: mod_loader::lua::RunOptions {
                patch: true,
                ..Default::default()
            },
        };
        let prepared = catch_unwind(AssertUnwindSafe(|| {
            host.prepare_eml(&env, args, &mod_loader::lua::Progress::none())
        }))
        .unwrap_or_else(|panic| Err(anyhow::anyhow!("panic: {}", panic_text(panic))));
        drop(host);
        let published = match prepared {
            Ok(mod_loader::lua::startup_host::PreparedEml::Cached(published)) => {
                let live = published
                    .receipt_sha256()
                    .ok_or_else(|| "no ownership receipt".to_owned())
                    .and_then(|r| self.live_published(r));
                match live {
                    Ok(live) => {
                        out.state = State::Cached;
                        out.attempt = Attempt::First;
                        out.live = Some(live);
                        out.packages = self.rows(admission, |p| Self::committed(p, Data::CachedVerified));
                    }
                    Err(text) => self.publication_failed(out, admission, Attempt::First, text),
                }
                return;
            }
            Ok(mod_loader::lua::startup_host::PreparedEml::Staged(run)) => {
                // kiln-eml (0.2.0-dev.32): kiln mode's EML dialect gets a change journal too, from EML's own record of who
                // wrote what (env/writes.rs), once the generation is published
                record = run.record().map(crate::sf_startup::journal::Journal::from_eml);
                let (archive, commit) = run.into_parts();
                let published = archive.publish();
                (self.observe)(Point::Published);
                match published {
                    Ok(published) => {
                        if let Err(e) = commit.finish_after_publish(&published) {
                            out.warnings.push(Warning {
                                code: "eml-cache-not-saved".into(),
                                text: format!("{e:#}"),
                            });
                        }
                        published
                    }
                    Err(e) => return self.publication_failed(out, admission, Attempt::First, e.to_string()),
                }
            }
            Err(e) => return self.pass_failed(out, admission, Attempt::First, format!("{e:#}"), None, Vec::new()),
        };
        out.attempt = Attempt::First;
        match published
            .receipt_sha256()
            .ok_or_else(|| "no ownership receipt".to_owned())
            .and_then(|r| self.live_published(r))
        {
            Ok(live) => {
                out.state = State::Committed;
                out.packages = self.rows(admission, |p| Self::committed(p, Data::Committed));
                // the dialect's change journal (format 2, dialect eml), bound to this generation (the outcome's
                // archiveSha256): the plan's EML packages only (dialect none runs unlisted contributors, which the
                // registry names nowhere); a journal that cannot be saved costs only the names
                if listed
                    && let Some(journal) = &record
                    && let Err(e) = StartupJournal::bind(self.game(), self.stem(), self.guard)
                        .map_err(|e| format!("{e:#}"))
                        .and_then(|file| self.write_journal(&file, journal, &live, Attempt::First, &out.packages))
                {
                    out.warnings.push(Warning {
                        code: "startup-journal-not-saved".into(),
                        text: format!("the EML pass's change journal was not saved ({e}): Kiln's registry names none of its mods this start"),
                    });
                }
                out.live = Some(live);
            }
            Err(text) => self.publication_failed(out, admission, Attempt::First, text),
        }
    }
}
