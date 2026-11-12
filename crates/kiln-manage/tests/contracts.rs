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
    assert!(cases.len() >= 7);
    for (name, want) in cases {
        let got = hash::dirhash(&dir.join("cases").join(name)).unwrap();
        assert_eq!(&got, want.as_str().unwrap(), "case {name}");
    }
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
