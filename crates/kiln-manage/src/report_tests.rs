//! Tests of `report`: the redaction, each finding (a fixture that triggers it and one that does not), broken installs,
//! size bounds. Fixtures are built by hand under the temp dir; the fake player is `frostling` on `frost-desk`.

use super::*;
use crate::game::PathMode;
use std::path::{Path, PathBuf};

const HOME: &str = "/var/home/frostling";

fn red(root: &Path) -> Redactor {
    let mut r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    r.add_home(&root.to_string_lossy());
    r
}

// ---------------------------------------------------------------- redaction

#[test]
fn redacts_linux_wine_and_windows_paths() {
    let r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    assert_eq!(redact("/var/home/frostling/.local/share/kiln/x", &r), "<home>/.local/share/kiln/x");
    assert_eq!(redact("file:///var/home/frostling/a", &r), "file:<home>/a", "the leading slashes belong to the path");
    assert_eq!(redact(r"Z:\var\home\frostling\.local\share\kiln", &r), r"<home>\.local\share\kiln");
    assert_eq!(redact(r"z:/VAR/home/Frostling/x", &r), "<home>/x");
    // inside JSON the backslashes are doubled
    assert_eq!(redact(r#""configDir": "Z:\\var\\home\\frostling\\.local""#, &r), r#""configDir": "<home>\\.local""#);
    // a different home that merely starts the same is not the home (the name inside it still goes)
    assert_eq!(redact("/var/home/frostlingx/a", &r), "/var/home/<user>x/a");
    let w = Redactor::new(Some(r"C:\Users\Frostling"), Some("Frostling"), None);
    assert_eq!(redact(r"c:/users/frostling/AppData/Local/Kiln", &w), "<home>/AppData/Local/Kiln");
    assert_eq!(redact(r"\\?\C:\Users\Frostling\Saved Games", &w), r"\\?\<home>\Saved Games");
    assert_eq!(redact(r#""C:\\Users\\FROSTLING\\x""#, &w), r#""<home>\\x""#);
    assert_eq!(redact(r"C:\Users\FROSTL~1\x", &w), r"C:\Users\FROSTL~1\x", "8.3 names that cut the name are out of reach");
}

#[test]
fn redacts_bare_names_ids_and_ips() {
    let r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    assert_eq!(redact("frostling's mod on FROST-DESK.local", &r), "<user>'s mod on <host>.local");
    assert_eq!(redact(r"C:\users\Frostling\AppData", &r), r"C:\users\<user>\AppData", "Wine's own user folder");
    assert_eq!(redact("player 76561198000000001 joined", &r), "player <steamid> joined");
    assert_eq!(redact(r"Steam\userdata\12345678\config", &r), r"Steam\userdata\<steamid>\config");
    assert_eq!(redact("[U:1:12345678]", &r), "[U:1:<steamid>]");
    assert_eq!(redact("join 203.0.113.7:15636 and 192.168.1.20", &r), "join <ip>:15636 and <ip>");
    assert_eq!(redact("lab 127.0.0.1:26090, bind 0.0.0.0", &r), "lab 127.0.0.1:26090, bind 0.0.0.0");
    // version numbers are no addresses
    for keep in ["Windows 10.0.22631", "1.2.3.4.5", "v1.2.3.4", "0.2.0-dev.21", "build 26430253", "300.1.1.1"] {
        assert_eq!(redact(keep, &r), keep);
    }
    // a short name only as a word, a common one never outside the home folder
    let s = Redactor::new(None, Some("al"), None);
    assert_eq!(redact("also al and Al.", &s), "also <user> and <user>.");
    let c = Redactor::new(Some("/home/user"), Some("user"), None);
    assert_eq!(redact("/home/user/x steamuser kiln.log user", &c), "<home>/x steamuser kiln.log user");
}

#[test]
fn redacts_secret_json_fields() {
    let mut v = serde_json::json!({
        "admin": {"token": "s3cr3t", "port": 8787}, "host": {"threads": 2}, "author": "me",
        "server": {"name": "Ember Isles", "address": "203.0.113.7:15636", "id": "srv-1"},
        "modpack": {"url": "https://example.org/p.zip?k=1"}, "rcon_password": 1234, "list": [{"apiKey": "x"}],
    });
    redact_json(&mut v);
    assert_eq!(v["admin"]["token"], "<redacted>");
    assert_eq!(v["admin"]["port"], 8787);
    assert_eq!(v["host"]["threads"], 2);
    assert_eq!(v["author"], "me");
    assert_eq!(v["server"]["name"], "Ember Isles");
    assert_eq!(v["server"]["address"], "<redacted>");
    assert_eq!(v["modpack"]["url"], "<redacted>");
    assert_eq!(v["rcon_password"], "<redacted>");
    assert_eq!(v["list"][0]["apiKey"], "<redacted>");
    // a file that does not parse keeps the key, loses the value
    let t = redact_json_lines("{\n  \"name\": \"a\",\n  \"token\": \"abc\",\n  \"x\": {\"password\": \"p\"\n");
    assert!(!t.contains("abc") && !t.contains("\"p\""), "{t}");
    assert!(t.contains("\"name\": \"a\"") && t.contains("\"token\": \"<redacted>\","), "{t}");
}

#[test]
fn os_release_names() {
    assert_eq!(os_release("NAME=\"Arch Linux\"\nPRETTY_NAME=\"Arch Linux\"\nID=arch\n"), (Some("Arch Linux".into()), Some("arch".into())));
    assert_eq!(os_release("NAME=SteamOS\nID=steamos\n"), (Some("SteamOS".into()), Some("steamos".into())));
    assert_eq!(os_release(""), (None, None));
}

// ---------------------------------------------------------------- fixtures

struct Fx {
    root: PathBuf,
    game: PathBuf,
}

impl Fx {
    fn new(name: &str) -> Fx {
        let root = std::env::temp_dir().join(format!("km-report-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let game = root.join("Enshrouded");
        std::fs::create_dir_all(&game).unwrap();
        Fx { root, game }
    }

    fn w(&self, rel: &str, bytes: impl AsRef<[u8]>) {
        let p = self.game.join(rel);
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(p, bytes).unwrap();
    }

    fn rm(&self, rel: &str) {
        std::fs::remove_file(self.game.join(rel)).unwrap();
    }

    fn install(&self, platform: Platform) -> GameInstall {
        GameInstall {
            id: "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b".into(),
            kind: GameKind::Client,
            path: self.game.clone(),
            exe: "enshrouded.exe".into(),
            app_id: crate::CLIENT_APP_ID,
            library: None,
            platform,
            prefix: None,
            build_id: Some("26430253".into()),
            exe_sha256: None,
            steam_flavor: None,
            path_mode: PathMode::Native,
            source: "manual".into(),
        }
    }

    fn input(&self) -> ReportInput {
        ReportInput {
            install: Some(self.install(Platform::Windows)),
            dirs: Some(DataDirs::at(self.root.join("data"))),
            tool: "kiln-launcher test".into(),
            roots: Some(Vec::new()),
            deck: Some(false),
            redactor: Some(red(&self.root)),
            ..Default::default()
        }
    }

    /// The receipt over these files as they are now.
    fn receipt(&self, files: &[&str]) {
        let files: Vec<serde_json::Value> = files.iter().map(|f| {
            let (sha, size) = crate::hash::sha256_file(&self.game.join(f)).unwrap();
            serde_json::json!({"path": f, "sha256": sha, "size": size, "component": "core"})
        }).collect();
        self.w("kiln/.install.json", serde_json::to_vec(&serde_json::json!({
            "format": 1, "installId": "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b", "installed": "2026-11-10T10:00:00Z",
            "installer": "kiln-launcher test", "runtime": {"version": "0.2.0-dev.22", "components": ["core"]}, "side": "client",
            "files": files, "replaced": [],
        })).unwrap());
    }

    fn profile(&self, dropins: serde_json::Value, mods: serde_json::Value) {
        self.w("kiln/profile.json", serde_json::to_vec(&serde_json::json!({
            "format": 1, "id": "my-mods", "name": "My mods", "generation": 3, "dropins": dropins,
            "configDir": format!(r"Z:\var\home\frostling\.local\share\kiln\profiles\my-mods\config"),
            "mods": mods,
        })).unwrap());
    }

    fn boot(&self, safe_idle: Option<&str>) {
        self.w("kiln/state/boot.json", serde_json::to_vec(&serde_json::json!({
            "format": 1, "kiln": "0.2.0-dev.22", "pid": 412, "written": "2026-11-10T11:00:00Z", "side": "client",
            "exe": {"name": "enshrouded.exe", "verified": safe_idle.is_none()}, "policy": "verified-only", "hooks": safe_idle.is_none(),
            "safeIdle": safe_idle, "profile": {"mode": "active", "id": "my-mods", "generation": 3,
                "configDir": r"Z:\var\home\frostling\.local\share\kiln\profiles\my-mods\config"},
            "paths": {"game": r"Z:\var\home\frostling\Enshrouded"}, "chain": [], "plugins": [], "errors": [],
        })).unwrap());
    }

    /// Kiln installed and healthy: nothing to find.
    fn healthy(name: &str) -> Fx {
        let f = Fx::new(name);
        f.w("enshrouded.exe", b"MZ not really the game");
        f.w("dinput8.dll", crate::pe::build_test_dll(&["KilnProxyInfo"], b""));
        f.w("kiln/kiln-core.dll", b"core bytes");
        f.w("kiln/Kiln.Host.dll", b"host bytes");
        f.receipt(&["dinput8.dll", "kiln/kiln-core.dll", "kiln/Kiln.Host.dll"]);
        let (sha, _) = crate::hash::sha256_file(&f.game.join("enshrouded.exe")).unwrap();
        f.w(&format!("kiln/sigdb/builds/client/{sha}.json"), format!(r#"{{"sha256":"{sha}","side":"client","verified":true}}"#));
        f.profile(serde_json::json!(false), serde_json::json!({"kiln": [{"id": "chat-plus", "version": "1.0.0", "path": r"Z:\var\home\frostling\.local\share\kiln\library\kiln\chat-plus\1.0.0+aaaaaaaa"}]}));
        f.boot(None);
        f.w("kiln/state/mods.json", br#"{"format":1,"pid":412,"written":"2026-11-10T11:00:01Z","generation":3,"mods":[{"id":"chat-plus","version":"1.0.0","state":"loaded"}]}"#);
        f.w("kiln/logs/kiln.log", "[I 11:00:00.000] [kiln] Kiln 0.2.0-dev.22 starting (pid 412)\r\n[I 11:00:00.100] [kiln] boot complete; handing control to the game\r\n");
        f.w("kiln/logs/proxy.log", "proxy: Kiln dinput8 0.2.0-dev.22\n");
        f.w("enshrouded.log", "game log\n");
        f
    }
}

impl Drop for Fx {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn ids(r: &Report) -> Vec<&str> {
    r.findings.iter().filter(|f| f.level != Level::Info).map(|f| f.id.as_str()).collect()
}

fn finding<'a>(r: &'a Report, id: &str) -> &'a Finding {
    r.findings.iter().find(|f| f.id == id).unwrap_or_else(|| panic!("no {id} in {:#?}", r.findings))
}

fn file<'a>(r: &'a Report, name: &str) -> Option<&'a str> {
    r.files.iter().find(|(n, _)| n == name).map(|(_, b)| std::str::from_utf8(b).unwrap())
}

// ---------------------------------------------------------------- findings

#[test]
fn healthy_install_finds_nothing() {
    let f = Fx::healthy("healthy");
    let r = build(&f.input());
    assert_eq!(ids(&r), Vec::<&str>::new(), "{}", r.summary);
    assert!(r.errors.is_empty(), "{:?}", r.errors);
    assert!(r.summary.contains("Known to Kiln: yes"), "{}", r.summary);
    assert!(r.summary.contains("chat-plus"));
    for name in ["summary.txt", "report.json", "game/kiln/logs/kiln.log", "game/kiln/state/boot.json", "game/kiln/profile.json",
        "game/kiln/.install.json", "listings/game-folder.txt", "listings/kiln-folder.txt"] {
        assert!(file(&r, name).is_some(), "{name} missing");
    }
}

#[test]
fn not_installed_and_foreign_dinput8() {
    let f = Fx::new("noinstall");
    f.w("enshrouded.exe", b"MZ");
    let r = build(&f.input());
    assert!(finding(&r, "not-installed").text.contains("not installed"));
    assert!(!ids(&r).contains(&"not-loaded"), "no Kiln, so nothing to load");
    // another engine's DLL at dinput8.dll, with and without Kiln's receipt
    f.w("dinput8.dll", crate::pe::build_test_dll(&[], b"EML_LOG_FILE_PATH"));
    let r = build(&f.input());
    assert!(finding(&r, "proxy-not-kiln").text.contains("EML"), "{:?}", r.findings);
    let h = Fx::healthy("foreign");
    assert!(!ids(&build(&h.input())).contains(&"proxy-not-kiln"));
    h.w("dinput8.dll", crate::pe::build_test_dll(&["DirectInput8Create"], b"dinput8-chain.ini"));
    let r = build(&h.input());
    assert!(finding(&r, "proxy-not-kiln").text.contains("another mod's installer"), "{:?}", r.findings);
}

#[test]
fn files_missing_or_changed_against_the_receipt() {
    let f = Fx::healthy("files");
    f.rm("kiln/kiln-core.dll");
    f.w("kiln/Kiln.Host.dll", b"host bytes, patched");
    let r = build(&f.input());
    let m = finding(&r, "files-missing");
    assert!(m.text.contains("kiln/kiln-core.dll") && m.text.contains("antivirus"), "{}", m.text);
    assert!(!m.text.contains("Kiln.Host.dll"));
    assert!(finding(&r, "files-changed").text.contains("kiln/Kiln.Host.dll"));
}

#[test]
fn kiln_did_not_load() {
    // the game ran (its log is there) and Kiln's proxy never wrote anything
    let f = Fx::healthy("notloaded");
    f.rm("kiln/state/boot.json");
    f.rm("kiln/logs/proxy.log");
    let r = build(&f.input());
    assert!(finding(&r, "not-loaded").text.contains("never started"), "{:?}", r.findings);
    // under Proton without the override: the advice comes with it
    let mut i = f.input();
    i.install = Some(f.install(Platform::Proton));
    let r = build(&i);
    let n = finding(&r, "not-loaded");
    assert!(n.text.contains("Launch options") || n.text.contains("launch options"), "{}", n.text);
    // a launch from the launcher after the last boot.json
    let g = Fx::healthy("stale");
    let old = SystemTime::now() - std::time::Duration::from_secs(3600);
    std::fs::File::options().write(true).open(g.game.join("kiln/state/boot.json")).unwrap().set_modified(old).unwrap();
    let dirs = DataDirs::at(g.root.join("data"));
    std::fs::create_dir_all(dirs.installs()).unwrap();
    std::fs::write(dirs.installs().join("c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b.launch.json"),
        format!(r#"{{"fingerprint":"kms1:x","mode":"profile","launched":"{}"}}"#, crate::util::now_rfc3339())).unwrap();
    let r = build(&g.input());
    assert!(finding(&r, "not-loaded").text.contains("did not load Kiln"), "{:?}", r.findings);
    // a fresh boot after the launch: fine
    std::fs::File::options().write(true).open(g.game.join("kiln/state/boot.json")).unwrap().set_modified(SystemTime::now() + std::time::Duration::from_secs(60)).unwrap();
    assert!(!ids(&build(&g.input())).contains(&"not-loaded"));
}

#[test]
fn unknown_build_and_safe_idle() {
    let f = Fx::healthy("build");
    let (sha, _) = crate::hash::sha256_file(&f.game.join("enshrouded.exe")).unwrap();
    f.rm(&format!("kiln/sigdb/builds/client/{sha}.json"));
    f.boot(Some("build abc is not verified (no build record) and policy is verified-only"));
    let r = build(&f.input());
    assert!(finding(&r, "unknown-build").text.contains("does not know"));
    assert!(finding(&r, "safe-idle").text.contains("no build record"));
    assert!(r.short.contains("known to Kiln: no"), "{}", r.short);
}

#[test]
fn engine_stood_aside_and_loader_in_folder() {
    let f = Fx::healthy("aside");
    f.w("kiln/state/eml.json", br#"{"pid":1,"phase":"done","mods":[{"id":"HoardersHelper","state":"loaded"}]}"#);
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"own-loader") && !ids(&r).contains(&"loader-in-folder"));
    // EML's own dbghelp.dll next to the game: on Windows it always runs
    f.w("dbghelp.dll", crate::pe::build_test_dll(&[], b"EML_LOG_FILE_PATH"));
    let r = build(&f.input());
    let l = finding(&r, "loader-in-folder");
    assert_eq!(l.level, Level::Warning, "Windows always loads it");
    assert!(l.text.starts_with("dbghelp.dll is EML"), "{}", l.text);
    let mut i = f.input();
    i.install = Some(f.install(Platform::Proton));
    assert_eq!(finding(&build(&i), "loader-in-folder").level, Level::Info, "Proton needs an override for it");
    // the plugin saw it and stood aside: one finding, from what the plugin wrote
    f.w("kiln/state/eml.json", br#"{"pid":1,"phase":"aside","aside":"dbghelp.dll is loaded","reason":"dbghelp.dll is loaded: EML's own loader patched the game files this start","mods":[]}"#);
    let r = build(&f.input());
    let o = finding(&r, "own-loader");
    assert!(o.text.contains("kiln-eml") && o.text.contains("EML mods in the profile"), "{}", o.text);
    assert!(!ids(&r).contains(&"loader-in-folder"));
    assert!(r.summary.contains("dbghelp.dll"));
}

#[test]
fn mods_in_the_game_folder_the_profile_skips() {
    let f = Fx::healthy("unlisted");
    f.w("kiln/mods/extra-mod/kiln.mod.json", br#"{"id":"extra-mod","version":"1.0.0","apiVersion":"0.2"}"#);
    f.w("kiln/mods/chat-plus/kiln.mod.json", br#"{"id":"chat-plus","version":"1.0.0","apiVersion":"0.2"}"#);
    f.w("kiln/mods/old-mod/.disabled", b"");
    f.w("mods/BiggerTorch/flame.json", br#"{}"#);
    let r = build(&f.input());
    let u = finding(&r, "unlisted-mods");
    assert!(u.text.contains("kiln/mods/extra-mod"), "{}", u.text);
    assert!(!u.text.contains("chat-plus"), "the profile lists it: {}", u.text);
    assert!(!u.text.contains("old-mod"), "switched off: {}", u.text);
    assert!(!u.text.contains("BiggerTorch"), "the profile has no Flame list, so Flame's folder is read: {}", u.text);
    // a Flame list in the profile: then the folder is not read
    f.profile(serde_json::json!(["kiln"]), serde_json::json!({"kiln": [], "flame": [{"id": "other", "path": "x"}]}));
    let r = build(&f.input());
    let u = finding(&r, "unlisted-mods");
    assert!(u.text.contains("mods/BiggerTorch") && !u.text.contains("extra-mod"), "{}", u.text);
    // drop-ins on: nothing is skipped
    f.profile(serde_json::json!(true), serde_json::json!({"kiln": [], "flame": [{"id": "other", "path": "x"}]}));
    assert!(!ids(&build(&f.input())).contains(&"unlisted-mods"));
    // legacy layout (no profile.json): every drop-in folder loads
    f.rm("kiln/profile.json");
    assert!(!ids(&build(&f.input())).contains(&"unlisted-mods"));
}

#[test]
fn report_and_game_folder_panel_agree() {
    let f = Fx::healthy("agree");
    f.w("kiln/mods/extra-mod/kiln.mod.json", br#"{"id":"extra-mod","version":"1.0.0","apiVersion":"0.2"}"#);
    f.w("mods/BiggerTorch/flame.json", br#"{}"#);
    f.profile(serde_json::json!(["kiln"]), serde_json::json!({"kiln": [], "flame": [{"id": "other", "path": "x"}]}));
    let r = build(&f.input());
    let u = finding(&r, "unlisted-mods");
    let file = crate::profile::read_game_profile(&f.game).unwrap().unwrap();
    let scan = crate::found::scan(&DataDirs::at(f.root.join("data")), &f.install(Platform::Windows), &profile_view(&file));
    for m in &scan.mods {
        assert_eq!(u.text.contains(&m.path), m.state == crate::found::FoundState::Off, "{} ({:?}): {}", m.path, m.state, u.text);
    }
    assert!(scan.mods.iter().any(|m| m.state == crate::found::FoundState::Loads), "{:?}", scan.mods);
}

#[test]
fn failed_mods_and_crash() {
    let f = Fx::healthy("failed");
    assert!(!ids(&build(&f.input())).contains(&"mod-failed"));
    f.w("kiln/state/mods.json", br#"{"format":1,"pid":412,"written":"2026-11-10T11:00:01Z","generation":3,"mods":[{"id":"chat-plus","state":"failed","reason":"missing dependency kiln.ui >= 2"}]}"#);
    f.w("kiln/state/flame.json", br#"{"status":"running","mods":[{"name":"BiggerTorch","status":"failed","error":"LoadLibrary 126"}]}"#);
    f.w("kiln/crash-reports/crash-20261110T110500Z.txt", "chat-plus faulted 5 times\n");
    let r = build(&f.input());
    let m = finding(&r, "mod-failed");
    assert!(m.text.contains("missing dependency kiln.ui") && m.text.contains("BiggerTorch") && m.text.contains("LoadLibrary 126"), "{}", m.text);
    assert!(finding(&r, "crash").text.contains("crash-20261110T110500Z.txt"));
    assert!(file(&r, "game/kiln/crash-reports/crash-20261110T110500Z.txt").is_some());
}

// ---------------------------------------------------------------- broken installs, bounds

#[test]
fn broken_profile_and_unreadable_files_still_give_a_report() {
    let f = Fx::healthy("broken");
    f.w("kiln/profile.json", b"{ not json");
    let r = build(&f.input());
    assert!(finding(&r, "profile-invalid").text.contains("not readable"));
    assert!(r.errors.iter().any(|e| e.contains("kiln/profile.json")), "{:?}", r.errors);
    assert!(file(&r, "game/kiln/logs/kiln.log").is_some(), "the rest is still there");
    assert!(file(&r, "game/kiln/profile.json").is_some_and(|t| t.contains("not json")), "the broken file itself is in it");
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let p = f.game.join("kiln/state/mods.json");
        std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o000)).unwrap();
        if std::fs::read(&p).is_err() {
            // (as root every file is readable)
            let r = build(&f.input());
            assert!(r.errors.iter().any(|e| e.contains("mods.json")), "{:?}", r.errors);
            assert!(file(&r, "summary.txt").unwrap().contains("Not readable"));
        }
        std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o644)).unwrap();
    }
}

#[test]
fn no_game_folder_or_a_missing_one() {
    let f = Fx::new("nogame");
    let mut i = f.input();
    i.install = None;
    let r = build(&i);
    assert_eq!(r.findings[0].id, "no-game");
    assert!(file(&r, "summary.txt").is_some());
    i.game_dir = Some(f.root.join("not-there"));
    let r = build(&i);
    assert!(r.errors.iter().any(|e| e.contains("does not exist")), "{:?}", r.errors);
    assert!(r.summary.contains("Left out"));
}

#[test]
fn size_bounds() {
    let f = Fx::healthy("big");
    let line = "[I 11:00:00.000] [host] stats: chat-plus: pre 0.0001 ms/step, post 0.0000 ms/step, max 0.001 ms, 0.002 ms/s, faults 0\n";
    f.w("kiln/logs/kiln.log", line.repeat((8 << 20) / line.len()));
    f.w("kiln/state/hooks.json", format!("{{\"x\":\"{}\"}}", "a".repeat(3 << 20)));
    for i in 0..3000 {
        f.w(&format!("kiln/mods/m{i:04}/kiln.mod.json"), b"{}");
    }
    let r = build(&f.input());
    let log = file(&r, "game/kiln/logs/kiln.log").unwrap();
    assert!(log.len() as u64 <= KILN_LOG_MAX + 200 && log.starts_with("... (older lines left out"), "{}", log.len());
    assert!(file(&r, "game/kiln/state/hooks.json").unwrap().len() as u64 <= FILE_MAX + 200);
    assert!(file(&r, "listings/mod-folders.txt").unwrap().contains("cut at"));
    let total: usize = r.files.iter().map(|(_, b)| b.len()).sum();
    assert!(total <= TOTAL_MAX + r.summary.len() + 64 * 1024, "{total}");
}

// ---------------------------------------------------------------- privacy over a whole tree

#[test]
fn nothing_private_in_any_file() {
    let f = Fx::healthy("leak");
    let root = f.root.to_string_lossy().into_owned();
    let private = format!(
        "[I 1] [kiln] paths: Z:\\var\\home\\frostling\\Enshrouded, /var/home/frostling/.steam, C:\\users\\frostling\\AppData, {root}/x\n\
         [I 2] [net] joined 203.0.113.7:15636 as 76561198000000001 on frost-desk, userdata/12345678\n\
         [I 3] [kiln] FROSTLING logged in\n");
    f.w("kiln/logs/kiln.log", format!("[I 11:00:00.000] [kiln] Kiln 0.2.0-dev.22 starting (pid 412)\n{private}"));
    f.w("kiln/logs/proxy.log", &private);
    f.w("kiln/logs/eml.log", &private);
    f.w("kiln/state/last-server.json", br#"{"format":1,"written":"2026-11-10T11:00:00Z","outcome":"welcome","server":{"id":"srv-1","name":"Isles","address":"203.0.113.7:15636"},"modpack":{"url":"https://example.org/p.zip?key=hunter3","sha256":"00"},"required":[]}"#);
    f.w("kiln/kiln.json", br#"{"policy":"verified-only","admin":{"token":"s3cr3t-token"},"remote":{"password":"hunter2"}}"#);
    f.w("kiln/config/network.json", br#"{"password":"hunter2-network"}"#);
    f.w("kiln/data/kiln-admin/admin-token", b"admintokenvalue");
    f.w("kiln/data/worlds/w1/save.bin", b"SAVEBYTES");
    f.w("kiln/mods/chat-plus/main.lua", b"MODCONTENTSECRET");
    f.w("kiln/mods/frostling-tweaks/kiln.mod.json", br#"{"id":"frostling-tweaks","version":"1.0.0"}"#);
    let launcher_log = f.root.join("data/logs/launcher.log");
    std::fs::create_dir_all(launcher_log.parent().unwrap()).unwrap();
    std::fs::write(&launcher_log, &private).unwrap();
    let r = build(&f.input());
    assert!(file(&r, "launcher/launcher.log").is_some());
    assert!(file(&r, "game/kiln/kiln.json").is_some_and(|t| t.contains("verified-only")));
    for (name, bytes) in &r.files {
        let t = String::from_utf8_lossy(bytes).to_lowercase();
        for bad in ["frostling", "frost-desk", "76561198000000001", "203.0.113.7", "12345678", "s3cr3t-token", "hunter2", "hunter3",
            "admintokenvalue", "savebytes", "modcontentsecret", &root.to_lowercase()] {
            assert!(!t.contains(bad), "{name} holds {bad:?}:\n{}", String::from_utf8_lossy(bytes));
        }
    }
    for t in [&r.short, &r.summary] {
        assert!(!t.to_lowercase().contains("frostling"));
    }
    for f in &r.findings {
        assert!(!f.text.to_lowercase().contains("frostling"), "{}", f.text);
    }
    assert!(r.summary.contains("<home>"), "the folder is shown redacted");
    assert!(!r.files.iter().any(|(n, _)| n.contains("kiln/config") || n.contains("kiln/data") || n.contains("main.lua")));
}

#[cfg(feature = "zip")]
#[test]
fn writes_a_zip() {
    let f = Fx::healthy("zip");
    let r = build(&f.input());
    let out = f.root.join(file_name(SystemTime::now()));
    let size = write_zip(&r, &out).unwrap();
    assert!(size > 0 && size < 1 << 20);
    let mut a = zip::ZipArchive::new(std::fs::File::open(&out).unwrap()).unwrap();
    let names: Vec<String> = (0..a.len()).map(|i| a.by_index(i).unwrap().name().unwrap().to_string()).collect();
    assert_eq!(names[0], "summary.txt");
    assert!(names.contains(&"game/kiln/state/boot.json".to_string()), "{names:?}");
    assert!(out.file_name().unwrap().to_string_lossy().starts_with("kiln-report-20"));
}

#[test]
fn print_fixture_summary() {
    // `cargo test -p kiln-manage report::tests::print_fixture_summary -- --nocapture` shows a fixture's summary.txt
    let f = Fx::healthy("print");
    f.rm("kiln/kiln-core.dll");
    f.w("kiln/mods/extra-mod/kiln.mod.json", br#"{"id":"extra-mod","version":"1.0.0","apiVersion":"0.2"}"#);
    f.w("kiln/state/eml.json", br#"{"pid":1,"phase":"aside","reason":"dbghelp.dll is loaded: EML's own loader patched the game files this start","mods":[]}"#);
    f.w("dbghelp.dll", crate::pe::build_test_dll(&[], b"EML_LOG_FILE_PATH"));
    f.w("kiln/chain.ini", "[chain]\nchain/emf-dinput8.dll\n");
    let r = build(&f.input());
    println!("{}\n---- short ----\n{}", r.summary, r.short);
}

#[test]
fn redacting_twice_changes_nothing() {
    let mut r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    r.add_whole_word("Steam", "<steam-name>");
    r.add_whole_word("Ember Knight", "<steam-name>");
    let once = redact("Z:\\var\\home\\frostling\\x frost-desk 76561198000000001 10.1.2.3 Ember Knight steam", &r);
    assert_eq!(once, "<home>\\x <host> <steamid> <ip> <steam-name> steam");
    assert_eq!(redact(&once, &r), once);
}

#[test]
fn a_default_account_name_is_not_a_secret() {
    // every Steam Deck's user is `deck`: the home folder goes, the word stays readable
    let r = Redactor::new(Some("/home/deck"), Some("deck"), None);
    let text = "Steam Deck: yes\nZ:\\home\\deck\\.local\\share\\kiln\\library\n/home/deck/.steam steamdeck";
    assert_eq!(redact(text, &r), "Steam Deck: yes\n<home>\\.local\\share\\kiln\\library\n<home>/.steam steamdeck");
}
