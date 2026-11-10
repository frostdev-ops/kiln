//! The catalog against a mock Nexus Mods (API, storage, CDN): search and the Kiln-mod rule, details, the key and app
//! headers, rate limits, download + check + add + Undo, updates, the nxm handler, the key store, the link inbox.

use super::api::*;
use super::catalog::*;
use super::handler::{self, HandlerPlatform, Previous};
use super::install::{self, InstallRequest};
use super::keystore::{TokenPlace, TokenStore, SecretBackend};
use crate::game::{GameInstall, GameKind, PathMode, Platform};
use crate::paths::DataDirs;
use crate::{ErrorCode, Result};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

const API: &str = "https://api.test";
const KEY: &str = "OAUTH-ACCESS-TOKEN-123";

// ---------------------------------------------------------------- the mock

type Route = Box<dyn Fn(&HttpRequest) -> HttpResponse + Send + Sync>;

#[derive(Default)]
struct Mock {
    routes: Mutex<Vec<(String, String, Route)>>,
    log: Mutex<Vec<HttpRequest>>,
    files: Mutex<HashMap<String, Vec<u8>>>,
}

fn ok(v: Value) -> HttpResponse {
    HttpResponse { status: 200, headers: vec![("content-type".into(), "application/json".into())], body: serde_json::to_vec(&v).unwrap() }
}

impl Mock {
    fn on(&self, method: &str, prefix: &str, f: impl Fn(&HttpRequest) -> HttpResponse + Send + Sync + 'static) {
        self.routes.lock().unwrap().insert(0, (method.into(), prefix.into(), Box::new(f)));
    }
    fn json(&self, method: &str, prefix: &str, v: Value) {
        self.on(method, prefix, move |_| ok(v.clone()));
    }
    fn calls(&self, needle: &str) -> usize {
        self.log.lock().unwrap().iter().filter(|r| r.url.contains(needle)).count()
    }
}

impl Transport for Mock {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
        self.log.lock().unwrap().push(req.clone());
        let routes = self.routes.lock().unwrap();
        for (m, p, f) in routes.iter() {
            if m == req.method && req.url.starts_with(p.as_str()) && (req.url.len() == p.len() || req.url.as_bytes()[p.len()] == b'?') {
                return Ok(f(req));
            }
        }
        Ok(HttpResponse { status: 404, headers: vec![], body: br#"{"title":"Not Found","status":404,"detail":"no route"}"#.to_vec() })
    }
    fn open(&self, url: &str, headers: &[(String, String)]) -> Result<HttpStream> {
        self.log.lock().unwrap().push(HttpRequest { method: "GET", url: url.into(), headers: headers.to_vec(), body: None });
        let files = self.files.lock().unwrap();
        let key = url.split('?').next().unwrap();
        match files.get(key) {
            Some(b) => Ok(HttpStream { status: 200, length: Some(b.len() as u64), reader: Box::new(std::io::Cursor::new(b.clone())) }),
            None => Ok(HttpStream { status: 404, length: None, reader: Box::new(std::io::empty()) }),
        }
    }
}

fn summary(gsid: &str, name: &str, updated: &str, endorsements: u64) -> Value {
    json!({
        "id": format!("2495805495{gsid:0>4}"), "game_scoped_id": gsid, "game": {"id": "5811", "domain_name": "enshrouded", "name": "Enshrouded"},
        "name": name, "summary": format!("{name} summary"), "version": "1.0.0", "author": {"id": "7", "name": "author"},
        "category": {"id": "2", "name": "Gameplay"}, "thumbnail_url": format!("https://img.test/{gsid}.png"), "blurred_thumbnail_url": null,
        "endorsements": endorsements, "downloads": endorsements * 10, "unique_downloads": endorsements * 5, "file_size_kilobytes": 10,
        "created_at": "2026-10-01T00:00:00Z", "updated_at": updated, "adult": false, "supports_vortex": false
    })
}
fn uid(gsid: &str) -> String {
    format!("2495805495{gsid:0>4}")
}

/// A mod with one active file whose newest version (gsid `vgsid`) has `paths` in its manifest.
fn mod_with_files(m: &Mock, gsid: &str, vgsid: &str, version: &str, paths: &[(&str, u64)]) {
    let file_id = format!("90{gsid}");
    m.json("GET", &format!("{API}/v3/mods/{}/files", uid(gsid)), json!({"data": {"mod_files": [
        {"id": file_id, "name": "Main file", "is_active": true, "last_file_uploaded_at": "2026-11-05T00:00:00Z", "versions_count": 1, "archived_count": 0, "removed_count": 0}
    ]}}));
    let manifest = format!("https://files.test/manifest/{vgsid}.json");
    m.json("GET", &format!("{API}/v3/mod-files/{file_id}/versions"), json!({"data": {"versions": [
        version_json(gsid, vgsid, version, "1", &manifest, "old_version"),
        version_json(gsid, vgsid, version, "2", &manifest, "main"),
    ]}}));
    m.json("GET", &manifest, json!({"files": paths.iter().map(|(p, s)| json!({"path": p, "size": s})).collect::<Vec<_>>()}));
}

fn version_json(gsid: &str, vgsid: &str, version: &str, position: &str, manifest: &str, category: &str) -> Value {
    json!({"id": format!("80{vgsid}{position}"), "file": {"id": format!("90{gsid}"), "name": "Main file"}, "position": position,
        "game_scoped_id": if category == "main" { vgsid.to_string() } else { format!("{vgsid}0") }, "name": format!("mod-{gsid}-{version}.zip"), "version": version,
        "category": category, "uploaded_at": format!("2026-11-0{position}T00:00:00Z"), "manifest_url": manifest, "is_primary": category == "main"})
}

fn setup(key: Option<&str>) -> (Arc<Mock>, Catalog, Tmp) {
    let m = Arc::new(Mock::default());
    // two search pages: cursor paging
    m.on("POST", &format!("{API}/v3/mods/search"), |req| {
        let body: Value = serde_json::from_slice(req.body.as_deref().unwrap()).unwrap();
        assert_eq!(body["filters"]["games"], json!(["enshrouded"]));
        if body["pagination"]["cursor"].is_null() {
            ok(json!({"data": {"mods": [
                summary("301", "Kiln", "2026-11-07T00:00:00Z", 50),
                summary("310", "Lantern Glow", "2026-11-05T00:00:00Z", 40),
                summary("311", "Old Texture Pack", "2026-03-01T00:00:00Z", 30),
            ]}, "meta": {"type": "cursor", "next_cursor": "c2", "total_count": 5}}))
        } else {
            ok(json!({"data": {"mods": [
                summary("312", "Bigger Bags EMF", "2026-11-03T00:00:00Z", 20),
                summary("313", "Night Sky", "2026-11-06T00:00:00Z", 10),
            ]}, "meta": {"type": "cursor", "next_cursor": null, "total_count": 5}}))
        }
    });
    mod_with_files(&m, "310", "1001", "1.2.0", &[("lantern-glow/kiln.mod.json", 120), ("lantern-glow/LanternGlow.dll", 2048)]);
    mod_with_files(&m, "312", "1002", "2.0", &[("BiggerBags/mod.json", 80), ("BiggerBags/BiggerBags.dll", 4096)]);
    mod_with_files(&m, "313", "1003", "0.3.1", &[("kiln.mod.json", 99), ("NightSky.dll", 777)]);
    let t = tmp("cat");
    let client = Client::with_origin(API, m.clone(), key.map(str::to_string), AppId::kiln_launcher("0.2.0-test")).without_gap();
    (m, Catalog::new(t.0.join("data/nexus"), client), t)
}

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    static N: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    let p = std::env::temp_dir().join(format!("km-nexus-{tag}-{}-{}", std::process::id(), N.fetch_add(1, std::sync::atomic::Ordering::Relaxed)));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}
fn dirs(t: &Tmp) -> DataDirs {
    DataDirs::at(t.0.join("data"))
}

// ---------------------------------------------------------------- search and the Kiln rule

#[test]
fn kiln_filter_finds_mods_with_kiln_mod_json_and_never_checks_old_ones() {
    let (m, cat, t) = setup(Some(KEY));
    let p = cat.search(&dirs(&t), &Query::default()).unwrap();
    let names: Vec<&str> = p.mods.iter().map(|x| x.name.as_str()).collect();
    assert_eq!(names, ["Lantern Glow", "Night Sky"], "endorsements order, Kiln mods only");
    assert!(p.mods.iter().all(|x| x.kind == Some(ModType::Kiln)));
    assert_eq!((p.total, p.unchecked, p.needs_sign_in), (2, 0, false));
    // Kiln's own page and a mod older than Kiln were never looked at
    assert_eq!(m.calls(&format!("/mods/{}/files", uid("301"))), 0);
    assert_eq!(m.calls(&format!("/mods/{}/files", uid("311"))), 0);
    assert_eq!(m.calls("/v3/mods/search"), 2, "two cursor pages");
    // the EMF mod was classified as such and kept for "All"
    let all = cat.search(&dirs(&t), &Query { filter: Filter::All, ..Default::default() }).unwrap();
    assert_eq!(all.total, 5);
    let emf = all.mods.iter().find(|x| x.id == "312").unwrap();
    assert_eq!(emf.kind, Some(ModType::Emf));
    assert_eq!(all.mods.iter().find(|x| x.id == "311").unwrap().kind, None);
    // answers are cached per updated_at: a second search (list cached too) asks nothing
    let before = m.log.lock().unwrap().len();
    cat.search(&dirs(&t), &Query { text: "night".into(), ..Default::default() }).unwrap();
    assert_eq!(m.log.lock().unwrap().len(), before);
    // and on disk: a new catalog (new process) reads verdicts.json
    let cat2 = Catalog::new(cat.root().to_path_buf(), Client::with_origin(API, m.clone(), Some(KEY.into()), AppId::kiln_launcher("x")).without_gap());
    assert_eq!(cat2.cached_verdict(&uid("310"), "2026-11-05T00:00:00Z").unwrap().kind, ModType::Kiln);
    assert!(cat2.cached_verdict(&uid("310"), "2026-11-09T00:00:00Z").is_none(), "a changed page is looked at again");
}

#[test]
fn text_search_sort_and_paging() {
    let (_m, cat, t) = setup(Some(KEY));
    let q = |text: &str, sort: Sort, page: u32, size: u32| Query { text: text.into(), filter: Filter::All, sort, page, page_size: size, ..Default::default() };
    let p = cat.search(&dirs(&t), &q("glow summary", Sort::Endorsements, 1, 24)).unwrap();
    assert_eq!(p.mods.iter().map(|x| x.id.as_str()).collect::<Vec<_>>(), ["310"]);
    let p = cat.search(&dirs(&t), &q("", Sort::Name, 2, 2)).unwrap();
    assert_eq!(p.mods.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), ["Lantern Glow", "Night Sky"]);
    assert_eq!((p.total, p.page, p.page_size), (5, 2, 2));
    let p = cat.search(&dirs(&t), &q("", Sort::Updated, 1, 1)).unwrap();
    assert_eq!(p.mods[0].id, "301");
}

#[test]
fn signed_out_the_kiln_filter_says_so_and_all_still_lists() {
    let (m, cat, t) = setup(None);
    let p = cat.search(&dirs(&t), &Query::default()).unwrap();
    assert!(p.needs_sign_in && p.mods.is_empty());
    let all = cat.search(&dirs(&t), &Query { filter: Filter::All, ..Default::default() }).unwrap();
    assert_eq!(all.total, 5);
    assert_eq!(m.calls("/files"), 0, "no key: no file calls");
    assert!(m.log.lock().unwrap().iter().all(|r| !r.headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("authorization"))));
}

#[test]
fn checks_are_batched_and_the_rest_reported_unchecked() {
    let (m, cat, t) = setup(Some(KEY));
    // many new mods without Kiln files
    let many: Vec<Value> = (400..430).map(|i| summary(&i.to_string(), &format!("Mod {i}"), "2026-11-04T00:00:00Z", 1)).collect();
    m.json("POST", &format!("{API}/v3/mods/search"), json!({"data": {"mods": many}, "meta": {"type": "cursor", "next_cursor": null, "total_count": 30}}));
    for i in 400..430 {
        mod_with_files(&m, &i.to_string(), &format!("{i}9"), "1", &[("data/textures.pak", 5)]);
    }
    let p = cat.search(&dirs(&t), &Query::default()).unwrap();
    assert_eq!((p.total, p.unchecked), (0, 30 - CHECK_BATCH));
    let p = cat.search(&dirs(&t), &Query::default()).unwrap();
    assert_eq!(p.unchecked, 30 - 2 * CHECK_BATCH);
    let p = cat.search(&dirs(&t), &Query::default()).unwrap();
    assert_eq!(p.unchecked, 0);
}

#[test]
fn the_token_goes_to_the_api_only_and_every_request_names_the_app() {
    let (m, cat, t) = setup(Some(KEY));
    cat.search(&dirs(&t), &Query::default()).unwrap();
    let log = m.log.lock().unwrap();
    assert!(!log.is_empty());
    for r in log.iter() {
        let h = |k: &str| r.headers.iter().find(|(n, _)| n.eq_ignore_ascii_case(k)).map(|(_, v)| v.as_str());
        assert_eq!(h("Application-Name"), Some("Kiln Launcher"), "{}", r.url);
        assert_eq!(h("Application-Version"), Some("0.2.0-test"));
        assert!(h("User-Agent").unwrap().starts_with("Kiln-Launcher/0.2.0-test ("));
        if r.url.starts_with(API) {
            assert_eq!(h("Authorization"), Some(format!("Bearer {KEY}").as_str()));
        } else {
            assert_eq!(h("Authorization"), None, "no key to {}", r.url);
        }
    }
    assert!(log.iter().any(|r| r.url.starts_with("https://files.test/")), "manifests were read from storage");
    assert!(!format!("{:?}", cat.client()).contains(KEY), "Debug hides the key");
}

// ---------------------------------------------------------------- rate limits and errors

#[test]
fn rate_limits_are_respected() {
    let m = Arc::new(Mock::default());
    m.on("GET", &format!("{API}/v3/games/enshrouded/mods/5"), |_| HttpResponse { status: 429, headers: vec![("Retry-After".into(), "120".into())], body: vec![] });
    let c = Client::with_origin(API, m.clone(), Some(KEY.into()), AppId::kiln_launcher("t")).without_gap();
    assert_eq!(c.mod_details("enshrouded", "5").unwrap_err().code, ErrorCode::Busy);
    // blocked: the next call is refused without a request
    let n = m.log.lock().unwrap().len();
    assert_eq!(c.mod_details("enshrouded", "5").unwrap_err().code, ErrorCode::Busy);
    assert_eq!(m.log.lock().unwrap().len(), n);
    assert!(c.rate().blocked_for > 100);

    // the budget headers: below the reserve in both, requests stop
    let m = Arc::new(Mock::default());
    m.on("GET", &format!("{API}/v3/games/enshrouded/mods/6"), |_| {
        let mut r = ok(json!({"data": {"id": "1", "game_scoped_id": "6", "name": "x"}}));
        r.headers.push(("x-rl-hourly-remaining".into(), "3".into()));
        r.headers.push(("x-rl-daily-remaining".into(), "2".into()));
        r
    });
    let c = Client::with_origin(API, m.clone(), Some(KEY.into()), AppId::kiln_launcher("t")).without_gap();
    c.mod_details("enshrouded", "6").unwrap();
    assert_eq!(c.rate().hourly_remaining, Some(3));
    assert_eq!(c.mod_details("enshrouded", "6").unwrap_err().code, ErrorCode::Busy);

    // errors: a bad key, ids that are not numbers (never sent)
    let m = Arc::new(Mock::default());
    m.on("GET", &format!("{API}/v1/users/validate.json"), |_| HttpResponse { status: 401, headers: vec![], body: br#"{"message":"Please provide a valid API Key"}"#.to_vec() });
    let c = Client::with_origin(API, m.clone(), Some(KEY.into()), AppId::kiln_launcher("t")).without_gap();
    let e = c.validate().unwrap_err();
    assert_eq!(e.code, ErrorCode::AuthFailed);
    assert!(!e.message.contains(KEY));
    assert_eq!(c.mod_details("enshrouded", "../x").unwrap_err().code, ErrorCode::InvalidArgument);
    assert_eq!(m.calls("../x"), 0);
}

#[test]
fn the_account_keeps_name_and_premium_only() {
    let m = Arc::new(Mock::default());
    m.json("GET", &format!("{API}/v1/users/validate.json"), json!({"user_id": 1, "key": KEY, "name": "frost", "email": "a@b.c", "is_premium?": true, "is_premium": true, "is_supporter": false}));
    let c = Client::with_origin(API, m, Some(KEY.into()), AppId::kiln_launcher("t")).without_gap();
    let a = c.validate().unwrap();
    assert_eq!(a, Account { name: "frost".into(), premium: true });
    assert!(!serde_json::to_string(&a).unwrap().contains('@'));
}

// ---------------------------------------------------------------- details

#[test]
fn details_have_text_images_files_and_requirements() {
    let (m, cat, t) = setup(Some(KEY));
    cat.search(&dirs(&t), &Query { filter: Filter::All, ..Default::default() }).unwrap();
    m.json("GET", &format!("{API}/v3/games/enshrouded/mods/310"), json!({"data": {
        "id": uid("310"), "game_scoped_id": "310", "game_id": "5811", "name": "Lantern Glow",
        "description": "[center][b]Warm light[/b][/center]<br />[list][*]Brighter[*]Warmer[/list]<br /><br /><br />[url=https://x]Wiki[/url] &amp; more[img]https://i/x.png[/img]",
        "version": "1.2.0", "author": "Frost & co", "adult_content": false, "uploader": {"id": "7", "name": "frost"}, "category": {"id": "2", "name": "Gameplay"},
        "stats": {"downloads": 100, "unique_downloads": 60, "views": 0, "endorsements": 40}, "tags": [{"id": "1", "name": "Quality of Life"}], "status": "published",
        "primary_image": {"url": "https://img.test/p.png", "thumbnail_url": "https://img.test/pt.png", "large_thumbnail_url": "https://img.test/pl.png"},
        "gallery_images": [{"id": "1", "title": "", "url": "https://img.test/1.png", "thumbnail_url": "t", "large_thumbnail_url": "https://img.test/1l.png"}],
        "primary_file_version": null
    }}));
    m.json("GET", &format!("{API}/v3/mod-file-versions/8010012/dependencies"), json!({"dependency_definitions": [
        {"id": "d1", "ranges": [{"id": "r1", "target_mod_file": {"id": "f301", "name": "Kiln Launcher (Linux)", "mod": {"id": uid("301"), "game_scoped_id": "301", "name": "Kiln", "game": {"id": "5811", "name": "Enshrouded", "domain_name": "enshrouded"}}},
            "min_version": {"id": "x", "file": {"id": "f301", "name": "Kiln"}, "position": "1", "game_scoped_id": "5", "name": "k", "version": "0.2.0", "category": "main", "uploaded_at": "", "manifest_url": null}, "max_version": null}]},
        {"id": "d2", "ranges": [{"id": "r2", "target_mod_file": {"id": "f313", "name": "Main file", "mod": {"id": uid("313"), "game_scoped_id": "313", "name": "Night Sky", "game": {"id": "5811", "name": "Enshrouded", "domain_name": "enshrouded"}}},
            "min_version": null, "max_version": null}]}
    ], "dlc_dependency_definitions": []}));
    let d = cat.details(&dirs(&t), "310").unwrap();
    assert_eq!(d.summary.name, "Lantern Glow");
    assert_eq!(d.summary.kind, Some(ModType::Kiln));
    assert_eq!(d.description, "Warm light\n\n• Brighter\n• Warmer\n\nWiki & more");
    assert_eq!(d.credit.as_deref(), Some("Frost & co"));
    assert_eq!(d.images.len(), 1);
    assert_eq!(d.images[0].thumb.as_deref(), Some("https://img.test/1l.png"));
    assert_eq!(d.files.len(), 1);
    let best = d.best_file.as_ref().unwrap();
    assert_eq!((best.version_gsid.as_str(), best.version.as_str(), best.kind), ("1001", "1.2.0", ModType::Kiln));
    assert_eq!(d.files_url, "https://www.nexusmods.com/enshrouded/mods/310?tab=files&file_id=1001&nmm=1");
    assert_eq!(d.requirements.len(), 2);
    assert!(d.requirements[0].kiln && d.requirements[0].min_version.as_deref() == Some("0.2.0"));
    assert!(!d.requirements[1].kiln && !d.requirements[1].installed);
    assert_eq!(d.requirements[1].url, "https://www.nexusmods.com/enshrouded/mods/313");
}

#[test]
fn bbcode_and_manifest_shapes() {
    assert_eq!(bbcode_to_text("a[b]b[/b] [x c"), "ab [x c");
    let tree = json!({"children": [{"type": "directory", "name": "Mod", "path": "Mod", "children": [
        {"type": "file", "name": "kiln.mod.json", "path": "Mod/kiln.mod.json", "size": "1.2 kB"}]}]});
    let e = parse_manifest(&tree);
    assert_eq!(e.iter().map(|x| x.path.as_str()).collect::<Vec<_>>(), ["Mod/kiln.mod.json"]);
    assert_eq!(e[0].size, None, "text sizes are not used to check");
    let flat = json!({"files": [{"path": "/a/b.dll", "size": 12, "sha256": "AB".repeat(32)}, {"path": "a/", "size": 0}]});
    let e = parse_manifest(&flat);
    assert_eq!(e, vec![ManifestEntry { path: "a/b.dll".into(), size: Some(12), sha256: Some("ab".repeat(32)) }]);
    let nested = json!({"name": "Root", "type": "directory", "children": [{"name": "x.dll", "size": 3}]});
    assert_eq!(parse_manifest(&nested)[0].path, "Root/x.dll");
    assert_eq!(classify(&["A/KILN.MOD.JSON"]), ModType::Kiln);
    assert_eq!(classify(&["a/native.json", "a/x.dll"]), ModType::Native);
    assert_eq!(classify(&["a/x.dll"]), ModType::Dll);
    assert_eq!(classify(&["a/mod.json", "a/readme.txt"]), ModType::Other);
}

// ---------------------------------------------------------------- download, check, add, updates

fn zip_of(files: &[(&str, &[u8])]) -> Vec<u8> {
    use std::io::Write;
    let mut buf = std::io::Cursor::new(Vec::new());
    {
        let mut z = zip::ZipWriter::new(&mut buf);
        for (n, d) in files {
            z.start_file(*n, zip::write::SimpleFileOptions::default()).unwrap();
            z.write_all(d).unwrap();
        }
        z.finish().unwrap();
    }
    buf.into_inner()
}

fn game(root: &Path) -> GameInstall {
    let g = root.join("game");
    std::fs::create_dir_all(g.join("kiln")).unwrap();
    std::fs::write(g.join("enshrouded.exe"), b"MZ").unwrap();
    GameInstall {
        id: "g-nexus".into(), kind: GameKind::Client, path: g, exe: "enshrouded.exe".into(), app_id: crate::CLIENT_APP_ID,
        library: None, platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None,
        path_mode: PathMode::Copy, source: "manual".into(),
    }
}

const MANIFEST: &[u8] = br#"{ "id": "lantern-glow", "name": "Lantern Glow", "version": "1.2.0", "apiVersion": "0.2", "entryAssembly": "LanternGlow.dll", "side": "both" }"#;

fn serve_download(m: &Mock, gsid: &str, vgsid: &str, zip: Vec<u8>, sizes: &[(&str, u64)], premium_only: bool) {
    let link = format!("https://cdn.test/files/{vgsid}/lantern-glow-1.2.0.zip");
    m.files.lock().unwrap().insert(link.clone(), zip);
    let l2 = link.clone();
    m.on("GET", &format!("{API}/v3/games/enshrouded/mod-file-versions/{vgsid}/download-link"), move |req| {
        let has_key = req.url.contains("key=dl-key") && req.url.contains("expires=");
        if premium_only && !has_key {
            return HttpResponse { status: 403, headers: vec![], body: br#"{"title":"Forbidden","status":403,"detail":"Free users must download through the website."}"#.to_vec() };
        }
        ok(json!({"data": {"links": [{"name": "Nexus CDN", "short_name": "nexus", "url": format!("{l2}?md5=sig&expires=1")}], "expires_at": "2026-11-07T10:00:00Z"}}))
    });
    let manifest = format!("https://files.test/manifest/dl-{vgsid}.json");
    m.json("GET", &manifest, json!({"files": sizes.iter().map(|(p, s)| json!({"path": p, "size": s})).collect::<Vec<_>>()}));
    m.json("GET", &format!("{API}/v3/games/enshrouded/mod-file-versions/{vgsid}"), json!({"data":
        {"id": format!("80{vgsid}2"), "file": {"id": format!("90{gsid}"), "name": "Main file"}, "position": "2", "game_scoped_id": vgsid,
         "name": "lantern-glow-1.2.0.zip", "version": "1.2.0", "category": "main", "uploaded_at": "2026-11-02T00:00:00Z", "manifest_url": manifest}}));
    m.json("GET", &format!("{API}/v3/games/enshrouded/mods/{gsid}"), json!({"data": {"id": uid(gsid), "game_scoped_id": gsid, "name": "Lantern Glow"}}));
    m.json("GET", &format!("{API}/v3/mod-file-versions/80{vgsid}2/dependencies"), json!({"dependency_definitions": [
        {"id": "d", "ranges": [{"target_mod_file": {"id": "f", "name": "Main", "mod": {"id": uid("313"), "game_scoped_id": "313", "name": "Night Sky"}}}]}
    ], "dlc_dependency_definitions": []}));
}

#[test]
fn an_nxm_download_is_checked_added_to_the_profile_recorded_and_undoable() {
    let (m, cat, t) = setup(Some(KEY));
    let zip = zip_of(&[("lantern-glow/kiln.mod.json", MANIFEST), ("lantern-glow/LanternGlow.dll", b"dll bytes")]);
    serve_download(&m, "310", "1001", zip, &[("lantern-glow/kiln.mod.json", MANIFEST.len() as u64), ("lantern-glow/LanternGlow.dll", 9)], true);
    let g = game(&t.0);
    let d = dirs(&t);
    // a free account without the nxm key: Nexus refuses (403)
    let free = InstallRequest { mod_id: "310".into(), file_id: "1001".into(), key: None, expires: None, source: "catalog".into() };
    assert_eq!(install::install(&cat, &d, &g, &free, Some(false), &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
    // with the link's key
    let req = InstallRequest { key: Some("dl-key".into()), expires: Some(1_900_000_000), source: "nxm".into(), ..free };
    assert!(!format!("{req:?}").contains("dl-key"));
    let r = install::install(&cat, &d, &g, &req, Some(false), &mut crate::task::NoProgress).unwrap();
    assert_eq!((r.name.as_str(), r.version.as_str()), ("Lantern Glow", "1.2.0"));
    assert_eq!((r.check.files, r.check.matched, r.check.no_manifest), (2, 2, false));
    assert!(r.add.applied && r.add.created, "{:?}", r.add);
    assert_eq!(r.add.added[0].id, "lantern-glow");
    assert!(r.download.ends_with("lantern-glow-1.2.0.zip"));
    assert_eq!(r.requirements.len(), 1);
    assert_eq!((r.requirements[0].mod_id.as_str(), r.requirements[0].installed), ("313", false));
    // the CDN got no account authorization
    assert!(m.log.lock().unwrap().iter().filter(|x| x.url.starts_with("https://cdn.test")).all(|x| !x.headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("authorization"))));
    // recorded: the catalog shows it installed, up to date
    let inst = cat.installed(&d).unwrap();
    assert_eq!(inst.len(), 1);
    assert!(inst[0].info.in_library && !inst[0].info.update_available);
    let all = cat.search(&d, &Query { filter: Filter::Installed, ..Default::default() }).unwrap();
    assert_eq!(all.mods.iter().map(|x| x.id.as_str()).collect::<Vec<_>>(), ["310"]);
    // Undo (the toast's): out of the profile, the library copy stays, the record says so
    crate::quick::revert(&d, r.add.profile_id.as_deref().unwrap(), &r.add.changes).unwrap();
    let p = crate::profile::get(&d, r.add.profile_id.as_deref().unwrap()).unwrap();
    assert!(p.mods.kiln.is_empty());
    assert!(cat.installed(&d).unwrap()[0].info.in_library);

    // a newer upload in the file: update available after a check; updating is a user click (no install ran)
    let manifest = "https://files.test/manifest/new.json";
    m.json("GET", &format!("{API}/v3/mod-files/90310/versions"), json!({"data": {"versions": [
        version_json("310", "1001", "1.2.0", "2", manifest, "main"),
        version_json("310", "1009", "1.3.0", "3", manifest, "main"),
    ]}}));
    let u = cat.check_updates(&d, true).unwrap();
    assert!(u[0].info.update_available);
    assert_eq!(u[0].info.latest_version.as_deref(), Some("1.3.0"));
    assert_eq!(cat.installed(&d).unwrap()[0].record.version, "1.2.0", "nothing was installed by the check");
    // a cached check within 30 minutes asks nothing
    let n = m.calls("/versions");
    cat.check_updates(&d, false).unwrap();
    assert_eq!(m.calls("/versions"), n);
}

#[test]
fn a_damaged_or_unusable_download_is_refused() {
    let (m, cat, t) = setup(Some(KEY));
    let zip = zip_of(&[("lantern-glow/kiln.mod.json", MANIFEST), ("lantern-glow/LanternGlow.dll", b"dll")]);
    serve_download(&m, "310", "1001", zip, &[("lantern-glow/LanternGlow.dll", 9)], false);
    let g = game(&t.0);
    let req = InstallRequest { mod_id: "310".into(), file_id: "1001".into(), key: None, expires: None, source: "catalog".into() };
    let e = install::install(&cat, &dirs(&t), &g, &req, Some(false), &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::HashMismatch, "{e:?}");
    assert!(cat.installed(&dirs(&t)).unwrap().is_empty());
    // a zip with nothing Kiln loads
    let (m, cat, t) = setup(Some(KEY));
    serve_download(&m, "310", "1001", zip_of(&[("readme.txt", b"hi")]), &[], false);
    let e = install::install(&cat, &dirs(&t), &game(&t.0), &req, Some(false), &mut crate::task::NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Unsupported);
    // a 7z
    let (m, cat, t) = setup(Some(KEY));
    serve_download(&m, "310", "1001", b"7z\xBC\xAF\x27\x1Cxxxx".to_vec(), &[], false);
    let e = install::install(&cat, &dirs(&t), &game(&t.0), &req, Some(false), &mut crate::task::NoProgress).unwrap_err();
    assert!(e.message.contains("7z"), "{e:?}");
    // Kiln itself
    let k = InstallRequest { mod_id: KILN_MOD.into(), ..req };
    assert_eq!(install::install(&cat, &dirs(&t), &game(&t.0), &k, Some(false), &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
}

// ---------------------------------------------------------------- the handler

#[derive(Default)]
struct FakeOs {
    current: Mutex<Option<String>>,
    forwarded: Mutex<Vec<(String, String)>>,
}
impl HandlerPlatform for FakeOs {
    fn supported(&self) -> bool {
        true
    }
    fn current(&self) -> Result<Option<Previous>> {
        Ok(self.current.lock().unwrap().clone().filter(|c| c != "kiln").map(|id| Previous::Desktop { id }))
    }
    fn is_ours(&self) -> bool {
        self.current.lock().unwrap().as_deref() == Some("kiln")
    }
    fn install(&self, _: &Path) -> Result<()> {
        *self.current.lock().unwrap() = Some("kiln".into());
        Ok(())
    }
    fn restore(&self, p: Option<&Previous>) -> Result<()> {
        *self.current.lock().unwrap() = p.map(|p| match p {
            Previous::Desktop { id } => id.clone(),
            Previous::Command { command, .. } => command.clone(),
        });
        Ok(())
    }
    fn forward(&self, p: &Previous, url: &str) -> Result<()> {
        self.forwarded.lock().unwrap().push((p.label(), url.into()));
        Ok(())
    }
}

#[test]
fn the_handler_remembers_vortex_forwards_other_games_and_gives_it_back() {
    let t = tmp("handler");
    let os = FakeOs::default();
    *os.current.lock().unwrap() = Some("com.nexusmods.vortex.desktop".into());
    let s = handler::register(&t.0, &os, Path::new("/apps/Kiln.AppImage")).unwrap();
    assert!(s.registered && s.active);
    assert_eq!(s.previous.as_deref(), Some("Vortex"));
    // again while on (the AppImage moved): Vortex is still the one remembered
    handler::register(&t.0, &os, Path::new("/apps2/Kiln.AppImage")).unwrap();
    assert_eq!(handler::state(&t.0).previous, Some(Previous::Desktop { id: "com.nexusmods.vortex.desktop".into() }));
    assert_eq!(handler::forward(&t.0, &os, "nxm://skyrim/mods/1/files/2?key=k").unwrap(), "Vortex");
    assert_eq!(os.forwarded.lock().unwrap()[0].1, "nxm://skyrim/mods/1/files/2?key=k");
    let s = handler::unregister(&t.0, &os).unwrap();
    assert!(!s.registered);
    assert_eq!(os.current.lock().unwrap().as_deref(), Some("com.nexusmods.vortex.desktop"));
    // nothing before Kiln: nothing to forward to
    *os.current.lock().unwrap() = None;
    handler::register(&t.0, &os, Path::new("/k")).unwrap();
    assert_eq!(handler::forward(&t.0, &os, "nxm://x/mods/1/files/1").unwrap_err().code, ErrorCode::NotFound);
    handler::unregister(&t.0, &os).unwrap();
    assert_eq!(*os.current.lock().unwrap(), None);
}

#[test]
fn previous_handler_labels_and_command_lines() {
    let c = |s: &str| Previous::Command { command: s.into(), icon: None, scope: "hkcu".into() }.label();
    assert_eq!(c(r#""C:\Program Files\Black Tree Gaming Ltd\Vortex\Vortex.exe" -d "%1""#), "Vortex");
    assert_eq!(c(r#""C:\Modding\MO2\nxmhandler.exe" "%1""#), "Mod Organizer 2");
    assert_eq!(Previous::Desktop { id: "modorganizer2-nxm-handler.desktop".into() }.label(), "Mod Organizer 2");
    assert_eq!(handler::fill_command(r#""C:\V\Vortex.exe" -d "%1""#, "nxm://a/mods/1/files/2"), r#""C:\V\Vortex.exe" -d "nxm://a/mods/1/files/2""#);
    assert_eq!(handler::split_command(r#""C:\V\Vortex.exe" -d "x""#), (r"C:\V\Vortex.exe".into(), r#"-d "x""#.into()));
    assert_eq!(handler::exec_argv(r#""/opt/My App/vortex" --flag %u %i"#, "nxm://a"), ["/opt/My App/vortex", "--flag", "nxm://a"]);
    assert_eq!(handler::exec_argv("nxmhandler", "nxm://a"), ["nxmhandler", "nxm://a"]);
    assert_eq!(handler::exec_quote(r#"/a b/"x"$y"#), r#""/a b/\\"x\\"\\$y""#);
}

/// The real xdg-mime in private XDG folders: Kiln becomes the nxm default, the old one comes back.
#[cfg(target_os = "linux")]
#[test]
fn linux_registration_with_xdg_mime() {
    if crate::util::which("xdg-mime").is_none() {
        eprintln!("xdg-mime not installed: skipped");
        return;
    }
    let t = tmp("xdg");
    let data = t.0.join("data");
    let config = t.0.join("config");
    std::fs::create_dir_all(data.join("applications")).unwrap();
    std::fs::create_dir_all(&config).unwrap();
    std::fs::write(data.join("applications/othermm.desktop"), "[Desktop Entry]\nType=Application\nName=Other\nExec=/bin/true --nxm %u\nMimeType=x-scheme-handler/nxm;\n").unwrap();
    std::fs::write(config.join("mimeapps.list"), "[Default Applications]\nx-scheme-handler/nxm=othermm.desktop\ntext/plain=gedit.desktop\n").unwrap();
    let env = vec![
        ("XDG_DATA_HOME".to_string(), data.display().to_string()),
        ("XDG_CONFIG_HOME".to_string(), config.display().to_string()),
        ("XDG_DATA_DIRS".to_string(), data.display().to_string()),
        ("XDG_CURRENT_DESKTOP".to_string(), "X-Generic".to_string()),
        ("HOME".to_string(), t.0.display().to_string()),
    ];
    let os = handler::LinuxMime { data_home: data.clone(), config_home: config.clone(), data_dirs: vec![], env };
    let root = t.0.join("nexus");
    let s = handler::register(&root, &os, Path::new("/opt/Kiln Launcher/kiln.AppImage")).unwrap();
    assert!(s.active, "{s:?}");
    assert_eq!(s.previous.as_deref(), Some("othermm"));
    let entry = std::fs::read_to_string(os.desktop_file()).unwrap();
    assert!(entry.contains("Exec=\"/opt/Kiln Launcher/kiln.AppImage\" --nxm %u") && entry.contains("MimeType=x-scheme-handler/nxm;") && entry.contains("NoDisplay=true"), "{entry}");
    let list = std::fs::read_to_string(config.join("mimeapps.list")).unwrap();
    assert!(list.contains("x-scheme-handler/nxm=kiln-launcher-nxm.desktop") && list.contains("text/plain=gedit.desktop"), "{list}");
    let s = handler::unregister(&root, &os).unwrap();
    assert!(!s.registered && !s.active);
    assert!(!os.desktop_file().exists());
    let list = std::fs::read_to_string(config.join("mimeapps.list")).unwrap();
    assert!(list.contains("x-scheme-handler/nxm=othermm.desktop") && !list.contains("kiln-launcher-nxm"), "{list}");

    // no handler before: turning it off leaves none (Kiln's line is dropped)
    std::fs::write(config.join("mimeapps.list"), "[Default Applications]\ntext/plain=gedit.desktop\n").unwrap();
    std::fs::remove_file(data.join("applications/othermm.desktop")).unwrap();
    let _ = std::fs::remove_file(data.join("applications/mimeinfo.cache")); // update-desktop-database's, still naming it
    handler::register(&root, &os, Path::new("/k")).unwrap();
    assert_eq!(handler::state(&root).previous, None);
    handler::unregister(&root, &os).unwrap();
    let list = std::fs::read_to_string(config.join("mimeapps.list")).unwrap();
    assert!(!list.contains("nxm"), "{list}");
}

// ---------------------------------------------------------------- key store, inbox

#[derive(Default)]
struct FakeRing {
    v: Mutex<Option<String>>,
    broken: bool,
}
impl SecretBackend for Arc<FakeRing> {
    fn get(&self) -> Result<Option<String>> {
        Ok(self.v.lock().unwrap().clone())
    }
    fn set(&self, s: &str) -> Result<()> {
        if self.broken {
            return Err(crate::Error::new(ErrorCode::Unsupported, "no"));
        }
        *self.v.lock().unwrap() = Some(s.into());
        Ok(())
    }
    fn clear(&self) -> Result<()> {
        *self.v.lock().unwrap() = None;
        Ok(())
    }
}

#[test]
fn oauth_tokens_go_to_the_keyring_else_a_private_file() {
    let t = tmp("tokens");
    let tokens = super::oauth::Tokens { client_id: "kiln-test".into(), access_token: KEY.into(), refresh_token: "refresh-token".into(), expires_at: 2000000000 };
    let ring = Arc::new(FakeRing::default());
    let store = TokenStore::with_backend(&t.0, Some(Box::new(ring)));
    assert_eq!(store.set(&tokens).unwrap(), TokenPlace::Keyring);
    assert_eq!(store.get().unwrap(), Some((tokens.clone(), TokenPlace::Keyring)));
    assert!(!store.file_path().exists());
    store.clear().unwrap(); assert!(store.get().unwrap().is_none());
    let broken = Arc::new(FakeRing { broken: true, ..Default::default() });
    let store = TokenStore::with_backend(&t.0, Some(Box::new(broken)));
    assert!(store.set(&tokens).is_err()); assert!(!store.file_path().exists());
    let store = TokenStore::with_backend(&t.0, None);
    assert_eq!(store.set(&tokens).unwrap(), TokenPlace::File);
    assert_eq!(store.get().unwrap(), Some((tokens, TokenPlace::File)));
    #[cfg(unix)] { use std::os::unix::fs::PermissionsExt; assert_eq!(std::fs::metadata(store.file_path()).unwrap().permissions().mode() & 0o777, 0o600); }
    store.clear().unwrap(); assert!(!store.file_path().exists());
}

#[test]
fn one_launcher_takes_links_from_the_inbox() {
    let t = tmp("inbox");
    let a = handler::acquire_instance(&t.0).unwrap();
    assert!(a.is_some());
    assert!(handler::acquire_instance(&t.0).unwrap().is_none(), "a second process sees the first");
    handler::queue_link(&t.0, "nxm://enshrouded/mods/1/files/2?key=a&expires=1").unwrap();
    std::thread::sleep(std::time::Duration::from_millis(3));
    handler::queue_link(&t.0, "nxm://enshrouded/mods/3/files/4").unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let f = std::fs::read_dir(t.0.join("inbox")).unwrap().next().unwrap().unwrap().path();
        assert_eq!(std::fs::metadata(f).unwrap().permissions().mode() & 0o777, 0o600);
    }
    assert_eq!(handler::take_links(&t.0), ["nxm://enshrouded/mods/1/files/2?key=a&expires=1", "nxm://enshrouded/mods/3/files/4"]);
    assert!(handler::take_links(&t.0).is_empty());
    drop(a);
    // A child that another test forked a moment ago holds a copy of the lock's descriptor until it execs.
    let free = (0..400).any(|_| handler::acquire_instance(&t.0).unwrap().is_some() || {
        std::thread::sleep(std::time::Duration::from_millis(5));
        false
    });
    assert!(free, "free again when the first ends");
}

// ---------------------------------------------------------------- the real API (opt-in, anonymous, read-only)

/// `cargo test -p kiln-manage --features full nexus::tests::live -- --ignored --nocapture`: the real Nexus Mods,
/// without a key: every Enshrouded mod through the cursor pages, and Kiln's own page parse.
#[cfg(feature = "http")]
#[test]
#[ignore]
fn live_anonymous_catalog() {
    let t = tmp("live");
    let cat = Catalog::new(t.0.join("data/nexus"), super::client(None, "0.0.0-test"));
    let p = cat.search(&dirs(&t), &Query { filter: Filter::All, page_size: 100, ..Default::default() }).unwrap();
    eprintln!("{} Enshrouded mods, fetched {}", p.total, p.fetched);
    assert!(p.total > 100, "{}", p.total);
    let k = cat.search(&dirs(&t), &Query::default()).unwrap();
    assert!(k.needs_sign_in, "the Kiln filter needs a key");
    let d = cat.details(&dirs(&t), KILN_MOD).unwrap();
    eprintln!("{} {} by {} ({} images, {} chars of description)", d.summary.name, d.summary.version.as_deref().unwrap_or("?"), d.summary.author, d.images.len(), d.description.len());
    assert!(d.summary.name.starts_with("Kiln") && d.needs_sign_in && !d.images.is_empty());
    eprintln!("calls: {:?}", cat.client().rate());
}
