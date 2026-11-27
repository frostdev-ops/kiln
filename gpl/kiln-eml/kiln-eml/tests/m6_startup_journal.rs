// SPDX-License-Identifier: GPL-3.0-or-later
//! The startup change journal of a kiln-mode ShroudForge-dialect start (`kiln/eml/ownership/<stem>.change-journal.json`,
//! sf_startup/journal.rs, startup.rs): per package of the committed attempt the resources its entrypoint changed, each
//! with the member paths it changed there (format 2 of dialect shroudforge, 0.2.0-dev.32; sf_startup/journal_paths.rs),
//! the ones it created (by ordinal) and the contents it created, the packages not applied, bound to the published
//! generation; a cache hit keeps it byte for byte; a verified BASE removes it; an injected failure and a real kill at each
//! new step never leave a journal for another generation than the live one; kiln mode's EML dialect journals its pass too.
#[path = "support/m6_fixture/mod.rs"]
mod fixture;
#[path = "support/m6_startup/mod.rs"]
mod startup_support;

use fixture::archive::{ArchiveFixture, CREATED_GUID, RESOURCE_GUID, STEM};
use kfc::guid::{ContentHash, Guid, ResourceId};
use kiln_eml::{
    sf_pass::StartupDialect,
    sf_startup::journal::PACKAGE_ENTRIES,
    startup::{Attempt, Data, PackageKind, PlanFacts, StartupOutcome, State},
};
use mod_loader_lua::test_gate;
use serde_json::{Value, json};
use sha2::{Digest as _, Sha256};
use startup_support::{Start, broken, native};
use std::{
    cell::RefCell,
    collections::BTreeSet,
    fs,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    rc::Rc,
    time::{Duration, Instant},
};

const GET: &str = "local G = \"01234567-89ab-cdef-0123-456789abcdef\"\nlocal function res(part) return game.assets.get_resource(G, \"m6.Root\", part) end\n";

fn journal_path(game: &Path) -> PathBuf {
    game.join(format!("kiln/eml/ownership/{STEM}.change-journal.json"))
}
fn journal_bytes(s: &Start) -> Option<Vec<u8>> {
    fs::read(journal_path(s.game())).ok()
}
/// The journal, parsed, its field order checked (and saved below `EML_JOURNAL_OUT` as `<name>.json` for the schema).
fn journal(s: &Start, name: &str) -> Value {
    let bytes = journal_bytes(s).unwrap_or_else(|| panic!("{name}: no change journal"));
    if let Some(dir) = std::env::var_os("EML_JOURNAL_OUT") {
        let dir = PathBuf::from(dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(format!("{name}.json")), &bytes).unwrap();
    }
    let text = String::from_utf8(bytes).unwrap();
    let mut at = 0;
    for field in [
        "format",
        "stem",
        "generation",
        "receiptSha256",
        "dialect",
        "attempt",
        "truncated",
        "packages",
        "unattributed",
    ] {
        at += text[at..]
            .find(&format!("\"{field}\":"))
            .unwrap_or_else(|| panic!("{name}: {field} out of order"));
    }
    serde_json::from_str(&text).unwrap()
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn type_hash() -> String {
    format!("{:08x}", kfc::hash::fnv("m6.Root"))
}
fn changed(part: u32, paths: &[&str]) -> Value {
    json!({"guid": RESOURCE_GUID, "typeHash": type_hash(), "part": part, "createdBy": null, "paths": paths})
}
fn package<'a>(j: &'a Value, id: &str) -> &'a Value {
    j["packages"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["id"] == id)
        .unwrap_or_else(|| panic!("no journal entry for {id}: {j}"))
}
fn ids(j: &Value) -> Vec<String> {
    j["packages"]
        .as_array()
        .unwrap()
        .iter()
        .map(|p| p["id"].as_str().unwrap().to_owned())
        .collect()
}
/// A package's changed entries as a set (their order is the resource keys').
fn changed_set(p: &Value) -> BTreeSet<String> {
    p["changed"].as_array().unwrap().iter().map(|c| c.to_string()).collect()
}
fn set(values: &[Value]) -> BTreeSet<String> {
    values.iter().map(|v| v.to_string()).collect()
}
/// The journal names this outcome's live generation (the outcome's archiveSha256) and its receipt.
fn bound_to(j: &Value, out: &StartupOutcome) {
    let live = out.live.as_ref().expect("a granted outcome");
    assert_eq!(j["generation"], hex(&live.archive_sha256));
    assert_eq!(j["receiptSha256"], hex(live.receipt_sha256.as_ref().unwrap()));
    assert_eq!(j["stem"], STEM);
}
fn module(s: &Start, id: &str, name: &str, lua: &str) {
    fs::write(
        s.game().join("mods").join(id).join("src").join(format!("{name}.lua")),
        lua,
    )
    .unwrap();
}
fn record_path(game: &Path) -> PathBuf {
    game.join(format!("kiln/eml/cache/sf-startup-{STEM}.json"))
}

/// The mixed pass: `alpha` (ShroudForge) edits R0, keeps references in its module, creates a resource at random and
/// one with its own GUID, edits its creation, makes a content; `bad` edits R9, creates a resource and a content, then
/// fails in its own entrypoint (the retry runs without it); `bravo` (an EML package of the plan) edits R0 again and
/// inserts into R3's array; `charlie` only reads (R9, alpha's creation, alpha's kept reference); `delta` writes R0 and
/// alpha's creation through alpha's kept references, never touching `data` itself.
fn mixed_packages(s: &Start) {
    s.patch(
        "alpha",
        &[],
        &[],
        &format!(
            "{GET}local lib = require(\"mods.alpha.lib\")\nlocal r0 = res(0)\nr0.data.child.extra = 41\nlib.child = r0.data.child\n\
             local made = game.assets.create_resource(r0.data, \"m6.Root\")\nmade.data.child.extra = 5\nlib.made = made\n\
             game.assets.create_resource(r0.data, \"m6.Root\", \"{CREATED_GUID}\", 0)\n\
             game.assets.create_content(buffer.wrap(\"alpha blob\"))\n"
        ),
    );
    module(s, "alpha", "lib", "return {}\n");
    s.patch(
        "bad",
        &[],
        &[],
        &format!(
            "{GET}local r9 = res(9)\nr9.data.child.extra = 99\ngame.assets.create_resource(r9.data, \"m6.Root\")\n\
             game.assets.create_content(buffer.wrap(\"bad blob\"))\n{}",
            broken("bad fails")
        ),
    );
    s.patch(
        "bravo",
        &[],
        &[],
        &format!("{GET}res(0).data.child.extra = 42\ntable.insert(res(3).data.children, {{base = 1, extra = 2}})\n"),
    );
    s.patch(
        "charlie",
        &[],
        &[],
        &format!(
            "{GET}local lib = require(\"mods.alpha.lib\")\nlocal sum = res(9).data.child.extra + lib.made.data.child.extra + lib.child.extra\n\
             shroudforge.log.info(\"charlie read \" .. tostring(sum))\n"
        ),
    );
    s.patch(
        "delta",
        &[],
        &[],
        "local lib = require(\"mods.alpha.lib\")\nlib.child.extra = 77\nlib.made.data.child.extra = 8\n",
    );
}
fn mixed_plan(s: &Start) -> PlanFacts {
    let mut plan = s.plan(StartupDialect::Shroudforge, &[]);
    plan.packages.iter_mut().find(|p| p.id == "bravo").unwrap().kind = PackageKind::Eml;
    plan
}

// A mixed ShroudForge/EML pass with a retry journals only the committed attempt's changes, each named
// for the package whose entrypoint made it: two packages editing one resource are both named, a package that only
// read is named with nothing, writes through references kept from another package name the writer (and the creator's
// ordinal for a created resource), a package's writes into its own creations are not listed again, the failed
// package's edits, creation and content are absent. The record is bound to the published generation and carries each
// package's plan identity.
#[test]
fn the_journal_names_each_packages_changes_of_the_committed_attempt_only() {
    let s = Start::new("r1-mixed");
    mixed_packages(&s);
    let plan = mixed_plan(&s);
    assert_eq!(plan.load_order, ["alpha", "bad", "bravo", "charlie", "delta"]);
    let (_, out) = s.start(&plan, &native());
    assert_eq!((out.state, out.attempt), (State::Committed, Attempt::Retry), "{out:?}");
    assert_eq!(out.packages.iter().find(|r| r.id == "bad").unwrap().data, Data::Failed);
    assert!(
        s.points.borrow().iter().any(|p| p == "Journaled"),
        "{:?}",
        s.points.borrow()
    );
    let all = s.points.borrow().clone();
    let at = |p: &str| all.iter().position(|q| q == p).unwrap();
    assert!(at("Published") < at("Journaled") && at("Journaled") < at("CacheWritten"));
    assert_eq!(s.extra(0), 77, "delta's write through alpha's reference is live");
    let j = journal(&s, "mixed-retry");
    bound_to(&j, &out);
    assert_eq!(
        (&j["format"], &j["dialect"], &j["attempt"], &j["truncated"]),
        (&json!(2), &json!("shroudforge"), &json!("retry"), &json!(false))
    );
    assert!(j.get("writer").is_none(), "kiln-eml's own record names no writer (absent: kiln-eml)");
    assert_eq!(
        ids(&j),
        ["alpha", "bravo", "charlie", "delta"],
        "the committed attempt's entrypoints, in order"
    );
    for id in ids(&j) {
        let p = package(&j, &id);
        let planned = plan.package(&id).unwrap();
        assert_eq!(p["version"], "1.0.0");
        assert_eq!(p["contentSha256"], hex(&planned.content_sha256));
        assert_eq!(p["settingsSha256"], hex(&planned.config_sha256));
        assert_eq!(p["kind"], if id == "bravo" { "eml" } else { "shroudforge" });
        assert_eq!(p["truncated"], false);
    }
    // alpha: R0's child.extra changed; two creations by ordinal (its own edit of the first not listed again); its content
    let alpha = package(&j, "alpha");
    assert_eq!(alpha["changed"], json!([changed(0, &["child.extra"])]));
    let created = alpha["created"].as_array().unwrap();
    assert_eq!(created.len(), 2, "{alpha}");
    assert_eq!(
        (
            &created[0]["ordinal"],
            &created[0]["typeHash"],
            &created[0]["part"],
            &created[0]["explicit"]
        ),
        (&json!(1), &json!(type_hash()), &json!(0), &json!(false))
    );
    assert_eq!(
        created[1],
        json!({"ordinal": 2, "guid": CREATED_GUID, "typeHash": type_hash(), "part": 0, "explicit": true})
    );
    let random = created[0]["guid"].as_str().unwrap().to_owned();
    assert_ne!(random, CREATED_GUID);
    let blob = ContentHash::from_data(b"alpha blob");
    assert_eq!(alpha["contents"], json!([{"guid": blob.to_string(), "size": 10}]));
    // the created resources are in the published generation under those keys
    let index = s.f.index();
    for guid in [random.as_str(), CREATED_GUID] {
        let id = ResourceId::new(Guid::parse(guid).unwrap(), kfc::hash::fnv("m6.Root"), 0);
        assert!(index.resources().contains_key(&id), "{guid} not published");
    }
    // bravo (an EML package of the pass): R0's child.extra again, and the element it appended to R3's children
    assert_eq!(
        changed_set(package(&j, "bravo")),
        set(&[changed(0, &["child.extra"]), changed(3, &["children[2]", "children[]"])])
    );
    assert_eq!(package(&j, "bravo")["created"], json!([]));
    // charlie only read
    let charlie = package(&j, "charlie");
    assert_eq!(
        (&charlie["changed"], &charlie["created"], &charlie["contents"]),
        (&json!([]), &json!([]), &json!([]))
    );
    // delta wrote through alpha's references: R0, and alpha's first creation named by its creator's ordinal
    let made = json!({"guid": random, "typeHash": type_hash(), "part": 0, "createdBy": {"package": "alpha", "ordinal": 1},
        "paths": ["child.extra"]});
    assert_eq!(changed_set(package(&j, "delta")), set(&[changed(0, &["child.extra"]), made]));
    // nothing of bad's changes: not R9, not its creation, not its content; it is listed as not applied, with its error
    let not = j["notApplied"].as_array().unwrap_or_else(|| panic!("no notApplied: {j}"));
    assert_eq!(not.len(), 1, "{not:?}");
    assert_eq!((&not[0]["id"], &not[0]["kind"], &not[0]["version"]), (&json!("bad"), &json!("shroudforge"), &json!("1.0.0")));
    assert_eq!(not[0]["contentSha256"], hex(&plan.package("bad").unwrap().content_sha256));
    assert!(not[0]["reason"].as_str().unwrap().contains("bad fails"), "{}", not[0]["reason"]);
    let mut text = j.clone();
    text.as_object_mut().unwrap().remove("notApplied");
    let text = text.to_string();
    assert!(!text.contains("\"bad\""));
    assert!(!text.contains(&ContentHash::from_data(b"bad blob").to_string()));
    assert_eq!(
        j["packages"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|p| p["changed"].to_string().contains("\"part\":9"))
            .count(),
        0
    );
    assert_eq!(
        j["unattributed"],
        json!({"truncated": false, "changed": [], "created": [], "contents": []})
    );
}

// Each kind of write names its writer, and only the resource it wrote: a mapped field, an array element, an insertion,
// a removal, a variant's value, a whole data assignment (a copy of another resource), a write into that copy, a write
// into a copy assigned into another resource (the copy is the target's, never its source's), a refused insertion
// (nothing written: nothing named), a package that reads all three, and writes into owned values (a table validated
// into a resource's field, an array assigned whole) that another package made and shared through its module: a field
// assignment, an insertion, an element assignment, a removal and a clear each name the resource the value was
// validated into; a clear of a mapped array; an owned variant's value (every write site of the six wrappers).
// 0.2.0-dev.32 (format 2 of the ShroudForge dialect): each is named by the members it CHANGED, at their published places:
// an insertion or removal by the list's membership (and the elements between its unchanged ends), and a change a later
// package took away again (k02's element, cleared by k17; k11's and k14's elements of R9, removed by k15) is no longer
// in the published resource, so it is not named there.
#[test]
fn every_kind_of_write_is_named_and_a_copy_belongs_to_its_target() {
    let s = Start::new("r1-kinds");
    for (id, lua) in [
        ("k01", "res(0).data.child.extra = 1"),
        ("k02", "res(3).data.children[1] = {base = 4, extra = 5}"),
        ("k03", "table.insert(res(9).data.children, {base = 6, extra = 7})"),
        ("k04", "table.remove(res(0).data.children, 1)"),
        ("k05", "res(3).data.choice.value = {base = 8, extra = 9}"),
        ("k06", "res(9).data = res(0).data"),
        ("k07", "res(9).data.child.extra = 10"),
        (
            "k08",
            "local c = res(0).data.child\nlocal r3 = res(3)\nr3.data.maybe = c\nr3.data.maybe.extra = 11",
        ),
        ("k09", "assert(not pcall(table.insert, res(3).data.fixed, 5))"),
        (
            "k11",
            "local lib = require(\"mods.k11.lib\")\nres(0).data.maybe = {base = 21, extra = 22}\nlib.kept = res(0).data.maybe\n\
             res(9).data.children = {{base = 1, extra = 2}}\nlib.arr = res(9).data.children",
        ),
        ("k12", "require(\"mods.k11.lib\").kept.extra = 23"),
        (
            "k13",
            "table.insert(require(\"mods.k11.lib\").arr, {base = 3, extra = 4})",
        ),
        (
            "k10",
            "local n = res(0).data.child.extra + res(3).data.child.extra + res(9).data.child.extra",
        ),
        // the remaining write sites of the owned and mapped wrappers, each through a value another package left
        ("k14", "require(\"mods.k11.lib\").arr[1] = {base = 5, extra = 6}"),
        ("k15", "table.remove(require(\"mods.k11.lib\").arr, 1)"),
        ("k16", "table.clear(require(\"mods.k11.lib\").arr)"),
        ("k17", "table.clear(res(3).data.children)"),
        (
            "k18",
            "local lib = require(\"mods.k18.lib\")\nres(0).data.choice = {type = \"m6.Child\", value = {base = 1, extra = 2}}\n\
             lib.choice = res(0).data.choice",
        ),
        ("k19", "require(\"mods.k18.lib\").choice.value = {base = 3, extra = 4}"),
    ] {
        s.patch(id, &[], &[], &format!("{GET}{lua}\n"));
    }
    module(&s, "k11", "lib", "return {}\n");
    module(&s, "k18", "lib", "return {}\n");
    let (_, out) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!((out.state, out.attempt), (State::Committed, Attempt::First), "{out:?}");
    let j = journal(&s, "write-kinds");
    bound_to(&j, &out);
    let choice: &[&str] = &["choice.base", "choice.extra"];
    let maybe: &[&str] = &["maybe.base", "maybe.extra"];
    let items: &[&str] = &["children[]"];
    for (id, parts) in [
        ("k01", vec![(0, &["child.extra"][..])]),
        ("k02", vec![]),
        ("k03", vec![(9, items)]),
        ("k04", vec![(0, items)]),
        ("k05", vec![(3, choice)]),
        ("k06", vec![(9, &["child.extra", "children[]"][..])]),
        ("k07", vec![(9, &["child.extra"][..])]),
        ("k08", vec![(3, maybe)]),
        ("k09", vec![]),
        ("k10", vec![]),
        ("k11", vec![(0, maybe)]),
        ("k12", vec![(0, &["maybe.extra"][..])]),
        ("k13", vec![(9, items)]),
        ("k14", vec![]),
        ("k15", vec![(9, items)]),
        ("k16", vec![(9, items)]),
        ("k17", vec![(3, items)]),
        ("k18", vec![(0, choice)]),
        ("k19", vec![(0, choice)]),
    ] {
        let expected: Vec<Value> = parts.into_iter().map(|(part, paths)| changed(part, paths)).collect();
        assert_eq!(package(&j, id)["changed"], json!(expected), "{id}");
    }
    assert_eq!(s.extra(9), 10);
}

// The cached generation keeps its journal: a hit replays the outcome and leaves the journal byte for byte (still
// naming the live generation); a hit whose journal is missing or another is a miss: the pass runs again and journals
// its publication.
#[test]
fn a_cache_hit_keeps_the_journal_byte_for_byte_and_a_missing_one_is_a_miss() {
    let s = Start::new("r1-hit");
    mixed_packages(&s);
    let plan = mixed_plan(&s);
    let (_, first) = s.start(&plan, &native());
    assert_eq!(first.state, State::Committed);
    let bytes = journal_bytes(&s).unwrap();
    let (_, hit) = s.start(&plan, &native());
    assert_eq!(hit.state, State::Cached, "{hit:?}");
    assert!(
        !s.points
            .borrow()
            .iter()
            .any(|p| p.starts_with("Attempt") || p == "Journaled")
    );
    assert_eq!(journal_bytes(&s).unwrap(), bytes, "the hit changed the journal");
    bound_to(&journal(&s, "cache-hit"), &hit);
    // another journal: a miss
    let mut other = bytes.clone();
    other.push(b' ');
    fs::write(journal_path(s.game()), &other).unwrap();
    let (_, miss) = s.start(&plan, &native());
    assert_eq!(miss.state, State::Committed, "{miss:?}");
    bound_to(&journal(&s, "cache-miss-other"), &miss);
    // no journal: a miss
    fs::remove_file(journal_path(s.game())).unwrap();
    let (_, miss) = s.start(&plan, &native());
    assert_eq!(miss.state, State::Committed, "{miss:?}");
    let j = journal(&s, "cache-miss-none");
    bound_to(&j, &miss);
    assert_eq!(ids(&j), ["alpha", "bravo", "charlie", "delta"]);
}

// A verified BASE removes the journal: a double failure over a journaled generation (restored), a start that admits
// nothing (the BASE, unchanged) with a stray journal; an EML-dialect publication removes the ShroudForge dialect's
// journal and (0.2.0-dev.32) writes its own: format 2 of dialect eml, bound to the published generation, the EML mod
// named on the member it wrote.
#[test]
fn a_verified_base_and_an_eml_publication_remove_the_journal() {
    let s = Start::new("r1-restore");
    s.patch("good", &[], &[], &format!("{GET}res(0).data.child.extra = 5\n"));
    let (_, first) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!(first.state, State::Committed);
    assert!(journal_bytes(&s).is_some());
    s.patch("bad-one", &[], &[], &broken("first failure"));
    s.patch("bad-two", &[], &[], &broken("second failure"));
    let (_, out) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!(out.state, State::Restored, "{out:?}");
    assert_eq!(journal_bytes(&s), None, "the journal outlived the restored BASE");
    assert!(s.receipt().is_none());
    // a stray journal (no receipt) and nothing admitted: the BASE stays, the journal goes
    fs::write(journal_path(s.game()), b"{\"format\":1}").unwrap();
    let mut plan = s.plan(StartupDialect::Shroudforge, &[]);
    for p in &mut plan.packages {
        p.candidate = false;
    }
    let (_, out) = s.start(&plan, &native());
    assert_eq!(out.state, State::Unchanged, "{out:?}");
    assert_eq!(journal_bytes(&s), None, "a stray journal survived the verified BASE");
    // the EML dialect's publication removes the ShroudForge dialect's journal (and writes none)
    let s = Start::new("r1-eml");
    s.patch("good", &[], &[], &format!("{GET}res(0).data.child.extra = 6\n"));
    let (_, first) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!(first.state, State::Committed);
    assert!(journal_bytes(&s).is_some());
    fs::remove_dir_all(s.game().join("mods/good")).unwrap();
    let path = s.package(
        "eml-mod",
        json!({"capabilities": ["patch"]}),
        Some(
            "local r = game.assets.get_resource(\"01234567-89ab-cdef-0123-456789abcdef\", game.types.get(\"m6.Root\"))\n\
             r.data.child.extra = 66",
        ),
    );
    let mut plan = s.plan(StartupDialect::Eml, &[]);
    plan.packages[0].kind = PackageKind::Eml;
    let w = s.witness(&plan).unwrap();
    let env = |_: kiln_eml::startup::EmlSelection<'_>| -> anyhow::Result<Option<mod_loader::ModEnvironment>> {
        Ok(Some(
            mod_loader::ModEnvironment::load_from(
                &s.f.game,
                s.f.game.join("kiln/eml/cache"),
                vec![camino::Utf8PathBuf::from_path_buf(path.clone()).unwrap()],
            )
            .map_err(|_| anyhow::anyhow!("load"))?,
        ))
    };
    let out = s.run_with(&w, &plan, &native(), &env, &|_| {});
    assert_eq!(out.state, State::Committed, "{out:?}");
    assert_eq!(s.extra(0), 66);
    let j = journal(&s, "kiln-mode-eml");
    bound_to(&j, &out);
    assert_eq!((&j["format"], &j["dialect"]), (&json!(2), &json!("eml")), "{j}");
    assert_eq!(ids(&j), ["eml-mod"], "the ShroudForge dialect's journal is gone, the EML dialect's names its mod");
    let mod_ = package(&j, "eml-mod");
    assert_eq!(mod_["kind"], "eml");
    assert_eq!(mod_["contentSha256"], hex(&plan.packages[0].content_sha256), "kiln mode froze its digests");
    assert_eq!(mod_["changed"], json!([changed(0, &["child.extra"])]));
    assert_eq!(j["notApplied"], json!([]));
}

// Injected failures at the journal's steps. A journal that cannot be removed refuses the publication before anything
// changes (failed, nothing granted, the live set and the receipt as before). A journal write that fails after the
// publication (its path taken by a folder once the publication returned): the publication stands (committed,
// granted), a warning says the journal was not saved, no cache record names the generation, and the next start runs
// the pass again and journals it. At a verified BASE with a journal that cannot be removed: nothing is restored and
// nothing granted, the receipt kept, so the generation and its (absent) journal never disagree.
#[test]
fn a_failed_journal_write_or_removal_never_leaves_a_journal_for_another_generation() {
    let s = Start::new("r1-fail");
    s.patch("good", &[], &[], &format!("{GET}res(0).data.child.extra = 5\n"));
    let plan = s.plan(StartupDialect::Shroudforge, &[]);
    fs::create_dir_all(journal_path(s.game()).join("in-the-way")).unwrap();
    let (_, out) = s.start(&plan, &native());
    assert_eq!(out.state, State::Failed, "{out:?}");
    assert!(out.live.is_none());
    assert_eq!(
        fixture::archive::validate(&s.f.game, STEM).unwrap(),
        s.f.original,
        "something was published"
    );
    assert!(s.receipt().is_none());
    fs::remove_dir_all(journal_path(s.game())).unwrap();
    let w = s.witness(&plan).unwrap();
    let in_the_way = journal_path(s.game()).join("in-the-way");
    let out = s.run_with(&w, &plan, &native(), &|_| Ok(None), &|point| {
        if matches!(point, kiln_eml::startup::Point::Published) {
            fs::create_dir_all(&in_the_way).unwrap();
        }
    });
    assert_eq!(out.state, State::Committed, "{out:?}");
    assert!(out.live.is_some());
    assert!(
        out.warnings.iter().any(|w| w.code == "startup-journal-not-saved"),
        "{:?}",
        out.warnings
    );
    assert!(
        !record_path(s.game()).exists(),
        "a cache record without the generation's journal"
    );
    assert!(
        !s.points
            .borrow()
            .iter()
            .any(|p| p == "Journaled" || p == "CacheWritten")
    );
    fs::remove_dir_all(journal_path(s.game())).unwrap();
    let (_, again) = s.start(&plan, &native());
    assert_eq!(
        again.state,
        State::Committed,
        "the next start runs the pass again: {again:?}"
    );
    bound_to(&journal(&s, "after-failed-write"), &again);
    // the BASE with a journal that cannot be removed
    fs::remove_file(journal_path(s.game())).unwrap();
    fs::create_dir_all(journal_path(s.game()).join("in-the-way")).unwrap();
    s.patch("bad-one", &[], &[], &broken("first failure"));
    s.patch("bad-two", &[], &[], &broken("second failure"));
    let receipt = s.receipt_sha256();
    let (_, out) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!(out.state, State::Failed, "{out:?}");
    assert!(out.live.is_none());
    assert!(
        out.warnings.iter().any(|w| w.code == "base-not-restored"),
        "{:?}",
        out.warnings
    );
    assert_eq!(s.receipt_sha256(), receipt, "the receipt is kept");
    assert_eq!(s.extra(0), 5, "the generation stays live");
    fs::remove_dir_all(journal_path(s.game())).unwrap();
}

// Over the bounds: a package that creates more resources than a package's journal holds commits as usual; its journal
// entry keeps the first PACKAGE_ENTRIES creations (ordinals 1..) and is truncated, as is the journal.
#[test]
fn a_package_over_the_bound_is_truncated_never_failed() {
    let s = Start::new("r1-bound");
    let n = PACKAGE_ENTRIES + 5;
    s.patch(
        "many",
        &[],
        &[],
        &format!("{GET}local r0 = res(0)\nfor i = 1, {n} do game.assets.create_resource(r0.data, \"m6.Root\") end\n"),
    );
    s.patch("after", &[], &[], &format!("{GET}res(3).data.child.extra = 3\n"));
    let (_, out) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
    assert_eq!(out.state, State::Committed, "{out:?}");
    assert_eq!(s.f.index().resources().len(), 3 + n, "every creation is published");
    let j = journal(&s, "truncated");
    assert_eq!(j["truncated"], true);
    let many = package(&j, "many");
    assert_eq!(many["truncated"], true);
    let created = many["created"].as_array().unwrap();
    assert_eq!(created.len(), PACKAGE_ENTRIES);
    assert_eq!(created.last().unwrap()["ordinal"], PACKAGE_ENTRIES);
    assert_eq!(package(&j, "after")["changed"], json!([changed(3, &["child.extra"])]));
    assert_eq!(package(&j, "after")["truncated"], false);
    assert!(journal_bytes(&s).unwrap().len() < 16 << 20);
}

/// The kill child's spec: the game folder, where to stop (a point's text and which occurrence), the marker to write.
fn spawn_child(game: &Path, park: &str, nth: usize, marker: &Path) -> std::process::Child {
    let spec = json!({"game": game, "park": park, "nth": nth, "marker": marker});
    let mut command = Command::new(std::env::current_exe().unwrap());
    command
        .args(["--exact", "journal_kill_child", "--ignored", "--test-threads=1"])
        .env("KILN_R1_CHILD", spec.to_string())
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    test_gate::spawn(&mut command).unwrap()
}

/// The kill tests' child process: one kiln-mode start (ShroudForge dialect, every discovered package) over the
/// parent's game folder, stopped at the `nth` point `park` until it is killed.
#[test]
#[ignore = "child process of the change-journal kill tests; does nothing without KILN_R1_CHILD"]
fn journal_kill_child() {
    let Ok(spec) = std::env::var("KILN_R1_CHILD") else {
        return;
    };
    let spec: Value = serde_json::from_str(&spec).unwrap();
    let game = PathBuf::from(spec["game"].as_str().unwrap());
    let park = spec["park"].as_str().unwrap().to_owned();
    let nth = spec["nth"].as_u64().unwrap() as usize;
    let marker = PathBuf::from(spec["marker"].as_str().unwrap());
    let _hold = test_gate::lock_hold();
    let guard = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(game.join(".kiln-eml.lock"))
        .unwrap();
    guard.try_lock().unwrap();
    let plan = startup_support::plan_of(&game, StartupDialect::Shroudforge, &[]);
    let game8 = camino::Utf8PathBuf::from_path_buf(game.clone()).unwrap();
    let w = kiln_eml::startup::witness(&game8, STEM, &guard, &plan, startup_support::key()).unwrap();
    let seen = RefCell::new(0usize);
    let hook = |point: kiln_eml::startup::Point<'_>| {
        if format!("{point:?}") == park {
            *seen.borrow_mut() += 1;
            if *seen.borrow() == nth {
                let tmp = marker.with_extension("tmp");
                fs::write(&tmp, &park).unwrap();
                fs::rename(&tmp, &marker).unwrap();
                loop {
                    std::thread::sleep(Duration::from_secs(1));
                }
            }
        }
    };
    let types = Rc::new(fixture::types::registry());
    startup_support::run_over(
        &game,
        &types,
        &guard,
        &w,
        &plan,
        &native(),
        &|_| Ok(None),
        &hook,
        &RefCell::default(),
        &RefCell::default(),
        Rc::default(),
    );
}

/// Kills a child stopped at `park` over `f`'s game folder; returns the fixture with the install lock taken again.
fn killed_at(f: ArchiveFixture, park: &str) -> Start {
    let marker = f.root.0.join("killed").into_std_path_buf();
    let mut child = spawn_child(f.game.as_std_path(), park, 1, &marker);
    let started = Instant::now();
    while !marker.exists() {
        if let Some(status) = child.try_wait().unwrap() {
            panic!("{park}: the child exited ({status}) without reaching it");
        }
        if started.elapsed() > Duration::from_secs(120) {
            let _ = child.kill();
            let _ = child.wait();
            panic!("{park}: the child did not reach it in time");
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    child.kill().unwrap();
    child.wait().unwrap();
    assert_eq!(fs::read_to_string(&marker).unwrap(), park);
    fs::remove_file(&marker).unwrap();
    Start::over(f)
}

/// The journal on disk, if any, names the receipt beside it, whose generation is live and owned (the journal never
/// names another generation than the live one).
fn journal_names_the_live_generation(s: &Start, label: &str) {
    let Some(bytes) = journal_bytes(s) else { return };
    let j: Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(
        j["receiptSha256"].as_str(),
        s.receipt_sha256().as_deref(),
        "{label}: the journal names another receipt"
    );
    assert!(
        mod_loader::lua::owns_output(s.game(), STEM).unwrap(),
        "{label}: its generation is not live"
    );
    let w =
        mod_loader::lua::startup_host::InputWitness::capture(s.game(), STEM, s.f.index().containers().len()).unwrap();
    let receipt: [u8; 32] =
        Sha256::digest(fs::read(s.game().join(format!("kiln/eml/ownership/{STEM}.json"))).unwrap()).into();
    assert_eq!(
        j["generation"],
        hex(&kiln_eml::startup::generation_digest(
            STEM,
            Some(&receipt),
            w.baseline()
        )),
        "{label}: the journal's generation is not the receipt's"
    );
}

// Real kills around the journal, after a first start journaled its generation (G1) and a package changed: killed after
// the publication (before the journal), after the journal (before the cache record), after the cache record, and in
// the attempt (before anything is published). Each time the journal on disk is none or the live generation's; the next
// start (the recovery) leaves the live generation's journal, and after the cache-record kill it hits with the killed
// start's journal byte for byte.
#[test]
fn a_kill_at_each_journal_step_never_leaves_a_journal_for_another_generation() {
    for park in ["Published", "Journaled", "CacheWritten", "Step(Prepared)"] {
        let s = Start::new("r1-kill");
        s.patch("one", &[], &[], &format!("{GET}res(0).data.child.extra = 1\n"));
        let (_, first) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
        assert_eq!(first.state, State::Committed);
        let g1 = journal_bytes(&s).unwrap();
        s.patch("one", &[], &[], &format!("{GET}res(0).data.child.extra = 2\n"));
        s.patch("two", &[], &[], &format!("{GET}res(3).data.child.extra = 3\n"));
        let s = killed_at(s.into_fixture(), park);
        let label = format!("killed at {park}");
        journal_names_the_live_generation(&s, &label);
        let killed = journal_bytes(&s);
        match park {
            "Published" => assert_eq!(
                killed, None,
                "{label}: the old journal must be gone, the new not yet written"
            ),
            "Step(Prepared)" => assert_eq!(killed.as_deref(), Some(g1.as_slice()), "{label}: G1 is still live"),
            _ => assert!(killed.is_some(), "{label}"),
        }
        mod_loader::lua::prune(s.game(), STEM);
        let (_, out) = s.start(&s.plan(StartupDialect::Shroudforge, &[]), &native());
        let expected = if park == "CacheWritten" {
            State::Cached
        } else {
            State::Committed
        };
        assert_eq!(out.state, expected, "{label}: {out:?}");
        let j = journal(&s, &format!("recovered-{}", park.replace(['(', ')'], "-")));
        bound_to(&j, &out);
        journal_names_the_live_generation(&s, &format!("{label}, recovered"));
        if park == "CacheWritten" {
            assert_eq!(journal_bytes(&s), killed, "{label}: the hit changed the journal");
        }
        assert_eq!(ids(&j), ["one", "two"]);
        assert_eq!((s.extra(0), s.extra(3)), (json!(2), json!(3)));
    }
}
