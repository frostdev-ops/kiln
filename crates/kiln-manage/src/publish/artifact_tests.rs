//! The artifact policy's fixture table: one test per refusal, and two zips shaped like `kiln pack` output that pass.
//! Fixtures are built here from made-up bytes: forbidden names and headers, never real game data or real keys.

use super::*;
use crate::publish::policy::reason_of;
use std::io::Write;
use std::path::PathBuf;

const MANIFEST: &str = r#"{"id": "good-mod", "name": "Good Mod", "version": "1.2.3", "apiVersion": "0.2", "side": "both", "entryAssembly": "GoodMod.dll"}"#;
const NATIVE: &str = r#"{"id": "native-mod", "version": "0.4.0", "dlls": ["native-mod.dll"]}"#;

struct Dir(PathBuf);
impl Dir {
    fn new() -> Dir {
        let d = std::env::temp_dir().join(format!("kiln-publish-audit-{}", crate::util::uuid_v4().unwrap()));
        std::fs::create_dir_all(&d).unwrap();
        Dir(d)
    }
    fn zip(&self, name: &str, files: &[(&str, &[u8])]) -> PathBuf {
        let path = self.0.join(name);
        let mut w = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
        let opts = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
        for (n, data) in files {
            if n.ends_with('/') {
                w.add_directory(n.trim_end_matches('/'), opts).unwrap();
            } else {
                w.start_file(*n, opts).unwrap();
                w.write_all(data).unwrap();
            }
        }
        w.finish().unwrap();
        path
    }
}
impl Drop for Dir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn managed(extra: &[(&'static str, &'static [u8])]) -> Vec<(&'static str, &'static [u8])> {
    let mut v: Vec<(&str, &[u8])> = vec![
        ("good-mod/kiln.mod.json", MANIFEST.as_bytes()),
        ("good-mod/GoodMod.dll", b"MZ made-up managed assembly"),
        ("good-mod/assets/assets.json", b"{}"),
        ("good-mod/locale/en.json", br#"{"hello": "Hello"}"#),
    ];
    v.extend_from_slice(extra);
    v
}

fn reason(d: &Dir, files: &[(&str, &[u8])]) -> String {
    reason_with(d, files, &AuditInput::default())
}
fn reason_with(d: &Dir, files: &[(&str, &[u8])], input: &AuditInput) -> String {
    match audit(&d.zip(&format!("{}.zip", crate::util::uuid_v4().unwrap()), files), input) {
        Ok(r) => format!("PASSED ({} members)", r.members.len()),
        Err(e) => {
            assert_eq!(e.code, ErrorCode::Refused, "{e:?}");
            reason_of(&e).unwrap_or("(no reason)").to_string()
        }
    }
}

#[test]
fn pack_shaped_zips_of_a_managed_and_a_native_mod_pass() {
    let d = Dir::new();
    let z = d.zip("good-mod-1.2.3.zip", &managed(&[("good-mod/assets/", b""), ("good-mod/assets/cook.json", br#"{"format": 1, "models": []}"#), ("good-mod/blocks/items.json", b"[]")]));
    let r = audit(&z, &AuditInput { expect_id: Some("good-mod"), expect_version: Some("1.2.3"), package_files: &[] }).unwrap();
    assert_eq!((r.kind, r.id.as_str(), r.version.as_str(), r.side, r.api_version.as_str()), (ModKind::Kiln, "good-mod", "1.2.3", Side::Both, "0.2"));
    assert_eq!(r.policy_version, policy::POLICY_VERSION);
    assert_eq!(r.members.len(), 6, "folders are not members");
    assert!(r.members.windows(2).all(|w| w[0].name < w[1].name));
    let m = r.members.iter().find(|m| m.name == "good-mod/kiln.mod.json").unwrap();
    assert_eq!((m.bytes, m.sha256.as_str()), (MANIFEST.len() as u64, crate::hash::sha256_hex(MANIFEST.as_bytes()).as_str()));
    assert_eq!(r.expanded_bytes, r.members.iter().map(|m| m.bytes).sum::<u64>());
    assert_eq!((r.zip_sha256.clone(), r.zip_bytes), crate::hash::sha256_file(&z).unwrap());

    let z = d.zip("native-mod-0.4.0.zip", &[("native-mod/native.json", NATIVE.as_bytes()), ("native-mod/native-mod.dll", b"MZ made-up native dll"), ("native-mod/README.md", b"hi")]);
    let r = audit(&z, &AuditInput { expect_id: Some("native-mod"), expect_version: Some("0.4"), package_files: &[] }).unwrap();
    assert_eq!((r.kind, r.id.as_str(), r.api_version.as_str()), (ModKind::Native, "native-mod", "*"));
    println!("PASS publish.artifact-policy");
}

#[test]
fn the_zip_itself() {
    let d = Dir::new();
    let not_zip = d.0.join("x.zip");
    std::fs::write(&not_zip, b"this is no zip").unwrap();
    assert_eq!(reason_of(&audit(&not_zip, &AuditInput::default()).unwrap_err()), Some("zip-unreadable"));
    assert_eq!(reason(&d, &[]), "layout");
    let many: Vec<(String, &[u8])> = (0..policy::MAX_MEMBERS + 1).map(|i| (format!("good-mod/f{i}.txt"), &b"x"[..])).collect();
    let many: Vec<(&str, &[u8])> = many.iter().map(|(n, b)| (n.as_str(), *b)).collect();
    assert_eq!(reason(&d, &many), "too-many-members");
}

#[test]
fn bombs() {
    let d = Dir::new();
    let zeros = vec![0u8; 8 * 1024 * 1024];
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/big.bin", Box::leak(zeros.into_boxed_slice()))])), "ratio");
    // a small repetitive file is no bomb
    let small = vec![b'a'; 32 * 1024];
    assert!(reason(&d, &managed(&[("good-mod/assets/small.txt", Box::leak(small.into_boxed_slice()))])).starts_with("PASSED"));
    // a member over the size limit: incompressible enough to get past the ratio rule first
    let mut big = vec![0u8; (policy::MAX_MEMBER_BYTES + 1) as usize];
    let mut x = 0x9E3779B97F4A7C15u64;
    for chunk in big.chunks_mut(8) {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        let b = x.to_le_bytes();
        chunk.copy_from_slice(&b[..chunk.len()]);
    }
    let path = d.0.join("huge.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    let stored = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);
    w.start_file("good-mod/kiln.mod.json", stored).unwrap();
    w.write_all(MANIFEST.as_bytes()).unwrap();
    w.start_file("good-mod/assets/huge.bin", stored).unwrap();
    w.write_all(&big).unwrap();
    w.finish().unwrap();
    assert_eq!(reason_of(&audit(&path, &AuditInput::default()).unwrap_err()), Some("member-too-large"));
}

#[test]
fn paths() {
    let d = Dir::new();
    assert_eq!(reason(&d, &managed(&[("good-mod/../evil.txt", b"x")])), "path-dots");
    assert_eq!(reason(&d, &managed(&[("C:\\Windows\\evil.txt", b"x")])), "path-drive");
    assert_eq!(reason(&d, &managed(&[("\\\\server\\share\\evil.txt", b"x")])), "path-absolute");
    assert_eq!(reason(&d, &managed(&[("/abs/evil.txt", b"x")])), "path-absolute");
    assert_eq!(reason(&d, &managed(&[("good-mod/a/b/c/d/e/f/g/h/i/j/k/l/m/n/o/p.txt", b"x")])), "depth");
    assert_eq!(reason(&d, &managed(&[("good-mod/Assets/assets.json", b"{}")])), "case-collision");
    assert_eq!(reason(&d, &managed(&[("good-mod\\locale\\en.json", b"{}")])), "duplicate-name");
    assert_eq!(reason(&d, &managed(&[("good-mod/locale", b"a file named like a folder")])), "duplicate-name");
}

#[test]
fn links_and_encryption() {
    let d = Dir::new();
    let path = d.0.join("link.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
    let opts = zip::write::SimpleFileOptions::default();
    w.start_file("good-mod/kiln.mod.json", opts).unwrap();
    w.write_all(MANIFEST.as_bytes()).unwrap();
    w.add_symlink("good-mod/link", "/etc/passwd", opts).unwrap();
    w.finish().unwrap();
    assert_eq!(reason_of(&audit(&path, &AuditInput::default()).unwrap_err()), Some("link"));

    // The "encrypted" flag (general purpose bit 0) set by hand in the local and the central header of one member.
    let plain = d.zip("enc.zip", &[("good-mod/kiln.mod.json", MANIFEST.as_bytes())]);
    let mut bytes = std::fs::read(&plain).unwrap();
    let mut patched = 0;
    for i in 0..bytes.len() - 10 {
        if &bytes[i..i + 4] == b"PK\x03\x04" {
            bytes[i + 6] |= 1;
            patched += 1;
        } else if &bytes[i..i + 4] == b"PK\x01\x02" {
            bytes[i + 8] |= 1;
            patched += 1;
        }
    }
    assert_eq!(patched, 2);
    std::fs::write(&plain, &bytes).unwrap();
    assert_eq!(reason_of(&audit(&plain, &AuditInput::default()).unwrap_err()), Some("encrypted"));
}

#[test]
fn layout_and_manifest() {
    let d = Dir::new();
    assert_eq!(reason(&d, &[("kiln.mod.json", MANIFEST.as_bytes()), ("GoodMod.dll", b"MZ")]), "layout-root-manifest");
    assert_eq!(reason(&d, &managed(&[("readme.txt", b"outside")])), "layout");
    assert_eq!(reason(&d, &managed(&[("other-mod/kiln.mod.json", MANIFEST.as_bytes())])), "layout");
    assert_eq!(reason(&d, &[("good-mod/readme.txt", b"no manifest")]), "manifest-missing");
    assert_eq!(reason(&d, &managed(&[("good-mod/native.json", NATIVE.as_bytes())])), "manifest-multiple");
    assert_eq!(reason(&d, &managed(&[("good-mod/sub/inner/kiln.mod.json", MANIFEST.as_bytes())])), "manifest-multiple");
    assert_eq!(reason(&d, &[("Good Mod/kiln.mod.json", MANIFEST.as_bytes())]), "layout");
    assert_eq!(reason(&d, &[("good-mod/kiln.mod.json", b"{ not json")]), "manifest-invalid");
    assert_eq!(reason(&d, &[("other-id/kiln.mod.json", MANIFEST.as_bytes()), ("other-id/GoodMod.dll", b"MZ")]), "manifest-mismatch");
    let files = managed(&[]);
    assert_eq!(reason_with(&d, &files, &AuditInput { expect_id: Some("another"), ..Default::default() }), "manifest-mismatch");
    assert_eq!(reason_with(&d, &files, &AuditInput { expect_version: Some("1.2.4"), ..Default::default() }), "manifest-mismatch");
    assert_eq!(reason_with(&d, &files, &AuditInput { expect_version: Some("1.2.3+other"), ..Default::default() }), "manifest-mismatch");
    assert!(reason_with(&d, &files, &AuditInput { expect_id: Some("good-mod"), expect_version: Some("v1.2.3"), ..Default::default() }).starts_with("PASSED"));
}

#[test]
fn forbidden_content() {
    let d = Dir::new();
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/enshrouded.kfc", b"made-up")])), "game-data");
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/renamed.bin", b"KFC4 made-up header, not game data")])), "game-data");
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/cooked/model.bin", b"made-up")])), "cooked-output");
    assert_eq!(reason(&d, &managed(&[("good-mod/world.db", b"made-up")])), "save-data");
    assert_eq!(reason(&d, &managed(&[("good-mod/savegames/slot1", b"made-up")])), "runtime-state");
    assert_eq!(reason(&d, &managed(&[("good-mod/.git/HEAD", b"ref: x")])), "vcs-or-build-folder");
    assert_eq!(reason(&d, &managed(&[("good-mod/obj/x.json", b"{}")])), "vcs-or-build-folder");
    assert_eq!(reason(&d, &managed(&[("good-mod/.env", b"A=1")])), "secret-file");
    assert_eq!(reason(&d, &managed(&[("good-mod/cert.pfx", b"made-up")])), "secret-file");
    assert_eq!(reason(&d, &managed(&[("good-mod/credentials.json", b"{}")])), "secret-file");
    // a private-key header inside any member, whatever its name
    assert_eq!(reason(&d, &managed(&[("good-mod/docs/notes.md", b"see below\n-----BEGIN RSA PRIVATE KEY-----\nmade-up, not a key\n")])), "private-key");
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/icon.png", b"\x89PNG....-----BEGIN PRIVATE KEY-----....")])), "private-key");
    // a recipe's output folder under another name
    let cook = br#"{"format": 1, "out": "../baked", "models": []}"#;
    assert_eq!(reason(&d, &managed(&[("good-mod/assets/cook.json", cook), ("good-mod/baked/model.bin", b"made-up")])), "cooked-output");
    let impact = br#"{"programs": [{"name": "a", "out": "gen"}]}"#;
    assert_eq!(reason(&d, &managed(&[("good-mod/impact/cook.json", impact), ("good-mod/impact/gen/a.bin", b"made-up")])), "cooked-output");
    // the recipe itself and its sources are fine
    assert!(reason(&d, &managed(&[("good-mod/assets/cook.json", cook), ("good-mod/assets/models/a.glb", b"glTF made-up")])).starts_with("PASSED"));
}

#[test]
fn dlls() {
    let d = Dir::new();
    let dep: &[u8] = b"MZ made-up third-party library";
    let sha = crate::hash::sha256_hex(dep);
    let decl = |purpose, license: Option<&str>, sha: &str| PackageFile { path: "lib/Third.Party.dll".into(), sha256: sha.into(), purpose, license: license.map(str::to_string) };
    let files = managed(&[("good-mod/lib/Third.Party.dll", dep)]);
    assert_eq!(reason(&d, &files), "undeclared-dll");
    let ok = [decl(Purpose::Dependency, Some("MIT"), &sha)];
    assert!(reason_with(&d, &files, &AuditInput { package_files: &ok, ..Default::default() }).starts_with("PASSED"));
    let wrong_sha = [decl(Purpose::Dependency, Some("MIT"), &"0".repeat(64))];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &wrong_sha, ..Default::default() }), "package-file-mismatch");
    let no_license = [decl(Purpose::Dependency, None, &sha)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &no_license, ..Default::default() }), "package-file-invalid");
    let wrong_purpose = [decl(Purpose::DefaultConfig, None, &sha)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &wrong_purpose, ..Default::default() }), "package-file-invalid");
    assert_eq!(reason_with(&d, &managed(&[]), &AuditInput { package_files: &ok, ..Default::default() }), "package-file-missing");
    // Kiln's and .NET's own DLLs: never, also when declared
    for name in ["good-mod/Kiln.Api.dll", "good-mod/lib/System.Text.Json.dll", "good-mod/hostfxr.dll"] {
        let files = managed(&[(name, dep)]);
        let declared = [PackageFile { path: name.strip_prefix("good-mod/").unwrap().into(), sha256: sha.clone(), purpose: Purpose::Dependency, license: Some("MIT".into()) }];
        assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &declared, ..Default::default() }), "runtime-dll", "{name}");
    }
    // a native mod's listed DLLs are its own; another one is not
    let native: Vec<(&str, &[u8])> = vec![("native-mod/native.json", NATIVE.as_bytes()), ("native-mod/native-mod.dll", b"MZ"), ("native-mod/helper.dll", b"MZ")];
    assert_eq!(reason(&d, &native), "undeclared-dll");
}

#[test]
fn default_configs() {
    let d = Dir::new();
    let cfg: &[u8] = br#"{"webhookUrl": "", "apiKey": "", "limit": 5}"#;
    let leaky: &[u8] = br#"{"limit": 5, "discord": {"bot_token": "made-up-not-a-token"}}"#;
    let ini: &[u8] = b"; comment\nlimit = 5\npassword = hunter2\n";
    let decl = |name: &str, bytes: &[u8]| PackageFile { path: format!("config/defaults/{name}"), sha256: crate::hash::sha256_hex(bytes), purpose: Purpose::DefaultConfig, license: None };
    let files = managed(&[("good-mod/config/defaults/good-mod.json", cfg)]);
    assert_eq!(reason(&d, &files), "undeclared-default-config");
    let ok = [decl("good-mod.json", cfg)];
    assert!(reason_with(&d, &files, &AuditInput { package_files: &ok, ..Default::default() }).starts_with("PASSED"));
    let files = managed(&[("good-mod/config/defaults/good-mod.json", leaky)]);
    let declared = [decl("good-mod.json", leaky)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &declared, ..Default::default() }), "secret-in-config");
    let files = managed(&[("good-mod/config/defaults/good-mod.ini", ini)]);
    let declared = [decl("good-mod.ini", ini)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &declared, ..Default::default() }), "secret-in-config");
    // a declaration waives nothing: a forbidden name stays forbidden
    let files = managed(&[("good-mod/config/defaults/credentials.json", cfg)]);
    let declared = [decl("credentials.json", cfg)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &declared, ..Default::default() }), "secret-file");
    let key: &[u8] = b"-----BEGIN PRIVATE KEY-----\nmade-up\n";
    let files = managed(&[("good-mod/config/defaults/good-mod.json", key)]);
    let declared = [decl("good-mod.json", key)];
    assert_eq!(reason_with(&d, &files, &AuditInput { package_files: &declared, ..Default::default() }), "private-key");
    let outside = [PackageFile { path: "assets/x.json".into(), sha256: "0".repeat(64), purpose: Purpose::DefaultConfig, license: None }];
    assert_eq!(reason_with(&d, &managed(&[]), &AuditInput { package_files: &outside, ..Default::default() }), "package-file-invalid");
}

#[test]
fn a_root_level_manifest_is_normalized_before_the_audit() {
    let d = Dir::new();
    let flat = d.zip("flat.zip", &[("kiln.mod.json", MANIFEST.as_bytes()), ("GoodMod.dll", b"MZ made-up"), ("locale/en.json", b"{}"), ("locale/", b"")]);
    assert_eq!(reason_of(&audit(&flat, &AuditInput::default()).unwrap_err()), Some("layout-root-manifest"));
    let fixed = d.0.join("fixed.zip");
    assert!(normalize_root(&flat, &fixed).unwrap());
    let r = audit(&fixed, &AuditInput { expect_id: Some("good-mod"), ..Default::default() }).unwrap();
    let names: Vec<&str> = r.members.iter().map(|m| m.name.as_str()).collect();
    assert_eq!(names, ["good-mod/GoodMod.dll", "good-mod/kiln.mod.json", "good-mod/locale/en.json"]);
    assert_eq!(r.members[1].sha256, crate::hash::sha256_hex(MANIFEST.as_bytes()), "bytes are copied, not changed");
    // the target is never overwritten; a zip that has its folder already is left alone
    assert!(normalize_root(&flat, &fixed).is_err());
    let untouched = d.0.join("untouched.zip");
    assert!(!normalize_root(&fixed, &untouched).unwrap() && !untouched.exists());
    // normalization waives nothing
    let bad = d.zip("flatbad.zip", &[("kiln.mod.json", MANIFEST.as_bytes()), (".env", b"A=1")]);
    let out = d.0.join("flatbad-fixed.zip");
    assert!(normalize_root(&bad, &out).unwrap());
    assert_eq!(reason_of(&audit(&out, &AuditInput::default()).unwrap_err()), Some("secret-file"));
}

#[test]
fn a_refusal_names_its_member_and_the_policy_version() {
    let d = Dir::new();
    let e = audit(&d.zip("a.zip", &managed(&[("good-mod/.env", b"A=1")])), &AuditInput::default()).unwrap_err();
    let detail = e.detail.clone().unwrap();
    assert_eq!(detail["member"], "good-mod/.env");
    assert_eq!(detail["policyVersion"], policy::POLICY_VERSION);
    assert!(e.message.contains("good-mod/.env"));
}
