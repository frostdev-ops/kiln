// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: tests for the change cache and its fingerprint reuse (prints.rs, stamp.rs). Temp folders only; the
// volume test needs a file system that keeps a change time, which every Linux host file system and Wine's does.

use std::{fs::{self, File, OpenOptions}, io::Write, path::{Path, PathBuf}, time::Duration};

use super::{prints::full_hash_requested, stamp::{self, Stamp}, FileStateCache, Fingerprints, Need};

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

/// The game's own files: fingerprinted by what the OS says of them, not by their bytes (`Need::Identity`).
fn tracked_game(prints: &mut Fingerprints, paths: &[&Path]) -> FileStateCache {
    let mut cache = FileStateCache::default();
    for path in paths {
        cache.track_as(*path, prints, Need::Identity);
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
    fn the_barrier_before_publication_sees_an_input_that_changed_without_reading_anything() {
        let dir = Dir::new();
        for i in 0..3 {
            dir.file(&format!("g_{i:03}.dat"), &vec![i as u8; MIB]);
        }
        dir.file("g.kfc.bak", b"vanilla header");
        let mut run = prints(&dir);
        let mut before = FileStateCache::default();
        before.track_game_inputs(&dir.0, "g", 3, &mut run);
        // the game's own files are recorded by their stamp: nothing is read
        assert_eq!((run.stats().hashed_files, run.stats().recorded_files), (0, 4), "{}", run.stats());
        edit_in_place(&dir.0.join("g_001.dat"));
        let mut now = FileStateCache::default();
        now.track_game_inputs(&dir.0, "g", 3, &mut run);
        assert!(before.diff(&now).is_some(), "the edited container is a changed input");
        let stats = run.stats();
        assert_eq!((stats.hashed_files, stats.hashed_bytes, stats.reused_files, stats.recorded_files), (0, 0, 3, 5), "{stats}");
    }

    /// The same, for the files a run keeps reading by content (a mod's, `Need::Content`).
    #[test]
    fn the_barrier_before_publication_rereads_only_the_content_file_that_changed() {
        let dir = Dir::new();
        let files: Vec<_> = (0..3).map(|i| dir.file(&format!("m_{i}.lua"), &vec![i as u8; MIB])).collect();
        let refs: Vec<&Path> = files.iter().map(|p| p.as_path()).collect();
        let mut run = prints(&dir);
        let before = tracked(&mut run, &refs);
        assert_eq!(run.stats().hashed_files, 3);
        edit_in_place(&files[1]);
        let now = tracked(&mut run, &refs);
        assert!(before.diff(&now).is_some());
        let stats = run.stats();
        assert_eq!((stats.hashed_files, stats.hashed_bytes, stats.reused_files), (4, 4 * MIB as u64, 2), "{stats}");
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

    // ---- stamp-only records (Need::Identity) ----

    #[test]
    fn a_first_run_records_an_old_file_by_its_stamp_without_reading_it() {
        let dir = Dir::new();
        let big = dir.file("g_000.dat", &vec![7u8; 4 * MIB]);
        let mut cold = prints(&dir);
        let first = tracked_game(&mut cold, &[&big]);
        let s = cold.stats();
        assert_eq!((s.hashed_bytes, s.hashed_files, s.recorded_files, s.recorded_bytes), (0, 0, 1, 4 * MIB as u64), "{s}");
        assert!(first.unread.contains(&big) && first.stamps.contains_key(&big), "marked as unread, with its stamp");
        let content = tracked(&mut prints(&dir), &[&big]);
        assert_ne!(first.files[&big].2, content.files[&big].2, "the third number is not the CRC of the bytes");
        assert_eq!(first.files[&big].0, content.files[&big].0, "same length");

        // a later start finds the same stamp: nothing is read, the same fingerprints come out
        let saved = saved_and_reloaded(&dir, &first);
        assert!(saved.unread.contains(&big), "the mark survives the file");
        let mut warm = prints(&dir);
        warm.seed(&saved);
        let second = tracked_game(&mut warm, &[&big]);
        let s = warm.stats();
        assert_eq!((s.hashed_bytes, s.hashed_files, s.reused_files, s.recorded_files), (0, 0, 1, 0), "{s}");
        assert!(second.diff(&saved).is_none() && !second.stamps_differ(&saved), "an unchanged file is an unchanged file");
    }

    #[test]
    fn a_recorded_file_that_changed_is_changed_and_is_not_read_to_find_out() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![9u8; MIB]);
        let saved = saved_and_reloaded(&dir, &tracked_game(&mut prints(&dir), &[&f]));
        // same length, same last-write time, other bytes: only the change time knows
        edit_in_place(&f);
        let mut run = prints(&dir);
        run.seed(&saved);
        let after = tracked_game(&mut run, &[&f]);
        let s = run.stats();
        assert_eq!((s.hashed_bytes, s.recorded_files, s.reused_files), (0, 1, 0), "{s}");
        assert!(after.diff(&saved).is_some(), "a new stamp is a changed file: the patch runs again");
        assert_eq!(after.files[&f].0, saved.files[&f].0);
        assert_eq!(after.files[&f].1, saved.files[&f].1);
    }

    #[test]
    fn a_file_changed_in_the_last_seconds_is_read_not_recorded() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![8u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, stamp::read, true, Duration::from_secs(3600));
        let cache = tracked_game(&mut run, &[&f]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.hashed_bytes, s.recorded_files), (1, MIB as u64, 0), "{s}");
        assert!(cache.unread.is_empty() && cache.stamps.is_empty(), "a recent change has no stamp to stand on");
    }

    #[test]
    fn a_recent_file_is_read_at_every_step_of_the_run_so_that_its_fingerprints_stay_comparable() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![8u8; MIB]);
        // the file is recent at the first step and old at the second
        let mut run = Fingerprints::with(&dir.0, stamp::read, true, Duration::from_millis(150));
        let first = tracked_game(&mut run, &[&f]);
        std::thread::sleep(Duration::from_millis(200));
        let second = tracked_game(&mut run, &[&f]);
        let third = tracked_game(&mut run, &[&f]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.recorded_files, s.reused_files), (2, 0, 1), "read twice, then reused: {s}");
        assert!(first.diff(&second).is_none() && second.diff(&third).is_none(), "a CRC at one step, a stamp digest at the next would read as a change");
        assert!(third.unread.is_empty() && !third.stamps.is_empty());
    }

    #[test]
    fn a_volume_that_fails_the_stamp_test_reads_every_game_file_in_full() {
        fn frozen(f: &File) -> Option<Stamp> {
            stamp::read(f).map(|s| Stamp { changed: 1_000_000_000, ..s })
        }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, frozen, true, Duration::ZERO);
        run.patch_certain(); // even when no comparison is left to make
        let cache = tracked_game(&mut run, &[&f]);
        tracked_game(&mut run, &[&f]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.recorded_files, s.reused_files), (2, 0, 0), "{s}");
        assert!(cache.unread.is_empty());
        assert!(run.announce("t", &[f.clone()], Need::Identity).read_files == 1, "and the plan says so");
    }

    #[test]
    fn the_full_hash_override_reads_game_files_in_full() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, stamp::read, false, Duration::ZERO);
        run.patch_certain();
        for _ in 0..3 {
            tracked_game(&mut run, &[&f]);
        }
        let s = run.stats();
        assert_eq!((s.hashed_files, s.recorded_files, s.reused_files), (3, 0, 0), "{s}");
    }

    #[test]
    fn a_stamp_that_cannot_be_read_is_never_recorded() {
        fn none(_: &File) -> Option<Stamp> { None }
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![1u8; MIB]);
        let mut run = Fingerprints::with(&dir.0, none, true, Duration::ZERO);
        run.patch_certain();
        tracked_game(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().recorded_files), (1, 0));
    }

    #[test]
    fn a_file_changed_between_the_record_and_publication_is_caught_by_its_stamp() {
        // what `run` does: a barrier before Lua records, an edit that restores the last-write time, a barrier before publication
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![5u8; MIB]);
        let mut run = prints(&dir);
        let before = tracked_game(&mut run, &[&f]);
        edit_in_place(&f);
        let now = tracked_game(&mut run, &[&f]);
        assert!(before.diff(&now).is_some());
        assert_eq!(run.stats().hashed_bytes, 0, "caught without reading it");
        // and the other way: a file replaced by another with the same length and last-write time
        let g = dir.file("g_001.dat", &vec![3u8; MIB]);
        let before = tracked_game(&mut run, &[&g]);
        std::thread::sleep(SETTLE);
        let modified = fs::metadata(&g).unwrap().modified().unwrap();
        let other = dir.file("replacement", &vec![4u8; MIB]);
        File::options().write(true).open(&other).unwrap().set_modified(modified).unwrap();
        fs::rename(&other, &g).unwrap();
        let now = tracked_game(&mut run, &[&g]);
        assert!(before.diff(&now).is_some(), "a new file id is a changed file");
    }

    #[test]
    fn an_old_cache_file_is_trusted_by_stamp_and_a_cache_without_stamps_is_not_read_when_a_patch_is_certain() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![2u8; MIB]);
        let g = dir.file("g_001.dat", &vec![3u8; MIB]);
        // dev.21's cache: CRCs and stamps
        let dev21 = saved_and_reloaded(&dir, &tracked(&mut prints(&dir), &[&f, &g]));
        assert!(dev21.unread.is_empty() && dev21.stamps.len() == 2);
        let mut run = prints(&dir);
        run.seed(&dev21);
        let again = tracked_game(&mut run, &[&f, &g]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.recorded_files, s.reused_files), (0, 0, 2), "{s}");
        assert!(again.diff(&dev21).is_none() && !again.stamps_differ(&dev21), "the recorded CRCs are reused as they were");

        // dev.20's: CRCs only. With the build changed (a patch is certain) nothing is read to compare them
        let mut v: serde_json::Value = serde_json::to_value(&dev21).unwrap();
        v.as_object_mut().unwrap().remove("stamps").unwrap();
        let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("dev20");
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join("files.json"), serde_json::to_string(&v).unwrap()).unwrap();
        let dev20 = FileStateCache::read(&folder);
        assert!(dev20.stamps.is_empty() && dev20.files.len() == 2);
        let mut run = prints(&dir);
        run.seed(&dev20);
        run.patch_certain();
        let cache = tracked_game(&mut run, &[&f, &g]);
        assert_eq!((run.stats().hashed_files, run.stats().recorded_files), (0, 2));
        assert_eq!(cache.unread.len(), 2);
        // with the build unchanged the CRCs decide: each file is read once, found the same, and the stamps are learned
        let mut run = prints(&dir);
        run.seed(&dev20);
        let cache = tracked_game(&mut run, &[&f, &g]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.recorded_files), (2, 0), "{s}");
        assert!(cache.diff(&dev20).is_none() && cache.stamps_differ(&dev20) && cache.unread.is_empty());
    }

    #[test]
    fn once_one_file_differs_from_the_saved_cache_the_others_are_not_read_to_be_compared() {
        let dir = Dir::new();
        let files: Vec<_> = ["a", "b", "c"].iter().map(|n| dir.file(&format!("g_{n}.dat"), &vec![1u8; MIB])).collect();
        let refs: Vec<&Path> = files.iter().map(|p| p.as_path()).collect();
        let saved = saved_and_reloaded(&dir, &tracked(&mut prints(&dir), &refs));
        edit_in_place(&files[0]);
        edit_in_place(&files[1]);
        let mut run = prints(&dir);
        run.seed(&saved);
        let cache = tracked_game(&mut run, &refs);
        let s = run.stats();
        // a is read to compare (the cache could still hit if its bytes were the same), and differs; b is then only
        // recorded; c is reused
        assert_eq!((s.hashed_files, s.recorded_files, s.reused_files), (1, 1, 1), "{s}");
        assert!(cache.diff(&saved).is_some());
        assert_eq!(cache.unread.iter().collect::<Vec<_>>(), [&files[1]]);
    }

    #[test]
    fn a_cache_file_with_the_unread_list_stays_readable_by_older_code() {
        let dir = Dir::new();
        let f = dir.file("g_000.dat", &vec![2u8; MIB]);
        let cache = tracked_game(&mut prints(&dir), &[&f]);
        let text = serde_json::to_string(&cache).unwrap();
        assert!(text.contains("\"unread\""));
        // the dev.21 reader: build_id, files and stamps, and nothing else
        #[derive(serde::Deserialize)]
        struct Dev21 { build_id: String, files: std::collections::HashMap<PathBuf, (u64, u128, u64)>, stamps: std::collections::HashMap<PathBuf, Stamp> }
        let old: Dev21 = serde_json::from_str(&text).unwrap();
        assert_eq!((old.files.len(), old.stamps.len(), old.build_id.is_empty()), (1, 1, false));

        // what that reader does with it: entries with no mark. Unchanged, the stamp stands for the entry; changed, it
        // reads the file, gets a CRC the entry does not have, and the file counts as changed.
        let mut v: serde_json::Value = serde_json::from_str(&text).unwrap();
        v.as_object_mut().unwrap().remove("unread").unwrap();
        let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("old");
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join("files.json"), serde_json::to_string(&v).unwrap()).unwrap();
        let old_view = FileStateCache::read(&folder);
        let mut run = prints(&dir);
        run.seed(&old_view);
        let same = tracked(&mut run, &[&f]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (0, 1));
        assert!(same.diff(&old_view).is_none());
        edit_in_place(&f);
        let mut run = prints(&dir);
        run.seed(&old_view);
        let after = tracked(&mut run, &[&f]);
        assert_eq!(run.stats().hashed_files, 1);
        assert!(after.diff(&old_view).is_some(), "a changed stamp reads as a changed file for the old reader too");
    }

    #[test]
    fn the_plan_says_what_will_be_read_and_why_before_anything_is() {
        let dir = Dir::new();
        let files: Vec<_> = (0..3).map(|i| dir.file(&format!("g_{i:03}.dat"), &vec![i as u8; MIB])).collect();
        let paths: Vec<PathBuf> = files.clone();

        let mut run = prints(&dir);
        let plan = run.announce("Game data", &paths, Need::Identity);
        assert_eq!((plan.files, plan.bytes, plan.recorded_files, plan.read_files), (3, 3 * MIB as u64, 3, 0));
        assert!(plan.describe("Game data").contains("3 file(s) (3.0 MiB) recorded by their change stamp, not read"), "{}", plan.describe("Game data"));

        let mut recent = Fingerprints::with(&dir.0, stamp::read, true, Duration::from_secs(3600));
        let plan = recent.announce("Game data", &paths, Need::Identity);
        assert_eq!((plan.read_files, plan.read_bytes), (3, 3 * MIB as u64));
        let line = plan.describe("Game data");
        assert!(line.contains("reading 3 file(s) (3.0 MiB): changed in the last few seconds (3)"), "{line}");
        assert_eq!(recent.stats().hashed_bytes, 0, "announcing reads nothing");

        let mut run = prints(&dir);
        let saved = saved_and_reloaded(&dir, &tracked(&mut prints(&dir), &[&files[0].as_path()]));
        run.seed(&saved);
        let plan = run.announce("Game data", &paths, Need::Identity);
        assert_eq!((plan.reused_files, plan.recorded_files), (1, 2));
    }

    // ---- an interrupted run ----

    #[test]
    fn an_interrupted_run_continues_from_the_files_it_had_read() {
        let dir = Dir::new();
        let files: Vec<_> = (0..4).map(|i| dir.file(&format!("m_{i}.bin"), &vec![i as u8; MIB])).collect();
        let refs: Vec<&Path> = files.iter().map(|p| p.as_path()).collect();
        let cache_dir = dir.0.join("cache");
        fs::create_dir(&cache_dir).unwrap();
        let journal = cache_dir.join("files.partial.json");

        // a run reads three of the four files and is killed: no `finish`, nothing else saved
        {
            let mut run = prints(&dir);
            run.resume_from(&cache_dir);
            run.journal_every(Duration::ZERO);
            tracked(&mut run, &refs[..3]);
            assert_eq!(run.stats().hashed_files, 3);
        }
        assert!(journal.exists());
        let partial = FileStateCache::read_file(&journal).expect("the journal parses");
        assert_eq!((partial.files.len(), partial.stamps.len()), (3, 3));
        let mut candidate = FileStateCache::default();
        candidate.configuration("whatever");
        assert!(partial.diff(&candidate).is_some() && candidate.diff(&partial).is_some(), "a journal is never a hit");

        // the next start: the three are not read again, the fourth is
        let mut run = prints(&dir);
        run.resume_from(&cache_dir);
        run.journal_every(Duration::ZERO);
        let cache = tracked(&mut run, &refs);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.hashed_bytes, s.reused_files), (1, MIB as u64, 3), "{s}");
        assert_eq!(cache.files.len(), 4);
        // and what it has now is in the journal too, should it be killed as well
        assert_eq!(FileStateCache::read_file(&journal).unwrap().files.len(), 4);
        run.finish();
        assert!(!journal.exists(), "a run that ends well leaves no journal");
    }

    #[test]
    fn the_journal_resumes_a_compare_of_a_cache_that_knows_the_files_without_stamps() {
        // dev.20's cache (CRCs only, so every file must be read to be compared) and a run killed half way through
        let dir = Dir::new();
        let files: Vec<_> = (0..4).map(|i| dir.file(&format!("g_{i:03}.dat"), &vec![i as u8; MIB])).collect();
        let refs: Vec<&Path> = files.iter().map(|p| p.as_path()).collect();
        let mut v: serde_json::Value = serde_json::to_value(&tracked(&mut prints(&dir), &refs)).unwrap();
        v.as_object_mut().unwrap().remove("stamps").unwrap();
        let folder = camino::Utf8Path::from_path(&dir.0).unwrap().join("dev20");
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join("files.json"), serde_json::to_string(&v).unwrap()).unwrap();
        let saved = FileStateCache::read(&folder);
        let cache_dir = dir.0.join("cache");
        fs::create_dir(&cache_dir).unwrap();
        {
            let mut run = prints(&dir);
            run.seed(&saved);
            run.resume_from(&cache_dir);
            run.journal_every(Duration::ZERO);
            tracked_game(&mut run, &refs[..2]);
            assert_eq!(run.stats().hashed_files, 2, "each is read to be compared with the CRC the saved cache has");
        }
        let mut run = prints(&dir);
        run.seed(&saved);
        run.resume_from(&cache_dir);
        let cache = tracked_game(&mut run, &refs);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.reused_files), (2, 2), "the two the first run had read are not read again: {s}");
        assert!(cache.diff(&saved).is_none(), "and the cache is still a hit");
    }

    #[test]
    fn a_journal_never_vouches_for_a_file_that_changed_since_and_a_torn_one_is_ignored() {
        let dir = Dir::new();
        let a = dir.file("m_a.bin", &vec![1u8; MIB]);
        let b = dir.file("m_b.bin", &vec![2u8; MIB]);
        let cache_dir = dir.0.join("cache");
        fs::create_dir(&cache_dir).unwrap();
        {
            let mut run = prints(&dir);
            run.resume_from(&cache_dir);
            run.journal_every(Duration::ZERO);
            tracked(&mut run, &[&a, &b]);
        }
        edit_in_place(&a);
        let mut run = prints(&dir);
        run.resume_from(&cache_dir);
        let cache = tracked(&mut run, &[&a, &b]);
        let s = run.stats();
        assert_eq!((s.hashed_files, s.reused_files), (1, 1), "the edited file is read again: {s}");
        let fresh = tracked(&mut prints(&dir), &[&a, &b]);
        assert!(cache.diff(&fresh).is_none(), "the same fingerprints as reading both");

        // half a journal (the disk lost the end of it): ignored, the run reads everything
        let text = fs::read_to_string(cache_dir.join("files.partial.json")).unwrap();
        fs::write(cache_dir.join("files.partial.json"), &text[..text.len() / 2]).unwrap();
        let mut run = prints(&dir);
        run.resume_from(&cache_dir);
        tracked(&mut run, &[&a, &b]);
        assert_eq!((run.stats().hashed_files, run.stats().reused_files), (2, 0));
    }

    #[test]
    fn a_run_that_only_reuses_writes_no_journal_and_a_full_hash_run_removes_one() {
        let dir = Dir::new();
        let f = dir.file("m_a.bin", &vec![1u8; MIB]);
        let cache_dir = dir.0.join("cache");
        fs::create_dir(&cache_dir).unwrap();
        let saved = saved_and_reloaded(&dir, &tracked(&mut prints(&dir), &[&f]));
        let mut run = prints(&dir);
        run.seed(&saved);
        run.resume_from(&cache_dir);
        run.journal_every(Duration::ZERO);
        tracked(&mut run, &[&f]);
        assert!(!cache_dir.join("files.partial.json").exists(), "nothing was read, so there is nothing to journal");

        fs::write(cache_dir.join("files.partial.json"), "{}").unwrap();
        let mut full = Fingerprints::with(&dir.0, stamp::read, false, Duration::ZERO);
        full.resume_from(&cache_dir);
        assert!(!cache_dir.join("files.partial.json").exists());
    }

    #[test]
    fn nothing_is_journaled_for_a_volume_that_is_not_trusted() {
        fn frozen(f: &File) -> Option<Stamp> {
            stamp::read(f).map(|s| Stamp { changed: 1_000_000_000, ..s })
        }
        let dir = Dir::new();
        let f = dir.file("m_a.bin", &vec![1u8; MIB]);
        let cache_dir = dir.0.join("cache");
        fs::create_dir(&cache_dir).unwrap();
        let mut run = Fingerprints::with(&dir.0, frozen, true, Duration::ZERO);
        run.resume_from(&cache_dir);
        run.journal_every(Duration::ZERO);
        tracked(&mut run, &[&f]);
        assert!(!cache_dir.join("files.partial.json").exists(), "a stamp that is not believed cannot resume anything");
    }

}

/// `cargo test --release -p mod-loader-lua bench_ -- --ignored --nocapture --test-threads=1`
/// with KILN_EML_BENCH_DIR (a folder with room for sparse files), KILN_EML_BENCH_FILES (default 55) and
/// KILN_EML_BENCH_MIB (default 1024): the fingerprint steps of a start on a synthetic install (sparse files, nothing is
/// stored), as EML 0.2.0 ran them (every file read at every step), as dev.21 does (a recorded CRC is reused while the
/// OS stamp is unchanged) and now (a game file nobody needs the bytes of is recorded by its stamp). Prints bytes read
/// (the process' read counter) and wall time for the starts a player meets. The files are removed afterwards.
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
    let sparse = |name: &str, bytes: u64| File::create(game.join(name)).unwrap().set_len(bytes).unwrap();
    for i in 0..count {
        sparse(&format!("enshrouded_{i:03}.dat"), mib << 20);
    }
    // the other files of a real client (sizes of a dev.21 install): exe, header, resources and their backups
    for (name, size) in [("enshrouded.exe", 35), ("enshrouded.kfc", 9), ("enshrouded.kfc.bak", 9), ("enshrouded.kfc_resources", 215), ("enshrouded.kfc_resources.bak", 215)] {
        sparse(name, size << 20);
    }
    fs::write(game.join("enshrouded.kfc.bak.integrity"), vec![b'x'; 86]).unwrap();
    // let the files settle past the recent-change rule, as a real install's files are
    let window = Duration::from_millis(50);
    std::thread::sleep(Duration::from_millis(100));
    let cache_dir = camino::Utf8Path::from_path(&game).unwrap().join("kiln-cache");
    fs::create_dir_all(&cache_dir).unwrap();
    let journal = cache_dir.join("files.partial.json");
    let total_gib = (count as u64 * mib + 35 + 9 + 9 + 215 + 215) as f64 / 1024.0;

    fn rchar() -> u64 {
        fs::read_to_string("/proc/self/io").unwrap().lines()
            .find_map(|l| l.strip_prefix("rchar: ")).unwrap().trim().parse().unwrap()
    }
    fn frozen(f: &File) -> Option<Stamp> {
        stamp::read(f).map(|s| Stamp { changed: 1_000_000_000, ..s })
    }
    #[derive(Clone, Copy)]
    struct Mode { name: &'static str, reuse: bool, need: Need, modern: bool }
    let eml = Mode { name: "EML 0.2.0", reuse: false, need: Need::Content, modern: false };
    let dev21 = Mode { name: "dev.21", reuse: true, need: Need::Content, modern: false };
    let now = Mode { name: "now", reuse: true, need: Need::Identity, modern: true };

    // One start: what `run` does to the game's files. `saved` is the cache file read at the start (None: no cache);
    // `update` says the build id differs from the saved cache's, so that a patch is certain. Returns the snapshot to save.
    struct Start<'a> { mode: Mode, saved: Option<&'a FileStateCache>, update: bool, patch: bool, read: stamp::Reader, kill_after: Option<usize>, publish: bool }
    let cache_dir_std = cache_dir.as_std_path().to_owned();
    let run_start = |s: Start| -> (FileStateCache, Fingerprints) {
        let mut run = Fingerprints::with(&game, s.read, s.mode.reuse, window);
        if s.mode.modern {
            run.resume_from(&cache_dir_std);
        }
        if let Some(saved) = s.saved {
            run.seed(saved);
        }
        if s.mode.modern && (s.saved.is_none() || s.update) {
            run.patch_certain();
        }
        let need = s.mode.need;
        let mut cache = FileStateCache::default();
        if let Some(k) = s.kill_after {
            // the first k files of the cache check, then the process is gone
            let mut paths: Vec<_> = fs::read_dir(&game).unwrap().map(|e| e.unwrap().path())
                .filter(|p| p.extension().is_some_and(|e| matches!(e.to_str(), Some("dat" | "kfc" | "kfc_resources" | "exe" | "bak" | "integrity")))).collect();
            paths.sort();
            for p in &paths[..k.min(paths.len())] {
                cache.track_as(p, &mut run, need);
            }
            return (cache, run);
        }
        cache.track_game_files_as(&game, &mut run, need); // cache check
        if !s.patch {
            return (cache, run);
        }
        let mut before = FileStateCache::default();
        before.track_game_inputs_as(&game, "enshrouded", count, &mut run, need); // before Lua
        let mut at_publication = FileStateCache::default();
        at_publication.track_game_inputs_as(&game, "enshrouded", count, &mut run, need); // before publication
        assert!(before.diff(&at_publication).is_none());
        // publication: the header and the resource file are new files (the rows that compare with a saved cache leave
        // the files as they are, so that the cache stays the cache of these files)
        for (name, size) in if s.publish { vec![("enshrouded.kfc", 9u64), ("enshrouded.kfc_resources", 215)] } else { vec![] } {
            let f = fs::OpenOptions::new().write(true).open(game.join(name)).unwrap();
            f.set_len(0).unwrap();
            f.set_len(size << 20).unwrap();
            use std::os::unix::fs::FileExt;
            f.write_at(b"patched", 0).unwrap();
        }
        let mut after = FileStateCache::default();
        after.track_game_files_as(&game, &mut run, need); // the snapshot saved
        (after, run)
    };
    let gib = |bytes: u64| bytes as f64 / (1u64 << 30) as f64;
    // (row, mode, GiB read, seconds)
    let mut table: Vec<(String, &'static str, f64, f64)> = Vec::new();
    let mut record = |row: &str, mode: Mode, read: u64, wall: Duration, run: &Fingerprints| {
        println!("BENCH {row:<58} {:<10} read {:>9.2} GiB  wall {:>7.1} s  [{}]", mode.name, gib(read), wall.as_secs_f64(), run.stats());
        table.push((row.to_owned(), mode.name, gib(read), wall.as_secs_f64()));
    };
    println!("BENCH {count} sparse containers of {mib} MiB + exe, header, resources and backups = {total_gib:.1} GiB, in {}", game.display());

    // KILN_EML_BENCH_SKIP_EML=1 leaves out EML 0.2.0's rows (it reads 220 GiB per patch run, minutes of the run);
    // KILN_EML_BENCH_SKIP_MODES=1 leaves out the rows of the first three starts, for a rerun of the later ones
    let skip_eml = std::env::var("KILN_EML_BENCH_SKIP_EML").is_ok_and(|v| v == "1");
    let skip_modes = std::env::var("KILN_EML_BENCH_SKIP_MODES").is_ok_and(|v| v == "1");
    for mode in [eml, dev21, now] {
        if skip_modes || (skip_eml && mode.name == "EML 0.2.0") {
            continue;
        }
        let _ = fs::remove_file(cache_dir.join("files.json"));
        let _ = fs::remove_file(&journal);

        // 1. first start ever
        let (r0, t0) = (rchar(), Instant::now());
        let (snapshot, run) = run_start(Start { mode, saved: None, update: false, patch: true, read: stamp::read, kill_after: None, publish: true });
        snapshot.write(&cache_dir);
        record("first start ever (no cache), patch run", mode, rchar() - r0, t0.elapsed(), &run);
        drop(run);

        // 2. the start after a patch run (the files the patch wrote are read once and stamped), then 3. one that changes nothing
        for row in ["start after a patch run (stamps of the new files)", "start where nothing changed"] {
            let (r0, t0) = (rchar(), Instant::now());
            let saved = FileStateCache::read(&cache_dir);
            let (hit, run) = run_start(Start { mode, saved: Some(&saved), update: false, patch: false, read: stamp::read, kill_after: None, publish: true });
            assert!(hit.diff(&saved).is_none(), "{row}");
            if hit.stamps_differ(&saved) {
                hit.write(&cache_dir);
            }
            record(row, mode, rchar() - r0, t0.elapsed(), &run);
            drop(run);
            std::thread::sleep(window * 2);
        }

        // 4. a mod changed: the patch runs on a warm cache
        let (r0, t0) = (rchar(), Instant::now());
        let saved = FileStateCache::read(&cache_dir);
        let (snapshot, run) = run_start(Start { mode, saved: Some(&saved), update: false, patch: true, read: stamp::read, kill_after: None, publish: true });
        snapshot.write(&cache_dir);
        record("a mod changed (patch run, warm cache)", mode, rchar() - r0, t0.elapsed(), &run);
        drop(run);
        std::thread::sleep(window * 2);

        // what this build leaves behind once its start after a patch run is over: the cache file the next build finds
        let saved = FileStateCache::read(&cache_dir);
        let (hit, run) = run_start(Start { mode, saved: Some(&saved), update: false, patch: false, read: stamp::read, kill_after: None, publish: true });
        if hit.stamps_differ(&saved) {
            hit.write(&cache_dir);
        }
        drop(run);
        std::thread::sleep(window * 2);
    }

    // The cache a Kiln that predates this build left, for the files as they are now: dev.21's (CRCs and stamps), and
    // dev.20's (CRCs only). The rows below leave the files alone, so that the saved cache stays right for them.
    let _ = fs::remove_file(cache_dir.join("files.json"));
    let _ = fs::remove_file(&journal);
    let (snapshot, run) = run_start(Start { mode: dev21, saved: None, update: false, patch: true, read: stamp::read, kill_after: None, publish: true });
    snapshot.write(&cache_dir);
    drop(run);
    std::thread::sleep(window * 2);
    let saved = FileStateCache::read(&cache_dir);
    let (learned, run) = run_start(Start { mode: dev21, saved: Some(&saved), update: false, patch: false, read: stamp::read, kill_after: None, publish: false });
    drop(run);
    if learned.stamps_differ(&saved) {
        learned.write(&cache_dir);
    }
    let dev21_cache = FileStateCache::read(&cache_dir);

    // the first start after a Kiln update: the build id differs, so the patch runs; the saved cache is dev.21's or dev.20's
    let dev21_saved = dev21_cache;
    let mut dev20_saved = dev21_saved.clone();
    dev20_saved.stamps.clear();
    for (row, saved) in [("first start after a Kiln update, dev.21's cache (stamps)", &dev21_saved), ("first start after a Kiln update, dev.20's cache (no stamps)", &dev20_saved)] {
        for mode in [dev21, now] {
            let _ = fs::remove_file(&journal);
            let (r0, t0) = (rchar(), Instant::now());
            let (snapshot, run) = run_start(Start { mode, saved: Some(saved), update: true, patch: true, read: stamp::read, kill_after: None, publish: false });
            drop(snapshot);
            record(row, mode, rchar() - r0, t0.elapsed(), &run);
            drop(run);
            std::thread::sleep(window * 2);
        }
    }

    // a start that was killed half way through reading, and the start after it. Where nothing needs the bytes (the
    // containers of a first start), there is little to read in the first place; where the CRCs decide (dev.20's cache and
    // an unchanged build), the second start continues from what the first had read.
    let half = (count + 5) / 2;
    for (row, saved, update) in [("restart after a first start killed half way", None, false), ("restart after a start killed half way through comparing CRCs (no stamps in the cache, same build)", Some(&dev20_saved), false)] {
        for mode in [dev21, now] {
            let _ = fs::remove_file(&journal);
            let (r0, _) = (rchar(), Instant::now());
            let (_, killed) = run_start(Start { mode, saved, update, patch: true, read: stamp::read, kill_after: Some(half), publish: false });
            let first_read = rchar() - r0;
            drop(killed);
            let (r0, t0) = (rchar(), Instant::now());
            let (snapshot, run) = run_start(Start { mode, saved, update, patch: true, read: stamp::read, kill_after: None, publish: false });
            drop(snapshot);
            record(&format!("{row} (the killed start read {:.1} GiB)", gib(first_read)), mode, rchar() - r0, t0.elapsed(), &run);
            drop(run);
            std::thread::sleep(window * 2);
        }
    }

    // a volume that fails the stamp test: nothing can be assumed of any file, so every start reads everything
    for mode in [dev21, now] {
        let _ = fs::remove_file(cache_dir.join("files.json"));
        let _ = fs::remove_file(&journal);
        let (snapshot, run) = run_start(Start { mode, saved: None, update: false, patch: true, read: frozen, kill_after: None, publish: true });
        snapshot.write(&cache_dir);
        drop(run);
        let (r0, t0) = (rchar(), Instant::now());
        let saved = FileStateCache::read(&cache_dir);
        let (hit, run) = run_start(Start { mode, saved: Some(&saved), update: false, patch: false, read: frozen, kill_after: None, publish: true });
        assert!(hit.diff(&saved).is_none());
        record("volume fails the stamp test, start where nothing changed", mode, rchar() - r0, t0.elapsed(), &run);
        drop(run);
    }

    println!("\n| start | mode | read | time |\n|---|---|---|---|");
    for (row, mode, read, wall) in &table {
        println!("| {row} | {mode} | {read:.2} GiB | {wall:.1} s |");
    }
}

// kiln-eml: a backup an earlier release recorded by its change stamp only (`unread`) is proof for adoption
// (ownership.rs `adopt`) exactly while that stamp, length and last-write time are the file's, on a volume that passes
// the scratch-file test; a same-size edit with the last-write time put back moves the stamp and is no proof.
#[test]
fn describes_takes_a_stamp_record_only_while_the_stamp_is_the_files() {
    let dir = Dir::new();
    let path = dir.file("g.kfc.bak", b"vanilla backup");
    let meta = fs::metadata(&path).unwrap();
    let mtime = meta.modified().unwrap().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
    let saved: Stamp = stamp::read(&File::open(&path).unwrap()).expect("this file system keeps stamps");
    let mut cache = FileStateCache::default();
    cache.files.insert(path.clone(), (meta.len(), mtime, 0x5eed));
    cache.stamps.insert(path.clone(), saved);
    cache.unread.insert(path.clone());
    let content = (meta.len(), 0);
    assert_eq!(cache.describes(&path, content, &mut None), Ok(()));
    // the same record without its stamp is no proof
    let mut stampless = cache.clone();
    stampless.stamps.clear();
    assert!(stampless.describes(&path, content, &mut None).is_err());
    // a same-size rewrite with the last-write time put back
    std::thread::sleep(SETTLE);
    let mut f = OpenOptions::new().write(true).open(&path).unwrap();
    f.write_all(b"VANILLA BACKUP").unwrap();
    f.set_modified(meta.modified().unwrap()).unwrap();
    drop(f);
    assert_eq!(fs::metadata(&path).unwrap().len(), meta.len());
    assert_eq!(cache.describes(&path, content, &mut None), Err("it changed since the last run saved it".into()));
    // a volume that failed the scratch-file test vouches for nothing
    let mut failed = Some(Err("the test failed".to_owned()));
    let mut fresh = cache.clone();
    fresh.stamps.insert(path.clone(), stamp::read(&File::open(&path).unwrap()).unwrap());
    let now = fs::metadata(&path).unwrap().modified().unwrap().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
    fresh.files.insert(path.clone(), (meta.len(), now, 0x5eed));
    assert_eq!(fresh.describes(&path, content, &mut None), Ok(()));
    assert!(fresh.describes(&path, content, &mut failed).is_err());
}
