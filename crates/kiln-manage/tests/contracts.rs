//! Pins kiln-manage's types and kdh1 to the contract files in schema/ (examples, fixtures).

use kiln_manage::*;
use std::path::{Path, PathBuf};

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn read(rel: &str) -> String {
    std::fs::read_to_string(repo().join(rel)).unwrap_or_else(|e| panic!("{rel}: {e}"))
}

/// Drops // comments and trailing commas (enough for the JSONC examples).
fn jsonc(text: &str) -> String {
    let mut out = String::new();
    let mut in_str = false;
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if in_str {
            out.push(c);
            if c == '\\' {
                out.extend(chars.next());
            } else if c == '"' {
                in_str = false;
            }
        } else if c == '"' {
            in_str = true;
            out.push(c);
        } else if c == '/' && chars.peek() == Some(&'/') {
            while chars.next_if(|&n| n != '\n').is_some() {}
        } else {
            out.push(c);
        }
    }
    let mut cleaned = String::new();
    let bytes: Vec<char> = out.chars().collect();
    for (i, &c) in bytes.iter().enumerate() {
        if c == ',' {
            let next = bytes[i + 1..].iter().find(|ch| !ch.is_whitespace());
            if matches!(next, Some('}') | Some(']')) {
                continue;
            }
        }
        cleaned.push(c);
    }
    cleaned
}

fn example<T: serde::de::DeserializeOwned>(name: &str) -> T {
    serde_json::from_str(&jsonc(&read(&format!("schema/examples/{name}")))).unwrap_or_else(|e| panic!("{name}: {e}"))
}

#[test]
fn workspace_version_is_the_version_file() {
    assert_eq!(VERSION, read("VERSION").trim());
}

#[test]
fn kdh1_matches_the_fixtures() {
    let dir = repo().join("schema/fixtures/dirhash");
    let expected: serde_json::Value = serde_json::from_str(&read("schema/fixtures/dirhash/expected.json")).unwrap();
    let cases = expected["cases"].as_object().unwrap();
    assert!(cases.len() >= 17);
    for (name, want) in cases {
        let got = hash::dirhash(&dir.join("cases").join(name)).unwrap();
        assert_eq!(&got, want.as_str().unwrap(), "case {name}");
    }
}

/// A mod's identity (kdh1 without a Kiln mod's cook outputs) against the same fixtures: on disk, and from records as
/// a modpack's entries are hashed.
#[test]
fn mod_identity_matches_the_fixtures() {
    let dir = repo().join("schema/fixtures/dirhash");
    let expected: serde_json::Value = serde_json::from_str(&read("schema/fixtures/dirhash/expected.json")).unwrap();
    let (cases, identity) = (expected["cases"].as_object().unwrap(), expected["identity"].as_object().unwrap());
    assert_eq!(cases.keys().collect::<Vec<_>>(), identity.keys().collect::<Vec<_>>());
    fn records(root: &std::path::Path, dir: &std::path::Path, out: &mut Vec<hash::Record>) {
        for e in std::fs::read_dir(dir).unwrap().flatten() {
            let p = e.path();
            if p.is_dir() {
                records(root, &p, out);
            } else {
                let (sha256, size) = hash::sha256_file(&p).unwrap();
                out.push(hash::Record { path: p.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/"), size, sha256 });
            }
        }
    }
    for (name, want) in identity {
        let case = dir.join("cases").join(name);
        assert_eq!(&hash::mod_identity(&case).unwrap(), want.as_str().unwrap(), "case {name}");
        assert_eq!(&hash::of_mod(manifest::ModKind::Kiln, &case).unwrap(), want.as_str().unwrap(), "case {name}");
        assert_eq!(hash::of_mod(manifest::ModKind::Native, &case).unwrap(), cases[name].as_str().unwrap(), "case {name}: another kind's folder is all its own");
        let mut rs = Vec::new();
        records(&case, &case, &mut rs);
        assert_eq!(&hash::mod_identity_records(rs.clone()).unwrap(), want.as_str().unwrap(), "case {name} from records");
        assert_eq!(hash::dirhash_records(rs).unwrap(), cases[name].as_str().unwrap(), "case {name} from records");
    }
    // the cases the rule exists for
    let of = |m: &str, n: &str| expected[m][n].as_str().unwrap().to_string();
    assert_eq!(of("identity", "mod-cooked"), of("cases", "mod"), "a mod with cooked files is the same mod without them");
    assert_ne!(of("identity", "mod-cooked"), of("cases", "mod-cooked"));
    for n in ["cooked-not-a-mod", "cooked-manifest-folder", "mod-cooked-case", "mod-cooked-file", "mod-cooked-empty"] {
        assert_eq!(of("identity", n), of("cases", n), "{n}: everything counts");
    }
    // a manifest that is no JSON still makes the folder a Kiln mod; a mod inside a mod keeps its own cooked folder counted
    for n in ["mod-cooked-names", "mod-bad-manifest", "mod-nested-mod"] {
        assert_ne!(of("identity", n), of("cases", n), "{n}");
    }
}

/// A dot entry is skipped whatever its name is made of; only a name that counts has to be valid Unicode. The Python
/// reference says the same of the same folder (a name of such bytes cannot be a committed fixture).
#[cfg(unix)]
#[test]
fn a_hidden_entry_with_a_name_that_is_not_unicode_is_skipped_like_any_dot_entry() {
    use std::os::unix::ffi::OsStrExt;
    let tmp = std::env::temp_dir().join(format!("kdh1-bytes-test-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    let m = tmp.join("m");
    std::fs::create_dir_all(m.join("assets/cooked")).unwrap();
    std::fs::write(m.join("kiln.mod.json"), b"{}").unwrap();
    std::fs::write(m.join("a.txt"), b"a").unwrap();
    let plain = (hash::dirhash(&m).unwrap(), hash::mod_identity(&m).unwrap());
    let bytes = |s: &[u8]| std::ffi::OsStr::from_bytes(s).to_os_string();
    std::fs::write(m.join(bytes(b".bad\xff")), b"hidden").unwrap();
    std::fs::create_dir_all(m.join(bytes(b".dir\xfe"))).unwrap();
    std::fs::write(m.join(bytes(b".dir\xfe")).join("x"), b"x").unwrap();
    // inside the cooked folder nothing is looked at by the identity, a name of any bytes included
    std::fs::write(m.join("assets/cooked").join(bytes(b"clip\xff.bin")), b"c").unwrap();
    assert_eq!(hash::mod_identity(&m).unwrap(), plain.1);
    assert_eq!(hash::dirhash(&m).unwrap_err().code, ErrorCode::Refused, "kdh1 counts that file, and its name is not Unicode");
    std::fs::remove_file(m.join("assets/cooked").join(bytes(b"clip\xff.bin"))).unwrap();
    assert_eq!((hash::dirhash(&m).unwrap(), hash::mod_identity(&m).unwrap()), plain);
    // the reference, on the same folder
    let script = repo().join("schema/fixtures/dirhash/dirhash.py");
    if let Ok(out) = std::process::Command::new("python3").arg(&script).arg(&m).output() {
        assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
        assert!(String::from_utf8_lossy(&out.stdout).starts_with(&plain.0), "{}", String::from_utf8_lossy(&out.stdout));
        let out = std::process::Command::new("python3").arg(&script).arg("--mod").arg(&m).output().unwrap();
        assert!(String::from_utf8_lossy(&out.stdout).starts_with(&plain.1), "{}", String::from_utf8_lossy(&out.stdout));
    }
    // a name that counts and is not Unicode is refused by both
    std::fs::write(m.join(bytes(b"bad\xff")), b"x").unwrap();
    assert_eq!(hash::dirhash(&m).unwrap_err().code, ErrorCode::Refused);
    if let Ok(out) = std::process::Command::new("python3").arg(&script).arg(&m).output() {
        assert!(!out.status.success());
    }
    let _ = std::fs::remove_dir_all(&tmp);
}

#[cfg(unix)]
#[test]
fn mod_identity_does_not_walk_the_cooked_folder_but_refuses_a_link_in_its_place() {
    let tmp = std::env::temp_dir().join(format!("kdh1-mod-test-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    let m = tmp.join("m");
    std::fs::create_dir_all(m.join("assets/cooked")).unwrap();
    std::fs::write(m.join("kiln.mod.json"), b"{}").unwrap();
    std::fs::write(m.join("assets/cooked/a.bin"), b"a").unwrap();
    let plain = hash::mod_identity(&m).unwrap();
    std::os::unix::fs::symlink("a.bin", m.join("assets/cooked/link.bin")).unwrap();
    assert_eq!(hash::dirhash(&m).unwrap_err().code, ErrorCode::Refused);
    assert_eq!(hash::mod_identity(&m).unwrap(), plain);
    std::fs::remove_dir_all(m.join("assets/cooked")).unwrap();
    std::os::unix::fs::symlink(&tmp, m.join("assets/cooked")).unwrap();
    assert_eq!(hash::mod_identity(&m).unwrap_err().code, ErrorCode::Refused);
    // a link named kiln.mod.json is no manifest (and a link below the root is refused anyway)
    std::fs::remove_file(m.join("assets/cooked")).unwrap();
    std::fs::remove_file(m.join("kiln.mod.json")).unwrap();
    std::fs::write(tmp.join("real.json"), b"{}").unwrap();
    std::os::unix::fs::symlink(tmp.join("real.json"), m.join("kiln.mod.json")).unwrap();
    assert!(!hash::is_kiln_mod(&m));
    assert_eq!(hash::mod_identity(&m).unwrap_err().code, ErrorCode::Refused);
    let _ = std::fs::remove_dir_all(&tmp);
}

#[cfg(unix)]
#[test]
fn kdh1_refuses_symlinks_but_skips_dot_entries() {
    let tmp = std::env::temp_dir().join(format!("kdh1-test-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    std::fs::create_dir_all(tmp.join("m")).unwrap();
    std::fs::write(tmp.join("m/a.txt"), b"a").unwrap();
    std::os::unix::fs::symlink("a.txt", tmp.join("m/.hidden-link")).unwrap();
    let ok = hash::dirhash(&tmp.join("m")).unwrap();
    std::os::unix::fs::symlink("a.txt", tmp.join("m/link")).unwrap();
    let err = hash::dirhash(&tmp.join("m")).unwrap_err();
    assert_eq!(err.code, ErrorCode::Refused);
    std::fs::remove_file(tmp.join("m/link")).unwrap();
    assert_eq!(hash::dirhash(&tmp.join("m")).unwrap(), ok);
    let _ = std::fs::remove_dir_all(&tmp);
}

#[test]
fn every_example_deserializes_into_its_type() {
    let p: profile::ProfileFile = example("profile.json");
    assert_eq!(p.mods.kiln.len(), 2);
    assert!(!p.mods.kiln[1].enabled);
    let c: profile::ProfileFile = example("profile-copy.json");
    assert_eq!(c.dropins, profile::Dropins::Kinds(vec![manifest::ModKind::Plugin]));
    let n: manifest::NativeManifest = example("native.json");
    assert_eq!(n.init.as_deref(), Some("kiln_native_init"));
    let plain: manifest::NativeManifest = example("native-plain.json");
    assert_eq!((plain.phase, plain.side, plain.enabled), (manifest::NativePhase::Dllmain, manifest::Side::Client, true));
    let _: config::NativeUserState = example("native-state.json");
    let s: config::SchemaSnapshot = example("config-schema.json");
    assert_eq!(s.entries.len(), 4);
    let _: config::SchemaSnapshot = example("config-schema-emf.json");
    let r: bundle::RuntimeManifest = example("kiln-runtime.json");
    assert!(r.files.iter().any(|f| f.config));
    let _: install::InstallReceipt = example("install-receipt.json");
    let _: modpack::ModpackManifest = example("kiln-modpack.json");
    let b: state::BootJson = example("state-boot.json");
    assert_eq!(b.chain[2].detail, Some(state::RefusalDetail::ShroudForge));
    let _: state::ModsJson = example("state-mods.json");
    let _: state::NativeJson = example("state-native.json");
    let l: state::LastServer = example("state-last-server.json");
    assert_eq!(l.history.len(), 1);
    let _: admin::protocol::Endpoint = example("admin-endpoint.json");
    let (_, j): (kiln_manage::data_folder::Pointer, kiln_manage::data_folder_move::Journal) = (example("data-folder.json"), example("data-folder-move.json"));
    assert_eq!((j.entries.len(), j.rewritten_after.len()), (5, 1));
}

#[test]
fn config_snapshot_paths() {
    let d = Path::new("/cfg");
    assert_eq!(config::snapshot_path(d, "my-mod.json"), Path::new("/cfg/.schema/my-mod.json"));
    assert_eq!(config::snapshot_path(d, "emf/beastmaster.ini"), Path::new("/cfg/.schema/emf/beastmaster.json"));
}

#[test]
fn kap_frames_round_trip_and_match_the_vector() {
    let v: serde_json::Value = serde_json::from_str(&read("schema/fixtures/kap/handshake.json")).unwrap();
    let hello = admin::protocol::decode(v["helloPayload"].as_str().unwrap().as_bytes()).unwrap();
    let frame = admin::protocol::encode(&hello).unwrap();
    assert_eq!(hash::hex(&frame[..4]), &v["helloFrameHex"].as_str().unwrap()[..8]);
    let token = admin::protocol::parse_token(v["tokenFile"].as_str().unwrap()).unwrap();
    assert_eq!(hash::hex(&token), v["token"].as_str().unwrap());
    let nonce = v["nonce"].as_str().unwrap();
    let cnonce = v["cnonce"].as_str().unwrap();
    assert_eq!(admin::protocol::mac_message("kap1-auth", nonce, cnonce), v["authMessage"].as_str().unwrap());
    #[cfg(feature = "admin")]
    {
        assert_eq!(admin::protocol::mac(&token, admin::protocol::AUTH_LABEL, nonce, cnonce), v["mac"].as_str().unwrap());
        assert_eq!(admin::protocol::mac(&token, admin::protocol::READY_LABEL, nonce, cnonce), v["serverMac"].as_str().unwrap());
    }
    let req: admin::protocol::Frame = serde_json::from_str(r#"{"t":"req","id":7,"op":"teleport","args":{"entity":5}}"#).unwrap();
    assert!(matches!(req, admin::protocol::Frame::Req { id: 7, .. }));
}

#[test]
fn errors_serialize_as_code_message_detail() {
    let e = Error::not_implemented("profile::activate");
    assert_eq!(serde_json::to_value(&e).unwrap(), serde_json::json!({"code": "not_implemented", "message": "profile::activate is not implemented yet"}));
    let e = e.with_detail("x");
    assert_eq!(serde_json::to_value(&e).unwrap()["detail"], "x");
}

#[test]
fn library_paths_use_hash8() {
    let d = paths::DataDirs::at("/data");
    let p = d.library_entry(manifest::ModKind::Kiln, "new-items", "1.0.0", "kdh1:3f2a9c1d00112233445566778899aabbccddeeff00112233445566778899aabb").unwrap();
    assert_eq!(p, Path::new("/data/library/kiln/new-items/1.0.0+3f2a9c1d"));
}
