// SPDX-License-Identifier: MIT
use super::*;
use shroudlink::{decode_packet, split_message, Frame, Reassembler};
fn call(b: &mut Bridge, v: Value, now: u64) -> Result<Value, Error> {
    b.rpc(&serde_json::to_vec(&v).unwrap(), MAX_REPLY, now)
        .map(|v| serde_json::from_slice(&v).unwrap())
        .map_err(|e| e.0)
}
// Test randomness only; the addon uses BCryptGenRandom.
fn secret() -> Option<u128> {
    use std::hash::{BuildHasher, RandomState};
    let s = RandomState::new();
    Some(u128::from(s.hash_one(1u8)) << 64 | u128::from(s.hash_one(2u8)))
}
fn fixture() -> Bridge {
    let mut b = Bridge::new("module-a".into(), secret);
    b.begin_session();
    b.welcome(2);
    b.world(true);
    b.tick(100, 1);
    // The real server-to-client serde contract, not a synthetic module catalog.
    b.observe(&serde_json::from_value(json!({"op":"entries","owner":"storage","entries":[{"id":"settings","label":"Storage"}]})).unwrap());
    b.observe(&serde_json::from_value(json!({"op":"open","owner":"storage","window":{"id":"w","title":"Settings","content":[
        {"type":"text","text":"Actual message"},{"type":"heading","text":"Heading"},{"type":"separator"},
        {"type":"row","children":[{"type":"button","id":"button","label":"Go"}]},
        {"type":"text_input","id":"input","value":"old","hint":"Name"},
        {"type":"toggle","id":"toggle","label":"Enabled","value":false},
        {"type":"item_grid","id":"items","columns":2,"items":[{"id":42,"count":3}]},
        {"type":"list","id":"list","entries":["one"]},
        {"type":"tabs","id":"tabs","tabs":["a","b"],"selected":0}
    ]}})).unwrap());
    b
}
fn acquire(b: &mut Bridge, client: &str, now: u64) -> Value {
    call(
        b,
        json!({"op":"acquire","module":b.module,"session":b.session(),"client":client}),
        now,
    )
    .unwrap()
}
fn event(b: &Bridge, s: &Value, node: &str, e: Value) -> Value {
    json!({"op":"event","module":s["module"],"session":s["session"],"lease":s["lease"],"revision":b.revision,"owner":"storage","event":{"window":"w","node":node,"event":e}})
}
#[test]
fn actual_state_and_all_ten_nodes() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    assert_eq!(s["entries"][0]["label"], "Storage");
    assert_eq!(
        s["windows"][0]["window"]["content"]
            .as_array()
            .unwrap()
            .len(),
        9
    );
    assert_eq!(
        s["windows"][0]["window"]["content"][3]["children"][0]["type"],
        "button"
    );
}
#[test]
fn no_fabricated_menu_from_welcome() {
    let mut b = Bridge::new("m".into(), secret);
    b.begin_session();
    b.welcome(2);
    b.world(true);
    b.tick(100, 1);
    assert_eq!(
        call(&mut b, json!({"op":"read"}), 100).unwrap()["entries"],
        json!([])
    );
}
#[test]
fn menu_and_all_interactive_event_types() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let cases = [
        ("button", json!({"kind":"click"})),
        ("input", json!({"kind":"change","value":"new"})),
        ("input", json!({"kind":"submit","value":"new"})),
        ("toggle", json!({"kind":"change","value":true})),
        ("items", json!({"kind":"click","index":0})),
        ("list", json!({"kind":"click","index":0})),
        ("tabs", json!({"kind":"click","index":1})),
        ("", json!({"kind":"closed"})),
    ];
    for (n, e) in cases {
        let req = event(&b, &s, n, e);
        call(&mut b, req, 100).unwrap();
    }
    let mut req = event(&b, &s, "settings", json!({"kind":"menu"}));
    req["event"]["window"] = json!("");
    call(&mut b, req, 100).unwrap();
    assert_eq!(b.queue.len(), 9);
}
#[test]
fn rejects_wrong_event_kind_value_and_index() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    for (n, e) in [
        ("button", json!({"kind":"change","value":true})),
        ("toggle", json!({"kind":"change","value":"yes"})),
        ("items", json!({"kind":"click","index":1})),
        ("tabs", json!({"kind":"click","index":2})),
        ("list", json!({"kind":"click"})),
        ("missing", json!({"kind":"click"})),
        ("input", json!({"kind":"submit","value":"x".repeat(257)})),
    ] {
        let req = event(&b, &s, n, e);
        assert_eq!(call(&mut b, req, 100), Err(Error::Invalid));
    }
    assert!(b.queue.is_empty());
}
#[test]
fn stale_owner_window_revision_session_and_module() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let good = event(&b, &s, "button", json!({"kind":"click"}));
    for (k, v, want) in [
        ("owner", json!("assistants"), Error::Invalid),
        ("module", json!("other"), Error::Stale),
        ("session", json!("other"), Error::Stale),
        ("lease", json!("other"), Error::Stale),
        ("revision", json!(0), Error::Stale),
    ] {
        let mut req = good.clone();
        req[k] = v;
        assert_eq!(call(&mut b, req, 100), Err(want));
    }
    let mut req = good.clone();
    req["event"]["window"] = json!("other");
    assert_eq!(call(&mut b, req, 100), Err(Error::Invalid));
    call(&mut b, good, 100).unwrap();
    b.observe(&ToClient::Entries {
        owner: "storage".into(),
        entries: vec![],
    });
    assert!(b.take(100, 10000).is_none());
}
#[test]
fn lease_conflict_release_expiry_and_reacquire() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let req = json!({"op":"acquire","module":b.module,"session":b.session(),"client":"other"});
    assert_eq!(call(&mut b, req, 100), Err(Error::Busy));
    let req = event(&b, &s, "button", json!({"kind":"click"}));
    call(&mut b, req, 100).unwrap();
    call(
        &mut b,
        json!({"op":"release","module":s["module"],"session":s["session"],"lease":s["lease"]}),
        100,
    )
    .unwrap();
    assert!(b.take(100, 10000).is_none());
    let next = acquire(&mut b, "other", 100);
    assert_ne!(s["lease"], next["lease"]);
    b.tick(2000, 1);
    assert!(!b.presenting(2100));
}
#[test]
fn shutdown_replaced_copy_and_reconnect_clear_ownership() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let old = event(&b, &s, "button", json!({"kind":"click"}));
    call(&mut b, old.clone(), 100).unwrap();
    b.begin_session();
    b.welcome(2);
    b.world(true);
    b.tick(101, 1);
    assert_eq!(call(&mut b, old.clone(), 101), Err(Error::Stale));
    assert!(b.take(101, 10000).is_none());
    assert_eq!(
        call(&mut b, json!({"op":"read"}), 101).unwrap()["windows"],
        json!([])
    );
    b.stop();
    assert_eq!(
        call(&mut b, json!({"op":"read"}), 102),
        Err(Error::Inactive)
    );
    let mut replacement = fixture();
    replacement.module = "module-b".into();
    assert_eq!(call(&mut replacement, old, 100), Err(Error::Stale));
}
fn read(b: &mut Bridge, now: u64) -> Value {
    call(b, json!({"op":"read"}), now).unwrap()
}
#[test]
fn world_departure_and_update_stall_end_ownership_but_keep_the_session() {
    for stall in [false, true] {
        let mut b = fixture();
        let mut s = acquire(&mut b, "kiln", 100);
        let (queued, at) = if stall { (2000, 2101) } else { (100, 101) };
        if stall {
            // Renewed close to the stall: the lease itself is still valid when the update goes stale.
            s = acquire(&mut b, "kiln", 2000);
        }
        let req = event(&b, &s, "button", json!({"kind":"click"}));
        call(&mut b, req.clone(), queued).unwrap();
        if !stall {
            b.world(false);
        }
        // While it lasts: not ready, the lease and the queue are gone.
        assert!(!b.ready(at));
        assert!(b.take(at, 10000).is_none());
        assert_eq!(call(&mut b, req.clone(), at), Err(Error::Stale));
        assert_eq!(read(&mut b, at)["leased"], false);
        // The next frame in the world / client update: the same session is ready again with what it received.
        b.world(true);
        b.tick(at + 1, 1);
        assert!(b.ready(at + 1));
        let r = read(&mut b, at + 1);
        assert_eq!(r["session"], s["session"]);
        assert_eq!(r["entries"][0]["label"], "Storage");
        assert_eq!(r["windows"].as_array().unwrap().len(), 1);
        assert_eq!(r["leased"], false);
        // Ownership is not revived: neither the old lease nor its queued event.
        assert!(b.take(at + 1, 10000).is_none());
        assert_eq!(call(&mut b, req, at + 1), Err(Error::Stale));
        assert_ne!(acquire(&mut b, "kiln", at + 1)["lease"], s["lease"]);
    }
}
#[test]
fn client_change_starts_a_new_session() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let req = event(&b, &s, "button", json!({"kind":"click"}));
    call(&mut b, req.clone(), 100).unwrap();
    b.tick(101, 2);
    // Inside every lifetime: only the client change can explain this.
    assert!(!b.ready(101));
    assert!(b.take(101, 10000).is_none());
    assert_eq!(call(&mut b, req, 101), Err(Error::Stale));
    let r = read(&mut b, 101);
    assert_ne!(r["session"], s["session"]);
    assert_eq!(r["entries"], json!([]));
}
#[test]
fn lease_token_cannot_be_derived_from_what_read_returns() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let token = s["lease"].as_str().unwrap().to_owned();
    let r = read(&mut b, 100);
    let (module, session) = (r["module"].as_str().unwrap(), r["session"].as_str().unwrap());
    assert!(r["lease"].is_null());
    assert!(!token.contains(module) && !token.contains(&format!(":{session}:")));
    // Another in-process reader guessing from module, session and a counter frees nothing.
    for serial in 0..16 {
        let guess = format!("{module}:{session}:{serial}");
        let release = json!({"op":"release","module":module,"session":session,"lease":guess});
        assert_eq!(call(&mut b, release, 100), Err(Error::Stale));
    }
    assert_eq!(read(&mut b, 100)["leased"], true);
    // A new acquisition gets a new token, not the next one in a sequence.
    call(
        &mut b,
        json!({"op":"release","module":module,"session":session,"lease":token}),
        100,
    )
    .unwrap();
    let next = acquire(&mut b, "kiln", 100)["lease"].as_str().unwrap().to_owned();
    assert_ne!(next, token);
    assert!(!next.contains(module));
}
fn open(owner: &str, id: &str, content: Value) -> ToClient {
    serde_json::from_value(json!({"op":"open","owner":owner,"window":{"id":id,"title":"t","content":content}})).unwrap()
}
fn entries(owner: &str, n: usize) -> ToClient {
    let entries: Vec<Value> = (0..n).map(|i| json!({"id":format!("e{i}"),"label":"x"})).collect();
    serde_json::from_value(json!({"op":"entries","owner":owner,"entries":entries})).unwrap()
}
// Each limit once at its bound (kept) and once past it (the session faults: no actionable state is left).
fn holds(messages: &[ToClient]) -> bool {
    let mut b = Bridge::new("m".into(), secret);
    b.begin_session();
    b.welcome(2);
    b.world(true);
    b.tick(100, 1);
    for m in messages {
        b.observe(m);
    }
    read(&mut b, 100)["ready"].as_bool().unwrap()
}
#[test]
fn aggregate_observe_limits() {
    let owners = |n: usize| (0..n).map(|i| entries(&format!("o{i}"), 1)).collect::<Vec<_>>();
    assert!(holds(&owners(64)));
    assert!(!holds(&owners(65)));
    assert!(holds(&[entries("a", 64), entries("b", 64)]));
    assert!(!holds(&[entries("a", 64), entries("b", 64), entries("c", 1)]));
    assert!(!holds(&[entries("a", 65)]));
    let windows = |n: usize| (0..n).map(|i| open("o", &format!("w{i}"), json!([]))).collect::<Vec<_>>();
    assert!(holds(&windows(32)));
    assert!(!holds(&windows(33)));
    // 128 KiB of state: about 120 KB is kept, about 140 KB is not.
    let text = |n: usize| (0..n).map(|_| json!({"type":"text","text":"x".repeat(4000)})).collect::<Vec<_>>();
    assert!(holds(&[open("o", "w", json!(text(30)))]));
    assert!(!holds(&[open("o", "w", json!(text(35)))]));
    // Depth 12 of nested rows, 128 nodes per list, 1024 nodes per window.
    let nested = |depth: usize| (0..depth).fold(json!([{"type":"separator"}]), |inner, _| json!([{"type":"row","children":inner}]));
    assert!(holds(&[open("o", "w", nested(12))]));
    assert!(!holds(&[open("o", "w", nested(13))]));
    let flat = |n: usize| json!((0..n).map(|_| json!({"type":"separator"})).collect::<Vec<_>>());
    assert!(holds(&[open("o", "w", flat(128))]));
    assert!(!holds(&[open("o", "w", flat(129))]));
    let rows = |extra: usize| {
        let mut nodes: Vec<Value> = (0..8).map(|_| json!({"type":"row","children":flat(127)})).collect();
        nodes.extend((0..extra).map(|_| json!({"type":"separator"})));
        json!(nodes)
    };
    assert!(holds(&[open("o", "w", rows(0))]));
    assert!(!holds(&[open("o", "w", rows(1))]));
}
#[test]
fn requires_beacon_protocol_world_and_actual_tick() {
    let mut b = Bridge::new("m".into(), secret);
    b.welcome(2);
    b.world(true);
    b.tick(100, 1);
    assert!(!b.ready(100));
    b.begin_session();
    b.welcome(1);
    b.world(true);
    b.tick(100, 1);
    assert!(!b.ready(100));
    b.welcome(2);
    b.world(false);
    assert!(!b.ready(100));
}
#[test]
fn probes_and_short_buffers_are_non_mutating() {
    let mut b = fixture();
    let req = serde_json::to_vec(
        &json!({"op":"acquire","module":b.module,"session":b.session(),"client":"kiln"}),
    )
    .unwrap();
    assert_eq!(b.rpc(&req, 0, 100).unwrap_err().0, Error::Small);
    assert!(b.lease.is_none());
    let s = acquire(&mut b, "kiln", 100);
    let req = event(&b, &s, "button", json!({"kind":"click"}));
    assert_eq!(
        b.rpc(&serde_json::to_vec(&req).unwrap(), 0, 100)
            .unwrap_err()
            .0,
        Error::Small
    );
    assert!(b.queue.is_empty());
}
#[test]
fn queue_and_input_bounds() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let req = event(&b, &s, "button", json!({"kind":"click"}));
    for _ in 0..32 {
        call(&mut b, req.clone(), 100).unwrap();
    }
    assert_eq!(call(&mut b, req, 100), Err(Error::Busy));
    assert_eq!(
        b.rpc(&vec![b' '; MAX_REQUEST + 1], MAX_REPLY, 100)
            .unwrap_err()
            .0,
        Error::Invalid
    );
    assert_eq!(
        call(&mut b, json!({"op":"receive","message":{}}), 100),
        Err(Error::Invalid)
    );
    assert_eq!(
        call(&mut b, json!({"op":"read","extra":1}), 100),
        Err(Error::Invalid)
    );
}
#[test]
fn invalid_replacement_never_leaves_stale_actionable_window() {
    for content in [
        json!([{"type":"button","id":"dup","label":"a"},{"type":"toggle","id":"dup","label":"b","value":false}]),
        json!([{"type":"item_grid","id":"i","columns":0,"items":[]}]),
        json!([{"type":"text","text":"x".repeat(4097)}]),
    ] {
        let mut b = fixture();
        let msg=serde_json::from_value(json!({"op":"open","owner":"storage","window":{"id":"w","title":"bad","content":content}})).unwrap();
        b.observe(&msg);
        let s = call(&mut b, json!({"op":"read"}), 100).unwrap();
        assert_eq!(s["ready"], false);
        assert_eq!(s["windows"], json!([]));
    }
}
#[test]
fn committed_close_is_revalidated_and_removes_only_owned_window() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let req = event(&b, &s, "", json!({"kind":"closed"}));
    call(&mut b, req, 100).unwrap();
    let (ticket, _) = b.take(100, 10000).unwrap();
    assert_eq!(b.windows.len(), 1);
    let mut stale = b.clone();
    stale.begin_session();
    assert!(stale.committed(&ticket, 100).is_none());
    assert_eq!(
        b.committed(&ticket, 100),
        Some(("storage".into(), "w".into()))
    );
    assert!(b.windows.is_empty());
    assert!(b.committed(&ticket, 100).is_none());
}
#[test]
fn game_thread_codec_preserves_owner_and_whole_message_budget() {
    let mut b = fixture();
    let s = acquire(&mut b, "kiln", 100);
    let req = event(&b, &s, "input", json!({"kind":"submit","value":"snow 雪"}));
    call(&mut b, req, 100).unwrap();
    assert!(b.take(100, 1).is_none());
    let (ticket, message) = b.take(100, 10000).unwrap();
    assert!(b.valid_ticket(&ticket, 100));
    let bytes = serde_json::to_vec(&message).unwrap();
    let mut re = Reassembler::default();
    let mut result = None;
    for packet in split_message(0x1234, 9, &bytes) {
        if let Frame::Data {
            nonce,
            msg,
            chunk,
            count,
            bytes,
        } = Frame::decode(&decode_packet(packet.as_bytes()).unwrap()).unwrap()
        {
            result = re.push(nonce, msg, chunk, count, bytes).or(result);
        }
    }
    let decoded: Value = serde_json::from_slice(&result.unwrap()).unwrap();
    assert_eq!(decoded["mod"], "storage");
    assert_eq!(
        UiEvent::from_message(&decoded).unwrap().event,
        Event::Submit {
            value: "snow 雪".into()
        }
    );
    b.observe(&ToClient::Close {
        owner: "storage".into(),
        id: "w".into(),
    });
    assert!(!b.valid_ticket(&ticket, 100));
}
