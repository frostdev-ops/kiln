//! schema/fixtures/ini/** (kini.py's cases): play-once.ini decisions and chain.ini / once.ini entries.

use kiln_manage::playonce::{PlayOnceStatus, decide_detail};
use kiln_manage::{chain, playonce};
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

fn fixtures() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/ini")
}

fn cases(dir: &str) -> Vec<(String, Value, Vec<u8>)> {
    let mut v: Vec<_> = std::fs::read_dir(fixtures().join(dir))
        .unwrap()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "json"))
        .collect();
    v.sort();
    v.into_iter()
        .map(|p| {
            let spec: Value = serde_json::from_str(&std::fs::read_to_string(&p).unwrap()).unwrap();
            let ini = std::fs::read(p.with_extension("ini")).unwrap();
            (p.file_name().unwrap().to_string_lossy().into_owned(), spec, ini)
        })
        .collect()
}

fn status_name(s: PlayOnceStatus) -> Value {
    serde_json::to_value(s).unwrap()
}

#[test]
fn play_once_fixtures() {
    let all = cases("play-once");
    assert!(all.len() >= 17, "{} cases", all.len());
    for (name, spec, ini) in all {
        let d = decide_detail(&ini, spec["now"].as_i64().unwrap(), spec["server"].as_bool().unwrap_or(false),
            spec["deletable"].as_bool().unwrap_or(true));
        let mut got = json!({ "status": status_name(d.status) });
        match d.status {
            PlayOnceStatus::IgnoredInvalid => got["error"] = json!(d.error),
            PlayOnceStatus::IgnoredExpired => got["nonce"] = json!(d.flag.as_ref().unwrap().nonce),
            PlayOnceStatus::Vanilla | PlayOnceStatus::NoMods | PlayOnceStatus::Profile => {
                let f = d.flag.as_ref().unwrap();
                got["created"] = json!(f.created);
                got["nonce"] = json!(f.nonce);
                if let Some(p) = &f.profile {
                    got["profile"] = json!({ "path": p, "relative": d.profile_relative.unwrap() });
                }
            }
            _ => {}
        }
        assert_eq!(got, spec["expect"], "play-once/{name}");
        // the short API agrees
        assert_eq!(playonce::decide(&ini, spec["now"].as_i64().unwrap(), spec["server"].as_bool().unwrap_or(false),
            spec["deletable"].as_bool().unwrap_or(true)).0, d.status, "{name}");
    }
}

#[test]
fn chain_fixtures() {
    let all = cases("chain");
    assert!(all.len() >= 7, "{} cases", all.len());
    for (name, spec, ini) in all {
        let got = if spec["file"] == "once" {
            serde_json::to_value(chain::parse_once(&ini)).unwrap()
        } else {
            serde_json::to_value(chain::parse(&ini)).unwrap()
        };
        assert_eq!(got, spec["expect"], "chain/{name}");
    }
}

#[test]
fn rendered_play_once_parses_like_the_proxy_reads_it() {
    for mode in [playonce::PlayOnceMode::Vanilla, playonce::PlayOnceMode::NoMods, playonce::PlayOnceMode::Profile] {
        let p = playonce::PlayOnce { mode, created: 1_762_257_600, nonce: "4f1c0e3a9b2d4e6f4f1c0e3a9b2d4e6f".into(),
            profile: (mode == playonce::PlayOnceMode::Profile).then(|| "profiles/x/profile.json".into()) };
        let d = decide_detail(playonce::render(&p).as_bytes(), 1_762_257_610, false, true);
        let f = d.flag.unwrap();
        assert_eq!((f.mode, f.created, f.nonce.as_str()), (p.mode, p.created, p.nonce.as_str()));
        if let Some(pp) = &p.profile {
            assert_eq!(f.profile.as_deref(), Some(pp.replace('/', "\\").as_str()));
        }
    }
}
