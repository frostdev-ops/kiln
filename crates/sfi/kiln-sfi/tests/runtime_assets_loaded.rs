// SPDX-License-Identifier: MIT
// the interpreter's game.assets loaded-resource members tests.
//! `game.assets.get_loaded_resource`, `get_loaded_resources_by_type`, `read_loaded_field` and
//! `resolve_loaded_resource` on kiln.assets' loaded-resource fake (support/fake_services `LoadedFake`) through the real
//! service adapter, the archive registry and the VM's typed values (support/fake_values), on a scripted registrar host.
//! Expected texts and shapes are ShroudForge's (MIT, Copyright (c) 2026 bonsaibauer), 1.7.9 d8a394bc:
//! modules/mod-runtime/lua/ingame/resources/read_loaded_resources.rs, lua/bindings/game/assets/resource.rs,
//! session/loaded_resources.rs, lua/values/resolve_loaded_field.rs and game-runtime/bridge/windows/resources/
//! {read_resource,find_resources_by_type,check_resource_exists,resolve_item_resource}.rs.
#[path = "shared/ecs_fixture.rs"]
mod fixture;

use fixture::fake_services::{self as fs, LoadedFake};
use fixture::*;
use kiln_sfi::runtime::assets::resource::{self as res, guid_text, parse_guid, text as r};
use kiln_sfi::runtime::assets::{self, Assets, CLAIMS, text as t};
use kiln_sfi::runtime::ecs::template::text as tpl;
use kiln_sfi::runtime::types::{self, TypesWiring};
use kiln_sfi::runtime::unavailable::{self, NO_RESOURCE_LISTING, NO_RESOURCE_READ};
use kiln_sfi::runtime::values::ValuesArena;
use serde_json::json;
use sfi_core::bindings::runtime::RuntimeHost;

const GUID: &str = "1d622bd9-8170-41b1-822e-d3984b386d95";
const ITEM_INFO: &str = "keen::ItemInfo";

/// The fixture's types and three more: a GUID, ShroudForge's item id struct and an ItemInfo with both.
fn types_with_items() -> Vec<T> {
    let mut list = archive_types();
    let base = list.len() as u64;
    let plain = |qualified: &'static str,
                 primitive: &'static str,
                 size: u32,
                 fields: Vec<(&'static str, u64, u64)>| T {
        qualified,
        primitive,
        size,
        inner: None,
        count: 0,
        fields,
        flags: 0,
    };
    list.push(plain("keen::Guid", "Guid", 16, vec![]));
    list.push(plain(
        "keen::ItemId",
        "Struct",
        4,
        vec![("value", UINT32 as u64, 0)],
    ));
    list.push(plain(
        ITEM_INFO,
        "Struct",
        24,
        vec![("itemId", base + 1, 0), ("objectId", base, 8)],
    ));
    list
}

/// Every value_open the typed values saw: origin, type, bytes, source instance.
type Opened = Rc<RefCell<Vec<(u32, String, Vec<u8>, u64)>>>;

struct Vm {
    facts: Rc<kiln_sfi::runtime::RuntimeFacts>,
    beta: Rc<Script>,
    owner: KilnOwner,
    vm: fixture::fake_values::TestVm,
    _arena: ValuesArena,
    host: Rc<Script>,
    session: Rc<Cell<u64>>,
    opened: Opened,
    wiring: TypesWiring,
    assets: Rc<Assets>,
}

/// A VM of package "alpha" with the members in its `game.assets` over `loaded` (None: kiln.assets answers
/// NOT_READY), and `with_items` the ItemInfo types.
fn vm(loaded: Option<LoadedFake>, with_items: bool) -> Vm {
    let owner = support::next_owner();
    fs::bind_thread(owner);
    fs::set_loaded(owner, loaded);
    let api: &'static KilnApi = Box::leak(fs::api(true));
    let services = Rc::new(kiln_sfi::runtime::ServiceAdapter::new(
        unsafe { kiln_sfi::worker::Host::read(api) },
        Rc::new(kiln_sfi::lifecycle::Watchdog::new(
            kiln_sfi::lifecycle::process_clock(),
        )),
    ));
    let vm = fixture::fake_values::vm();
    let arena = vm
        .arena(kiln_sfi::runtime::values::ArenaOptions::default())
        .unwrap();
    // the typed values: BYTES of keen::UInt32 open as a Lua integer, a loaded snapshot as a mapped struct
    let opened: Opened = Rc::new(RefCell::new(Vec::new()));
    let seen = opened.clone();
    let node = Rc::new(Cell::new(0x700u64));
    let exports: Rc<RefCell<std::collections::HashMap<u64, Vec<u8>>>> = Rc::default();
    let exports_in = exports.clone();
    fixture::fake_values::on("value_open", move |call| {
        node.set(node.get() + 1);
        seen.borrow_mut().push((
            call.origin,
            call.qualified_type.clone(),
            call.bytes.clone(),
            call.instance,
        ));
        match call.origin as i32 {
            KILN_VALUE_OPEN_BYTES
                if call.qualified_type == "keen::UInt32" && call.bytes.len() == 4 =>
            {
                let value = u32::from_le_bytes(call.bytes[..4].try_into().unwrap());
                let item = fixture::fake_values::lease(KILN_VALUE_INTEGER, node.get());
                let view =
                    fixture::fake_values::json(json!({"schema_version": 1, "packet": "view",
                    "value": {"tag": "i64", "value": format!("i64:{:016x}", value as u64)}}));
                fixture::fake_values::handled(1, item, view)
            }
            KILN_VALUE_OPEN_LOADED_SNAPSHOT => {
                let item = fixture::fake_values::lease(KILN_VALUE_STRUCT_MAPPED, node.get());
                fixture::fake_values::handled(1, item, 0)
            }
            // a Lua number as keen::UInt32 (what write_into encodes)
            KILN_VALUE_OPEN_LUA_INPUT if call.qualified_type == "keen::UInt32" => {
                let (code, described) =
                    fixture::fake_values::access(KILN_LUA_DESCRIBE, call.object, b"", 0);
                if code != KILN_OK {
                    return fixture::fake_values::Answer::Refuse(code);
                }
                let number = match described.kind as i32 {
                    KILN_LUA_INTEGER => described.bits as i64 as f64,
                    KILN_LUA_NUMBER => f64::from_bits(described.bits),
                    _ => return fixture::fake_values::Answer::Refuse(KILN_ERR_INVALID_ARGUMENT),
                };
                let item = fixture::fake_values::lease(KILN_VALUE_NUMBER, node.get());
                exports_in
                    .borrow_mut()
                    .insert(item, (number as u32).to_le_bytes().to_vec());
                fixture::fake_values::handled(1, item, 0)
            }
            _ => fixture::fake_values::Answer::Refuse(KILN_ERR_INVALID_ARGUMENT),
        }
    });
    let exports_out = exports.clone();
    fixture::fake_values::on("value_export", move |call| {
        match exports_out.borrow().get(&call.lease) {
            Some(bytes) => {
                fixture::fake_values::handled(1, 0, fixture::fake_values::packet(bytes.clone()))
            }
            None => fixture::fake_values::Answer::Refuse(KILN_ERR_INVALID_ARGUMENT),
        }
    });
    let wiring = types::wire(
        Port(if with_items {
            types_with_items()
        } else {
            archive_types()
        }),
        |_, _| {},
    );
    let session = Rc::new(Cell::new(FAKE_SESSION));
    let live = session.clone();
    let facts = Rc::new(kiln_sfi::runtime::RuntimeFacts::new(
        services.clone(),
        &kiln_sfi::runtime::operation_catalog::CATALOG,
        kiln_sfi::runtime::availability::FactsConfig {
            global_patch: false,
            global_export: false,
            game_build: BUILD.into(),
        },
    ));
    let assets = Rc::new(
        Assets::new(
            wiring.handle.session(vm.lua()).unwrap(),
            services,
            owner,
            Some(arena.values()),
            Rc::new(move || live.get()),
        )
        .with_facts(facts.clone()),
    );
    let host = script("alpha");
    host.facts.borrow_mut().resource_can_write = true;
    let game = vm.lua().create_table().unwrap();
    let dyn_host: Rc<dyn RuntimeHost> = host.clone();
    assets::install_into(vm.lua(), &game, "alpha", Some(dyn_host), assets.clone()).unwrap();
    vm.set("game", game);
    let plain = vm.lua().create_table().unwrap();
    assets::install_into(vm.lua(), &plain, "beta", None, assets.clone()).unwrap();
    vm.set("plain", plain);
    // runtime tables of two packages with the overrides (alpha's host is the one above)
    let beta = script("beta");
    beta.facts.borrow_mut().resource_can_write = true;
    for (id, h) in [("alpha", host.clone()), ("beta", beta.clone())] {
        let g = vm.lua().create_table().unwrap();
        let rt = vm.lua().create_table().unwrap();
        rt.raw_set("overrides", vm.lua().create_table().unwrap())
            .unwrap();
        let world = vm.lua().create_table().unwrap();
        let live = session.clone();
        world
            .raw_set(
                "session_id",
                vm.lua()
                    .create_function(move |_, ()| Ok(live.get()))
                    .unwrap(),
            )
            .unwrap();
        rt.raw_set("world", world).unwrap();
        let dyn_h: Rc<dyn RuntimeHost> = h.clone();
        assets::install_into(vm.lua(), &g, id, Some(dyn_h.clone()), assets.clone()).unwrap();
        assets::install_overrides(vm.lua(), &rt, &g, dyn_h, assets.clone()).unwrap();
        vm.set(&format!("g_{id}"), g);
        vm.set(&format!("rt_{id}"), rt);
    }
    vm.run::<()>(
        "function perr(f, ...)
           local ok, e = pcall(f, ...)
           assert(not ok, 'expected an error')
           return (tostring(e):match('^[^\\n]*'))
         end",
    )
    .unwrap();
    Vm {
        facts,
        beta,
        owner,
        vm,
        _arena: arena,
        host,
        session,
        opened,
        wiring,
        assets,
    }
}

impl Vm {
    fn eval<R: FromLuaMulti>(&self, code: &str) -> R {
        self.vm
            .run::<R>(code)
            .unwrap_or_else(|e| panic!("{code}: {e}"))
    }
    fn raised(&self, call: &str) -> String {
        self.eval(&format!("return perr(function() return {call} end)"))
    }
    fn hash(&self, name: &str) -> u32 {
        self.wiring
            .handle
            .registry()
            .unwrap()
            .by_qualified_name(name)
            .unwrap()
            .qualified_hash
    }
}

fn key(text: &str, type_hash: u32, part: u32) -> KilnAssetKey {
    KilnAssetKey {
        guid: parse_guid(text).unwrap(),
        r#type: type_hash,
        part,
    }
}

fn health_fake(hash: u32) -> LoadedFake {
    LoadedFake {
        resources: vec![
            (key(GUID, hash, 0), words(&[10, 50])),
            (
                key("00000001-0000-0000-0000-000000000002", hash, 1),
                words(&[7, 9]),
            ),
            (
                key("00000003-0000-0000-0000-000000000004", 0x1234, 0),
                words(&[1]),
            ),
        ],
        ..LoadedFake::default()
    }
}

/// A VM over Health resources (the type hash needs the VM's registry first).
fn health_vm() -> Vm {
    let v = vm(None, false);
    let hash = v.hash(HEALTH);
    fs::set_loaded(v.owner, Some(health_fake(hash)));
    v
}

#[test]
fn guid_text_is_the_games_byte_order_both_ways() {
    let guid = parse_guid(GUID).unwrap();
    // .NET's Guid.ToByteArray order: the first three groups little-endian
    assert_eq!(&guid[..4], &[0xd9, 0x2b, 0x62, 0x1d]);
    assert_eq!(&guid[4..8], &[0x70, 0x81, 0xb1, 0x41]);
    assert_eq!(
        &guid[8..],
        &[0x82, 0x2e, 0xd3, 0x98, 0x4b, 0x38, 0x6d, 0x95]
    );
    assert_eq!(guid_text(&guid), GUID);
    assert_eq!(
        parse_guid("1D622BD9-8170-41B1-822E-D3984B386D95"),
        Some(guid)
    );
    assert_eq!(parse_guid("1d622bd9-8170-41b1-822e-d3984b386d9"), None);
    assert_eq!(parse_guid("1d622bd9x8170-41b1-822e-d3984b386d95"), None);
    assert_eq!(parse_guid("1d622bd9-8170-41b1-822e-d3984b386dzz"), None);
}

#[test]
fn a_lookup_answers_one_confirmed_snapshot_as_a_loaded_resource() {
    let v = health_vm();
    let (guid, source, loaded, part, session, typed): (String, String, bool, u32, u64, bool) = v
        .eval(&format!(
            "local r = game.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         return r.guid, r.source, r.loaded, r.part, r.session_id, r.type ~= nil"
        ));
    assert_eq!(
        (guid.as_str(), source.as_str(), loaded, part, session, typed),
        (GUID, "loaded-runtime", true, 0, FAKE_SESSION, true)
    );
    assert_eq!(
        fs::counters(v.owner).calls("loaded_snapshot"),
        1,
        "one read, no size-only probe"
    );
    // a part is part of the key; a missing GUID or part is nil, never an error
    let found: bool = v.eval(&format!(
        "return game.assets.get_loaded_resource('00000001-0000-0000-0000-000000000002', '{HEALTH}', 1) ~= nil"
    ));
    assert!(found);
    let missing: bool = v.eval(&format!(
        "return game.assets.get_loaded_resource('00000001-0000-0000-0000-000000000002', '{HEALTH}') == nil
            and game.assets.get_loaded_resource('ffffffff-0000-0000-0000-000000000002', '{HEALTH}') == nil"
    ));
    assert!(missing);
    // `data` is the snapshot decoded once per object, as a loaded root of this instance and session
    let same: bool = v.eval(&format!(
        "local r = game.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         return r.data ~= nil and rawequal(r.data, r.data)"
    ));
    assert!(same);
    let opened = v.opened.borrow();
    let loaded: Vec<_> = opened
        .iter()
        .filter(|o| o.0 == KILN_VALUE_OPEN_LOADED_SNAPSHOT as u32)
        .collect();
    assert_eq!(loaded.len(), 1);
    assert_eq!(
        (loaded[0].1.as_str(), loaded[0].2.clone()),
        (HEALTH, words(&[10, 50]))
    );
    assert_ne!(loaded[0].3, 0, "the resource object's own instance");
    drop(opened);
    assert_eq!(v.raised(&format!("(function() local r = game.assets.get_loaded_resource('{GUID}', '{HEALTH}'); r.data = 1 end)()")), r::READ_ONLY);
}

#[test]
fn a_lookup_raises_the_bridges_texts() {
    let v = health_vm();
    let call = format!("game.assets.get_loaded_resource('{GUID}', '{HEALTH}')");
    fs::loaded(v.owner, |f| f.refuse = Some(KILN_LOADED_NOT_READY as u32));
    assert_eq!(v.raised(&call), t::RESOURCE_UNAVAILABLE);
    // a read that changed: its reason while a size query still finds the resource, nil once the lookup no longer finds
    // it (ShroudForge's resource_read, then resource_exists)
    fs::loaded(v.owner, |f| {
        f.refuse = None;
        f.torn_reads = 1;
    });
    assert_eq!(v.raised(&call), t::RESOURCE_CHANGED);
    fs::loaded(v.owner, |f| f.refuse = Some(KILN_LOADED_CHANGED as u32));
    let missing: bool = v.eval(&format!("return {call} == nil"));
    assert!(
        missing,
        "a lookup that no longer finds the resource answers nil"
    );
    fs::loaded(v.owner, |f| {
        f.refuse = None;
        f.growing = 3;
    });
    assert_eq!(v.raised(&call), t::RESOURCE_SIZE_CHANGING);
    // a buffer too small once is read again at the size the service answered
    fs::loaded(v.owner, |f| f.growing = 1);
    let found: bool = v.eval(&format!("return {call} ~= nil"));
    assert!(found);
    fs::loaded(v.owner, |f| f.reported = Some(256 * 1024 * 1024 + 1));
    assert_eq!(v.raised(&call), t::RESOURCE_LIMIT);
    fs::loaded(v.owner, |f| f.reported = None);
    // the arguments, as ShroudForge converts them
    assert_eq!(
        v.raised(&format!(
            "game.assets.get_loaded_resource('nope', '{HEALTH}')"
        )),
        "invalid guid string: nope"
    );
    assert!(
        v.raised(&format!("game.assets.get_loaded_resource(12, '{HEALTH}')"))
            .contains("Guid")
    );
    assert!(
        !v.raised(&format!(
            "game.assets.get_loaded_resource('{GUID}', 'keen::NoSuchType')"
        ))
        .is_empty()
    );
    // the world session moved between the lookup's two samples
    let session = v.session.clone();
    fs::on_loaded_call(Some(Box::new(move || session.set(session.get() + 1))));
    assert_eq!(v.raised(&call), r::SESSION_LOOKUP);
    fs::on_loaded_call(None);
}

#[test]
fn a_listing_answers_lazy_resources_bound_to_its_session() {
    let v = health_vm();
    let (count, first, part): (u32, String, u32) = v.eval(&format!(
        "local list = game.assets.get_loaded_resources_by_type('{HEALTH}')
         return #list, list[1].guid, list[2].part"
    ));
    assert_eq!((count, first.as_str(), part), (2, GUID, 1));
    assert_eq!(
        fs::counters(v.owner).calls("loaded_snapshot"),
        0,
        "a listing reads no snapshot"
    );
    // its snapshot is read on first use, in the listing's session
    let hp: i64 = v.eval(&format!(
        "local list = game.assets.get_loaded_resources_by_type('{HEALTH}')
         local value, why, ty = game.assets.read_loaded_field(list[1], 'hp')
         assert(why == nil and ty ~= nil, why)
         return value"
    ));
    assert_eq!(hp, 10);
    v.eval::<()>(&format!(
        "held = game.assets.get_loaded_resources_by_type('{HEALTH}')[1]"
    ));
    v.session.set(FAKE_SESSION + 1);
    assert_eq!(v.raised("held.data"), r::SESSION_BEFORE);
    v.session.set(FAKE_SESSION);
    let session = v.session.clone();
    fs::on_loaded_call(Some(Box::new(move || session.set(FAKE_SESSION + 2))));
    assert_eq!(v.raised("held.data"), r::SESSION_DURING);
    fs::on_loaded_call(None);
    v.session.set(FAKE_SESSION);
    // more keys than the first buffer, then the texts
    let hash = v.hash(HEALTH);
    fs::loaded(v.owner, |f| {
        for i in 0..300u32 {
            f.resources.push((
                key(&format!("{i:08x}-0000-0000-0000-00000000aaaa"), hash, 0),
                words(&[i, i]),
            ));
        }
    });
    let many: u32 = v.eval(&format!(
        "return #game.assets.get_loaded_resources_by_type('{HEALTH}')"
    ));
    assert_eq!(many, 302);
    let call = format!("game.assets.get_loaded_resources_by_type('{HEALTH}')");
    fs::loaded(v.owner, |f| f.refuse = Some(KILN_LOADED_NOT_READY as u32));
    assert_eq!(v.raised(&call), t::LISTING_UNAVAILABLE);
    fs::loaded(v.owner, |f| {
        f.refuse = None;
        f.growing = 3;
    });
    assert_eq!(v.raised(&call), t::LISTING_CHANGED);
    fs::loaded(v.owner, |f| {
        f.growing = 0;
        f.reported = Some((1 << 20) + 1);
    });
    assert_eq!(v.raised(&call), t::LISTING_LIMIT);
    fs::loaded(v.owner, |f| f.reported = None);
    let session = v.session.clone();
    fs::on_loaded_call(Some(Box::new(move || session.set(session.get() + 1))));
    assert_eq!(v.raised(&call), r::SESSION_LISTING);
    fs::on_loaded_call(None);
}

#[test]
fn read_loaded_field_answers_value_nil_type_or_nil_reason_nil() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = game.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    let (max, why, typed): (i64, Value, bool) =
        v.eval("local a, b, c = game.assets.read_loaded_field(res, 'max'); return a, b, c ~= nil");
    assert_eq!((max, why.is_nil(), typed), (50, true, true));
    let (value, why, ty): (Value, String, Value) =
        v.eval("return game.assets.read_loaded_field(res)");
    assert!(value.is_nil() && ty.is_nil());
    assert_eq!(why, r::FIELD_ARGS);
    let (value, why): (Value, String) = v.eval("return game.assets.read_loaded_field(res, 'nope')");
    assert!(value.is_nil() && !why.is_empty());
    let (value, why): (Value, String) = v.eval("return game.assets.read_loaded_field({}, 'hp')");
    assert!(value.is_nil() && !why.is_empty());
}

#[test]
fn resolve_loaded_resource_stops_at_the_missing_item_registry_resolver() {
    let v = vm(None, true);
    let reason = |code: &str| -> String {
        v.eval(&format!(
            "local r, why = {code}; assert(r == nil); return why"
        ))
    };
    assert_eq!(
        reason("game.assets.resolve_loaded_resource('g38_items::ItemRegistry', 42)"),
        r::NO_RESOLVER
    );
    assert_eq!(
        reason("game.assets.resolve_loaded_resource('g38_items::ItemRegistry')"),
        r::RESOLVE_ARGS
    );
    assert_eq!(
        reason("game.assets.resolve_loaded_resource('other::Registry', 1)"),
        "no validated resource relation for other::Registry"
    );
    // the read denial first: an inactive lifecycle, and a package without the runtime capability
    v.host.active.set(false);
    assert_eq!(
        reason("game.assets.resolve_loaded_resource('g38_items::ItemRegistry', 42)"),
        NOT_ACTIVE
    );
    v.host.active.set(true);
    assert_eq!(
        reason("plain.assets.resolve_loaded_resource('g38_items::ItemRegistry', 42)"),
        "mod 'beta' requires capabilities: [\"runtime\"]"
    );
    // an archive without ItemInfo
    let bare = vm(None, false);
    let why: String = bare.eval("local r, why = game.assets.resolve_loaded_resource('g38_items::ItemRegistry', 42); return why");
    assert_eq!(why, r::NO_ITEM_INFO);
}

#[test]
fn without_kiln_assets_loaded_members_the_lookups_raise_the_providers_text() {
    fs::set_shape("kiln.assets", Some(fs::Shape::Short(232)));
    let v = health_vm();
    assert_eq!(
        v.raised(&format!(
            "game.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
        )),
        NO_RESOURCE_READ
    );
    assert_eq!(
        v.raised(&format!(
            "game.assets.get_loaded_resources_by_type('{HEALTH}')"
        )),
        NO_RESOURCE_LISTING
    );
    fs::set_shape("kiln.assets", None);
}

#[test]
fn the_family_takes_its_names_from_the_unavailable_rows() {
    let wiring = types::wire(Port(archive_types()), |_, _| {});
    let left = unavailable::unclaimed(&[assets::family(wiring.handle.clone())]);
    for name in CLAIMS {
        assert!(
            left.iter().all(|row| row.name != name),
            "{name} still unclaimed"
        );
    }
    // the loaded index is the family's; the template override stays ShroudForge's refusal until it lands
    for name in [
        "game.assets.get_loaded_resource_by_field",
        "game.assets.prepare_loaded_index",
    ] {
        assert!(left.iter().all(|row| row.name != name), "{name}");
    }
    assert!(
        left.iter()
            .any(|row| row.name == "runtime.overrides.template")
    );
    let _ = res::ITEM_INFO;
}

#[test]
fn get_template_answers_the_entitys_loaded_template_resource() {
    let s = setup(EcsWorld::server().with(7, &[]));
    s.world(|w| w.entities.get_mut(&7).unwrap().template = Some([0x5a; 16]));
    let wiring = types::wire(Port(archive_types()), |_, _| {});
    let hash = wiring
        .handle
        .registry()
        .unwrap()
        .by_qualified_name(TEMPLATE)
        .unwrap()
        .qualified_hash;
    let live = s.session.clone();
    let assets = Rc::new(Assets::new(
        wiring.handle.session(s.vm.lua()).unwrap(),
        s.services.clone(),
        s.owner,
        Some(s.arena.values()),
        Rc::new(move || live.get()),
    ));
    let game = s.vm.lua().create_table().unwrap();
    let host: Rc<dyn RuntimeHost> = s.host.clone();
    assets::install_into(s.vm.lua(), &game, "alpha", Some(host), assets).unwrap();
    s.vm.set("game", game);
    let template = KilnAssetKey {
        guid: [0x5a; 16],
        r#type: hash,
        part: 0,
    };
    fs::set_loaded(
        s.owner,
        Some(LoadedFake {
            resources: vec![(template, words(&[1, 2]))],
            ..LoadedFake::default()
        }),
    );
    let h7 = s.handle(7);
    let call = format!("ecs.get_template({h7})");
    let (guid, source, session, part): (String, String, u64, u32) = s.eval(&format!(
        "local r, why = {call}; assert(r, why); return r.guid, r.source, r.session_id, r.part"
    ));
    assert_eq!(
        (guid, source.as_str(), session, part),
        (guid_text(&[0x5a; 16]), "loaded-runtime", FAKE_SESSION, 0)
    );
    assert_eq!(
        fs::counters(s.owner).calls("loaded_snapshot"),
        1,
        "a size query, the snapshot is read on first use"
    );
    // not loaded, an unavailable service
    fs::loaded(s.owner, |f| f.resources.clear());
    assert_eq!(s.reason(&call), tpl::NOT_LOADED);
    fs::loaded(s.owner, |f| f.refuse = Some(KILN_LOADED_NOT_READY as u32));
    assert_eq!(s.reason(&call), t::RESOURCE_UNAVAILABLE);
    fs::loaded(s.owner, |f| {
        f.refuse = None;
        f.resources.push((template, words(&[1, 2])));
    });
    // the entity's template, then the session, moved during the lookup
    let owner = s.owner;
    fs::on_loaded_call(Some(Box::new(move || {
        fs::ecs_world(owner, |w| {
            w.entities.get_mut(&7).unwrap().template = Some([0x6b; 16])
        });
    })));
    assert_eq!(s.reason(&call), tpl::CHANGED);
    fs::on_loaded_call(None);
    s.world(|w| w.entities.get_mut(&7).unwrap().template = Some([0x5a; 16]));
    let moved = s.session.clone();
    fs::on_loaded_call(Some(Box::new(move || moved.set(FAKE_SESSION + 1))));
    assert_eq!(s.reason(&call), tpl::SESSION);
    fs::on_loaded_call(None);
}

// ---- the writes ----

fn stored(v: &Vm, guid: &str) -> Vec<u8> {
    let hash = v.hash(HEALTH);
    fs::loaded(v.owner, |f| {
        f.resources
            .iter()
            .find(|(k, _)| k.guid == parse_guid(guid).unwrap() && k.r#type == hash)
            .unwrap()
            .1
            .clone()
    })
    .unwrap()
}

#[test]
fn write_loaded_field_compare_exchanges_one_scalar_and_answers_the_proven_resource() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    let (hp, why): (i64, Value) = v.eval(
        "local updated, why = g_alpha.assets.write_loaded_field(res, 'hp', 12)
         assert(updated, why)
         return g_alpha.assets.read_loaded_field(updated, 'hp'), why",
    );
    assert_eq!((hp, why.is_nil()), (12, true));
    assert_eq!(stored(&v, GUID), words(&[12, 50]));
    let writes = fs::loaded(v.owner, |f| f.writes.clone()).unwrap();
    assert_eq!(writes.len(), 1);
    assert_eq!(
        (writes[0].1, writes[0].2.clone(), writes[0].3),
        (0, 12u32.to_le_bytes().to_vec(), FAKE_SESSION)
    );
    // the old object's snapshot is no longer the current one: no blind assignment
    let reason = |code: &str| -> String {
        v.eval(&format!(
            "local r, why = {code}; assert(r == nil); return why"
        ))
    };
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 13)"),
        r::WRITE_SOURCE
    );
    // the observed value as the expected one: written; a wrong one: refused
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 13, 99)"),
        r::WRITE_EXPECTED
    );
    let ok: bool = v.eval("return g_alpha.assets.write_loaded_field(res, 'hp', 13, 12) ~= nil");
    assert!(ok);
    assert_eq!(stored(&v, GUID), words(&[13, 50]));
    // the native outcomes
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    fs::loaded(v.owner, |f| f.cas_answer = Some(KILN_ECS_CONFLICT as u32));
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        t::WRITE_CONFLICT
    );
    fs::loaded(v.owner, |f| f.cas_answer = Some(KILN_ECS_BUSY as u32));
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        t::WRITE_FAILED
    );
    // arguments, the session, the operations
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp')"),
        r::WRITE_ARGS
    );
    v.session.set(FAKE_SESSION + 1);
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        r::WRITE_SESSION
    );
    v.session.set(FAKE_SESSION);
    v.host.facts.borrow_mut().resource_can_write = false;
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        "runtime.assets.write is currently unavailable"
    );
    v.host.facts.borrow_mut().resource_can_write = true;
    v.host.active.set(false);
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        NOT_ACTIVE
    );
    v.host.active.set(true);
    assert_eq!(
        reason("plain.assets.write_loaded_field(res, 'hp', 1)"),
        "mod 'beta' requires capabilities: [\"runtime\"]"
    );
    assert_eq!(
        stored(&v, GUID),
        words(&[13, 50]),
        "no refusal wrote anything"
    );
}

#[test]
fn an_uncertain_write_stops_the_resource_and_latches_the_session() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    fs::loaded(v.owner, |f| f.cas_answer = Some(KILN_ECS_UNCERTAIN as u32));
    let why: String =
        v.eval("local r, why = g_alpha.assets.write_loaded_field(res, 'hp', 1); return why");
    let uncertain = "Resource write outcome is uncertain because lifetime or original data-page protection could not be verified. Do not retry blindly";
    assert_eq!(why, uncertain);
    assert_eq!(
        v.facts.write_faults().fault().map(|f| f.reason),
        Some(uncertain)
    );
    // the object's writes stop (no second native call)
    let calls = fs::counters(v.owner).calls("loaded_scalar_compare_exchange_named");
    assert_eq!(calls, 1);
    let again: String =
        v.eval("local r, why = g_alpha.assets.write_loaded_field(res, 'hp', 1, 10); return why");
    assert_eq!(again, uncertain);
    assert_eq!(
        fs::counters(v.owner).calls("loaded_scalar_compare_exchange_named"),
        calls
    );
}

// ---- sfi R3: a write names its package (kiln.assets' loaded_scalar_compare_exchange_named) ----

#[test]
fn a_write_names_its_package_and_field_for_kilns_registry_report() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    let ok: bool = v.eval("return g_alpha.assets.write_loaded_field(res, 'hp', 12) ~= nil");
    assert!(ok);
    assert_eq!(stored(&v, GUID), words(&[12, 50]));
    assert_eq!(
        fs::loaded(v.owner, |f| f.names.clone()).unwrap(),
        vec![("shroudforge:alpha".to_owned(), "hp".to_owned())]
    );
    assert_eq!(
        fs::counters(v.owner).calls("loaded_scalar_compare_exchange_named"),
        1
    );
    assert_eq!(
        fs::counters(v.owner).calls("loaded_scalar_compare_exchange"),
        0
    );
    // the resource override target writes through write_loaded_field: named the same way
    v.eval::<()>(&format!(
        "target = rt_alpha.overrides.resource(g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}'), 'hp')"
    ));
    let (ok, actual): (bool, i64) = v.eval("return target.write(target.read(), 20)");
    assert_eq!((ok, actual), (true, 20));
    let names = fs::loaded(v.owner, |f| f.names.clone()).unwrap();
    assert_eq!(names.len(), 2);
    assert_eq!(names[1], ("shroudforge:alpha".to_owned(), "hp".to_owned()));
}

#[test]
fn an_older_kiln_assets_without_the_named_member_still_writes_unnamed() {
    // a kiln.assets of 0.2.0-dev.31 ends at loaded_scalar_compare_exchange (256 bytes)
    fs::set_shape("kiln.assets", Some(fs::Shape::Short(256)));
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    let (ok, why): (bool, Value) = v.eval(
        "local updated, why = g_alpha.assets.write_loaded_field(res, 'hp', 12); return updated ~= nil, why",
    );
    fs::set_shape("kiln.assets", None);
    assert!(ok && why.is_nil(), "{why:?}");
    assert_eq!(stored(&v, GUID), words(&[12, 50]));
    assert_eq!(
        fs::loaded(v.owner, |f| f.names.clone()).unwrap(),
        vec![(String::new(), String::new())]
    );
    assert_eq!(
        fs::counters(v.owner).calls("loaded_scalar_compare_exchange"),
        1
    );
}

#[test]
fn a_write_name_is_text_kiln_assets_keeps_and_never_refuses_a_write() {
    use kiln_sfi::runtime::assets::{NAME_MAX, WriteName};
    assert_eq!(NAME_MAX, 127);
    let n = WriteName::package("alpha", "hp");
    assert_eq!((n.writer(), n.field()), ("shroudforge:alpha", "hp"));
    let id = "a".repeat(NAME_MAX - "shroudforge:".len());
    assert_eq!(WriteName::package(&id, "hp").writer().len(), NAME_MAX);
    // not a plain package id, or too long for kiln.assets: the write names nobody
    let longer = format!("{id}a");
    for bad in ["", "bad id", "caf\u{e9}", "a\u{0}b", longer.as_str()] {
        assert_eq!(WriteName::package(bad, "hp").writer(), "", "{bad:?}");
    }
    // the field: control characters as '?', cut on a character boundary
    assert_eq!(WriteName::package("alpha", "a\u{1}b\u{85}c").field(), "a?b?c");
    let long = WriteName::package("alpha", &"\u{e9}".repeat(100));
    assert_eq!(long.field().len(), 126);
    assert_eq!(WriteName::package("alpha", &"x".repeat(300)).field().len(), NAME_MAX);
}

#[test]
fn a_process_latched_compare_exchange_records_the_latch_without_a_session_fault() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    let reason = |code: &str| -> String {
        v.eval(&format!(
            "local r, why = {code}; assert(r == nil); return why"
        ))
    };
    let _cap = support::session_lock();
    fs::set_cap(
        assets::WRITE_CAPABILITY,
        Some((kiln_abi::services::kiln_caps::KILN_CAP_AVAILABLE, 0)),
    );
    // the member refused without a reply: nothing was written, so no proven resource
    fs::loaded(v.owner, |f| f.cas_refuse = Some(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        t::WRITE_FAILED
    );
    assert_eq!(stored(&v, GUID), words(&[10, 50]));
    let can_write_before_latch = v.facts.provider_facts().resource_can_write;
    // kiln.assets' writes latched by an earlier uncertain write (WHY_TARGET_LATCHED): this one attempted nothing, so it
    // fails as ShroudForge's latched provider does and latches neither the session nor the object
    fs::loaded(v.owner, |f| {
        f.cas_answer = Some(KILN_ECS_UNCERTAIN as u32);
        f.cas_why = KILN_ECS_WRITE_WHY_TARGET_LATCHED as u32;
    });
    assert_eq!(
        reason("g_alpha.assets.write_loaded_field(res, 'hp', 1)"),
        t::WRITE_FAILED
    );
    assert_eq!(v.facts.write_faults().fault(), None);
    let can_write = v.facts.provider_facts().resource_can_write;
    fs::set_cap(assets::WRITE_CAPABILITY, None);
    assert!(can_write_before_latch);
    assert!(!can_write);
    let ok: bool = v.eval("return g_alpha.assets.write_loaded_field(res, 'hp', 2) ~= nil");
    assert!(ok);
    assert_eq!(stored(&v, GUID), words(&[2, 50]));
}

#[test]
fn a_resource_claim_keeps_other_mods_off_and_the_target_never_restores_over_a_foreign_write() {
    let v = health_vm();
    v.eval::<()>(&format!(
        "res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')"
    ));
    // the claim: its arguments, then ownership of the resource for alpha
    let (claim, why): (Value, String) = v.eval("return rt_alpha.overrides.get_resource_claim()");
    assert!(claim.is_nil());
    assert_eq!(why, res::text::WRITE_ARGS.replace("write_loaded_field expects a loaded Resource, field path, value, and optional expected value", "get_resource_claim expects one current loaded Resource"));
    let (ok, why): (bool, Value) =
        v.eval("claim = rt_alpha.overrides.get_resource_claim(res); return claim:acquire()");
    assert!(ok && why.is_nil());
    let beta: String = v.eval(&format!(
        "local r = g_beta.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         local u, why = g_beta.assets.write_loaded_field(r, 'hp', 1); assert(u == nil); return why"
    ));
    assert!(beta.contains("owned by runtime mod 'alpha'"), "{beta}");
    let _ = &v.beta;
    // the target: key, read, canonical value, write
    v.eval::<()>("target = rt_alpha.overrides.resource(res, 'hp')");
    let key: String = v.eval("return target.key");
    assert_eq!(key, format!("resource/{GUID}/{HEALTH}/0/hp"));
    let shared: bool = v.eval("return target.shared");
    assert!(shared);
    let value: i64 = v.eval("snap = target.read(); return snap.value");
    assert_eq!(value, 10);
    let canonical: i64 = v.eval("return target.normalize(snap, 12)");
    assert_eq!(canonical, 12);
    let (ok, actual): (bool, i64) = v.eval("return target.write(snap, 12)");
    assert_eq!((ok, actual), (true, 12));
    assert_eq!(stored(&v, GUID), words(&[12, 50]));
    // the restore of the original value (what a stop does)
    let (ok, actual): (bool, i64) = v.eval("return target.write(target.read(), 10)");
    assert_eq!((ok, actual), (true, 10));
    assert_eq!(stored(&v, GUID), words(&[10, 50]));
    // another writer changes it between the read and the restore: refused, its value kept
    v.eval::<()>("snap = target.read()");
    let hash = v.hash(HEALTH);
    fs::loaded(v.owner, |f| {
        f.resources
            .iter_mut()
            .find(|(k, _)| k.r#type == hash && k.guid == parse_guid(GUID).unwrap())
            .unwrap()
            .1 = words(&[77, 50])
    });
    let (ok, _, why): (bool, Value, String) = v.eval("return target.write(snap, 30)");
    assert!(!ok);
    assert_eq!(why, r::WRITE_EXPECTED);
    assert_eq!(stored(&v, GUID), words(&[77, 50]));
    // another session: neither read nor write
    v.session.set(FAKE_SESSION + 1);
    let (snap, why): (Value, String) = v.eval("return target.read()");
    assert!(snap.is_nil());
    assert_eq!(why, "Resource target belongs to another world session");
    let (ok, _, why): (bool, Value, String) = v.eval("return target.write(snap, 30)");
    assert!(!ok);
    assert_eq!(why, "Resource target belongs to another world session");
    let (ok, why): (bool, String) = v.eval("return claim:acquire()");
    assert!(!ok && !why.is_empty());
}

#[test]
fn the_catalog_backs_assets_write_and_resource_can_write_on_the_loaded_writes() {
    use kiln_sfi::runtime::operation_catalog::{Backing, CATALOG, Fact};
    use sfi_core::bindings::runtime::RuntimeOperation as Op;
    let op = CATALOG
        .operations
        .iter()
        .find(|row| row.operation == Op::AssetsWrite)
        .unwrap();
    assert!(matches!(op.backing, Backing::Kiln(_)));
    let fact = CATALOG
        .facts
        .iter()
        .find(|row| row.fact == Fact::ResourceCanWrite)
        .unwrap();
    assert!(matches!(fact.backing, Backing::Kiln(_)));
    let v = health_vm();
    let _ = &v;
    fs::set_shape("kiln.assets", Some(fs::Shape::Short(248)));
    let again = health_vm();
    let why: String = again.eval(&format!(
        "local r = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         local u, why = g_alpha.assets.write_loaded_field(r, 'hp', 1); return why"
    ));
    assert_eq!(why, t::NO_WRITE);
    fs::set_shape("kiln.assets", None);
}

// ---- the loaded-resource field index (prepare_loaded_index, get_loaded_resource_by_field) ----
// ShroudForge 1.7.9 d8a394bc: modules/mod-runtime/lua/ingame/resources/index_loaded_resources.rs,
// session/loaded_resource_indexes.rs, game-runtime/bridge/windows/resources/read_resource_scalars.rs.

use kiln_sfi::runtime::assets::by_field::text as bf;
use kiln_sfi::runtime::assets::index::{self as idx, text as it};

/// A health VM with `extra` more Health resources, hp 1000 + i (unique), and two that share hp 5555.
fn index_vm(extra: u32) -> Vm {
    let v = health_vm();
    let hash = v.hash(HEALTH);
    fs::loaded(v.owner, |f| {
        for i in 0..extra {
            f.resources.push((
                key(&format!("{i:08x}-0000-0000-0000-00000000bbbb"), hash, 0),
                words(&[1000 + i, 1]),
            ));
        }
        f.resources.push((
            key("0000aaaa-0000-0000-0000-000000000001", hash, 0),
            words(&[5555, 1]),
        ));
        f.resources.push((
            key("0000aaaa-0000-0000-0000-000000000002", hash, 0),
            words(&[5555, 2]),
        ));
    });
    v
}

/// What `prepare_loaded_index` answered: ready, reason, progress (scanned, total, complete, phase, session) or nil.
type Prepared = (bool, Option<String>, Option<(u64, u64, bool, String, u64)>);

impl Vm {
    /// `prepare_loaded_index(HEALTH, path)` through `ns`: ready, reason, progress (scanned, total, complete, phase,
    /// session) or nil.
    fn prepare(&self, ns: &str, path: &str) -> Prepared {
        let out: (bool, Option<String>, Option<Vec<Value>>) = self.eval(&format!(
            "local ready, why, p = {ns}.assets.prepare_loaded_index('{HEALTH}', '{path}')
             if p == nil then return ready, why, nil end
             return ready, why, {{p.scanned, p.total, p.complete, p.phase, p.session_id}}"
        ));
        out.map_progress()
    }
    /// Runner rounds until the jobs are complete (at most `limit`); the rounds it took.
    fn drain(&self, limit: usize) -> usize {
        for round in 1..=limit {
            kiln_sfi::runtime::assets::by_field::tick(&self.assets);
            if self.prepare("g_alpha", "hp").0 {
                return round;
            }
        }
        panic!("the index did not complete in {limit} rounds");
    }
}

trait MapProgress {
    fn map_progress(self) -> Prepared;
}
impl MapProgress for (bool, Option<String>, Option<Vec<Value>>) {
    fn map_progress(self) -> Prepared {
        let progress = self.2.map(|p| {
            let int = |v: &Value| v.as_integer().unwrap() as u64;
            (
                int(&p[0]),
                int(&p[1]),
                p[2].as_boolean().unwrap(),
                p[3].to_string().unwrap(),
                int(&p[4]),
            )
        });
        (self.0, self.1, progress)
    }
}

/// `get_loaded_resource_by_field(HEALTH, 'hp', value)`: the GUID or nil, the reason, whether a progress came back and
/// its payload_reads.
fn by_field(v: &Vm, value: &str) -> (Option<String>, Option<String>, Option<(bool, i64)>) {
    let (guid, why, p): (Option<String>, Option<String>, Option<Vec<Value>>) = v.eval(&format!(
        "local r, why, p = g_alpha.assets.get_loaded_resource_by_field('{HEALTH}', 'hp', {value})
         return r and r.guid, why, p and {{p.complete, p.payload_reads}}"
    ));
    (
        guid,
        why,
        p.map(|p| (p[0].as_boolean().unwrap(), p[1].as_integer().unwrap())),
    )
}

#[test]
fn an_index_builds_over_several_runner_rounds_and_answers_hits_misses_and_ambiguity() {
    let v = index_vm(1100);
    // a declaration queues the job; lookups register and inspect, they never advance discovery
    let (ready, why, progress) = v.prepare("g_alpha", "hp");
    assert!(!ready);
    assert_eq!(why.as_deref(), Some(bf::QUEUED));
    assert_eq!(
        progress,
        Some((0, 0, false, "indexing".into(), FAKE_SESSION))
    );
    let (guid, why, progress) = by_field(&v, "10");
    assert_eq!(
        (guid, why.as_deref(), progress),
        (None, Some(it::scanning(0, 0).as_str()), Some((false, 0)))
    );
    assert_eq!(
        fs::counters(v.owner).calls("loaded_list"),
        0,
        "a lookup lists nothing"
    );
    assert_eq!(fs::counters(v.owner).calls("loaded_snapshot"), 0);
    // one round reads at most MAX_ROUND_KEYS scalars: 1104 resources take more than one round
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    let (ready, _, progress) = v.prepare("g_alpha", "hp");
    let (scanned, total, ..) = progress.unwrap();
    assert!(
        !ready && total == 1104 && scanned <= idx::MAX_ROUND_KEYS as u64,
        "{scanned}/{total}"
    );
    let rounds = 1 + v.drain(64);
    assert!(rounds >= 2, "{rounds}");
    // one listing per job (two calls: the first 256-key buffer, then the size the service answered)
    assert_eq!(
        fs::counters(v.owner).calls("loaded_list"),
        2,
        "one listing per job"
    );
    // no batch member: ShroudForge's fallback reads each resource once in full
    assert_eq!(fs::counters(v.owner).calls("loaded_snapshot"), 1104);
    let (ready, why, progress) = v.prepare("g_alpha", "hp");
    assert_eq!(
        (ready, why, progress),
        (
            true,
            None,
            Some((1104, 1104, true, "complete".into(), FAKE_SESSION))
        )
    );
    // a hit is read again in full, checked, and answered as a loaded resource of the lookup's session
    let (guid, why, progress) = by_field(&v, "10");
    assert_eq!(
        (guid.as_deref(), why, progress),
        (Some(GUID), None, Some((true, 1)))
    );
    assert_eq!(fs::counters(v.owner).calls("loaded_snapshot"), 1105);
    let (session, hp, max): (u64, i64, i64) = v.eval(&format!(
        "local r = g_alpha.assets.get_loaded_resource_by_field('{HEALTH}', 'hp', 1500)
         return r.session_id, g_alpha.assets.read_loaded_field(r, 'hp'), (g_alpha.assets.read_loaded_field(r, 'max'))"
    ));
    assert_eq!((session, hp, max), (FAKE_SESSION, 1500, 1));
    // a miss and a shared scalar
    assert_eq!(
        by_field(&v, "424242"),
        (None, Some(it::NOT_FOUND.into()), Some((true, 0)))
    );
    assert_eq!(
        by_field(&v, "5555"),
        (None, Some(it::AMBIGUOUS.into()), Some((true, 0)))
    );
    // the job is shared: another package's declaration joins it
    let (ready, ..) = v.prepare("g_beta", "hp");
    assert!(ready);
    assert_eq!(v.assets.indexes().len(), 1);
    // a second field is its own job
    assert!(!v.prepare("g_alpha", "max").0);
    assert_eq!(v.assets.indexes().len(), 2);
}

#[test]
fn a_resource_change_requeues_the_index() {
    let v = index_vm(10);
    v.prepare("g_alpha", "hp");
    v.drain(16);
    // a write to another field of the type leaves the hp index complete
    v.eval::<()>(&format!(
        "local res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         assert(g_alpha.assets.write_loaded_field(res, 'max', 51))"
    ));
    assert!(
        v.prepare("g_alpha", "hp").0,
        "a write outside the field keeps the index"
    );
    // a write to the field requeues it; the next rounds find the new value
    v.eval::<()>(&format!(
        "local res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')
         assert(g_alpha.assets.write_loaded_field(res, 'hp', 11))"
    ));
    let (ready, why, progress) = v.prepare("g_alpha", "hp");
    assert!(!ready);
    assert_eq!(why.as_deref(), Some(bf::QUEUED));
    assert_eq!(progress.unwrap().0, 0);
    v.drain(16);
    assert_eq!(by_field(&v, "11").0.as_deref(), Some(GUID));
    assert_eq!(by_field(&v, "10").1.as_deref(), Some(it::NOT_FOUND));
    // a candidate changed behind the index: refused, the job discarded and retried after its delay
    let hash = v.hash(HEALTH);
    fs::loaded(v.owner, |f| {
        let at = f
            .resources
            .iter()
            .position(|(k, _)| k.r#type == hash && guid_text(&k.guid) == GUID)
            .unwrap();
        f.resources[at].1 = words(&[12, 51]);
    });
    assert_eq!(
        by_field(&v, "11"),
        (None, Some(it::CANDIDATE_CHANGED.into()), None)
    );
    let (ready, why, _) = v.prepare("g_alpha", "hp");
    assert_eq!(
        (ready, why.as_deref()),
        (false, Some(it::CANDIDATE_CHANGED))
    );
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    assert!(!v.prepare("g_alpha", "hp").0, "no retry before RETRY_DELAY");
    std::thread::sleep(idx::RETRY_DELAY);
    v.drain(16);
    assert_eq!(by_field(&v, "12").0.as_deref(), Some(GUID));
}

#[test]
fn only_a_committed_or_an_uncertain_write_requeues_the_indexes() {
    let v = index_vm(10);
    v.prepare("g_alpha", "hp");
    v.drain(16);
    assert!(!v.prepare("g_alpha", "max").0);
    for _ in 0..16 {
        kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    }
    assert!(v.prepare("g_alpha", "max").0);
    let write = |value: u32| -> Option<String> {
        v.eval(&format!(
            "local res = g_alpha.assets.get_loaded_resource('{GUID}', '{HEALTH}')
             local _, why = g_alpha.assets.write_loaded_field(res, 'hp', {value})
             return why"
        ))
    };
    // ShroudForge requeues on a written scalar only: a refusal of the member (nothing written), a write kiln.assets'
    // latch refused and a conflict leave the completed index as it is
    fs::loaded(v.owner, |f| f.cas_refuse = Some(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(write(11).as_deref(), Some(t::WRITE_FAILED));
    assert!(
        v.prepare("g_alpha", "hp").0,
        "a refused write keeps the index"
    );
    fs::loaded(v.owner, |f| {
        f.cas_answer = Some(KILN_ECS_UNCERTAIN as u32);
        f.cas_why = KILN_ECS_WRITE_WHY_TARGET_LATCHED as u32;
    });
    assert_eq!(write(11).as_deref(), Some(t::WRITE_FAILED));
    assert!(
        v.prepare("g_alpha", "hp").0 && v.prepare("g_alpha", "max").0,
        "a latched refusal keeps every index"
    );
    fs::loaded(v.owner, |f| f.cas_answer = Some(KILN_ECS_CONFLICT as u32));
    assert_eq!(write(11).as_deref(), Some(t::WRITE_CONFLICT));
    assert!(v.prepare("g_alpha", "hp").0, "a conflict keeps the index");
    // an uncertain outcome requeues every index, the other field's too
    fs::loaded(v.owner, |f| f.cas_answer = Some(KILN_ECS_UNCERTAIN as u32));
    assert!(write(11).unwrap().contains("uncertain"));
    assert!(!v.prepare("g_alpha", "hp").0 && !v.prepare("g_alpha", "max").0);
}

#[test]
fn a_package_whose_lifecycle_stops_leaves_its_index_jobs() {
    let v = index_vm(10);
    v.prepare("g_alpha", "hp");
    v.prepare("g_beta", "hp");
    v.prepare("g_alpha", "max");
    assert_eq!(v.assets.indexes().len(), 2);
    // ShroudForge's set_runtime_mod_active(false) removes the owner: a job nobody else declared goes, a shared one stays
    v.host.active.set(false);
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    assert_eq!(
        v.assets.indexes().len(),
        1,
        "alpha's max job goes, beta keeps hp"
    );
    v.beta.active.set(false);
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    assert_eq!(v.assets.indexes().len(), 0);
    // active again (a restore): its next declaration is a new job
    v.host.active.set(true);
    assert!(!v.prepare("g_alpha", "hp").0);
    assert_eq!(v.assets.indexes().len(), 1);
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    assert_eq!(v.assets.indexes().len(), 1, "an active owner keeps its job");
}

#[test]
fn a_session_change_resets_every_job_and_keeps_the_declarations() {
    let v = index_vm(10);
    v.prepare("g_alpha", "hp");
    v.drain(16);
    v.session.set(FAKE_SESSION + 1);
    let (ready, _, progress) = v.prepare("g_alpha", "hp");
    assert_eq!(
        (ready, progress),
        (
            false,
            Some((0, 0, false, "indexing".into(), FAKE_SESSION + 1))
        )
    );
    v.drain(16);
    let session: u64 = v.eval(&format!(
        "return g_alpha.assets.get_loaded_resource_by_field('{HEALTH}', 'hp', 10).session_id"
    ));
    assert_eq!(session, FAKE_SESSION + 1);
    assert_eq!(fs::counters(v.owner).calls("loaded_list"), 2);
    // the session moving during a lookup's confirming read: ShroudForge's text, the job reset to the new session
    let session = v.session.clone();
    fs::on_loaded_call(Some(Box::new(move || session.set(FAKE_SESSION + 2))));
    assert_eq!(by_field(&v, "10"), (None, Some(bf::SESSION.into()), None));
    fs::on_loaded_call(None);
    let (ready, _, progress) = v.prepare("g_alpha", "hp");
    assert_eq!((ready, progress.unwrap().4), (false, FAKE_SESSION + 2));
    // the session moving during a round: the round's job fails with the text and is retried later
    let session = v.session.clone();
    fs::on_loaded_call(Some(Box::new(move || session.set(FAKE_SESSION + 3))));
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    fs::on_loaded_call(None);
    let (ready, why, progress) = v.prepare("g_alpha", "hp");
    assert!(!ready);
    assert_eq!(progress.unwrap().4, FAKE_SESSION + 3);
    assert_eq!(
        why.as_deref(),
        Some(bf::QUEUED),
        "the new session's reset clears the failure"
    );
}

#[test]
fn the_members_check_their_arguments_limits_and_the_read_denial() {
    let v = index_vm(0);
    let prepare = |code: &str| -> (bool, String, bool) {
        v.eval(&format!(
            "local ready, why, p = {code}; return ready, why, p == nil"
        ))
    };
    let get = |code: &str| -> (bool, String, bool) {
        v.eval(&format!(
            "local r, why, p = {code}; return r == nil, why, p == nil"
        ))
    };
    assert_eq!(
        prepare(&format!("game.assets.prepare_loaded_index('{HEALTH}')")),
        (false, bf::PREPARE_ARGS.into(), true)
    );
    assert_eq!(
        get(&format!(
            "game.assets.get_loaded_resource_by_field('{HEALTH}', 'hp')"
        )),
        (true, bf::GET_ARGS.into(), true)
    );
    assert_eq!(
        prepare(&format!("game.assets.prepare_loaded_index('{HEALTH}', '')")),
        (false, bf::PATH.into(), true)
    );
    let long = "a".repeat(1025);
    assert_eq!(
        prepare(&format!(
            "game.assets.prepare_loaded_index('{HEALTH}', '{long}')"
        )),
        (false, bf::PATH.into(), true)
    );
    let (ready, why, none) = prepare("game.assets.prepare_loaded_index('keen::NoSuchType', 'hp')");
    assert!(!ready && !why.is_empty() && none);
    let (ready, why, none) = prepare(&format!(
        "game.assets.prepare_loaded_index('{HEALTH}', 'nope')"
    ));
    assert!(!ready && !why.is_empty() && none);
    let (missing, why, none) = get(&format!(
        "game.assets.get_loaded_resource_by_field('{HEALTH}', 'hp', 'text')"
    ));
    assert!(
        missing && !why.is_empty() && none,
        "a value the field's type refuses"
    );
    // the read denial first: a package without the runtime capability, an inactive lifecycle
    assert_eq!(
        prepare(&format!(
            "plain.assets.prepare_loaded_index('{HEALTH}', 'hp')"
        )),
        (
            false,
            "mod 'beta' requires capabilities: [\"runtime\"]".into(),
            true
        )
    );
    v.host.active.set(false);
    assert_eq!(
        get(&format!(
            "game.assets.get_loaded_resource_by_field('{HEALTH}', 'hp', 10)"
        )),
        (true, NOT_ACTIVE.into(), true)
    );
    v.host.active.set(true);
    assert_eq!(
        v.assets.indexes().len(),
        0,
        "a refused call declares nothing"
    );
}

#[test]
fn without_kiln_assets_loaded_members_a_job_keeps_the_providers_text() {
    let v = index_vm(0);
    fs::set_shape("kiln.assets", Some(fs::Shape::Short(232)));
    v.prepare("g_alpha", "hp");
    kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    let (ready, why, _) = v.prepare("g_alpha", "hp");
    assert_eq!((ready, why.as_deref()), (false, Some(NO_RESOURCE_LISTING)));
    assert_eq!(by_field(&v, "10").1.as_deref(), Some(NO_RESOURCE_LISTING));
    fs::set_shape("kiln.assets", None);
}

#[test]
fn the_index_runner_is_a_hook_only_family_that_drives_live_vms() {
    let wiring = types::wire(Port(archive_types()), |_, _| {});
    let families = kiln_sfi::runtime::Families::new(vec![
        assets::family(wiring.handle.clone()),
        assets::index_family(),
    ])
    .unwrap();
    assert_eq!(families.hooks().len(), 1);
    assert!(assets::index_family().claims().is_empty());
    // a driven VM's jobs advance on the runner's rounds; a closed VM's are let go
    let v = index_vm(10);
    assets::drive(&v.assets);
    v.prepare("g_alpha", "hp");
    for _ in 0..16 {
        assets::advance_indexes();
    }
    assert!(v.prepare("g_alpha", "hp").0);
    let weak = Rc::downgrade(&v.assets);
    drop(v);
    assets::advance_indexes();
    assert!(weak.upgrade().is_none());
}

#[test]
fn an_item_info_index_answers_unlimited_flights_lookup_through_the_nested_item_id() {
    let v = vm(None, true);
    let hash = v.hash(ITEM_INFO);
    let items = (0..40u32)
        .map(|i| {
            (
                key(&format!("{i:08x}-0000-0000-0000-00000000cccc"), hash, 0),
                words(&[7000 + i, 0, i, i, i, i]),
            )
        })
        .collect();
    fs::set_loaded(
        v.owner,
        Some(LoadedFake {
            resources: items,
            ..LoadedFake::default()
        }),
    );
    // on_load's declaration, then the mod's lookup shape while it scans and once it completes
    v.eval::<()>(&format!(
        "game.assets.prepare_loaded_index('{ITEM_INFO}', 'itemId.value')"
    ));
    let lookup = format!(
        "local ok, resource, reason, progress = pcall(game.assets.get_loaded_resource_by_field, '{ITEM_INFO}', 'itemId.value', 7021)
         assert(ok, resource)
         if not resource then return nil, reason or (progress.phase .. ' ' .. progress.scanned .. '/' .. progress.total) end
         return resource.guid, nil"
    );
    let (guid, why): (Option<String>, Option<String>) = v.eval(&lookup);
    assert_eq!(
        (guid, why.as_deref()),
        (None, Some(it::scanning(0, 0).as_str()))
    );
    for _ in 0..16 {
        kiln_sfi::runtime::assets::by_field::tick(&v.assets);
    }
    let (guid, why): (Option<String>, Option<String>) = v.eval(&lookup);
    assert_eq!(
        (guid.as_deref(), why),
        (Some("00000015-0000-0000-0000-00000000cccc"), None)
    );
    // the struct itself is not a 32-bit scalar
    let (ready, why): (bool, String) = v.eval(&format!(
        "return game.assets.prepare_loaded_index('{ITEM_INFO}', 'objectId')"
    ));
    assert!(!ready && why == bf::ALIGNED, "{why}");
}
