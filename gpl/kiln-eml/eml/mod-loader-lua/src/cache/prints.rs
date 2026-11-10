// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: hash each file once, not once per barrier. The change cache and the input barriers of `run` fingerprint
// the game's data files (55 containers, about 48 GiB, on a real install) up to four times per start; reading all of
// it each time made a patch run read ~190 GiB and a cache hit ~48 GiB (bisdat/kiln-mods#17, whose analysis of the
// problem and of OS change stamps this follows; the code is written here).
//
// A fingerprint is still (length, last-write time, CRC-64 of every byte). What changes is when the bytes are read:
// a file's recorded fingerprint is reused only while the OS says it is the same file and it has not changed since the
// bytes were hashed (`Stamp`: volume, file id, change time), and only if
//   - the stamp is a strong one (the platform reports all of it), and
//   - the file's volume passed a scratch-file test in this run, made in the game folder: a same-size rewrite with
//     the last-write time put back moves the change time there (`stamp::probe`; so size and last-write time are never
//     the evidence), and
//   - the file was not changed in the last RACY_WINDOW, so that a later change cannot share its change time with
//     the one recorded (a coarse clock; the same rule as git's racily-clean entries), and
//   - length and last-write time are unchanged as well, and the file was unchanged while it was being hashed.
// Anything else is hashed in full, exactly as before. KILN_EML_FULL_HASH=1 turns reuse off altogether.
//
// Two places hold recorded fingerprints: `memo` (this run: every barrier re-stats instead of re-hashing, and a file
// replaced or edited since is read again) and `seed` (the cache file of the last run, which is how a start that
// changes nothing reads nothing).

use std::{collections::HashMap, fs::File, io::Read, path::{Path, PathBuf}, time::{Duration, Instant, SystemTime, UNIX_EPOCH}};

use crate::log::{info, warn};

use super::stamp::{self, Reader, Stamp};

/// (length, last-write time in ns, CRC-64): the entry of `FileStateCache::files`.
pub(super) type Sig = (u64, u128, u64);

/// CRC-64/ECMA-182 as before, computed with sixteen-way lookup tables: the same values, several times faster than the
/// `crc` crate's default byte-at-a-time table (about 0.4 GiB/s, which alone made reading 48 GiB take two minutes).
pub(crate) static CRC64: crc::Crc<u64, crc::Table<16>> = crc::Crc::<u64, crc::Table<16>>::new(&crc::CRC_64_ECMA_182);

/// A change newer than this is not trusted as a stamp: the file system's clock may not have moved on since.
const RACY_WINDOW: Duration = Duration::from_secs(3);
const BUFFER: usize = 1 << 20;

#[cfg(test)]
thread_local! { pub(crate) static RACY_WINDOW_OVERRIDE: std::cell::Cell<Option<Duration>> = const { std::cell::Cell::new(None) }; }

fn racy_window() -> Duration {
    #[cfg(test)]
    if let Some(window) = RACY_WINDOW_OVERRIDE.with(|w| w.get()) {
        return window;
    }
    RACY_WINDOW
}

#[derive(Clone, Copy, Debug)]
struct Known {
    sig: Sig,
    stamp: Stamp,
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
    /// Time spent fingerprinting, in ms.
    pub ms: f64,
}

impl FingerprintStats {
    /// `hashed 0 B, reused 57 file(s)`: for a summary line that has the time already.
    pub fn brief(&self) -> String {
        format!("hashed {}, reused {} file(s)", human(self.hashed_bytes), self.reused_files)
    }
}

impl std::fmt::Display for FingerprintStats {
    /// `hashed 1.2 GiB in 12 file(s), reused 55 file(s) (48.0 GiB not read), 31 ms`
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f, "hashed {} in {} file(s), reused {} file(s) ({} not read), {:.0} ms",
            human(self.hashed_bytes), self.hashed_files, self.reused_files, human(self.reused_bytes), self.ms
        )
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

pub struct Fingerprints {
    seed: HashMap<PathBuf, Known>,
    memo: HashMap<PathBuf, Known>,
    gate: Gate,
    read: Reader,
    reuse: bool,
    window: Duration,
    buffer: Vec<u8>,
    stats: FingerprintStats,
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
            memo: HashMap::new(),
            gate: if reuse { Gate::test(game_dir, read) } else { Gate::off(game_dir) },
            read,
            reuse,
            window,
            buffer: Vec::new(),
            stats: FingerprintStats::default(),
        }
    }

    /// The fingerprints the last run saved (`files.json`), with the stamps they were taken under.
    pub fn seed(&mut self, saved: &super::FileStateCache) {
        for (path, stamp) in &saved.stamps {
            if let Some(&sig) = saved.files.get(path) {
                self.seed.insert(path.clone(), Known { sig, stamp: *stamp });
            }
        }
    }

    pub fn stats(&self) -> FingerprintStats {
        self.stats
    }

    /// The fingerprint of `path` and, when it can be trusted to detect a later change, the stamp it was taken under.
    /// An error (missing, unreadable, a symlink) means there is no fingerprint: the caller poisons its snapshot.
    pub(super) fn fingerprint(&mut self, path: &Path) -> std::io::Result<(Sig, Option<Stamp>)> {
        let started = Instant::now();
        let result = self.fingerprint_inner(path);
        if result.is_err() {
            self.memo.remove(path); // no fingerprint now: nothing recorded earlier may stand in for one
        }
        self.stats.ms += started.elapsed().as_secs_f64() * 1000.0;
        result
    }

    fn fingerprint_inner(&mut self, path: &Path) -> std::io::Result<(Sig, Option<Stamp>)> {
        let by_path = path.symlink_metadata()?;
        if by_path.file_type().is_symlink() {
            return Err(std::io::Error::other("symlink"));
        }
        if !by_path.is_file() {
            // a directory (mod folders are walked): nothing to read
            self.memo.remove(path);
            return Ok(((by_path.len(), mtime_ns(&by_path)?, CRC64.digest().finalize()), None));
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

        if let (true, Some(now)) = (self.reuse, before) {
            let known = self.memo.get(path).or_else(|| self.seed.get(path)).copied();
            if let Some(known) = known
                && known.stamp == now
                && known.sig.0 == len
                && known.sig.1 == mtime
                && self.gate.trusts(path, now.volume)
            {
                self.stats.reused_files += 1;
                self.stats.reused_bytes += len;
                self.memo.insert(path.to_owned(), known);
                return Ok((known.sig, Some(now)));
            }
        }

        if self.buffer.is_empty() {
            self.buffer.resize(BUFFER, 0);
        }
        let mut digest = CRC64.digest();
        let mut total = 0u64;
        loop {
            let n = file.read(&mut self.buffer)?;
            if n == 0 {
                break;
            }
            digest.update(&self.buffer[..n]);
            total += n as u64;
        }
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
                self.memo.insert(path.to_owned(), Known { sig, stamp });
                Ok((sig, Some(stamp)))
            }
            _ => {
                self.memo.remove(path);
                Ok((sig, None))
            }
        }
    }
}

impl Drop for Fingerprints {
    fn drop(&mut self) {
        let s = &self.stats;
        if s.hashed_files + s.reused_files == 0 {
            return;
        }
        info!("Fingerprints: {s}");
    }
}

/// KILN_EML_FULL_HASH set to anything but empty or `0`: read every file in full at every step, as EML 0.2.0 does.
pub(super) fn full_hash_requested(value: Option<&std::ffi::OsStr>) -> bool {
    value.is_some_and(|v| !v.is_empty() && v != "0")
}

/// `1.5 GiB`, `312 B`.
fn human(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KiB", "MiB", "GiB", "TiB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 { format!("{bytes} B") } else { format!("{value:.1} {}", UNITS[unit]) }
}

fn mtime_ns(meta: &std::fs::Metadata) -> std::io::Result<u128> {
    Ok(meta.modified()?.duration_since(UNIX_EPOCH).map_err(std::io::Error::other)?.as_nanos())
}

fn now_ns() -> i128 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(i128::MIN, |d| d.as_nanos() as i128)
}
