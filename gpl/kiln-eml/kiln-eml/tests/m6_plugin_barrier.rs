// SPDX-License-Identifier: GPL-3.0-or-later
// tests of the interpreter's startup seam.
//! kiln-eml's wiring of kiln.eml.startup v1 on the synthetic m6 archive, game-free: the early phase
//! (`startup_seam::early`: the plan read as kiln-sfi reads it, the install lock, the startup controller's witness), the five members of the
//! real C table driven as kiln-sfi drives them, the on_ready barrier through the runner (kiln mode goes to the startup
//! pass), the grant (a PUBLISHED view acquired through kiln.eml.archive), and kiln/state/eml.json's rows. The cases
//! that belong here: lock busy, no attach, no terminal (the 20 s wait), a DLL changes the
//! input, panics, and the outcome before the barrier. Each start runs in a child process (the seam
//! is process-wide, one start per process). The full two-DLL integration is tested elsewhere.
#[path = "support/m6_barrier/mod.rs"]
mod barrier;
#[path = "support/m6_fixture/mod.rs"]
mod fixture;

use barrier::{Game, LAUNCH_ID, PLAN_ID, Sfi, content, hex, pkg, plan_id_bytes, published, row, terminal};
use kiln_abi::{
    KILN_ERR_BUFFER_TOO_SMALL, KILN_ERR_INVALID_ARGUMENT, KILN_ERR_UNAVAILABLE, KILN_OK,
    services::kiln_eml_archive::KILN_EML_ARCHIVE_PUBLISHED,
};
use kiln_eml::startup_seam::{self, BarrierPorts, Barriered};
use mod_loader_lua::test_gate;
use serde_json::{Value, json};
use std::{
    collections::BTreeSet,
    time::{Duration, Instant},
};

const CASE: &str = "KILN_EML_BARRIER_CASE";

/// Runs `body` in a child process of this test binary (the seam's state is the process's).
fn in_child(name: &str, body: impl FnOnce()) {
    if std::env::var(CASE).as_deref() == Ok(name) {
        body();
        return;
    }
    let out = test_gate::output(
        std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", name, "--nocapture", "--test-threads=1"])
            .env(CASE, name),
    )
    .unwrap();
    let stdout = String::from_utf8_lossy(&out.stdout);
    assert!(
        out.status.success() && stdout.contains("1 passed"),
        "{name}: {stdout}{}",
        String::from_utf8_lossy(&out.stderr)
    );
}

/// The barrier as on_ready runs it, with the production wait.
fn run_barrier(game: &Game, observe: &dyn Fn(kiln_eml::startup::Point<'_>)) -> Box<startup_seam::Finished> {
    let cache = game.kiln().join("eml/cache");
    match startup_seam::barrier(&BarrierPorts {
        cache_dir: &cache,
        unlisted: &[],
        wait: startup_seam::TERMINAL_WAIT,
        observe,
    }) {
        Barriered::Done(finished) => finished,
        Barriered::NoStart => panic!("no start"),
        Barriered::Faulted(why) => panic!("faulted: {why}"),
    }
}

/// The v1 bytes of two identity records (every pin; `size` aside).
fn same_identity(a: &kiln_abi::services::kiln_eml_archive::KilnEmlArchiveIdentityV1, b: &kiln_abi::services::kiln_eml_archive::KilnEmlArchiveIdentityV1) -> bool {
    let bytes = |r: &kiln_abi::services::kiln_eml_archive::KilnEmlArchiveIdentityV1| {
        // SAFETY: a plain repr(C) record of integers and byte arrays, read as its 160 bytes.
        unsafe { std::slice::from_raw_parts((r as *const _ as *const u8).add(4), 156) }.to_vec()
    };
    bytes(a) == bytes(b)
}

/// published_identity after a barrier: OK with the granted identity exactly when the start has a grant, else
/// UNAVAILABLE with nothing written; the session-free key is the only one it takes.
fn published_matches(hash: [u8; 32], granted: Option<&kiln_abi::services::kiln_eml_archive::KilnEmlArchiveIdentityV1>) {
    let (rc, got, tail) = published(LAUNCH_ID, hash, 0, 168);
    assert_eq!(tail, [0xab; 8], "bytes past v1 are never written");
    match granted {
        Some(identity) => {
            assert_eq!(rc, KILN_OK);
            assert_eq!(got.size, 168, "the size the caller set is kept");
            assert!(same_identity(&got, identity), "the identity outcome serves");
            assert_eq!(got.phase, KILN_EML_ARCHIVE_PUBLISHED as u32);
        }
        None => {
            assert_eq!(rc, KILN_ERR_UNAVAILABLE, "no grant: no identity");
            assert_eq!((got.generation, got.size), (0xdead, 168), "nothing written");
        }
    }
}

fn entry<'a>(out: &'a kiln_eml::runner::Outcome, id: &str) -> &'a kiln_eml::discover::Entry {
    out.mods
        .iter()
        .find(|e| {
            e.id.as_deref() == Some(id) || e.path.ends_with(&format!("/{id}")) || e.path.ends_with(&format!("\\{id}"))
        })
        .unwrap_or_else(|| panic!("no entry {id}: {:#?}", out.mods))
}

// The table without a start: the getter serves one table for version 0 and 1 and none above; a process that is no
// kiln-backend start (no early phase) answers attach UNAVAILABLE, and nothing else without a session.
#[test]
fn the_early_export_serves_one_table_and_grants_nothing() {
    in_child("the_early_export_serves_one_table_and_grants_nothing", || {
        let t0 = startup_seam::kiln_eml_startup_service(0);
        let t1 = startup_seam::kiln_eml_startup_service(1);
        assert!(!t1.is_null() && t0 == t1);
        assert!(startup_seam::kiln_eml_startup_service(2).is_null());
        let t = barrier::table();
        assert_eq!((t.header.size, t.header.version), (56, 1));
        // No launch installed (no kiln-backend start): no identity, whatever the key
        assert_eq!(published(LAUNCH_ID, [7; 32], 0, 160).0, KILN_ERR_UNAVAILABLE);
        assert_eq!(published(LAUNCH_ID ^ 1, [8; 32], 0, 160).0, KILN_ERR_UNAVAILABLE);
        assert_eq!(
            Sfi::attach([7; 32], 0).err(),
            Some(KILN_ERR_UNAVAILABLE),
            "not a kiln start"
        );
        let ghost = Sfi {
            plan_sha256: [7; 32],
            session: 1,
        };
        assert_eq!(ghost.witness(0).0, KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(ghost.terminal("{}"), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(ghost.outcome().0, KILN_ERR_INVALID_ARGUMENT);
    });
}

// A kiln start end to end: the witness is READY before any DLL could load; the outcome is UNAVAILABLE
// until the barrier; the recheck confirms the input; the terminal is taken once; the barrier publishes once and grants
// the published generation; the identity's pins are the launch's and acquire a PUBLISHED view; the state file shows the
// pass's rows (source shroudforge) and an unlisted EML drop-in skipped; after the barrier attach and
// recheck are UNAVAILABLE; the record is the same at every read.
#[test]
fn a_kiln_start_is_witnessed_before_its_dlls_and_granted_once() {
    in_child("a_kiln_start_is_witnessed_before_its_dlls_and_granted_once", || {
        let game = Game::new(
            "g14b-happy",
            &[
                pkg("alpha", &content("alpha blob")),
                pkg("beta", &content("beta blob")).needs("alpha"),
                pkg("dll-pack", &content("dll-pack blob")).with_dll(),
            ],
        );
        // an unlisted EML drop-in (an add_mod handover or a kiln/eml/mods folder the plan does not list)
        let stray = game.kiln().join("eml/mods/stray");
        std::fs::create_dir_all(stray.join("src")).unwrap();
        std::fs::write(
            stray.join("mod.json"),
            br#"{"id":"stray","name":"stray","version":"1.0.0","capabilities":["patch"]}"#,
        )
        .unwrap();
        std::fs::write(stray.join("src/mod.lua"), "-- fixture").unwrap();
        // The plan names the BASE origin the inspector sees
        let bytes = game.plan(|p| p["game"]["archiveBaseOrigin"] = json!("first-managed-unclassified"));
        let (hash, profile) = game.freeze(&bytes);
        assert!(!game.game().join("enshrouded.kfc.bak").exists());
        game.early(hash, &profile).unwrap();
        assert!(
            game.game().join("enshrouded.kfc.bak").exists(),
            "the witness captured the working baseline"
        );
        // the key's rules: a wrong launch, a short record, flags, a session: refused, and no state changed
        let bad = |edit: &dyn Fn(&mut kiln_abi::services::kiln_eml_startup::KilnEmlStartupKeyV1)| {
            let mut k = barrier::key(LAUNCH_ID, hash, 0);
            edit(&mut k);
            let mut session = 0u64;
            // SAFETY: a sized key, a live out-pointer.
            unsafe { (barrier::table().attach.unwrap())(&raw const k, 1, &raw mut session) }
        };
        assert_eq!(bad(&|k| k.launch_id ^= 1), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(bad(&|k| k.plan_sha256[0] ^= 1), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(bad(&|k| k.size = 48), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(bad(&|k| k.flags = 1), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(bad(&|k| k.session = 5), KILN_ERR_INVALID_ARGUMENT);
        // published_identity before the barrier: no identity yet; its key's rules; none of it changes a state (the
        // attach below is still the one attach)
        let (rc, unwritten, _) = published(LAUNCH_ID, hash, 0, 160);
        assert_eq!((rc, unwritten.generation), (KILN_ERR_UNAVAILABLE, 0xdead));
        assert_eq!(published(LAUNCH_ID ^ 1, hash, 0, 160).0, KILN_ERR_INVALID_ARGUMENT);
        let mut other = hash;
        other[31] ^= 1;
        assert_eq!(published(LAUNCH_ID, other, 0, 160).0, KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(published(LAUNCH_ID, hash, 5, 160).0, KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(published(LAUNCH_ID, hash, 0, 152).0, KILN_ERR_INVALID_ARGUMENT);
        let sfi = Sfi::attach(hash, 1).expect("the one attach");
        assert_eq!(
            Sfi::attach(hash, 1).err(),
            Some(KILN_ERR_UNAVAILABLE),
            "a second attach"
        );
        // the loader thread: the witness first
        let (rc, state, plan_id, revision, reason) = sfi.witness(2000);
        assert_eq!((rc, state, plan_id, reason), (KILN_OK, 1, plan_id_bytes(), None));
        assert_ne!(revision, 0);
        assert_eq!(sfi.witness(0).3, revision, "the state never changes once decided");
        // no outcome before the barrier
        assert_eq!(sfi.outcome().0, KILN_ERR_UNAVAILABLE);
        // the recheck after the DLLs: the input unchanged; another witness is refused
        assert_eq!(sfi.recheck(plan_id, revision), (KILN_OK, None));
        assert_eq!(sfi.recheck(plan_id, revision + 1).0, KILN_ERR_INVALID_ARGUMENT);
        let t = terminal(false, false, Ok(()), &[], &[("dll-pack", "loaded")]);
        assert_eq!(sfi.terminal(&t), KILN_OK);
        assert_eq!(sfi.terminal(&t), KILN_ERR_UNAVAILABLE, "one terminal");
        let notes = startup_seam::late_notes();
        assert!(
            notes.iter().any(|n| n.contains("refused a second native terminal")),
            "a second terminal is noted for kiln.log: {notes:?}"
        );
        // on_ready: the runner takes kiln mode to the barrier
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Done, "{:#?}", out.mods);
        for id in ["alpha", "beta", "dll-pack"] {
            let e = entry(&out, id);
            assert_eq!(
                (e.source.as_str(), e.state.as_str()),
                ("shroudforge", "loaded"),
                "{e:?}"
            );
        }
        let s = entry(&out, "stray");
        assert_eq!(s.state, "skipped", "{s:?}");
        assert!(
            s.reason.as_deref().unwrap().contains("does not list this EML mod"),
            "{s:?}"
        );
        let contents = game.contents();
        for blob in ["alpha blob", "beta blob", "dll-pack blob"] {
            assert!(contents.contains(&blob.to_owned()), "{blob}: {contents:?}");
        }
        // after the barrier
        assert_eq!(Sfi::attach(hash, 1).err(), Some(KILN_ERR_UNAVAILABLE));
        assert_eq!(sfi.recheck(plan_id, revision).0, KILN_ERR_UNAVAILABLE, "the wait ended");
        // the outcome and the grant
        let (rc, identity, record) = sfi.outcome();
        assert_eq!(rc, KILN_OK);
        let record = record.unwrap();
        assert_eq!(
            (record["state"].as_str(), record["grant"].as_bool()),
            (Some("committed"), Some(true)),
            "{record}"
        );
        assert_eq!(record["launchId"], format!("{LAUNCH_ID:016x}"));
        assert_eq!(record["planId"], PLAN_ID);
        assert_eq!(record["planSha256"], hex(&hash));
        assert_eq!(record["archiveSha256"], hex(&identity.archive_sha256));
        assert_eq!(record["baseOrigin"]["token"], "first-managed-unclassified");
        assert_eq!(record["native"], json!({"received": true, "coherent": true}));
        for id in ["alpha", "beta", "dll-pack"] {
            assert_eq!(row(&record, id)["data"], "committed");
            assert_eq!(row(&record, id)["archivePresent"], true);
        }
        let mut launch = [0u8; 16];
        launch[8..].copy_from_slice(&LAUNCH_ID.to_be_bytes());
        assert_eq!(identity.size, 160);
        assert_eq!(identity.phase, KILN_EML_ARCHIVE_PUBLISHED as u32);
        assert_eq!(
            (identity.launch_id, identity.plan_id, identity.plan_sha256),
            (launch, plan_id_bytes(), hash)
        );
        assert_eq!(identity.reload_epoch, 1);
        assert!(identity.generation != 0 && identity.registry_id != 0);
        // the grant: kiln-sfi acquires a PUBLISHED view with these pins (kiln.eml.archive v1)
        let archive = kiln_eml::archive_service::table();
        let mut view = 0u64;
        // SAFETY: a sized identity, a live out-pointer.
        let rc = unsafe { (archive.acquire.unwrap())(7, &raw const identity, &raw mut view) };
        assert_eq!((rc, view != 0), (KILN_OK, true));
        // every read gives the same answer
        let (_, again, record2) = sfi.outcome();
        assert_eq!(
            (record2.unwrap(), again.archive_sha256),
            (record, identity.archive_sha256)
        );
        // kiln.boot's session-free read of the same identity: exactly outcome's, its size kept, bytes past v1
        // untouched; kiln-sfi's session is not its key; a short record, another launch or plan, a null pointer: refused
        published_matches(hash, Some(&identity));
        published_matches(hash, Some(&identity));
        assert_eq!(published(LAUNCH_ID, hash, sfi.session, 160).0, KILN_ERR_INVALID_ARGUMENT);
        let (rc, short, _) = published(LAUNCH_ID, hash, 0, 159);
        assert_eq!((rc, short.generation), (KILN_ERR_INVALID_ARGUMENT, 0xdead));
        assert_eq!(published(LAUNCH_ID ^ 1, hash, 0, 160).0, KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(published(LAUNCH_ID, other, 0, 160).0, KILN_ERR_INVALID_ARGUMENT);
        let k = barrier::key(LAUNCH_ID, hash, 0);
        // SAFETY: a sized key; null identity and null key are refused before any read.
        unsafe {
            assert_eq!((barrier::table().published_identity.unwrap())(&raw const k, std::ptr::null_mut()), KILN_ERR_INVALID_ARGUMENT);
            let mut id = barrier::blank_identity();
            assert_eq!((barrier::table().published_identity.unwrap())(std::ptr::null(), &raw mut id), KILN_ERR_INVALID_ARGUMENT);
        }
        // the identity acquires the same PUBLISHED view kiln-sfi's does (kiln.boot's owner is another one)
        let (_, boot, _) = published(LAUNCH_ID, hash, 0, 160);
        let mut boot_view = 0u64;
        // SAFETY: a sized identity, a live out-pointer.
        let rc = unsafe { (archive.acquire.unwrap())(0xb007, &raw const boot, &raw mut boot_view) };
        assert_eq!((rc, boot_view != 0 && boot_view != view), (KILN_OK, true));
    });
}

// kiln-sfi never attached (absent, refused early, its state not writable): the barrier does not wait; the
// ShroudForge dialect's packages are left out (backend-unavailable) and the BASE stays live.
#[test]
fn without_an_attach_the_barrier_does_not_wait() {
    in_child("without_an_attach_the_barrier_does_not_wait", || {
        let game = Game::new("g14b-k5", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let started = Instant::now();
        let finished = run_barrier(&game, &|_| {});
        assert!(finished.waited < Duration::from_millis(500), "{:?}", finished.waited);
        assert!(started.elapsed() < Duration::from_secs(10));
        let out = &finished.outcome;
        assert_eq!(out.packages[0].code.as_deref(), Some("backend-unavailable"), "{out:?}");
        assert!(!out.native.received);
        assert!(!game.contents().contains(&"alpha blob".to_owned()));
        // session-free, so it answers without an attach: the identity exactly when this start has a grant
        published_matches(hash, finished.identity.as_ref());
        assert_eq!(
            Sfi::attach(hash, 0).err(),
            Some(KILN_ERR_UNAVAILABLE),
            "attach after the barrier began"
        );
    });
}

// kiln-sfi attached but pushed no terminal: the barrier waits 20 s from the attach (TERMINAL_WAIT), then the
// packages with native code and what needs them are left out (native-terminal-missing); a terminal after the wait is
// UNAVAILABLE and only noted for kiln.log.
#[test]
fn an_attach_without_a_terminal_waits_twenty_seconds_from_the_attach() {
    in_child(
        "an_attach_without_a_terminal_waits_twenty_seconds_from_the_attach",
        || {
            let game = Game::new(
                "g14b-k6",
                &[
                    pkg("dll-pack", &content("dll-pack blob")).with_dll(),
                    pkg("needs-dll", &content("needs-dll blob")).needs("dll-pack"),
                    pkg("plain", &content("plain blob")),
                ],
            );
            let (hash, profile) = game.freeze(&game.plan(|_| {}));
            game.early(hash, &profile).unwrap();
            let attached = Instant::now();
            let sfi = Sfi::attach(hash, 1).unwrap();
            std::thread::sleep(Duration::from_secs(2));
            let finished = run_barrier(&game, &|_| {});
            let after = attached.elapsed();
            assert!(after >= startup_seam::TERMINAL_WAIT, "{after:?}");
            assert!(
                finished.waited < Duration::from_secs(19),
                "measured from the attach, not from on_ready: {:?}",
                finished.waited
            );
            assert!(
                after < startup_seam::TERMINAL_WAIT + Duration::from_secs(8),
                "{after:?}"
            );
            let out = &finished.outcome;
            let code = |id: &str| out.packages.iter().find(|r| r.id == id).unwrap().code.clone();
            assert_eq!(code("dll-pack").as_deref(), Some("native-terminal-missing"));
            assert_eq!(code("needs-dll").as_deref(), Some("native-terminal-missing"));
            assert_eq!(
                out.packages.iter().find(|r| r.id == "needs-dll").unwrap().chain,
                ["dll-pack"]
            );
            assert_eq!(code("plain"), None, "{out:?}");
            assert!(game.contents().contains(&"plain blob".to_owned()));
            let t = terminal(false, false, Ok(()), &[], &[("dll-pack", "loaded")]);
            assert_eq!(sfi.terminal(&t), KILN_ERR_UNAVAILABLE);
            let notes = startup_seam::late_notes();
            assert!(
                notes
                    .iter()
                    .any(|n| n.contains("arrived after kiln-eml's startup wait ended")),
                "{notes:?}"
            );
        },
    );
}

// The install lock is busy at the early witness (another process publishing game data): the witness is REFUSED
// (no DLL may load), the reason reaches kiln-sfi whole (BUFFER_TOO_SMALL gives the complete size and writes nothing
// else), and on_ready blocks the start (the existing stop before the game's entry point); nothing is granted.
#[test]
fn a_busy_install_lock_refuses_the_witness_and_blocks_the_start() {
    in_child("a_busy_install_lock_refuses_the_witness_and_blocks_the_start", || {
        let game = Game::new("g14b-k1", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        let other = std::fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(game.game().join(".kiln-eml.lock"))
            .unwrap();
        other.try_lock().unwrap();
        game.early(hash, &profile).unwrap();
        assert!(!game.game().join("enshrouded.kfc.bak").exists(), "nothing captured");
        let sfi = Sfi::attach(hash, 0).unwrap();
        // a reason buffer too small: the complete size, nothing else written
        let k = sfi.key();
        let mut out = barrier::blank_witness();
        let mut small = [0u8; 4];
        let mut required = 0usize;
        // SAFETY: sized records, a buffer of 4 bytes.
        let rc = unsafe {
            (barrier::table().witness_wait.unwrap())(
                &raw const k,
                0,
                &raw mut out,
                small.as_mut_ptr().cast(),
                small.len(),
                &raw mut required,
            )
        };
        assert_eq!((rc, out.state, small), (KILN_ERR_BUFFER_TOO_SMALL, 0xdead, [0; 4]));
        let (rc, state, plan_id, revision, reason) = sfi.witness(0);
        assert_eq!((rc, state, plan_id, revision), (KILN_OK, 2, [0; 16], 0));
        let reason = reason.unwrap();
        assert_eq!(reason.len(), required);
        assert!(reason.contains("install lock"), "{reason}");
        let state = game.on_ready(hash);
        assert!(state.start_blocked, "the start stops before the game's entry point");
        assert_eq!(state.phase, kiln_eml::runner::Phase::Failed);
        let (_, identity, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["state"].as_str(), record["grant"].as_bool()),
            (Some("refused"), Some(false))
        );
        assert_eq!(row(&record, "alpha")["code"], "lock-busy");
        assert_eq!((identity.phase, identity.generation), (0, 0));
        // No grant, no identity for kiln.boot
        published_matches(hash, None);
        drop(other);
    });
}

// A package DLL changed the witnessed input: the recheck says how, the terminal carries it,
// the pass publishes nothing and grants nothing, the record is failed without a BASE origin (carried note c: a start
// without a grant records none), and kiln.log names the DLL packages that loaded; the start goes on.
#[test]
fn a_dll_that_changes_the_witnessed_input_publishes_and_grants_nothing() {
    in_child(
        "a_dll_that_changes_the_witnessed_input_publishes_and_grants_nothing",
        || {
            let game = Game::new("g14b-k7", &[pkg("dll-pack", &content("dll-pack blob")).with_dll()]);
            let (hash, profile) = game.freeze(&game.plan(|_| {}));
            game.early(hash, &profile).unwrap();
            let sfi = Sfi::attach(hash, 1).unwrap();
            let (_, _, plan_id, revision, _) = sfi.witness(2000);
            // the DLL rewrites the resource file with the same bytes
            let resources = game.game().join("enshrouded.kfc_resources");
            let bytes = std::fs::read(&resources).unwrap();
            std::thread::sleep(Duration::from_millis(20));
            std::fs::write(&resources, &bytes).unwrap();
            let (rc, text) = sfi.recheck(plan_id, revision);
            assert_eq!(rc, KILN_OK);
            let text = text.expect("changed: the reason");
            let t = terminal(false, false, Err(&text), &[], &[("dll-pack", "loaded")]);
            assert_eq!(sfi.terminal(&t), KILN_OK);
            let out = game.on_ready(hash);
            assert_eq!(out.phase, kiln_eml::runner::Phase::Failed);
            assert!(!out.start_blocked, "no start stop");
            let (_, identity, record) = sfi.outcome();
            let record = record.unwrap();
            assert_eq!(
                (record["state"].as_str(), record["grant"].as_bool()),
                (Some("failed"), Some(false))
            );
            assert_eq!(record["baseOrigin"], Value::Null);
            assert_eq!(row(&record, "dll-pack")["code"], "startup-input-changed");
            assert!(
                record["warnings"][0]["text"]
                    .as_str()
                    .unwrap()
                    .contains("DLL packages that loaded: dll-pack"),
                "{record}"
            );
            assert_eq!(record["native"]["coherent"], false);
            assert_eq!((identity.phase, identity.generation, identity.size), (0, 0, 160));
            assert!(!game.contents().contains(&"dll-pack blob".to_owned()));
            // No grant, no identity for kiln.boot
            published_matches(hash, None);
        },
    );
}

// The loader: kiln-sfi publishes a terminal not coherent when its witness recheck did not answer within
// 5 s (RECHECK_UNANSWERED), as when kiln-eml no longer answers it (RECHECK_GONE). Read as kiln-sfi reads it: a run that
// loaded a DLL is not confirmed (no publication, no grant); a run that loaded none changed nothing (kiln-eml's own
// check decides, here: unchanged, so the pass publishes). The terminal is taken either way.
#[test]
fn an_unanswered_recheck_from_a_run_that_loaded_a_dll_is_not_confirmed() {
    in_child(
        "an_unanswered_recheck_from_a_run_that_loaded_a_dll_is_not_confirmed",
        || unanswered(true),
    );
}
#[test]
fn an_unanswered_recheck_from_a_run_that_loaded_no_dll_changes_nothing() {
    in_child(
        "an_unanswered_recheck_from_a_run_that_loaded_no_dll_changes_nothing",
        || unanswered(false),
    );
}
fn unanswered(loaded: bool) {
    const UNANSWERED: &str = "the startup witness recheck did not answer within 5 s of the native run's conclusion, so \
        the input it witnessed cannot be confirmed unchanged after the DLLs";
    let game = Game::new(
        "g14b-unanswered",
        &[
            pkg("dll-pack", &content("dll-pack blob")).with_dll(),
            pkg("plain", &content("plain blob")),
        ],
    );
    let (hash, profile) = game.freeze(&game.plan(|_| {}));
    game.early(hash, &profile).unwrap();
    let sfi = Sfi::attach(hash, 1).unwrap();
    let (_, _, _, _, _) = sfi.witness(2000);
    let t = if loaded {
        terminal(false, false, Err(UNANSWERED), &[], &[("dll-pack", "loaded")])
    } else {
        terminal(
            false,
            true,
            Err(UNANSWERED),
            &[("dll-pack", "dll-timed-out", "not finished at the deadline", &[])],
            &[("dll-pack", "timed-out")],
        )
    };
    assert_eq!(sfi.terminal(&t), KILN_OK, "taken");
    let finished = run_barrier(&game, &|_| {});
    let out = &finished.outcome;
    let code = |id: &str| out.packages.iter().find(|r| r.id == id).unwrap().code.clone();
    if loaded {
        assert_eq!(
            (code("dll-pack").as_deref(), code("plain").as_deref()),
            (Some("startup-input-changed"), Some("startup-input-changed"))
        );
        assert!(finished.identity.is_none());
        assert!(!game.contents().contains(&"plain blob".to_owned()));
    } else {
        assert_eq!(
            (code("dll-pack").as_deref(), code("plain")),
            (Some("native-refused"), None),
            "{out:?}"
        );
        assert!(finished.identity.is_some(), "granted");
        assert!(game.contents().contains(&"plain blob".to_owned()));
        assert!(
            !out.native.incoherent.as_deref().unwrap_or("").is_empty(),
            "the record still says what kiln-sfi said"
        );
    }
}

// The EML dialect through the wiring: the plan's EML package runs in the EML pass (the production port builds its
// environment from the plan's root), publishes once, and its row is the pass's (pass "eml"); an unlisted EML drop-in is
// skipped (one pass per start, the plan's).
#[test]
fn the_eml_dialect_runs_the_plans_eml_package_and_no_unlisted_one() {
    in_child("the_eml_dialect_runs_the_plans_eml_package_and_no_unlisted_one", || {
        let game = Game::new("g14b-emld", &[pkg("eml-mod", &barrier::eml_edit(77)).eml()]);
        let stray = game.kiln().join("eml/mods/stray");
        std::fs::create_dir_all(stray.join("src")).unwrap();
        std::fs::write(
            stray.join("mod.json"),
            br#"{"id":"stray","name":"stray","version":"1.0.0","capabilities":["patch"]}"#,
        )
        .unwrap();
        std::fs::write(stray.join("src/mod.lua"), barrier::eml_edit(5)).unwrap();
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        assert_eq!(sfi.terminal(&terminal(true, false, Ok(()), &[], &[])), KILN_OK);
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Done, "{:#?}", out.mods);
        let e = entry(&out, "eml-mod");
        assert_eq!(
            (e.source.as_str(), e.state.as_str()),
            ("shroudforge", "loaded"),
            "{e:?}"
        );
        assert_eq!(entry(&out, "stray").state, "skipped");
        assert_eq!(
            game.extra(0),
            77,
            "the plan's EML package ran, the unlisted one did not"
        );
        let (_, identity, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["dialect"].as_str(), record["state"].as_str()),
            (Some("eml"), Some("committed"))
        );
        assert_eq!(row(&record, "eml-mod")["pass"], "eml");
        assert_eq!(identity.phase, KILN_EML_ARCHIVE_PUBLISHED as u32);
    });
}

// No startup dialect: the unlisted EML contributors are the EML pass's own (the production port's `Unlisted` selection),
// published once by the startup controller and shown as loaded; the record has no rows.
#[test]
fn a_start_without_a_dialect_runs_the_unlisted_eml_mods() {
    in_child("a_start_without_a_dialect_runs_the_unlisted_eml_mods", || {
        let game = Game::new("g14b-nodial", &[]);
        let stray = game.kiln().join("eml/mods/stray");
        std::fs::create_dir_all(stray.join("src")).unwrap();
        std::fs::write(
            stray.join("mod.json"),
            br#"{"id":"stray","name":"stray","version":"1.0.0","capabilities":["patch"]}"#,
        )
        .unwrap();
        std::fs::write(stray.join("src/mod.lua"), barrier::eml_edit(31)).unwrap();
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        assert_eq!(sfi.terminal(&terminal(true, false, Ok(()), &[], &[])), KILN_OK);
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Done, "{:#?}", out.mods);
        assert_eq!(entry(&out, "stray").state, "loaded");
        assert_eq!(game.extra(0), 31);
        let (_, _, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["dialect"].as_str(), record["state"].as_str()),
            (Some("none"), Some("committed"))
        );
        assert_eq!(record["packages"], json!([]));
    });
}

// Carried note a: kiln-sfi could not read its native plan and attached with 0 DLLs; its terminal (not armed) withdraws
// packages that have no DLL record in it: taken, and the rows are native-refused with the terminal's text and chain.
#[test]
fn a_withdrawal_without_a_dll_record_is_taken() {
    in_child("a_withdrawal_without_a_dll_record_is_taken", || {
        let game = Game::new(
            "g14b-noarm",
            &[
                pkg("dll-pack", &content("dll-pack blob")).with_dll(),
                pkg("needs-dll", &content("needs-dll blob")).needs("dll-pack"),
                pkg("plain", &content("plain blob")),
            ],
        );
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        let t = terminal(
            true,
            false,
            Ok(()),
            &[
                (
                    "dll-pack",
                    "native-not-armed",
                    "the plan's native part could not be read",
                    &[],
                ),
                (
                    "needs-dll",
                    "dependency-withdrawn",
                    "depends on withdrawn package dll-pack",
                    &["dll-pack"],
                ),
            ],
            &[],
        );
        assert_eq!(sfi.terminal(&t), KILN_OK);
        let finished = run_barrier(&game, &|_| {});
        let out = &finished.outcome;
        let r = |id: &str| out.packages.iter().find(|r| r.id == id).unwrap().clone();
        assert_eq!(
            (r("dll-pack").code.as_deref(), r("dll-pack").text.as_deref()),
            (Some("native-refused"), Some("the plan's native part could not be read"))
        );
        assert_eq!(
            (r("needs-dll").code.as_deref(), r("needs-dll").chain.clone()),
            (Some("native-refused"), vec!["dll-pack".to_owned()])
        );
        assert_eq!(r("plain").code, None);
        let contents = game.contents();
        assert!(contents.contains(&"plain blob".to_owned()) && !contents.contains(&"dll-pack blob".to_owned()));
    });
}

// A refused terminal (another plan's) is no terminal: the call counts (a later valid one is UNAVAILABLE), and the
// packages with native code are left out as without one (native-terminal-missing), without waiting further.
#[test]
fn a_refused_terminal_is_no_terminal() {
    in_child("a_refused_terminal_is_no_terminal", || {
        let game = Game::new(
            "g14b-badterm",
            &[
                pkg("dll-pack", &content("dll-pack blob")).with_dll(),
                pkg("plain", &content("plain blob")),
            ],
        );
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 1).unwrap();
        let good = terminal(false, false, Ok(()), &[], &[("dll-pack", "loaded")]);
        let other_plan = good.replace(PLAN_ID, "00000000-0000-4000-8000-000000000000");
        assert_eq!(sfi.terminal(&other_plan), KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(sfi.terminal(&good), KILN_ERR_UNAVAILABLE, "the first call counted");
        let finished = run_barrier(&game, &|_| {});
        assert!(finished.waited < Duration::from_millis(500), "{:?}", finished.waited);
        let out = &finished.outcome;
        assert_eq!(
            out.packages
                .iter()
                .find(|r| r.id == "dll-pack")
                .unwrap()
                .code
                .as_deref(),
            Some("native-terminal-missing")
        );
        assert_eq!(out.packages.iter().find(|r| r.id == "plain").unwrap().code, None);
    });
}

// A panic in the startup controller's witness is contained in the early phase: the witness is REFUSED, the start's
// outcome is refused and grants nothing.
#[test]
fn a_panic_in_the_witness_is_a_refused_witness() {
    in_child("a_panic_in_the_witness_is_a_refused_witness", || {
        let game = Game::new("g14b-k14w", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        let kiln = game.kiln();
        let panicking = |_: &camino::Utf8Path,
                         _: &str,
                         _: &std::fs::File,
                         _: &kiln_eml::startup::PlanFacts,
                         _: kiln_eml::startup::LaunchKey|
         -> Result<kiln_eml::startup::Witnessed, kiln_eml::startup::Refused> {
            std::panic::resume_unwind(Box::new("the witness failed"))
        };
        startup_seam::early(&startup_seam::EarlyInputs {
            game: game.game(),
            kiln: &kiln,
            stem: "enshrouded",
            launch_id: LAUNCH_ID,
            plan_sha256: hash,
            build: Some((game.exe_sha256(), "client")),
            profile_json: Some(&profile),
            foreign_loader: None,
            lock: &startup_seam::install_lock,
            witness: &panicking,
        })
        .unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        let (rc, state, _, _, reason) = sfi.witness(0);
        assert_eq!((rc, state), (KILN_OK, 2));
        assert!(reason.unwrap().contains("internal error: the witness failed"));
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Failed);
        let (_, identity, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["state"].as_str(), record["grant"].as_bool()),
            (Some("refused"), Some(false))
        );
        assert_eq!(row(&record, "alpha")["code"], "startup-internal-error");
        assert_eq!(identity.phase, 0);
    });
}

// A panic in the early phase before the witness (here taking the lock) installs no launch: attach is UNAVAILABLE
// (kiln-sfi loads no DLL and has no outcome), and on_ready runs nothing.
#[test]
fn a_panic_in_the_early_phase_installs_no_launch() {
    in_child("a_panic_in_the_early_phase_installs_no_launch", || {
        let game = Game::new("g14b-k14e", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        let panicking = |_: &camino::Utf8Path| -> std::io::Result<std::sync::Arc<std::fs::File>> {
            std::panic::resume_unwind(Box::new("the lock failed"))
        };
        assert!(
            game.early_with(hash, &profile, &panicking, None)
                .unwrap_err()
                .contains("the lock failed")
        );
        assert_eq!(Sfi::attach(hash, 0).err(), Some(KILN_ERR_UNAVAILABLE));
        let out = game.on_ready(hash);
        let e = entry(&out, "alpha");
        assert_eq!(e.state, "skipped", "{e:?}");
        assert!(!game.contents().contains(&"alpha blob".to_owned()));
    });
}

// on_ready: a panic inside the pass is contained by the startup controller: the outcome is failed and grants nothing.
#[test]
fn a_panic_in_the_pass_grants_nothing() {
    in_child("a_panic_in_the_pass_grants_nothing", || {
        let game = Game::new("g14b-k14p", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        assert_eq!(sfi.terminal(&terminal(true, false, Ok(()), &[], &[])), KILN_OK);
        let finished = run_barrier(&game, &|point| {
            if matches!(point, kiln_eml::startup::Point::Admitted) {
                std::panic::resume_unwind(Box::new("the pass failed"));
            }
        });
        assert!(finished.identity.is_none());
        let (_, identity, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["state"].as_str(), record["grant"].as_bool()),
            (Some("failed"), Some(false))
        );
        assert_eq!(row(&record, "alpha")["code"], "startup-internal-error");
        assert_eq!(identity.phase, 0);
    });
}

// Carried note d: a plan that names another BASE origin than the inspector sees is stale: the witness is refused
// (plan-stale), nothing is captured, nothing granted.
#[test]
fn a_plan_naming_another_base_origin_is_stale() {
    in_child("a_plan_naming_another_base_origin_is_stale", || {
        let game = Game::new("g14b-origin", &[pkg("alpha", &content("alpha blob"))]);
        let bytes = game.plan(|p| p["game"]["archiveBaseOrigin"] = json!("rebaselined-unclassified"));
        let (hash, profile) = game.freeze(&bytes);
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 0).unwrap();
        let (_, state, _, _, reason) = sfi.witness(0);
        assert_eq!(state, 2);
        assert!(reason.as_deref().unwrap().contains("base-origin-changed"), "{reason:?}");
        assert!(!game.game().join("enshrouded.kfc.bak").exists());
        game.on_ready(hash);
        let (_, _, record) = sfi.outcome();
        assert_eq!(row(&record.unwrap(), "alpha")["code"], "plan-stale");
    });
}

// A plan kiln-sfi's reader would not take (here: made for another executable) installs no launch: kiln-eml decides
// nothing from it, attach is UNAVAILABLE, and on_ready runs nothing.
#[test]
fn a_plan_the_interpreter_would_not_read_installs_no_launch() {
    in_child("a_plan_the_interpreter_would_not_read_installs_no_launch", || {
        let game = Game::new("g14b-exe", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|p| p["game"]["exe_sha256"] = json!("0".repeat(64))));
        let why = game.early(hash, &profile).unwrap_err();
        assert!(why.contains("plan executable hash or side mismatch"), "{why}");
        assert_eq!(Sfi::attach(hash, 0).err(), Some(KILN_ERR_UNAVAILABLE));
        let out = game.on_ready(hash);
        // discovery set the ShroudForge package aside; nothing of the plan runs, nothing is written
        assert_eq!(entry(&out, "alpha").state, "skipped");
        assert_eq!(out.phase, kiln_eml::runner::Phase::Idle);
        assert!(!game.game().join("enshrouded.kfc.bak").exists());
        assert!(!game.contents().contains(&"alpha blob".to_owned()));
    });
}

// In kiln mode another EML loader that patched the game files in this process refuses the witness (
// fail closed, no stand-aside outcome, ShroudForge's config never read): no DLL may load (REFUSED, with the loader's
// reason), nothing is captured or published, the record is refused with eml-loader-loaded and grants nothing, and the
// start is not stopped.
#[test]
fn another_eml_loader_in_the_process_refuses_the_witness() {
    in_child("another_eml_loader_in_the_process_refuses_the_witness", || {
        let game = Game::new("g14b-loader", &[pkg("alpha", &content("alpha blob"))]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        let why = "an EML loader (dinput8.dll) patched the game files in this process";
        game.early_with(hash, &profile, &startup_seam::install_lock, Some(why.into()))
            .unwrap();
        assert!(!game.game().join("enshrouded.kfc.bak").exists(), "nothing captured");
        let sfi = Sfi::attach(hash, 0).unwrap();
        let (rc, state, plan_id, revision, reason) = sfi.witness(0);
        assert_eq!((rc, state, plan_id, revision), (KILN_OK, 2, [0; 16], 0));
        assert!(reason.as_deref().unwrap().starts_with(why), "{reason:?}");
        assert_eq!(sfi.terminal(&terminal(true, false, Ok(()), &[], &[])), KILN_OK);
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Failed);
        assert!(!out.start_blocked, "no start stop");
        let e = entry(&out, "alpha");
        assert_eq!(
            (e.source.as_str(), e.state.as_str()),
            ("shroudforge", "skipped"),
            "{e:?}"
        );
        assert!(e.reason.as_deref().unwrap().contains("eml-loader-loaded"), "{e:?}");
        let (_, identity, record) = sfi.outcome();
        let record = record.unwrap();
        assert_eq!(
            (record["state"].as_str(), record["grant"].as_bool()),
            (Some("refused"), Some(false))
        );
        assert_eq!(row(&record, "alpha")["code"], "eml-loader-loaded");
        assert_eq!(record["baseOrigin"], Value::Null);
        assert_eq!((identity.phase, identity.generation), (0, 0));
        assert!(!game.contents().contains(&"alpha blob".to_owned()));
    });
}

// The end of the barrier's wait and the decision on a terminal are one step under the provider's lock (no TSan here:
// a stress with real threads instead). A terminal pushed about when the wait ends is either taken (OK) and the pass uses
// it (native.received), or refused (UNAVAILABLE, noted for kiln.log) and the pass ran without it; never one without the
// other. Each round is a start of its own (the seam is the process's), its push timed around the deadline.
#[test]
fn the_end_of_the_wait_and_the_terminal_decision_are_one_step() {
    const NAME: &str = "the_end_of_the_wait_and_the_terminal_decision_are_one_step";
    const ROUND: &str = "KILN_EML_BARRIER_ROUND";
    const ROUNDS: i64 = 16;
    if std::env::var(CASE).as_deref() == Ok(NAME) {
        let round: i64 = std::env::var(ROUND).unwrap().parse().unwrap();
        let game = Game::new(&format!("g14b-race{round}"), &[]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let wait = Duration::from_millis(40);
        let sfi = Sfi::attach(hash, 0).unwrap();
        let deadline = Instant::now() + wait;
        // -2.4 ms .. +2.1 ms around the deadline
        let offset = (round - ROUNDS / 2) * 300;
        let at = if offset >= 0 {
            deadline + Duration::from_micros(offset as u64)
        } else {
            deadline - Duration::from_micros(offset.unsigned_abs())
        };
        let pusher = {
            let sfi = Sfi {
                plan_sha256: sfi.plan_sha256,
                session: sfi.session,
            };
            std::thread::spawn(move || {
                while Instant::now() < at {
                    std::hint::spin_loop();
                }
                sfi.terminal(&terminal(true, false, Ok(()), &[], &[]))
            })
        };
        let cache = game.kiln().join("eml/cache");
        let finished = match startup_seam::barrier(&BarrierPorts {
            cache_dir: &cache,
            unlisted: &[],
            wait,
            observe: &|_| {},
        }) {
            Barriered::Done(finished) => finished,
            _ => panic!("no barrier"),
        };
        let rc = pusher.join().unwrap();
        let late = startup_seam::late_notes()
            .iter()
            .any(|n| n.contains("arrived after kiln-eml's startup wait ended"));
        match rc {
            KILN_OK => assert!(finished.outcome.native.received && !late, "taken but not used"),
            KILN_ERR_UNAVAILABLE => assert!(!finished.outcome.native.received && late, "refused but used"),
            other => panic!("terminal: {other}"),
        }
        println!(
            "race round {round} offset {offset} us: {}",
            if rc == KILN_OK { "taken" } else { "late" }
        );
        return;
    }
    let (mut taken, mut late) = (0, 0);
    for round in 0..ROUNDS {
        let out = test_gate::output(
            std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact", NAME, "--nocapture", "--test-threads=1"])
                .env(CASE, NAME)
                .env(ROUND, round.to_string()),
        )
        .unwrap();
        let stdout = String::from_utf8_lossy(&out.stdout);
        assert!(
            out.status.success() && stdout.contains("1 passed"),
            "round {round}: {stdout}{}",
            String::from_utf8_lossy(&out.stderr)
        );
        taken += stdout.contains(": taken") as u32;
        late += stdout.contains(": late") as u32;
    }
    assert_eq!(taken + late, ROUNDS as u32);
    eprintln!("the end of the wait vs a terminal pushed around it: {taken} taken, {late} late");
}

// Two packages of the plan in one folder under two names (a link; on Windows a junction): kiln-sfi's reader refuses the
// plan (sfi-policy's check_folders, plan-invalid), so kiln-eml's early step does too: no launch, attach UNAVAILABLE, no
// witness, nothing captured or written at on_ready.
#[test]
fn a_plan_with_one_folder_under_two_names_installs_no_launch() {
    in_child("a_plan_with_one_folder_under_two_names_installs_no_launch", || {
        let game = Game::new(
            "g14b-onefolder",
            &[pkg("alpha", &content("alpha blob")), pkg("beta", &content("beta blob"))],
        );
        // each component joined, so cmd sees backslashes only (a "/beta-link" would be a switch to it)
        let link = game.game().join("mods").join("beta-link");
        #[cfg(unix)]
        std::os::unix::fs::symlink(game.game().join("mods/alpha"), &link).unwrap();
        #[cfg(windows)]
        {
            let out = test_gate::output(
                std::process::Command::new("cmd")
                    .args(["/C", "mklink", "/J"])
                    .arg(link.as_std_path())
                    .arg(game.game().join("mods").join("alpha").as_std_path()),
            )
            .unwrap();
            assert!(out.status.success(), "{out:?}");
        }
        let (hash, profile) = game.freeze(&game.plan(|p| p["packages"][1]["root"] = json!("mods/beta-link")));
        let why = game.early(hash, &profile).unwrap_err();
        assert!(
            why.contains("overlapping package roots: alpha and beta are one folder"),
            "{why}"
        );
        assert_eq!(Sfi::attach(hash, 0).err(), Some(KILN_ERR_UNAVAILABLE));
        assert!(!game.game().join("enshrouded.kfc.bak").exists(), "nothing captured");
        let out = game.on_ready(hash);
        assert_eq!(out.phase, kiln_eml::runner::Phase::Idle);
        let contents = game.contents();
        assert!(!contents.contains(&"alpha blob".to_owned()) && !contents.contains(&"beta blob".to_owned()));
        #[cfg(unix)]
        std::fs::remove_file(&link).unwrap();
        #[cfg(windows)]
        std::fs::remove_dir(&link).unwrap(); // the junction only
    });
}

/// The terminals of native-terminal.schema.json's fixtures: the reader kiln-eml's `native_terminal` uses takes every
/// valid one and refuses every invalid one, with the plan facts each names (its planId, the packages it names, its DLL
/// count); and refuses a terminal that names a package the plan does not list, another DLL count, or a DLL that did not
/// load whose package is not withdrawn.
#[test]
fn the_terminal_reader_takes_what_the_schema_takes() {
    let valid: Value =
        serde_json::from_str(include_str!("../../../../schema/sfi/eml-startup/fixtures/valid.json")).unwrap();
    let invalid: Value =
        serde_json::from_str(include_str!("../../../../schema/sfi/eml-startup/fixtures/invalid.json")).unwrap();
    let facts = |t: &Value| {
        let id: String = t["planId"].as_str().unwrap_or("").replace('-', "");
        let mut plan_id = [0u8; 16];
        for (i, b) in plan_id.iter_mut().enumerate() {
            *b = id
                .get(i * 2..i * 2 + 2)
                .and_then(|h| u8::from_str_radix(h, 16).ok())
                .unwrap_or(0);
        }
        let mut listed = BTreeSet::new();
        for w in t["withdrawn"].as_array().into_iter().flatten() {
            listed.extend(w["id"].as_str().map(str::to_owned));
        }
        for d in t["dlls"].as_array().into_iter().flatten() {
            listed.extend(d["modId"].as_str().map(str::to_owned));
        }
        let planned = t["dlls"].as_array().map_or(0, Vec::len) as u32;
        (plan_id, listed, planned)
    };
    let mut taken = 0;
    for (name, t) in valid["native-terminal"].as_object().unwrap() {
        let (plan_id, listed, planned) = facts(t);
        let bytes = serde_json::to_vec(t).unwrap();
        startup_seam::check_native_terminal(&bytes, plan_id, &listed, planned)
            .unwrap_or_else(|e| panic!("{name}: {e}"));
        taken += 1;
        // not listed, another count
        let mut fewer = listed.clone();
        if let Some(first) = listed.iter().next() {
            fewer.remove(first);
            assert!(
                startup_seam::check_native_terminal(&bytes, plan_id, &fewer, planned).is_err(),
                "{name}: unlisted"
            );
        }
        assert!(
            startup_seam::check_native_terminal(&bytes, plan_id, &listed, planned + 1).is_err(),
            "{name}: count"
        );
        assert!(
            startup_seam::check_native_terminal(&bytes, [9; 16], &listed, planned).is_err(),
            "{name}: plan"
        );
    }
    assert!(taken >= 4);
    let mut refused = 0;
    for case in invalid
        .as_array()
        .unwrap()
        .iter()
        .filter(|c| c["schema"] == "native-terminal")
    {
        let t = &case["record"];
        let (plan_id, listed, planned) = facts(t);
        let bytes = serde_json::to_vec(t).unwrap();
        assert!(
            startup_seam::check_native_terminal(&bytes, plan_id, &listed, planned).is_err(),
            "{}: taken",
            case["why"]
        );
        refused += 1;
    }
    assert!(refused >= 10, "{refused}");
    // a DLL that did not load whose package is not withdrawn
    let listed: BTreeSet<String> = ["a".to_owned()].into();
    let t = |outcome: &str, withdrawn: bool| {
        json!({"format": 1, "planId": PLAN_ID, "notArmed": false, "deadlinePassed": true, "coherent": {"ok": true},
            "withdrawn": if withdrawn { json!([{"id": "a", "code": "dll-faulted", "text": "x", "chain": []}]) } else { json!([]) },
            "dlls": [{"statusId": "dll:a:a.dll", "modId": "a", "outcome": outcome}]})
        .to_string()
    };
    for outcome in ["refused", "faulted", "timed-out", "withdrawn"] {
        assert!(
            startup_seam::check_native_terminal(t(outcome, false).as_bytes(), plan_id_bytes(), &listed, 1).is_err(),
            "{outcome}"
        );
        assert!(
            startup_seam::check_native_terminal(t(outcome, true).as_bytes(), plan_id_bytes(), &listed, 1).is_ok(),
            "{outcome}"
        );
    }
    assert!(startup_seam::check_native_terminal(t("loaded", false).as_bytes(), plan_id_bytes(), &listed, 1).is_ok());
}

/// Windows: the early export is in kiln-eml.dll itself (kiln-sfi resolves it with GetModuleHandleW + GetProcAddress),
/// and a copy of the module that has no start answers as the header says. The DLL is next to this test binary
/// (`kiln_eml.dll`, as cargo builds the cdylib) or at `KILN_EML_DLL`.
#[cfg(windows)]
#[test]
fn windows_the_early_export_is_in_kiln_eml_dll() {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn LoadLibraryW(name: *const u16) -> *mut std::ffi::c_void;
        fn GetProcAddress(module: *mut std::ffi::c_void, name: *const u8) -> *mut std::ffi::c_void;
    }
    let path = std::env::var_os("KILN_EML_DLL")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| {
            let exe = std::env::current_exe().unwrap();
            let here = exe.parent().unwrap();
            [here.join("kiln_eml.dll"), here.parent().unwrap().join("kiln_eml.dll")]
                .into_iter()
                .find(|p| p.exists())
                .expect("kiln_eml.dll next to the test binary (or KILN_EML_DLL)")
        });
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
    // SAFETY: a NUL-terminated path.
    let module = unsafe { LoadLibraryW(wide.as_ptr()) };
    assert!(!module.is_null(), "{}", path.display());
    // SAFETY: the export name ends in its NUL.
    let address = unsafe { GetProcAddress(module, b"kiln_eml_startup_service\0".as_ptr()) };
    assert!(!address.is_null(), "no kiln_eml_startup_service export");
    // SAFETY: kiln_eml_startup.h's getter signature.
    let getter: unsafe extern "C" fn(u32) -> *const kiln_abi::services::kiln_eml_startup::KilnEmlStartupApiV1 =
        unsafe { std::mem::transmute(address) };
    // SAFETY: the getter's contract.
    let (t0, t1, t2) = unsafe { (getter(0), getter(1), getter(2)) };
    assert!(!t1.is_null() && t0 == t1 && t2.is_null());
    // SAFETY: the module's process-lifetime table.
    let table = unsafe { &*t1 };
    // kiln_eml_startup.h v1: 56 bytes since published_identity was appended (48 -> 56), as
    // the_early_export_serves_one_table_and_grants_nothing checks on the linked copy
    assert_eq!((table.header.size, table.header.version), (56, 1));
    assert_eq!(table.header.size as usize, std::mem::size_of_val(table));
    let k = barrier::key(LAUNCH_ID, [1; 32], 0);
    let mut session = 0u64;
    // SAFETY: a sized key, a live out-pointer.
    let rc = unsafe { (table.attach.unwrap())(&raw const k, 0, &raw mut session) };
    assert_eq!((rc, session), (KILN_ERR_UNAVAILABLE, 0), "that copy has no start");
    // the appended member answers too: no launch installed in that copy, so no identity, and the record is not written
    let mut identity = barrier::blank_identity();
    // SAFETY: a sized key, a 160-byte record that says so.
    let rc = unsafe { (table.published_identity.expect("published_identity"))(&raw const k, &raw mut identity) };
    assert_eq!((rc, identity.generation), (KILN_ERR_UNAVAILABLE, 0xdead), "that copy has no identity");
    for name in [
        &b"kiln_plugin_early\0"[..],
        b"kiln_plugin_main\0",
        b"kiln_eml_service\0",
        b"kiln_eml_archive_service\0",
    ] {
        // SAFETY: as above.
        assert!(
            !unsafe { GetProcAddress(module, name.as_ptr()) }.is_null(),
            "{}",
            String::from_utf8_lossy(name)
        );
    }
}

/// Windows (the loader): kiln-sfi's deadline thread calls `witness_recheck` and `native_terminal` while a package's
/// DllMain may hold the loader lock, and kiln-eml's barrier waits meanwhile. Here a thread holds the loader lock in its
/// own thread-attach notification (a TLS callback of this binary, as a DllMain would): the control shows the lock is
/// held (a new thread cannot start), the recheck and the terminal answer at once, and the barrier, already running on a
/// thread of its own, ends its wait with that terminal and finishes its pass, publication and grant while the lock is
/// still held, natively and under Wine (whose loader takes the lock for every LoadLibrary and GetProcAddress: the early
/// phase asks the system what the pass would ask it once per process, the first Lua state's C runtime calls).
#[cfg(windows)]
#[test]
fn windows_the_seam_answers_while_the_loader_lock_is_held() {
    in_child("windows_the_seam_answers_while_the_loader_lock_is_held", || {
        use loader_lock::{HOLDING, RELEASE, arm};
        use std::sync::atomic::Ordering;
        // the package's Lua also takes paths a pass commonly takes: a caught error (Lua's longjmp unwinds) and the C
        // runtime's number formatting and parsing (ShroudForge's sandbox has no `os`)
        let lua = format!(
            "local ok = pcall(function() error(\"caught\") end)\nassert(not ok)\nlocal s = string.format(\"%.3f %g %d\", 1.5, 2.25, 7) .. tostring(0.5) .. tonumber(\"1e3\")\nassert(s == \"1.500 2.25 70.51000.0\", s)\n{}",
            content("dll-pack blob")
        );
        let game = Game::new("g14b-ldr", &[pkg("dll-pack", &lua).with_dll()]);
        let (hash, profile) = game.freeze(&game.plan(|_| {}));
        game.early(hash, &profile).unwrap();
        let sfi = Sfi::attach(hash, 1).unwrap();
        let (_, _, plan_id, revision, _) = sfi.witness(2000);
        // the barrier's thread exists before the lock is held (on_ready's boot thread does in Kiln)
        let (go_tx, go_rx) = std::sync::mpsc::channel::<()>();
        let (ready_tx, ready_rx) = std::sync::mpsc::channel::<()>();
        let (done_tx, done_rx) = std::sync::mpsc::channel::<(Duration, Duration, Option<String>)>();
        let cache = game.kiln().join("eml/cache");
        let barrier_thread = std::thread::spawn(move || {
            // running (its own thread-attach notification done) before the lock is held
            ready_tx.send(()).unwrap();
            go_rx.recv().unwrap();
            let began = Instant::now();
            let state = match startup_seam::barrier(&BarrierPorts {
                cache_dir: &cache,
                unlisted: &[],
                wait: startup_seam::TERMINAL_WAIT,
                observe: &|_| {},
            }) {
                Barriered::Done(f) => (f.waited, began.elapsed(), Some(f.outcome.state.word().to_owned())),
                _ => (Duration::MAX, began.elapsed(), None),
            };
            done_tx.send(state).unwrap();
        });
        ready_rx.recv().unwrap();
        // a thread whose attach notification holds the loader lock until released
        arm();
        let holder = std::thread::spawn(|| {});
        let started = Instant::now();
        while !HOLDING.load(Ordering::SeqCst) {
            assert!(
                started.elapsed() < Duration::from_secs(20),
                "the TLS callback never ran"
            );
            std::thread::sleep(Duration::from_millis(1));
        }
        // control: no new thread can start while it is held
        let probe_ran = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let probe = {
            let ran = probe_ran.clone();
            std::thread::spawn(move || ran.store(true, Ordering::SeqCst))
        };
        std::thread::sleep(Duration::from_millis(500));
        let control = !probe_ran.load(Ordering::SeqCst);
        // the deadline thread's calls, under the loader lock
        let t = Instant::now();
        let recheck = sfi.recheck(plan_id, revision);
        let recheck_took = t.elapsed();
        let t = Instant::now();
        let pushed = sfi.terminal(&terminal(false, false, Ok(()), &[], &[("dll-pack", "loaded")]));
        let terminal_took = t.elapsed();
        go_tx.send(()).unwrap();
        let under_lock = done_rx.recv_timeout(Duration::from_secs(10)).ok();
        RELEASE.store(true, Ordering::SeqCst);
        holder.join().unwrap();
        probe.join().unwrap();
        let (waited, total, state) = under_lock.clone().unwrap_or_else(|| done_rx.recv().unwrap());
        barrier_thread.join().unwrap();
        eprintln!(
            "loader lock: control={control} recheck={recheck_took:?} terminal={terminal_took:?} barrier wait={waited:?} \
             barrier total={total:?} finished under the lock={} state={state:?}",
            under_lock.is_some()
        );
        assert!(control, "the loader lock was not held: the probe thread started");
        assert_eq!(recheck, (KILN_OK, None));
        assert!(recheck_took < Duration::from_secs(2), "{recheck_took:?}");
        assert_eq!(pushed, KILN_OK);
        assert!(terminal_took < Duration::from_secs(2), "{terminal_took:?}");
        assert!(
            waited < Duration::from_secs(2),
            "the barrier's wait ended with the terminal: {waited:?}"
        );
        assert!(
            under_lock.is_some(),
            "the barrier's pass waited for the loader lock: {total:?}"
        );
        assert_eq!(state.as_deref(), Some("committed"));
        assert!(
            game.contents().contains(&"dll-pack blob".to_owned()),
            "the package's Lua ran to its end: {:?}",
            sfi.outcome().2
        );
    });
}

/// A thread-attach TLS callback of this test binary that holds the loader lock for one armed thread, as a DllMain that
/// does not return does.
#[cfg(windows)]
mod loader_lock {
    use std::sync::atomic::{AtomicBool, Ordering};
    pub static ARMED: AtomicBool = AtomicBool::new(false);
    pub static HOLDING: AtomicBool = AtomicBool::new(false);
    pub static RELEASE: AtomicBool = AtomicBool::new(false);
    pub fn arm() {
        // keep the callback referenced
        // SAFETY: a static's address.
        let _ = unsafe { std::ptr::read_volatile(&raw const CALLBACK) };
        ARMED.store(true, Ordering::SeqCst);
    }
    #[used]
    #[unsafe(link_section = ".CRT$XLY")]
    static CALLBACK: unsafe extern "system" fn(*mut std::ffi::c_void, u32, *mut std::ffi::c_void) = on_thread;
    unsafe extern "system" fn on_thread(_: *mut std::ffi::c_void, reason: u32, _: *mut std::ffi::c_void) {
        const DLL_THREAD_ATTACH: u32 = 2;
        if reason == DLL_THREAD_ATTACH && ARMED.swap(false, Ordering::SeqCst) {
            HOLDING.store(true, Ordering::SeqCst);
            let started = std::time::Instant::now();
            while !RELEASE.load(Ordering::SeqCst) && started.elapsed() < std::time::Duration::from_secs(30) {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
            HOLDING.store(false, Ordering::SeqCst);
        }
    }
}
