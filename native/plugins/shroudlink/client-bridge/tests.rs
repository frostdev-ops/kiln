// SPDX-License-Identifier: MIT
use super::*;
use shroudlink::{decode_packet, split_message, Frame, Reassembler};
fn call(b: &mut Bridge, v: Value, now: u64) -> Result<Value, Error> {
    b.rpc(&serde_json::to_vec(&v).unwrap(), MAX_REPLY, now)
        .map(|v| serde_json::from_slice(&v).unwrap())
        .map_err(|e| e.0)
}
fn fixture() -> Bridge {
    let mut b = Bridge::new("module-a".into());
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
    let mut b = Bridge::new("m".into());
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
#[test]
fn world_departure_client_change_and_tick_gap_fail_closed() {
    for transition in 0..3 {
        let mut b = fixture();
        let s = acquire(&mut b, "kiln", 100);
        let req = event(&b, &s, "button", json!({"kind":"click"}));
        call(&mut b, req, 100).unwrap();
        match transition {
            0 => b.world(false),
            1 => b.tick(101, 2),
            _ => b.tick(2201, 1),
        };
        b.world(true);
        assert!(b.take(2201, 10000).is_none());
        assert!(!b.ready(2201));
    }
}
#[test]
fn requires_beacon_protocol_world_and_actual_tick() {
    let mut b = Bridge::new("m".into());
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
