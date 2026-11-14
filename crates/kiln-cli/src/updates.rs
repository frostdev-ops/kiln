// SPDX-License-Identifier: MIT
//! `kiln updates check`: is a newer version of an installed mod published? A notice and a link per mod; nothing is
//! downloaded, installed or changed. `--offline` makes no request, `--refresh` asks again although a recent answer
//! is remembered (a provider's request limit still holds), `--json` prints the result set as it is.
//!
//! GitHub sources are read anonymously. Mods installed from Nexus Mods are checked by the launcher, which holds the
//! sign-in; this command never signs in and makes no request to Nexus Mods: it shows what the launcher last saw
//! (an "update available" kept and marked stale, anything else as unknown).

use anyhow::Result;
use kiln_manage::net::{self, Transport, WireRequest};
use kiln_manage::nexus::api as nexus_api;
use kiln_manage::nexus::catalog::Catalog;
use kiln_manage::paths::DataDirs;
use kiln_manage::updates::coordinator::{self, CheckOptions, CheckReport, UpdateProvider};
use kiln_manage::updates::github::{self, GithubProvider};
use kiln_manage::updates::nexus::NexusProvider;
use kiln_manage::updates::{Coverage, ModUpdateStatus, UpdateState};
use std::sync::Arc;

#[derive(clap::Args)]
pub struct UpdatesArgs {
    #[command(subcommand)]
    pub cmd: UpdatesCmd,
}

#[derive(clap::Subcommand)]
pub enum UpdatesCmd {
    /// Look for newer versions of the installed mods (a notice and a link; nothing is downloaded or installed).
    Check {
        /// Make no request: show what is remembered, marked stale where it is old.
        #[arg(long)]
        offline: bool,
        /// Ask again although a recent answer is remembered. A provider's request limit still holds.
        #[arg(long, conflicts_with = "offline")]
        refresh: bool,
    },
}

/// The transport of an offline check: it has no way to send anything.
struct NoNetwork;

impl Transport for NoNetwork {
    fn exchange(&self, req: WireRequest) -> kiln_manage::Result<net::Response> {
        Err(net::network_error(&req.url))
    }
}

/// This command's Nexus Mods transport: none. The catalog it serves is only read from disk.
struct NoNexus;

impl nexus_api::Transport for NoNexus {
    fn send(&self, _: &nexus_api::HttpRequest) -> kiln_manage::Result<nexus_api::HttpResponse> {
        Err(kiln_manage::Error::new(kiln_manage::ErrorCode::Network, "kiln updates check makes no request to Nexus Mods."))
    }
    fn open(&self, _: &str, _: &[(String, String)]) -> kiln_manage::Result<nexus_api::HttpStream> {
        Err(kiln_manage::Error::new(kiln_manage::ErrorCode::Network, "kiln updates check makes no request to Nexus Mods."))
    }
}

/// The check over a data folder with a given transport: what `run` does after it has picked the real ones.
pub fn check(dirs: &DataDirs, transport: &dyn Transport, opts: &CheckOptions) -> kiln_manage::Result<CheckReport> {
    let github = GithubProvider::new(dirs, transport, &github::user_agent());
    // signed out by construction: no token, and a transport that sends nothing
    let app = nexus_api::AppId::kiln_launcher(kiln_manage::VERSION);
    let catalog = Catalog::new(kiln_manage::nexus::catalog::root_of(dirs), nexus_api::Client::new(Arc::new(NoNexus), None, app));
    let nexus = NexusProvider::new(dirs, Some(&catalog));
    let providers: [&dyn UpdateProvider; 2] = [&github, &nexus];
    coordinator::check(dirs, &providers, opts, &mut kiln_manage::task::NoProgress)
}

/// Text that comes from a mod's files or from a provider, made safe to print: one line, printable ASCII and plain
/// letters only (no terminal escape, no control character), at most `max` characters.
pub fn plain(text: &str, max: usize) -> String {
    let mut out: String = text.chars().map(|c| if c.is_control() || c == '\u{7f}' || ('\u{80}'..='\u{9f}').contains(&c) || c == '\u{202e}' || c == '\u{2066}' || c == '\u{2067}' || c == '\u{2068}' || c == '\u{202d}' { '?' } else { c }).take(max + 1).collect();
    if out.chars().count() > max {
        out = out.chars().take(max.saturating_sub(1)).collect::<String>() + "…";
    }
    out
}

fn state_text(s: &ModUpdateStatus) -> String {
    let mut t = match s.state {
        UpdateState::Available => "update available".to_string(),
        UpdateState::NewUpload => "new upload of this version".to_string(),
        UpdateState::Current => "up to date".to_string(),
        UpdateState::Unknown => "unknown".to_string(),
        UpdateState::Unsupported => "no known source".to_string(),
        UpdateState::Disabled => "provider switched off".to_string(),
    };
    if let Some(r) = s.reason {
        t.push_str(&format!(" ({})", r.as_str()));
    }
    if s.stale {
        t.push_str(", stale");
    }
    t
}

/// The table: one line per mod, and the page to open under a mod that has a candidate.
pub fn table(r: &CheckReport) -> String {
    let rows: Vec<[String; 6]> = r.mods.iter().map(|s| {
        [
            plain(&format!("{} ({})", s.library_ref.id, s.library_ref.kind.as_str()), 48),
            plain(&s.installed_version, 24),
            state_text(s),
            plain(s.candidate_version.as_deref().unwrap_or("-"), 24),
            match (s.source.is_some(), s.coverage) {
                (false, _) => "-".to_string(),
                (true, Coverage::Complete) => "complete".to_string(),
                (true, Coverage::Incomplete) => "incomplete".to_string(),
            },
            plain(s.checked_at.as_deref().unwrap_or("never"), 24),
        ]
    }).collect();
    let head = ["MOD", "INSTALLED", "STATE", "CANDIDATE", "COVERAGE", "CHECKED"];
    let width = |i: usize| rows.iter().map(|row| row[i].chars().count()).chain([head[i].len()]).max().unwrap_or(0);
    let widths: Vec<usize> = (0..6).map(width).collect();
    let line = |cells: &[String]| cells.iter().enumerate().map(|(i, c)| format!("{c:<w$}", w = widths[i])).collect::<Vec<_>>().join("  ").trim_end().to_string();
    let mut out = line(&head.map(str::to_string)) + "\n";
    for (row, s) in rows.iter().zip(&r.mods) {
        out.push_str(&line(row));
        out.push('\n');
        if let Some(url) = s.page_url.as_deref().filter(|_| matches!(s.state, UpdateState::Available | UpdateState::NewUpload)) {
            out.push_str(&format!("    page: {}\n", plain(url, 300)));
        }
    }
    let n = |st: UpdateState| r.mods.iter().filter(|s| s.state == st).count();
    out.push_str(&format!("{} mod(s): {} with an update, {} up to date, {} unknown, {} without a source{}\n", r.mods.len(),
        n(UpdateState::Available) + n(UpdateState::NewUpload), n(UpdateState::Current), n(UpdateState::Unknown) + n(UpdateState::Disabled), n(UpdateState::Unsupported),
        if r.offline { " (offline: no request was made)" } else { "" }));
    out
}

/// The JSON document of `--json`: the result set with `ok`.
pub fn document(r: &CheckReport) -> serde_json::Value {
    let mut v = serde_json::to_value(r).unwrap_or_default();
    if let Some(o) = v.as_object_mut() {
        o.insert("ok".into(), serde_json::Value::Bool(true));
    }
    v
}

pub fn run(a: UpdatesArgs, json: bool) -> Result<i32> {
    let UpdatesCmd::Check { offline, refresh } = a.cmd;
    let dirs = DataDirs::locate()?;
    let opts = CheckOptions { offline, refresh, ..CheckOptions::manual() };
    let report = if offline {
        check(&dirs, &NoNetwork, &opts)?
    } else {
        check(&dirs, &net::UreqTransport::with_timeouts(github::REQUEST_TIMEOUT, github::REQUEST_TIMEOUT), &opts)?
    };
    if json {
        crate::out::print_json(&document(&report));
    } else {
        print!("{}", table(&report));
    }
    Ok(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use kiln_manage::net::fake::{FakeTransport, Reply};
    use kiln_manage::net::Method;
    use kiln_manage::task::NoProgress;
    use std::path::{Path, PathBuf};

    const NOW: i64 = 1_800_000_000;

    struct Tmp(PathBuf);
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn opts(now: i64) -> CheckOptions {
        CheckOptions { offline: false, refresh: false, automatic: false, now, api_version: "0.2.3".into() }
    }

    fn fixture(name: &str) -> Vec<u8> {
        std::fs::read(Path::new(env!("CARGO_MANIFEST_DIR")).join("../kiln-manage/tests/fixture/updates-github").join(name)).unwrap()
    }

    const LIST: &str = "https://api.github.com/repos/example-org/lantern-glow/releases?per_page=100&page=1";
    const BINDING: &str = "https://api.github.com/repos/example-org/lantern-glow/releases/assets/7052";

    /// A data folder with three mods in its library: one imported by hand with no source, one the Nexus catalog
    /// installed (its record says version 1.2.0 of file 90310, and the launcher last saw 1.3.0 there), one whose
    /// manifest names a GitHub repository.
    fn three_mods(tag: &str) -> (Tmp, DataDirs) {
        let root = std::env::temp_dir().join(format!("kiln-cli-updates-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let dirs = DataDirs::at(root.join("data"));
        let src = root.join("src");
        let folder = |name: &str, manifest: &str| {
            std::fs::create_dir_all(src.join(name)).unwrap();
            std::fs::write(src.join(name).join("kiln.mod.json"), manifest).unwrap();
            src.join(name)
        };
        let folders = [
            folder("local", r#"{"id": "local-tweaks", "name": "Local \u001b[31mTweaks", "version": "0.4.0", "apiVersion": "0.2"}"#),
            folder("nexus", r#"{"id": "night-sky", "name": "Night Sky", "version": "1.2.0", "apiVersion": "0.2"}"#),
            folder("github", r#"{"id": "lantern-glow", "name": "Lantern Glow", "version": "1.9.0", "apiVersion": "0.2",
                "updates": {"provider": "github", "repository": "example-org/lantern-glow", "asset": "lantern-glow-{version}.zip"}}"#),
        ];
        let report = kiln_manage::library::import(&dirs, &folders, &mut NoProgress).unwrap();
        assert_eq!(report.imported.len(), 3, "{report:?}");
        let sky = report.imported.iter().find(|r| r.id == "night-sky").unwrap();
        let nexus = kiln_manage::nexus::catalog::root_of(&dirs);
        std::fs::create_dir_all(&nexus).unwrap();
        std::fs::write(nexus.join("installs.json"), serde_json::to_vec(&serde_json::json!({"format": 1, "mods": [{
            "modId": "313", "name": "Night Sky", "fileId": "90313", "fileName": "Main file", "versionId": "8010032", "versionGsid": "1003",
            "version": "1.2.0", "refs": [sky], "installed": "2026-11-07T10:00:00Z", "source": "nxm"}]})).unwrap()).unwrap();
        std::fs::write(nexus.join("latest.json"), serde_json::to_vec(&serde_json::json!({"90313": {"version": "1.3.0", "versionGsid": "1009", "checked": NOW - 600}})).unwrap()).unwrap();
        (Tmp(root), dirs)
    }

    fn of<'a>(r: &'a CheckReport, id: &str) -> &'a ModUpdateStatus {
        r.mods.iter().find(|m| m.library_ref.id == id).unwrap()
    }

    #[test]
    fn three_mods_one_result_set_and_offline_asks_nothing() {
        let (_t, dirs) = three_mods("check");
        // offline before anything was ever checked: zero requests, and nothing is called up to date
        let none = FakeTransport::offline();
        let r = check(&dirs, &none, &CheckOptions { offline: true, ..opts(NOW) }).unwrap();
        assert!(none.requests().is_empty(), "{:?}", none.requests());
        assert_eq!(r.mods.len(), 3);
        assert!(r.offline && r.mods.iter().all(|m| m.state != UpdateState::Current));
        assert_eq!((of(&r, "lantern-glow").state, of(&r, "lantern-glow").reason.map(|x| x.as_str())), (UpdateState::Unknown, Some("not_checked")));

        // online: the GitHub mod is read from the fake; nothing else is asked anything
        let t = FakeTransport::new();
        t.reply(Method::Get, LIST, Reply::bytes(200, &fixture("lantern-glow.releases.json")).with_header("ETag", "\"1\""));
        t.reply(Method::Get, BINDING, Reply::bytes(200, &fixture("lantern-glow.kiln-release.json")));
        let r = check(&dirs, &t, &opts(NOW)).unwrap();
        assert!(t.unexpected().is_empty() && t.unused().is_empty(), "{:?} {:?}", t.unexpected(), t.unused());
        assert_eq!(t.requests().iter().map(|x| x.url.as_str()).collect::<Vec<_>>(), [LIST, BINDING]);
        // local: no source
        let local = of(&r, "local-tweaks");
        assert_eq!((local.state, local.source.as_ref(), local.reason), (UpdateState::Unsupported, None, None));
        // Nexus: this command is signed out; what the launcher last saw is kept, marked stale
        let sky = of(&r, "night-sky");
        let src = sky.source.as_ref().unwrap();
        assert_eq!((src.provider.as_str(), src.destination.as_str(), src.authenticated), ("nexus", "enshrouded/313/90313", true));
        assert_eq!((sky.state, sky.stale, sky.reason.map(|x| x.as_str()), sky.candidate_version.as_deref()), (UpdateState::Available, true, Some("network"), Some("1.3.0")));
        // GitHub: 1.10.0 is published
        let glow = of(&r, "lantern-glow");
        assert_eq!((glow.state, glow.stale, glow.candidate_version.as_deref()), (UpdateState::Available, false, Some("1.10.0")));
        assert_eq!(glow.page_url.as_deref(), Some("https://github.com/example-org/lantern-glow/releases/tag/v1.10.0"));

        // the JSON is the result set: every status with every key of ModUpdateStatus, and it reads back as one
        let doc = document(&r);
        assert_eq!((doc["ok"].as_bool(), doc["format"].as_u64(), doc["offline"].as_bool(), doc["checkedAt"].as_str()), (Some(true), Some(1), Some(false), Some("2027-01-15T08:00:00Z")));
        let mods = doc["mods"].as_array().unwrap();
        assert_eq!(mods.len(), 3);
        const KEYS: [&str; 13] = ["candidateIdentity", "candidateVersion", "checkedAt", "compatibility", "coverage", "installedVersion", "libraryRef", "name",
            "pageUrl", "reason", "source", "stale", "state"];
        for (m, s) in mods.iter().zip(&r.mods) {
            let mut keys: Vec<&str> = m.as_object().unwrap().keys().map(String::as_str).collect();
            keys.sort();
            assert_eq!(keys, KEYS);
            assert_eq!(&serde_json::from_value::<ModUpdateStatus>(m.clone()).unwrap(), s);
        }
        let g = mods.iter().find(|m| m["libraryRef"]["id"] == "lantern-glow").unwrap();
        assert_eq!((g["state"].as_str(), g["compatibility"].as_str(), g["coverage"].as_str(), g["stale"].as_bool(), g["reason"].is_null()),
            (Some("available"), Some("compatible"), Some("complete"), Some(false), true));
        assert_eq!(g["source"], serde_json::json!({"provider": "github", "destination": "example-org/lantern-glow", "origin": "manifest", "authenticated": false}));
        assert_eq!(g["candidateIdentity"]["assetName"], "lantern-glow-1.10.0.zip");
        let l = mods.iter().find(|m| m["libraryRef"]["id"] == "local-tweaks").unwrap();
        assert!(l["source"].is_null() && l["candidateVersion"].is_null() && l["state"] == "unsupported");

        // offline afterwards: zero requests again, the same answers from what is remembered
        let none = FakeTransport::offline();
        let again = check(&dirs, &none, &CheckOptions { offline: true, ..opts(NOW + 60) }).unwrap();
        assert!(none.requests().is_empty());
        assert_eq!((of(&again, "lantern-glow").state, of(&again, "lantern-glow").stale), (UpdateState::Available, false));
        assert_eq!(of(&again, "night-sky").state, UpdateState::Available);

        // nothing was installed or changed by any of it: the library holds the three versions it held
        let lib = kiln_manage::library::list(&dirs, None).unwrap();
        assert_eq!(lib.iter().map(|e| (e.id.as_str(), e.versions.len(), e.versions[0].version.as_str())).collect::<Vec<_>>(),
            [("lantern-glow", 1, "1.9.0"), ("local-tweaks", 1, "0.4.0"), ("night-sky", 1, "1.2.0")]);
        assert!(!dirs.profiles().exists());

        // the table: a line per mod, the page under a mod with a candidate, and a mod's own text cannot steer the terminal
        let text = table(&r);
        assert!(!text.contains('\u{1b}'), "{text:?}");
        assert!(text.contains("lantern-glow (kiln)") && text.contains("update available") && text.contains("1.10.0"), "{text}");
        assert!(text.contains("    page: https://github.com/example-org/lantern-glow/releases/tag/v1.10.0"), "{text}");
        assert!(text.contains("no known source") && text.contains("update available (network), stale"), "{text}");
        assert!(text.contains("3 mod(s): 2 with an update, 0 up to date, 0 unknown, 1 without a source"), "{text}");
    }

    #[test]
    fn untrusted_text_is_printed_as_plain_text() {
        assert_eq!(plain("1.2.0", 24), "1.2.0");
        assert_eq!(plain("\u{1b}[2J\u{1b}[31mowned\r\n\tnext\u{7}", 80), "?[2J?[31mowned???next?");
        assert_eq!(plain("a\u{9b}b\u{202e}c", 80), "a?b?c");
        assert_eq!(plain(&"x".repeat(50), 10), format!("{}…", "x".repeat(9)));
        assert_eq!(plain("Größe ✓", 80), "Größe ✓", "letters of any language stay");
        // through the table: a version and a page that try
        let mut s = ModUpdateStatus::new(
            kiln_manage::library::LibraryRef { kind: kiln_manage::manifest::ModKind::Kiln, id: "m".into(), version: "1.0.0\u{1b}]0;title\u{7}".into(), hash: "kdh1:0".into() },
            "M", None).with_state(UpdateState::Available);
        s.source = Some(kiln_manage::updates::SourceInfo { provider: kiln_manage::updates::Provider::Github, destination: "a/b".into(), origin: kiln_manage::updates::SourceOrigin::Manifest, authenticated: false });
        s.candidate_version = Some("2.0.0\nMOD  INSTALLED".into());
        s.page_url = Some("https://github.com/a/b/releases/tag/v2\u{1b}[8m".into());
        let r = CheckReport { format: 1, checked_at: "2027-01-15T08:00:00Z".into(), offline: false, mods: vec![s] };
        let text = table(&r);
        assert!(!text.contains('\u{1b}') && !text.contains('\u{7}'), "{text:?}");
        assert_eq!(text.lines().count(), 4, "a line break in a version does not make a line: {text}");
    }
}
