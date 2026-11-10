// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: hash each file once, not once per barrier, and not at all when nothing needs its bytes. The change cache and
// the input barriers of `run` fingerprint the game's data files (55 containers, about 48 GiB, on a real install) up to
// four times per start; reading all of it each time made a patch run read ~190 GiB and a cache hit ~48 GiB
// (bisdat/kiln-mods#17, whose analysis of the problem and of OS change stamps this follows; the code is written here).
//
// A fingerprint is (length, last-write time, CRC-64 of every byte), or, for a file nobody needs the bytes of, (length,
// last-write time, a digest of the OS stamp): see below. What changes is when the bytes are read.
//
// The stamp (`Stamp`: volume, file id, change time) says "this is the same file, unchanged since it was recorded". It
// is trusted only when
//   - the stamp is a strong one (the platform reports all of it), and
//   - the file's volume passed a scratch-file test in this run, made in the game folder: a same-size rewrite with
//     the last-write time put back moves the change time there (`stamp::probe`; so size and last-write time are never
//     the evidence), and
//   - the file was not changed in the last RACY_WINDOW, so that a later change cannot share its change time with
//     the one recorded (a coarse clock; the same rule as git's racily-clean entries), and
//   - length and last-write time are unchanged as well, and a file that was read was unchanged while it was read.
// KILN_EML_FULL_HASH=1 turns every use of the stamp off.
//
// Two uses of it, both resting on that one assumption:
//   - REUSE: a recorded CRC is taken again while the stamp is the one it was recorded under (dev.21).
//   - RECORD (`Need::Identity`, the game's own data files): a file with no CRC on record is not read at all. Its
//     fingerprint is its length, its last-write time and a digest of its stamp, marked `unread` in the cache file. A
//     later start finds the same stamp (unchanged, same fingerprint) or another (changed: the patch runs again). The
//     CRC was never anything else than "the content that goes with this stamp", and nothing in a run consumes a CRC
//     of a game file except comparing it with the one saved: the bytes the patch needs it reads itself, and the
//     backups are sealed by their own checks (load.rs). Where no stamp can vouch for a file (see above) it is read in
//     full, as before.
//
// Two places hold recorded fingerprints: `memo` (this run: every barrier re-stats instead of re-hashing, and a file
// replaced or edited since is read again) and `seed` (the cache file of the last run, which is how a start that
// changes nothing reads nothing). A third, `resume`, holds what an interrupted run had read (`journal`).

use std::{collections::{HashMap, HashSet}, fs::File, io::Read, path::{Path, PathBuf}, time::{Duration, Instant, SystemTime, UNIX_EPOCH}};

use crate::{log::{debug, info, warn}, progress::{Progress, human}};

use super::stamp::{self, Reader, Stamp};

/// (length, last-write time in ns, CRC-64 or stamp digest): the entry of `FileStateCache::files`.
pub(super) type Sig = (u64, u128, u64);

/// CRC-64/ECMA-182 as before, computed with sixteen-way lookup tables: the same values, several times faster than the
/// `crc` crate's default byte-at-a-time table (about 0.4 GiB/s, which alone made reading 48 GiB take two minutes).
pub(crate) static CRC64: crc::Crc<u64, crc::Table<16>> = crc::Crc::<u64, crc::Table<16>>::new(&crc::CRC_64_ECMA_182);

/// A change newer than this is not trusted as a stamp: the file system's clock may not have moved on since.
const RACY_WINDOW: Duration = Duration::from_secs(3);
const BUFFER: usize = 1 << 20;
/// The journal of an interrupted run is written at most this often.
const JOURNAL_EVERY: Duration = Duration::from_secs(2);
const JOURNAL: &str = "files.partial.json";

#[cfg(test)]
thread_local! { pub(crate) static RACY_WINDOW_OVERRIDE: std::cell::Cell<Option<Duration>> = const { std::cell::Cell::new(None) }; }

fn racy_window() -> Duration {
    #[cfg(test)]
    if let Some(window) = RACY_WINDOW_OVERRIDE.with(|w| w.get()) {
        return window;
    }
    RACY_WINDOW
}

/// What the caller needs of a file.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Need {
    /// The CRC-64 of its bytes: the mods' files, which Lua reads anyway, and anything a test wants checked by content.
    Content,
    /// Only "the same file, unchanged since it was recorded": the game's data files. Read only where no stamp can
    /// vouch for them.
    Identity,
}

/// What `Fingerprints::fingerprint` found.
#[derive(Clone, Copy, Debug)]
pub(super) struct Fingerprint {
    pub sig: Sig,
    /// The stamp the fingerprint was taken under, when it can tell a later change.
    pub stamp: Option<Stamp>,
    /// `sig.2` is a digest of the stamp, not of the bytes.
    pub unread: bool,
}

#[derive(Clone, Copy, Debug)]
struct Known {
    sig: Sig,
    stamp: Option<Stamp>,
    unread: bool,
}

/// What the run did to get a file's fingerprint.
enum Action {
    Reuse(Known),
    Record,
    Read(Why),
}

/// Why a file is read in full.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Why {
    /// KILN_EML_FULL_HASH is set.
    FullHash,
    /// The volume failed the scratch-file test (or names no volume).
    UntrustedVolume,
    /// The file system gave no usable stamp.
    NoStamp,
    /// Changed in the last few seconds.
    Recent,
    /// The saved fingerprint is a CRC that no stamp stands behind, or the file changed since: the CRC decides.
    Compare,
    /// A mod's file: those are always fingerprinted by content.
    Mod,
    /// Read earlier in this run (a recent change): the same kind of fingerprint at every step.
    Pinned,
}

impl Why {
    fn text(self) -> &'static str {
        match self {
            Why::FullHash => "KILN_EML_FULL_HASH is set",
            Why::UntrustedVolume => "change stamps are not trusted on this volume",
            Why::NoStamp => "the file system gave no change stamp",
            Why::Recent => "changed in the last few seconds",
            Why::Compare => "to compare with the CRC the last run saved",
            Why::Mod => "mod files are fingerprinted by content",
            Why::Pinned => "read earlier in this run",
        }
    }
}

/// What the run is about to do with a batch of files, for the log and the progress line.
#[derive(Clone, Debug, Default)]
pub struct Plan {
    pub files: usize,
    pub bytes: u64,
    pub read_files: usize,
    pub read_bytes: u64,
    pub recorded_files: usize,
    pub recorded_bytes: u64,
    pub reused_files: usize,
    pub reused_bytes: u64,
    reasons: Vec<(Why, usize)>,
}

impl Plan {
    fn why(&mut self, why: Why) {
        match self.reasons.iter_mut().find(|(w, _)| *w == why) {
            Some((_, n)) => *n += 1,
            None => self.reasons.push((why, 1)),
        }
    }

    /// `Game data: 66 file(s), 48.8 GiB: reading 2 file(s) (224.1 MiB) ...`
    pub fn describe(&self, what: &str) -> String {
        let mut parts = Vec::new();
        if self.read_files > 0 {
            let mut reasons = self.reasons.clone();
            reasons.sort_by_key(|(_, n)| std::cmp::Reverse(*n));
            let why = reasons.iter().take(2).map(|(w, n)| format!("{} ({n})", w.text())).collect::<Vec<_>>().join("; ");
            parts.push(format!("reading {} file(s) ({}): {why}", self.read_files, human(self.read_bytes)));
        }
        if self.recorded_files > 0 {
            parts.push(format!("{} file(s) ({}) recorded by their change stamp, not read", self.recorded_files, human(self.recorded_bytes)));
        }
        if self.reused_files > 0 {
            parts.push(format!("{} file(s) ({}) already recorded, not read", self.reused_files, human(self.reused_bytes)));
        }
        format!("{what}: {} file(s), {}: {}", self.files, human(self.bytes), parts.join("; "))
    }
}

/// What the fingerprinting of one run cost: logged by `run`, returned in `RunResult`, shown in kiln-eml's summary.
#[derive(Clone, Copy, Debug, Default, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FingerprintStats {
    /// Bytes read to compute a CRC.
    pub hashed_bytes: u64,
    pub hashed_files: usize,
    /// Files whose recorded CRC was reused because the OS stamp showed them unchanged.
    pub reused_files: usize,
    /// Bytes of those files, which were not read.
    pub reused_bytes: u64,
    /// Files recorded by their stamp alone (nothing was read), and their bytes.
    pub recorded_files: usize,
    pub recorded_bytes: u64,
    /// Time spent fingerprinting, in ms.
    pub ms: f64,
}

impl FingerprintStats {
    /// `hashed 0 B, reused 57 file(s)`: for a summary line that has the time already.
    pub fn brief(&self) -> String {
        let recorded = if self.recorded_files > 0 { format!(", recorded {} file(s) unread", self.recorded_files) } else { String::new() };
        format!("hashed {}, reused {} file(s){recorded}", human(self.hashed_bytes), self.reused_files)
    }

    /// Bytes of files this run did not have to read.
    pub fn not_read(&self) -> u64 {
        self.reused_bytes + self.recorded_bytes
    }
}

impl std::fmt::Display for FingerprintStats {
    /// `hashed 1.2 GiB in 12 file(s), recorded 43 file(s) (47.0 GiB unread), reused 12 file(s) (1.0 GiB not read), 31 ms`
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "hashed {} in {} file(s)", human(self.hashed_bytes), self.hashed_files)?;
        if self.recorded_files > 0 {
            write!(f, ", recorded {} file(s) ({} unread)", self.recorded_files, human(self.recorded_bytes))?;
        }
        write!(f, ", reused {} file(s) ({} not read), {:.0} ms", self.reused_files, human(self.reused_bytes), self.ms)
    }
}

/// Is the change stamp of a file's volume to be believed? Decided once per run by a scratch-file test in the game folder.
///
/// The game folder is where the test may write: it is Kiln's to write in anyway (backups, patched files, the install
/// lock), and the folder itself is not one of the entries the change cache records. Mod folders are never written to.
/// A file is trusted when it is in the game folder and the test passed, or when it is anywhere else on the very volume
/// the test ran on, if the OS names that volume (Wine reports serial number 0 for some mounts, and then different
/// mounts look alike: files elsewhere are read in full).
struct Gate {
    folder: PathBuf,
    /// The volume the test ran on, or why it did not pass.
    verdict: Result<u64, String>,
}

impl Gate {
    /// Runs the test now, before the first file is looked at: a verdict that arrived later would make the files read
    /// before it (mods, on a cold start) be read again.
    fn test(folder: &Path, read: Reader) -> Self {
        let verdict = stamp::probe(folder, read);
        match &verdict {
            Ok(tested) => info!(
                folder = folder.display().to_string(), volume = format!("{tested:#x}"),
                "Change stamps are reliable here; unchanged files are not read again"
            ),
            Err(why) => warn!(
                folder = folder.display().to_string(), reason = why.as_str(),
                "Change stamps are not trusted here; every file is read in full"
            ),
        }
        Self { folder: folder.to_owned(), verdict }
    }

    fn off(folder: &Path) -> Self {
        Self { folder: folder.to_owned(), verdict: Err("reuse is off".into()) }
    }

    fn trusts(&self, path: &Path, volume: u64) -> bool {
        let here = path.parent() == Some(self.folder.as_path());
        matches!(self.verdict, Ok(tested) if tested == volume && (here || volume != 0))
    }
}

/// What an interrupted run had read: written while a run reads, removed when it ends well (see `Fingerprints::resume_from`).
struct Journal {
    path: PathBuf,
    every: Duration,
    last: Instant,
}

pub struct Fingerprints {
    /// The saved cache (`files.json`): what the fingerprints of this run are compared with.
    seed: HashMap<PathBuf, Known>,
    /// What an interrupted run had read. Taken for reuse exactly as `seed`, but never compared with.
    resume: HashMap<PathBuf, Known>,
    memo: HashMap<PathBuf, Known>,
    /// Files read in full in this run whose fingerprint has no stamp behind it (changed a moment ago): they are read at
    /// every step, so that the fingerprints of one run stay comparable.
    pinned: HashSet<PathBuf>,
    /// The fingerprints this run read (and the resumed ones it used): what the journal holds.
    read_now: HashMap<PathBuf, Known>,
    /// The saved cache cannot be a hit (another build, a fingerprint that differs already): nothing will be compared
    /// with it any more, so a file need not be read to be compared.
    moot: bool,
    journal: Option<Journal>,
    /// The paths announced by `announce` and not read yet: their bytes are in the progress total already.
    announced: HashSet<PathBuf>,
    gate: Gate,
    read: Reader,
    reuse: bool,
    window: Duration,
    buffer: Vec<u8>,
    stats: FingerprintStats,
    progress: Progress,
}

impl Fingerprints {
    /// `game_dir` is where the volume test writes its scratch file (it runs here, so a run that cannot use stamps says
    /// so before it reads anything).
    pub fn new(game_dir: &Path) -> Self {
        let off = full_hash_requested(std::env::var_os("KILN_EML_FULL_HASH").as_deref());
        if off {
            info!("KILN_EML_FULL_HASH is set: every file is read in full at every step");
        }
        Self::with(game_dir, stamp::read, !off, racy_window())
    }

    pub(super) fn with(game_dir: &Path, read: Reader, reuse: bool, window: Duration) -> Self {
        Self {
            seed: HashMap::new(),
            resume: HashMap::new(),
            memo: HashMap::new(),
            pinned: HashSet::new(),
            read_now: HashMap::new(),
            moot: false,
            journal: None,
            announced: HashSet::new(),
            gate: if reuse { Gate::test(game_dir, read) } else { Gate::off(game_dir) },
            read,
            reuse,
            window,
            buffer: Vec::new(),
            stats: FingerprintStats::default(),
            progress: Progress::none(),
        }
    }

    /// Reports what is read into `progress` (the run's counters: file, bytes done and to do).
    pub fn with_progress(mut self, progress: Progress) -> Self {
        self.progress = progress;
        self
    }

    /// The fingerprints the last run saved (`files.json`), with the stamps they were taken under.
    pub fn seed(&mut self, saved: &super::FileStateCache) {
        for (path, &sig) in &saved.files {
            let stamp = saved.stamps.get(path).copied();
            let unread = saved.unread.contains(path);
            if unread && stamp.is_none() {
                continue; // a digest of a stamp nobody kept: no evidence of anything
            }
            self.seed.insert(path.clone(), Known { sig, stamp, unread });
        }
    }

    /// The saved cache cannot be a hit, whatever the files say (another build of the cache, or none): files are not
    /// compared with it, so none is read to be compared.
    pub fn patch_certain(&mut self) {
        self.moot = true;
    }

    /// Picks up what an interrupted run had read (`<cache>/files.partial.json`) and keeps writing the same file while
    /// this run reads. Nothing is written when a run only reuses. Call `finish` when the run ends well.
    ///
    /// The journal is a cache file with no build id (it can never be a hit, whatever it holds) and is used like the
    /// saved cache's records: an entry applies only to a file whose stamp, length and last-write time are those it was
    /// taken under. Entries taken from a volume that is not trusted, or without a stamp, are never written.
    pub fn resume_from(&mut self, cache_dir: &Path) {
        let path = cache_dir.join(JOURNAL);
        if !self.reuse {
            let _ = std::fs::remove_file(&path); // a full-hash run neither uses nor leaves one
            return;
        }
        if let Some(saved) = super::FileStateCache::read_file(&path) {
            for (p, &sig) in &saved.files {
                let Some(&stamp) = saved.stamps.get(p) else { continue };
                self.resume.insert(p.clone(), Known { sig, stamp: Some(stamp), unread: saved.unread.contains(p) });
            }
            if !self.resume.is_empty() {
                let bytes: u64 = self.resume.values().filter(|k| !k.unread).map(|k| k.sig.0).sum();
                info!("Resuming: an earlier run that did not finish had read {} file(s) ({})", self.resume.len(), human(bytes));
            }
        }
        self.journal = Some(Journal { path, every: JOURNAL_EVERY, last: Instant::now() });
    }

    #[cfg(test)]
    pub(super) fn journal_every(&mut self, every: Duration) {
        if let Some(j) = &mut self.journal {
            j.every = every;
        }
    }

    /// The run ended well: the journal has nothing left to say.
    pub fn finish(&mut self) {
        if let Some(j) = self.journal.take() {
            let _ = std::fs::remove_file(&j.path);
        }
    }

    pub fn stats(&self) -> FingerprintStats {
        self.stats
    }

    /// Looks at `paths` without reading any of them and says what `track` is about to do: which are read, and why, which
    /// are recorded by stamp and which already are. Logs one line, and tells the progress counters what is to be read.
    pub fn announce(&mut self, what: &str, paths: &[PathBuf], need: Need) -> Plan {
        let mut plan = Plan::default();
        for path in paths {
            let Ok(by_path) = path.symlink_metadata() else { continue };
            if !by_path.is_file() || by_path.file_type().is_symlink() {
                continue;
            }
            let Ok(file) = File::open(path) else { continue };
            let Ok(meta) = file.metadata() else { continue };
            let len = meta.len();
            let Ok(mtime) = mtime_ns(&meta) else { continue };
            let before = (self.read)(&file);
            plan.files += 1;
            plan.bytes += len;
            match self.decide(path, need, len, mtime, before, now_ns()) {
                Action::Reuse(_) => {
                    plan.reused_files += 1;
                    plan.reused_bytes += len;
                }
                Action::Record => {
                    plan.recorded_files += 1;
                    plan.recorded_bytes += len;
                }
                Action::Read(why) => {
                    plan.read_files += 1;
                    plan.read_bytes += len;
                    plan.why(why);
                    self.announced.insert(path.clone());
                }
            }
        }
        if plan.files > 0 {
            let line = plan.describe(what);
            if plan.read_files + plan.recorded_files > 0 { info!("{line}") } else { debug!("{line}") }
        }
        self.progress.announce(plan.read_files, plan.read_bytes);
        plan
    }

    /// The fingerprint of `path` and, when it can be trusted to detect a later change, the stamp it was taken under.
    /// An error (missing, unreadable, a symlink) means there is no fingerprint: the caller poisons its snapshot.
    pub(super) fn fingerprint(&mut self, path: &Path, need: Need) -> std::io::Result<Fingerprint> {
        let started = Instant::now();
        let result = self.fingerprint_inner(path, need);
        match &result {
            Err(_) => {
                self.memo.remove(path); // no fingerprint now: nothing recorded earlier may stand in for one
                self.pinned.remove(path);
            }
            Ok(fp) => {
                // a fingerprint that is not the saved one: the saved cache cannot be a hit
                if !self.moot && !self.seed.is_empty() && self.seed.get(path).is_none_or(|k| k.sig != fp.sig) {
                    self.moot = true;
                }
            }
        }
        self.stats.ms += started.elapsed().as_secs_f64() * 1000.0;
        result
    }

    /// What to do about a file whose length, last-write time and stamp are known (the stamp `before` reading it).
    fn decide(&self, path: &Path, need: Need, len: u64, mtime: u128, before: Option<Stamp>, seen: i128) -> Action {
        if !self.reuse {
            return Action::Read(Why::FullHash);
        }
        let Some(now) = before else { return Action::Read(Why::NoStamp) };
        let trusted = self.gate.trusts(path, now.volume);
        // each place that holds a fingerprint is asked: one whose stamp is the file's now settles it, whichever it is (the
        // saved cache may know the file with no stamp, and the journal of an interrupted run with one)
        let recorded = [self.memo.get(path), self.seed.get(path), self.resume.get(path)];
        if trusted
            && let Some(k) = recorded.into_iter().flatten().find(|k| k.stamp == Some(now) && k.sig.0 == len && k.sig.1 == mtime)
        {
            return Action::Reuse(*k);
        }
        let known = recorded.into_iter().flatten().next().copied();
        if !trusted {
            return Action::Read(Why::UntrustedVolume);
        }
        let settled = now.changed.saturating_add(self.window.as_nanos() as i128) <= seen;
        if need == Need::Content {
            return Action::Read(if known.is_some() && settled { Why::Compare } else if settled { Why::Mod } else { Why::Recent });
        }
        if !settled {
            return Action::Read(Why::Recent);
        }
        if self.pinned.contains(path) {
            return Action::Read(Why::Pinned);
        }
        // The saved CRC (a real one, with no usable stamp or an old one) is compared with unless it cannot matter any
        // more; a saved record that is a stamp itself is compared by stamp, and a file with no record by nothing.
        let saved = self.seed.get(path);
        if self.moot || saved.is_none_or(|k| k.unread) {
            return Action::Record;
        }
        Action::Read(Why::Compare)
    }

    fn fingerprint_inner(&mut self, path: &Path, need: Need) -> std::io::Result<Fingerprint> {
        let by_path = path.symlink_metadata()?;
        if by_path.file_type().is_symlink() {
            return Err(std::io::Error::other("symlink"));
        }
        if !by_path.is_file() {
            // a directory (mod folders are walked): nothing to read
            self.memo.remove(path);
            let sig = (by_path.len(), mtime_ns(&by_path)?, CRC64.digest().finalize());
            return Ok(Fingerprint { sig, stamp: None, unread: false });
        }
        let mut file = File::open(path)?;
        // length and last-write time of the file actually opened
        let meta = file.metadata()?;
        if !meta.is_file() {
            return Err(std::io::Error::other("not a regular file"));
        }
        let (len, mtime) = (meta.len(), mtime_ns(&meta)?);
        let before = (self.read)(&file);
        let seen = now_ns();

        match self.decide(path, need, len, mtime, before, seen) {
            Action::Reuse(known) => {
                self.stats.reused_files += 1;
                self.stats.reused_bytes += len;
                self.memo.insert(path.to_owned(), known);
                if !known.unread && self.resume.get(path).is_some_and(|r| r.sig == known.sig) {
                    self.read_now.insert(path.to_owned(), known); // an earlier run's work, kept in the journal
                }
                return Ok(Fingerprint { sig: known.sig, stamp: known.stamp, unread: known.unread });
            }
            Action::Record => {
                // `decide` only says Record with a stamp in hand
                let stamp = before.expect("a stamp");
                let sig = (len, mtime, stamp_digest(&stamp));
                self.memo.insert(path.to_owned(), Known { sig, stamp: Some(stamp), unread: true });
                self.stats.recorded_files += 1;
                self.stats.recorded_bytes += len;
                return Ok(Fingerprint { sig, stamp: Some(stamp), unread: true });
            }
            Action::Read(_) => {}
        }

        if self.buffer.is_empty() {
            self.buffer.resize(BUFFER, 0);
        }
        if !self.announced.remove(path) {
            self.progress.announce(1, len);
        }
        self.progress.begin_file(&path.file_name().map_or_else(String::new, |n| n.to_string_lossy().into_owned()));
        let mut digest = CRC64.digest();
        let mut total = 0u64;
        let read_all = (|| -> std::io::Result<()> {
            loop {
                let n = file.read(&mut self.buffer)?;
                if n == 0 {
                    return Ok(());
                }
                digest.update(&self.buffer[..n]);
                total += n as u64;
                self.progress.add(n as u64);
            }
        })();
        self.progress.end_file();
        read_all?;
        self.stats.hashed_bytes += total;
        self.stats.hashed_files += 1;
        let sig = (len, mtime, digest.finalize());

        // The CRC describes one version of the file only if nothing changed while it was read, and the stamp can tell
        // a later change only if the change time was already old enough to differ from any later one.
        let after = (self.read)(&file);
        let after_meta = file.metadata().ok();
        let steady = total == len
            && before.is_some()
            && before == after
            && after_meta.is_some_and(|m| m.len() == len && mtime_ns(&m).ok() == Some(mtime));
        let settled = before.is_some_and(|b| b.changed.saturating_add(self.window.as_nanos() as i128) <= seen);
        match before {
            Some(stamp) if steady && settled => {
                let known = Known { sig, stamp: Some(stamp), unread: false };
                self.memo.insert(path.to_owned(), known);
                self.pinned.remove(path);
                if self.gate.trusts(path, stamp.volume) {
                    self.read_now.insert(path.to_owned(), known);
                    self.write_journal();
                }
                Ok(Fingerprint { sig, stamp: Some(stamp), unread: false })
            }
            _ => {
                self.memo.remove(path);
                self.pinned.insert(path.to_owned());
                Ok(Fingerprint { sig, stamp: None, unread: false })
            }
        }
    }

    /// Writes what this run has read so far, when the last write is older than `JOURNAL_EVERY`: a run that is killed
    /// loses at most that much work. The file is replaced whole (`atomic_write`), like the cache.
    fn write_journal(&mut self) {
        let Some(journal) = &mut self.journal else { return };
        if journal.last.elapsed() < journal.every {
            return;
        }
        journal.last = Instant::now();
        let mut partial = super::FileStateCache::error();
        for (path, k) in &self.read_now {
            if let Some(stamp) = k.stamp {
                partial.files.insert(path.clone(), k.sig);
                partial.stamps.insert(path.clone(), stamp);
            }
        }
        partial.write_file(&journal.path);
    }
}

impl Drop for Fingerprints {
    fn drop(&mut self) {
        let s = &self.stats;
        if s.hashed_files + s.reused_files + s.recorded_files == 0 {
            return;
        }
        info!("Fingerprints: {s}");
    }
}

/// KILN_EML_FULL_HASH set to anything but empty or `0`: read every file in full at every step, as EML 0.2.0 does.
pub(super) fn full_hash_requested(value: Option<&std::ffi::OsStr>) -> bool {
    value.is_some_and(|v| !v.is_empty() && v != "0")
}

/// What stands where the CRC would, for a file nobody read: a CRC-64 of its stamp. Two records of one file under one stamp
/// are equal and any other stamp gives another value. It cannot be told from a real CRC by its value: the `unread` list
/// of the cache file says which is which (and an older reader, which has no such list, sees a fingerprint that no
/// content has, so that a changed stamp reads as a changed file there too).
fn stamp_digest(stamp: &Stamp) -> u64 {
    let mut digest = CRC64.digest();
    digest.update(b"kiln-eml stamp");
    digest.update(&stamp.volume.to_le_bytes());
    digest.update(&stamp.file.to_le_bytes());
    digest.update(&stamp.changed.to_le_bytes());
    digest.finalize()
}

fn mtime_ns(meta: &std::fs::Metadata) -> std::io::Result<u128> {
    Ok(meta.modified()?.duration_since(UNIX_EPOCH).map_err(std::io::Error::other)?.as_nanos())
}

fn now_ns() -> i128 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(i128::MIN, |d| d.as_nanos() as i128)
}
