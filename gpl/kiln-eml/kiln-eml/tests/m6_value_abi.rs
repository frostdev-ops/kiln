// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln.eml.values v1 as its C table (native/core/abi/services/kiln_eml_values.h), called as native consumers do:
//! through the table kiln-eml publishes as `kiln.eml.values` and the generated kiln-abi bindings, never through the
//! graph's Rust API. The archive views come from kiln.eml.archive's real table over generations the startup controller API
//! authorizes with the synthetic m6 registry. The Lua side is a real mlua 0.11.6 VM behind the C reader ABI
//! (support/m6_reader), and a C consumer (tests/c/m6_values_consumer.c, built as C11 and as C++20) drives the same
//! table with its own reader. Expectations are ShroudForge 1.7.9's at Kiln's fork pin where Source behaviour is involved
//! (the value wrappers are the oracle in `wrappers_and_the_c_round_trip_agree`), and the header's elsewhere.
#[path = "support/m6_fixture/mod.rs"]
mod fixture;
#[path = "support/m6_reader/mod.rs"]
mod reader;
#[path = "support/m6_types/mod.rs"]
mod reg;
#[path = "support/c_consumer/mod.rs"]
mod c_consumer;
#[path = "support/m6_userdata/mod.rs"]
mod userdata;
#[path = "support/m6_values/mod.rs"]
mod values;

use std::{
    ffi::c_void,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
        mpsc,
    },
    time::Duration,
};

use kfc::reflection::TypeRegistry;
use kiln_abi::{
    KILN_ERR_BUFFER_TOO_SMALL, KILN_ERR_INVALID_ARGUMENT, KILN_ERR_NOT_FOUND, KILN_ERR_REVOKED, KILN_ERR_UNAVAILABLE,
    KILN_ERR_WRONG_THREAD, KILN_OK, KilnStr,
    services::{
        kiln_caps::{KILN_CAP_AVAILABLE, KilnCapsApi},
        kiln_eml_archive::{KilnEmlArchiveApiV1, KilnEmlArchiveIdentityV1},
        kiln_eml_values::{
            KILN_LUA_GET_FIELD_VALUE, KILN_LUA_LENGTH, KILN_LUA_SOURCE_TO_STRING, KILN_VALUE_LOADED_SNAPSHOT,
            KILN_VALUE_MUTATED, KILN_VALUE_OPEN_LUA_INPUT, KILN_VALUE_READ_LENGTH, KILN_VALUE_RUNTIME,
            KILN_VALUE_SEMANTIC_ERROR, KILN_VALUE_STARTUP, KilnEmlLuaReaderV1, KilnEmlValuesApiV1,
        },
    },
};
use kiln_eml::{
    archive_service::{self, Grant, Phase, RegistrySnapshot},
    archive_values::abi,
    sf_startup::resource_values::{self as rv, ValueObjects},
};
use mlua::{Lua, Value};
use reader::{HOST_OWNER, Host, OWNER_B, field, open_record, reader_table, unhex};
use serde_json::json;

const ROOT_TABLE: &str = r#"return {child = {base = 7, extra = 11}, fixed = {2, 3},
    children = {{base = 13, extra = 17}, {base = 19, extra = 23}}, maybe = {base = 29, extra = 31},
    choice = {type = "m6.Child", value = {base = 37, extra = 41}}, text = "synthetic"}"#;
const GAME_VERSION: &str = "m6-kfc-data-12";
const ROOT: usize = 8;

// ---- the table and the archive views it resolves -------------------------------------------------------------------

/// The table as `publish` registers it.
fn api() -> &'static KilnEmlValuesApiV1 {
    abi::table()
}

fn archive() -> &'static KilnEmlArchiveApiV1 {
    unsafe { &*archive_service::kiln_eml_archive_service(1) }
}

/// The synthetic registry the values tests use (the wrappers': the codec's plus the optional list and the faulty types).
fn registry() -> TypeRegistry {
    reg::registry_of("m6-userdata", userdata::types())
}

/// Generations are process-global in the archive provider: every test takes its own numbers.
static NEXT: AtomicU64 = AtomicU64::new(12_000);

fn grant() -> Grant {
    Grant {
        phase: Phase::Published,
        launch_id: [0x12; 16],
        plan_id: [0x21; 16],
        generation: NEXT.fetch_add(1, Ordering::SeqCst),
        reload_epoch: 3,
        plan_sha256: [0x31; 32],
        archive_sha256: [0x41; 32],
        prepared_owners: Vec::new(),
    }
}

fn acquire(pins: &KilnEmlArchiveIdentityV1) -> u64 {
    let mut view = 0;
    assert_eq!(
        unsafe { archive().acquire.unwrap()(HOST_OWNER, pins, &mut view) },
        KILN_OK
    );
    view
}

/// A live view of a generation that keeps `source` beside its snapshot of `snapshot_of`.
fn view_of(snapshot_of: &TypeRegistry, source: Option<TypeRegistry>) -> (u64, KilnEmlArchiveIdentityV1) {
    let snapshot = RegistrySnapshot::new(snapshot_of, GAME_VERSION);
    let pins = match source {
        Some(source) => archive_service::authorize_with_registry(grant(), snapshot, Arc::new(source)),
        None => archive_service::authorize(grant(), snapshot),
    }
    .unwrap();
    (acquire(&pins), pins)
}

/// A live view over the values registry.
fn view() -> u64 {
    view_of(&registry(), Some(registry())).0
}

/// The codec's encoder over a plain Lua table (an independent producer of resource bytes).
fn encode(chunk: &str, index: usize) -> Vec<u8> {
    let lua = Lua::new();
    let value: Value = lua.load(chunk).eval().unwrap();
    let registry = userdata::registry();
    rv::encode(&value, &userdata::handle(&registry, index), &lua, &ValueObjects::new()).unwrap()
}

fn node_id(host: &Host, lease: u64) -> u64 {
    host.info(lease, 0).unwrap().identity.node_id
}

// ---- registration -----------------------------------------------------------------------------------------------

// Red-first: the table is the header's (144 bytes, version 1, every member) and `publish` registers exactly it as
// kiln.eml.values, then reports `eml.values` and its seventeen members; a failed registration reports nothing.
#[test]
fn published_table_and_capability_rows() {
    static ROWS: Mutex<Vec<(u64, String, u32)>> = Mutex::new(Vec::new());
    unsafe extern "C" fn report(owner: u64, name: KilnStr, state: u32, _reason: u32, _text: KilnStr) -> i32 {
        let name = unsafe { std::slice::from_raw_parts(name.ptr.cast::<u8>(), name.len) };
        ROWS.lock()
            .unwrap()
            .push((owner, String::from_utf8(name.to_vec()).unwrap(), state));
        KILN_OK
    }
    let caps = KilnCapsApi {
        header: kiln_abi::KilnServiceHeader {
            size: std::mem::size_of::<KilnCapsApi>() as u32,
            version: 1,
        },
        count: None,
        at: None,
        find: None,
        report: Some(report),
        subscribe: None,
        unsubscribe: None,
        generation: None,
    };
    let table = api();
    assert_eq!((table.header.size as usize, table.header.version), (144, 1));
    assert_eq!(std::mem::size_of::<KilnEmlValuesApiV1>(), 144);
    let members = [
        table.arena_create.is_some(),
        table.arena_close.is_some(),
        table.arena_collect.is_some(),
        table.value_open.is_some(),
        table.value_info.is_some(),
        table.value_read.is_some(),
        table.value_child.is_some(),
        table.value_set.is_some(),
        table.value_clone.is_some(),
        table.value_export.is_some(),
        table.value_close.is_some(),
        table.value_result_copy.is_some(),
        table.value_array_push.is_some(),
        table.value_array_insert.is_some(),
        table.value_array_pop.is_some(),
        table.value_array_remove.is_some(),
        table.value_array_clear.is_some(),
    ];
    assert!(members.iter().all(|present| *present));
    let mut registered = Vec::new();
    let rc = abi::publish(
        91,
        |name: &str, table: *const c_void| {
            registered.push((name.to_owned(), table));
            KILN_OK
        },
        Some(&caps),
    );
    assert_eq!(rc, KILN_OK);
    assert_eq!(
        registered,
        [("kiln.eml.values".to_owned(), api() as *const _ as *const c_void)]
    );
    let rows = std::mem::take(&mut *ROWS.lock().unwrap());
    assert_eq!(rows.len(), 18);
    assert_eq!(rows[0], (91, "eml.values".to_owned(), KILN_CAP_AVAILABLE as u32));
    assert!(rows.iter().all(|(owner, name, state)| *owner == 91
        && *state == KILN_CAP_AVAILABLE as u32
        && name.starts_with("eml.values")));
    assert!(rows.iter().any(|(_, name, _)| name == "eml.values.value_array_clear"));
    let rc = abi::publish(92, |_: &str, _: *const c_void| KILN_ERR_INVALID_ARGUMENT, Some(&caps));
    assert_eq!(rc, KILN_ERR_INVALID_ARGUMENT);
    assert!(ROWS.lock().unwrap().is_empty(), "a failed registration reports no row");
    assert_eq!(
        abi::publish(93, |_: &str, _: *const c_void| KILN_OK, None),
        KILN_OK,
        "kiln.caps is optional"
    );
}

// ---- view -> registry -----------------------------------------------------------------------------------------------

// Red-first: arena_create resolves the archive view to its generation's kept registry, copies it, and refuses
// (UNAVAILABLE, no arena, no reader call) a copy whose digest is not the view's registry_sha256: another registry, the
// same types under another registry version, one changed field offset, or a generation that keeps no registry. A view
// that is unknown is INVALID_ARGUMENT, a released or retired one REVOKED. The arena's identity carries the view's pins
// and layout_revision = registry_id; its copy outlives the view and the generation.
#[test]
fn arena_create_resolves_the_view_to_its_registry() {
    let base = registry();
    let mut versioned = registry();
    versioned.version = "m6-userdata-other".into();
    let mut moved_types = userdata::types();
    moved_types[2].struct_fields.get_mut("extra").unwrap().data_offset = 5;
    let moved = reg::registry_of("m6-userdata", moved_types);
    for (source, why) in [
        (Some(reg::registry()), "another registry"),
        (Some(versioned), "another registry version"),
        (Some(moved), "one field offset differs"),
        (None, "no registry kept"),
    ] {
        let (view, _) = view_of(&base, source);
        let host = Host::bare(api(), view);
        assert_eq!(host.create(KILN_VALUE_RUNTIME), KILN_ERR_UNAVAILABLE, "{why}");
        assert_eq!(
            host.shared().callbacks.load(Ordering::SeqCst),
            0,
            "{why}: the reader was never called"
        );
    }
    let unknown = Host::bare(api(), 0x7fff_ffff);
    assert_eq!(unknown.create(KILN_VALUE_RUNTIME), KILN_ERR_INVALID_ARGUMENT);
    let (released, _) = view_of(&base, Some(registry()));
    assert_eq!(unsafe { archive().release.unwrap()(released) }, KILN_OK);
    assert_eq!(Host::bare(api(), released).create(KILN_VALUE_RUNTIME), KILN_ERR_REVOKED);
    let (retired, pins) = view_of(&base, Some(registry()));
    archive_service::retire(pins.generation);
    assert_eq!(Host::bare(api(), retired).create(KILN_VALUE_STARTUP), KILN_ERR_REVOKED);

    let (view, pins) = view_of(&base, Some(registry()));
    let host = Host::new(api(), view, KILN_VALUE_RUNTIME);
    let child = host
        .call(|| host.open_lua("m6.Child", "return {base = 7, extra = 11}"))
        .item;
    let info = host.info(child, 0).unwrap();
    let archive_pins = info.identity.archive;
    assert_eq!(
        (
            archive_pins.generation,
            archive_pins.registry_id,
            archive_pins.registry_sha256,
            archive_pins.reload_epoch
        ),
        (
            pins.generation,
            pins.registry_id,
            pins.registry_sha256,
            pins.reload_epoch
        )
    );
    assert_eq!(
        info.identity.layout_revision, pins.registry_id,
        "layout_revision is registry_id"
    );
    assert_eq!(info.identity.lua_phase, KILN_VALUE_RUNTIME as u32);
    assert_eq!(info.identity.creator_owner, HOST_OWNER);
    // another generation's view (same registry) is no owner view of this arena; a released one is REVOKED
    let (other, _) = view_of(&base, Some(registry()));
    let object = host.arg(host.eval("return {base = 1, extra = 2}"));
    let record = open_record(KILN_VALUE_OPEN_LUA_INPUT, b"m6.Child", &[], object);
    let open_with = |owner_view: u64| {
        host.op(|r| unsafe { api().value_open.unwrap()(HOST_OWNER, host.control.get(), owner_view, &record, r) })
    };
    assert_eq!(open_with(other).err(), Some(KILN_ERR_INVALID_ARGUMENT));
    // the view goes, then its generation: values already made keep working on the arena's own registry copy
    assert_eq!(unsafe { archive().release.unwrap()(view) }, KILN_OK);
    assert_eq!(open_with(view).err(), Some(KILN_ERR_REVOKED));
    archive_service::retire(pins.generation);
    let base_field = host
        .call(|| host.op(|r| unsafe { api().value_child.unwrap()(OWNER_B, child, &field(b"base"), r) }))
        .unwrap();
    assert_eq!(host.json(base_field.payload)["value"]["value"], "i64:0000000000000007");
    host.close(base_field.payload);
    host.close(child);
    host.finish();
}

// ---- reader admission -------------------------------------------------------------------------------------------

#[cfg(windows)]
mod guard {
    use std::ffi::c_void;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn VirtualAlloc(addr: *mut c_void, size: usize, kind: u32, protect: u32) -> *mut c_void;
        fn VirtualProtect(addr: *mut c_void, size: usize, protect: u32, old: *mut u32) -> i32;
        fn VirtualFree(addr: *mut c_void, size: usize, kind: u32) -> i32;
    }
    const PAGE: usize = 4096;
    const MEM_COMMIT_RESERVE: u32 = 0x1000 | 0x2000;
    const MEM_RELEASE: u32 = 0x8000;
    const PAGE_READWRITE: u32 = 0x04;
    const PAGE_NOACCESS: u32 = 0x01;
    /// `bytes` copied to the very end of a committed page whose next page is PAGE_NOACCESS: reading one byte past them
    /// faults.
    pub struct Guarded {
        base: *mut c_void,
        pub at: *const u8,
    }
    impl Guarded {
        pub fn new(bytes: &[u8]) -> Self {
            unsafe {
                let base = VirtualAlloc(std::ptr::null_mut(), 2 * PAGE, MEM_COMMIT_RESERVE, PAGE_READWRITE);
                assert!(!base.is_null());
                let mut old = 0u32;
                assert_ne!(
                    VirtualProtect(base.cast::<u8>().add(PAGE).cast(), PAGE, PAGE_NOACCESS, &mut old),
                    0
                );
                let at = base.cast::<u8>().add(PAGE - bytes.len());
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), at, bytes.len());
                Self { base, at }
            }
        }
    }
    impl Drop for Guarded {
        fn drop(&mut self) {
            unsafe { VirtualFree(self.base, 0, MEM_RELEASE) };
        }
    }
}

#[cfg(target_os = "linux")]
mod guard {
    use std::ffi::c_void;
    unsafe extern "C" {
        fn mmap(addr: *mut c_void, len: usize, prot: i32, flags: i32, fd: i32, offset: i64) -> *mut c_void;
        fn mprotect(addr: *mut c_void, len: usize, prot: i32) -> i32;
        fn munmap(addr: *mut c_void, len: usize) -> i32;
    }
    const PAGE: usize = 4096;
    /// `bytes` copied to the very end of a readable page whose next page is inaccessible: reading one byte past them
    /// faults.
    pub struct Guarded {
        base: *mut c_void,
        pub at: *const u8,
    }
    impl Guarded {
        pub fn new(bytes: &[u8]) -> Self {
            unsafe {
                let base = mmap(std::ptr::null_mut(), 2 * PAGE, 1 | 2, 0x02 | 0x20, -1, 0);
                assert!(!base.is_null() && base as isize != -1);
                assert_eq!(mprotect(base.cast::<u8>().add(PAGE).cast(), PAGE, 0), 0);
                let at = base.cast::<u8>().add(PAGE - bytes.len());
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), at, bytes.len());
                Self { base, at }
            }
        }
    }
    impl Drop for Guarded {
        fn drop(&mut self) {
            unsafe { munmap(self.base, 2 * PAGE) };
        }
    }
}

// Red-first: a reader is admitted only with version 1, `user`, and access/copy/retain/release covered by its size and
// non-null; `wake` is optional. Nothing past `size` is ever read: on Linux and Windows every truncated table ends at a
// page guard.
// The provider keeps its own snapshot, so the host may reuse its table's memory after arena_create.
#[test]
fn reader_admission_and_truncated_tables() {
    let view = view();
    let full = std::mem::size_of::<KilnEmlLuaReaderV1>();
    assert_eq!(full, 56);
    for size in [4usize, 8, 15, 16, 23, 24, 31, 32, 39, 40, 47, 48, 55, 56] {
        let host = Host::bare(api(), view);
        let mut table = *host.reader.borrow();
        table.size = size as u32;
        let bytes =
            unsafe { std::slice::from_raw_parts((&table as *const KilnEmlLuaReaderV1).cast::<u8>(), full) }.to_vec();
        let mut control = u64::MAX;
        let call = |reader: *const KilnEmlLuaReaderV1, control: &mut u64| unsafe {
            api().arena_create.unwrap()(HOST_OWNER, view, KILN_VALUE_RUNTIME as u32, reader, control)
        };
        #[cfg(any(target_os = "linux", windows))]
        let rc = {
            let guarded = guard::Guarded::new(&bytes[..size]);
            call(guarded.at.cast(), &mut control)
        };
        #[cfg(not(any(target_os = "linux", windows)))]
        let rc = call(bytes.as_ptr().cast(), &mut control);
        if size >= 48 {
            assert_eq!(rc, KILN_OK, "size {size}");
            host.control.set(control);
            // the snapshot: the table's memory is gone; calls still reach the admitted callbacks
            let lease = host
                .call(|| host.open_lua("m6.Child", "return {base = 1, extra = 2}"))
                .item;
            let closer = std::thread::spawn(move || unsafe { api().value_close.unwrap()(OWNER_B, lease) });
            assert_eq!(closer.join().unwrap(), KILN_OK);
            let wakes = host.shared().wakes.load(Ordering::SeqCst);
            assert_eq!(
                wakes,
                usize::from(size >= 56),
                "size {size}: a wake only when the table has one"
            );
            host.finish();
        } else {
            assert_eq!((rc, control), (KILN_ERR_INVALID_ARGUMENT, 0), "size {size}");
            assert_eq!(host.shared().callbacks.load(Ordering::SeqCst), 0);
        }
    }
    // the version and null members
    let host = Host::bare(api(), view);
    let edits: [(&str, fn(&mut KilnEmlLuaReaderV1)); 6] = [
        ("version 0", |t| t.version = 0),
        ("version 2", |t| t.version = 2),
        ("no access", |t| t.access = None),
        ("no copy", |t| t.copy = None),
        ("no retain", |t| t.retain = None),
        ("no release", |t| t.release = None),
    ];
    for (what, edit) in edits {
        let mut table = reader_table(&*host.ctx);
        edit(&mut table);
        *host.reader.borrow_mut() = table;
        assert_eq!(host.create(KILN_VALUE_RUNTIME), KILN_ERR_INVALID_ARGUMENT, "{what}");
    }
    let mut table = reader_table(&*host.ctx);
    table.wake = None;
    *host.reader.borrow_mut() = table;
    assert_eq!(host.create(KILN_VALUE_RUNTIME), KILN_OK, "no wake is fine");
    host.finish();
    // a larger (later-version-sized) table is admitted by its v1 prefix
    let bigger = Host::bare(api(), view);
    let mut long = [0u64; 8];
    let table = reader_table(&*bigger.ctx);
    unsafe {
        std::ptr::copy_nonoverlapping(
            (&table as *const KilnEmlLuaReaderV1).cast::<u8>(),
            long.as_mut_ptr().cast::<u8>(),
            full,
        );
        long.as_mut_ptr().cast::<u32>().write(64);
    }
    let mut control = 0;
    let rc = unsafe {
        api().arena_create.unwrap()(
            HOST_OWNER,
            view,
            KILN_VALUE_RUNTIME as u32,
            long.as_ptr().cast(),
            &mut control,
        )
    };
    assert_eq!(rc, KILN_OK);
    bigger.control.set(control);
    bigger.finish();
}

// Red-first: a count no caller can mean (above isize::MAX: no object is that large) is INVALID_ARGUMENT before any slice
// over the caller's memory exists or any work runs: value_result_copy's capacity (nothing written, `required` untouched)
// and value_open's byte range (even for LUA_INPUT, which reads no bytes).
#[test]
fn counts_past_isize_max_are_refused() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    let root = host
        .call(|| host.open_lua("m6.Child", "return {base = 7, extra = 11}"))
        .item;
    let view_payload = host
        .call(|| host.op(|r| unsafe { api().value_read.unwrap()(OWNER_B, root, 0, r) }))
        .unwrap()
        .payload;
    let huge = isize::MAX as usize + 1;
    let mut buf = [0u8; 4096];
    let mut required = 7usize;
    let copy = api().value_result_copy.unwrap();
    assert_eq!(
        unsafe { copy(OWNER_B, view_payload, buf.as_mut_ptr().cast(), huge, &mut required) },
        KILN_ERR_INVALID_ARGUMENT
    );
    assert_eq!((required, buf[0]), (7, 0), "nothing written");
    let object = host.arg(host.eval("return {base = 1, extra = 2}"));
    let mut record = open_record(KILN_VALUE_OPEN_LUA_INPUT, b"m6.Child", &buf, object);
    record.length = huge;
    let accesses = host.stats.accesses.get();
    assert_eq!(host.call(|| host.open(&record)).err(), Some(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(host.stats.accesses.get(), accesses, "no reader call");
    record.length = buf.len();
    let opened = host.call(|| host.open(&record)).unwrap();
    host.close(opened.item);
    host.close(view_payload);
    host.close(root);
    host.finish();
}

// ---- the reader adapter over a real mlua host ------------------------------------------------------------------------

// Red-first: through the C table and the C reader only: Source's aliases and clones; metatable-provided struct fields
// (Table::get runs __index once per field); a throwing field get is missing_field and is not retained; a throwing variant
// get is the lua class with its host error retained once and released once at close; a stateful __len is called as
// often as Source calls len; an incompatible value's text is one SOURCE_TO_STRING; the same node mutably borrowed is
// Source's borrow error; a foreign userdata is found_userdata; an array member on a struct is UNSUPPORTED; short packet
// copies never repeat the operation that made the packet.
#[test]
fn reader_adapter_over_a_real_mlua_host() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    host.lua
        .globals()
        .set("Foreign", host.lua.create_userdata(reader::Foreign).unwrap())
        .unwrap();
    host.call(|| host.global("R", host.open_lua("m6.Root", ROOT_TABLE)));
    host.run(
        r#"
        local c = R.child
        assert(rawequal(c, R.child), "a get returns the same userdata")
        c.base = 70
        assert(R.child.base == 70, "an alias sees the edit")
        R.child = c
        assert(not rawequal(R.child, c), "an assignment clones")
        R.child = setmetatable({}, {__index = function(_, k) return ({base = 5, extra = 6})[k] end})
        assert(R.child.base == 5 and R.child.extra == 6, "metatable-provided fields")
        local e = perr(function() R.child = setmetatable({}, {__index = function() error("nope") end}) end)
        assert(e == "at .: missing field `base` in struct `m6.Child`", e)
        e = perr(function() R.children = R.child end)
        assert(e == "at .: expected `m6.Children`, got `userdata `<frontend TypeId>``", e)
        e = perr(function() R.child = Foreign end)
        assert(e:find("frontend TypeId", 1, true), e)
        local seen
        R.children[1] = setmetatable({}, {__index = function(_, k) seen = R.child.base return 99 end})
        assert(seen == 5 and R.children[1].base == 99, "re-entry into another node")
        vt.insert(R.children, {base = 1, extra = 2})
        assert(#R.children == 3)
        local removed = vt.remove(R.children, 1)
        assert(removed.base == 99 and #R.children == 2)
        e = perr(function() vt.insert(R.fixed, 4) end)
        assert(e == "attempt to modify static array", e)
        e = perr(function() R.nope = 1 end)
        assert(e == "attempt to set invalid property 'nope' on m6.Root", e)
        "#,
    );
    let root = host.lease_of_global("R");
    // a throwing variant get: the lua class, its host error retained, released once on close
    let raising = host.arg(host.eval(r#"return setmetatable({}, {__index = function(_, k) error("no " .. k) end})"#));
    let retains = host.stats.retains.get();
    let reply = host
        .call(|| host.op(|r| unsafe { api().value_set.unwrap()(OWNER_B, root, &field(b"choice"), raising, r) }))
        .unwrap();
    assert_eq!(reply.disposition, KILN_VALUE_SEMANTIC_ERROR as u32);
    assert_eq!(reply.flags & KILN_VALUE_MUTATED as u32, 0);
    assert_eq!(
        host.stats.retains.get(),
        retains + 1,
        "the host error is retained before the call ends"
    );
    let tree = host.json(reply.error)["error"].clone();
    assert_eq!(
        (&tree["path"], &tree["kind"], &tree["cause"]["class"]),
        (&json!("type"), &json!("lua"), &json!("lua"))
    );
    let message = String::from_utf8(unhex(tree["message"].as_str().unwrap())).unwrap();
    assert!(
        message.starts_with("at type: runtime error: ") && message.contains("no type"),
        "{message}"
    );
    let (releases, accesses) = (host.stats.releases.get(), host.stats.accesses.get());
    let _ = host.json(reply.error);
    assert_eq!(
        host.stats.accesses.get(),
        accesses,
        "copying an error packet re-runs nothing"
    );
    host.close(reply.error);
    assert_eq!(host.stats.releases.get(), releases + 1, "released once at close");
    host.close(reply.error);
    assert_eq!(host.stats.releases.get(), releases + 1, "a second close is idempotent");
    // a struct field get that raises: missing_field, nothing retained
    let retains = host.stats.retains.get();
    let reply = host
        .call(|| host.op(|r| unsafe { api().value_set.unwrap()(OWNER_B, root, &field(b"child"), raising, r) }))
        .unwrap();
    assert_eq!(host.json(reply.error)["error"]["kind"], "missing_field");
    assert_eq!(
        host.stats.retains.get(),
        retains,
        "a discarded get error is not retained"
    );
    host.close(reply.error);
    // a stateful __len: Source calls len three times for a static array that does not fit
    host.run("calls = 0");
    let counted = host.arg(
        host.eval(r#"return setmetatable({1, 2, 3}, {__len = function(t) calls = calls + 1 return rawlen(t) end})"#),
    );
    let lengths = host.count(KILN_LUA_LENGTH);
    let reply = host
        .call(|| host.open(&open_record(KILN_VALUE_OPEN_LUA_INPUT, b"m6.Fixed", &[], counted)))
        .unwrap();
    assert_eq!(reply.disposition, KILN_VALUE_SEMANTIC_ERROR as u32);
    assert_eq!(
        (
            host.lua.globals().get::<i64>("calls").unwrap(),
            host.count(KILN_LUA_LENGTH)
        ),
        (3, lengths + 3)
    );
    assert_eq!(
        unhex(host.json(reply.error)["error"]["message"].as_str().unwrap()),
        b"at .: expected `array of length 2`, got `array of length 3`"
    );
    host.close(reply.error);
    // one SOURCE_TO_STRING per incompatible value
    let strings = host.count(KILN_LUA_SOURCE_TO_STRING);
    let wrong = host.arg(host.eval("return {base = true, extra = 1}"));
    let reply = host
        .call(|| host.open(&open_record(KILN_VALUE_OPEN_LUA_INPUT, b"m6.Child", &[], wrong)))
        .unwrap();
    assert_eq!(host.count(KILN_LUA_SOURCE_TO_STRING), strings + 1);
    assert_eq!(host.json(reply.error)["error"]["found"], "bytes:74727565");
    host.close(reply.error);
    // the same node mutably borrowed: a push validating a table whose getter reads that array
    let kids = host
        .call(|| host.op(|r| unsafe { api().value_child.unwrap()(OWNER_B, root, &field(b"children"), r) }))
        .unwrap()
        .item;
    let input = host.arg(host.eval(
        r#"return setmetatable({}, {__index = function(_, k) if k == "base" then local _ = R.children[1] end return 1 end})"#,
    ));
    let gets = host.count(KILN_LUA_GET_FIELD_VALUE);
    let reply = host
        .call(|| host.op(|r| unsafe { api().value_array_push.unwrap()(OWNER_B, kids, input, r) }))
        .unwrap();
    assert!(host.count(KILN_LUA_GET_FIELD_VALUE) > gets);
    assert_eq!(
        host.json(reply.error)["error"]["kind"],
        "missing_field",
        "Source: the borrow error raised in the get"
    );
    host.close(reply.error);
    // an array member on a struct
    let reply = host
        .call(|| host.op(|r| unsafe { api().value_array_pop.unwrap()(OWNER_B, root, r) }))
        .unwrap();
    assert_eq!((reply.disposition, reply.arity, reply.item), (2, 0, 0), "UNSUPPORTED");
    // short packet copies after a pop: the pop happened once
    let len_of = |lease: u64| {
        let reply = host
            .call(|| {
                host.op(|r| unsafe { api().value_read.unwrap()(OWNER_B, lease, KILN_VALUE_READ_LENGTH as u32, r) })
            })
            .unwrap();
        let value = host.json(reply.payload)["value"].clone();
        host.close(reply.payload);
        value
    };
    assert_eq!(len_of(kids), "i64:0000000000000002");
    let popped = host
        .call(|| host.op(|r| unsafe { api().value_array_pop.unwrap()(OWNER_B, kids, r) }))
        .unwrap();
    assert_eq!(popped.flags & KILN_VALUE_MUTATED as u32, KILN_VALUE_MUTATED as u32);
    let view = host
        .call(|| host.op(|r| unsafe { api().value_read.unwrap()(OWNER_B, popped.item, 0, r) }))
        .unwrap();
    let mut small = [0u8; 4];
    let mut required = 0usize;
    for _ in 0..3 {
        let copy = api().value_result_copy.unwrap();
        let rc = unsafe {
            copy(
                OWNER_B,
                view.payload,
                small.as_mut_ptr().cast(),
                small.len(),
                &mut required,
            )
        };
        assert_eq!(rc, KILN_ERR_BUFFER_TOO_SMALL);
        assert!(required > small.len());
    }
    assert_eq!(len_of(kids), "i64:0000000000000001", "three short copies, one pop");
    for token in [view.payload, popped.item, kids] {
        host.close(token);
    }
    host.lua.globals().raw_remove("R").unwrap();
    host.lua.gc_collect().unwrap();
    host.lua.gc_collect().unwrap();
    host.finish();
    assert!(host.stats.adopted.get() > 0, "MAKE_TYPED_PROXY crossed the reader");
    assert!(host.stats.copies.get() > 0);
}

// Red-first: GC forced inside every MAKE_TYPED_PROXY runs proxy finalizers that close other leases through the C table
// while the reader callback runs: those closes are deferred, nothing is released inside a reader call, and once the
// outer calls end every deferred close is applied exactly once (pending equals what is still retained).
#[test]
fn gc_inside_reader_callbacks_defers_closes() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    host.call(|| host.global("R", host.open_lua("m6.Root", ROOT_TABLE)));
    host.gc_in_proxy.set(true);
    host.run(
        r#"
        for i = 1, 20 do
          local kids = R.children
          local first = kids[1]
          R.children = {{base = i, extra = i}, {base = i, extra = i}}
          kids, first = nil, nil
          local t = {}
          for j = 1, 10 do t[j] = R.children[1] end
          local tmp = R.child
          R.child = {base = i, extra = i}
          tmp = nil
          local text = tostring(R)                   -- the variant's text: MAKE_TYPED_PROXY for R.choice
          if i == 10 then local _ = R.choice end      -- pinned from now on: later texts REUSE its proxy
          assert(text:find('"choice":', 1, true))
        end
        "#,
    );
    host.gc_in_proxy.set(false);
    assert!(host.stats.adopted.get() > 0 && host.stats.reused.get() > 0);
    assert!(
        host.stats.closes_in_proxy.get() > 0,
        "finalizers closed leases inside MAKE_TYPED_PROXY"
    );
    assert_eq!(
        host.stats.releases_in_proxy_gc.get(),
        0,
        "nothing released while the callback ran"
    );
    let mut pending = u32::MAX;
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, host.control.get(), &mut pending) },
        KILN_OK
    );
    assert_eq!(pending as usize, host.retained.borrow().len());
    host.lua.globals().raw_remove("R").unwrap();
    host.lua.gc_collect().unwrap();
    host.lua.gc_collect().unwrap();
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, host.control.get(), &mut pending) },
        KILN_OK
    );
    assert_eq!(pending, 0, "the last proxy's close released every pin");
    host.finish();
}

// ---- The value wrappers against the C round trip ---------------------------------------------------------------------------------

const ROUND_TRIP: &str = r##"
local out = {}
local function note(...)
  local t = {}
  for i = 1, select("#", ...) do t[i] = tostring((select(i, ...))) end
  out[#out + 1] = table.concat(t, " ")
end
local function try(f, ...)
  local ok, e = pcall(f, ...)
  if ok then note("ok") else note("error", (tostring(e):match("^[^\n]*"))) end
end
local V = ROOT
note(V.child.base, V.child.extra, #V.children, #V.fixed, V.fixed[1], V.fixed[2], V.text, V.maybe.base)
note(V.choice.type, V.choice.value.base, V.choice.value.extra)
V.child = {base = 1, extra = 2}
note(V.child.base, V.child.extra)
try(function() V.child = {base = "x", extra = 1} end)
try(function() V.child = {extra = 1} end)
try(function() V.nope = 1 end)
INS(V.children, {base = 5, extra = 6})
note(#V.children, V.children[3].base)
INS(V.children, 1, {base = 8, extra = 9})
note(#V.children, V.children[1].base, V.children[2].base)
try(function() INS(V.children, 9, {base = 1, extra = 1}) end)
try(function() INS(V.children, 0, {base = 1, extra = 1}) end)
local x = REM(V.children)
note(x.base, #V.children)
note(REM(V.children, 1).base, #V.children)
note(REM(V.children, 0), #V.children)
note(REM(V.children, #V.children + 1), #V.children)
try(function() REM(V.children, #V.children + 2) end)
try(function() INS(V.fixed, 4) end)
try(function() REM(V.fixed) end)
try(function() CLR(V.fixed) end)
V.fixed[1] = 40
note(V.fixed[1])
local alias = V.children[1]
alias.extra = 77
note(V.children[1].extra, rawequal(alias, V.children[1]))
V.choice.value = {base = 3, extra = 4}
note(V.choice.value.base)
try(function() V.choice.value = {base = "y", extra = 4} end)
CLR(V.children)
note(#V.children)
INS(V.children, {base = 11, extra = 12})
note(#V.children, V.children[1].base)
return table.concat(out, "\n")
"##;

fn wrapper_round_trip(mapped: bool) -> String {
    let (vm, env) = userdata::core_vm();
    let registry = userdata::registry();
    let r#type = userdata::handle(&registry, ROOT);
    let objects = ValueObjects::new();
    let value: Value = vm.lua().load(ROOT_TABLE).set_environment(env.clone()).eval().unwrap();
    let wrapped = if mapped {
        let bytes = rv::encode(&value, &r#type, vm.lua(), &objects).unwrap();
        rv::decode(bytes.into(), &r#type, vm.lua(), &objects).unwrap()
    } else {
        rv::validate_and_clone(&value, &r#type, vm.lua(), &objects).unwrap()
    };
    env.set("ROOT", wrapped).unwrap();
    let script = ROUND_TRIP
        .replace("INS(", "table.insert(")
        .replace("REM(", "table.remove(")
        .replace("CLR(", "table.clear(");
    vm.lua()
        .load(&script)
        .set_name("=g07")
        .set_environment(env)
        .eval::<String>()
        .unwrap()
}

// Red-first: the same Lua script over the value wrappers (the reviewed SF port, in a production sfi-core VM with the typed
// table dispatcher) and over the C round trip (C table + C reader + the minimal frontend) gives the same reads, the same
// error texts (first line) and the same table-operation results, owned and mapped.
#[test]
fn wrappers_and_the_c_round_trip_agree() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    for mapped in [false, true] {
        let reply = host.call(|| {
            if mapped {
                host.open_bytes("m6.Root", &encode(ROOT_TABLE, ROOT))
            } else {
                host.open_lua("m6.Root", ROOT_TABLE)
            }
        });
        host.call(|| host.global("ROOT", reply));
        let script = ROUND_TRIP
            .replace("INS(", "vt.insert(")
            .replace("REM(", "vt.remove(")
            .replace("CLR(", "vt.clear(");
        let c_side: String = host.lua.load(&script).set_name("=g12").eval().unwrap();
        let wrappers = wrapper_round_trip(mapped);
        assert_eq!(c_side, wrappers, "mapped = {mapped}");
        assert!(c_side.lines().count() > 20);
        for expected in [
            "error attempt to set invalid property 'nope' on m6.Root",
            "error attempt to modify static array",
            "error position out of bounds: expected index in range [1, ",
            "error at base: expected ",
        ] {
            assert!(c_side.contains(expected), "{expected}: {c_side}");
        }
        host.lua.globals().raw_remove("ROOT").unwrap();
        host.lua.gc_collect().unwrap();
        host.lua.gc_collect().unwrap();
    }
    host.finish();
}

// ---- LOADED_SNAPSHOT roots ----------------------------------------------------------------------------------------

// Red-first (O1): a LOADED_SNAPSHOT root is held weakly with no pin. While the frontend's ResourceInfo keeps the root
// proxy, a re-open of the same instance is the same node with its edits; another instance is another node. Once the
// ResourceInfo is collected, its root and every pin under it are freed, and a re-open decodes the bytes afresh.
#[test]
fn loaded_roots_are_weak_and_follow_their_resource_info() {
    let bytes = encode(ROOT_TABLE, ROOT);
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    let open = |instance: u64| host.call(|| host.open_loaded("m6.Root", &bytes, instance));
    let first = open(10);
    assert_ne!(
        host.info(first.item, 0).unwrap().flags & KILN_VALUE_LOADED_SNAPSHOT as u32,
        0
    );
    let id = node_id(&host, first.item);
    host.call(|| {
        let info = host.lua.create_table().unwrap();
        info.set("root", host.wrap(first.item)).unwrap();
        host.lua.globals().set("Info", info).unwrap();
    });
    host.run("Info.root.child.base = 70 assert(Info.root.child.base == 70)");
    assert!(
        !host.retained.borrow().is_empty(),
        "the root's child slot pins its proxy"
    );
    // live: the same node, edits kept; the frontend's interning gives the same userdata
    let again = open(10);
    assert_eq!(node_id(&host, again.item), id);
    host.call(|| host.lua.globals().set("Again", host.wrap(again.item)).unwrap());
    host.run("assert(rawequal(Again, Info.root) and Again.child.base == 70) Again = nil");
    let other = open(11);
    assert_ne!(node_id(&host, other.item), id, "per instance");
    host.close(other.item);
    // the ResourceInfo goes: the root and its pins go with it
    host.run("Info = nil");
    host.lua.gc_collect().unwrap();
    host.lua.gc_collect().unwrap();
    let mut pending = u32::MAX;
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, host.control.get(), &mut pending) },
        KILN_OK
    );
    assert!(host.retained.borrow().is_empty(), "the record kept nothing alive");
    assert_eq!(pending, 0);
    let fresh = open(10);
    assert_ne!(node_id(&host, fresh.item), id, "a fresh decode");
    let base = host.call(|| {
        let child = host
            .op(|r| unsafe { api().value_child.unwrap()(OWNER_B, fresh.item, &field(b"child"), r) })
            .unwrap();
        let base = host
            .op(|r| unsafe { api().value_child.unwrap()(OWNER_B, child.item, &field(b"base"), r) })
            .unwrap();
        host.close(child.item);
        let value = host.json(base.payload)["value"]["value"].clone();
        host.close(base.payload);
        value
    });
    assert_eq!(base, "i64:0000000000000007", "the dropped edit is gone");
    host.close(fresh.item);
    host.finish();
}

// ---- worker exit ------------------------------------------------------------------------------------------------

// Red-first: a worker that exits with its arena still open (the host skipped arena_close/collect) revokes it: closes
// from other threads afterwards stay idempotent and signal nothing, arena_close/arena_collect read REVOKED, and no host
// callback runs from the moment the worker's own code ends: not while its thread-local state is destroyed (the
// abandon itself) and not later (the host's memory is kept here only so a regression cannot crash the test).
#[test]
fn worker_exit_revokes_an_open_arena() {
    let view = view();
    let (control, leases, shared, before_exit) = std::thread::spawn(move || {
        let host = Host::new(api(), view, KILN_VALUE_RUNTIME);
        let root = host.call(|| host.open_lua("m6.Root", ROOT_TABLE)).item;
        let child = host
            .call(|| host.op(|r| unsafe { api().value_child.unwrap()(OWNER_B, root, &field(b"child"), r) }))
            .unwrap()
            .item;
        assert!(!host.retained.borrow().is_empty(), "a pinned proxy is retained");
        let shared = host.shared().clone();
        let out = (host.control.get(), [root, child], shared.clone());
        std::mem::forget(host);
        // the last moment the worker's own code runs: its thread-local teardown follows
        (
            out.0,
            out.1,
            out.2,
            (
                shared.wakes.load(Ordering::SeqCst),
                shared.callbacks.load(Ordering::SeqCst),
            ),
        )
    })
    .join()
    .unwrap();
    let (wakes, callbacks) = (
        shared.wakes.load(Ordering::SeqCst),
        shared.callbacks.load(Ordering::SeqCst),
    );
    assert_eq!(
        (wakes, callbacks),
        before_exit,
        "the worker's thread-local teardown (the abandon) called the host"
    );
    for lease in leases {
        assert_eq!(
            unsafe { api().value_close.unwrap()(OWNER_B, lease) },
            KILN_OK,
            "idempotent"
        );
    }
    let mut pending = 7u32;
    assert_eq!(
        unsafe { api().arena_close.unwrap()(HOST_OWNER, control, &mut pending) },
        KILN_ERR_REVOKED
    );
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, control, &mut pending) },
        KILN_ERR_REVOKED
    );
    assert_eq!(pending, 7);
    let mut info = reader::info_record(0);
    assert_eq!(
        unsafe { api().value_info.unwrap()(OWNER_B, leases[0], &mut info) },
        KILN_ERR_REVOKED
    );
    assert_eq!(
        shared.wakes.load(Ordering::SeqCst),
        wakes,
        "no wake after the worker exited"
    );
    assert_eq!(
        shared.callbacks.load(Ordering::SeqCst),
        callbacks,
        "no host callback after the worker exited"
    );
}

// ---- wake ---------------------------------------------------------------------------------------------------------

/// Runs `work` on another thread and waits at most ten seconds (a deadlock fails the test instead of hanging it).
fn elsewhere<R: Send + 'static>(work: impl FnOnce() -> R + Send + 'static) -> R {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(work());
    });
    rx.recv_timeout(Duration::from_secs(10))
        .expect("the other thread finished (no deadlock)")
}

// Red-first: wake is called once per off-worker close, with the control token, on the closing thread, and never for a
// close on the worker. A wake that calls back into the table is refused WRONG_THREAD for every member instead of
// deadlocking on the arena's lock, and that refusal still clears the member's outputs (a result record, arena_create's
// token). After the teardown (pending 0) nothing signals the host again.
#[test]
fn wake_discipline() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    let shared = host.shared().clone();
    let root = host.call(|| host.open_lua("m6.Root", ROOT_TABLE)).item;
    let lease = |host: &Host| {
        host.call(|| host.op(|r| unsafe { api().value_clone.unwrap()(OWNER_B, root, r) }))
            .unwrap()
            .item
    };
    let one = lease(&host);
    host.close(one);
    assert_eq!(
        shared.wakes.load(Ordering::SeqCst),
        0,
        "a close on the worker does not wake"
    );
    let two = lease(&host);
    let thread = elsewhere(move || {
        assert_eq!(unsafe { api().value_close.unwrap()(OWNER_B, two) }, KILN_OK);
        std::thread::current().id()
    });
    assert_eq!(shared.wakes.load(Ordering::SeqCst), 1);
    assert_eq!(*shared.wake_controls.lock().unwrap(), [host.control.get()]);
    assert_eq!(*shared.wake_threads.lock().unwrap(), [thread]);
    // a host bug: wake calls back in
    let three = lease(&host);
    let held = lease(&host);
    shared.reenter_lease.store(held, Ordering::SeqCst);
    shared.reenter_in_wake.store(true, Ordering::SeqCst);
    assert_eq!(
        elsewhere(move || unsafe { api().value_close.unwrap()(OWNER_B, three) }),
        KILN_OK
    );
    shared.reenter_in_wake.store(false, Ordering::SeqCst);
    assert_eq!(
        *shared.reentry_codes.lock().unwrap(),
        [KILN_ERR_WRONG_THREAD; 6],
        "every member refused inside wake"
    );
    assert_eq!(
        *shared.reentry_outputs.lock().unwrap(),
        [48, 0, 0, 0, 0, 0, 0, 0, 0],
        "a refusal inside wake clears the result record (size kept) and arena_create's token"
    );
    assert!(
        host.info(held, 0).is_ok(),
        "the lease wake tried to close is still open"
    );
    let mut pending = u32::MAX;
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, host.control.get(), &mut pending) },
        KILN_OK
    );
    host.close(held);
    host.close(root);
    host.finish();
    let wakes = shared.wakes.load(Ordering::SeqCst);
    let control = host.control.get();
    let (closed, again) = elsewhere(move || unsafe {
        let mut pending = u32::MAX;
        let closed = api().arena_close.unwrap()(HOST_OWNER, control, &mut pending);
        (closed, api().value_close.unwrap()(OWNER_B, one))
    });
    assert_eq!((closed, again), (KILN_OK, KILN_OK));
    assert_eq!(shared.wakes.load(Ordering::SeqCst), wakes, "no wake after the teardown");
}

// ---- closes from many threads ---------------------------------------------------------------------------------------

// Red-first: leases closed from four threads (every third by all four at once) while the worker keeps reading through
// the C table; then an arena_close from another thread while the worker is still working: the worker's next calls are
// REVOKED, its collect drains to 0, every retained host object is released exactly once and never inside a reader call.
#[test]
fn multi_thread_close_stress() {
    let host = Host::new(api(), view(), KILN_VALUE_RUNTIME);
    let root = host.call(|| host.open_lua("m6.Root", ROOT_TABLE)).item;
    let read = |selector: &[u8]| {
        host.call(|| host.op(|r| unsafe { api().value_child.unwrap()(OWNER_B, root, &field(selector), r) }))
    };
    for round in 0..50 {
        let leases: Vec<u64> = (0..64).map(|_| read(b"children").unwrap().item).collect();
        let shared = Arc::new(leases.clone());
        let threads: Vec<_> = (0..4u64)
            .map(|t| {
                let shared = shared.clone();
                std::thread::spawn(move || {
                    let mut ok = 0;
                    for (i, lease) in shared.iter().enumerate() {
                        if i as u64 % 4 == t || i % 3 == 0 {
                            assert_eq!(unsafe { api().value_close.unwrap()(OWNER_B, *lease) }, KILN_OK);
                            ok += 1;
                        }
                        assert_eq!(
                            unsafe { api().value_close.unwrap()(OWNER_B, u64::MAX - t) },
                            KILN_ERR_NOT_FOUND
                        );
                    }
                    ok
                })
            })
            .collect();
        for _ in 0..32 {
            let reply = read(b"text").unwrap();
            host.close(reply.payload);
        }
        let closed: usize = threads.into_iter().map(|t| t.join().unwrap()).sum();
        assert!(closed >= 64, "round {round}");
        let mut pending = u32::MAX;
        assert_eq!(
            unsafe { api().arena_collect.unwrap()(HOST_OWNER, host.control.get(), &mut pending) },
            KILN_OK
        );
        assert_eq!(pending as usize, host.retained.borrow().len(), "round {round}");
    }
    assert!(host.shared().wakes.load(Ordering::SeqCst) > 0);
    // arena_close from another thread while the worker keeps calling
    let control = host.control.get();
    let closer = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(5));
        let mut pending = 0u32;
        unsafe { api().arena_close.unwrap()(HOST_OWNER, control, &mut pending) }
    });
    let mut revoked = false;
    for _ in 0..1_000_000 {
        match read(b"text") {
            Ok(reply) => host.close(reply.payload),
            Err(rc) => {
                assert_eq!(rc, KILN_ERR_REVOKED);
                revoked = true;
                break;
            }
        }
    }
    assert_eq!(closer.join().unwrap(), KILN_OK);
    assert!(revoked || read(b"text").err() == Some(KILN_ERR_REVOKED));
    let mut pending = u32::MAX;
    assert_eq!(
        unsafe { api().arena_collect.unwrap()(HOST_OWNER, control, &mut pending) },
        KILN_OK
    );
    assert_eq!(pending, 0, "the worker's collect tears the arena down");
    host.assert_balanced();
    assert_eq!(unsafe { api().value_close.unwrap()(OWNER_B, root) }, KILN_OK);
}

// ---- the C consumer -------------------------------------------------------------------------------------------------

#[repr(C)]
struct ConsumerArgs {
    size: u32,
    reserved: u32,
    api: *const KilnEmlValuesApiV1,
    view: u64,
    closed_view: u64,
    mismatch_view: u64,
    bare_view: u64,
    owner: u64,
    log: *mut u8,
    log_capacity: usize,
    checks: u32,
    failures: u32,
}

type ConsumerRun = unsafe extern "C" fn(*mut ConsumerArgs) -> i32;

#[cfg(unix)]
fn load(path: &std::path::Path) -> ConsumerRun {
    unsafe extern "C" {
        fn dlopen(filename: *const std::ffi::c_char, flag: i32) -> *mut c_void;
        fn dlsym(handle: *mut c_void, symbol: *const std::ffi::c_char) -> *mut c_void;
    }
    let name = std::ffi::CString::new(path.to_str().unwrap()).unwrap();
    let module = unsafe { dlopen(name.as_ptr(), 2) };
    assert!(!module.is_null(), "dlopen {}", path.display());
    let symbol = unsafe { dlsym(module, c"m6_values_consumer_run".as_ptr()) };
    assert!(!symbol.is_null());
    unsafe { std::mem::transmute::<*mut c_void, ConsumerRun>(symbol) }
}

#[cfg(windows)]
fn load(path: &std::path::Path) -> ConsumerRun {
    use windows::{
        Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW},
        core::{HSTRING, s},
    };
    let module = unsafe { LoadLibraryW(&HSTRING::from(path.as_os_str())) }.expect("LoadLibraryW");
    let symbol = unsafe { GetProcAddress(module, s!("m6_values_consumer_run")) }.expect("GetProcAddress");
    unsafe { std::mem::transmute::<unsafe extern "system" fn() -> isize, ConsumerRun>(symbol) }
}

/// The consumer libraries: `M6_VALUES_CONSUMERS` (paths, `;`-separated; also kiln-sfi's `values_consumer` example),
/// else built here from tests/c/m6_values_consumer.c as C11 and as C++20 (`$CC`/`$CXX` on Unix, MSVC on Windows:
/// support/c_consumer).
fn consumers() -> Vec<std::path::PathBuf> {
    if let Ok(list) = std::env::var("M6_VALUES_CONSUMERS") {
        return list.split(';').filter(|p| !p.is_empty()).map(Into::into).collect();
    }
    c_consumer::build("m6_values_consumer", "M6_VALUES_CONSUMERS")
}

// Red-first: a C consumer, compiled as C11 and as C++20 against the MIT header only, drives the real table: refusals,
// records, every member's fixed reply, packets, threads, a reader without wake, and the drain (its own checks, counted).
#[test]
fn c_consumer_drives_the_real_table() {
    let base = registry();
    let (view, _) = view_of(&base, Some(registry()));
    let (closed_view, _) = view_of(&base, Some(registry()));
    assert_eq!(unsafe { archive().release.unwrap()(closed_view) }, KILN_OK);
    let (mismatch_view, _) = view_of(&base, Some(reg::registry()));
    let (bare_view, _) = view_of(&base, None);
    let libraries = consumers();
    assert_eq!(libraries.len(), 2, "C11 and C++20");
    for library in libraries {
        let run = load(&library);
        let mut log = vec![0u8; 16 * 1024];
        let mut args = ConsumerArgs {
            size: std::mem::size_of::<ConsumerArgs>() as u32,
            reserved: 0,
            api: api(),
            view,
            closed_view,
            mismatch_view,
            bare_view,
            owner: HOST_OWNER,
            log: log.as_mut_ptr(),
            log_capacity: log.len(),
            checks: 0,
            failures: 0,
        };
        // each library runs on a fresh thread (its worker); its own statics hold its object store
        let at = &mut args as *mut ConsumerArgs as usize;
        let failures = std::thread::scope(|scope| {
            scope
                .spawn(move || unsafe { run(at as *mut ConsumerArgs) })
                .join()
                .unwrap()
        });
        let text = String::from_utf8_lossy(&log[..log.iter().position(|b| *b == 0).unwrap_or(0)]).into_owned();
        println!(
            "{}: {} checks, {} failures\n{text}",
            library.display(),
            args.checks,
            args.failures
        );
        assert_eq!((failures, args.failures), (0, 0), "{}: {text}", library.display());
        assert!(args.checks >= 80, "{} ran {} checks", library.display(), args.checks);
    }
}
