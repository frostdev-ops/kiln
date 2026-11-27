// SPDX-License-Identifier: GPL-3.0-or-later
//! The startup change journal's contract with Kiln's registry stage, which reads the file format only (MIT code:
//! native/services/content/registry/archive_journal.cpp). kiln-eml's own writer (`sf_startup::journal`: the recorder of a
//! pass and `Journal::json`) makes the record of one fixed pass, and it must be byte for byte the fixture
//! `schema/sfi/eml-journal/fixtures/kiln-eml-writer.json`, which the native reader's host test
//! (native/services/content/tests/registry_writers_test.cpp) reads and names entries from. So a change of the writer's
//! output fails here, with the new record printed, until the fixture is made again from it; the reader's test then says
//! whether Kiln still reads it. The GUIDs are given as the game's key bytes: their text is what Kiln's id table holds for
//! the same bytes (keen::Guid's Windows layout, printed the usual way).
use kfc::guid::{ContentHash, Guid, ResourceId};
use kiln_eml::sf_startup::journal::{Header, Identity, Recorder};

/// The game's 16 key bytes of the GUID whose text is `text` (data1, data2, data3 little-endian, the rest as written).
fn key(text: &str) -> Guid {
    let hex: Vec<u8> = text
        .split('-')
        .flat_map(|g| (0..g.len()).step_by(2).map(move |i| u8::from_str_radix(&g[i..i + 2], 16).unwrap()))
        .collect();
    let mut b = [0u8; 16];
    b[..4].copy_from_slice(&[hex[3], hex[2], hex[1], hex[0]]);
    b[4..6].copy_from_slice(&[hex[5], hex[4]]);
    b[6..8].copy_from_slice(&[hex[7], hex[6]]);
    b[8..].copy_from_slice(&hex[8..]);
    Guid::from(b)
}

const ITEM_TYPE: u32 = 0x5c3f_0e21;
const STONE: &str = "1d622bd9-8170-41b1-822e-d3984b386d95";
const LOGS: &str = "aaaaaaaa-0000-4000-8000-000000000001";
const CREATED: &str = "5d2f8a90-1c3b-4e7f-9a12-6b4c8d0e2f13";
const EXPLICIT: &str = "0e0e0e0e-1111-4222-8333-444444444444";
const BALANCE: &str = "82706b40-61b1-4b8f-8b23-dcec6971bda1";

fn fixture_path() -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../schema/sfi/eml-journal/fixtures/kiln-eml-writer.json")
}

/// One pass: a write before any entrypoint; "alpha" (ShroudForge) writes the stone and the balance table, creates an item
/// at random and one with its own GUID, and a content; "bravo" (an EML package of the pass) writes the stone and alpha's
/// first creation; "charlie" changes nothing.
fn record() -> String {
    let r = Recorder::default();
    r.wrote(ResourceId::new(key(LOGS), ITEM_TYPE, 0));
    r.begin("alpha");
    r.wrote(ResourceId::new(key(STONE), ITEM_TYPE, 0));
    r.wrote(ResourceId::new(key(BALANCE), 0x0123_4567, 0));
    r.created(ResourceId::new(key(CREATED), ITEM_TYPE, 0), false);
    r.created(ResourceId::new(key(EXPLICIT), ITEM_TYPE, 1), true);
    r.content(ContentHash::from_data(b"kiln"));
    r.end();
    r.begin("bravo");
    r.wrote(ResourceId::new(key(STONE), ITEM_TYPE, 0));
    r.wrote(ResourceId::new(key(CREATED), ITEM_TYPE, 0));
    r.end();
    r.begin("charlie");
    r.end();
    let header = Header {
        stem: "enshrouded_server",
        generation: [0xab; 32],
        receipt_sha256: [0xcd; 32],
        dialect: "shroudforge",
        attempt: "first",
    };
    let identity = |id: &str| {
        Some(Identity {
            version: (id != "charlie").then(|| "1.2.0".to_string()),
            kind: if id == "bravo" { "eml" } else { "shroudforge" },
            content_sha256: [0x11; 32],
            settings_sha256: [0x22; 32],
        })
    };
    r.take().json(&header, &identity)
}

#[test]
fn the_writer_makes_the_record_kilns_reader_is_tested_on() {
    let made = record();
    let kept = std::fs::read_to_string(fixture_path()).unwrap_or_default();
    assert!(
        made == kept.trim_end(),
        "kiln-eml's journal record is no longer the fixture Kiln's reader is tested on ({}).\nMake the fixture this \
         one line and run the native registry_writers test:\n{made}",
        fixture_path().display()
    );
}

// ---- the EML dialect (format 2): the same contract for kiln-eml's EML pass (`Journal::from_eml` over the record EML's
// runtime makes, then `Journal::json`), fixture schema/sfi/eml-journal/fixtures/kiln-eml-writer-eml.json, which the native
// reader names entries and fields from (registry_writers_test.cpp: items and balance per field, a recipe and a loot
// entry per element). ----
const RECIPE_TYPE: u32 = 0x7e3a_11c9;
const LOOT_TYPE: u32 = 0x2b44_9d07;
const RECIPES: &str = "3c0a5e77-2f41-4b6a-9e10-5d8c7b6a5f40";
const LOOT: &str = "6e9f0a12-7c3d-4b85-a1f6-0d2e4c6b8a91";

fn eml_fixture_path() -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../schema/sfi/eml-journal/fixtures/kiln-eml-writer-eml.json")
}

/// One EML pass: "alpha" writes the stone's stack size, the balance table's level cap and a recipe's duration; "bravo"
/// writes the stone's damage factor, replaces recipe 2 whole after inserting it (the list's members), a loot entry's
/// stack size, creates an item and a content; "charlie" writes nothing; "two words" (an id the record cannot name)
/// replaces the logs whole, so its write is unattributed.
fn eml_record() -> String {
    use mod_loader::lua::{CreatedResource, PackageWrites, WriteRecord, WrittenResource};
    let written = |guid: &str, ty: u32, paths: &[&str]| WrittenResource {
        id: ResourceId::new(key(guid), ty, 0),
        created_by: None,
        paths: paths.iter().map(|p| p.to_string()).collect(),
    };
    let record = WriteRecord {
        packages: vec![
            PackageWrites {
                id: Some("alpha".into()),
                changed: vec![
                    written(BALANCE, 0x0123_4567, &["playerLevelCap"]),
                    written(STONE, ITEM_TYPE, &["maxStackSize"]),
                    written(RECIPES, RECIPE_TYPE, &["recipes[1].craftingDuration"]),
                ],
                ..PackageWrites::default()
            },
            PackageWrites {
                id: Some("bravo".into()),
                changed: vec![
                    written(STONE, ITEM_TYPE, &["damageSetup.dmgMod"]),
                    written(RECIPES, RECIPE_TYPE, &["recipes[2]", "recipes[]"]),
                    written(LOOT, LOOT_TYPE, &["items[0].stackSizeMax"]),
                ],
                created: vec![CreatedResource { ordinal: 1, id: ResourceId::new(key(CREATED), ITEM_TYPE, 0), explicit: false }],
                contents: vec![ContentHash::from_data(b"kiln")],
            },
            PackageWrites { id: Some("charlie".into()), ..PackageWrites::default() },
            PackageWrites { id: Some("two words".into()), changed: vec![written(LOGS, ITEM_TYPE, &[""])], ..PackageWrites::default() },
        ],
        unattributed: PackageWrites::default(),
    };
    let header = Header { stem: "enshrouded_server", generation: [0xef; 32], receipt_sha256: [0xef; 32], dialect: "eml", attempt: "first" };
    let identity = |id: &str| {
        Some(Identity { version: (id != "charlie").then(|| "1.0.0".to_string()), kind: "eml", content_sha256: [0; 32], settings_sha256: [0; 32] })
    };
    kiln_eml::sf_startup::journal::Journal::from_eml(&record).json(&header, &identity)
}

#[test]
fn the_eml_pass_makes_the_record_kilns_reader_is_tested_on() {
    let made = eml_record();
    let kept = std::fs::read_to_string(eml_fixture_path()).unwrap_or_default();
    assert!(
        made == kept.trim_end(),
        "kiln-eml's EML-dialect journal record is no longer the fixture Kiln's reader is tested on ({}).\nMake the fixture \
         this one line and run the native registry_writers test:\n{made}",
        eml_fixture_path().display()
    );
}

// ---- the ShroudForge dialect with member paths (format 2, 0.2.0-dev.32): kiln mode's record of a fixed pass (the
// recorder's per-resource rule, then the member paths each package changed, `Journal::with_member_paths`, written with
// `notApplied`), fixture schema/sfi/eml-journal/fixtures/kiln-eml-writer-sf2.json, which the native reader names per field
// and per element from (registry_writers_test.cpp). ----
fn sf2_fixture_path() -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../schema/sfi/eml-journal/fixtures/kiln-eml-writer-sf2.json")
}

/// One kiln-mode pass: "alpha" (ShroudForge) changes the stone's stack size and a recipe's duration and creates an item;
/// "bravo" (an EML package of the pass) appends a recipe and wrote the stone back as it was (so it is not listed there);
/// "charlie" changes nothing; "broken" failed and the retry ran without it.
fn sf2_record() -> String {
    use kiln_eml::sf_startup::journal::{NotApplied, Tail};
    let stone = ResourceId::new(key(STONE), ITEM_TYPE, 0);
    let recipes = ResourceId::new(key(RECIPES), RECIPE_TYPE, 0);
    let r = Recorder::default();
    r.begin("alpha");
    r.wrote(stone);
    r.wrote(recipes);
    r.created(ResourceId::new(key(CREATED), ITEM_TYPE, 0), false);
    r.end();
    r.begin("bravo");
    r.wrote(stone);
    r.wrote(recipes);
    r.end();
    r.begin("charlie");
    r.end();
    let journal = r.take().with_member_paths(|bucket, id| {
        let paths: &[&str] = match (bucket, *id == stone, *id == recipes) {
            (1, true, _) => &["maxStackSize"],
            (1, _, true) => &["recipes[1].craftingDuration"],
            (2, true, _) => &[],
            (2, _, true) => &["recipes[2]", "recipes[]"],
            _ => return None,
        };
        Some(paths.iter().map(|p| p.to_string()).collect())
    });
    let header = Header { stem: "enshrouded_server", generation: [0x7c; 32], receipt_sha256: [0x3e; 32], dialect: "shroudforge", attempt: "retry" };
    let identity = |id: &str| {
        Some(Identity { version: Some("1.2.0".into()), kind: if id == "bravo" { "eml" } else { "shroudforge" }, content_sha256: [0x11; 32], settings_sha256: [0x22; 32] })
    };
    let not_applied = [NotApplied { id: "broken".into(), version: Some("0.9.0".into()), kind: "shroudforge", content_sha256: [0x11; 32],
        reason: "runtime error: [string \"broken\"]:2: boom\nstack traceback:".into() }];
    journal.json_with(&header, &identity, &Tail { writer: None, not_applied: Some(&not_applied) })
}

#[test]
fn the_shroudforge_dialect_with_paths_makes_the_record_kilns_reader_is_tested_on() {
    let made = sf2_record();
    let kept = std::fs::read_to_string(sf2_fixture_path()).unwrap_or_default();
    assert!(
        made == kept.trim_end(),
        "kiln-eml's format-2 ShroudForge-dialect record is no longer the fixture Kiln's reader is tested on ({}).\nMake the \
         fixture this one line and run the native registry_writers test:\n{made}",
        sf2_fixture_path().display()
    );
}
