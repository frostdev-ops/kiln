// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: tests for the change cache and its fingerprint reuse (prints.rs, stamp.rs). Temp folders only; the
// volume test needs a file system that keeps a change time, which every Linux host file system and Wine's does.

use std::{fs::{self, File, OpenOptions}, io::Write, path::{Path, PathBuf}, time::Duration};

use super::{prints::full_hash_requested, stamp::{self, Stamp}, FileStateCache, Fingerprints};

const MIB: usize = 1 << 20;
/// Long enough that no test file is "recent", short enough to let the coarse clock move on between edits.
const SETTLE: Duration = Duration::from_millis(30);

struct Dir(PathBuf);

impl Dir {
    fn new() -> Self {
        let dir = std::env::temp_dir().join(format!("kiln-cache-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&dir).unwrap();
        Dir(dir)
    }
    fn file(&self, name: &str, bytes: &[u8]) -> PathBuf {
        let path = self.0.join(name);
        fs::write(&path, bytes).unwrap();
        path
    }
}

impl Drop for Dir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

/// A run that trusts a stamp as soon as it is taken (the "recent change" rule has its own test).
fn prints(dir: &Dir) -> Fingerprints {
    Fingerprints::with(&dir.0, stamp::read, true, Duration::ZERO)
}

fn tracked(prints: &mut Fingerprints, paths: &[&Path]) -> FileStateCache {
    let mut cache = FileStateCache::default();
    for path in paths {
        cache.track(*path, prints);
    }
    cache
}

/// Same length and last-write time, other bytes: the edit that size and mtime cannot see.
fn edit_in_place(path: &Path) {
    std::thread::sleep(SETTLE);
    let modified = fs::metadata(path).unwrap().modified().unwrap();
    let mut bytes = fs::read(path).unwrap();
    bytes[0] ^= 0x55;
    let mut f = OpenOptions::new().write(true).open(path).unwrap();
    f.write_all(&bytes).unwrap();
    f.set_modified(modified).unwrap();
    assert_eq!(fs::metadata(path).unwrap().modified().unwrap(), modified, "the last-write time was put back");
}

fn saved_and_reloaded(dir: &Dir, cache: &FileStateCache) -> FileStateCache {
    let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("saved");
    cache.write(&folder);
    FileStateCache::read(&folder)
}

#[test]
fn preserved_timestamp_edit_and_unreadable_file_invalidate_cache() {
    let dir = Dir::new();
    let p = dir.file("data.bin", b"before");
    let time = p.metadata().unwrap().modified().unwrap();
    let mut a = FileStateCache::default();
    a.track(&p, &mut prints(&dir));
    fs::write(&p, "after!").unwrap();
    File::options().write(true).open(&p).unwrap().set_modified(time).unwrap();
    let mut b = FileStateCache::default();
    b.track(&p, &mut prints(&dir));
    assert!(a.diff(&b).is_some());
    fs::remove_file(&p).unwrap();
    let mut c = FileStateCache::default();
    c.track(&p, &mut prints(&dir));
    c.configuration("options");
    assert!(c.diff(&c).is_some());
}

#[test]
fn the_fast_crc_computes_the_same_values() {
    use super::prints::CRC64;
    let reference = crc::Crc::<u64>::new(&crc::CRC_64_ECMA_182);
    assert_eq!(CRC64.checksum(b"123456789"), 0x6c40_df5f_0b49_7347, "CRC-64/ECMA-182 check value");
    let bytes: Vec<u8> = (0..70_000u32).map(|i| (i.wrapping_mul(2_654_435_761) >> 13) as u8).collect();
    for len in [0, 1, 7, 15, 16, 17, 31, 255, 4096, 65_537, bytes.len()] {
        assert_eq!(CRC64.checksum(&bytes[..len]), reference.checksum(&bytes[..len]), "{len} bytes");
    }
    // every start alignment and odd length of the sixteen-byte blocks
    for start in 0..17 {
        for len in (0..80).chain([1021, 4093, 65_535]) {
            let part = &bytes[start..start + len];
            assert_eq!(CRC64.checksum(part), reference.checksum(part), "{len} bytes at offset {start}");
        }
    }
    // split updates, as the file loop does
    let mut digest = CRC64.digest();
    for chunk in bytes.chunks(4093) {
        digest.update(chunk);
    }
    assert_eq!(digest.finalize(), reference.checksum(&bytes));
}

#[cfg(any(unix, windows))]
mod reuse {
    use super::*;

    #[test]
    fn the_volume_test_passes_where_the_change_time_moves() {
        let dir = Dir::new();
        // a scratch file a crashed run left behind is removed; one of a test in progress (young) is not
        let stale = dir.file(".kiln-eml-stamp-probe-1-stale", b"kiln-eml");
        File::options().write(true).open(&stale).unwrap()
            .set_modified(std::time::SystemTime::now() - Duration::from_secs(600)).unwrap();
        let young = dir.file(".kiln-eml-stamp-probe-2-young", b"kiln-eml");
        stamp::probe(&dir.0, stamp::read).expect("this file system keeps a change time");
        assert!(!stale.exists() && young.exists());
        fs::remove_file(&young).unwrap();
        stamp::probe(&dir.0, stamp::read).unwrap();
        assert_eq!(fs::read_dir(&dir.0).unwrap().count(), 0, "the scratch file is removed");
    }

    /// `KILN_EML_PROBE_DIR=<folder> cargo test -p mod-loader-lua probe_a_real_folder -- --ignored --nocapture`: the
    /// volume test's verdict on a real file system (an exFAT or FAT mount must be refused).
    #[test]
    #[ignore]
    fn probe_a_real_folder() {
        let dir = PathBuf::from(std::env::var("KILN_EML_PROBE_DIR").expect("KILN_EML_PROBE_DIR"));
        println!("PROBE {}: {:?}", dir.display(), stamp::probe(&dir, stamp::read));
    }

    #[test]
    fn a_cache_hit_hashes_no_bytes_of_unchanged_files() {
        let dir = Dir::new();
        let big = dir.file("g_000.dat", &vec![7u8; 4 * MIB]);
        let small = dir.file("g.kfc", b"header");

        let mut cold = prints(&dir);
        let first = tracked(&mut cold, &[&big, &small]);
        assert_eq!((cold.stats().hashed_bytes, cold.stats().reused_files), ((4 * MIB + 6) as u64, 0));
        assert_eq!(first.stamps.len(), 2, "both files are stamped");
        let saved = saved_and_reloaded(&dir, &first);

        let mut warm = prints(&dir);
        warm.seed(&saved);
        let second = tracked(&mut warm, &[&big, &small]);
        let stats = warm.stats();
        assert_eq!((stats.hashed_bytes, stats.hashed_files, stats.reused_files), (0, 0, 2), "{stats}");
        assert_eq!(stats.reused_bytes, (4 * MIB + 6) as u64);
        assert!(second.diff(&saved).is_none(), "the same fingerprints as when they were read");
        assert!(second.diff(&first).is_none());
        assert!(!second.stamps_differ(&saved));
    }

    #[test]
    fn a_run_hashes_each_file_once_across_all_its_steps() {
        let dir = Dir::new();
        let a = dir.file("g_000.dat", &vec![1u8; 2 * MIB]);
        let b = dir.file("g_001.dat", &vec![2u8; 2 * MIB]);
        let mut run = prints(&dir);
        // what `run` does: cache check, barrier before Lua, barrier before publication, snapshot to save
        let steps: Vec<_> = (0..4).map(|_| tracked(&mut run, &[&a, &b])).collect();
        let stats = run.stats();
        assert_eq!((stats.hashed_bytes, stats.hashed_files, stats.reused_files), ((4 * MIB) as u64, 2, 6), "{stats}");
        assert!(steps.iter().all(|s| s.diff(&steps[0]).is_none()));
    }

    #[test]
    fn a_same_size_edit_with_the_last_write_time_restored_is_read_again() {
        // across runs: the stored CRC must not be reused
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![9u8; MIB]);
        let first = tracked(&mut prints(&dir), &[&f]);
        let saved = saved_and_reloaded(&dir, &first);
        edit_in_place(&f);
        let mut run = prints(&dir);
        run.seed(&saved);
        let after = tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_bytes, run.stats().reused_files), (MIB as u64, 0));
        assert!(after.diff(&saved).is_some(), "the CRC changed");
        assert_eq!(first.files[&f].0, after.files[&f].0, "same length");
        assert_eq!(first.files[&f].1, after.files[&f].1, "same last-write time");

        // within a run: the barrier before publication must see it too
        let mut run = prints(&dir);
        let before = tracked(&mut run, &[&f]);
        edit_in_place(&f);
        let now = tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
        assert!(before.diff(&now).is_some());
    }

    #[test]
    fn the_barrier_before_publication_rereads_only_the_input_that_changed() {
        let dir = Dir::new();
        for i in 0..3 {
            dir.file(&format!("g_{i:03}.dat"), &vec![i as u8; MIB]);
        }
        dir.file("g.kfc.bak", b"vanilla header");
        let mut run = prints(&dir);
        let mut before = FileStateCache::default();
        before.track_game_inputs(&dir.0, "g", 3, &mut run);
        assert_eq!(run.stats().hashed_files, 4);
        edit_in_place(&dir.0.join("g_001.dat"));
        let mut now = FileStateCache::default();
        now.track_game_inputs(&dir.0, "g", 3, &mut run);
        assert!(before.diff(&now).is_some(), "the edited container is a changed input");
        let stats = run.stats();
        assert_eq!((stats.hashed_files, stats.hashed_bytes, stats.reused_files), (5, 4 * MIB as u64 + 14, 3), "{stats}");
    }

    #[test]
    fn a_file_replaced_between_steps_is_read_again() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![3u8; MIB]);
        let mut run = prints(&dir);
        let before = tracked(&mut run, &[&f]);
        // another file with the same length and last-write time takes its place
        std::thread::sleep(SETTLE);
        let modified = fs::metadata(&f).unwrap().modified().unwrap();
        let other = dir.file("replacement", &vec![4u8; MIB]);
        File::options().write(true).open(&other).unwrap().set_modified(modified).unwrap();
        fs::rename(&other, &f).unwrap();
        let now = tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
        assert!(before.diff(&now).is_some());
    }

    #[test]
    fn a_vanished_or_unreadable_file_is_never_reused_and_poisons_the_snapshot() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![5u8; MIB]);
        let mut run = prints(&dir);
        tracked(&mut run, &[&f]);
        fs::remove_file(&f).unwrap();
        let mut gone = tracked(&mut run, &[&f]);
        gone.configuration("options");
        assert!(gone.diff(&gone).is_some(), "a missing input has no fingerprint, so no cache hit");
        assert!(gone.files.is_empty() && gone.stamps.is_empty());

        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let f = dir.file("g_001.dat", &vec![6u8; MIB]);
            tracked(&mut run, &[&f]);
            fs::set_permissions(&f, fs::Permissions::from_mode(0)).unwrap();
            if File::open(&f).is_err() {
                let mut locked = tracked(&mut run, &[&f]);
                locked.configuration("options");
                assert!(locked.diff(&locked).is_some(), "an unreadable input poisons the snapshot");
            }
            fs::set_permissions(&f, fs::Permissions::from_mode(0o600)).unwrap();
            let link = dir.0.join("link.dat");
            std::os::unix::fs::symlink(&f, &link).unwrap();
            let mut linked = tracked(&mut run, &[&link]);
            linked.configuration("options");
            assert!(linked.diff(&linked).is_some(), "a symlink is refused as before");
        }
    }

    #[test]
    fn a_recent_change_is_not_stamped() {
        // the file system's clock may not have moved on since a change this recent: a later change could share its
        // change time, so the file is read at every step until it has settled
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![8u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, stamp::read, true, Duration::from_secs(3600));
        let cache = tracked(&mut run, &[&f]);
        tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
        assert!(cache.stamps.is_empty(), "nothing to save: a start soon after would reread the file");
    }

    #[test]
    fn a_volume_that_keeps_no_change_time_is_never_trusted() {
        fn frozen(f: &File) -> Option<Stamp> {
            stamp::read(f).map(|s| Stamp { changed: 1_000_000_000, ..s })
        }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        assert!(stamp::probe(&dir.0, frozen).is_err(), "the scratch-file test sees a change time that never moves");
        let mut run = Fingerprints::with(&dir.0, frozen, true, Duration::ZERO);
        tracked(&mut run, &[&f]);
        edit_in_place(&f);
        tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0), "every file is read in full");
    }

    #[test]
    fn a_volume_the_os_does_not_name_is_trusted_only_in_the_tested_folder() {
        // Wine reports serial number 0 for some mounts; a test on one of them says nothing about another
        fn unnamed(f: &File) -> Option<Stamp> {
            stamp::read(f).map(|s| Stamp { volume: 0, ..s })
        }
        let (game, elsewhere) = (Dir::new(), Dir::new());
        let (here, there) = (game.file("g_000.dat", &vec![1u8; MIB]), elsewhere.file("big.bin", &vec![2u8; MIB]));
        let mut run = Fingerprints::with(&game.0, unnamed, true, Duration::ZERO);
        for _ in 0..2 {
            tracked(&mut run, &[&here, &there]);
        }
        let stats = run.stats();
        assert_eq!((stats.hashed_files, stats.reused_files), (3, 1), "only the game folder's file is reused: {stats}");
        assert_eq!(fs::read_dir(&elsewhere.0).unwrap().count(), 1, "nothing is ever written outside the game folder");

        // a volume the OS does name: files anywhere on it are trusted once the game folder's test passed
        let mut run = Fingerprints::with(&game.0, stamp::read, true, Duration::ZERO);
        for _ in 0..2 {
            tracked(&mut run, &[&here, &there]);
        }
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 2), "same volume as the game folder");
    }

    #[test]
    fn a_file_on_another_named_volume_than_the_test_is_not_trusted() {
        // the scratch file is 8 bytes: report it on volume 7 and everything else on volume 9
        fn two_volumes(f: &File) -> Option<Stamp> {
            let volume = if f.metadata().ok()?.len() == 8 { 7 } else { 9 };
            stamp::read(f).map(|s| Stamp { volume, ..s })
        }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, two_volumes, true, Duration::ZERO);
        for _ in 0..2 {
            tracked(&mut run, &[&f]);
        }
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
    }

    #[test]
    fn a_stamp_that_cannot_be_read_means_full_hashing() {
        fn none(_: &File) -> Option<Stamp> { None }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, none, true, Duration::ZERO);
        let cache = tracked(&mut run, &[&f]);
        tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
        assert!(cache.stamps.is_empty());
    }

    #[test]
    fn a_file_that_changes_while_it_is_hashed_is_not_stamped() {
        thread_local! { static TICKS: std::cell::Cell<i128> = const { std::cell::Cell::new(0) }; }
        fn drifting(f: &File) -> Option<Stamp> {
            stamp::read(f).map(|s| Stamp { changed: TICKS.with(|t| { t.set(t.get() + 1); t.get() }), ..s })
        }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, drifting, true, Duration::ZERO);
        let cache = tracked(&mut run, &[&f]);
        assert!(cache.stamps.is_empty(), "the stamp before and after reading differ");
    }

    #[test]
    fn full_hash_mode_reads_everything_at_every_step() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, stamp::read, false, Duration::ZERO);
        for _ in 0..4 {
            tracked(&mut run, &[&f]);
        }
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (4, 0));
        assert!(full_hash_requested(Some("1".as_ref())) && full_hash_requested(Some("yes".as_ref())));
        assert!(!full_hash_requested(None) && !full_hash_requested(Some("".as_ref())) && !full_hash_requested(Some("0".as_ref())));
    }

    #[test]
    fn a_cache_file_from_before_stamps_is_read_once_then_fast() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![2u8; MIB]);
        // what the dev.20 build saved: fingerprints only
        let current = tracked(&mut prints(&dir), &[&f]);
        let mut old: serde_json::Value = serde_json::to_value(&current).unwrap();
        old.as_object_mut().unwrap().remove("stamps").expect("stamps are saved");
        let old_text = serde_json::to_string(&old).unwrap();
        assert!(!old_text.contains("stamps"));
        let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("old");
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join("files.json"), &old_text).unwrap();
        let saved = FileStateCache::read(&folder);
        assert!(saved.stamps.is_empty() && !saved.files.is_empty(), "it still loads");

        // first start: the file is read once; the cache is unchanged by it, only its stamps are new
        let mut first = prints(&dir);
        first.seed(&saved);
        let hit = tracked(&mut first, &[&f]);
        assert_eq!(first.stats().hashed_bytes, MIB as u64);
        assert!(hit.diff(&saved).is_none() && hit.stamps_differ(&saved));
        hit.write(&folder);

        // from then on: nothing is read
        let mut second = prints(&dir);
        second.seed(&FileStateCache::read(&folder));
        let again = tracked(&mut second, &[&f]);
        assert_eq!((second.stats().hashed_bytes, second.stats().reused_files), (0, 1));
        assert!(again.diff(&saved).is_none());
    }

    #[test]
    fn a_stamped_cache_survives_the_json_round_trip_and_stays_readable_by_older_code() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![2u8; 1024]);
        let cache = tracked(&mut prints(&dir), &[&f]);
        let text = serde_json::to_string(&cache).unwrap();
        let back: FileStateCache = serde_json::from_str(&text).unwrap();
        assert_eq!(back.stamps, cache.stamps);
        assert!(back.diff(&cache).is_none());
        // the dev.20 reader: a struct of build_id + files only ignores the extra field
        #[derive(serde::Deserialize)]
        struct Dev20 { build_id: String, files: std::collections::HashMap<PathBuf, (u64, u128, u64)> }
        let old: Dev20 = serde_json::from_str(&text).unwrap();
        assert_eq!(old.files.len(), 1);
        assert!(!old.build_id.is_empty());
    }

    #[test]
    fn the_cache_file_is_replaced_whole() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", b"x");
        let cache = tracked(&mut prints(&dir), &[&f]);
        let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("saved");
        cache.write(&folder);
        cache.write(&folder);
        let names: Vec<_> = fs::read_dir(&folder).unwrap().map(|e| e.unwrap().file_name()).collect();
        assert_eq!(names, ["files.json"], "no temp file is left behind");
    }
}

/// `cargo test --release -p mod-loader-lua bench_ -- --ignored --nocapture --test-threads=1`
/// with KILN_EML_BENCH_DIR (a folder with room for sparse files), KILN_EML_BENCH_FILES (default 55) and
/// KILN_EML_BENCH_MIB (default 1024): the fingerprint steps of a start on a synthetic install, as EML 0.2.0 ran them
/// (every file read at every step) and now. Prints bytes read (the process' read counter) and wall time.
#[cfg(all(unix, test))]
#[test]
#[ignore]
fn bench_game_data_steps() {
    use std::time::Instant;
    let base = PathBuf::from(std::env::var("KILN_EML_BENCH_DIR").expect("KILN_EML_BENCH_DIR"));
    let count: usize = std::env::var("KILN_EML_BENCH_FILES").ok().and_then(|v| v.parse().ok()).unwrap_or(55);
    let mib: u64 = std::env::var("KILN_EML_BENCH_MIB").ok().and_then(|v| v.parse().ok()).unwrap_or(1024);
    let game = base.join(format!("bench-{}", uuid::Uuid::new_v4()));
    fs::create_dir_all(&game).unwrap();
    struct Cleanup(PathBuf);
    impl Drop for Cleanup { fn drop(&mut self) { let _ = fs::remove_dir_all(&self.0); } }
    let _cleanup = Cleanup(game.clone());
    for i in 0..count {
        let f = File::create(game.join(format!("enshrouded_{i:03}.dat"))).unwrap();
        f.set_len(mib << 20).unwrap(); // sparse
    }
    fs::write(game.join("enshrouded.kfc.bak"), b"vanilla header").unwrap();
    fs::write(game.join("enshrouded.kfc"), b"patched header").unwrap();
    // let the files settle past the recent-change rule, as a real install's files are
    let window = Duration::from_millis(50);
    std::thread::sleep(Duration::from_millis(100));
    let cache_dir = camino::Utf8Path::from_path(&game).unwrap().join("kiln-cache");
    fs::create_dir_all(&cache_dir).unwrap();

    fn rchar() -> u64 {
        fs::read_to_string("/proc/self/io").unwrap().lines()
            .find_map(|l| l.strip_prefix("rchar: ")).unwrap().trim().parse().unwrap()
    }
    // the four fingerprint steps of `run` that touch the game's files
    fn start(game: &Path, count: usize, prints: &mut Fingerprints, patch: bool) -> FileStateCache {
        let mut saved = FileStateCache::default();
        saved.track_game_files(game, prints); // cache check
        if !patch {
            return saved;
        }
        let mut before = FileStateCache::default();
        before.track_game_inputs(game, "enshrouded", count, prints); // before Lua
        let mut now = FileStateCache::default();
        now.track_game_inputs(game, "enshrouded", count, prints); // before publication
        assert!(before.diff(&now).is_none());
        let mut after = FileStateCache::default();
        after.track_game_files(game, prints); // the snapshot saved
        after
    }
    let report = |label: &str, prints: &Fingerprints, read: u64, wall: Duration| {
        println!("BENCH {label:<34} read {:>9.2} GiB  wall {:>7.1} s  [{}]", read as f64 / (1u64 << 30) as f64, wall.as_secs_f64(), prints.stats());
    };
    println!("BENCH {count} sparse files of {mib} MiB = {:.1} GiB, in {}", (count as u64 * mib) as f64 / 1024.0, game.display());

    for (mode, reuse) in [("EML 0.2.0 (full hash every step)", false), ("kiln-eml dev.21 (stamps)", true)] {
        let new = |dir: &Path| Fingerprints::with(dir, stamp::read, reuse, window);
        let _ = fs::remove_file(cache_dir.join("files.json"));

        let (r0, t0) = (rchar(), Instant::now());
        let mut run = new(&game);
        let snapshot = start(&game, count, &mut run, true);
        snapshot.write(&cache_dir);
        report(&format!("{mode}: cold patch run"), &run, rchar() - r0, t0.elapsed());
        drop(run);

        let (r0, t0) = (rchar(), Instant::now());
        let mut run = new(&game);
        let saved = FileStateCache::read(&cache_dir);
        run.seed(&saved);
        let hit = start(&game, count, &mut run, false);
        assert!(hit.diff(&saved).is_none());
        if hit.stamps_differ(&saved) {
            hit.write(&cache_dir);
        }
        report(&format!("{mode}: warm cache hit"), &run, rchar() - r0, t0.elapsed());
        drop(run);

        let (r0, t0) = (rchar(), Instant::now());
        let mut run = new(&game);
        run.seed(&FileStateCache::read(&cache_dir));
        let snapshot = start(&game, count, &mut run, true);
        snapshot.write(&cache_dir);
        report(&format!("{mode}: patch run, warm cache"), &run, rchar() - r0, t0.elapsed());
        drop(run);
    }
}
