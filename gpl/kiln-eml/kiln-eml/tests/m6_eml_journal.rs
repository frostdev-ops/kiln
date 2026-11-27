// SPDX-License-Identifier: GPL-3.0-or-later
//! The EML dialect's change journal (`kiln/eml/ownership/<stem>.change-journal.json`, format 2, sf_startup/journal.rs,
//! runner.rs): an EML start outside kiln mode that publishes writes per mod of the run the resources its entrypoint
//! wrote (with the member paths, at their published places), created and the contents it created, bound to the
//! receipt it published (`generation` = `receiptSha256`, the generation kiln-eml reports in eml.json); an unchanged start
//! keeps it byte for byte and reports the same generation; a failed mod's writes are never in it (the retry's are); a
//! start without mods restores the game data and removes it.
#[path = "support/m6_fixture/mod.rs"]
mod fixture;

use fixture::archive::{ArchiveFixture, RESOURCE_GUID, STEM};
use kiln_eml::{
    discover::{self, Candidate},
    runner::{self, Outcome},
};
use serde_json::Value;
use sha2::{Digest as _, Sha256};
use std::{fs, path::PathBuf};

const GET: &str = "local G = \"01234567-89ab-cdef-0123-456789abcdef\"\nlocal function res(part) return game.assets.get_resource(G, \"m6.Root\", part) end\n";

fn journal_path(f: &ArchiveFixture) -> PathBuf {
    f.game.join(format!("kiln/eml/ownership/{STEM}.change-journal.json")).into_std_path_buf()
}
/// The journal, parsed, its field order checked (and saved below `EML_JOURNAL_OUT` as `<name>.json` for the schema).
fn journal(f: &ArchiveFixture, name: &str) -> Value {
    let bytes = fs::read(journal_path(f)).unwrap_or_else(|e| panic!("{name}: no change journal ({e})"));
    if let Some(dir) = std::env::var_os("EML_JOURNAL_OUT") {
        let dir = PathBuf::from(dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(format!("{name}.json")), &bytes).unwrap();
    }
    let text = String::from_utf8(bytes).unwrap();
    let mut at = 0;
    for field in ["format", "stem", "generation", "receiptSha256", "dialect", "attempt", "truncated", "packages", "unattributed"] {
        at += text[at..].find(&format!("\"{field}\":")).unwrap_or_else(|| panic!("{name}: {field} out of order"));
    }
    serde_json::from_str(&text).unwrap()
}
/// `run_packages` as a player's start runs them: patch only (an export run never skips the patch, so it never finds the
/// game data unchanged).
fn run(f: &ArchiveFixture, ids: &[&str]) -> Outcome {
    let cfg = runner::RunConfig { export: false, export_dir: None, ..f.config() };
    let candidates: Vec<_> = ids
        .iter()
        .map(|id| Candidate { path: f.root.0.join("packages").join(id), source: "m6-synthetic".into(), enabled: true })
        .collect();
    let (mods, entries) = discover::resolve(&candidates);
    runner::run(&cfg, mods, entries)
}
fn receipt_sha256(f: &ArchiveFixture) -> String {
    format!("{:x}", Sha256::digest(fs::read(f.receipt()).unwrap()))
}
fn type_hash() -> String {
    format!("{:08x}", kfc::hash::fnv("m6.Root"))
}
fn package<'a>(j: &'a Value, id: &str) -> &'a Value {
    j["packages"].as_array().unwrap().iter().find(|p| p["id"] == id).unwrap_or_else(|| panic!("no package {id}: {j}"))
}
/// The changed row of `part` in `p`, with its paths.
fn paths(p: &Value, part: u32) -> Vec<String> {
    let row = p["changed"]
        .as_array()
        .unwrap()
        .iter()
        .find(|c| c["guid"] == RESOURCE_GUID && c["typeHash"] == type_hash() && c["part"] == part)
        .unwrap_or_else(|| panic!("{} did not write part {part}: {p}", p["id"]));
    row["paths"].as_array().unwrap().iter().map(|v| v.as_str().unwrap().to_owned()).collect()
}

// Two mods write one resource and another, one through a list element's field, one a whole static array element and a
// content: each is named with the member paths it wrote, in the order they ran, bound to the published receipt.
#[test]
fn an_eml_start_names_each_mod_and_the_members_it_wrote() {
    let f = ArchiveFixture::new("eml-journal");
    f.package("alpha", &format!("{GET}local r = res(0)\nr.data.child.base = 99\nr.data.children[2].extra = 5\n"), &["patch"]);
    f.package(
        "bravo",
        &format!("{GET}res(0).data.fixed[2] = 9\nres(3).data.child.extra = 1\ngame.assets.create_content(buffer.wrap(\"bravo content\"))\n"),
        &["patch"],
    );
    f.package("quiet", &format!("{GET}local _ = res(9).data.child.base\n"), &["patch"]);
    let out = run(&f, &["alpha", "bravo", "quiet"]);
    assert_eq!(out.mods.iter().filter(|m| m.state == "loaded").count(), 3, "{:?}", out.mods);
    let generation = receipt_sha256(&f);
    assert_eq!(out.generation.as_deref(), Some(generation.as_str()), "eml.json names the published receipt's generation");
    let j = journal(&f, "eml_two_mods");
    assert_eq!(j["format"], 2);
    assert_eq!(j["dialect"], "eml");
    assert_eq!(j["stem"], STEM);
    assert_eq!(j["attempt"], "first");
    assert_eq!(j["generation"], generation.as_str());
    assert_eq!(j["receiptSha256"], generation.as_str());
    assert_eq!(j["truncated"], false);
    assert!(j.get("notApplied").is_none() && j.get("writer").is_none(), "a run where every mod ran writes the record as 0.2.0-dev.31.18 did: {j}");
    let order: Vec<&str> = j["packages"].as_array().unwrap().iter().map(|p| p["id"].as_str().unwrap()).collect();
    assert_eq!(order.len(), 3, "every mod that ran, in run order: {order:?}");
    let alpha = package(&j, "alpha");
    assert_eq!(alpha["kind"], "eml");
    assert_eq!(alpha["version"], "1.0.0");
    assert!(alpha["contentSha256"].is_null() && alpha["settingsSha256"].is_null(), "no launch plan froze them");
    assert_eq!(paths(alpha, 0), ["child.base", "children[1].extra"], "a field and a list element's field (0-based)");
    assert_eq!(alpha["changed"].as_array().unwrap().len(), 1, "a resource only read is not listed");
    let bravo = package(&j, "bravo");
    assert_eq!(paths(bravo, 0), ["fixed[]"], "a plain element set writes the list's members");
    assert_eq!(paths(bravo, 3), ["child.extra"]);
    assert_eq!(bravo["contents"].as_array().unwrap().len(), 1);
    let quiet = package(&j, "quiet");
    assert!(quiet["changed"].as_array().unwrap().is_empty() && quiet["created"].as_array().unwrap().is_empty());
    assert!(j["unattributed"]["changed"].as_array().unwrap().is_empty());

    // unchanged: no Lua runs, the journal stays byte for byte and the same generation is reported
    let before = fs::read(journal_path(&f)).unwrap();
    let again = run(&f, &["alpha", "bravo", "quiet"]);
    assert!(again.cached, "an unchanged start runs nothing");
    assert_eq!(again.generation.as_deref(), Some(generation.as_str()));
    assert_eq!(fs::read(journal_path(&f)).unwrap(), before, "kept byte for byte");

    // no mods: the game data goes back to vanilla and the journal with it
    let idle = run(&f, &[]);
    assert!(idle.restored);
    assert!(idle.generation.is_none(), "nothing of kiln-eml's is live");
    assert!(!journal_path(&f).exists(), "the restore removed the journal");
}

// A whole resource replaced is "", a mod that fails is dropped and the run repeats: only the committed run's writes are in
// the journal, which says it was a retry.
#[test]
fn a_failed_mods_writes_are_not_in_the_journal() {
    let f = ArchiveFixture::new("eml-journal-retry");
    f.package("whole", &format!("{GET}local r = res(0)\nr.data = r.data\n"), &["patch"]);
    f.package("broken", &format!("{GET}res(3).data.child.base = 7\nerror(\"broken on purpose\")\n"), &["patch"]);
    let out = run(&f, &["whole", "broken"]);
    assert_eq!(out.mods.iter().find(|m| m.id.as_deref() == Some("broken")).unwrap().state, "failed");
    let j = journal(&f, "eml_retry");
    assert_eq!(j["attempt"], "retry");
    let ids: Vec<&str> = j["packages"].as_array().unwrap().iter().map(|p| p["id"].as_str().unwrap()).collect();
    assert_eq!(ids, ["whole"]);
    assert_eq!(paths(package(&j, "whole"), 0), [""], "the data replaced: the whole resource");
    // 0.2.0-dev.32: the mod the run went on without is listed as not applied, with its error (effect replay reads it)
    let not = j["notApplied"].as_array().unwrap_or_else(|| panic!("no notApplied: {j}"));
    assert_eq!(not.len(), 1, "{j}");
    assert_eq!((&not[0]["id"], &not[0]["kind"], &not[0]["contentSha256"]), (&serde_json::json!("broken"), &serde_json::json!("eml"), &Value::Null));
    assert!(not[0]["reason"].as_str().unwrap().contains("broken on purpose"), "{j}");
    assert_eq!(out.generation.as_deref(), Some(receipt_sha256(&f).as_str()));
}

// Review (mod identity): a mod's entry module is that mod's code. EML runs its mods in its mod table's order (a hash map:
// any order); a mod that requires another's entry module (`require("mods.alpha")`) before that mod's turn runs it then,
// and alpha's own turn finds it loaded and runs nothing. Its writes are alpha's all the same, never the requiring mod's.
// Three passes with four such mods each: whatever the order, alpha is named on its write and none of them is.
#[test]
fn an_entry_module_another_mod_requires_first_writes_as_its_own_mod() {
    for pass in 0..3 {
        let f = ArchiveFixture::new(&format!("eml-journal-require-{pass}"));
        f.package("alpha", &format!("{GET}res(0).data.child.base = 41\nreturn {{ ready = true }}\n"), &["patch"]);
        let mut ids = vec!["alpha".to_string()];
        for n in 1..=4 {
            let id = format!("user{n}");
            f.package(&id, &format!("{GET}assert(require(\"mods.alpha\").ready)\nres(3).data.child.extra = {n}\n"), &["patch"]);
            ids.push(id);
        }
        let ids: Vec<&str> = ids.iter().map(String::as_str).collect();
        let out = run(&f, &ids);
        assert_eq!(out.mods.iter().filter(|m| m.state == "loaded").count(), 5, "{:?}", out.mods);
        let j = journal(&f, &format!("eml_require_{pass}"));
        assert_eq!(paths(package(&j, "alpha"), 0), ["child.base"], "pass {pass}: alpha's own write: {j}");
        for n in 1..=4 {
            let user = package(&j, &format!("user{n}"));
            assert_eq!(paths(user, 3), ["child.extra"], "pass {pass}");
            assert_eq!(user["changed"].as_array().unwrap().len(), 1, "pass {pass}: user{n} is not named on alpha's write: {j}");
        }
        let ids: Vec<&str> = j["packages"].as_array().unwrap().iter().map(|p| p["id"].as_str().unwrap()).collect();
        assert_eq!(ids.len(), 5, "each mod once: {ids:?}");
    }
}

// Review (a resource's data replaced): a mod that assigns a copy of a resource's data back (`r.data = r.data`, or plain
// tables built from it) keeps what the mods before it wrote there, so they stay named on the members they wrote; the
// copying mod is named on the whole resource. Three passes with four copying mods each: whatever the order, alpha is
// named on its field.
#[test]
fn a_resource_replaced_by_a_copy_keeps_the_mods_that_wrote_it_before() {
    for pass in 0..3 {
        let f = ArchiveFixture::new(&format!("eml-journal-copy-{pass}"));
        f.package("alpha", &format!("{GET}res(0).data.child.base = 41\n"), &["patch"]);
        let mut ids = vec!["alpha".to_string()];
        for n in 1..=4 {
            let id = format!("copy{n}");
            f.package(&id, &format!("{GET}local r = res(0)\nr.data = r.data\n"), &["patch"]);
            ids.push(id);
        }
        let ids: Vec<&str> = ids.iter().map(String::as_str).collect();
        let out = run(&f, &ids);
        assert_eq!(out.mods.iter().filter(|m| m.state == "loaded").count(), 5, "{:?}", out.mods);
        let j = journal(&f, &format!("eml_copy_{pass}"));
        assert_eq!(paths(package(&j, "alpha"), 0), ["child.base"], "pass {pass}: alpha stays named on its field: {j}");
        for n in 1..=4 {
            assert_eq!(paths(package(&j, &format!("copy{n}")), 0), [""], "pass {pass}: the whole resource");
        }
    }
}
