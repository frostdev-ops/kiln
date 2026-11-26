// SPDX-License-Identifier: MIT
//! A client build's record mirrors the server lab proof (owner ruling, 2026-11-23 ~1:20 PM, dev.31 DECISIONS "Registry proof
//! rulings"): no game client is ever run for a proof. A client row is `ok` only when the same row is `ok` in the server
//! build's verified record AND the client exe's reflection gives that row the same layout as the server exe's (every member
//! its paths walk, the leaf types, offsets and the enum values it maps: [`refl::Status::Same`], and the registry's entry
//! members the same). Every other mapped row is `unproven`: a row the server proof left unproven carries the server's
//! reason, a row whose layout differs says so ([`LAYOUT_DIFFERS`]). The record is the client exe's candidate
//! (regmap_check.py --record: its layouts and aliases) with these rows; `verified` only when the server record is verified,
//! of the same game build, and both reflections are of the two exes.
use super::refl::{self, TypeDb};
use super::regmap::{self, RegMap};
use super::{Proof, RowVerdict};
use serde_json::Value;
use std::collections::BTreeMap;

/// The reason of a client row the server proved whose layout on the client is not the server build's.
pub const LAYOUT_DIFFERS: &str = "layout differs from the proven server build";

/// What a mirror judges, already read.
#[derive(Debug, Clone, Default)]
pub struct MirrorInputs {
    pub map: RegMap,
    /// [`regmap::digest`] of the regmap/common judged
    pub regmap_digest: String,
    /// the server build's record (verified by a lab proof)
    pub server: Option<Value>,
    pub server_refl: Option<TypeDb>,
    pub client_sha: String,
    pub client_refl: Option<TypeDb>,
    /// regmap_check.py's record of the client exe (its rows resolved and type-checked, its layouts and aliases)
    pub candidate: Option<Value>,
    pub problems: Vec<String>,
    pub notes: Vec<String>,
}

fn s<'a>(v: &'a Value, k: &str) -> &'a str {
    v.get(k).and_then(Value::as_str).unwrap_or_default()
}

fn short(s: &str) -> &str {
    s.get(..12).unwrap_or(s)
}

/// The client record's rows from the server proof and the two reflections.
pub fn mirror(inp: &MirrorInputs, when: &str) -> Proof {
    let mut p = Proof { when: when.to_string(), side: "client".into(), exe_sha256: inp.client_sha.clone(), regmap_digest: inp.regmap_digest.clone(),
                        ..Default::default() };
    p.blockers.extend(inp.problems.iter().cloned());
    p.notes.extend(inp.notes.iter().cloned());
    let empty = Value::Null;
    let server = inp.server.as_ref().unwrap_or(&empty);
    let server_sha = s(server, "exeSha256").to_ascii_lowercase();
    p.mirror_of = Some(server_sha.clone());
    if inp.server.is_none() {
        p.blockers.push("no server record to mirror".into());
    } else {
        if s(server, "side") != "server" {
            p.blockers.push("the record to mirror is not a server build's".into());
        }
        if server.get("verified").and_then(Value::as_bool) != Some(true) {
            p.blockers.push(format!("the server record of {} is not verified: there is no lab proof to mirror", short(&server_sha)));
        } else if s(server, "provenBy") != super::PROVEN_BY_LAB {
            // review3 S5: a reflection-only candidate (the gate copy's) is verified with every row ok, but proves nothing
            p.blockers.push(format!("the server record of {} is not proven by a lab (provenBy {:?}; kilnctl regproof writes \"lab\" when it \
                                     marks from fresh lab evidence)", short(&server_sha), s(server, "provenBy")));
        }
        match server.get("regmapDigest").and_then(Value::as_str) {
            Some(d) if !d.eq_ignore_ascii_case(&inp.regmap_digest) => p.blockers.push(format!(
                "the server record was proven on another mapping (regmapDigest {}, this tree's regmap/common is {})", short(d), short(&inp.regmap_digest))),
            Some(_) => {}
            None => p.blockers.push("the server record names no regmapDigest: it is not tied to the mapping it was proven on".into()),
        }
    }
    match &inp.server_refl {
        None => p.blockers.push("no reflection dump of the server exe".into()),
        Some(d) if !d.exe_sha256.eq_ignore_ascii_case(&server_sha) => {
            p.blockers.push(format!("the server reflection dump is of exe {}, not of the proven server exe {}", short(&d.exe_sha256), short(&server_sha)))
        }
        _ => {}
    }
    match &inp.client_refl {
        None => p.blockers.push("no reflection dump of the client exe".into()),
        Some(d) if !d.exe_sha256.eq_ignore_ascii_case(&inp.client_sha) => {
            p.blockers.push(format!("the client reflection dump is of exe {}, not of the client exe {}", short(&d.exe_sha256), short(&inp.client_sha)))
        }
        _ => {}
    }
    let mut cand_rows = BTreeMap::new();
    match &inp.candidate {
        None => p.blockers.push("no candidate record: regmap_check.py --record did not run on the client exe".into()),
        Some(c) => {
            if !s(c, "exeSha256").eq_ignore_ascii_case(&inp.client_sha) || s(c, "side") != "client" {
                p.blockers.push(format!("the candidate record is not the client exe {}'s", short(&inp.client_sha)));
            }
            if c.get("verified").and_then(Value::as_bool) != Some(true) {
                p.blockers.push("regmap_check.py did not find every registry's root type in the client exe's reflection".into());
            }
            let (cb, sb) = (s(c, "gameBuild"), s(server, "gameBuild"));
            if cb.is_empty() {
                p.blockers.push("the client's game build is not known (--game-build, or the .kfc beside the exe)".into());
            } else if inp.server.is_some() && cb != sb {
                p.blockers.push(format!("the client exe is of game build {cb}, the server proof of game build {sb}: a proof mirrors only within one game build"));
            }
            if let Some(o) = c.get("rows").and_then(Value::as_object) {
                cand_rows = o.iter().map(|(k, v)| (k.clone(), v.as_str().unwrap_or_default().to_string())).collect();
            }
        }
    }
    if let (Some(a), Some(b)) = (&inp.server_refl, &inp.client_refl) {
        p.refl = Some(refl::diff(&inp.map, a, b));
    }
    let server_rows = server.get("rows").and_then(Value::as_object);
    let server_reasons = server.get("reasons").and_then(Value::as_object);
    for (q, f, _, row) in regmap::qualified(&inp.map) {
        let mut why = Vec::new();
        let mut reason = None;
        let state = if !row.has_mapping() {
            "unmapped"
        } else {
            match server_rows.and_then(|r| r.get(&q)).and_then(Value::as_str) {
                Some("ok") => {
                    let entries = format!("{}.(entries)", f.registry);
                    let mut differs = Vec::new();
                    match p.refl.as_ref() {
                        None => differs.push("reflection: not compared".to_string()),
                        Some(d) => {
                            for k in [&q, &entries] {
                                match d.rows.get(k) {
                                    Some(r) if r.status == refl::Status::Same => {}
                                    Some(r) => {
                                        differs.push(format!("reflection: {k} is {:?} on the client", r.status));
                                        differs.extend(r.changes.iter().map(|c| format!("reflection: {c}")));
                                        differs.extend(r.notes.iter().map(|n| format!("reflection: {}", serde_json::to_string(n).unwrap_or_default())));
                                    }
                                    None => differs.push(format!("reflection: {k} not diffed")),
                                }
                            }
                        }
                    }
                    match cand_rows.get(&q).map(String::as_str) {
                        Some("ok") => {}
                        other => differs.push(format!("regmap_check.py: {} on the client exe", other.unwrap_or("not in the candidate"))),
                    }
                    if differs.is_empty() {
                        why.push(format!("ok in the server proof of {}; the same layout on the client", short(&server_sha)));
                        "ok"
                    } else {
                        why = differs;
                        reason = Some(LAYOUT_DIFFERS.to_string());
                        "unproven"
                    }
                }
                other => {
                    let r = server_reasons.and_then(|r| r.get(&q)).and_then(Value::as_str).map(str::to_string).unwrap_or_else(|| match other {
                        Some("unproven") | None => "not proven on the server build".to_string(),
                        Some(x) => format!("not proven on the server build ({x} there)"),
                    });
                    why.push(format!("server proof: {} ({r})", other.unwrap_or("no row")));
                    reason = Some(r);
                    "unproven"
                }
            }
        };
        p.rows.insert(q, RowVerdict { state: state.into(), why, reason });
    }
    // an element row needs its list row ok (as in the proof)
    for (q, f, field, _) in regmap::qualified(&inp.map) {
        let Some(list) = regmap::list_of(field) else { continue };
        let parent = format!("{}.{}", f.registry, list);
        if p.rows.get(&parent).is_none_or(|r| r.state != "ok") {
            if let Some(row) = p.rows.get_mut(&q).filter(|r| r.state == "ok") {
                let r = format!("its list field {parent} is not proven on this build");
                row.state = "unproven".into();
                row.why.push(r.clone());
                row.reason = Some(r);
            }
        }
    }
    p.verified = p.blockers.is_empty();
    p
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::regproof::record;
    use crate::regproof::refl::tests::{db, old_types};
    use crate::regproof::regmap::tests::{DIGEST, sample};
    use serde_json::json;

    const SERVER: &str = "5555555555555555555555555555555555555555555555555555555555555555";
    const CLIENT: &str = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    fn server_record() -> Value {
        json!({"regmap": 1, "side": "server", "exeSha256": SERVER, "gameBuild": "101", "verified": true, "proof": "kilnctl regproof (lab)",
               "regmapDigest": DIGEST, "provenBy": "lab",
               "rows": {"gadgets.size": "ok", "gadgets.tier": "ok", "gadgets.power": "unproven", "gadgets.label": "ok", "gadgets.glow": "unmapped",
                        "plans.parts": "ok", "plans.parts[].count": "ok", "plans.cap": "changed"},
               "reasons": {"gadgets.power": "no lab round trip of it on this build yet"},
               "aliases": {"renames": [], "removals": [], "suffixes": []}})
    }

    fn client_candidate() -> Value {
        json!({"regmap": 1, "side": "client", "exeSha256": CLIENT, "gameBuild": "101", "steamBuild": "7", "verified": true,
               "proof": "regmap_check.py --exe", "layouts": {"demo::Gadget": "0x00001111"},
               "rows": {"gadgets.size": "ok", "gadgets.tier": "ok", "gadgets.power": "ok", "gadgets.label": "ok", "gadgets.glow": "unmapped",
                        "plans.parts": "ok", "plans.parts[].count": "ok", "plans.cap": "ok"},
               "aliases": {"renames": [{"registry": "gadgets", "guid": "g1", "id": "game:a"}], "removals": [], "suffixes": []}})
    }

    fn inputs() -> MirrorInputs {
        MirrorInputs {
            map: sample(),
            regmap_digest: DIGEST.into(),
            server: Some(server_record()),
            server_refl: Some(db(old_types(), SERVER)),
            client_sha: CLIENT.into(),
            client_refl: Some(db(old_types(), CLIENT)),
            candidate: Some(client_candidate()),
            ..Default::default()
        }
    }

    /// Owner ruling: ok only where the server proved it and the client's layout is the server's; the server's reason for
    /// its unproven rows; nothing a client run would have to show.
    #[test]
    fn a_client_row_is_ok_only_where_the_server_proved_it_with_the_same_layout() {
        let p = mirror(&inputs(), "T");
        assert!(p.verified, "{:?}", p.blockers);
        let st = |q: &str| p.rows[q].state.as_str();
        assert_eq!(st("gadgets.size"), "ok");
        assert_eq!(st("gadgets.tier"), "ok");
        assert_eq!(st("gadgets.label"), "ok");
        assert_eq!(st("plans.parts"), "ok");
        assert_eq!(st("plans.parts[].count"), "ok");
        assert_eq!(st("gadgets.glow"), "unmapped");
        // the server's unproven row, with the server's reason; a changed server row is not proven there either
        assert_eq!(st("gadgets.power"), "unproven");
        assert_eq!(p.rows["gadgets.power"].reason.as_deref(), Some("no lab round trip of it on this build yet"));
        assert_eq!(st("plans.cap"), "unproven", "the client never claims more than the server proof: {:?}", p.rows["plans.cap"]);
        assert!(p.rows["plans.cap"].reason.as_deref().is_some_and(|r| r.contains("changed")), "{:?}", p.rows["plans.cap"]);
        let rec = record(&p, &client_candidate());
        assert_eq!(rec["side"], "client");
        assert_eq!(rec["verified"], true);
        assert_eq!(rec["rows"]["gadgets.power"], "unproven");
        assert_eq!(rec["reasons"]["gadgets.power"], "no lab round trip of it on this build yet");
        assert_eq!(rec["aliases"], client_candidate()["aliases"], "the client's own aliases and layouts are kept");
        assert_eq!(rec["layouts"], client_candidate()["layouts"]);
        assert_eq!(rec["regmapDigest"], DIGEST, "the mirror is bound to the same mapping");
        assert_eq!(rec["provenBy"], "mirror", "a mirror is never a lab proof another mirror could start from");
        let s = rec["proof"].as_str().unwrap();
        assert!(s.contains("mirror") && s.contains(&SERVER[..12]) && s.contains("rows ok 5/7 (2 unproven)"), "{s}");
    }

    #[test]
    fn a_layout_the_client_does_not_share_leaves_the_row_unproven() {
        // a member of the power formula moved on the client (Carried between builds, but not the proven layout)
        let mut n = old_types();
        let at = n.iter().position(|t| t["name"] == "demo::Gadget").unwrap();
        n[at]["fields"][0]["name"] = json!("maxStack");
        let hit = n.iter().position(|t| t["name"] == "demo::Part").unwrap();
        n[hit] = json!({"name": "demo::Part", "kind": refl::K_STRUCT, "size": 4, "layout": 7, "inner": null,
                        "fields": [{"name": "spare", "type": "demo::u16", "offset": 0}, {"name": "amount", "type": "demo::u16", "offset": 2}]});
        let mut inp = inputs();
        inp.client_refl = Some(db(n, CLIENT));
        let p = mirror(&inp, "T");
        assert!(p.verified, "{:?}", p.blockers);
        for q in ["gadgets.size", "plans.parts[].count"] {
            assert_eq!(p.rows[q].state, "unproven", "{q}: {:?}", p.rows[q]);
            assert_eq!(p.rows[q].reason.as_deref(), Some(LAYOUT_DIFFERS), "{q}");
        }
        assert_eq!(p.rows["gadgets.tier"].state, "ok");
        // a row regmap_check.py could not resolve on the client exe
        let mut inp = inputs();
        inp.candidate.as_mut().unwrap()["rows"]["gadgets.tier"] = json!("unmapped");
        let p = mirror(&inp, "T");
        assert_eq!((p.rows["gadgets.tier"].state.as_str(), p.rows["gadgets.tier"].reason.as_deref()), ("unproven", Some(LAYOUT_DIFFERS)));
        // the list's layout differs: its element rows are not proven either
        let mut inp = inputs();
        inp.candidate.as_mut().unwrap()["rows"]["plans.parts"] = json!("changed");
        let p = mirror(&inp, "T");
        assert_eq!(p.rows["plans.parts[].count"].state, "unproven");
    }

    /// dev.32 limits: a hooks registry has no reflection; its client row is ok where the server proved it and regmap_check.py
    /// resolved Kiln's hook of it on the client exe (the candidate), unproven otherwise
    #[test]
    fn a_hooks_row_mirrors_on_the_clients_candidate() {
        let caps = r#"{"regmap": 1, "registry": "caps", "entries": {"kind": "hooks", "unnamed": "caps"},
            "rows": {"most": {"paths": ["altarCap"], "conv": "int", "read": "now", "backend": "hook"}}}"#;
        let mut inp = inputs();
        inp.map.insert("caps".into(), crate::regproof::regmap::parse(caps, "caps").unwrap());
        inp.server.as_mut().unwrap()["rows"]["caps.most"] = json!("ok");
        inp.candidate.as_mut().unwrap()["rows"]["caps.most"] = json!("ok");
        let p = mirror(&inp, "T");
        assert!(p.verified, "{:?}", p.blockers);
        assert_eq!(p.rows["caps.most"].state, "ok", "{:?}", p.rows["caps.most"]);
        inp.candidate.as_mut().unwrap()["rows"]["caps.most"] = json!("unmapped");
        let p = mirror(&inp, "T");
        assert_eq!((p.rows["caps.most"].state.as_str(), p.rows["caps.most"].reason.as_deref()), ("unproven", Some(LAYOUT_DIFFERS)));
    }

    #[test]
    fn the_mirror_fails_closed() {
        type Strip = fn(&mut MirrorInputs);
        let cases: Vec<(Strip, &str)> = vec![
            (|i| i.server = None, "no server record"),
            (|i| i.server.as_mut().unwrap()["verified"] = json!(false), "not verified"),
            (|i| i.server.as_mut().unwrap()["side"] = json!("client"), "not a server build"),
            (|i| i.server.as_mut().unwrap()["regmapDigest"] = json!("e2".repeat(32)), "another mapping"),
            (|i| { i.server.as_mut().unwrap().as_object_mut().unwrap().remove("regmapDigest"); }, "names no regmapDigest"),
            // review3 S5: only a lab-proven server record (the gate copy's reflection-only candidate is verified, all ok and
            // carries the copy's digest, but proves nothing)
            (|i| { i.server.as_mut().unwrap().as_object_mut().unwrap().remove("provenBy"); }, "not proven by a lab"),
            (|i| i.server.as_mut().unwrap()["provenBy"] = json!("mirror"), "not proven by a lab"),
            (|i| i.server_refl = None, "server exe"),
            (|i| i.server_refl = Some(db(old_types(), CLIENT)), "server reflection dump"),
            (|i| i.client_refl = None, "client exe"),
            (|i| i.client_refl = Some(db(old_types(), SERVER)), "client reflection dump"),
            (|i| i.candidate = None, "candidate record"),
            (|i| i.candidate.as_mut().unwrap()["side"] = json!("server"), "candidate record"),
            (|i| i.candidate.as_mut().unwrap()["verified"] = json!(false), "root type"),
            (|i| i.candidate.as_mut().unwrap()["gameBuild"] = json!("102"), "game build 102"),
            (|i| i.candidate.as_mut().unwrap()["gameBuild"] = json!(""), "game build is not known"),
        ];
        for (strip, word) in cases {
            let mut inp = inputs();
            strip(&mut inp);
            let p = mirror(&inp, "T");
            assert!(!p.verified, "{word}: should block");
            assert!(p.blockers.iter().any(|b| b.contains(word)), "{word:?} not in {:?}", p.blockers);
            assert_eq!(record(&p, &client_candidate())["verified"], false, "{word}");
            assert!(record(&p, &client_candidate()).get("provenBy").is_none(), "{word}: an unverified record is proven by nothing");
        }
    }
}
