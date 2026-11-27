// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: physical startup facade, independent of VM/dialect/admission/cache policy.
//!
//! The caller supplies the already validated baseline and retains its existing install lock and serial
//! runner reservation for the entire generation (and the install lock for process life). Opening a host
//! does not capture/seal a baseline, acquire another writer lock, inspect SF state, or claim live output.
//! Both SF pins finalize privately before commit (modules/mod-runtime/startup/commit_asset_changes.rs:35-36).
//! This facade uses ONLY Kiln's existing transaction/ownership/handoff publisher, not SF's transaction.
//!
//! `prepare_eml` runs EML's Lua pass on the host's one staged writer and stops before publication, returning the
//! private generation and a separate `CacheCommit`. `PreparedArchive::publish` is the sole publisher and returns the
//! generation's `PublishedArchive`; only that exact identity completes the change cache. `run_with` composes the same
//! three steps internally (lib.rs), unchanged.
//!
//! `StagedArchive::lend` lends the same one writer to the SF-dialect startup VM as `StageWriter` handles, and
//! `StartupHost::reference_reader` opens the captured baseline's reader; neither adds a writer, a lock or a path.
//! `LentStage::prepare_with` takes that writer back from every handle, then lets the pass serialize its dirty
//! resources into the same stage, then prepares it.
use crate::{
    Progress, RunArgs, Stage,
    cache::Fingerprints,
    ownership::{Digest, Publication, RecordedOrigin},
    transaction::{self, FileIdentity, TargetSnapshot},
};
pub use crate::ownership::SfHandoff;
pub use crate::ownership::{HostedShroudForge, hosted_shroudforge_output};
pub use crate::transaction::InputWitness;
use kfc::{
    container::{KFCCursor, KFCFile, KFCReader},
    guid::{ContentHash, ResourceId},
    reflection::TypeRegistry,
};
use mod_loader::ModEnvironment;
use std::{
    cell::RefCell,
    collections::BTreeMap,
    fs::{self, File, OpenOptions},
    io,
    path::{Path, PathBuf},
    rc::Rc,
};

/// The accepted working BASE's provenance, as its owner (the plugin wiring) labels it, keyed to the exact baseline this host
/// captured. Main's rule: a fully validated first-managed current set is `unclassified: game files as found when Kiln
/// first managed this install`, never vanilla. The facade carries the label verbatim from `StartupHost::with_origin`
/// to `PreparedArchive` and `PublishedArchive`; it never creates, upgrades or persists one (persistence in the
/// writer/activation metadata is the plugin wiring's interface, not this facade's).
///
/// kiln-eml: an origin made with `recorded` carries one of `RECORDED_ORIGINS`'s tokens too; a Deferred
/// (kiln-mode) publication writes it into the receipt (`baseOrigin`), keyed to the backups, in the same atomic write
/// as the backups. One made with `new` is never persisted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BaseOrigin {
    label: String,
    baseline: BaselineIdentity,
    token: Option<String>,
}
impl BaseOrigin {
    pub fn new(label: impl Into<String>, baseline: BaselineIdentity) -> Self {
        Self {
            label: label.into(),
            baseline,
            token: None,
        }
    }
    /// An origin a Deferred publication records: refused unless `token` and `label` are one of
    /// `RECORDED_ORIGINS` (never `unrecorded`, never anything more than unclassified).
    pub fn recorded(token: &str, label: &str, baseline: BaselineIdentity) -> anyhow::Result<Self> {
        if !crate::ownership::RECORDED_ORIGINS.iter().any(|&(t, l)| t == token && l == label) {
            anyhow::bail!("not a BASE origin a receipt records: {token}");
        }
        Ok(Self {
            label: label.into(),
            baseline,
            token: Some(token.into()),
        })
    }
    pub fn label(&self) -> &str {
        &self.label
    }
    pub fn baseline(&self) -> &BaselineIdentity {
        &self.baseline
    }
    pub fn token(&self) -> Option<&str> {
        self.token.as_deref()
    }
    /// What a receipt of `stem` records (keyed by the backup file names), when this origin is one it records.
    fn recorded_for(&self, stem: &str) -> Option<RecordedOrigin> {
        let baseline = self.baseline.0.iter().map(|(ext, d)| (format!("{stem}.{ext}"), d.clone())).collect();
        RecordedOrigin::new(self.token.as_deref()?, &self.label, baseline)
    }
}

/// The exact working baseline a host captured: size and SHA-256 of `<stem>.kfc.bak`, `<stem>.kfc_resources.bak` and
/// `<stem>.kfc.bak.integrity` (absent ones included as absent), the same files an ownership receipt lists as backups.
/// Opaque: only a host makes one, from its input witness.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BaselineIdentity(BTreeMap<&'static str, Option<Digest>>);
impl BaselineIdentity {
    /// kiln-eml: made by `InputWitness::capture` only.
    pub(crate) fn of(files: BTreeMap<&'static str, Option<Digest>>) -> Self {
        Self(files)
    }
    /// kiln-eml: each backup (`kfc.bak`, `kfc_resources.bak`, `kfc.bak.integrity`) with its size and
    /// SHA-256 (lower-case hex), None when absent: what the startup cache and the generation digest are keyed to.
    pub fn files(&self) -> impl Iterator<Item = (&'static str, Option<(u64, &str)>)> + '_ {
        self.0.iter().map(|(ext, d)| (*ext, d.as_ref().map(|d| (d.size, d.sha256()))))
    }
}

/// A single captured physical generation; not launch/admission authority.
/// `guard` is BORROWED, never unlocked or closed here. The host, stage, and prepared token cannot outlive it.
pub struct StartupHost<'guard> {
    game: PathBuf,
    name: String,
    types: Rc<TypeRegistry>,
    baseline: Rc<KFCFile>,
    witness: TargetSnapshot,
    guard: Guard<'guard>,
    started: bool,
    origin: Option<BaseOrigin>,
    handoff: SfHandoff,
}
struct Guard<'guard> {
    file: &'guard File,
    path: PathBuf,
    identity: FileIdentity,
}
impl<'guard> Guard<'guard> {
    fn bind(game: &Path, file: &'guard File) -> io::Result<Self> {
        let guard = Guard {
            file,
            path: game.join(".kiln-eml.lock"),
            identity: FileIdentity::of(file)?,
        };
        guard.check()?;
        Ok(guard)
    }
    /// The same held guard, bound again (each generation part checks it on its own).
    fn again(&self) -> io::Result<Guard<'guard>> {
        Ok(Guard {
            file: self.file,
            path: self.path.clone(),
            identity: FileIdentity::of(self.file)?,
        })
    }
    fn check(&self) -> io::Result<()> {
        transaction::regular(&self.path)?;
        // kiln-eml: in this crate's and kiln-eml's unit tests, a probe that gets the lock holds it under the test gate.
        #[cfg(any(test, feature = "test-gate"))]
        let _hold = crate::test_gate::lock_hold();
        let probe = OpenOptions::new().read(true).write(true).open(&self.path)?;
        if FileIdentity::of(self.file)? != self.identity
            || FileIdentity::of(&probe)? != self.identity
        {
            return Err(io::Error::other("install guard identity changed"));
        }
        match probe.try_lock() {
            Err(fs::TryLockError::WouldBlock) => Ok(()),
            Err(fs::TryLockError::Error(error)) => Err(error),
            Ok(()) => Err(io::Error::other("install guard is not held")),
        }
    }
}
impl<'guard> StartupHost<'guard> {
    /// Read-only binding to the canonical target, prevalidated reference and existing held install guard.
    /// This is not a replacement for the caller's admission/input/baseline validation or serial reservation.
    pub fn open(
        game: &Path,
        name: &str,
        types: Rc<TypeRegistry>,
        baseline: Rc<KFCFile>,
        guard: &'guard File,
    ) -> anyhow::Result<Self> {
        crate::ownership::check_stem(name)?;
        let game = fs::canonicalize(game)?;
        let guard = Guard::bind(&game, guard)?;
        let witness = TargetSnapshot::capture(&game, name, baseline.clone())?;
        Self::bind(game, name, types, baseline, guard, witness)
    }
    /// `open` over inputs witnessed earlier (the start's witness, captured before any DLL ran): refused unless
    /// `witness` covers exactly this archive in this folder and every input is still as witnessed, checked by its
    /// stamps (no large file is read). The host then checks those same proofs before every step, as `open`'s does.
    pub fn open_matching(
        game: &Path,
        name: &str,
        types: Rc<TypeRegistry>,
        baseline: Rc<KFCFile>,
        guard: &'guard File,
        witness: &InputWitness,
    ) -> anyhow::Result<Self> {
        crate::ownership::check_stem(name)?;
        let game = fs::canonicalize(game)?;
        let guard = Guard::bind(&game, guard)?;
        if !witness.covers(&game, name, baseline.containers().len()) {
            anyhow::bail!("the input witness was captured for another archive than this host's");
        }
        witness
            .check(&game)
            .map_err(|error| anyhow::anyhow!("the game's archive inputs changed since they were witnessed: {error}"))?;
        let witness = TargetSnapshot::witnessed(witness.clone(), baseline.clone())?;
        Self::bind(game, name, types, baseline, guard, witness)
    }
    fn bind(
        game: PathBuf,
        name: &str,
        types: Rc<TypeRegistry>,
        baseline: Rc<KFCFile>,
        guard: Guard<'guard>,
        witness: TargetSnapshot,
    ) -> anyhow::Result<Self> {
        // The reference is supplied by the caller, but its version/catalog/ranges must describe THIS backup.
        let actual = KFCFile::from_path(game.join(format!("{name}.kfc.bak")), false)?;
        if !same_reference(&baseline, &actual) {
            anyhow::bail!("reference does not match captured baseline");
        }
        for (i, info) in baseline.containers().iter().enumerate() {
            let path = game.join(format!("{name}_{i:03}.dat"));
            crate::ownership::checked_path(&game, &path)?;
            transaction::regular(&path)?;
            if fs::metadata(&path)?.len() != info.size {
                anyhow::bail!("baseline DAT size mismatch");
            }
        }
        for ext in ["kfc", "kfc_resources"] {
            transaction::regular(&game.join(format!("{name}.{ext}")))?;
        }
        witness.check_inputs(&game)?;
        Ok(Self {
            game,
            name: name.into(),
            types,
            baseline,
            witness,
            guard,
            started: false,
            origin: None,
            handoff: SfHandoff::Live,
        })
    }
    /// The validated type registry this host's writer encodes with (the SF-dialect pass's sessions use the same one).
    pub fn registry(&self) -> &Rc<TypeRegistry> {
        &self.types
    }
    /// The captured baseline's index (the pass's reference catalog).
    pub fn baseline(&self) -> &Rc<KFCFile> {
        &self.baseline
    }
    /// The archive stem this host writes.
    pub fn stem(&self) -> &str {
        &self.name
    }
    /// The working baseline this host captured (its input witness), for an origin to be keyed to.
    pub fn baseline_identity(&self) -> BaselineIdentity {
        self.witness.input().baseline().clone()
    }
    /// The input witness this host checks before every step.
    pub fn input_witness(&self) -> &InputWitness {
        self.witness.input()
    }
    /// kiln-eml: how this generation's publication or restore treats ShroudForge's folder (`SfHandoff`;
    /// Live unless set). Kiln mode sets Deferred.
    pub fn with_handoff(mut self, handoff: SfHandoff) -> Self {
        self.handoff = handoff;
        self
    }
    /// Binds the caller's BASE origin to this generation. Refused unless it is keyed to exactly the baseline captured.
    pub fn with_origin(mut self, origin: BaseOrigin) -> anyhow::Result<Self> {
        if origin.baseline != self.baseline_identity() {
            anyhow::bail!("BASE origin is keyed to another working baseline than the one captured");
        }
        self.origin = Some(origin);
        Ok(self)
    }
    /// EML's Lua pass over this generation, staged and NOT published. The host supplies the validated type
    /// registry, baseline, a reader of that baseline and its one staged writer (`begin_stage`); no second baseline,
    /// writer or lock is made. Every archive reader and the Lua VM are closed before this returns.
    ///
    /// A cache hit over output the ownership receipt proves live returns that published identity and stages nothing.
    /// Exports run as in `run_with` and are never skipped by the cache. Only patch runs without the runtime step are
    /// prepared (EML 0.2.0's runtime step registers DLLs Kiln does not load; kiln-eml passes it off). `env` must target
    /// this host's game folder and `args` its archive. One prepared run per host, even after a failure.
    pub fn prepare_eml(
        &mut self,
        env: &ModEnvironment,
        args: RunArgs,
        progress: &Progress,
    ) -> anyhow::Result<PreparedEml<'guard>> {
        if self.started {
            anyhow::bail!("startup generation already staged");
        }
        if !args.options.patch || args.options.runtime {
            anyhow::bail!("a prepared EML run patches the game data and has no runtime step");
        }
        if args.file_name != self.name || fs::canonicalize(env.game_dir())? != self.game {
            anyhow::bail!("the run targets another archive than this host's");
        }
        crate::log::info!("Preparing lua with options: {:?}", args);
        let mut prints = Fingerprints::new(&self.game).with_progress(progress.clone());
        prints.resume_from(env.cache_dir().as_std_path());
        crate::ownership::reset_read();
        let planned = match crate::check(env, args, progress, &mut prints) {
            crate::Checked::Hit { receipt } => {
                prints.finish();
                return Ok(PreparedEml::Cached(PublishedArchive {
                    published: crate::Published {
                        generation: crate::Generation::new(),
                        receipt: Some(receipt),
                        displaced: Vec::new(),
                    },
                    origin: self.origin.clone(),
                }));
            }
            crate::Checked::Run(planned) => *planned,
        };
        progress.stage(Stage::Prepare);
        let StagedArchive {
            patch,
            guard,
            origin,
            handoff,
            ..
        } = self.begin_stage()?;
        let game = camino::Utf8Path::from_path(&self.game)
            .ok_or_else(|| anyhow::anyhow!("non UTF-8 archive root"))?;
        let reader = crate::load::create_reader(game, &self.name)?;
        let app_state = crate::env::AppState::hosted(
            env.clone(),
            planned.args.clone(),
            self.types.clone(),
            self.baseline.clone(),
            reader,
            patch,
        );
        let executed = crate::execute(env, app_state, planned, progress, &mut prints)?;
        let applied = executed.applied;
        let crate::Executed { runner, staged, record, .. } = executed;
        // The VM, the AppState and its reader of the vanilla data close here, before a prepared token exists.
        drop(runner);
        let staged = staged.ok_or_else(|| anyhow::anyhow!("prepared EML run staged nothing"))?;
        guard.check()?;
        let generation = staged.completion.generation;
        let commit_guard = guard.again()?;
        let archive = PreparedArchive {
            archive: staged.writer.prepare().map_err(crate::load::stage_refusal(&self.name))?,
            guard,
            generation,
            origin,
            handoff,
            game: self.game.clone(),
            name: self.name.clone(),
        };
        Ok(PreparedEml::Staged(Box::new(PreparedEmlRun {
            archive,
            commit: CacheCommit {
                completion: staged.completion,
                env: env.clone(),
                prints,
                guard: commit_guard,
            },
            patched: applied,
            record,
        })))
    }
    /// A reader of the baseline this host captured (`<stem>.kfc.bak` and its resources), the same reader a prepared
    /// EML run reads the vanilla data with. The caller closes it before `PreparedArchive::publish`.
    pub fn reference_reader(&self) -> anyhow::Result<KFCCursor<KFCReader>> {
        self.guard.check()?;
        let game = camino::Utf8Path::from_path(&self.game)
            .ok_or_else(|| anyhow::anyhow!("non UTF-8 archive root"))?;
        crate::load::create_reader(game, &self.name)
    }
    /// One stage per host, even after abort/failure. A new attempt needs a newly captured host generation.
    pub fn begin_stage(&mut self) -> anyhow::Result<StagedArchive<'guard>> {
        if self.started {
            anyhow::bail!("startup generation already staged");
        }
        self.guard.check()?;
        self.witness.check_inputs(&self.game)?;
        self.started = true;
        let game = camino::Utf8Path::from_path(&self.game)
            .ok_or_else(|| anyhow::anyhow!("non UTF-8 archive root"))?;
        // kiln-eml: the writer takes over this host's witness (checked again there) instead of capturing the
        // inputs a second time, which would hash the resource backup again.
        let patch = crate::load::create_writer_over(game, &self.name, &self.types, &self.baseline, Some(self.witness.input()))?;
        self.witness.check_inputs(&self.game)?;
        Ok(StagedArchive {
            patch,
            guard: self.guard.again()?,
            generation: crate::Generation::new(),
            origin: self.origin.clone(),
            handoff: self.handoff,
            game: self.game.clone(),
            name: self.name.clone(),
        })
    }

    /// kiln-eml: the verified BASE instead of a generation (no plan contribution was committed: a double
    /// failure, nothing admitted): the host's one generation step, like `begin_stage`. Under the guard, with every input
    /// still as witnessed: output the ownership receipt proves is restored from the working baseline (`restore_backup`
    /// in this host's handoff mode); then the live header must be the backup header byte for byte, the live resource file
    /// must hold the resource backup's bytes from its start (a restore interrupted between the two files leaves chunks
    /// the BASE header never references after them) and every container the BASE header lists must have its size,
    /// whatever was live before. Output Kiln cannot prove it published is never touched: when it is not the BASE either,
    /// this refuses, except (kiln mode, Deferred) for Kiln's bundled ShroudForge's output whose record proves it was
    /// made over exactly the backups present (`ownership::hosted_shroudforge_output`), which is restored as owned output
    /// is (the backups pass the same checks); a record that names the live header but not the rest refuses. The
    /// receipt goes after a verified BASE (`baseOrigin` with it). Reads the resource backup and as much of the resource
    /// file in full.
    pub fn restore_base(&mut self) -> anyhow::Result<RestoredBase> {
        if self.started {
            anyhow::bail!("startup generation already staged");
        }
        self.started = true;
        self.guard.check()?;
        self.witness.check_inputs(&self.game)?;
        // kiln-eml: the change journal of the receipt's generation goes before the live files change (it never
        // names a generation that is not the live one)
        crate::ownership::forget_journal(&self.game, &self.name)?;
        let owned = crate::ownership::owns_output(&self.game, &self.name)?;
        let header = |ext: &str| crate::ownership::digest(&self.game, &self.game.join(format!("{}.{ext}", self.name)));
        let header_path = || {
            camino::Utf8PathBuf::from_path_buf(self.game.join(format!("{}.kfc", self.name)))
                .map_err(|_| anyhow::anyhow!("non UTF-8 archive root"))
        };
        let mut copied = owned;
        if owned {
            crate::load::restore_backup_with(&header_path()?, self.handoff)
                .map_err(|()| anyhow::anyhow!("the BASE could not be restored from the working baseline (see kiln.log)"))?;
        } else if header("kfc")? == header("kfc.bak")? {
            // the BASE header is live already (checked once: nothing was copied)
        } else {
            // kiln-eml: output no receipt lists. Kiln's bundled ShroudForge's output, proven by its record over the
            // backups present, goes back to the BASE in kiln mode (that ShroudForge stands aside in kiln mode and prepares
            // its output again from the same backups when it runs next); anything else is left as it is.
            use crate::ownership::HostedShroudForge;
            let hosted = match self.handoff {
                SfHandoff::Deferred => crate::ownership::hosted_shroudforge_output(&self.game, &self.name)?,
                SfHandoff::Live => HostedShroudForge::NotProven,
            };
            match hosted {
                HostedShroudForge::Output => {
                    crate::load::restore_backup_as(&header_path()?, self.handoff, crate::ownership::RestoreProof::HostedShroudForge)
                        .map_err(|()| {
                            anyhow::anyhow!(
                                "the live {}.kfc is what Kiln's bundled ShroudForge prepared, and its backup could not be put back \
                                 (see kiln.log). {VERIFY_GAME_FILES}",
                                self.name
                            )
                        })?;
                    copied = true;
                }
                HostedShroudForge::Stale(why) => anyhow::bail!(
                    "the live {}.kfc is what Kiln's bundled ShroudForge prepared, but {why}, so Kiln cannot prove its backup \
                     is this game's own data; left as it is. {VERIFY_GAME_FILES}",
                    self.name
                ),
                HostedShroudForge::NotProven => anyhow::bail!(
                    "the live {}.kfc is neither the BASE nor output Kiln can prove it published; left as it is. {VERIFY_GAME_FILES}",
                    self.name
                ),
            }
        }
        self.guard.check()?;
        // the BASE is what the game reads: the live header is the backup header byte for byte, and the live resource
        // file holds the resource backup's bytes from its start (a restore interrupted between the header and the
        // resource file leaves the published generation's appended chunks after them, which the BASE header never
        // references; the next publication replaces the file)
        if copied && header("kfc")? != header("kfc.bak")? {
            anyhow::bail!(
                "the live {}.kfc is neither the BASE nor output Kiln can prove it published; left as it is. {VERIFY_GAME_FILES}",
                self.name
            );
        }
        let resources = self.game.join(format!("{}.kfc_resources", self.name));
        let backup = self.game.join(format!("{}.kfc_resources.bak", self.name));
        crate::ownership::checked_path(&self.game, &resources)?;
        crate::ownership::checked_path(&self.game, &backup)?;
        transaction::regular(&resources)?;
        transaction::regular(&backup)?;
        if !same_prefix(&backup, &resources)? {
            anyhow::bail!(
                "the live {}.kfc_resources is neither the BASE nor output Kiln can prove it published; left as it is. \
                 {VERIFY_GAME_FILES}",
                self.name
            );
        }
        for (i, info) in self.baseline.containers().iter().enumerate() {
            let path = self.game.join(format!("{}_{i:03}.dat", self.name));
            crate::ownership::checked_path(&self.game, &path)?;
            transaction::regular(&path)?;
            if fs::metadata(&path)?.len() != info.size {
                anyhow::bail!("the BASE container {} does not have its size. {VERIFY_GAME_FILES}", path.display());
            }
        }
        let receipt_removed = crate::ownership::forget_receipt(&self.game, &self.name)?;
        Ok(RestoredBase {
            copied,
            receipt_removed,
        })
    }
}

/// kiln-eml: what a player does when kiln mode cannot put the game's own data back (the end of `restore_base`'s
/// refusals; the launcher shows it as the reason Kiln's interpreter did not start).
pub const VERIFY_GAME_FILES: &str =
    "Verify the game files in Steam (Properties, Installed Files), then start the game again from the Kiln launcher";

/// kiln-eml (stale game data, review): the refusal of a kiln-mode start whose working baseline holds a program a known
/// mod creates (Auto Loot Plus's auto-smash program and aura, `load.rs` `KNOWN_LEFTOVERS`), or, when the game's files are
/// about to become the working baseline (`live`), whose game files hold one; None when `header` holds none. Only the
/// archive header is looked at (no file is read here): a kiln-mode start builds from such a pair, so the mod's
/// changes stayed in the game data after the mod was removed (HadToRegister, 2026-11-26).
pub fn known_leftover_refusal(stem: &str, header: &KFCFile, live: bool) -> Option<String> {
    let found = crate::load::leftover(header)?;
    Some(if live {
        crate::load::stale_live_words(stem, &found)
    } else {
        crate::load::stale_words(stem, &format!("it holds {found}"))
    })
}

/// `whole` is, byte for byte, the start of `file` (which may be longer).
fn same_prefix(whole: &Path, file: &Path) -> io::Result<bool> {
    use io::Read;
    let (mut a, mut b) = (File::open(whole)?, File::open(file)?);
    if b.metadata()?.len() < a.metadata()?.len() {
        return Ok(false);
    }
    let (mut x, mut y) = (vec![0u8; 1 << 20], vec![0u8; 1 << 20]);
    loop {
        let n = a.read(&mut x)?;
        if n == 0 {
            return Ok(true);
        }
        b.read_exact(&mut y[..n])?;
        if x[..n] != y[..n] {
            return Ok(false);
        }
    }
}

/// What `StartupHost::restore_base` did: the BASE is live and verified either way.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RestoredBase {
    /// Owned output was live and the BASE was copied back over it (false: the BASE was live already).
    pub copied: bool,
    /// The ownership receipt was removed (its output is no longer live).
    pub receipt_removed: bool,
}

/// kiln-eml: one record kiln-eml's startup controller keeps beside EML's change cache
/// (`<game>/kiln/eml/cache/<name>`): read freely, written and removed only while the install guard is held, written by
/// an atomic replace as the receipts are. Never ownership proof on its own.
pub struct StartupRecord<'guard> {
    guard: Guard<'guard>,
    game: PathBuf,
    path: PathBuf,
}
impl<'guard> StartupRecord<'guard> {
    /// `name`: a plain `.json` file name (letters, digits, `-`, `_`, `.`). Refused unless `guard` is the held
    /// install guard of `game`.
    pub fn bind(game: &Path, name: &str, guard: &'guard File) -> anyhow::Result<Self> {
        if !name.ends_with(".json")
            || name.starts_with('.')
            || !name.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.'))
        {
            anyhow::bail!("not a startup record name: {name:?}");
        }
        let game = fs::canonicalize(game)?;
        let guard = Guard::bind(&game, guard)?;
        let path = game.join("kiln/eml/cache").join(name);
        crate::ownership::checked_path(&game, &path)?;
        Ok(Self { guard, game, path })
    }
    /// The record's bytes (at most 16 MiB), None when there is none.
    pub fn read(&self) -> io::Result<Option<Vec<u8>>> {
        crate::ownership::checked_path(&self.game, &self.path)?;
        if !self.path.try_exists()? {
            return Ok(None);
        }
        crate::ownership::bounded(&self.game, &self.path).map(Some)
    }
    /// Replaces the record atomically, under the held guard.
    pub fn write(&self, bytes: &[u8]) -> io::Result<()> {
        self.guard.check()?;
        let dir = self.path.parent().ok_or_else(|| io::Error::other("startup record without a folder"))?;
        crate::ownership::checked_path(&self.game, dir)?;
        fs::create_dir_all(dir)?;
        crate::ownership::checked_path(&self.game, &self.path)?;
        transaction::atomic_write(&self.path, bytes)
    }
    /// Removes the record (none is fine), under the held guard.
    pub fn remove(&self) -> io::Result<()> {
        self.guard.check()?;
        crate::ownership::checked_path(&self.game, &self.path)?;
        match fs::remove_file(&self.path) {
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
            other => other,
        }
    }
}

/// kiln-eml: the startup change journal of one archive, beside its ownership receipt
/// (`<game>/kiln/eml/ownership/<stem>.change-journal.json`): read freely; written and removed only while the install
/// guard is held, written by an atomic replace as the receipts are. The bytes are kiln-eml's (startup-journal.schema.json);
/// here only where they may be written: `write_for` refuses unless the receipt is still the one the journal is bound
/// to, and every publication and every verified BASE restore removes the journal before it changes the receipt or the
/// live files (`ownership::journal_path`). Never ownership proof.
pub struct StartupJournal<'guard> {
    guard: Guard<'guard>,
    game: PathBuf,
    stem: String,
}
impl<'guard> StartupJournal<'guard> {
    /// Refused unless `guard` is the held install guard of `game`.
    pub fn bind(game: &Path, stem: &str, guard: &'guard File) -> anyhow::Result<Self> {
        crate::ownership::check_stem(stem)?;
        let game = fs::canonicalize(game)?;
        let guard = Guard::bind(&game, guard)?;
        crate::ownership::journal_path(&game, stem)?;
        Ok(Self {
            guard,
            game,
            stem: stem.into(),
        })
    }
    /// The journal's bytes (at most 16 MiB), None when there is none.
    pub fn read(&self) -> io::Result<Option<Vec<u8>>> {
        let path = crate::ownership::journal_path(&self.game, &self.stem)?;
        if !path.try_exists()? {
            return Ok(None);
        }
        crate::ownership::bounded(&self.game, &path).map(Some)
    }
    /// Replaces the journal atomically, under the held guard, only while the ownership receipt's SHA-256 is
    /// `receipt_sha256` (lower-case hex): the generation the journal is bound to is the receipt's.
    pub fn write_for(&self, receipt_sha256: &str, bytes: &[u8]) -> io::Result<()> {
        self.guard.check()?;
        if crate::ownership::marker(&self.game, &self.stem)?.as_deref() != Some(receipt_sha256) {
            return Err(io::Error::other(
                "the change journal is not written: the ownership receipt is no longer the one it is bound to",
            ));
        }
        crate::ownership::directory(&self.game)?;
        let path = crate::ownership::journal_path(&self.game, &self.stem)?;
        transaction::atomic_write(&path, bytes)
    }
    /// Removes the journal (none is fine), under the held guard; Ok(true) when there was one.
    pub fn remove(&self) -> io::Result<bool> {
        self.guard.check()?;
        crate::ownership::forget_journal(&self.game, &self.stem)
    }
}

/// kiln-eml: the working baseline of the archive `header` (`<game>/<stem>.kfc`) captured, resealed or refreshed
/// for another game build (`create_backup`'s rules) in ShroudForge handoff mode `handoff`, under the held install
/// guard. Kiln mode does this in its early witness only, before any package DLL runs.
pub fn capture_baseline(header: &Path, guard: &File, handoff: SfHandoff) -> anyhow::Result<()> {
    let game = header.parent().ok_or_else(|| anyhow::anyhow!("an archive without a folder"))?;
    let guard = Guard::bind(&fs::canonicalize(game)?, guard)?;
    let header = camino::Utf8Path::from_path(header).ok_or_else(|| anyhow::anyhow!("non UTF-8 archive path"))?;
    crate::load::create_backup_with(header, handoff)?;
    guard.check()?;
    Ok(())
}

/// kiln-eml: a kiln-mode start whose inspection found the ownership receipt's working baseline gone
/// and none of the live game files Kiln's output from it (the inspector's note `receipt-stale`) keeps that receipt as
/// a recovery copy before it takes a new baseline: renamed to `kiln/eml/ownership/<stem>.stale-<uuid>.json` beside it,
/// never deleted, and only while it is the very receipt the inspection judged (`receipt_sha256`, the inspection's
/// `receipt.sha256`), one this build understands, and no backup file is back in the game folder. Its change journal goes
/// first (it names a generation that is no longer live). Under the held install guard `guard`; ShroudForge's folder is
/// not touched (a handoff an earlier kiln-mode write owes stays owed). Returns the recovery copy's path.
pub fn set_aside_stale_receipt(
    game: &Path,
    stem: &str,
    guard: &File,
    receipt_sha256: &str,
) -> anyhow::Result<PathBuf> {
    use sha2::Digest as _;
    let root = fs::canonicalize(game)?;
    let guard = Guard::bind(&root, guard)?;
    crate::ownership::check_stem(stem)?;
    for ext in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
        let backup = root.join(format!("{stem}.{ext}"));
        crate::ownership::checked_path(&root, &backup)?;
        match fs::symlink_metadata(&backup) {
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
            Ok(_) => anyhow::bail!("{stem}.{ext} is in the game folder: the working baseline is not lost"),
        }
    }
    let dir = root.join("kiln/eml/ownership");
    let receipt = dir.join(format!("{stem}.json"));
    crate::ownership::checked_path(&root, &receipt)?;
    let bytes = crate::ownership::bounded(&root, &receipt)?;
    if format!("{:x}", sha2::Sha256::digest(&bytes)) != receipt_sha256 {
        anyhow::bail!("the ownership receipt changed since the game files were inspected");
    }
    if crate::ownership::readonly::receipt(stem, &bytes).is_none() {
        anyhow::bail!("the ownership receipt is not one this Kiln understands; it is kept as it is");
    }
    crate::ownership::forget_journal(&root, stem)?;
    let stale = dir.join(format!("{stem}.stale-{}.json", uuid::Uuid::new_v4()));
    crate::ownership::checked_path(&root, &stale)?;
    if fs::symlink_metadata(&stale).is_ok() {
        anyhow::bail!("{} is there already", stale.display());
    }
    fs::rename(&receipt, &stale)?;
    transaction::sync_dir(&dir)?;
    guard.check()?;
    Ok(stale)
}

/// kiln-eml: SHA-256 (lower-case hex) of `stem`'s ownership receipt, None when there is none.
pub fn receipt_sha256(game: &Path, stem: &str) -> io::Result<Option<String>> {
    crate::ownership::marker(game, stem)
}

/// kiln-eml: the generation the ownership receipt with SHA-256 `receipt_sha256` lists is the live one, by
/// the start's witnessed header (its SHA-256 equals the receipt's, with nothing unproven) and the sizes the receipt
/// lists for every other file and backup. Reads only the receipt; the startup cache's hit check.
pub fn vouch_live(witness: &InputWitness, receipt_sha256: &str) -> io::Result<bool> {
    let Some(header) = witness.parsed("kfc") else {
        return Ok(false);
    };
    crate::ownership::vouched_header(witness.game(), witness.stem(), receipt_sha256, header)
}
fn same_reference(a: &KFCFile, b: &KFCFile) -> bool {
    a.game_version() == b.game_version()
        && a.containers()
            .iter()
            .map(|c| (c.size, c.count))
            .eq(b.containers().iter().map(|c| (c.size, c.count)))
        && a.resource_chunks()
            .iter()
            .map(|c| {
                (
                    c.offset,
                    c.size,
                    c.compressed_size,
                    c.uncompressed_offset,
                    c.uncompressed_size,
                )
            })
            .eq(b.resource_chunks().iter().map(|c| {
                (
                    c.offset,
                    c.size,
                    c.compressed_size,
                    c.uncompressed_offset,
                    c.uncompressed_size,
                )
            }))
        && a.resources()
            .iter()
            .map(|(id, e)| (id, e.offset, e.size))
            .eq(b.resources().iter().map(|(id, e)| (id, e.offset, e.size)))
        && a.contents()
            .iter()
            .map(|(id, e)| (id, e.offset, e.container_index))
            .eq(b
                .contents()
                .iter()
                .map(|(id, e)| (id, e.offset, e.container_index)))
}

/// Private mutable bytes with write-ahead cleanup proof; drop is abort, not a live restore.
pub struct StagedArchive<'guard> {
    patch: transaction::PatchWriter,
    guard: Guard<'guard>,
    generation: crate::Generation,
    origin: Option<BaseOrigin>,
    handoff: SfHandoff,
    game: PathBuf,
    name: String,
}
impl<'guard> StagedArchive<'guard> {
    pub fn write_resource(&mut self, id: &ResourceId, bytes: &[u8]) -> io::Result<()> {
        self.patch.write_resource(id, bytes)?;
        failpoint!("stage.serialized-resource-written", Path::new("stage/resource"));
        Ok(())
    }
    pub fn write_content(&mut self, id: &ContentHash, bytes: &[u8]) -> io::Result<()> {
        self.patch.write_content(id, bytes)
    }
    /// Consumes/closes the writer and all validation readers; never publishes or claims ownership.
    pub fn prepare(self) -> anyhow::Result<PreparedArchive<'guard>> {
        self.guard.check()?;
        Ok(PreparedArchive {
            archive: self.patch.prepare().map_err(crate::load::stage_refusal(&self.name))?,
            guard: self.guard,
            generation: self.generation,
            origin: self.origin,
            handoff: self.handoff,
            game: self.game,
            name: self.name,
        })
    }
}

impl<'guard> StagedArchive<'guard> {
    /// Lends this stage's one writer to a Lua VM whose functions must own what they capture (the SF-dialect startup
    /// pass, the asset session): `LentStage::writer` hands out `'static` handles to the SAME writer, never a second one or a
    /// path. `LentStage::prepare` takes the writer back and prepares as `prepare` does; from then on (and once the
    /// lent stage is dropped, which aborts like dropping this stage) every handle refuses to write.
    pub fn lend(self) -> LentStage<'guard> {
        let StagedArchive {
            patch,
            guard,
            generation,
            origin,
            handoff,
            game,
            name,
        } = self;
        LentStage {
            writer: StageWriter(Rc::new(RefCell::new(Some(patch)))),
            rest: Some(Lent {
                guard,
                generation,
                origin,
                handoff,
                game,
                name,
            }),
        }
    }
}

/// A stage whose writer is lent out (`StagedArchive::lend`). Dropping it closes the writer for every handle and
/// aborts the stage, as dropping a `StagedArchive` does.
pub struct LentStage<'guard> {
    writer: StageWriter,
    rest: Option<Lent<'guard>>,
}
struct Lent<'guard> {
    guard: Guard<'guard>,
    generation: crate::Generation,
    origin: Option<BaseOrigin>,
    handoff: SfHandoff,
    game: PathBuf,
    name: String,
}
impl<'guard> LentStage<'guard> {
    /// A handle to the lent writer.
    pub fn writer(&self) -> StageWriter {
        self.writer.clone()
    }
    /// Takes the writer back from every handle, then prepares exactly as `StagedArchive::prepare`.
    pub fn prepare(self) -> anyhow::Result<PreparedArchive<'guard>> {
        self.prepare_with(|_| Ok(()))
    }
    /// Takes the writer back from every handle FIRST, then lets `serialize` write into the taken-back stage (the
    /// SF pass's dirty resources, as SF takes its writer before it serializes them, commit_asset_changes.rs:12), then
    /// prepares exactly as `StagedArchive::prepare`. No Lua handle can write from the moment this is called, so nothing
    /// a finalizer or a resumed coroutine attempts meanwhile reaches the generation. An error from `serialize` drops
    /// the stage, which aborts it.
    pub fn prepare_with(
        mut self,
        serialize: impl FnOnce(&mut StagedArchive<'guard>) -> anyhow::Result<()>,
    ) -> anyhow::Result<PreparedArchive<'guard>> {
        let patch = self
            .writer
            .close()
            .ok_or_else(|| anyhow::anyhow!("the staged archive writer is already closed"))?;
        let Lent {
            guard,
            generation,
            origin,
            handoff,
            game,
            name,
        } = self.rest.take().ok_or_else(|| anyhow::anyhow!("the lent stage is already prepared"))?;
        let mut stage = StagedArchive {
            patch,
            guard,
            generation,
            origin,
            handoff,
            game,
            name,
        };
        failpoint!("stage.lent-taken-back", Path::new("stage/writer"));
        serialize(&mut stage)?;
        stage.prepare()
    }
}
impl Drop for LentStage<'_> {
    fn drop(&mut self) {
        // Abort now, not when the last Lua handle goes: the writer (and with it the private stage) is dropped here.
        drop(self.writer.close());
    }
}

/// A `'static` handle to a lent stage's one writer (`LentStage::writer`). Worker-local (`Rc`): only the thread that
/// runs the VM holding it writes through it. Writes after the stage was taken back or dropped are refused.
///
/// Contents only: SF writes a content the moment a package creates it, but resources only when it serializes them,
/// after taking its writer back, so a resource goes through `LentStage::prepare_with`'s taken-back stage instead.
#[derive(Clone)]
pub struct StageWriter(Rc<RefCell<Option<transaction::PatchWriter>>>);
impl StageWriter {
    pub fn write_content(&self, id: &ContentHash, bytes: &[u8]) -> io::Result<()> {
        self.with(|patch| patch.write_content(id, bytes))?;
        failpoint!("stage.lent-content-written", Path::new("stage/content"));
        Ok(())
    }
    /// Whether the stage still lends its writer.
    pub fn is_open(&self) -> bool {
        self.0.borrow().is_some()
    }
    fn with(&self, write: impl FnOnce(&mut transaction::PatchWriter) -> io::Result<()>) -> io::Result<()> {
        match self.0.borrow_mut().as_mut() {
            Some(patch) => write(patch),
            None => Err(io::Error::other("the staged archive writer is closed")),
        }
    }
    fn close(&self) -> Option<transaction::PatchWriter> {
        self.0.borrow_mut().take()
    }
}

/// Immutable PRIVATE parsed/read-validated set. There is deliberately no Clone or writer/path accessor.
/// A prepared view is NOT proof of live ownership. `publish` consumes it even on failure.
///
/// ```compile_fail
/// use mod_loader_lua::startup_host::PreparedArchive;
/// fn twice(prepared: PreparedArchive<'_>) {
///     prepared.publish().unwrap();
///     prepared.publish().unwrap(); // moved, not a second publication
/// }
/// ```
pub struct PreparedArchive<'guard> {
    archive: transaction::PreparedArchive,
    guard: Guard<'guard>,
    generation: crate::Generation,
    origin: Option<BaseOrigin>,
    handoff: SfHandoff,
    game: PathBuf,
    name: String,
}
impl PreparedArchive<'_> {
    pub fn index(&self) -> &KFCFile {
        self.archive.index()
    }
    pub fn read_resource(&self, id: &ResourceId) -> io::Result<Option<Vec<u8>>> {
        self.archive.read_resource(id)
    }
    pub fn read_content(&self, id: &ContentHash) -> io::Result<Option<Vec<u8>>> {
        self.archive.read_content(id)
    }
    /// The BASE origin bound to the host this generation was staged on, verbatim.
    pub fn origin(&self) -> Option<&BaseOrigin> {
        self.origin.as_ref()
    }
    /// No caller path. Revalidates the same target/baseline/guard/staged bytes, then invokes the sole publisher.
    /// The caller must already have closed other archive readers/jobs and completed admission/input barriers.
    /// Returns this generation's published identity: the only one its `CacheCommit` accepts.
    /// kiln-eml: in the host's ShroudForge handoff mode; a Deferred publication records the host's BASE origin
    /// in the receipt when it is one a receipt records (`BaseOrigin::recorded`), else the earlier receipt's while it is
    /// keyed to the same backups.
    pub fn publish(self) -> io::Result<PublishedArchive> {
        self.guard.check()?;
        let publication = Publication {
            handoff: self.handoff,
            origin: match self.handoff {
                SfHandoff::Deferred => self.origin.as_ref().and_then(|o| o.recorded_for(&self.name)),
                SfHandoff::Live => None,
            },
        };
        let published = crate::publish(self.archive, self.generation, &self.game, &self.name, &publication)?;
        Ok(PublishedArchive {
            published,
            origin: self.origin,
        })
    }
}

/// What `StartupHost::prepare_eml` produced.
pub enum PreparedEml<'guard> {
    /// Nothing changed since the generation the ownership receipt proves live: its identity, no stage, nothing to
    /// publish and no cache to complete.
    Cached(PublishedArchive),
    /// A new private generation and the change cache it may save once published.
    Staged(Box<PreparedEmlRun<'guard>>),
}

/// A staged EML run: the private archive and its cache completion, owned separately.
///
/// ```compile_fail
/// use mod_loader_lua::startup_host::PreparedEmlRun;
/// fn twice(run: PreparedEmlRun<'_>) {
///     let (archive, commit) = run.into_parts();
///     let published = archive.publish().unwrap();
///     commit.finish_after_publish(&published).unwrap();
///     commit.finish_after_publish(&published).unwrap(); // moved: one completion per generation
/// }
/// ```
pub struct PreparedEmlRun<'guard> {
    archive: PreparedArchive<'guard>,
    commit: CacheCommit<'guard>,
    patched: usize,
    /// kiln-eml: who wrote what in the run (its change journal's record, env/writes.rs); kiln mode's EML dialect writes it
    /// once the generation is published (0.2.0-dev.32).
    record: Option<crate::WriteRecord>,
}
impl<'guard> PreparedEmlRun<'guard> {
    /// kiln-eml: the run's record of who wrote what (None: the run recorded nothing).
    pub fn record(&self) -> Option<&crate::WriteRecord> {
        self.record.as_ref()
    }
    pub fn archive(&self) -> &PreparedArchive<'guard> {
        &self.archive
    }
    /// Resources the run wrote into the stage.
    pub fn patched(&self) -> usize {
        self.patched
    }
    pub fn into_parts(self) -> (PreparedArchive<'guard>, CacheCommit<'guard>) {
        (self.archive, self.commit)
    }
}

/// The change cache a prepared EML run may save: never ownership proof, and never saved by preparation alone.
/// kiln-eml: it borrows the install guard its generation was staged under and saves nothing
/// once that guard is no longer held.
pub struct CacheCommit<'guard> {
    completion: crate::Completion,
    env: ModEnvironment,
    prints: Fingerprints,
    guard: Guard<'guard>,
}
impl CacheCommit<'_> {
    /// Saves `files.json` only for this run's own published generation, while the receipt that publication wrote is
    /// still the live one and every mod and game input is as it was before the Lua pass. Otherwise any saved cache is
    /// removed (the next start runs again) and this returns an error; the publication itself stands either way.
    /// Refused, writing nothing, once the install guard is no longer held.
    pub fn finish_after_publish(mut self, published: &PublishedArchive) -> anyhow::Result<()> {
        if let Err(error) = self.guard.check() {
            anyhow::bail!("change cache not saved: the install guard is no longer held ({error})");
        }
        if self
            .completion
            .finish(&published.published, &self.env, &mut self.prints)
        {
            self.prints.finish();
            Ok(())
        } else {
            anyhow::bail!(
                "change cache not saved: another generation, receipt or input than this run prepared"
            )
        }
    }
}

/// One live generation: published by this facade, or proven live by a cache hit.
pub struct PublishedArchive {
    published: crate::Published,
    origin: Option<BaseOrigin>,
}
impl PublishedArchive {
    /// SHA-256 of the ownership receipt this generation's output is listed in.
    pub fn receipt_sha256(&self) -> Option<&str> {
        self.published.receipt.as_deref()
    }
    /// ShroudForge asset mods (`<id> <version>`) whose prepared output this publication replaced.
    pub fn sf_displaced(&self) -> &[String] {
        &self.published.displaced
    }
    pub fn origin(&self) -> Option<&BaseOrigin> {
        self.origin.as_ref()
    }
}
