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
fn addon_log_is_optional_bounded_and_redacted() {
    let f = Fx::healthy("addon-log");
    let absent = build(&f.input());
    assert!(file(&absent, "game/shroudlink_client.log").is_none());
    assert!(absent.errors.is_empty(), "{:?}", absent.errors);

    let old = "[1] old addon startup\n";
    let last = "[2] shroudlink client 0.8.0 starting (module 0x1234 Z:\\var\\home\\frostling\\addon.dll)\n\
                [3] native UI ready: fixture\n[4] ui: screen 0x04 page=false menu=4 open=0\n";
    f.w("shroudlink_client.log", old.repeat(SMALL_LOG_MAX as usize / old.len() + 1) + last);
    // Mod settings are not needed to diagnose initialization and stay private.
    f.w("shroudlink_keys.json", br#"{"mods":"K"}"#);
    f.w("shroudlink_mods_off.json", br#"["storage"]"#);
    let r = build(&f.input());
    let log = file(&r, "game/shroudlink_client.log").expect("the addon log must be collected");
    assert!(log.starts_with("... (older lines left out"));
    assert!(log.len() as u64 <= SMALL_LOG_MAX + 200);
    assert!(log.contains(r"<home>\addon.dll") && !log.contains("frostling"), "{log}");
    assert!(log.contains("native UI ready: fixture") && log.contains("menu=4"));
    assert!(!r.files.iter().any(|(n, _)| n.ends_with("shroudlink_keys.json") || n.ends_with("shroudlink_mods_off.json")));
    assert_eq!(std::fs::read_to_string(f.game.join("shroudlink_client.log")).unwrap(),
        old.repeat(SMALL_LOG_MAX as usize / old.len() + 1) + last, "collection is read-only");
}

/// manage-import F9: the addon log sits in the game root, where no Kiln component writes; a link planted there (by a
/// hand-extracted archive) must not put the file it points at into the report the player posts.
#[cfg(unix)]
#[test]
fn a_link_at_the_addon_log_is_not_followed() {
    let f = Fx::healthy("addon-log-link");
    let secret = f.root.join("outside/id_ed25519");
    std::fs::create_dir_all(secret.parent().unwrap()).unwrap();
    std::fs::write(&secret, "-----BEGIN OPENSSH PRIVATE KEY-----\nSECRETKEYBYTES\n-----END OPENSSH PRIVATE KEY-----\n").unwrap();
    std::os::unix::fs::symlink(&secret, f.game.join("shroudlink_client.log")).unwrap();
    let r = build(&f.input());
    for (name, bytes) in &r.files {
        assert!(!String::from_utf8_lossy(bytes).contains("SECRETKEYBYTES"), "{name} holds the link's target");
    }
    assert!(file(&r, "game/shroudlink_client.log").is_none(), "{:?}", file(&r, "game/shroudlink_client.log"));
    assert!(r.errors.iter().any(|e| e.contains("shroudlink_client.log")), "say why it was left out: {:?}", r.errors);
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

/// A player's report of 13 Nov 2026 (Steam Deck): Kiln hosted ShroudForge, kiln-eml stood aside for it as designed, and
/// the summary said "EML's own loader runs in the game" as a problem. No such loader ran. `Fx::healthy`'s boot.json
/// names pid 412: the two state files are of the last start when they name it too.
#[test]
fn eml_aside_for_hosted_shroudforge_is_no_own_loader() {
    const WHY: &str = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    const BOOT: u32 = 412;
    let eml = |pid: u32, aside: &str, mods: &str| format!(r#"{{"pid":{pid},"phase":"aside","aside":"{aside}","mods":[{mods}]}}"#);
    let sf = |pid: u32, mode: &str| format!(r#"{{"pid":{pid},"mode":"{mode}","source":"installed","runtime":"running"}}"#);
    let on = r#"{"id":"a","state":"own-loader","reason":"runs under ShroudForge (hosted by Kiln): its ShroudForge switch is on"}"#;
    let f = Fx::healthy("eml-under-sf");
    f.w("kiln/state/shroudforge.json", sf(BOOT, "hosted").as_bytes());
    f.w("kiln/state/eml.json", eml(BOOT, WHY, on).as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"own-loader"), "{:?}", ids(&r));
    let n = finding(&r, "eml-under-shroudforge");
    assert_eq!(n.level, Level::Info);
    assert!(n.text.starts_with("kiln-eml stood aside at the last start for ShroudForge, which Kiln hosted"), "{}", n.text);
    assert!(!r.summary.contains("EML's own loader runs in the game"), "{}", r.summary);
    // a mod ShroudForge has switched off, and one that is not in its folder: these did not run, and that is the finding
    let off = format!(r#"{on},{{"id":"b","state":"off-in-shroudforge","reason":"x"}},{{"id":"c","state":"not-loaded","reason":"y"}},{{"id":"d","state":"disabled"}}"#);
    f.w("kiln/state/eml.json", eml(BOOT, WHY, &off).as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "eml-under-shroudforge");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.starts_with("2 EML mods of the profile did not run") && n.text.contains(": b, c. "), "{}", n.text);
    assert!(!ids(&r).contains(&"own-loader"));
    // EML's own loader in a start where Kiln hosts ShroudForge too: still the own-loader problem
    f.w("kiln/state/eml.json", eml(BOOT, "dbghelp.dll is loaded", on).as_bytes());
    assert!(ids(&build(&f.input())).contains(&"own-loader"));
    // ShroudForge ran by itself: nothing says Kiln hosted it
    f.w("kiln/state/eml.json", eml(BOOT, WHY, on).as_bytes());
    f.w("kiln/state/shroudforge.json", sf(BOOT, "self-hosted").as_bytes());
    let r = build(&f.input());
    assert!(ids(&r).contains(&"own-loader") && !r.findings.iter().any(|x| x.id == "eml-under-shroudforge"), "{:?}", ids(&r));
}

/// Review of 6a78171d: two files of one OLDER start said "hosted" and "aside for ShroudForge" while neither plugin ran
/// in the last start; and a file without a pid stood for any start. Neither proves anything about the last start, so
/// neither takes the place of the own-loader finding.
#[test]
fn state_files_of_another_start_do_not_stand_for_the_last_one() {
    const WHY: &str = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    let f = Fx::healthy("eml-under-sf-stale");
    let own_loader = |sf: &str, eml: &str| {
        f.w("kiln/state/shroudforge.json", sf.as_bytes());
        f.w("kiln/state/eml.json", eml.as_bytes());
        let r = build(&f.input());
        ids(&r).contains(&"own-loader") && !r.findings.iter().any(|x| x.id == "eml-under-shroudforge")
    };
    let sf = |pid: &str| format!(r#"{{{pid}"mode":"hosted","source":"bundled"}}"#);
    let eml = |pid: &str| format!(r#"{{{pid}"phase":"aside","aside":"{WHY}","mods":[]}}"#);
    assert!(!own_loader(&sf(r#""pid":412,"#), &eml(r#""pid":412,"#)), "the last start's pair is the hosted case");
    assert!(own_loader(&sf(r#""pid":7,"#), &eml(r#""pid":7,"#)), "an older start's pair");
    assert!(own_loader(&sf(r#""pid":412,"#), &eml(r#""pid":7,"#)), "kiln-eml's file is older");
    assert!(own_loader(&sf(r#""pid":7,"#), &eml(r#""pid":412,"#)), "ShroudForge's file is older");
    assert!(own_loader(&sf(""), &eml(r#""pid":412,"#)), "ShroudForge's file names no pid");
    assert!(own_loader(&sf(r#""pid":412,"#), &eml("")), "kiln-eml's file names no pid");
}

/// Nexus report (11 Nov 2026, The Worst Map Mod): a Greeneye mod says what it found and patched only in its own log and
/// status file in its folder (unlock_map_markers.log, UnlockMapMarkers.status), not in kiln.log. The report carries
/// them, bounded and redacted, with Greeneye Loader's own loader.log; never the mod's settings.
#[test]
fn greeneye_mod_logs_are_in_the_report() {
    let f = Fx::healthy("greeneye-logs");
    // kiln-greeneye ran the mod from Kiln's copy in greeneye\ (and an older Kiln from a Library entry)
    let lib = f.root.join("data/library/greeneye/find/1.4.0+bbbbbbbb");
    std::fs::create_dir_all(&lib).unwrap();
    std::fs::write(lib.join("find.log"), "Find started from Z:\\var\\home\\frostling\\x\n").unwrap();
    f.w("kiln/state/greeneye.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "plugin": "kiln-greeneye", "status": "running", "steppedAside": false, "updatedUnixMs": 1,
        "mods": [
            {"id": "unlock-map-markers", "status": "started", "folder": format!("{}", f.game.join("greeneye/unlock-map-markers").display())},
            {"id": "find", "status": "started", "folder": format!("{}", lib.display())}]})).unwrap());
    f.w("greeneye/unlock-map-markers/mod.json", br#"{"id":"unlock-map-markers"}"#);
    let old = "[2026-11-11 09:00:00.000] old line\n";
    f.w("greeneye/unlock-map-markers/unlock_map_markers.log",
        old.repeat(GREENEYE_LOG_MAX as usize / old.len() + 1) + "[2026-11-11 10:00:00.000] ERROR: Failed to find signature for map_marker_type_quest_rumor_downgrade (frostling)\n");
    f.w("greeneye/unlock-map-markers/UnlockMapMarkers.status", "state=error_signature_not_found\npatched=0\n");
    f.w("greeneye/unlock-map-markers/minimap_config.ini", "[MinimapV2]\nSecretNote=frostling\n");
    f.w("greeneye/loader.log", "[10:00:00.000] Greeneye Loader 1.2.2 loaded as dbgcore.dll in enshrouded.exe (game)\n");
    f.w("kiln/logs/greeneye-loader.log", "[10:00:00.000] kiln-greeneye 1.0.0 (GreeneyeHost API 2) on the client\n");
    let r = build(&f.input());
    let log = file(&r, "greeneye-mods/unlock-map-markers/game/unlock_map_markers.log").expect("the mod's own log");
    assert!(log.starts_with("... (older lines left out") && log.len() as u64 <= GREENEYE_LOG_MAX + 200, "bounded");
    assert!(log.contains("Failed to find signature") && log.contains("(<user>)") && !log.contains("frostling"), "{log}");
    assert!(file(&r, "greeneye-mods/unlock-map-markers/game/UnlockMapMarkers.status").is_some_and(|t| t.contains("patched=0")));
    assert!(file(&r, "greeneye-mods/find/library/find.log").is_some_and(|t| t.contains("Find started") && !t.contains("frostling")), "a Library folder too");
    assert!(file(&r, "game/greeneye/loader.log").is_some_and(|t| t.contains("Greeneye Loader 1.2.2")));
    assert!(file(&r, "game/kiln/logs/greeneye-loader.log").is_some());
    assert!(!r.files.iter().any(|(n, _)| n.ends_with("minimap_config.ini")), "the mod's settings stay private");
    assert!(r.summary.contains("kiln-greeneye      running"), "the engine's word: {}", r.summary);
}

/// The zip is posted in public: a folder that kiln/state/greeneye.json names is read only inside the game's greeneye/
/// folder or the launcher's Library for Greeneye mods, resolved and without links. Anything else is left out, with a note.
#[test]
fn greeneye_logs_come_only_from_greeneyes_folders() {
    let f = Fx::healthy("greeneye-outside");
    let outside = f.root.join("elsewhere/private");
    std::fs::create_dir_all(&outside).unwrap();
    std::fs::write(outside.join("secret.log"), "not a mod's log\n").unwrap();
    std::fs::write(outside.join("mod.json"), r#"{"id":"x"}"#).unwrap();
    // a folder in greeneye/ that is a link to the same place
    std::fs::create_dir_all(f.game.join("greeneye")).unwrap();
    std::os::unix::fs::symlink(&outside, f.game.join("greeneye/linked")).unwrap();
    // and a Library path that climbs out of the Library
    std::fs::create_dir_all(f.root.join("data/library/greeneye")).unwrap();
    let climb = f.root.join("data/library/greeneye/../../../elsewhere/private");
    f.w("kiln/state/greeneye.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "plugin": "kiln-greeneye", "status": "running", "steppedAside": false, "updatedUnixMs": 1,
        "mods": [{"id": "outside", "status": "started", "folder": format!("{}", outside.display())},
                 {"id": "climb", "status": "started", "folder": format!("{}", climb.display())}]})).unwrap());
    let r = build(&f.input());
    assert!(!r.files.iter().any(|(n, b)| n.contains("secret") || String::from_utf8_lossy(b).contains("not a mod's log")),
        "{:?}", r.files.iter().map(|(n, _)| n).collect::<Vec<_>>());
    let note = r.findings.iter().find(|x| x.id == "greeneye-log-skipped").unwrap_or_else(|| panic!("{:#?}", r.findings));
    assert_eq!(note.level, Level::Info);
    assert!(note.text.contains("outside") && note.text.contains("climb") && note.text.contains("linked"), "{}", note.text);
}

/// Player report r1111 (Steam Deck, Proton without a dbgcore override): Greeneye's dbgcore.dll was in the folder but did
/// not start, kiln-greeneye said so and ran the mods; the report said kiln-greeneye "stands aside for it".
#[test]
fn a_loader_in_the_folder_that_did_not_start_is_not_said_to_have_made_kiln_stand_aside() {
    let f = Fx::healthy("greeneye-idle-loader");
    f.w("dbgcore.dll", crate::pe::build_test_dll(&["MiniDumpWriteDump"], b"Greeneye Loader 1.2.2"));
    let mut i = f.input();
    i.install = Some(f.install(Platform::Proton));
    // no state from this start: the note says what the loader would do
    let l = finding(&build(&i), "loader-in-folder").text.clone();
    assert!(l.contains("stands aside"), "{l}");
    // kiln-greeneye wrote at the last start (the process boot.json names, pid 412) that it did not step aside. It writes
    // in on_ready, seconds before the core writes boot.json after the CLR: its time says nothing about which start it is.
    f.w("kiln/state/greeneye.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "plugin": "kiln-greeneye", "status": "running", "steppedAside": false, "pid": 412, "updatedUnixMs": 1, "mods": []})).unwrap());
    let l = finding(&build(&i), "loader-in-folder").text.clone();
    assert!(!l.contains("stands aside") && l.contains("did not start at the last start") && l.contains("ran the Greeneye Loader mods itself"), "{l}");
    // a state file of another start (another process), however recent: what the loader would do
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis() as i64;
    f.w("kiln/state/greeneye.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "plugin": "kiln-greeneye", "status": "running", "steppedAside": false, "pid": 9, "updatedUnixMs": now, "mods": []})).unwrap());
    assert!(finding(&build(&i), "loader-in-folder").text.contains("stands aside"));
}

/// Review R8: the head of a JSON or INI game file is read only from the file itself, never through a link (a link at
/// `<game>/dinput8-chain.ini` put up to 1 MiB of its target into a zip players post in public).
#[test]
fn a_linked_game_file_is_not_followed() {
    let f = Fx::healthy("linked-head");
    let target = f.root.join("elsewhere/private.txt");
    std::fs::create_dir_all(target.parent().unwrap()).unwrap();
    std::fs::write(&target, "[Chain]\nnot the game's chain file, private text\n").unwrap();
    std::os::unix::fs::symlink(&target, f.game.join("dinput8-chain.ini")).unwrap();
    let r = build(&f.input());
    assert!(!r.files.iter().any(|(_, b)| String::from_utf8_lossy(b).contains("private text")), "the target is not in the report");
    assert!(r.errors.iter().any(|e| e.contains("dinput8-chain.ini") && e.contains("link")), "{:?}", r.errors);
}

/// Review R2: one Greeneye id with logs in two folders (the Library entry it runs from, and a copy that stays in
/// greeneye/) gave two zip entries of one name, and "Save report" failed with "Duplicate filename".
#[test]
fn one_greeneye_id_in_two_folders_saves_as_a_zip() {
    let f = Fx::healthy("greeneye-two-folders");
    let lib = f.root.join("data/library/greeneye/unlock-map-markers/1.3.48+cccccccc");
    std::fs::create_dir_all(&lib).unwrap();
    std::fs::write(lib.join("unlock_map_markers.log"), "from the Library entry\n").unwrap();
    f.w("greeneye/unlock-map-markers/mod.json", br#"{"id":"unlock-map-markers"}"#);
    f.w("greeneye/unlock-map-markers/unlock_map_markers.log", "from the game folder copy\n");
    f.w("kiln/state/greeneye.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "plugin": "kiln-greeneye", "status": "running", "steppedAside": false, "pid": 412,
        "mods": [{"id": "unlock-map-markers", "status": "started", "folder": format!("{}", lib.display())}]})).unwrap());
    let r = build(&f.input());
    let out = f.root.join("report.zip");
    write_zip(&r, &out).expect("the report is saved");
    let mut a = zip::ZipArchive::new(std::fs::File::open(&out).unwrap()).unwrap();
    let names: Vec<String> = (0..a.len()).map(|i| a.by_index(i).unwrap().name().unwrap().to_string()).collect();
    let logs: Vec<&String> = names.iter().filter(|n| n.ends_with("unlock_map_markers.log")).collect();
    assert_eq!(logs.len(), 2, "{names:?}");
    assert!(file(&r, "greeneye-mods/unlock-map-markers/library/unlock_map_markers.log").is_some_and(|t| t.contains("from the Library entry")), "{names:?}");
    assert!(file(&r, "greeneye-mods/unlock-map-markers/game/unlock_map_markers.log").is_some_and(|t| t.contains("from the game folder copy")), "{names:?}");
}

/// Player report r1110: SET's powrprof.dll had been imported as a native mod (before the Library recognised loaders).
/// Kiln loaded it, kiln-set stood aside for it, and the report blamed "SET's own loader". It now names the native mod.
#[test]
fn native_mod_that_is_an_engines_own_loader() {
    let f = Fx::healthy("native-loader");
    let native = f.root.join("data/library/native/powrprof-00d1b1/0.0.0+abda29bc");
    std::fs::create_dir_all(&native).unwrap();
    std::fs::write(native.join("powrprof.dll"), crate::pe::build_test_dll(&["PowerGetActiveScheme"], b"VK_LAYER_SHROUDED_ENGINE_TWEAKS")).unwrap();
    f.profile(serde_json::json!(true), serde_json::json!({"native": [{"id": "powrprof-00d1b1", "version": "0.0.0", "path": native.display().to_string()}]}));
    // before a start: a warning
    let r = build(&f.input());
    let n = finding(&r, "native-loader");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.contains("native mod powrprof-00d1b1 is Shrouded Engine Tweaks's own loader (powrprof.dll)")
        && n.text.contains("Remove powrprof-00d1b1 from the profile"), "{}", n.text);
    // kiln-set stood aside for it: one problem that names the mod, not the generic own-loader finding
    f.w("kiln/state/set.json", br#"{"format":1,"plugin":"kiln-set","status":"stepped-aside","steppedAside":true,"reason":"a non-system powrprof.dll is loaded","mods":[]}"#);
    let r = build(&f.input());
    let n = finding(&r, "native-loader");
    assert_eq!(n.level, Level::Problem);
    assert!(n.text.starts_with("kiln-set stood aside at the last start: the profile's native mod powrprof-00d1b1"), "{}", n.text);
    assert!(!ids(&r).contains(&"own-loader"), "{:?}", ids(&r));
    // switched off in the profile: nothing to say
    f.profile(serde_json::json!(true), serde_json::json!({"native": [{"id": "powrprof-00d1b1", "version": "0.0.0", "path": native.display().to_string(), "enabled": false}]}));
    assert!(!ids(&build(&f.input())).contains(&"native-loader"));
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
    // every plugin follows `dropins` (dev.22): `false` reads no folder, Flame's included
    assert!(u.text.contains("mods/BiggerTorch"), "{}", u.text);
    // a list naming flame (what the launcher writes for a profile from before dev.22 without Flame mods): read
    f.profile(serde_json::json!(["flame"]), serde_json::json!({"kiln": [{"id": "chat-plus", "version": "1.0.0", "path": "x"}]}));
    let r = build(&f.input());
    let u = finding(&r, "unlisted-mods");
    assert!(u.text.contains("kiln/mods/extra-mod") && !u.text.contains("BiggerTorch"), "{}", u.text);
    // a list without flame: Flame's folder is not read, whether or not the profile has Flame mods
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

/// Player report (Nexus, 2026-11-11): a client's server mods (Loot Leveler) are "skipped" in the menu's mods.json and
/// start with the player's own world. The report says so in words, and does not count them as failed.
#[test]
fn server_mods_waiting_for_the_players_world_are_a_note() {
    let f = Fx::healthy("hosted");
    f.w("kiln/state/mods.json", format!(r#"{{"format":1,"pid":412,"written":"2026-11-10T11:00:01Z","generation":3,"mods":[
        {{"id":"loot-leveler","version":"0.1.0","state":"skipped","reason":"{}"}},
        {{"id":"enemy-leveler","version":"0.1.0","state":"skipped","reason":"{}"}}]}}"#,
        crate::profile::GAME_HOSTED_WAITING, crate::profile::GAME_HOSTED_OFF).as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"mod-failed"));
    let wait = finding(&r, "hosted-mods");
    assert_eq!(wait.level, Level::Info);
    assert_eq!(wait.text, "loot-leveler starts with the player's own world (single-player or co-op), so \"skipped\" in the main menu is \
        expected; it does not run in someone else's world.");
    let off = finding(&r, "hosted-off");
    assert_eq!(off.level, Level::Warning);
    assert!(off.text.contains("enemy-leveler") && off.text.contains("\"hosted\": false"), "{}", off.text);
    assert!(r.summary.contains("loot-leveler") && r.summary.contains("[note] loot-leveler starts with"), "{}", r.summary);
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
    f.w("shroudlink_client.log", &private);
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

// ---------------------------------------------------------------- ShroudForge's own startup pass

/// A game folder where ShroudForge's own loader 1.7.9 runs next to Kiln (kiln-shroudforge stood aside) and one
/// made-up startup mod is switched on.
fn sf_own_loader(name: &str) -> Fx {
    let f = Fx::healthy(name);
    f.w("dbghelp.dll", crate::pe::build_test_dll(&[], b"EML_LOG_FILE_PATH shroudforge"));
    f.w("shroudforge/shroudforge-runtime.dll", b"runtime");
    f.w("shroudforge/version.json", br#"{"version":"1.7.9"}"#);
    // Kiln's own ShroudForge, which would run the mods if ShroudForge's own loader did not start itself
    f.w("kiln/plugins/kiln-shroudforge.dll", b"adapter");
    f.w("kiln/plugins/shroudforge/shroudforge-runtime.dll", b"runtime");
    f.w("kiln/plugins/shroudforge/version.json", br#"{"version":"1.7.9","build":"kiln.63b8862","host":"kiln"}"#);
    f.w("kiln/state/shroudforge.json", br#"{"format":1,"mode":"self-hosted","pid":412,"side":"client","source":"installed","updated":1794308400,
        "reason":"ShroudForge's own bootstrap runs (Z:\\var\\home\\frostling\\Enshrouded\\winmm.dll)"}"#);
    f.w("mods/bone-caller/mod.json", br#"{"id":"bone-caller","name":"Bone Caller","version":"1.0.0","capabilities":["patch","runtime"]}"#);
    f.w("mods/bone-caller/extended.mod.json", br#"{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{}}"#);
    f.w("mods/bone-caller/src/mod.lua", "return {}");
    f.w("mods/sky-lamp/mod.json", br#"{"id":"sky-lamp","name":"Sky Lamp","version":"1.0.0","capabilities":["runtime"]}"#);
    f.w("mods/sky-lamp/extended.mod.json", br#"{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{}}"#);
    f.w("mods/sky-lamp/src/mod.lua", "return {}");
    f
}

/// Player report 327 (13 Nov 2026, a client under Proton): ShroudForge's own loader 1.7.9 ran next to Kiln, could not
/// write its startup mods into the game data ("pregame phase failed: ... (os error 5)", its fault under Wine and
/// Proton) and started the game without them. The launcher showed the mod as on and the report had no word of it: the
/// player had to find ShroudForge's own log. ShroudForge's record of the pass (`shroudforge/state.json` "assets") is
/// now a finding that names the mods and what to change.
#[test]
fn shroudforges_own_failed_startup_pass_is_a_finding() {
    let f = sf_own_loader("sf-pass");
    let mut i = f.input();
    i.install = Some(f.install(Platform::Proton));
    // an applied pass is nothing to find
    f.w("shroudforge/state.json", br#"{"schemaVersion":1,"assets":{"schemaVersion":1,"status":"applied","phase":"startup","target":"enshrouded",
        "completedAt":1794308414,"fingerprint":"f","mods":[{"id":"bone-caller","version":"1.0.0"}]}}"#);
    assert!(!ids(&build(&i)).contains(&"sf-startup-failed"));
    // ShroudForge quotes the system's message, in the system's language
    f.w("shroudforge/state.json", r#"{"schemaVersion":1,"assets":{"schemaVersion":1,"status":"failed","phase":"startup","target":"enshrouded",
        "completedAt":1794308414,"error":"pregame phase failed: Accès refusé. (os error 5)"}}"#);
    let r = build(&i);
    let s = finding(&r, "sf-startup-failed");
    assert_eq!(s.level, Level::Problem);
    assert!(s.text.starts_with("ShroudForge 1.7.9's own loader could not write"), "{}", s.text);
    assert!(s.text.contains("pregame phase failed: Accès refusé. (os error 5)"), "ShroudForge's own words: {}", s.text);
    assert!(s.text.contains("2026-11-10T11:00:14Z"), "when: {}", s.text);
    assert!(s.text.contains("(bone-caller)") && !s.text.contains("sky-lamp"), "only the mods that need the pass: {}", s.text);
    assert!(s.text.contains("WINEDLLOVERRIDES") && s.text.contains("dbghelp") && s.text.contains("dinput8=n,b"), "the cure: {}", s.text);
    assert!(r.summary.contains("ShroudForge's record") && r.summary.contains("failed"), "{}", r.summary);
    // on Windows the same record is said without the Wine cure
    let w = build(&f.input());
    let s = finding(&w, "sf-startup-failed");
    assert!(s.text.contains("(os error 5)") && !s.text.contains("WINEDLLOVERRIDES"), "{}", s.text);
}

/// Review of 6e231b37, finding 1. ShroudForge's words name files, so they can name the home folder. They were put on
/// one line (runs of spaces collapsed) and cut to their bound before the report redacted them: a home folder whose
/// name has two spaces no longer matched, and a cut inside a name left its beginning. The whole original text is
/// redacted first.
#[test]
fn shroudforges_words_are_redacted_before_they_are_put_on_one_line_and_cut() {
    let f = sf_own_loader("sf-redact");
    let record = |error: &str| f.w("shroudforge/state.json", serde_json::json!({"schemaVersion": 1, "assets": {
        "status": "failed", "phase": "startup", "target": "enshrouded", "completedAt": 1794308414u64, "error": error}}).to_string());
    // a home folder with two spaces in its name, as ShroudForge's error would spell it
    let mut i = f.input();
    let mut r = Redactor::new(Some(r"C:\Users\Frost  Ling"), Some("Frost  Ling"), Some("frost-desk"));
    r.add_home(&f.root.to_string_lossy());
    i.redactor = Some(r);
    record(r"pregame phase failed: C:\Users\Frost  Ling\Games\Enshrouded\enshrouded.kfc_resources: Access denied. (os error 5)");
    let rep = build(&i);
    let s = finding(&rep, "sf-startup-failed");
    assert!(s.text.contains(r"pregame phase failed: <home>\Games\Enshrouded\enshrouded.kfc_resources: Access denied. (os error 5)"), "{}", s.text);
    for text in [&s.text, &rep.summary, &rep.short] {
        assert!(!text.contains("Frost") && !text.contains("Ling"), "the name is in the report: {text}");
    }
    // a long text whose bound falls inside the user's name: no beginning of the name is left
    let long = format!("pregame phase failed: {} /var/home/frostling/Games/Enshrouded/enshrouded.kfc: denied", "x".repeat(263));
    assert_eq!(&long[..300][286..], "/var/home/fros", "the fixture: the cut is inside the name");
    record(&long);
    let rep = build(&f.input());
    let s = finding(&rep, "sf-startup-failed");
    for text in [&s.text, &rep.summary, &rep.short] {
        assert!(!text.contains("fros"), "a part of the name is in the report: {text}");
    }
    assert!(s.text.contains(" <home>/Games"), "redacted, then cut: {}", s.text);
}

/// Review of 6e231b37, finding 2: a hosted pass failed, then the player started once without mods. That start skips
/// Kiln's plugins, so the adapter's status and ShroudForge's record are still the earlier start's; the report is about
/// the last start and says nothing of it.
#[test]
fn a_start_without_shroudforge_does_not_repeat_an_earlier_starts_failed_pass() {
    let f = Fx::healthy("sf-old-boot");
    f.w("kiln/plugins/kiln-shroudforge.dll", b"adapter");
    f.w("kiln/plugins/shroudforge/shroudforge-runtime.dll", b"runtime");
    f.w("mods/bone-caller/mod.json", br#"{"id":"bone-caller","name":"Bone Caller","version":"1.0.0","capabilities":["patch","runtime"]}"#);
    f.w("mods/bone-caller/extended.mod.json", br#"{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{}}"#);
    f.w("mods/bone-caller/src/mod.lua", "return {}");
    // the start of 11:00:00 (Fx::boot, pid 412): Kiln hosted ShroudForge and its pass failed
    f.w("kiln/state/shroudforge.json", br#"{"format":1,"mode":"hosted","source":"bundled","pid":412,"side":"client","updated":1794308900,
        "bundled":{"version":"1.7.9","build":"kiln.63b8862"},"startupPrepared":true}"#);
    f.w("shroudforge/state.json", br#"{"schemaVersion":1,"assets":{"status":"failed","phase":"startup","target":"enshrouded",
        "completedAt":1794308403,"error":"pregame phase failed: could not create a new enshrouded.kfc working baseline"}}"#);
    let s = build(&f.input());
    assert!(finding(&s, "sf-startup-failed").text.starts_with("ShroudForge 1.7.9, hosted by Kiln, could not write"), "that start's failure");
    // the next start, an hour later: play once without mods (the same pid, as under Proton; another, as on Windows)
    for pid in [412, 977] {
        f.w("kiln/state/boot.json", serde_json::to_vec(&serde_json::json!({
            "format": 1, "kiln": "0.2.0-dev.22", "pid": pid, "started": "2026-11-10T12:00:00Z", "written": "2026-11-10T12:00:02Z", "side": "client",
            "exe": {"name": "enshrouded.exe", "verified": true}, "policy": "verified-only", "hooks": true, "safeIdle": null,
            "playOnce": {"mode": "no-mods", "consumed": true, "nonce": null}, "profile": {"mode": "no-mods"},
            "paths": {"game": r"Z:\var\home\frostling\Enshrouded"}, "chain": [], "plugins": [], "errors": [],
        })).unwrap());
        let r = build(&f.input());
        assert!(!r.findings.iter().any(|x| x.id == "sf-startup-failed"), "pid {pid}: {:#?}", r.findings);
        assert!(!r.summary.contains("ShroudForge's record of its startup pass"), "pid {pid}: {}", r.summary);
    }
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
