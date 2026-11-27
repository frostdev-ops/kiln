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
fn redacts_other_players_names_in_kiln_and_shroudlink_lines() {
    let r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    let cases = [
        ("[I 22:31:42.935] [service:kiln-players.dll] players: joined slot 1 entity 7 'Brim Wyn' (steam 76561198000000002, admin 0, perms 0x1) [event 2]",
         "[I 22:31:42.935] [service:kiln-players.dll] players: joined slot 1 entity 7 '<player>' (steam <steamid>, admin 0, perms 0x1) [event 2]"),
        ("[I 22:31:36.187] [kiln] net: Brim (0x81) joined with Kiln 0.2.0-dev.29.19: 6 mod(s), 13 shared channel(s)",
         "[I 22:31:36.187] [kiln] net: <player> (0x81) joined with Kiln 0.2.0-dev.29.19: 6 mod(s), 13 shared channel(s)"),
        ("[I 1] [kiln] net: peer Al (0x82) left (in game)", "[I 1] [kiln] net: peer <player> (0x82) left (in game)"),
        ("[W 1] [kiln] net: kicking Mia (0x83) by admin: spam", "[W 1] [kiln] net: kicking <player> (0x83) by admin: spam"),
        ("[W 1] [kiln] net: refusing Mia (Kiln 0.2.0-dev.20): too old", "[W 1] [kiln] net: refusing <player> (Kiln 0.2.0-dev.20): too old"),
        ("[I 1] [kiln] net: peer Mia said bye", "[I 1] [kiln] net: peer <player> said bye"),
        ("[W 1] [kiln] net: peer Mia: dropped frame (late); 1 dropped so far", "[W 1] [kiln] net: peer <player>: dropped frame (late); 1 dropped so far"),
        ("[I 1] [kiln] net: config state 0a0b0c0d (3 config(s)) sent to Mia", "[I 1] [kiln] net: config state 0a0b0c0d (3 config(s)) sent to <player>"),
        ("[W 1] [kiln] Kiln.Saves: Mia has no character id on this build: x", "[W 1] [kiln] Kiln.Saves: <player> has no character id on this build: x"),
        ("[1794424596] shroudlink: client 2eaa65d2d4c437f9 is Ember Fox", "[1794424596] shroudlink: client 2eaa65d2d4c437f9 is <player>"),
        ("[1] world: client 2eaa65d2d4c437f9 is player 1 (the only unmatched one) (Ember Fox)",
         "[1] world: client 2eaa65d2d4c437f9 is player 1 (the only unmatched one) (<player>)"),
        ("[1] world: client 2eaa65d2d4c437f9 is player 2 (Mia)", "[1] world: client 2eaa65d2d4c437f9 is player 2 (<player>)"),
    ];
    for (raw, want) in cases {
        assert_eq!(redact(raw, &r), want);
    }
    // lines that name no player stay as they are
    for keep in [
        "[1] shroudlink: client 2eaa65d2d4c437f9 is addon 0.8.0, UI protocol 3",
        "[1] shroudlink: client 2eaa65d2d4c437f9 is an addon older than 0.6 (no hello)",
        "[1] world: client 2eaa65d2d4c437f9 is player 1 (the only unmatched one)",
        "[I 1] [kiln] net: last-server.json: welcome from srv-76bd0efb99c3",
        "[I 1] [kiln] net: lab peer 0x80 kicked (reason 2)",
        "[I 1] [service:kiln-players.dll] players: server world session ended (quit): 2 player(s) left with it",
    ] {
        assert_eq!(redact(keep, &r), keep);
    }
    // the names a log shows also go where a line does not say it is a player
    let log = "players: joined slot 1 entity 7 'Brimwyn' (steam 0, admin 0, perms 0x1)\nnet: Al (0x9) joined with Kiln x\n";
    assert_eq!(player_names(log), vec!["Brimwyn".to_string(), "Al".to_string()]);
    let mut r2 = r.clone();
    r2.add_players_from(log);
    assert_eq!(redact("chat-plus: welcome back, Brimwyn! Also Al.", &r2), "chat-plus: welcome back, <player>! Also Al.",
        "a name of 2 characters only where a line says it is a player");
    assert_eq!(redact("Brimwynne", &r2), "Brimwynne", "whole words only");
    // the local player keeps the Steam name token
    let mut me = r.clone();
    me.add_whole_word("Frosty", "<steam-name>");
    assert_eq!(redact("players: joined slot 0 entity 1 'Frosty' (steam 0, admin 1, perms 0x3f)", &me),
        "players: joined slot 0 entity 1 '<steam-name>' (steam 0, admin 1, perms 0x3f)");
}

#[test]
fn redacts_ipv6_macs_mails_and_plain_text_secrets() {
    let r = Redactor::new(Some(HOME), Some("frostling"), Some("frost-desk"));
    assert_eq!(redact("join [2001:db8:85a3::8a2e:370:7334]:15636 or fe80::1c2d:3e4f%eth0", &r), "join [<ip>]:15636 or <ip>%eth0");
    assert_eq!(redact("2001:0db8:0000:0000:0000:ff00:0042:8329 then", &r), "<ip> then");
    assert_eq!(redact("pad 00:1a:2b:3c:4d:5e connected", &r), "pad <mac> connected");
    assert_eq!(redact("mail frost.ling+kiln@example.co.uk.", &r), "mail <email>.");
    for keep in [
        "[I 22:30:56.603] at 12:30:45", "loopback ::1 and ::", "keen::ItemCategory abc::def", "kdh1:8f21dcb1097232d5",
        "kiln.world@4 period@resolved", "Kiln.Host 0.2.0.0 (mod API 0.2.10)", "@types/node", "deadbeef:cafe",
    ] {
        assert_eq!(redact(keep, &r), keep);
    }
    assert_eq!(redact("join 0.2.0.0 and 10.0.0.2", &r), "join 0.2.0.0 and <ip>", "0.x.x.x is a version, never a host");
    // plain text and INI: the value after a secret's name
    assert_eq!(redact("[Server]\nPassword=hunter2\nApiKey = abc123\nShortcutKey=0x2D\n", &r),
        "[Server]\nPassword=<redacted>\nApiKey = <redacted>\nShortcutKey=0x2D\n");
    assert_eq!(redact("GET /x?token=abc&page=2 token: abc, server_password: 'p w'", &r),
        "GET /x?token=<redacted>&page=2 token: <redacted>, server_password: '<redacted>'");
    assert_eq!(redact("%command% -password hunter2 --apikey x -log", &r), "%command% -password <redacted> --apikey <redacted> -log");
    for keep in [
        "the admin token and endpoint", "\"token\": \"<redacted>\",", "\"tokens\": [", "password=", "credential::Store",
        "client-checks pass: pre", "fields named like passwords, tokens, keys",
    ] {
        assert_eq!(redact(keep, &r), keep);
    }
    // a report redacted twice does not change
    let once = redact("token=abc fe80::1 a@b.org players: joined slot 1 entity 7 'Mia' (steam 0)", &r);
    assert_eq!(redact(&once, &r), once);
}

#[test]
fn json_fields_that_name_people_are_redacted() {
    for k in ["userName", "user_name", "accountName", "personaName", "playerName", "character_name", "email", "contactEmail", "api-key"] {
        assert!(is_secret_key(k), "{k}");
    }
    for k in ["name", "displayName", "modName", "keybind", "players", "emailVerifiedAt"] {
        assert!(!is_secret_key(k), "{k}");
    }
}

// ---------------------------------------------------------------- the local Steam account, on every Steam layout

const LOGINUSERS: &str = "\"users\"\n{\n\t\"76561198000000009\"\n\t{\n\t\t\"AccountName\"\t\t\"oryx_acct\"\n\t\t\"PersonaName\"\t\t\"Oryx593\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n";

/// A fake home with one Steam root at `rel` (with `steamapps/`, as `steam::roots_at` wants), its loginusers.vdf.
fn steam_home(name: &str, user: &str, rel: &str, loginusers: Option<&str>) -> (PathBuf, PathBuf) {
    let base = std::env::temp_dir().join(format!("km-report-steam-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&base);
    let home = base.join(user);
    let root = home.join(rel);
    std::fs::create_dir_all(root.join("steamapps")).unwrap();
    std::fs::create_dir_all(root.join("config")).unwrap();
    if let Some(t) = loginusers {
        std::fs::write(root.join("config/loginusers.vdf"), t).unwrap();
    }
    (base, home)
}

fn steam_redactor(home: &Path) -> Redactor {
    let roots = crate::steam::roots_at(home);
    assert_eq!(roots.len(), 1, "the fixture's Steam root is found: {roots:?}");
    let mut r = Redactor::new(Some(&home.to_string_lossy()), None, None);
    r.add_steam_accounts(&roots, home);
    r
}

/// Every place a Linux report shows the local Steam names (oryx_acct, Oryx593): none may survive.
fn assert_no_steam_names(r: &Redactor, layout: &str) {
    for text in [
        "[I 22:31:42.935] [service:kiln-players.dll] players: joined slot 0 entity 1 'Oryx593' (steam 76561198000000009, admin 1, perms 0x3f) [event 1]",
        "[I 22:31:36.187] [kiln] net: Oryx593 (0x80) joined with Kiln 0.2.0-dev.29.21: 6 mod(s)",
        "[1794424596] shroudlink: client 2eaa65d2d4c437f9 is Oryx593",
        "[1] world: client 2eaa65d2d4c437f9 is player 1 (the only unmatched one) (ORYX593)",
        "[I 1] [mod:chat-plus] welcome back, oryx593! saved Oryx593_world.json and oryx593-backup",
        "Folder: /run/media/oryx_acct/Games/SteamLibrary/steamapps/common/Enshrouded",
        r"save dir Z:\run\media\Oryx_Acct\Saves",
    ] {
        let out = redact(text, r);
        assert!(!out.to_ascii_lowercase().contains("oryx"), "{layout}: {text}\n  -> {out}");
    }
}

#[test]
fn the_local_steam_names_are_replaced_on_every_steam_layout() {
    for (name, user, rel) in [
        ("native", "oryx", ".local/share/Steam"),
        ("debian", "oryx", ".steam/debian-installation"),
        ("flatpak", "oryx", ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
        ("flatpak-data", "oryx", ".var/app/com.valvesoftware.Steam/data/Steam"),
        ("snap", "oryx", "snap/steam/common/.local/share/Steam"),
        // a Steam Deck: the user is `deck`, a word Kiln's files are full of, so only Steam's names say who it is
        ("deck", "deck", ".local/share/Steam"),
    ] {
        let (base, home) = steam_home(name, user, rel, Some(LOGINUSERS));
        assert_no_steam_names(&steam_redactor(&home), name);
        let _ = std::fs::remove_dir_all(base);
    }
}

#[test]
fn the_signed_in_persona_comes_from_localconfig_when_loginusers_does_not_have_it() {
    // loginusers.vdf without the account (a fresh Steam, a login that is not remembered): userdata/<id>/config/localconfig.vdf
    // still names the signed-in account's persona, and Steam's registry.vdf its login name
    let (base, home) = steam_home("localconfig", "deck", ".local/share/Steam", None);
    let root = home.join(".local/share/Steam");
    std::fs::create_dir_all(root.join("userdata/39734281/config")).unwrap();
    std::fs::write(root.join("userdata/39734281/config/localconfig.vdf"),
        "\"UserLocalConfigStore\"\n{\n\t\"friends\"\n\t{\n\t\t\"PersonaName\"\t\t\"Oryx593\"\n\t}\n}\n").unwrap();
    std::fs::write(home.join(".steam/registry.vdf").tap_dir(),
        "\"Registry\"\n{\n\t\"HKCU\"\n\t{\n\t\t\"Software\"\n\t\t{\n\t\t\t\"Valve\"\n\t\t\t{\n\t\t\t\t\"Steam\"\n\t\t\t\t{\n\t\t\t\t\t\"AutoLoginUser\"\t\t\"oryx_acct\"\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n").unwrap();
    assert_no_steam_names(&steam_redactor(&home), "localconfig");
    let _ = std::fs::remove_dir_all(base);
}

trait TapDir {
    fn tap_dir(self) -> Self;
}
impl TapDir for PathBuf {
    /// Creates the parent folder, for a write.
    fn tap_dir(self) -> Self {
        std::fs::create_dir_all(self.parent().unwrap()).unwrap();
        self
    }
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
            // a Steam install (the usual player): a custom folder starts directly and has no Steam launch options
            source: "steam".into(),
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

fn sf_report_mod(f: &Fx, folder: &str, id: &str, version: &str) {
    f.w(&format!("{folder}/mod.json"), serde_json::to_vec(&serde_json::json!({
        "id": id, "name": "Unlimited Flight", "version": version, "capabilities": ["runtime"],
    })).unwrap());
    f.w(&format!("{folder}/extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"launcher":"SF"}"#);
}

const SF_OWN: &str = "mods/my-flight-copy";
const SF_BUNDLED: &str = "kiln/plugins/shroudforge/mods/bundled-flight";

#[test]
fn sf_mod_bundled_newer_notes_older_copy_by_manifest_id_without_changing_it() {
    let f = Fx::healthy("sf-bundled-older");
    sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
    sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
    let paths: Vec<_> = [SF_OWN, SF_BUNDLED].into_iter().flat_map(|d| ["mod.json", "extended.mod.json"]
        .map(|n| f.game.join(d).join(n))).collect();
    let before: Vec<_> = paths.iter().map(|p| std::fs::read(p).unwrap()).collect();

    let r = build(&f.input());
    let found: Vec<_> = r.findings.iter().filter(|x| x.id == "sf-mod-bundled-newer").collect();
    assert_eq!(found.len(), 1, "{:?}", r.findings);
    assert_eq!(found[0].level, Level::Info);
    assert_eq!(found[0].text, "sf-unlimited-flight 1.2.0 is older than the 1.3.0 Kiln brings.");
    assert!(r.summary.contains(&format!("[note] {}", found[0].text)));
    let json: serde_json::Value = serde_json::from_str(file(&r, "report.json").unwrap()).unwrap();
    let note = json["findings"].as_array().unwrap().iter().find(|x| x["id"] == "sf-mod-bundled-newer").unwrap();
    assert_eq!(note["level"], "info", "notes use the existing serialized level");
    assert!(r.errors.is_empty(), "{:?}", r.errors);
    assert_eq!(paths.iter().map(|p| std::fs::read(p).unwrap()).collect::<Vec<_>>(), before);
    assert!(!r.files.iter().any(|(n, _)| n.ends_with("/mod.json") || n.ends_with("/extended.mod.json")));
}

#[test]
fn sf_mod_bundled_newer_equal_versions_do_not_fire() {
    let f = Fx::healthy("sf-bundled-equal");
    for (own, bundled) in [("1.3.0", "1.3.0"), ("1.3.0+player", "1.3.0+kiln")] {
        sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", own);
        sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", bundled);
        let r = build(&f.input());
        assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{:?}", r.findings);
        assert!(r.errors.is_empty(), "{:?}", r.errors);
    }
}

#[test]
fn sf_mod_bundled_newer_newer_copy_does_not_fire() {
    let f = Fx::healthy("sf-bundled-newer");
    sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.4.0");
    sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
    let r = build(&f.input());
    assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{:?}", r.findings);
    assert!(r.errors.is_empty(), "{:?}", r.errors);
}

#[test]
fn sf_mod_bundled_newer_uses_semver_numeric_and_prerelease_precedence() {
    let f = Fx::healthy("sf-bundled-semver");
    for (own, bundled, fires) in [("1.9.0", "1.10.0", true), ("1.10.0", "1.9.0", false),
        ("1.3.0-rc.2", "1.3.0-rc.10", true), ("1.3.0-rc.1", "1.3.0", true), ("1.3.0", "1.3.0-rc.1", false)] {
        sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", own);
        sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", bundled);
        let r = build(&f.input());
        assert_eq!(r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), fires, "{own} vs {bundled}: {:?}", r.findings);
        assert!(r.errors.is_empty(), "{:?}", r.errors);
    }
}

#[test]
fn sf_mod_bundled_newer_no_matching_bundled_copy_does_not_fire() {
    let f = Fx::healthy("sf-bundled-absent");
    sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
    let r = build(&f.input());
    assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"));
    assert!(r.errors.is_empty(), "{:?}", r.errors);
    // Even a matching folder name is not a matching manifest id (ids are case-sensitive in this format).
    sf_report_mod(&f, "kiln/plugins/shroudforge/mods/my-flight-copy", "SF-Unlimited-Flight", "1.3.0");
    let r = build(&f.input());
    assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"));
    assert!(r.errors.is_empty(), "{:?}", r.errors);
}

#[test]
fn sf_mod_bundled_newer_bad_manifests_on_either_side_are_not_readable_not_findings() {
    for folder in [SF_OWN, SF_BUNDLED] {
        for fault in ["missing", "directory", "json", "id", "version", "semver", "extended"] {
            let f = Fx::healthy("sf-bundled-bad-manifest");
            sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
            sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
            let manifest = format!("{folder}/mod.json");
            match fault {
                "missing" => f.rm(&manifest),
                "directory" => {
                    f.rm(&manifest);
                    std::fs::create_dir(f.game.join(&manifest)).unwrap();
                }
                "json" => f.w(&manifest, b"not JSON"),
                "id" => f.w(&manifest, br#"{"version":"1.2.0"}"#),
                "version" => f.w(&manifest, br#"{"id":"sf-unlimited-flight"}"#),
                "semver" => f.w(&manifest, br#"{"id":"sf-unlimited-flight","version":"broken"}"#),
                "extended" => f.w(&format!("{folder}/extended.mod.json"), b"not JSON"),
                _ => unreachable!(),
            }
            let r = build(&f.input());
            assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{folder}: {fault}: {:?}", r.findings);
            assert!(r.errors.iter().any(|e| e.contains(folder)), "{folder}: {fault}: {:?}", r.errors);
            assert!(r.summary.contains("== Not readable =="), "{}", r.summary);
            assert!(!r.summary.contains(&f.root.to_string_lossy().to_string()), "paths must be redacted");
        }
    }
}

#[cfg(unix)]
#[test]
fn sf_mod_bundled_newer_linked_inputs_are_not_followed() {
    for folder in [SF_OWN, SF_BUNDLED] {
        for input in ["root", "folder", "mod.json", "extended.mod.json"] {
            let f = Fx::healthy("sf-bundled-link");
            sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
            sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
            let path = match input {
                "root" => f.game.join(folder).parent().unwrap().to_path_buf(),
                "folder" => f.game.join(folder),
                name => f.game.join(folder).join(name),
            };
            let outside = f.root.join("linked-input");
            std::fs::rename(&path, &outside).unwrap();
            std::os::unix::fs::symlink(&outside, &path).unwrap();
            let r = build(&f.input());
            assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{folder}: {input}: {:?}", r.findings);
            assert!(r.errors.iter().any(|e| e.contains("link")), "{folder}: {input}: {:?}", r.errors);
        }
    }
}

#[test]
fn sf_mod_bundled_newer_nonstandard_versions_are_not_guessed() {
    let f = Fx::healthy("sf-bundled-version-edges");
    for version in ["v1.2.0", "V1.2.0", "1.2", "1", "release-one", "1.2.0-01", "1.2.0-rc.1+build", "2147483648.0.0"] {
        for (own, bundled) in [(version, "2147483647.0.0"), ("0.0.0", version)] {
            sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", own);
            sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", bundled);
            let r = build(&f.input());
            assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{own} vs {bundled}: {:?}", r.findings);
            assert!(!r.errors.is_empty(), "{own} vs {bundled}");
        }
    }
}

#[test]
fn sf_mod_bundled_newer_bad_manifest_does_not_hide_other_findings() {
    let f = Fx::healthy("sf-bundled-other-findings");
    sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
    sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
    sf_report_mod(&f, "mods/aaa-broken", "broken", "1.0.0");
    f.w("mods/aaa-broken/mod.json", b"not JSON");
    f.rm("kiln/kiln-core.dll");
    let r = build(&f.input());
    assert_eq!(finding(&r, "sf-mod-bundled-newer").text, "sf-unlimited-flight 1.2.0 is older than the 1.3.0 Kiln brings.");
    finding(&r, "files-missing");
    assert!(r.errors.iter().any(|e| e.contains("aaa-broken")), "{:?}", r.errors);
}

#[cfg(unix)]
#[test]
fn sf_mod_bundled_newer_detection_does_not_follow_links_or_read_native_payloads() {
    for input in ["native-plugin.ini", "payload.dll", "src"] {
        let f = Fx::healthy("sf-bundled-detection-link");
        sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
        sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
        f.rm(&format!("{SF_OWN}/extended.mod.json"));
        let outside = f.root.join("outside-game");
        std::fs::create_dir(&outside).unwrap();
        match input {
            "native-plugin.ini" => {
                std::fs::write(outside.join(input), b"[Plugin]\nDll=bin/sf.dll\n").unwrap();
            }
            "payload.dll" => {
                f.w(&format!("{SF_OWN}/native-plugin.ini"), b"[Plugin]\nDll=payload.dll\n");
                std::fs::write(outside.join(input), crate::pe::build_test_dll(&["SfPlugin"], b"")).unwrap();
            }
            "src" => {
                std::fs::create_dir(outside.join(input)).unwrap();
                std::fs::write(outside.join(input).join("mod.lua"), b"return {}").unwrap();
            }
            _ => unreachable!(),
        }
        std::os::unix::fs::symlink(outside.join(input), f.game.join(SF_OWN).join(input)).unwrap();
        let r = build(&f.input());
        assert!(!r.findings.iter().any(|x| x.id == "sf-mod-bundled-newer"), "{input}: {:?}", r.findings);
    }
}

#[test]
fn sf_mod_bundled_newer_ignores_unrelated_mod_folders() {
    let f = Fx::healthy("sf-bundled-other-engines");
    f.w("mods/native-only/native-plugin.ini", b"[Plugin]\nDll=mod.dll\n");
    f.w("mods/emf/mod.json", br#"{"id":"emf-mod","name":"EMF mod","version":"v1","dll":"mod.dll"}"#);
    let r = build(&f.input());
    assert!(r.errors.is_empty(), "{:?}", r.errors);
}

#[test]
fn sf_mod_bundled_newer_recognizes_legacy_lua_and_schema_metadata() {
    for marker in ["schema", "mod.lua", "src/mod.lua"] {
        let f = Fx::healthy("sf-bundled-legacy-metadata");
        sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
        sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
        f.rm(&format!("{SF_OWN}/extended.mod.json"));
        if marker == "schema" {
            f.w(&format!("{SF_OWN}/mod.json"), br#"{"id":"sf-unlimited-flight","name":"Flight","version":"1.2.0","$schema":"https://example.invalid/shroudforge/mod.json"}"#);
        } else {
            f.w(&format!("{SF_OWN}/{marker}"), b"return {}");
        }
        let r = build(&f.input());
        finding(&r, "sf-mod-bundled-newer");
        assert!(r.errors.is_empty(), "{marker}: {:?}", r.errors);
    }
}

#[cfg(not(windows))]
#[test]
fn sf_mod_bundled_newer_uses_windows_folder_case_under_proton() {
    let f = Fx::healthy("sf-bundled-proton-case");
    sf_report_mod(&f, "MoDs/My-Flight-Copy", "sf-unlimited-flight", "1.2.0");
    sf_report_mod(&f, "kiln/PlUgInS/ShRoUdFoRgE/MoDs/Bundled-Flight", "sf-unlimited-flight", "1.3.0");
    let mut input = f.input();
    input.install.as_mut().unwrap().platform = Platform::Proton;
    let r = build(&input);
    assert_eq!(finding(&r, "sf-mod-bundled-newer").text, "sf-unlimited-flight 1.2.0 is older than the 1.3.0 Kiln brings.");
    assert!(r.errors.is_empty(), "{:?}", r.errors);
}

#[cfg(windows)]
#[test]
fn sf_mod_bundled_newer_handles_windows_verbatim_paths_and_folder_case() {
    let f = Fx::healthy("sf-bundled-windows-path");
    sf_report_mod(&f, "MoDs/My-Flight-Copy", "sf-unlimited-flight", "1.2.0");
    sf_report_mod(&f, "KiLn/PlUgInS/ShRoUdFoRgE/MoDs/Bundled-Flight", "sf-unlimited-flight", "1.3.0");
    let mut input = f.input();
    let game = f.game.canonicalize().unwrap();
    assert!(game.to_string_lossy().starts_with(r"\\?\"), "{}", game.display());
    input.install.as_mut().unwrap().path = game;
    let r = build(&input);
    assert_eq!(finding(&r, "sf-mod-bundled-newer").text, "sf-unlimited-flight 1.2.0 is older than the 1.3.0 Kiln brings.");
    assert!(r.errors.is_empty(), "{:?}", r.errors);
}

#[test]
fn sf_mod_bundled_newer_privacy_notice_discloses_manifest_metadata() {
    let f = Fx::healthy("sf-bundled-privacy-notice");
    sf_report_mod(&f, SF_OWN, "sf-unlimited-flight", "1.2.0");
    sf_report_mod(&f, SF_BUNDLED, "sf-unlimited-flight", "1.3.0");
    let r = build(&f.input());
    assert!(r.summary.contains("manifest ids and versions"), "{}", r.summary);
    let preview = contents(&r);
    let notice = preview.never.iter().find(|x| x.id == "mods").unwrap();
    assert!(notice.text.contains("manifest ids and versions"), "{}", notice.text);
}

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

/// (0.2.0-dev.29.18) The facts a public post may name: Kiln's version, the system, the game's build and how many mods
/// are on (never which), also in report.json.
#[test]
fn the_public_facts_name_versions_and_a_mod_count_not_the_mods() {
    let f = Fx::healthy("public-facts");
    let r = build(&f.input());
    assert_eq!(r.facts.kiln.as_deref(), Some("0.2.0-dev.22"));
    assert_eq!(r.facts.mods_on, Some(1));
    assert!(!r.facts.os.is_empty());
    let json: serde_json::Value = serde_json::from_str(file(&r, "report.json").unwrap()).unwrap();
    // the relay's shape (docs/relay-api.md): {kiln, launcher, os, runner, gameBuild, modsOn, deck}
    let v = &json["versions"];
    assert_eq!((v["kiln"].as_str(), v["modsOn"].as_u64(), v["deck"].as_bool()), (Some("0.2.0-dev.22"), Some(1), Some(false)));
    assert_eq!(v["runner"], "Windows");
    assert!(v.get("launcher").is_none(), "the tool here is not a launcher's");
    assert!(!v.to_string().contains("chat-plus"), "{v}");
    // with the launcher as the tool, its version
    let mut input = f.input();
    input.tool = "kiln-launcher 0.2.0-dev.29.18 beta 3".into();
    let r = build(&input);
    assert_eq!(r.facts.versions()["launcher"], "0.2.0-dev.29.18");
    // report.json alone for a post without the zip: the launcher's version, the findings, the versions, nothing else
    // (not when it was written, not the test build's number: the post shows neither)
    let alone: serde_json::Value = serde_json::from_slice(&public_json(&r)).unwrap();
    let mut keys: Vec<&str> = alone.as_object().unwrap().keys().map(String::as_str).collect();
    keys.sort();
    assert_eq!(keys, ["findings", "format", "tool", "versions"]);
    assert_eq!((alone["format"].as_u64(), alone["tool"].as_str()), (Some(1), Some("kiln-launcher 0.2.0-dev.29.18")));
}

/// (0.2.0-dev.29.18) The zip in memory is the zip on disk: the same files with the same bytes (the launcher sends it
/// without writing it to a shared temp folder).
#[cfg(feature = "zip")]
#[test]
fn the_zip_in_memory_holds_what_the_zip_on_disk_holds() {
    let f = Fx::healthy("zip-bytes");
    let r = build(&f.input());
    let bytes = zip_bytes(&r).unwrap();
    let mut z = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
    assert_eq!(z.len(), r.files.len());
    for (name, data) in &r.files {
        let mut got = Vec::new();
        std::io::Read::read_to_end(&mut z.by_name(name).unwrap(), &mut got).unwrap();
        assert_eq!(&got, data, "{name}");
    }
}

/// OptiScaler next to the game (an upscaler with its own overlay, inbox 80): one Info finding with what Kiln's overlay
/// does next to it, its ini in the zip, nothing found when it is not there.
#[test]
fn optiscaler_is_named_with_what_kilns_overlay_does() {
    let f = Fx::healthy("optiscaler");
    f.w("OptiScaler.ini", "[Menu]\nOverlayMenu=auto\n");
    let r = build(&f.input());
    assert!(!r.findings.iter().any(|x| x.id == "optiscaler"), "its ini alone is no OptiScaler: {:#?}", r.findings);
    f.w("winmm.dll", crate::pe::build_test_dll(&["timeGetTime"], b"OptiScaler.ini"));
    let r = build(&f.input());
    assert_eq!(ids(&r), Vec::<&str>::new(), "an Info finding only: {}", r.summary);
    let x = finding(&r, "optiscaler");
    assert!(x.text.contains("OptiScaler is installed (winmm.dll)") && x.text.contains("steps aside"), "{}", x.text);
    assert!(r.summary.contains("Graphics tool: OptiScaler (winmm.dll): Kiln's overlay drawing steps aside"), "{}", r.summary);
    assert!(r.summary.contains("winmm.dll        OptiScaler"), "{}", r.summary);
    assert!(file(&r, "game/OptiScaler.ini").is_some_and(|t| t.contains("OverlayMenu=auto")));
    assert!(file(&r, "game/OptiScaler.log").is_none() && r.errors.is_empty(), "no log: nothing, no error: {:?}", r.errors);
    f.w("OptiScaler.ini", "[Menu]\nOverlayMenu=false\n");
    let r = build(&f.input());
    assert!(finding(&r, "optiscaler").text.contains("draws next to it"));
    assert!(r.summary.contains("Graphics tool: OptiScaler (winmm.dll): Kiln's overlay draws next to it (its own menu and frame generation are off"), "{}", r.summary);
    // Inbox 80: its menu off, frame generation set: Kiln steps aside, and the finding says why
    f.w("OptiScaler.ini", "[FrameGen]\r\nFGInput=upscaler\r\nFGOutput=fsrfg\r\n[Menu]\r\nOverlayMenu=false\r\n");
    let r = build(&f.input());
    let x = finding(&r, "optiscaler");
    assert!(x.text.contains("frame generation is set") && x.text.contains("steps aside"), "{}", x.text);
    assert!(r.summary.contains("Graphics tool: OptiScaler (winmm.dll): Kiln's overlay drawing steps aside"), "{}", r.summary);
    // the DLL as it really is: ReShade's texts first, its own later (29.17 named it "ReShade (text reshade)")
    let mut text = b"ReShade 6.4 reshade64.dll ".to_vec();
    text.extend(std::iter::repeat_n(0u8, 2048));
    text.extend("OptiScaler.ini".encode_utf16().flat_map(|c| c.to_le_bytes()));
    f.w("winmm.dll", crate::pe::build_test_dll(&["timeGetTime"], &text));
    let r = build(&f.input());
    assert!(r.summary.contains("winmm.dll        OptiScaler") && !r.summary.contains("winmm.dll        ReShade"), "{}", r.summary);
    f.w("OptiScaler.ini", "[Menu]\nOverlayMenu=false\n");
    // its log can grow large (debug level): only its tail, within a log's budget, as for the other tools' logs
    let old = "[info] an earlier line of OptiScaler's log\n";
    f.w("OptiScaler.log", old.repeat(SMALL_LOG_MAX as usize / old.len() + 1) + "[info] HookDevice: the newest line\n");
    let r = build(&f.input());
    let log = file(&r, "game/OptiScaler.log").expect("its log is in the zip");
    assert!(log.starts_with("... (older lines left out") && log.len() as u64 <= SMALL_LOG_MAX + 200, "{}", log.len());
    assert!(log.contains("the newest line"));
    // a Linux folder is case sensitive: the files under their names on disk
    std::fs::remove_file(f.game.join("OptiScaler.ini")).unwrap();
    std::fs::rename(f.game.join("OptiScaler.log"), f.game.join("optiscaler.log")).unwrap();
    f.w("optiscaler.ini", "[menu]\noverlaymenu=false\n");
    let r = build(&f.input());
    assert!(finding(&r, "optiscaler").text.contains("draws next to it"));
    assert!(file(&r, "game/optiscaler.ini").is_some_and(|t| t.contains("overlaymenu=false")), "{:?}", r.files.iter().map(|x| &x.0).collect::<Vec<_>>());
    assert!(file(&r, "game/optiscaler.log").is_some() && r.errors.is_empty(), "{:?}", r.errors);
}

// The state files of Kiln's deliberate stops (roster, the mods' NPCs and quests not built) go into the zip whole, the
// next start's .last copies too: a support thread can tell which stop it was (dev.29).
#[test]
fn stop_state_files_are_in_the_report() {
    let f = Fx::healthy("stops");
    let names = ["roster-refused.json", "content-refused.json", "content-refused.last.json"];
    for n in names {
        f.w(&format!("kiln/state/{n}"), br#"{"format":1,"refused":true,"exitCode":19203,"unixExitStatus":3,"mods":["cs-good"]}"#);
    }
    let r = build(&f.input());
    for n in names {
        assert!(file(&r, &format!("game/kiln/state/{n}")).is_some_and(|t| t.contains("cs-good")), "{n} missing");
    }
}

// the interpreter's launch plans and kiln-eml's ownership records (newest three of each) go in
#[test]
fn the_interpreters_launch_plans_and_ownership_records_are_in_the_report() {
    let f = Fx::healthy("sfi-plans");
    for i in 0..4 {
        f.w(&format!("kiln/launch-plans/plan-{i}.json"), format!(r#"{{"format":1,"planId":"plan-{i}"}}"#).as_bytes());
        let t = std::time::SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(1_700_000_000 + i * 60);
        std::fs::File::options().write(true).open(f.game.join(format!("kiln/launch-plans/plan-{i}.json"))).unwrap().set_modified(t).unwrap();
    }
    f.w("kiln/launch-plans/.tmp-plan.json", b"{}");
    f.w("kiln/eml/ownership/enshrouded.json", br#"{"format":1,"receipt":"r"}"#);
    let r = build(&f.input());
    for i in 1..4 {
        assert!(file(&r, &format!("game/kiln/launch-plans/plan-{i}.json")).is_some_and(|t| t.contains(&format!("plan-{i}"))), "plan-{i} missing");
    }
    assert!(file(&r, "game/kiln/launch-plans/plan-0.json").is_none(), "only the newest three plans");
    assert!(file(&r, "game/kiln/launch-plans/.tmp-plan.json").is_none());
    assert!(file(&r, "game/kiln/eml/ownership/enshrouded.json").is_some());
}

#[test]
fn a_test_build_names_its_channel_and_beta_number() {
    let f = Fx::healthy("channel");
    let mut input = f.input();
    input.tool = "kiln-launcher 0.2.0-dev.29 beta 3".into();
    input.channel = Some(ReportChannel { build: "beta".into(), beta: Some(3), follows: "beta".into() });
    let r = build(&input);
    assert!(r.summary.contains("Channel:       Beta, test build 3 (follows Beta)"), "{}", r.summary);
    assert!(r.short.contains("kiln-launcher 0.2.0-dev.29 beta 3;"), "{}", r.short);
    assert!(r.short.contains("Channel: Beta, test build 3 (follows Beta)"), "{}", r.short);
    let json: serde_json::Value = serde_json::from_str(file(&r, "report.json").unwrap()).unwrap();
    assert_eq!(json["channel"], serde_json::json!({"build": "beta", "beta": 3, "follows": "beta"}));
    // a release build that follows the release; no channel (kilnctl): no line
    let words = ReportChannel { build: "dev".into(), beta: None, follows: "dev".into() }.words();
    assert_eq!(words, "Release (follows Release)");
    let r = build(&f.input());
    assert!(!r.summary.contains("Channel:"), "{}", r.summary);
    assert!(file(&r, "report.json").unwrap().contains("\"channel\": null"));
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

/// netjunk's report (2026-11-20): Building Tools' own logs were in the game folder's listing only. Other mods' and
/// loaders' logs that sit in the game folder are in the zip now (Building Tools' BuildingTools.log and .prev.log,
/// ShroudLink's core shroudlink.log, Auto Loot Plus' auto-loot-plus.log): each optional, a bounded tail, redacted; their
/// settings files are not read.
#[test]
fn other_mods_logs_in_the_game_folder_are_optional_bounded_and_redacted() {
    let f = Fx::healthy("other-mod-logs");
    let names = ["BuildingTools.log", "BuildingTools.prev.log", "shroudlink.log", "auto-loot-plus.log"];
    let absent = build(&f.input());
    for n in names {
        assert!(file(&absent, &format!("game/{n}")).is_none(), "{n}");
    }
    assert!(absent.errors.is_empty(), "{:?}", absent.errors);

    let old = "[1] old line\n";
    for n in names {
        f.w(n, old.repeat(SMALL_LOG_MAX as usize / old.len() + 1) + &format!("[2] {n} last line (Z:\\var\\home\\frostling\\x.dll)\n"));
    }
    f.w("BuildingTools.ini", "[Keys]\nSecretSetting=1\n");
    let r = build(&f.input());
    for n in names {
        let log = file(&r, &format!("game/{n}")).unwrap_or_else(|| panic!("{n} must be collected"));
        assert!(log.starts_with("... (older lines left out") && log.len() as u64 <= SMALL_LOG_MAX + 200, "{n}: {} bytes", log.len());
        assert!(log.contains(&format!("{n} last line")) && log.contains(r"<home>\x.dll") && !log.contains("frostling"), "{n}: {log}");
    }
    assert!(!r.files.iter().any(|(n, _)| n.ends_with("BuildingTools.ini")), "a mod's settings stay out");
}

/// inbox 86: XHL-Core loaded a mod the profile had off, and the report had nothing of XHL-Core's own. Its log,
/// XHL-Native-Plugin-Host.log, written next to the DLL it runs as (the game's dinput8.dll, or Kiln's copy in
/// kiln/chain/<id>/), is in the zip now when it is there: bounded and redacted as Kiln's logs, never through a link.
#[test]
fn other_players_names_never_reach_any_file_of_the_report() {
    let f = Fx::healthy("players");
    f.w("kiln/logs/kiln.log", concat!(
        "[I 11:00:00.000] [kiln] Kiln 0.2.0-dev.22 starting (pid 412)\n",
        "[I 11:00:00.100] [kiln] boot complete; handing control to the game\n",
        "[I 11:01:00.000] [kiln] net: Brimwyn (0x81) joined with Kiln 0.2.0-dev.22: 1 mod(s), 2 shared channel(s)\n",
        "[I 11:01:00.500] [service:kiln-players.dll] players: joined slot 1 entity 7 'Brimwyn' (steam 76561198000000002, admin 0, perms 0x1) [event 2]\n",
        "[I 11:02:00.000] [mod:chat-plus] welcome back, Brimwyn! mail brim@example.org, rcon password=hunter2, peer fe80::1c2d:3e4f\n",
        "[E 11:03:00.000] [kiln] net: peer Brimwyn: 12 drops in 10 s: kicking\n",
    ));
    f.w("shroudlink.log", "[1] shroudlink: client 2eaa65d2d4c437f9 is Ember Fox\n[2] world: client 2eaa65d2d4c437f9 is player 2 (Ember Fox)\n");
    f.w("kiln/state/last-server.json", br#"{"format":1,"server":{"id":"srv-1","name":"Ember Isles","address":"203.0.113.7:15636"},"players":[{"playerName":"Brimwyn","email":"brim@example.org"}]}"#);
    let r = build(&f.input());
    let mut all = r.summary.clone() + &r.short;
    for (n, b) in &r.files {
        let t = String::from_utf8_lossy(b);
        for secret in ["Brimwyn", "Ember Fox", "brim@example.org", "hunter2", "fe80::1c2d", "76561198000000002", "203.0.113.7"] {
            assert!(!t.contains(secret), "{n} holds {secret}: {t}");
        }
        all.push_str(&t);
    }
    let log = file(&r, "game/kiln/logs/kiln.log").unwrap();
    assert!(log.contains("net: <player> (0x81) joined") && log.contains("welcome back, <player>!") && log.contains("password=<redacted>")
        && log.contains("<email>") && log.contains("peer <ip>"), "{log}");
    assert!(file(&r, "game/shroudlink.log").unwrap().contains("is player 2 (<player>)"));
    assert!(file(&r, "game/kiln/state/last-server.json").unwrap().contains("Ember Isles"), "the server keeps its name");
    assert!(r.summary.contains("(<player>)") && r.summary.contains("<email>"), "summary.txt says what was replaced");
}

#[test]
fn xhl_cores_own_log_is_collected_where_it_runs_from() {
    let f = Fx::healthy("xhl-core-log");
    let absent = build(&f.input());
    assert!(!absent.files.iter().any(|(n, _)| n.ends_with("XHL-Native-Plugin-Host.log")), "nothing without XHL-Core");
    assert!(absent.errors.is_empty(), "{:?}", absent.errors);

    let old = "[XHL-Native-Host] an older line\n";
    let last = "[XHL-Native-Host] loaded plugin: BuildingTools 0.34.0 (Z:\\var\\home\\frostling\\Enshrouded\\mods\\building-tools)\n";
    f.w("kiln/chain/xhl-core/xhl-core.dll", b"MZ");
    f.w("kiln/chain/xhl-core/XHL-Native-Plugin-Host.log", old.repeat(SMALL_LOG_MAX as usize / old.len() + 1) + last);
    f.w("XHL-Native-Plugin-Host.log", "[XHL-Native-Host] core: DirectInput8Create is forwarded to System32 dinput8.dll.\n");
    // the copy of another profile without a log, and a folder that is no mod id: nothing, no error
    f.w("kiln/chain/xhl-core-old/xhl-core.dll", b"MZ");
    f.w("kiln/chain/Not An Id/XHL-Native-Plugin-Host.log", "not read\n");
    let r = build(&f.input());
    let log = file(&r, "game/kiln/chain/xhl-core/XHL-Native-Plugin-Host.log").expect("Kiln's copy's log is in the zip");
    assert!(log.starts_with("... (older lines left out") && log.len() as u64 <= SMALL_LOG_MAX + 200, "bounded");
    assert!(log.contains("loaded plugin: BuildingTools 0.34.0") && log.contains(r"<home>\Enshrouded") && !log.contains("frostling"), "{log}");
    assert!(file(&r, "game/XHL-Native-Plugin-Host.log").is_some_and(|l| l.contains("forwarded to System32")), "the game's own XHL-Core's log");
    let xhl: Vec<&str> = r.files.iter().map(|(n, _)| n.as_str()).filter(|n| n.ends_with("XHL-Native-Plugin-Host.log")).collect();
    assert_eq!(xhl.len(), 2, "{xhl:?}");
    assert!(r.errors.is_empty(), "{:?}", r.errors);
    #[cfg(unix)]
    {
        // a link in place of the log or of the copy's folder is not followed
        let secret = f.root.join("secret.log");
        std::fs::write(&secret, "SECRET\n").unwrap();
        f.rm("XHL-Native-Plugin-Host.log");
        #[cfg(unix)] std::os::unix::fs::symlink(&secret, f.game.join("XHL-Native-Plugin-Host.log")).unwrap();
        #[cfg(unix)] std::os::unix::fs::symlink(f.root.join("elsewhere"), f.game.join("kiln/chain/linked")).unwrap();
        std::fs::create_dir_all(f.root.join("elsewhere")).unwrap();
        std::fs::write(f.root.join("elsewhere/XHL-Native-Plugin-Host.log"), "SECRET\n").unwrap();
        let r = build(&f.input());
        assert!(!r.files.iter().any(|(_, b)| String::from_utf8_lossy(b).contains("SECRET")), "a link was followed");
        assert!(file(&r, "game/kiln/chain/linked/XHL-Native-Plugin-Host.log").is_none());
    }
}

/// GitHub issue 39: a player's "slow motion" report could not be told apart from the save cycle because the zip had no
/// tick-stats.jsonl (Kiln's per-minute step times, rewritten at every game start). It is in the zip now: the newest lines,
/// whole, within [`TICK_STATS_MAX`].
#[test]
fn tick_stats_are_collected_newest_lines_whole_and_bounded() {
    let f = Fx::healthy("tick-stats");
    let absent = build(&f.input());
    assert!(file(&absent, "game/kiln/logs/tick-stats.jsonl").is_none());
    assert!(absent.errors.is_empty(), "{:?}", absent.errors);

    let line = |t: &str, rate: f64| format!(
        "{{\"time\":\"2026-11-15T{t}\",\"world\":\"server\",\"steps\":3580,\"rate\":{rate:.2},\"clock_rate\":59.67,\
         \"step_ms\":{{\"p50\":4.120,\"p99\":9.870,\"max\":2310.500}},\"pre_ms\":{{\"p50\":0.0210,\"p99\":0.0900,\"max\":0.300}},\
         \"post_ms\":{{\"p50\":0.0110,\"p99\":0.0500,\"max\":0.200}},\"interval_ms\":{{\"p50\":16.67,\"p99\":33.40,\"max\":2402.10}},\
         \"subs\":{{\"pre\":12,\"post\":9}}}}\n");
    let old = line("22:45:00", 59.67);
    // issue #49: the newest line carries a tasks object; the collector keeps it whole like the rest
    let base = line("00:33:00", 24.46);
    let last = format!(
        "{},\"tasks\":{{\"window\":{{\"id\":7,\"span_ms\":60000.0,\"age_ms\":0.0}},\"pool\":{{\"workers\":3,\"long_running\":1,\"queue_max\":12}},\"owners\":[{{\"owner\":\"kiln-auto-loot\",\"queued\":{{\"current\":0,\"max\":4}},\"running\":{{\"current\":1,\"max\":2}},\"completed\":120,\"faulted\":0,\"cancelled\":2,\"cpu_ms\":830.5,\"wall_ms\":910.2,\"longest_ms\":44.1,\"longest_name\":\"loot-scan\",\"queued_too_long\":0,\"running_too_long\":0,\"queue_full\":0,\"unload_blocked\":0}}],\"apply\":{{\"applied\":96,\"deferred\":3,\"cancelled\":0,\"ms\":4.2,\"owner_budget_stops\":1,\"step_budget_stops\":0,\"owners_served\":1,\"backlog\":0}},\"hops\":{{\"refused\":0}}}}}}\n",
        base.strip_suffix("}\n").unwrap());
    let n = TICK_STATS_MAX as usize / old.len() + 50;
    f.w("kiln/logs/tick-stats.jsonl", old.repeat(n) + &last);
    let r = build(&f.input());
    let t = file(&r, "game/kiln/logs/tick-stats.jsonl").expect("tick-stats.jsonl must be in the report");
    assert!(t.len() as u64 <= TICK_STATS_MAX + 200, "{}", t.len());
    let mut lines = t.lines();
    assert!(lines.next().is_some_and(|l| l.starts_with("... (older lines left out")), "a cut file says so first");
    let rest: Vec<&str> = lines.collect();
    assert!(rest.len() > 100, "{}", rest.len());
    for l in &rest {
        serde_json::from_str::<serde_json::Value>(l).unwrap_or_else(|e| panic!("every line after the note is whole JSON ({e}): {l}"));
    }
    assert_eq!(rest.last().copied(), Some(last.trim_end()), "the newest line is kept");
    let parsed = crate::logs::tick_stats(rest.last().unwrap());
    assert_eq!(parsed[0].tasks.as_ref().map(|t| (t.window.as_ref().and_then(|w| w.id), t.owners[0].completed)), Some((Some(7), 120)),
        "the kept line's tasks object still parses, old fields with it");
    assert_eq!(std::fs::read_to_string(f.game.join("kiln/logs/tick-stats.jsonl")).unwrap(), old.repeat(n) + &last, "collection is read-only");
    // a short file whole, without the note
    f.w("kiln/logs/tick-stats.jsonl", last.clone());
    let r = build(&f.input());
    assert_eq!(file(&r, "game/kiln/logs/tick-stats.jsonl"), Some(last.as_str()));
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
    #[cfg(unix)] std::os::unix::fs::symlink(&secret, f.game.join("shroudlink_client.log")).unwrap();
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

/// Inbox 92 (a player on Windows 11): an antivirus held kiln-greeneye.dll while Kiln installed it, the install failed with
/// "Access is denied. (os error 5)" and was undone, and the report said only "Kiln is not installed". launcher.log had the
/// cause: the report now says it (`install-blocked`), until an install works (a newer receipt).
#[test]
fn an_install_windows_refused_a_dll_of_is_a_finding_until_an_install_works() {
    let f = Fx::new("blocked");
    f.w("enshrouded.exe", b"MZ");
    let log = f.root.join("data/logs/launcher.log");
    std::fs::create_dir_all(log.parent().unwrap()).unwrap();
    // a 0.2.0-dev.29.18 launcher's lines: the error's detail after its words (this test's folder is a Linux path, so
    // the words alone would not count: 5 is no refusal there; blocked.rs has the Windows lines of older launchers)
    let failed = |at: &str, game: &Path| format!(
        "{at} task: runtime_install-2-6afe1f10 started: Install Kiln 0.2.0-dev.29.18\n\
         {at} task: runtime_install-2-6afe1f10 failed: [permission] installing {}/kiln/plugins/kiln-greeneye.dll: Access is denied. (os error 5) \
         {{\"blocked\":{{\"osError\":5,\"path\":\"kiln/plugins/kiln-greeneye.dll\"}}}} (last step: installed kiln/plugins/kiln-flame.dll)\n\
         {at} update: automatic check (dev): up to date (frostdev.io)\n", game.display());
    std::fs::write(&log, failed("2026-11-18T21:10:08Z", &f.game)).unwrap();
    let r = build(&f.input());
    let b = finding(&r, "install-blocked");
    assert_eq!(b.level, Level::Problem);
    assert!(b.text.starts_with("A Kiln install in this game folder (Install Kiln 0.2.0-dev.29.18, 2026-11-18T21:10:08Z) failed because Windows denied access to kiln/plugins/kiln-greeneye.dll (os error 5), and no install has worked since."), "{}", b.text);
    assert!(b.text.contains("An antivirus or another program may be blocking the file"), "{}", b.text);
    assert!(ids(&r).contains(&"not-installed"), "the receipt's own finding stays: {:?}", ids(&r));
    assert!(r.summary.contains("kiln-greeneye.dll (os error 5)") && r.short.contains("kiln-greeneye.dll (os error 5)"), "{}", r.short);
    assert!(file(&r, "launcher/launcher.log").is_some_and(|t| t.contains("os error 5")), "the raw line stays in the log");
    // another game folder's failure is not this one's
    std::fs::write(&log, failed("2026-11-18T21:10:08Z", &f.root.join("EnshroudedServer"))).unwrap();
    assert!(!ids(&build(&f.input())).contains(&"install-blocked"));

    // Kiln installed: a failed update after the receipt is found, one before it is over (an install worked since)
    let h = Fx::healthy("blocked-update");
    let log = h.root.join("data/logs/launcher.log");
    std::fs::create_dir_all(log.parent().unwrap()).unwrap();
    std::fs::write(&log, failed("2026-11-18T21:10:08Z", &h.game)).unwrap();
    assert_eq!(ids(&build(&h.input())), ["install-blocked"]);
    std::fs::write(&log, failed("2026-11-09T21:10:08Z", &h.game)).unwrap();
    assert_eq!(ids(&build(&h.input())), Vec::<&str>::new());
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
    assert!(n.text.contains("launch options") && n.text.contains(crate::game::DLL_OVERRIDE_ADVICE), "{}", n.text);
    // If the dinput8 override is already present, don't recommend setting it again.
    let prefix = f.root.join("pfx");
    std::fs::create_dir_all(&prefix).unwrap();
    std::fs::write(prefix.join("user.reg"), "[Software\\\\Wine\\\\DllOverrides]\n\"dinput8\"=\"native,builtin\"\n").unwrap();
    i.install.as_mut().unwrap().prefix = Some(prefix);
    let r = build(&i);
    let n = finding(&r, "not-loaded");
    assert!(n.text.contains("Check Steam's launch options for Enshrouded (WINEDLLOVERRIDES) and the Proton version."), "{}", n.text);
    assert!(!n.text.contains("try this fallback") && !n.text.contains(crate::game::DLL_OVERRIDE_ADVICE), "{}", n.text);
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

/// KR-YNQZ (31.11, 2026-11-26): the last start from the launcher ended in Kiln's early plugins (ShroudForge preparing,
/// 1.2 s in), and the game no longer ran when the report was written. kiln.log and progress.json name that start's
/// process; there was no boot.json. The report said "did not load Kiln: boot.json is missing. Check that dinput8.dll in
/// the game folder is Kiln's and that no antivirus blocks it": a wrong cause and wrong advice, Kiln had loaded. That
/// start's progress.json says how far it got. The fixtures are that report's progress.json and kiln.log.
#[test]
fn a_start_that_ended_while_kiln_was_starting_did_load_kiln() {
    const PROGRESS: &str = include_str!("../tests/fixtures/kr-ynqz/progress.json");
    const LOG: &str = include_str!("../tests/fixtures/kr-ynqz/kiln.log");
    let f = Fx::healthy("kr-ynqz-boot");
    f.rm("kiln/state/boot.json");
    f.w("kiln/logs/kiln.log", LOG);
    let dirs = DataDirs::at(f.root.join("data"));
    std::fs::create_dir_all(dirs.installs()).unwrap();
    let launch = |at: &str| std::fs::write(dirs.installs().join("c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b.launch.json"),
        format!(r#"{{"fingerprint":"kms1:x","mode":"profile","launched":"{at}"}}"#)).unwrap();
    launch("2026-11-26T15:25:06Z");
    // no progress.json: nothing says Kiln loaded
    assert!(finding(&build(&f.input()), "not-loaded").text.contains("did not load Kiln: boot.json is missing"));
    f.w("kiln/state/progress.json", PROGRESS);
    let r = build(&f.input());
    let n = finding(&r, "not-loaded");
    assert_eq!(n.level, Level::Problem);
    assert_eq!(n.text, "The last start from the launcher (2026-11-26T15:25:06Z) loaded Kiln (pid 18264), but the game ended before \
        Kiln finished starting: its last step, 1.2 s in, was \"Running the early plugins (ShroudForge, 3 of 5)\" \
        (kiln/state/progress.json), and boot.json is missing.");
    // an older boot.json: said as before
    f.boot(None);
    let old = SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(1_795_700_000);
    std::fs::File::options().write(true).open(f.game.join("kiln/state/boot.json")).unwrap().set_modified(old).unwrap();
    assert!(finding(&build(&f.input()), "not-loaded").text.ends_with("and boot.json is older (2026-11-26T13:33:20Z)."), "{:?}", build(&f.input()).findings);
    f.rm("kiln/state/boot.json");
    // a progress.json of an earlier start (over 30 s before the launch) says nothing about this one
    launch("2026-11-26T15:30:00Z");
    assert!(finding(&build(&f.input()), "not-loaded").text.contains("did not load Kiln: boot.json is missing"));
}

#[test]
fn unknown_build_and_safe_idle() {
    let f = Fx::healthy("build");
    let (sha, _) = crate::hash::sha256_file(&f.game.join("enshrouded.exe")).unwrap();
    f.rm(&format!("kiln/sigdb/builds/client/{sha}.json"));
    f.boot(Some("build abc is not verified (no build record) and policy is verified-only"));
    let r = build(&f.input());
    // the launcher's words for it (Play, the Settings row): the same sentence
    assert!(finding(&r, "unknown-build").text.starts_with(&crate::direct::unknown_build_sentence(&sha[..12])), "{}", finding(&r, "unknown-build").text);
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

/// Player report 2026-11-17 (Holster Toggle, Steam Deck): a modified EML loader next to the game is named as one, with
/// what its own code does, and under Proton the finding says that without the override that code does not run either.
#[test]
fn a_modified_eml_loader_in_the_folder_is_named_with_its_own_code() {
    let f = Fx::healthy("modified-eml");
    f.w("dbghelp.dll", crate::pe::build_test_dll(&["MiniDumpWriteDump"],
        b"EML_LOG_FILE_PATH crates\\mod-loader-runtime\\src\\native\\holster.rs m182-holster m182_native.json"));
    let l = finding(&build(&f.input()), "loader-in-folder").text.clone();
    assert!(l.starts_with("dbghelp.dll is a modified EML loader with code of its own (a weapon holster hotkey; settings in \
        m182_native.json). On Windows the game always loads it"), "{l}");
    assert!(!l.contains("Without that override"), "Windows has no override: {l}");
    let mut i = f.input();
    i.install = Some(f.install(Platform::Proton));
    let l = finding(&build(&i), "loader-in-folder").text.clone();
    assert!(l.contains("Under Wine or Proton it loads only with a DLL override for that name") && l.ends_with("Without that override \
        neither EML nor its own code runs from it, and Kiln runs the EML mods' data part itself."), "{l}");
}

/// PepegaJedi's report of 21 Nov 2026: EMF's own loader chained on purpose (kiln/chain.ini [chain] names it), kiln-emf
/// stood aside, and EMF's core ran the EMF mods. The finding said "problem", which read as a failure: it is info, that is
/// how the setup works. A problem stays a problem: a profile mod EMF cannot see, or EMF's core gone idle.
#[test]
fn emf_running_its_own_mods_through_its_chained_loader_is_info() {
    let state = |idle: &str, lua: &str| format!(r#"{{"format":1,"plugin":"kiln-emf","steppedAside":true,"emfIdle":"{idle}",
        "steppedAsideReason":"EMF's loader is loaded (E:\\Enshrouded\\kiln\\chain\\emf-dinput8.dll), it starts EMF and its mods",
        "mods":[{{"id":"mage_skills","status":"own-loader","reason":"runs under EMF's own loader"}},
                {{"id":"lua","status":"{lua}","reason":"..."}},
                {{"id":"clock","status":"disabled","reason":"switched off in the profile"}}]}}"#);
    let f = Fx::healthy("emf-own-loader");
    f.w("kiln/state/emf.json", state("", "own-loader").as_bytes());
    let o = finding(&build(&f.input()), "own-loader").clone();
    assert_eq!(o.level, Level::Info, "{}", o.text);
    assert!(o.text.starts_with("EMF runs its own mods; that's how this setup works.") && o.text.contains("emf-dinput8.dll"), "{}", o.text);
    // a profile mod EMF does not see: a problem, named
    f.w("kiln/state/emf.json", state("", "not-loaded").as_bytes());
    let o = finding(&build(&f.input()), "own-loader").clone();
    assert_eq!(o.level, Level::Problem, "{}", o.text);
    assert!(o.text.contains("1 EMF mod of the profile is not in emf\\mods") && o.text.contains(": lua.") && !o.text.contains("mage_skills"), "{}", o.text);
    // EMF's core idle: no EMF mod ran at all
    f.w("kiln/state/emf.json", state("EMF's core went idle on this game build: no EMF mod runs", "own-loader").as_bytes());
    let o = finding(&build(&f.input()), "own-loader").clone();
    assert_eq!(o.level, Level::Problem, "{}", o.text);
    assert!(o.text.contains("went idle") && o.text.contains("kiln/chain.ini"), "{}", o.text);
}

/// A player's report of 16 Nov 2026 (Steam Deck, shroudlink's singleplayer package 0.8.1): its client addon, chained
/// from the Library, loaded the `shroudlink_core.dll` an older hand installation had left next to the game, and
/// kiln-shroudlink stood aside for it. The finding names that file and keeps the addon, which draws the MODS tab.
#[test]
fn shroudlink_aside_for_a_core_its_addon_loaded_names_the_core() {
    let f = Fx::healthy("sl-core");
    f.w("kiln/state/shroudlink.json", br#"{"format":1,"plugin":"kiln-shroudlink","steppedAside":true,
        "corePath":"S:\\steamapps\\common\\Enshrouded\\shroudlink_core.dll",
        "steppedAsideReason":"shroudlink's own core runs in this process (S:\\steamapps\\common\\Enshrouded\\shroudlink_core.dll); its mods in shroudlink_mods/ load there",
        "mods":[{"id":"expeditions","status":"not-loaded","reason":"..."}]}"#);
    let r = build(&f.input());
    let o = finding(&r, "own-loader");
    assert!(o.text.contains(r"S:\steamapps\common\Enshrouded\shroudlink_core.dll") && o.text.contains("MODS tab"), "{}", o.text);
}

#[test]
fn shroudlink_aside_names_a_chained_core_instead_of_the_game_root() {
    let f = Fx::healthy("sl-chain-core");
    f.w("kiln/state/shroudlink.json", br#"{"format":1,"steppedAside":true,
        "corePath":"S:\\Enshrouded\\kiln\\chain\\shroudlink_core.dll",
        "steppedAsideReason":"own core S:\\Enshrouded\\kiln\\chain\\shroudlink_core.dll",
        "mods":[]}"#);
    let r = build(&f.input());
    let o = finding(&r, "own-loader");
    assert!(o.text.contains(r"S:\Enshrouded\kiln\chain\shroudlink_core.dll"), "{}", o.text);
    assert!(!o.text.contains("Remove shroudlink_core.dll from the game folder") && !o.text.contains("client addon loaded"), "{}", o.text);
}

#[test]
fn shroudlink_aside_without_a_core_path_does_not_guess_from_the_reason() {
    let f = Fx::healthy("sl-unknown-core");
    f.w("kiln/state/shroudlink.json", br#"{"format":1,"steppedAside":true,
        "steppedAsideReason":"another core claimed first; could be shroudlink_core.dll", "mods":[]}"#);
    let r = build(&f.input());
    let o = finding(&r, "own-loader");
    assert!(!o.text.contains("Remove shroudlink_core.dll from the game folder") && !o.text.contains("client addon loaded"), "{}", o.text);
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
    assert!(n.text.starts_with("kiln-eml handed your 1 EML pack to Kiln's ShroudForge at the last start: "), "{}", n.text);
    assert!(!r.summary.contains("EML's own loader runs in the game"), "{}", r.summary);
    // a mod ShroudForge has switched off, and one that is not in its folder: these did not run, and that is the finding
    let off = format!(r#"{on},{{"id":"b","state":"off-in-shroudforge","reason":"x"}},{{"id":"c","state":"not-loaded","reason":"y"}},{{"id":"d","state":"disabled"}}"#);
    f.w("kiln/state/eml.json", eml(BOOT, WHY, &off).as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "eml-under-shroudforge");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.starts_with("2 EML mods of the profile did not run") && n.text.contains(": b, c. "), "{}", n.text);
    assert!(!ids(&r).contains(&"own-loader"));
    // a server without a profile (dev.30.3): the drop-ins nothing runs are no mods of the profile
    let dropins = r#"{"id":"e","source":"game-mods","state":"off-in-shroudforge","reason":"x"},{"id":"f","source":"kiln-dropin","state":"not-loaded","reason":"y"}"#;
    f.w("kiln/state/eml.json", eml(BOOT, WHY, dropins).as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "eml-under-shroudforge");
    assert!(n.level == Level::Warning && n.text.starts_with("2 EML mods did not run") && n.text.contains(": e, f. "), "{}", n.text);
    // EML's own loader in a start where Kiln hosts ShroudForge too: still the own-loader problem
    f.w("kiln/state/eml.json", eml(BOOT, "dbghelp.dll is loaded", on).as_bytes());
    assert!(ids(&build(&f.input())).contains(&"own-loader"));
    // ShroudForge's file of that start says it ran by itself: kiln-eml handed the pack to ShroudForge, not Kiln's; the
    // own loader that ran is ShroudForge's (its own finding), never EML's
    f.w("kiln/state/eml.json", eml(BOOT, WHY, on).as_bytes());
    f.w("kiln/state/shroudforge.json", sf(BOOT, "self-hosted").as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "eml-under-shroudforge");
    assert!(n.text.starts_with("kiln-eml handed your 1 EML pack to ShroudForge at the last start: "), "{}", n.text);
    assert!(!r.summary.contains("EML's own loader runs"), "{}", r.summary);
    let own: Vec<&str> = r.findings.iter().filter(|x| x.id == "own-loader").map(|x| x.text.as_str()).collect();
    assert!(!own.is_empty() && own.iter().all(|t| t.starts_with("kiln-shroudforge stood aside at the last start: ShroudForge's own loader")), "{own:#?}");
}

/// KR-YNQZ (31.11, 2026-11-26): Kiln hosted ShroudForge and kiln-eml handed all 8 EML packs to it, but the report was
/// written before ShroudForge's state file (the start had not finished; no boot.json either). The summary said "kiln-eml
/// stood aside at the last start: EML's own loader runs in the game", a problem, in a start with no such loader. kiln-eml's
/// own file says whom it stood aside for (only Kiln's ShroudForge plugin gives that reason) and kiln.log names the last
/// start's process: the two are enough. The fixtures are that report's eml.json and kiln.log.
#[test]
fn eml_handed_to_hosted_shroudforge_before_its_state_file_is_said_so() {
    const EML: &str = include_str!("../tests/fixtures/kr-ynqz/eml.json");
    const LOG: &str = include_str!("../tests/fixtures/kr-ynqz/kiln.log");
    let f = Fx::healthy("kr-ynqz");
    std::fs::remove_file(f.game.join("kiln/state/boot.json")).unwrap();
    f.w("kiln/state/eml.json", EML.as_bytes());
    f.w("kiln/logs/kiln.log", LOG.as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"own-loader"), "{:?}", ids(&r));
    assert!(!r.summary.contains("EML's own loader runs"), "{}", r.summary);
    let n = finding(&r, "eml-under-shroudforge");
    assert_eq!(n.level, Level::Info);
    assert!(n.text.starts_with("kiln-eml handed your 8 EML packs to Kiln's ShroudForge at the last start: "), "{}", n.text);
    // the file of another start than the one kiln.log names: what it says, and that it is not the last start's; never
    // EML's own loader, which nothing here shows
    f.w("kiln/state/eml.json", EML.replace("\"pid\": 18264", "\"pid\": 9100").as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"own-loader") && !r.summary.contains("EML's own loader runs"), "{}", r.summary);
    let n = finding(&r, "eml-under-shroudforge");
    assert_eq!(n.level, Level::Info);
    assert!(n.text.starts_with("kiln-eml's state file is of an earlier start (pid 9100) than the last one (pid 18264): in that start it \
        handed 8 EML packs to ShroudForge."), "{}", n.text);
    // EML's own loader (its reason names the DLL): still the own-loader problem
    f.w("kiln/state/eml.json", EML.replace("ShroudForge owns startup asset patching and runs EML packages in its mods folder", "dbghelp.dll is loaded").as_bytes());
    let r = build(&f.input());
    assert!(ids(&r).contains(&"own-loader") && !ids(&r).contains(&"eml-under-shroudforge"), "{:?}", ids(&r));
}

/// 0.2.0-dev.31.12, a player's report: a folder of his own in ShroudForge's mods folder, of a mod his profile had off, ran
/// while the summary said "disabled: switched off in the profile". Skipped now when Kiln hosts ShroudForge; where nothing
/// could skip it, kiln-eml's row says it runs (`own-loader`) and the summary has a warning naming it. A skipped row is no
/// finding.
#[test]
fn a_mod_that_is_off_but_ran_from_the_players_own_folder_is_a_warning() {
    const WHY: &str = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    let f = Fx::healthy("off-but-runs");
    f.w("kiln/state/shroudforge.json", br#"{"pid":412,"mode":"hosted","source":"installed","runtime":"running",
        "profileOff":[{"id":"glide-boost","package":"glide-boost","path":"S:\\mods\\glide-boost","own":true,"skipped":false}]}"#);
    let row = |state: &str, reason: &str| format!(r#"{{"pid":412,"phase":"aside","aside":"{WHY}","mods":[{{"id":"quiver-slots","state":"{state}","reason":"{reason}"}}]}}"#);
    f.w("kiln/state/eml.json", row("own-loader", "runs under ShroudForge (hosted by Kiln) although it is off in the profile: it is your own folder S:/mods/quiver-slots").as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "off-but-runs");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.contains("quiver-slots") && n.text.contains("ran at the last start although the profile has"), "{}", n.text);
    assert!(r.findings.iter().any(|x| x.id == "off-but-runs" && x.text.contains("glide-boost")), "{:?}", ids(&r));
    // skipped: nothing to warn about
    f.w("kiln/state/eml.json", row("disabled", "quiver-slots is off in the profile: your own folder S:/mods/quiver-slots is skipped this start; Kiln leaves its files as they are").as_bytes());
    f.w("kiln/state/shroudforge.json", br#"{"pid":412,"mode":"hosted","source":"bundled","runtime":"running",
        "profileOff":[{"id":"glide-boost","package":"glide-boost","path":"S:\\mods\\glide-boost","own":true,"skipped":true}]}"#);
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"off-but-runs"), "{:?}", ids(&r));
}

/// Review (0.2.0-dev.31.12): `off-but-runs` is about a folder of the player's own. Kiln's copy that runs although the
/// profile has its mod off (switched on in the game's menu since the last activation) is Kiln's, and the next Play
/// switches it off: the finding must not call it "a folder of the player's own, which Kiln never writes into".
#[test]
fn kilns_own_copy_that_ran_although_off_is_no_players_own_folder_finding() {
    const WHY: &str = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    let f = Fx::healthy("off-but-runs-copy");
    f.w("kiln/state/shroudforge.json", br#"{"pid":412,"mode":"hosted","source":"installed","runtime":"running",
        "profileOff":[{"id":"stack-copy","package":"stack-copy","path":"S:\\mods\\stack-copy","own":false,"skipped":false}]}"#);
    f.w("kiln/state/eml.json", format!(r#"{{"pid":412,"phase":"aside","aside":"{WHY}","mods":[{{"id":"stack-tweaks","state":"own-loader",
        "reason":"runs under ShroudForge (hosted by Kiln) although it is off in the profile: ShroudForge's switch is on in Kiln's copy S:/mods/stack-tweaks, and this ShroudForge runtime takes no skip list from Kiln. Press Play in the Kiln launcher (or activate the profile again): Kiln then takes its copy out (or switches it off)"}}]}}"#).as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"off-but-runs"), "{:?}", r.findings.iter().filter(|x| x.id == "off-but-runs").collect::<Vec<_>>());
}

/// Review of the 31.12 bundle: compat-aside-truth's plugins (emf.json, greeneye.json, shroudtopia.json, flame.json) name
/// their rows' state `status`, and say "runs (your own folder …; …) although it is off in the profile: …". Those are a
/// player's own folders: `off-but-runs`. Their "runs (Kiln's copy … is still there; …)" rows are Kiln's copy, which the next
/// Play takes out: no finding.
#[test]
fn an_own_loader_status_row_of_the_players_own_folder_is_off_but_runs_and_kilns_copy_is_not() {
    let f = Fx::healthy("off-but-runs-compat");
    f.w("kiln/state/emf.json", br#"{"format":1,"plugin":"kiln-emf","mods":[{"id":"mage_skills","status":"own-loader",
        "reason":"runs (your own folder E:/Enshrouded/emf/mods/mage_skills; EMF's own switch is on) although it is off in the profile: EMF's own loader runs this start and reads only <game>/emf/mods, not the profile, and Kiln never changes a folder you put there. To turn it off in EMF: enabled=0 in emf/config/mage_skills.ini"}]}"#);
    f.w("kiln/state/flame.json", br#"{"status":"running","mods":[{"name":"BiggerTorch","status":"own-loader",
        "reason":"runs (your own folder E:/Enshrouded/mods/BiggerTorch; Flame's own loader has no switch) although it is off in the profile: Flame's own loader runs this start and reads only <game>/mods, not the profile, and Kiln never changes a folder you put there. To turn it off in Flame: move the folder"}]}"#);
    f.w("kiln/state/greeneye.json", br#"{"status":"running","mods":[{"id":"fast-swim","status":"own-loader",
        "reason":"runs (Kiln's copy E:/Enshrouded/greeneye/fast-swim is still there; Greeneye Loader's own switch is on) although it is off in the profile: Greeneye Loader's own loader runs this start and reads only <game>/greeneye, not the profile. Activate the profile again in the Kiln launcher to take Kiln's copy out, or enabled false in that mod.json"}]}"#);
    let r = build(&f.input());
    let warned: Vec<&str> = r.findings.iter().filter(|x| x.id == "off-but-runs").map(|x| x.text.as_str()).collect();
    assert!(warned.iter().any(|t| t.contains("mage_skills")), "{warned:?}");
    assert!(warned.iter().any(|t| t.contains("BiggerTorch")), "{warned:?}");
    assert!(!warned.iter().any(|t| t.contains("fast-swim")), "Kiln's copy is no player's own folder: {warned:?}");
    assert!(r.findings.iter().filter(|x| x.id == "off-but-runs").all(|x| x.level == Level::Warning));
    // switched off in the loader too: nothing ran, nothing to warn about
    f.w("kiln/state/emf.json", br#"{"format":1,"plugin":"kiln-emf","mods":[{"id":"mage_skills","status":"disabled",
        "reason":"switched off in the profile; your own folder E:/Enshrouded/emf/mods/mage_skills is in <game>/emf/mods too, and EMF's own loader keeps it off as well (enabled=0 in its ini)"}]}"#);
    f.w("kiln/state/flame.json", br#"{"status":"running","mods":[]}"#);
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"off-but-runs"), "{:?}", r.findings.iter().filter(|x| x.id == "off-but-runs").collect::<Vec<_>>());
}

/// Review of the 31.12 bundle: an off native mod whose DLL the player also chained (native.json `chainProxy`, kiln-core's
/// step 3b) ran its DllMain before the profile was read: `off-but-runs` names it; without `chainProxy` nothing ran.
#[test]
fn an_off_native_mod_that_loads_as_a_chain_proxy_is_off_but_runs() {
    let f = Fx::healthy("off-but-runs-native");
    let native = |proxy: &str| format!(r#"{{"format":1,"pid":412,"written":"2026-11-25T11:00:01Z","policy":"always","mods":[
        {{"id":"fps-unlock","state":"disabled","reason":"disabled in the profile, but its DLL fpsunlock.dll loads all the same as a chain proxy","chainProxy":{proxy}}},
        {{"id":"clock","state":"disabled","reason":"disabled in the profile"}}]}}"#);
    f.w("kiln/state/native.json", native(r#""E:/Enshrouded/kiln/chain/fps-unlock-dinput8.dll""#).as_bytes());
    let r = build(&f.input());
    let n = finding(&r, "off-but-runs");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.starts_with("1 native mod ran at the last start although it is off: fps-unlock.") && n.text.contains("chain proxy")
        && !n.text.contains("clock"), "{}", n.text);
    f.w("kiln/state/native.json", native("null").as_bytes());
    let r = build(&f.input());
    assert!(!ids(&r).contains(&"off-but-runs"), "{:?}", ids(&r));
}

/// Review of 6a78171d: two files of one OLDER start said "hosted" and "aside for ShroudForge" while neither plugin ran
/// in the last start; and a file without a pid stood for any start. Neither proves anything about the last start, so
/// neither says the last start was fine. KR-YNQZ (26 Nov): nor do they show EML's own loader, which the own-loader
/// finding claimed: such a file now says what it does show (the start it is of) and that it says nothing about the last
/// one. kiln-eml's file of the last start (its pid is kiln.log's last boot) speaks for that start by itself: only Kiln's
/// ShroudForge plugin gives that reason, and ShroudForge's file may not be written yet, or be of an older start.
#[test]
fn state_files_of_another_start_do_not_stand_for_the_last_one() {
    const WHY: &str = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    let f = Fx::healthy("eml-under-sf-stale");
    let said = |sf: &str, eml: &str| {
        f.w("kiln/state/shroudforge.json", sf.as_bytes());
        f.w("kiln/state/eml.json", eml.as_bytes());
        let r = build(&f.input());
        assert!(!ids(&r).contains(&"own-loader") && !r.summary.contains("EML's own loader runs"), "{}", r.summary);
        finding(&r, "eml-under-shroudforge").text.clone()
    };
    let sf = |pid: &str| format!(r#"{{{pid}"mode":"hosted","source":"bundled"}}"#);
    let eml = |pid: &str| format!(r#"{{{pid}"phase":"aside","aside":"{WHY}","mods":[]}}"#);
    let last = |t: String| t.starts_with("kiln-eml handed the EML packs to Kiln's ShroudForge at the last start: ");
    let other = |t: String, of: &str| t.starts_with(&format!("kiln-eml's state file is of {of}: in that start it handed its EML packs to \
        ShroudForge. It says nothing about the last start."));
    assert!(last(said(&sf(r#""pid":412,"#), &eml(r#""pid":412,"#))), "the last start's pair is the hosted case");
    assert!(other(said(&sf(r#""pid":7,"#), &eml(r#""pid":7,"#)), "an earlier start (pid 7) than the last one (pid 412)"), "an older start's pair");
    assert!(other(said(&sf(r#""pid":412,"#), &eml(r#""pid":7,"#)), "an earlier start (pid 7) than the last one (pid 412)"), "kiln-eml's file is older");
    assert!(other(said(&sf(r#""pid":412,"#), &eml("")), "a start it does not name (no pid)"), "kiln-eml's file names no pid");
    // kiln-eml's file is the last start's: it speaks for it, whatever ShroudForge's file is of
    assert!(last(said(&sf(r#""pid":7,"#), &eml(r#""pid":412,"#))), "ShroudForge's file is older");
    assert!(last(said(&sf(""), &eml(r#""pid":412,"#))), "ShroudForge's file names no pid");
    // review of KR-YNQZ's fix: a later start (pid 500) is in kiln.log but got to no boot.json (it ended in its early
    // plugins, as KR-YNQZ's did): the pair of boot.json's start (412) is of the start before, not of the last one
    f.w("kiln/logs/kiln.log", "[I 11:00:00.000] [kiln] Kiln 0.2.0-dev.22 starting (pid 412)\r\n\
        [I 11:00:00.100] [kiln] boot complete; handing control to the game\r\n\
        [I 12:00:00.000] [kiln] Kiln 0.2.0-dev.22 starting (pid 500)\r\n");
    assert!(other(said(&sf(r#""pid":412,"#), &eml(r#""pid":412,"#)), "an earlier start (pid 412) than the last one (pid 500)"),
        "boot.json's pair, a later start in kiln.log");
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
    #[cfg(unix)] std::os::unix::fs::symlink(&outside, f.game.join("greeneye/linked")).unwrap();
    #[cfg(windows)] crate::util::test_junction(&f.game.join("greeneye/linked"), &outside);
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
    let link = f.game.join("dinput8-chain.ini");
    #[cfg(unix)] std::os::unix::fs::symlink(&target, &link).unwrap();
    #[cfg(windows)] crate::util::test_junction(&link, target.parent().unwrap());
    let r = build(&f.input());
    assert!(!r.files.iter().any(|(_, b)| String::from_utf8_lossy(b).contains("private text")), "the target is not in the report");
    assert!(r.errors.iter().any(|e| e.contains("dinput8-chain.ini") && e.contains("link")), "{:?}", r.errors);
}

/// Review R2: one Greeneye id with logs in two folders (the Library entry it runs from, and a copy that stays in
/// greeneye/) gave two zip entries of one name, and "Save report" failed with "Duplicate filename".
#[test]
#[cfg(feature = "zip")]
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
    f.w("kiln/crash-reports/crash-20261110T110500Z.txt",
        "chat-plus faulted 5 times\n\n-- Tasks --\n  running thread=42 owner=\"kiln-auto-loot\" task=\"loot-scan\" handle=0x1234 started_ns=99 elapsed_ms=12.500 state=running\n");
    let r = build(&f.input());
    let m = finding(&r, "mod-failed");
    assert!(m.text.contains("missing dependency kiln.ui") && m.text.contains("BiggerTorch") && m.text.contains("LoadLibrary 126"), "{}", m.text);
    assert!(finding(&r, "crash").text.contains("crash-20261110T110500Z.txt"));
    let crash = file(&r, "game/kiln/crash-reports/crash-20261110T110500Z.txt").expect("the crash report is collected");
    assert!(crash.contains("-- Tasks --") && crash.contains("task=\"loot-scan\""), "the crash report's task lines are kept: {crash}");
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

/// (dev.29.18) A finding the launcher knows (WebKit's sandbox) is listed with the report's own, problems first, in
/// summary.txt, report.json and the short summary, redacted like them; with a game and without one.
#[test]
fn the_callers_findings_are_listed_with_the_reports_own() {
    let f = Fx::new("caller-findings");
    let mut i = f.input();
    i.findings.push(Finding { id: "webkit-sandbox".into(), level: Level::Problem,
        text: format!("WebKit's sandbox cannot start on this system (bwrap: setting up uid map: Permission denied); launcher.log: {HOME}/.local/share/kiln/logs/launcher.log") });
    for game in [true, false] {
        if !game {
            i.install = None;
        }
        let r = build(&i);
        let w = r.findings.iter().find(|x| x.id == "webkit-sandbox").expect("listed");
        assert_eq!(w.level, Level::Problem);
        assert!(w.text.contains("<home>/.local/share/kiln/logs/launcher.log") && !w.text.contains(HOME), "{}", w.text);
        assert_eq!(r.findings[0].level, Level::Problem, "problems first");
        assert!(r.summary.contains("[problem] WebKit's sandbox cannot start"), "{}", r.summary);
        assert!(r.short.contains("WebKit's sandbox cannot start"), "{}", r.short);
        assert!(file(&r, "report.json").unwrap().contains("\"webkit-sandbox\""));
    }
    // none given: none listed
    assert!(build(&f.input()).findings.iter().all(|x| x.id != "webkit-sandbox"));
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

/// Two saves of one report file at once (a second click, a second window): both end with the whole zip at the path
/// chosen and no temp file left. With one shared `<file>.tmp` the second one's rename found nothing (2026-11-20).
#[cfg(feature = "zip")]
#[test]
fn saves_of_one_report_file_at_once_all_succeed() {
    let f = Fx::healthy("zip-race");
    let r = build(&f.input());
    let dir = f.root.join("saved");
    std::fs::create_dir_all(&dir).unwrap();
    let out = dir.join("kiln-report.zip");
    let errors: Vec<String> = std::thread::scope(|s| {
        let ws: Vec<_> = (0..4).map(|_| s.spawn(|| (0..10).filter_map(|_| write_zip(&r, &out).err().map(|e| e.message)).collect::<Vec<_>>())).collect();
        ws.into_iter().flat_map(|w| w.join().unwrap()).collect()
    });
    assert!(errors.is_empty(), "{} of 40 saves failed, first: {:?}", errors.len(), errors.first());
    assert!(zip::ZipArchive::new(std::fs::File::open(&out).unwrap()).unwrap().len() > 1);
    let names: Vec<_> = std::fs::read_dir(&dir).unwrap().flatten().map(|e| e.file_name()).collect();
    assert_eq!(names, ["kiln-report.zip"], "nothing else beside it");
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

/// dev.29.18 (inbox 79): Kiln's ShroudForge ran its startup pass again without the one mod it failed in. The report
/// names the mod and its error as a warning; the other mods loaded, so it is no `sf-startup-failed`.
#[test]
fn a_startup_pass_that_ran_without_one_mod_is_a_warning() {
    let f = Fx::healthy("sf-skipped");
    f.w("kiln/plugins/kiln-shroudforge.dll", b"adapter");
    f.w("kiln/plugins/shroudforge/shroudforge-runtime.dll", b"runtime");
    for id in ["bone-caller", "wisp"] {
        f.w(&format!("mods/{id}/mod.json"), serde_json::to_vec(&serde_json::json!({"id": id, "name": id, "version": "1.0.0", "capabilities": ["patch"]})).unwrap());
        f.w(&format!("mods/{id}/extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"targets":["client"],"settings":{}}"#);
        f.w(&format!("mods/{id}/src/mod.lua"), "return {}");
    }
    f.w("kiln/state/shroudforge.json", br#"{"format":1,"mode":"hosted","source":"bundled","pid":412,"side":"client","updated":1794308430,
        "bundled":{"version":"1.7.9","build":"kiln.4b1ab16"},"startupPrepared":true}"#);
    f.w("shroudforge/state.json", br#"{"schemaVersion":1,"assets":{"schemaVersion":1,"status":"applied","phase":"startup","target":"enshrouded",
        "completedAt":1794308405,"fingerprint":"f","mods":[{"id":"bone-caller","version":"1.0.0"}],
        "skipped":[{"id":"wisp","name":"Wisp Light Timer","version":"1.0.3","error":"runtime error: Missing base localization: keenglish"}]}}"#);
    let r = build(&f.input());
    let s = finding(&r, "sf-startup-skipped");
    assert_eq!(s.level, Level::Warning);
    assert!(s.text.starts_with("ShroudForge 1.7.9, hosted by Kiln, left Wisp Light Timer (wisp 1.0.3) out of its startup pass"), "{}", s.text);
    assert!(s.text.contains("(runtime error: Missing base localization: keenglish)") && s.text.contains("The other mod that needs that step loaded")
        && s.text.ends_with("Turn Wisp Light Timer off, or check whether it clashes with another mod: one that changes the same game data can make its check fail."),
        "{}", s.text);
    assert!(!r.findings.iter().any(|x| x.id == "sf-startup-failed"), "{:#?}", r.findings);
    assert!(r.summary.contains("ShroudForge's record of its startup pass (state.json \"assets\"): applied at 2026-11-10T11:00:05Z without Wisp Light Timer"),
        "{}", r.summary);
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

#[test]
fn chained_loader_override_finding() {
    let f = Fx::healthy("chained-override");
    let mut input = f.input();
    input.install = Some(f.install(Platform::Proton));
    let r = build(&input);
    assert!(!ids(&r).contains(&"proton-chained-override"));
    assert!(finding(&r, "proton-override").text.contains(crate::game::DLL_OVERRIDE_ADVICE));
    f.w("kiln/chain.ini", "[chain]\n..\\xinput9_1_0.dll\n");
    f.w("xinput9_1_0.dll", "fixture");
    let r = build(&input);
    assert!(r.summary.contains("dinput8 override: no; chained xinput9_1_0 override missing: yes"));
    let n = finding(&r, "proton-chained-override");
    assert_eq!(n.level, Level::Warning);
    assert!(n.text.contains("Ultimate ASI Loader") && n.text.contains(".asi plugins"));
    assert!(!n.text.contains("Kiln may not load"));
    assert!(finding(&r, "proton-override").text.contains("dinput8,xinput9_1_0=n,b"));
    let prefix = f.root.join("pfx");
    std::fs::create_dir_all(&prefix).unwrap();
    std::fs::write(prefix.join("user.reg"), "[Software\\\\Wine\\\\DllOverrides]\n\"dinput8\"=\"native,builtin\"\n").unwrap();
    input.install.as_mut().unwrap().prefix = Some(prefix.clone());
    let r = build(&input);
    assert!(!ids(&r).contains(&"proton-override"));
    assert!(ids(&r).contains(&"proton-chained-override"));
    std::fs::write(prefix.join("user.reg"), "[Software\\\\Wine\\\\DllOverrides]\n\"dinput8\"=\"native,builtin\"\n\"xinput9_1_0\"=\"native,builtin\"\n").unwrap();
    assert!(!ids(&build(&input)).contains(&"proton-chained-override"));
    input.install.as_mut().unwrap().platform = Platform::Windows;
    assert!(!ids(&build(&input)).contains(&"proton-chained-override"));
}

#[test]
fn shroudlink_addon_finding_is_one_info_and_client_only() {
    let fx = Fx::new("sl-addon-hint");
    fx.w("enshrouded.exe", b"fixture only");
    fx.profile(serde_json::json!(false), serde_json::json!({"shroudlink":[
        {"id":"assistants","path":"sl/assistants"}, {"id":"expeditions","path":"sl/expeditions"}
    ]}));
    let input = fx.input();
    let report = build(&input);
    let findings = report.findings.iter().filter(|f| f.id == "shroudlink-client-addon").collect::<Vec<_>>();
    assert_eq!(findings.len(), 1);
    assert_eq!(findings[0].level, Level::Info);
    assert!(findings[0].text.contains("My mods") && findings[0].text.contains("assistants, expeditions"));
    let mut server = input.clone();
    server.install.as_mut().unwrap().kind = GameKind::Server;
    assert!(!build(&server).findings.iter().any(|f| f.id == "shroudlink-client-addon"));
    fx.profile(serde_json::json!(false), serde_json::json!({"shroudlink":[{"id":"assistants","path":"sl","enabled":false}]}));
    assert!(!build(&input).findings.iter().any(|f| f.id == "shroudlink-client-addon"));
    fx.profile(serde_json::json!(false), serde_json::json!({"shroudlink":[{"id":"assistants","path":"sl"}]}));
    fx.w("kiln/chain/renamed.dll", crate::pe::build_test_dll(&["DirectInput8Create", "shroudlink_shutdown"], b""));
    fx.w("kiln/chain.ini", "[chain]\nchain\\renamed.dll\n");
    assert!(!build(&input).findings.iter().any(|f| f.id == "shroudlink-client-addon"));
}

/// Inbox 38 (sosmic, 16 Nov 2026): an EML mod the launcher had on was listed "off" in the summary's Profile section.
/// The game's profile had it `enabled: false` with an `emlRuntimeBlocked` reason (ShroudForge's runtime was switched
/// off at activation). The section says it is on and why its data was not applied; a mod the player switched off
/// stays "off".
#[test]
fn a_runtime_blocked_eml_mod_reads_on_not_applied() {
    let f = Fx::healthy("eml-blocked");
    let why = "needs ShroudForge's runtime, which the KILN_SHROUDFORGE=0 environment variable switches off";
    f.w("kiln/profile.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "id": "my-mods", "name": "My mods", "generation": 3, "dropins": false,
        "configDir": r"Z:\var\home\frostling\.local\share\kiln\profiles\my-mods\config",
        "emlRuntimeBlocked": {"ammo-slots": why},
        "mods": {"eml": [
            {"id": "ammo-slots", "version": "0.8.2", "path": r"Z:\lib\eml\ammo-slots", "enabled": false},
            {"id": "other-eml", "version": "1.0.0", "path": r"Z:\lib\eml\other-eml", "enabled": false},
        ]},
    })).unwrap());
    let r = build(&f.input());
    let line = r.summary.lines().find(|l| l.contains("ammo-slots") && l.contains("0.8.2")).unwrap_or_default();
    assert!(line.contains(&format!("on, not applied: {why}")), "{}", r.summary);
    let other = r.summary.lines().find(|l| l.contains("other-eml")).unwrap_or_default();
    assert!(other.trim_end().ends_with("off"), "{}", r.summary);
}

/// (dev.29.17) A custom folder that starts directly: the report says so, has no Steam launch-option finding (a direct
/// start sets the override itself), keeps the build check, and carries the Proton/Wine output of the last direct start.
#[test]
fn a_custom_folder_started_directly() {
    let f = Fx::healthy("direct");
    let mut i = f.input();
    let mut inst = f.install(Platform::Proton);
    inst.source = "manual".into();
    i.install = Some(inst.clone());
    let dirs = i.dirs.clone().unwrap();
    std::fs::create_dir_all(dirs.logs()).unwrap();
    std::fs::write(crate::launch::direct_log(&dirs, &inst), "wine: created the configuration directory\n").unwrap();
    let r = build(&i);
    assert!(r.summary.contains("Custom folder: yes"), "{}", r.summary);
    assert!(r.summary.contains("Start:         directly"), "{}", r.summary);
    assert!(!r.summary.contains("Launch opts:"), "{}", r.summary);
    assert!(!ids(&r).contains(&"proton-override"));
    assert!(r.files.iter().any(|(n, _)| n == "launcher/game-start.log"), "{:?}", r.files.iter().map(|(n, _)| n).collect::<Vec<_>>());
    // through Steam after all: the launch options count again
    crate::game::write_record(&dirs, &inst, None).unwrap();
    crate::game::set_start(&dirs, &inst.id, &crate::direct::StartSettings { mode: Some(crate::direct::StartMode::Steam), ..Default::default() }, &[]).unwrap();
    let r = build(&i);
    assert!(r.summary.contains("Start:         through Steam"), "{}", r.summary);
    assert!(finding(&r, "proton-override").text.contains(crate::game::DLL_OVERRIDE_ADVICE));
}


// ---------------------------------------------------------------- what is in a report (dev.29.22)

/// A player declined to send a report: "a little too much personal information". The launcher's "What's in this report"
/// groups the files; only other mods' and tools' files can be left out (support needs Kiln's own logs, the settings and
/// the listings).
#[test]
fn file_kinds_group_the_zip_and_only_other_mods_files_can_be_left_out() {
    for (p, g, about) in [
        ("summary.txt", FileGroup::Kiln, "summary"),
        ("report.json", FileGroup::Kiln, "findings"),
        ("game/kiln/logs/kiln.log", FileGroup::Kiln, "kiln-log"),
        ("game/kiln/logs/proxy.log", FileGroup::Kiln, "proxy-log"),
        ("game/kiln/logs/eml.log", FileGroup::Kiln, "eml-log"),
        ("game/kiln/logs/tick-stats.jsonl", FileGroup::Kiln, "tick-stats"),
        ("game/kiln/logs/greeneye-loader.log", FileGroup::Kiln, "greeneye-bridge"),
        ("game/kiln/crash-reports/crash-2026-11-10.txt", FileGroup::Kiln, "crash"),
        ("launcher/launcher.log", FileGroup::Kiln, "launcher-log"),
        ("launcher/game-start.log", FileGroup::Kiln, "game-start"),
        ("game/kiln/state/boot.json", FileGroup::Settings, "state"),
        ("game/kiln/profile.json", FileGroup::Settings, "profile"),
        ("game/kiln/chain.ini", FileGroup::Settings, "chain"),
        ("game/dinput8-chain.ini", FileGroup::Settings, "chain"),
        ("game/kiln/kiln.json", FileGroup::Settings, "kiln-json"),
        ("game/kiln/.install.json", FileGroup::Settings, "receipt"),
        ("listings/game-folder.txt", FileGroup::Listings, "game-folder"),
        ("listings/mod-folders.txt", FileGroup::Listings, "mod-folders"),
        ("game/BuildingTools.log", FileGroup::Mods, "building-tools"),
        ("game/BuildingTools.prev.log", FileGroup::Mods, "building-tools"),
        ("game/shroudlink.log", FileGroup::Mods, "shroudlink"),
        ("game/shroudlink_client.log", FileGroup::Mods, "shroudlink"),
        ("game/auto-loot-plus.log", FileGroup::Mods, "auto-loot"),
        ("game/XHL-Native-Plugin-Host.log", FileGroup::Mods, "xhl-core"),
        ("game/kiln/chain/xhl-core/XHL-Native-Plugin-Host.log", FileGroup::Mods, "xhl-core"),
        ("game/greeneye/loader.log", FileGroup::Mods, "greeneye"),
        ("greeneye-mods/unlock-map/game/unlock_map_markers.log", FileGroup::Mods, "greeneye"),
        ("game/OptiScaler.ini", FileGroup::Mods, "optiscaler-settings"),
        ("game/optiscaler.log", FileGroup::Mods, "optiscaler-log"),
        ("game/SomeNewMod.log", FileGroup::Mods, "mod-log"),
    ] {
        assert_eq!(file_kind(p), (g, about), "{p}");
        assert_eq!(excludable(p), g == FileGroup::Mods, "{p}");
    }
}

#[cfg(feature = "zip")]
#[test]
fn a_left_out_file_is_described_but_not_in_the_zip_and_kilns_own_cannot_be_left_out() {
    let f = Fx::healthy("left-out");
    f.w("BuildingTools.log", "[BT] loaded from Z:\\var\\home\\frostling\\Enshrouded\n");
    f.w("auto-loot-plus.log", "[ALP] patterns found\n");
    let mut input = f.input();
    input.exclude = vec!["game/BuildingTools.log".into(), "game/kiln/logs/kiln.log".into(), "listings/game-folder.txt".into()];
    let r = build(&input);
    assert!(file(&r, "game/BuildingTools.log").is_none(), "left out");
    assert!(file(&r, "game/auto-loot-plus.log").is_some(), "not asked for");
    assert!(file(&r, "game/kiln/logs/kiln.log").is_some() && file(&r, "listings/game-folder.txt").is_some(), "Kiln's own files and the listings stay");
    assert_eq!(r.left_out.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(), ["game/BuildingTools.log"]);
    let summary = file(&r, "summary.txt").unwrap();
    assert!(summary.contains("Left out by the player (other mods' own files):\n  game/BuildingTools.log"), "{summary}");
    let json: serde_json::Value = serde_json::from_str(file(&r, "report.json").unwrap()).unwrap();
    assert_eq!(json["leftOut"], serde_json::json!(["game/BuildingTools.log"]));
    // the zip has no trace of it
    let zip = zip_bytes(&r).unwrap();
    let mut z = zip::ZipArchive::new(std::io::Cursor::new(zip)).unwrap();
    let names: Vec<String> = (0..z.len()).map(|i| z.by_index(i).unwrap().name().unwrap().into_owned()).collect();
    assert!(!names.iter().any(|n| n.contains("BuildingTools")), "{names:?}");

    // every other mod's file at once
    input.exclude = vec![EXCLUDE_OTHER_MODS.into()];
    let all = build(&input);
    assert!(all.files.iter().all(|(n, _)| !excludable(n)), "{:?}", all.files.iter().map(|(n, _)| n).collect::<Vec<_>>());
    assert_eq!(all.left_out.len(), 2);

    // nothing asked: as before, nothing left out and no line about it
    let none = build(&f.input());
    assert!(none.left_out.is_empty() && !file(&none, "summary.txt").unwrap().contains("Left out by the player"));
}

#[test]
fn contents_counts_each_replacement_per_file() {
    let f = Fx::healthy("contents");
    f.w("kiln/logs/kiln.log", "[I] [kiln] Kiln 0.2.0-dev.22 starting (pid 412)\n[I] [kiln] path /var/home/frostling/a and /var/home/frostling/b\n\
        [I] [net] net: Wintermoth (0x81) joined with Kiln\n[I] [kiln] boot complete; handing control to the game\n");
    f.w("BuildingTools.log", "[BT] Wintermoth placed a wall at 192.168.1.20\n");
    let mut input = f.input();
    input.exclude = vec!["game/BuildingTools.log".into()];
    let r = build(&input);
    let c = contents(&r);
    let get = |p: &str| c.files.iter().find(|x| x.path == p).unwrap_or_else(|| panic!("no {p}: {:?}", c.files.iter().map(|x| &x.path).collect::<Vec<_>>()));
    let log = get("game/kiln/logs/kiln.log");
    assert_eq!((log.group, log.about.as_str(), log.excludable, log.excluded), (FileGroup::Kiln, "kiln-log", false, false));
    assert_eq!(log.replaced.get("home"), Some(&2), "{:?}", log.replaced);
    assert_eq!(log.replaced.get("player"), Some(&1), "{:?}", log.replaced);
    assert_eq!(log.size, file(&r, "game/kiln/logs/kiln.log").unwrap().len() as u64);
    let bt = get("game/BuildingTools.log");
    assert!(bt.excluded && bt.excludable && bt.group == FileGroup::Mods);
    assert_eq!((bt.replaced.get("player"), bt.replaced.get("ip")), (Some(&1), Some(&1)), "{:?}", bt.replaced);
    // summary.txt's "Left out" names every token once: that is not a replacement
    let s = get("summary.txt");
    assert!(!s.replaced.contains_key("mac") && !s.replaced.contains_key("email"), "{:?}", s.replaced);
    assert_eq!(c.size, r.files.iter().map(|(_, b)| b.len() as u64).sum::<u64>());
    assert_eq!(c.summary, r.summary);
    // the never-collected list is the summary's, item by item
    assert_eq!(c.never.iter().map(|n| n.id.as_str()).collect::<Vec<_>>(), ["saves", "settings", "admin", "mods", "steam"]);
    for n in &c.never {
        assert!(LEFT_OUT.contains(&format!("- {}", n.text)), "{}", n.text);
    }
    // a report's JSON for the launcher
    let j = serde_json::to_value(&c).unwrap();
    assert_eq!(j["files"][0]["path"], "summary.txt");
    assert!(j["files"].as_array().unwrap().iter().any(|x| x["path"] == "game/kiln/logs/kiln.log" && x["replaced"]["home"] == 2 && x["group"] == "kiln"));
}

// ---------------------------------------------------------------- support report save destinations

#[cfg(feature = "zip")]
mod save_destinations {
    use super::*;

    fn folders(f: &Fx) -> SaveFolders {
        let folders = SaveFolders { owned: f.root.join("state"), downloads: vec![f.root.join("Downloads")], home: Some(f.root.join("home")) };
        std::fs::create_dir_all(&folders.downloads[0]).unwrap();
        std::fs::create_dir_all(folders.home.as_ref().unwrap()).unwrap();
        folders
    }

    fn check_zip(saved: &(PathBuf, u64), expected: &Path) {
        assert_eq!(saved.0, expected);
        assert_eq!(saved.1, std::fs::metadata(expected).unwrap().len());
        let mut zip = zip::ZipArchive::new(std::fs::File::open(expected).unwrap()).unwrap();
        assert!(zip.by_name("summary.txt").is_ok());
    }

    #[test]
    fn direct_write_keeps_the_chosen_path() {
        let f = Fx::healthy("save-direct");
        let places = folders(&f);
        let out = f.root.join("chosen.zip");
        let mut log = Vec::new();
        let saved = save_zip(&build(&f.input()), &out, SaveDestination::Chosen, &places, |s| log.push(s)).unwrap();
        check_zip(&saved, &out);
        assert!(!places.owned.exists(), "no staging needed");
        assert!(log.iter().any(|s| s.contains("written to") && s.contains(&out.display().to_string())));
    }

    #[test]
    fn failed_adjacent_temp_uses_owned_staging_then_copies_to_chosen_file() {
        let f = Fx::healthy("save-copy");
        let places = folders(&f);
        let out = f.root.join("chosen.zip");
        std::fs::write(&out, "old report").unwrap();
        let mut log = Vec::new();
        let mut write = |r: &Report, p: &Path| {
            if p == out { return Err(crate::util::io_at(p, "creating adjacent temporary file")(std::io::Error::from(std::io::ErrorKind::PermissionDenied))); }
            write_zip(r, p)
        };
        let saved = save_zip_with(&build(&f.input()), &out, SaveDestination::Chosen, &places, &mut |s| log.push(s), &mut write).unwrap();
        check_zip(&saved, &out);
        assert!(log.iter().any(|s| s.to_lowercase().contains("permission denied")), "{log:?}");
        assert!(log.iter().any(|s| s.contains("copying") && s.contains(&out.display().to_string())), "{log:?}");
        assert_eq!(std::fs::read_dir(&places.owned).unwrap().count(), 0, "staging is cleaned up");
    }

    #[test]
    fn a_chosen_file_in_kilns_folder_can_also_use_staging_and_copy() {
        let f = Fx::healthy("save-copy-owned");
        let places = folders(&f);
        std::fs::create_dir_all(&places.owned).unwrap();
        let out = places.owned.join("chosen.zip");
        let mut write = |r: &Report, p: &Path| {
            if p == out { return Err(crate::Error::new(crate::ErrorCode::Permission, "adjacent temporary file refused")); }
            write_zip(r, p)
        };
        let saved = save_zip_with(&build(&f.input()), &out, SaveDestination::Chosen, &places, &mut |_| {}, &mut write).unwrap();
        check_zip(&saved, &out);
        assert_eq!(std::fs::read_dir(&places.owned).unwrap().count(), 1, "only the saved report remains");
    }

    #[test]
    fn chosen_write_and_copy_fail_then_downloads_is_used() {
        let f = Fx::healthy("save-downloads");
        let places = folders(&f);
        let out = f.root.join("missing/report.zip");
        let mut log = Vec::new();
        let saved = save_zip(&build(&f.input()), &out, SaveDestination::Chosen, &places, |s| log.push(s)).unwrap();
        check_zip(&saved, &places.downloads[0].join("report.zip"));
        assert!(log.iter().any(|s| s.contains("copying") && s.contains("failed")), "{log:?}");
        assert!(log.iter().any(|s| s.contains("os error")), "OS errors are kept: {log:?}");
        assert_eq!(std::fs::read_dir(&places.owned).unwrap().count(), 0);
    }

    #[test]
    fn missing_downloads_falls_back_to_home_without_creating_downloads() {
        let f = Fx::healthy("save-home");
        let places = folders(&f);
        std::fs::remove_dir(&places.downloads[0]).unwrap();
        let saved = save_zip(&build(&f.input()), &f.root.join("missing/report.zip"), SaveDestination::Chosen, &places, |_| {}).unwrap();
        check_zip(&saved, &places.home.as_ref().unwrap().join("report.zip"));
        assert!(!places.downloads[0].exists());
    }

    #[test]
    fn dialog_error_saves_in_kilns_folder_first() {
        let f = Fx::healthy("save-dialog-owned");
        let places = folders(&f);
        let saved = save_zip(&build(&f.input()), Path::new("report.zip"), SaveDestination::Automatic, &places, |_| {}).unwrap();
        check_zip(&saved, &places.owned.join("report.zip"));
    }

    #[test]
    fn dialog_error_with_unwritable_owned_folder_uses_downloads() {
        let f = Fx::healthy("save-dialog-downloads");
        let places = folders(&f);
        std::fs::write(&places.owned, "not a folder").unwrap();
        let saved = save_zip(&build(&f.input()), Path::new("report.zip"), SaveDestination::Automatic, &places, |_| {}).unwrap();
        check_zip(&saved, &places.downloads[0].join("report.zip"));
    }

    #[test]
    fn dialog_error_with_no_owned_or_downloads_folder_uses_home() {
        let f = Fx::healthy("save-dialog-home");
        let places = folders(&f);
        std::fs::write(&places.owned, "not a folder").unwrap();
        std::fs::remove_dir(&places.downloads[0]).unwrap();
        let saved = save_zip(&build(&f.input()), Path::new("report.zip"), SaveDestination::Automatic, &places, |_| {}).unwrap();
        check_zip(&saved, &places.home.as_ref().unwrap().join("report.zip"));
    }

    #[test]
    fn save_to_downloads_never_uses_the_working_directory_or_owned_destination() {
        let f = Fx::healthy("save-button");
        let places = folders(&f);
        let saved = save_zip(&build(&f.input()), Path::new("report.zip"), SaveDestination::Downloads, &places, |_| {}).unwrap();
        check_zip(&saved, &places.downloads[0].join("report.zip"));
        assert!(!places.owned.exists());
    }

    #[test]
    fn automatic_destinations_do_not_overwrite_existing_files() {
        for destination in [SaveDestination::Automatic, SaveDestination::Downloads] {
            let f = Fx::healthy(&format!("save-collision-{destination:?}"));
            let places = folders(&f);
            let dir = if matches!(destination, SaveDestination::Automatic) { &places.owned } else { &places.downloads[0] };
            std::fs::create_dir_all(dir).unwrap();
            let existing = dir.join("report.zip");
            std::fs::write(&existing, b"keep this file").unwrap();
            let saved = save_zip(&build(&f.input()), Path::new("report.zip"), destination, &places, |_| {}).unwrap();
            assert_eq!(std::fs::read(&existing).unwrap(), b"keep this file");
            check_zip(&saved, &dir.join("report-1.zip"));
        }
    }

    #[test]
    fn all_failures_are_logged_and_returned() {
        let f = Fx::healthy("save-failed");
        let mut places = folders(&f);
        std::fs::write(&places.owned, "not a folder").unwrap();
        std::fs::remove_dir(&places.downloads[0]).unwrap();
        places.home = Some(f.root.join("missing-home"));
        let mut log = Vec::new();
        let err = save_zip(&build(&f.input()), Path::new("report.zip"), SaveDestination::Automatic, &places, |s| log.push(s)).unwrap_err();
        for path in [&places.owned, &places.downloads[0], places.home.as_ref().unwrap()] {
            assert!(err.message.contains(&path.display().to_string()), "{}", err.message);
            assert!(log.iter().any(|s| s.contains("failed") && s.contains(&path.display().to_string())), "{log:?}");
        }
        assert!(err.message.contains("os error"));
        assert!(log.last().unwrap().contains("NOT saved"));
    }
}
