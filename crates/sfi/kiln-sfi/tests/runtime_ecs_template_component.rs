// SPDX-License-Identifier: MIT
// the interpreter's runtime.ecs.get_template_component tests.
//! `runtime.ecs.get_template_component` and its view's `read_fields` and `write_field` on kiln.world's ECS fake (the
//! entity's template uuid, `ecs_registration`'s rows: support/fake_services `RegistrationFake`) and kiln.assets'
//! loaded-resource fake (the template's snapshot and the scalar compare-exchange), through the real service adapter,
//! the archive registry (the fixture's TemplateResource with a BlobArray of BlobVariants over keen::tpl::Component)
//! and the VM's typed values. Expected texts, shapes and order are ShroudForge's (MIT, Copyright (c) 2026
//! bonsaibauer), 1.7.9 d8a394bc: modules/mod-runtime/lua/ingame/ecs/access_template_component.rs.
#[path = "shared/ecs_fixture.rs"]
mod fixture;

use fixture::fake_services::{self as fs, LoadedFake, RegistrationFake, template_row};
use fixture::*;
use kiln_abi::services::kiln_assets::KilnAssetKey;
use kiln_sfi::runtime::assets::resource::guid_text;
use kiln_sfi::runtime::assets::{self, Assets, text as t};
use kiln_sfi::runtime::ecs::registry::text as reg;
use kiln_sfi::runtime::ecs::template_component::text as tc;
use kiln_sfi::runtime::overrides::targets::template::{self as target, text as tt};
use kiln_sfi::runtime::types;
use sfi_core::bindings::runtime::RuntimeHost;

const UUID: [u8; 16] = [0x5a; 16];

/// A TemplateResource snapshot: its components array at 0 (offset 8, count), the variants' descriptors, then each
/// variant's value (8 bytes: two f32).
fn template(variants: &[(&str, [f32; 2])]) -> Vec<u8> {
    let n = variants.len();
    let values = 8 + n * 12;
    let mut bytes = vec![0u8; values + n * 8];
    bytes[0..4].copy_from_slice(&8u32.to_le_bytes());
    bytes[4..8].copy_from_slice(&(n as u32).to_le_bytes());
    for (i, (name, value)) in variants.iter().enumerate() {
        let d = 8 + i * 12;
        let at = values + i * 8;
        bytes[d..d + 4].copy_from_slice(&fnv(name).to_le_bytes());
        bytes[d + 4..d + 8].copy_from_slice(&((at - d - 4) as u32).to_le_bytes());
        bytes[d + 8..d + 12].copy_from_slice(&8u32.to_le_bytes());
        bytes[at..at + 4].copy_from_slice(&value[0].to_le_bytes());
        bytes[at + 4..at + 8].copy_from_slice(&value[1].to_le_bytes());
    }
    bytes
}

struct T {
    s: Setup,
    key: KilnAssetKey,
    call: String,
    _wiring: types::TypesWiring,
}

fn setup_template(bytes: Vec<u8>) -> T {
    let s = setup(EcsWorld::server().with(7, &[]));
    s.world(|w| w.entities.get_mut(&7).unwrap().template = Some(UUID));
    s.facts(|f| f.resource_can_write = true);
    let wiring = types::wire(Port(archive_types()), |_, _| {});
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
    let key = KilnAssetKey {
        guid: UUID,
        r#type: fnv(TEMPLATE),
        part: 0,
    };
    fs::set_loaded(
        s.owner,
        Some(LoadedFake {
            resources: vec![(key, bytes)],
            ..LoadedFake::default()
        }),
    );
    fs::set_registrations(
        s.owner,
        Some(RegistrationFake {
            rows: vec![template_row(210, FALL_DAMAGE, 8, (0, 0))],
            ..RegistrationFake::default()
        }),
    );
    let call = format!(
        "ecs.get_template_component({}, '{FALL_DAMAGE}')",
        s.handle(7)
    );
    T {
        s,
        key,
        call,
        _wiring: wiring,
    }
}

fn stored(t: &T) -> Vec<u8> {
    fs::loaded(t.s.owner, |f| {
        f.resources
            .iter()
            .find(|(k, _)| k.guid == UUID)
            .unwrap()
            .1
            .clone()
    })
    .unwrap()
}

#[test]
fn a_view_of_the_entitys_template_component_reads_its_fields() {
    let t = setup_template(template(&[
        ("keen::tpl::Other", [1.0, 2.0]),
        (FALL_DAMAGE, [8.0, 16.0]),
    ]));
    let (key, scope, session, start, lethal): (String, String, u64, f64, f64) = t.s.eval(&format!(
        "view = assert({}); local f = assert(view:read_fields({{'start', 'lethal'}}));
         return view.key, view.scope, view.session_id, f.start, f.lethal",
        t.call
    ));
    assert_eq!(
        key,
        format!("{}/{TEMPLATE}/0/{FALL_DAMAGE}", guid_text(&UUID))
    );
    assert_eq!(
        (scope.as_str(), session, start, lethal),
        ("shared-template", FAKE_SESSION, 8.0, 16.0)
    );
    assert!(
        t.s.eval::<bool>("return view.type ~= nil"),
        "the view names its component type"
    );
    // read_fields' own refusals, in ShroudForge's order and words
    assert_eq!(t.s.reason("view:read_fields()"), tc::READ_ARGS);
    assert_eq!(t.s.reason("view:read_fields('start')"), tc::READ_TABLE);
    assert_eq!(t.s.reason("view:read_fields({})"), tc::NONEMPTY);
    assert_eq!(t.s.reason("view:read_fields({x = 'start'})"), tc::DENSE);
    assert_eq!(t.s.reason("view:read_fields({1})"), tc::PATH_STRINGS);
    assert_eq!(
        t.s.reason("view:read_fields({'start', 'start'})"),
        tc::DUPLICATE
    );
    assert_eq!(
        t.s.reason("view:read_fields({'nope'})"),
        format!("nope: reflected field not found: {FALL_DAMAGE}.nope")
    );
    // the view reads the template again each time, and holds to its session
    fs::loaded(t.s.owner, |f| {
        f.resources[0].1 = template(&[(FALL_DAMAGE, [3.0, 4.0])])
    });
    assert_eq!(
        t.s.eval::<f64>("return view:read_fields({'start'}).start"),
        3.0
    );
    t.s.session.set(FAKE_SESSION + 1);
    assert_eq!(t.s.reason("view:read_fields({'start'})"), tc::SESSION);
}

#[test]
fn get_template_component_refuses_in_shroudforges_order() {
    let t = setup_template(template(&[(FALL_DAMAGE, [8.0, 16.0])]));
    let h7 = t.s.handle(7);
    assert_eq!(
        t.s.reason(&format!("ecs.get_template_component({h7})")),
        tc::ARGS
    );
    t.s.session.set(0);
    assert_eq!(t.s.reason(&t.call), tc::SESSION);
    t.s.session.set(FAKE_SESSION);
    // the registration comes before the entity's template: an entity without one still hears about the registration
    t.s.world(|w| w.entities.get_mut(&7).unwrap().template = None);
    fs::registrations(t.s.owner, |f| f.rows.clear());
    assert_eq!(
        t.s.reason(&t.call),
        format!("no live template registration for {FALL_DAMAGE}")
    );
    fs::registrations(t.s.owner, |f| {
        f.rows.push(template_row(210, FALL_DAMAGE, 8, (0, 0)))
    });
    assert_eq!(t.s.reason(&t.call), tc::NO_UUID);
    t.s.world(|w| w.entities.get_mut(&7).unwrap().template = Some(UUID));
    // the registration's report and its template role
    fs::registrations(t.s.owner, |f| f.refuse = Some(KILN_ERR_UNAVAILABLE));
    assert_eq!(t.s.reason(&t.call), tc::REGISTRATION_UNAVAILABLE);
    fs::registrations(t.s.owner, |f| {
        f.refuse = None;
        f.unreadable = 4;
    });
    assert_eq!(t.s.reason(&t.call), reg::CHANGED);
    fs::registrations(t.s.owner, |f| f.unreadable = 3);
    assert!(
        t.s.eval::<bool>(&format!("return ({}) ~= nil", t.call)),
        "a catalog that moved three times is read again"
    );
    fs::registrations(t.s.owner, |f| f.rows[0].template_hash = 0);
    assert_eq!(t.s.reason(&t.call), tc::NO_LAYOUT);
    fs::registrations(t.s.owner, |f| {
        f.rows[0] = template_row(210, FALL_DAMAGE, 12, (0, 0))
    });
    assert_eq!(t.s.reason(&t.call), tc::LAYOUT_DISAGREES);
    fs::registrations(t.s.owner, |f| {
        f.rows[0] = template_row(70_000, FALL_DAMAGE, 8, (0, 0))
    });
    assert_eq!(t.s.reason(&t.call), tc::OWNER_DISAGREES);
    fs::registrations(t.s.owner, |f| {
        f.rows[0] = template_row(210, FALL_DAMAGE, 8, (fnv("keen::tpl::Runtime"), 4))
    });
    assert!(
        t.s.eval::<bool>(&format!("return ({}) ~= nil", t.call)),
        "a storage type of its own is no matter"
    );
    // a storage-only registration is no template role
    fs::registrations(t.s.owner, |f| {
        f.rows[0].template_hash = 0;
        f.rows[0].template_size = 0;
    });
    assert_eq!(t.s.reason(&t.call), tc::NO_LAYOUT);
    fs::registrations(t.s.owner, |f| {
        f.rows[0] = template_row(210, FALL_DAMAGE, 8, (0, 0))
    });
    // the template's variants
    let set = |bytes: Vec<u8>| fs::loaded(t.s.owner, |f| f.resources[0].1 = bytes);
    set(template(&[("keen::tpl::Other", [1.0, 2.0])]));
    assert_eq!(
        t.s.reason(&t.call),
        format!("template has no {FALL_DAMAGE} configuration")
    );
    set(template(&[
        (FALL_DAMAGE, [1.0, 2.0]),
        (FALL_DAMAGE, [1.0, 2.0]),
    ]));
    assert_eq!(
        t.s.reason(&t.call),
        format!("template has multiple {FALL_DAMAGE} variants")
    );
    let mut short = template(&[(FALL_DAMAGE, [1.0, 2.0])]);
    short.truncate(20);
    set(short);
    assert_eq!(t.s.reason(&t.call), tc::OUTSIDE);
    let mut huge = template(&[(FALL_DAMAGE, [1.0, 2.0])]);
    huge[4..8].copy_from_slice(&1000u32.to_le_bytes());
    set(huge);
    assert_eq!(t.s.reason(&t.call), tc::UNSUPPORTED);
    set(template(&[(FALL_DAMAGE, [1.0, 2.0])]));
    // not loaded, the service down
    fs::loaded(t.s.owner, |f| f.resources.clear());
    assert_eq!(t.s.reason(&t.call), t::RESOURCE_CHANGED);
    fs::loaded(t.s.owner, |f| {
        f.resources
            .push((t.key, template(&[(FALL_DAMAGE, [1.0, 2.0])])))
    });
    // the entity's template, then the session, moved during the read
    let owner = t.s.owner;
    fs::on_loaded_call(Some(Box::new(move || {
        fs::ecs_world(owner, |w| {
            w.entities.get_mut(&7).unwrap().template = Some([0x6b; 16])
        });
    })));
    assert_eq!(t.s.reason(&t.call), tc::CHANGED);
    fs::on_loaded_call(None);
    t.s.world(|w| w.entities.get_mut(&7).unwrap().template = Some(UUID));
    let moved = t.s.session.clone();
    fs::on_loaded_call(Some(Box::new(move || moved.set(FAKE_SESSION + 1))));
    assert_eq!(t.s.reason(&t.call), tc::SESSION);
    fs::on_loaded_call(None);
    t.s.session.set(FAKE_SESSION);
    // the reads off: ShroudForge's availability text
    t.s.facts(|f| f.ready = false);
    assert_eq!(
        t.s.reason(&t.call),
        "runtime.ecs.read is currently unavailable"
    );
    t.s.facts(|f| f.ready = true);
    assert!(t.s.eval::<bool>(&format!("return ({}) ~= nil", t.call)));
}

#[test]
fn write_field_compare_exchanges_one_scalar_of_the_shared_template() {
    let t = setup_template(template(&[
        ("keen::tpl::Other", [1.0, 2.0]),
        (FALL_DAMAGE, [8.0, 16.0]),
    ]));
    t.s.eval::<()>(&format!("view = assert({})", t.call));
    let (ok, why): (bool, Option<String>) = t.s.eval("return view:write_field('start', 20.0, 8.0)");
    assert!(ok, "{why:?}");
    // the FallDamage value starts after two descriptors and the Other value: 8 + 24 + 8
    let writes = fs::loaded(t.s.owner, |f| f.writes.clone()).unwrap();
    assert_eq!(writes.len(), 1);
    assert_eq!(
        (writes[0].1, writes[0].2.clone(), writes[0].3),
        (40, 20f32.to_le_bytes().to_vec(), FAKE_SESSION)
    );
    // sfi R3: the write names the view's package and the field path, for Kiln's registry report
    assert_eq!(
        fs::loaded(t.s.owner, |f| f.names.clone()).unwrap(),
        vec![("shroudforge:alpha".to_owned(), "start".to_owned())]
    );
    assert_eq!(&stored(&t)[40..44], &20f32.to_le_bytes());
    assert_eq!(
        t.s.eval::<f64>("return view:read_fields({'start'}).start"),
        20.0
    );
    // the expected value and the expected fields
    assert_eq!(
        t.s.reason("view:write_field('start', 30.0, 8.0)"),
        tc::WRITE_EXPECTED
    );
    assert_eq!(
        t.s.reason("view:write_field('start', 30.0, 20.0, {lethal = 15.0})"),
        tc::SNAPSHOT_CHANGED
    );
    assert_eq!(
        t.s.reason("view:write_field('start', 30.0, 20.0, {})"),
        tc::EXPECTED_EMPTY
    );
    assert_eq!(
        t.s.reason("view:write_field('start', 30.0, 20.0, 5)"),
        tc::EXPECTED_MAP
    );
    assert_eq!(
        t.s.reason("view:write_field('start', 30.0, 20.0, {[1] = 16.0})"),
        tc::EXPECTED_KEYS
    );
    let (ok, _): (bool, Option<String>) =
        t.s.eval("return view:write_field('start', 30.0, 20.0, {lethal = 16.0})");
    assert!(ok);
    assert_eq!(&stored(&t)[40..44], &30f32.to_le_bytes());
    // the arguments, and the write availability
    assert_eq!(t.s.reason("view:write_field('start', 1.0)"), tc::WRITE_ARGS);
    assert_eq!(t.s.reason("view:write_field(1, 1.0, 30.0)"), tc::WRITE_PATH);
    t.s.facts(|f| f.resource_can_write = false);
    assert_eq!(
        t.s.reason("view:write_field('start', 1.0, 30.0)"),
        "runtime.assets.write is currently unavailable"
    );
    t.s.facts(|f| f.resource_can_write = true);
    // an uncertain write stops this view's writes
    fs::loaded(t.s.owner, |f| {
        f.cas_answer = Some(KILN_ECS_UNCERTAIN as u32)
    });
    let first = t.s.reason("view:write_field('start', 1.0, 30.0)");
    assert!(first.to_lowercase().contains("uncertain"), "{first}");
    assert_eq!(t.s.reason("view:write_field('start', 1.0, 30.0)"), first);
}

#[test]
fn the_template_override_target_reads_and_writes_through_the_view() {
    let t = setup_template(template(&[(FALL_DAMAGE, [8.0, 16.0])]));
    let lua = t.s.vm.lua();
    let runtime = lua.create_table().unwrap();
    runtime
        .raw_set("overrides", lua.create_table().unwrap())
        .unwrap();
    let host: Rc<dyn RuntimeHost> = t.s.host.clone();
    target::install_into(lua, &runtime, host, t.s.ecs.clone()).unwrap();
    t.s.vm.set("rt", runtime);
    let (key, shared, value, lethal): (String, bool, f64, f64) = t.s.eval(&format!(
        "view = assert({}); target = assert(rt.overrides.template(view, 'start', {{'lethal'}}));
         local snap = assert(target.read()); return target.key, target.shared, snap.value, snap.values.lethal",
        t.call
    ));
    assert_eq!(
        key,
        format!(
            "view/{FAKE_SESSION}/{}/{TEMPLATE}/0/{FALL_DAMAGE}/start",
            guid_text(&UUID)
        )
    );
    assert!(shared);
    assert_eq!((value, lethal), (8.0, 16.0));
    // the write goes through write_field with the snapshot's value and expected fields (the one variant's value is at
    // 8 + 12: start at 20, lethal at 24)
    let (ok, written): (bool, f64) = t
        .s
        .eval("local snap = target.read(); local ok, v = target.write(snap, 16.0); return ok, v");
    assert!(ok);
    assert_eq!(written, 16.0);
    assert_eq!(&stored(&t)[20..24], &16f32.to_le_bytes());
    // another writer moved the expected field in between: refused, nothing written
    let snap_then = "snap = target.read()";
    t.s.eval::<()>(snap_then);
    fs::loaded(t.s.owner, |f| {
        f.resources[0].1[24..28].copy_from_slice(&15f32.to_le_bytes())
    });
    let (ok, why): (bool, String) =
        t.s.eval("local ok, _, why = target.write(snap, 20.0); return ok, why");
    assert!(!ok);
    assert_eq!(why, tc::SNAPSHOT_CHANGED);
    assert_eq!(&stored(&t)[20..24], &16f32.to_le_bytes());
    // the normalize is the field codec's canonical value
    assert_eq!(t.s.eval::<f64>("return (target.normalize(nil, 3))"), 3.0);
    // get_template_claim's arguments
    assert_eq!(
        t.s.reason("rt.overrides.get_template_claim()"),
        tt::ARGUMENTS
    );
    assert!(t.s.eval::<bool>("return rt.overrides.get_template_claim(view) ~= nil"));
}

#[test]
fn a_variant_value_on_its_descriptors_or_a_leaf_that_is_no_scalar_is_refused_per_path() {
    // ShroudForge resolves a view's field as components[n].value.<path> (resolve_resource_field.rs:18-223): the
    // variant's descriptor and payload are checked on the way, then the leaf; each refusal names the path, and nothing
    // is written.
    let t = setup_template(template(&[(FALL_DAMAGE, [8.0, 16.0])]));
    t.s.eval::<()>(&format!("view = assert({})", t.call));
    // the one variant's descriptor at 8: {hash, offset from its offset word at 12, size}; its value at 20
    let with = |relative: u32, size: u32| {
        let mut bytes = template(&[(FALL_DAMAGE, [8.0, 16.0])]);
        bytes[12..16].copy_from_slice(&relative.to_le_bytes());
        bytes[16..20].copy_from_slice(&size.to_le_bytes());
        bytes
    };
    for (bytes, why) in [
        // no payload: the value would lie on the descriptor's own offset and size words
        (
            with(0, 8),
            "invalid, absent or mismatched resource variant descriptor",
        ),
        // a payload that starts inside the descriptor (at 16, its size word)
        (
            with(4, 8),
            "variant payload overlaps its resource descriptors",
        ),
        // another size than the reflected component's
        (
            with(8, 12),
            "invalid, absent or mismatched resource variant descriptor",
        ),
    ] {
        fs::loaded(t.s.owner, |f| f.resources[0].1 = bytes.clone());
        assert_eq!(
            t.s.reason("view:read_fields({'start'})"),
            format!("start: {why}")
        );
        assert_eq!(
            t.s.reason("view:write_field('start', 1.0, 8.0)"),
            format!("start: {why}")
        );
        assert_eq!(stored(&t), bytes, "nothing written");
    }
    // a leaf whose value is no finite scalar is refused for a read as well
    fs::loaded(t.s.owner, |f| {
        f.resources[0].1 = template(&[(FALL_DAMAGE, [f32::NAN, 16.0])])
    });
    assert_eq!(
        t.s.reason("view:read_fields({'start'})"),
        "start: runtime field writes require finite floating-point values"
    );
    assert_eq!(
        t.s.eval::<f64>("return view:read_fields({'lethal'}).lethal"),
        16.0
    );
    assert!(
        fs::loaded(t.s.owner, |f| f.writes.is_empty()).unwrap(),
        "nothing was written"
    );
}
