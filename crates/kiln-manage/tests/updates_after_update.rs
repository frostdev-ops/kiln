//! netjunk's report (Discord #suggestions, 2026-11-26): the launcher offered "Update 0.2.10" for Kiln Auto Loot with
//! 0.2.10 installed, while 0.2.12 was out, and the offer stayed after he updated by drag and drop and by Add mod.
//!
//! Replayed here with the mod's real release list (tests/fixture/updates-github/kiln-auto-loot.releases.json, from
//! `gh release list --repo frostdev-ops/kiln-auto-loot`; ids and sizes made up) against a fake GitHub: a check while
//! 0.2.9 is installed offers 0.2.10; the player imports the 0.2.10 zip (what a drop, Add mod and the Library's import
//! all do: `library::import`); from then on no check offers 0.2.10, and the next check that may ask GitHub asks again
//! although its last answer is minutes old, so it names 0.2.12, the newest the repository has.
#![cfg(feature = "zip")]

use kiln_manage::library;
use kiln_manage::net::fake::{FakeTransport, Reply};
use kiln_manage::net::Method;
use kiln_manage::paths::DataDirs;
use kiln_manage::task::NoProgress;
use kiln_manage::updates::coordinator::{self, CheckOptions, CheckReport};
use kiln_manage::updates::github::{self, GithubProvider};
use kiln_manage::updates::{ModUpdateStatus, UpdateState};
use serde_json::Value;
use std::io::Write;
use std::path::{Path, PathBuf};

const NOW: i64 = 1_800_000_000;
const MINUTE: i64 = 60;
const REPO: &str = "frostdev-ops/kiln-auto-loot";
const LIST: &str = "https://api.github.com/repos/frostdev-ops/kiln-auto-loot/releases?per_page=100&page=1";

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn tmp(tag: &str) -> (Tmp, DataDirs) {
    let p = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("updates-after-update-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    let dirs = DataDirs::at(p.join("data"));
    (Tmp(p), dirs)
}

/// kiln-auto-loot-<version>.zip as the release workflow packs it: one folder with the manifest (and its `updates`).
fn release_zip(dir: &Path, version: &str) -> PathBuf {
    let z = dir.join(format!("kiln-auto-loot-{version}.zip"));
    let mut w = zip::ZipWriter::new(std::fs::File::create(&z).unwrap());
    let o = zip::write::SimpleFileOptions::default();
    let manifest = format!(r#"{{"id": "kiln-auto-loot", "name": "Kiln Auto Loot", "version": "{version}", "apiVersion": "0.2.15", "side": "both",
        "entryAssembly": "KilnAutoLoot.dll", "updates": {{"provider": "github", "repository": "{REPO}", "asset": "kiln-auto-loot-{{version}}.zip"}}}}"#);
    for (name, data) in [("kiln-auto-loot/kiln.mod.json", manifest.into_bytes()), ("kiln-auto-loot/KilnAutoLoot.dll", format!("dll {version}").into_bytes())] {
        w.start_file(name, o).unwrap();
        w.write_all(&data).unwrap();
    }
    w.finish().unwrap();
    z
}

/// The repository's release list as GitHub answers it, with the releases published up to `newest` only. The asset of
/// each zip given in `zips` lists that zip's digest (GitHub states one per asset).
fn listing(newest: &str, zips: &[(&str, &Path)]) -> Vec<u8> {
    let all: Value = serde_json::from_slice(&std::fs::read(Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixture/updates-github/kiln-auto-loot.releases.json")).unwrap()).unwrap();
    let mut seen = false;
    let mut out = Vec::new();
    for mut r in all.as_array().unwrap().iter().cloned() {
        seen |= r["tag_name"] == format!("v{newest}");
        if !seen {
            continue;
        }
        for (v, zip) in zips {
            if r["tag_name"] == format!("v{v}") {
                let (sha, size) = kiln_manage::hash::sha256_file(zip).unwrap();
                r["assets"][0]["digest"] = Value::String(format!("sha256:{sha}"));
                r["assets"][0]["size"] = Value::from(size);
            }
        }
        out.push(r);
    }
    assert!(seen, "no v{newest} in the fixture");
    serde_json::to_vec(&Value::Array(out)).unwrap()
}

fn check(dirs: &DataDirs, t: &FakeTransport, opts: CheckOptions) -> CheckReport {
    let gh = GithubProvider::new(dirs, t, &github::user_agent());
    let r = coordinator::check(dirs, &[&gh], &opts, &mut NoProgress).unwrap();
    for s in &r.mods {
        offer_is_newer(s);
    }
    r
}

/// The rule the report broke: an offer names a version above the installed one.
fn offer_is_newer(s: &ModUpdateStatus) {
    if s.state == UpdateState::Available {
        let (have, offered) = (s.installed_version.as_str(), s.candidate_version.as_deref().unwrap_or(""));
        assert_eq!(kiln_manage::bundle::compare_versions(have, offered), std::cmp::Ordering::Less, "offered {offered} with {have} installed: {s:?}");
    }
}

fn auto_loot(r: &CheckReport) -> &ModUpdateStatus {
    let s = r.mods.iter().find(|m| m.library_ref.id == "kiln-auto-loot").expect("Auto Loot is checked");
    assert_eq!(s.installed_version, s.library_ref.version);
    s
}

fn manual(now: i64) -> CheckOptions {
    CheckOptions { offline: false, refresh: false, automatic: false, now, api_version: "0.2.18".into() }
}

#[test]
fn after_an_update_the_offer_clears_and_the_next_check_names_the_newest_release() {
    let (t, dirs) = tmp("auto-loot");
    let (z9, z10) = (release_zip(&t.0, "0.2.9"), release_zip(&t.0, "0.2.10"));
    let gh = FakeTransport::new();

    // 0.2.9 installed; the repository's newest is 0.2.10 (2026-11-25, before 0.2.11 was out)
    library::import(&dirs, std::slice::from_ref(&z9), &mut NoProgress).unwrap();
    gh.reply(Method::Get, LIST, Reply::bytes(200, &listing("0.2.10", &[("0.2.9", &z9), ("0.2.10", &z10)])));
    let s = auto_loot(&check(&dirs, &gh, manual(NOW))).clone();
    assert_eq!((s.state, s.installed_version.as_str(), s.candidate_version.as_deref(), s.stale), (UpdateState::Available, "0.2.9", Some("0.2.10"), false));
    assert_eq!(gh.requests().len(), 1);

    // the player downloads 0.2.10 and drops it on the launcher (Add mod and the Library's import are the same import)
    let imported = library::import(&dirs, std::slice::from_ref(&z10), &mut NoProgress).unwrap();
    assert_eq!(imported.imported.iter().map(|r| r.version.as_str()).collect::<Vec<_>>(), ["0.2.10"]);

    // what the launcher shows next without asking anyone (its first read after a start, and its refresh after a
    // Library change): the remembered answer compared with what is installed now, never "Update 0.2.10"
    let s = auto_loot(&check(&dirs, &gh, CheckOptions { offline: true, ..manual(NOW + 10 * MINUTE) })).clone();
    assert_eq!((s.state, s.installed_version.as_str(), s.candidate_version.as_deref()), (UpdateState::Current, "0.2.10", Some("0.2.10")));
    assert_eq!(gh.requests().len(), 1, "an offline check asks nobody");

    // 0.2.11 and 0.2.12 are out. "Check for updates" a few minutes after the last answer: the installed copy changed
    // since that answer, so it is not reused; GitHub is asked again and the offer is the newest release
    gh.reply(Method::Get, LIST, Reply::bytes(200, &listing("0.2.12", &[("0.2.9", &z9), ("0.2.10", &z10)])));
    let s = auto_loot(&check(&dirs, &gh, manual(NOW + 12 * MINUTE))).clone();
    assert_eq!(gh.requests().len(), 2, "the check after an update asks GitHub again");
    assert_eq!((s.state, s.installed_version.as_str(), s.candidate_version.as_deref(), s.stale), (UpdateState::Available, "0.2.10", Some("0.2.12"), false));
    assert_eq!(s.page_url.as_deref(), Some("https://github.com/frostdev-ops/kiln-auto-loot/releases/tag/v0.2.12"));

    // nothing changed since: the next check within the half hour answers from memory again, as before
    let s = auto_loot(&check(&dirs, &gh, manual(NOW + 14 * MINUTE))).clone();
    assert_eq!((s.state, s.candidate_version.as_deref()), (UpdateState::Available, Some("0.2.12")));
    assert_eq!(gh.requests().len(), 2, "no change, no new request within the half hour");
    assert!(gh.unexpected().is_empty() && gh.unused().is_empty(), "{:?} {:?}", gh.unexpected(), gh.unused());
}

/// The check after an update meets GitHub's request limit (the answer before the update used the hour's last request):
/// it asks nobody, and it does not forget that another copy is installed. The first check after the limit asks again,
/// although the last answer is still within the half hour, and names the newest release. (Before: the held check wrote
/// the new copy down as compared, and every check for the next half hour said "up to date" from the old answer.)
#[test]
fn a_check_held_by_the_request_limit_still_asks_after_it() {
    let (t, dirs) = tmp("limit");
    let (z9, z10) = (release_zip(&t.0, "0.2.9"), release_zip(&t.0, "0.2.10"));
    let gh = FakeTransport::new();
    library::import(&dirs, std::slice::from_ref(&z9), &mut NoProgress).unwrap();
    gh.reply(Method::Get, LIST, Reply::bytes(200, &listing("0.2.10", &[("0.2.9", &z9), ("0.2.10", &z10)]))
        .with_header("x-ratelimit-remaining", "0").with_header("x-ratelimit-reset", &(NOW + 5 * MINUTE).to_string()));
    let s = auto_loot(&check(&dirs, &gh, manual(NOW))).clone();
    assert_eq!((s.state, s.candidate_version.as_deref()), (UpdateState::Available, Some("0.2.10")));
    assert_eq!(gh.requests().len(), 1);

    library::import(&dirs, std::slice::from_ref(&z10), &mut NoProgress).unwrap();
    // within GitHub's limit: nobody is asked, whatever changed
    let s = auto_loot(&check(&dirs, &gh, manual(NOW + 2 * MINUTE))).clone();
    assert_eq!(gh.requests().len(), 1, "nothing is asked under the request limit");
    assert_ne!((s.state, s.candidate_version.as_deref()), (UpdateState::Available, Some("0.2.10")));

    // the limit is over; the last answer is 8 minutes old, but it was never compared with 0.2.10 by an answer that
    // could have named something newer: asked again
    gh.reply(Method::Get, LIST, Reply::bytes(200, &listing("0.2.12", &[("0.2.9", &z9), ("0.2.10", &z10)])));
    let s = auto_loot(&check(&dirs, &gh, manual(NOW + 8 * MINUTE))).clone();
    assert_eq!(gh.requests().len(), 2, "the first check after the limit asks again");
    assert_eq!((s.state, s.installed_version.as_str(), s.candidate_version.as_deref()), (UpdateState::Available, "0.2.10", Some("0.2.12")));
    // and only once
    let _ = auto_loot(&check(&dirs, &gh, manual(NOW + 9 * MINUTE)));
    assert_eq!(gh.requests().len(), 2, "no change, no new request within the half hour");
    assert!(gh.unexpected().is_empty() && gh.unused().is_empty(), "{:?} {:?}", gh.unexpected(), gh.unused());
}

/// The extra request is only for an answer that offers the installed copy nothing: an update to a version below the
/// newest the answer names (0.2.9 to 0.2.10 while it names 0.2.12), a roll back, or many mods updated at once by a
/// modpack ask nobody within the half hour, as before. The notice names what the answer names.
#[test]
fn an_update_the_answer_still_offers_more_than_asks_nobody() {
    let (t, dirs) = tmp("partial");
    let (z9, z10) = (release_zip(&t.0, "0.2.9"), release_zip(&t.0, "0.2.10"));
    let gh = FakeTransport::new();
    library::import(&dirs, std::slice::from_ref(&z9), &mut NoProgress).unwrap();
    gh.reply(Method::Get, LIST, Reply::bytes(200, &listing("0.2.12", &[("0.2.9", &z9), ("0.2.10", &z10)])));
    let s = auto_loot(&check(&dirs, &gh, manual(NOW))).clone();
    assert_eq!((s.state, s.candidate_version.as_deref()), (UpdateState::Available, Some("0.2.12")));
    library::import(&dirs, std::slice::from_ref(&z10), &mut NoProgress).unwrap();
    for minutes in [5, 10] {
        let s = auto_loot(&check(&dirs, &gh, manual(NOW + minutes * MINUTE))).clone();
        assert_eq!((s.state, s.installed_version.as_str(), s.candidate_version.as_deref()), (UpdateState::Available, "0.2.10", Some("0.2.12")));
        // the daily check too
        let s = auto_loot(&check(&dirs, &gh, CheckOptions { automatic: true, ..manual(NOW + minutes * MINUTE + 1) })).clone();
        assert_eq!(s.candidate_version.as_deref(), Some("0.2.12"));
    }
    assert_eq!(gh.requests().len(), 1, "no request beyond the first");
    assert!(gh.unexpected().is_empty() && gh.unused().is_empty(), "{:?} {:?}", gh.unexpected(), gh.unused());
}
