//! dev.21 closed DLL mods, launcher side: compat-rules and hooks.json fixtures (schema/fixtures/{compat-rules,
//! hooks-state}/launcher-*.json), and the whole flow on temp folders: a profile's late native and the rules land in
//! chain.ini [late] on activation, a rule switches a [chain] line off, the view maps the last session's overlaps.

use kiln_manage::compat::{self, Facts, OverlapKind, RuleAction, RuleSource, Section};
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::chain;
use std::path::{Path, PathBuf};

fn fixture(rel: &str) -> Vec<u8> {
    std::fs::read(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures").join(rel)).unwrap()
}

#[test]
fn rule_fixtures_parse_and_evaluate() {
    let (rules, errors) = compat::parse_rules(&fixture("compat-rules/launcher-basic.json"), RuleSource::Kiln);
    assert!(errors.is_empty(), "{errors:?}");
    assert_eq!(rules.len(), 4);
    let (empty, errors) = compat::parse_rules(&fixture("compat-rules/launcher-empty.json"), RuleSource::User);
    assert!(empty.is_empty() && errors.is_empty());
    let entries = vec![
        compat::PlanEntry { path: "chain\\xhl-core.dll".into(), file: "xhl-core.dll".into(), section: Section::Chain, engine: None, mod_id: None },
        compat::PlanEntry { path: "chain\\chain-loader-dinput8.dll".into(), file: "chain-loader-dinput8.dll".into(), section: Section::Chain, engine: Some("chain-loader".into()), mod_id: None },
    ];
    let facts = Facts {
        modules: vec!["xhl-core.dll".into(), "chain-loader-dinput8.dll".into(), "foo-hook.dll".into()],
        engines: vec!["kiln".into(), "shroudforge".into(), "chain-loader".into()],
        mods: vec!["emf:foo".into()],
        symbols: vec!["ChatLogAdd".into()],
    };
    let res = compat::evaluate(&rules, &facts, &entries);
    let fired: Vec<(&str, bool)> = res.iter().map(|r| (r.id.as_str(), r.applied)).collect();
    assert_eq!(fired, [("xhl-after-sf", true), ("chat-overlap", true), ("old-chainloader-off", false), ("emf-foo-off", false)]);
    assert!(!res[2].fired, "emf:better-chat is not in the profile");
    assert!(res[3].fired && res[3].note.is_some(), "kiln-core skips an engine mod itself");
}

#[test]
fn hooks_fixtures_parse() {
    let s = compat::parse_hooks(&fixture("hooks-state/launcher-overlaps.json")).unwrap();
    let o = compat::overlaps(&s);
    assert_eq!(o.iter().map(|o| o.kind).collect::<Vec<_>>(), [OverlapKind::ForeignBefore, OverlapKind::ForeignOver, OverlapKind::Refused]);
    assert_eq!(s.rules.len(), 2);
    // a WP-E core (no overlaps list): derived from the targets, Kiln's own modules left out
    let s = compat::parse_hooks(&fixture("hooks-state/launcher-wpe.json")).unwrap();
    let o = compat::overlaps(&s);
    assert_eq!(o.len(), 1);
    assert_eq!((o[0].symbol.as_str(), o[0].foreign.as_ref().unwrap().module.as_str()), ("ChatLogAdd", "xhl-core.dll"));
    assert!(o[0].foreign.as_ref().unwrap().in_place);
}

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

#[test]
fn activation_writes_late_and_rules_and_the_view_maps_overlaps() {
    let t = Tmp(std::env::temp_dir().join(format!("km-compat-flow-{}", std::process::id())));
    let _ = std::fs::remove_dir_all(&t.0);
    std::fs::create_dir_all(&t.0).unwrap();
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    // [chain]: XHL-Core and an old chain loader (its markers say chain-loader)
    put(&game, "kiln/chain/xhl-core.dll", &build_test_dll(&[], b"xhl"));
    put(&game, "kiln/chain/chain-loader-dinput8.dll", &build_test_dll(&[], b"reads dinput8-chain.ini"));
    put(&game, "kiln/chain.ini", b"[chain]\r\nchain\\xhl-core.dll\r\nchain\\chain-loader-dinput8.dll\r\n");
    put(&game, "kiln/compat-rules.json", &fixture("compat-rules/launcher-basic.json"));
    put(&game, "kiln/state/hooks.json", &fixture("hooks-state/launcher-overlaps.json"));
    // two dllmain natives
    let mut srcs = Vec::new();
    for id in ["fpsunlock", "foo-hook"] {
        let d = t.0.join(format!("src/{id}"));
        put(&d, "native.json", format!(r#"{{ "format": 1, "id": "{id}", "version": "1.0.0", "dlls": ["{id}.dll"], "phase": "dllmain", "init": null }}"#).as_bytes());
        put(&d, &format!("{id}.dll"), &build_test_dll(&[], id.as_bytes()));
        srcs.push(d);
    }
    let r = library::import(&dirs, &srcs, &mut NoProgress).unwrap();
    let lref = |id: &str| -> LibraryRef { r.imported.iter().find(|x| x.kind == ModKind::Native && x.id == id).cloned().unwrap() };
    let pm = |l: LibraryRef| ProfileMod { id: l.id, version: l.version, hash: l.hash, enabled: true };
    let mut p = profile::create(&dirs, "Closed", None).unwrap();
    p.mods.native = vec![pm(lref("fpsunlock")), pm(lref("foo-hook"))];
    p.late = vec!["fpsunlock".into()];
    let p = profile::save(&dirs, &p).unwrap();
    // the player's rule: switch foo-hook off while xhl is there; and SF is "present" via the player rule's when
    put(&dirs.profile(&p.id), "config/compat-rules.json", br#"{ "format": 1, "rules": [
        { "id": "foo-off", "when": [{"module": "xhl-core.dll"}], "action": "disable", "subject": {"mod": "native:foo-hook"}, "message": "foo-hook breaks XHL" },
        { "id": "xhl-after-sf", "when": [{"module": "xhl-*.dll"}], "action": "late", "subject": {"module": "xhl-*.dll"}, "message": "mine" },
        { "id": "loader-off", "when": [{"engine": "chain-loader"}], "action": "disable", "subject": {"engine": "chain-loader"} }
    ] }"#);
    let inst = GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game.clone(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None, build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
    };
    profile::activate(&dirs, &p.id, &inst).unwrap();
    let text = chain::read_raw(&game).unwrap();
    let c = chain::parse(text.as_bytes());
    assert_eq!(c.chain.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\xhl-core.dll"], "{text}");
    assert_eq!(chain::rule_off_entries(&text), [("loader-off".to_string(), "chain\\chain-loader-dinput8.dll".to_string())]);
    assert_eq!(c.profile.len(), 1, "foo-hook switched off by the player's rule: {text}");
    assert!(c.profile[0].path.ends_with("fpsunlock.dll"));
    let late: Vec<&str> = c.late.iter().map(|e| e.path.as_str()).collect();
    assert_eq!(late.len(), 2, "{text}");
    assert!(late[0].ends_with("fpsunlock.dll") && late[1] == "chain\\xhl-core.dll", "{text}");
    assert!(text.contains("; kiln rule xhl-after-sf"), "{text}");

    // the view: entries with who made them late, rules, overlaps mapped
    let p = profile::get(&dirs, &p.id).unwrap();
    let v = compat::view(&dirs, &p, &inst).unwrap();
    assert!(v.active && v.current && v.error.is_none(), "{v:?}");
    let e = |file: &str| v.entries.iter().find(|e| e.entry.file == file).unwrap();
    assert_eq!(e("xhl-core.dll").late_by.as_deref(), Some("rule:xhl-after-sf"));
    assert_eq!(e("fpsunlock.dll").late_by.as_deref(), Some("profile"));
    assert_eq!(e("foo-hook.dll").off_by.as_deref(), Some("rule:foo-off"));
    assert_eq!(e("chain-loader-dinput8.dll").off_by.as_deref(), Some("rule:loader-off"));
    assert_eq!(e("chain-loader-dinput8.dll").entry.engine.as_deref(), Some("chain-loader"));
    let xhl_rule = v.rules.iter().find(|r| r.id == "xhl-after-sf").unwrap();
    assert_eq!((xhl_rule.source, xhl_rule.action, xhl_rule.message.as_str()), (RuleSource::User, RuleAction::Late, "mine"));
    assert_eq!(v.session.as_ref().unwrap().phase.as_deref(), Some("world"));
    let o = |sym: &str| v.overlaps.iter().find(|o| o.overlap.symbol == sym).unwrap();
    assert_eq!((o("ChatLogAdd").entry.as_deref(), o("ChatLogAdd").late, o("ChatLogAdd").fix.as_str()), (Some("chain\\xhl-core.dll"), true, "none"));
    assert_eq!(o("SpawnNpc").library.as_ref().map(|l| l.id.as_str()), Some("fpsunlock"));
    assert_eq!(o("WorldTick").section, Some(Section::Profile));

    // the player turns fpsunlock's late off: the profile changes, refresh writes chain.ini again
    let mut p2 = p.clone();
    let entry = e("fpsunlock.dll").entry.clone();
    assert!(compat::set_entry_late(&inst, &mut p2, &entry, false).unwrap());
    profile::save(&dirs, &p2).unwrap();
    assert!(!compat::view(&dirs, &profile::get(&dirs, &p.id).unwrap(), &inst).unwrap().current);
    assert!(profile::refresh_active(&dirs, &inst).unwrap().is_none(), "profile.json itself is current");
    let c = chain::read(&game).unwrap();
    assert_eq!(c.late.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\xhl-core.dll"]);
    // a [chain] entry: game-wide switch in chain.ini
    let p3 = profile::get(&dirs, &p.id).unwrap();
    let v = compat::view(&dirs, &p3, &inst).unwrap();
    assert!(v.current);
    let mut p4 = p3.clone();
    assert!(!compat::set_entry_late(&inst, &mut p4, &v.entries.iter().find(|e| e.entry.file == "xhl-core.dll").unwrap().entry, false).unwrap());
    // the rule puts it back on the next activation
    assert!(chain::read(&game).unwrap().late.is_empty());
    profile::activate(&dirs, &p.id, &inst).unwrap();
    assert_eq!(chain::read(&game).unwrap().late.len(), 1);
    // deactivation leaves no [late] line naming a profile DLL
    profile::deactivate(&dirs, &inst).unwrap();
    let c = chain::read(&game).unwrap();
    assert!(c.profile.is_empty() && c.late.iter().all(|e| e.path.starts_with("chain\\")), "{c:?}");
}
