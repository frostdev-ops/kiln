// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln.eml.archive v1's read group (resource_json, find_resource, list_resources, read_resource,
//! content_read_range, list_contents), called as a native consumer does: through the exported early getter and the
//! generated kiln-abi bindings of native/core/abi/services/kiln_eml_archive.h, never the provider's Rust internals.
//! The startup controller side is the GPL Rust API the startup controller and the plugin call (`ArchiveBacking::open`, `authorize_with_archive`).
//!
//! The archives are real: the synthetic fixture's synthetic KFC archive (written by EML's own writer) and its captured
//! baseline, a real the asset session SF-dialect pass over it whose prepared stage gives the committed generation's transaction, and
//! that generation's actual publication. A scalar archive with a hand-laid resource checks the lossless tree; hostile
//! archives are the same files truncated, corrupted or with index fields patched.
#[path = "support/m6_fixture/mod.rs"]
mod fixture;
#[path = "support/m6_reader/mod.rs"]
mod reader;
#[path = "support/c_consumer/mod.rs"]
mod c_consumer;

use fixture::{
    archive::{self, ArchiveFixture, STEM},
    types,
};
use kfc::{
    container::{KFCFile, KFCWriter},
    guid::{ContentHash, Guid, ResourceId},
    hash::fnv,
    reflection::{PrimitiveType as P, StructFieldMetadata, TypeFlags as F, TypeIndex, TypeMetadata, TypeRegistry},
};
use kiln_abi::{
    KILN_ERR_BUFFER_TOO_SMALL, KILN_ERR_INTERNAL, KILN_ERR_INVALID_ARGUMENT, KILN_ERR_NOT_FOUND, KILN_ERR_REVOKED,
    KILN_ERR_UNAVAILABLE, KILN_OK, KilnStr,
    services::{kiln_assets::*, kiln_caps::*, kiln_eml_archive::*, kiln_eml_values::*},
};
use kiln_eml::{
    archive_service::{self, ArchiveBacking, Grant, NamedIndex, Phase, RegistrySnapshot},
    archive_values::abi as values,
    sf_startup::{SelectedTarget, TypeSession, game, game::assets::AssetSession},
};
use mlua::Lua;
use mod_loader::lua::startup_host::StartupHost;
use reader::{HOST_OWNER, Host, open_record};
use serde_json::Value as Json;
use std::{
    collections::BTreeMap,
    ffi::c_void,
    fs,
    io::{Seek, SeekFrom, Write},
    path::Path,
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
};

const OWNER: u64 = 0x51;
const OTHER: u64 = 0x52;

/// Generations are process-global in the provider: every test takes its own numbers.
static NEXT: AtomicU64 = AtomicU64::new(40_000);
fn next() -> u64 {
    NEXT.fetch_add(1, Ordering::Relaxed)
}

fn api() -> &'static KilnEmlArchiveApiV1 {
    let table = archive_service::kiln_eml_archive_service(1);
    assert!(!table.is_null());
    unsafe { &*table }
}

fn grant(phase: Phase, owners: &[u64]) -> Grant {
    Grant {
        phase,
        launch_id: [0x11; 16],
        plan_id: [0x22; 16],
        generation: next(),
        reload_epoch: 5,
        plan_sha256: [0x33; 32],
        archive_sha256: [0x44; 32],
        prepared_owners: owners.to_vec(),
    }
}

/// The fixture's captured baseline (StartupHost's reference): `<stem>.kfc.bak` and `<stem>.kfc_resources.bak`.
fn baseline(f: &ArchiveFixture) -> Rc<KFCFile> {
    for ext in ["kfc", "kfc_resources"] {
        fs::copy(
            f.game.join(format!("{STEM}.{ext}")),
            f.game.join(format!("{STEM}.{ext}.bak")),
        )
        .unwrap();
    }
    Rc::new(KFCFile::from_path(f.game.join(format!("{STEM}.kfc.bak")), false).unwrap())
}

fn guard(f: &ArchiveFixture) -> fs::File {
    let file = fs::File::create(f.game.join(".kiln-eml.lock")).unwrap();
    file.try_lock().unwrap();
    file
}

/// A generation over `dir`'s archive with `transaction`, authorized with `registry` (the snapshot is built from it).
fn authorize_in(
    dir: &Path,
    registry: TypeRegistry,
    transaction: Vec<(ResourceId, Vec<u8>)>,
    grant: Grant,
    named: &[(NamedIndex, &[(&str, ResourceId)])],
) -> KilnEmlArchiveIdentityV1 {
    let mut backing = ArchiveBacking::open(dir, STEM, transaction).unwrap();
    for (kind, entries) in named {
        backing = backing.with_named_index(*kind, entries.iter().map(|(n, id)| (n.to_string(), *id)));
    }
    let registry = Arc::new(registry);
    let snapshot = RegistrySnapshot::new(&registry, backing.game_version());
    archive_service::authorize_with_archive(grant, snapshot, registry, Arc::new(backing)).unwrap()
}

/// A PUBLISHED generation of the fixture's baseline (no transaction) and a view of OWNER's.
fn published(f: &ArchiveFixture) -> (KilnEmlArchiveIdentityV1, u64) {
    let pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[],
    );
    (pins, acquire(OWNER, &pins))
}

fn acquire(owner: u64, pins: &KilnEmlArchiveIdentityV1) -> u64 {
    let mut view = 0;
    assert_eq!(unsafe { api().acquire.unwrap()(owner, pins, &mut view) }, KILN_OK);
    view
}

fn release(view: u64) {
    assert_eq!(unsafe { api().release.unwrap()(view) }, KILN_OK);
}

fn error_out() -> KilnArchiveErrorV1 {
    KilnArchiveErrorV1 {
        size: 32,
        result: -1,
        token: u64::MAX,
        message_bytes: u64::MAX,
        flags: 0,
        reserved: 0,
    }
}

/// What a refused call left: its result and error record.
#[derive(Clone, Copy)]
struct Refusal {
    rc: i32,
    error: KilnArchiveErrorV1,
}

impl std::fmt::Debug for Refusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "Refusal({}, record result {} token {} bytes {})",
            self.rc, self.error.result, self.error.token, self.error.message_bytes
        )
    }
}

/// `unwrap_err` for replies whose records have no Debug.
trait Refused {
    fn refused(self) -> Refusal;
}

impl<T> Refused for Result<T, Refusal> {
    fn refused(self) -> Refusal {
        match self {
            Ok(_) => panic!("expected a refusal"),
            Err(refusal) => refusal,
        }
    }
}

impl Refusal {
    fn plain(&self) -> i32 {
        assert_eq!(self.error.result, self.rc, "the record carries the result");
        assert_eq!(
            (self.error.token, self.error.message_bytes),
            (0, 0),
            "no diagnostic for {}",
            self.rc
        );
        self.rc
    }
    /// A genuine failure: its result and retained cause (copied, then released once).
    fn cause(&self, owner: u64) -> (i32, String) {
        assert_eq!(self.error.result, self.rc);
        assert_ne!(self.error.token, 0, "a genuine failure retains its cause");
        let text = copy_error(owner, self.error.token);
        assert_eq!(text.len() as u64, self.error.message_bytes);
        assert_eq!(
            unsafe { api().error_release.unwrap()(owner, self.error.token) },
            KILN_OK
        );
        (self.rc, String::from_utf8(text).unwrap())
    }
}

fn copy_error(owner: u64, token: u64) -> Vec<u8> {
    let copy = api().error_copy.unwrap();
    let mut required = 0usize;
    let mut probe = [0xeeu8; 1];
    let rc = unsafe { copy(owner, token, probe.as_mut_ptr().cast(), 0, &mut required) };
    assert!(
        rc == KILN_ERR_BUFFER_TOO_SMALL || (rc == KILN_OK && required == 0),
        "probe {rc}"
    );
    assert_eq!(probe[0], 0xee, "a probe writes nothing");
    let mut text = vec![0u8; required];
    assert_eq!(
        unsafe { copy(owner, token, text.as_mut_ptr().cast(), text.len(), &mut required) },
        KILN_OK
    );
    text
}

fn key(id: &ResourceId) -> KilnAssetKey {
    KilnAssetKey {
        guid: *id.guid().data(),
        r#type: id.type_hash(),
        part: id.part_index(),
    }
}

fn chash(hash: &ContentHash) -> KilnContentHash {
    KilnContentHash {
        size: hash.size(),
        hash0: hash.hash0(),
        hash1: hash.hash1(),
        hash2: hash.hash2(),
    }
}

fn info_out() -> KilnArchiveResourceInfo {
    KilnArchiveResourceInfo {
        size: 64,
        schema_version: 0xaaaa,
        key: KilnAssetKey {
            guid: [0xaa; 16],
            r#type: 0xaaaa_aaaa,
            part: 0xaaaa_aaaa,
        },
        source: 0xaaaa,
        reserved: 0xaaaa,
        type_index: u64::MAX,
        byte_length: u64::MAX,
        generation: u64::MAX,
    }
}

fn find(owner: u64, view: u64, id: &ResourceId) -> Result<KilnArchiveResourceInfo, Refusal> {
    let mut out = info_out();
    let mut e = error_out();
    let rc = unsafe { api().find_resource.unwrap()(owner, view, &key(id), &mut out, &mut e) };
    if rc == KILN_OK {
        assert_eq!((e.result, e.token, e.message_bytes), (KILN_OK, 0, 0));
        Ok(out)
    } else {
        assert_eq!(out.byte_length, u64::MAX, "a refusal leaves the record alone");
        Err(Refusal { rc, error: e })
    }
}

/// A byte-reply member, probed with capacity 0 first and then copied.
fn sized(call: impl Fn(*mut c_void, usize, *mut usize, *mut KilnArchiveErrorV1) -> i32) -> Result<Vec<u8>, Refusal> {
    let mut e = error_out();
    let mut required = usize::MAX;
    let mut sentinel = [0x5au8; 4];
    let rc = call(sentinel.as_mut_ptr().cast(), 0, &mut required, &mut e);
    if rc == KILN_OK {
        assert_eq!(required, 0, "only an empty reply fits capacity 0");
        return Ok(Vec::new());
    }
    if rc != KILN_ERR_BUFFER_TOO_SMALL {
        assert_eq!(required, usize::MAX, "a failure leaves required alone");
        return Err(Refusal { rc, error: e });
    }
    assert_eq!(
        (e.result, e.token, e.message_bytes),
        (KILN_ERR_BUFFER_TOO_SMALL, 0, 0),
        "a short buffer has no token"
    );
    assert_eq!(sentinel, [0x5a; 4], "a short buffer writes nothing");
    let mut data = vec![0xa5u8; required + 1];
    let mut again = 0usize;
    let rc = call(data.as_mut_ptr().cast(), data.len(), &mut again, &mut e);
    if rc != KILN_OK {
        return Err(Refusal { rc, error: e });
    }
    assert_eq!(again, required, "the copy answers the probe's size");
    assert_eq!(data[required], 0xa5, "nothing past the reply");
    data.truncate(required);
    Ok(data)
}

fn read(owner: u64, view: u64, id: &ResourceId) -> Result<Vec<u8>, Refusal> {
    let k = key(id);
    sized(|out, cap, req, e| unsafe { api().read_resource.unwrap()(owner, view, &k, out, cap, req, e) })
}

fn content(owner: u64, view: u64, hash: &KilnContentHash, start: u64, count: u64) -> Result<Vec<u8>, Refusal> {
    sized(|out, cap, req, e| unsafe {
        api().content_read_range.unwrap()(owner, view, hash, start, count, out, cap, req, e)
    })
}

fn kstr(text: &[u8]) -> KilnStr {
    KilnStr {
        ptr: text.as_ptr().cast(),
        len: text.len(),
    }
}

fn json_bytes(view: u64, query: &str) -> Result<Vec<u8>, Refusal> {
    let q = kstr(query.as_bytes());
    sized(|out, cap, req, e| unsafe { api().resource_json.unwrap()(view, q, out.cast(), cap, req, e) })
}

fn json(view: u64, query: &str) -> Result<Json, Refusal> {
    json_bytes(view, query).map(|bytes| serde_json::from_slice(&bytes).unwrap())
}

fn query(flags: i32, order: i32, id: &ResourceId, limit: u64) -> KilnArchiveQuery {
    let k = key(id);
    KilnArchiveQuery {
        size: 48,
        schema_version: 1,
        flags: flags as u32,
        order: order as u32,
        key: KilnAssetKey {
            guid: if flags & KILN_ARCHIVE_FILTER_GUID != 0 {
                k.guid
            } else {
                [0; 16]
            },
            r#type: if flags & KILN_ARCHIVE_FILTER_TYPE != 0 {
                k.r#type
            } else {
                0
            },
            part: if flags & KILN_ARCHIVE_FILTER_PART != 0 {
                k.part
            } else {
                0
            },
        },
        limit,
    }
}

/// list_resources, probed (capacity 0) and then listed; Ok is the keys.
fn list(owner: u64, view: u64, q: &KilnArchiveQuery, first: u64) -> Result<Vec<KilnAssetKey>, Refusal> {
    let call = api().list_resources.unwrap();
    let mut e = error_out();
    let mut required = usize::MAX;
    let rc = unsafe { call(owner, view, q, first, std::ptr::null_mut(), 0, &mut required, &mut e) };
    match rc {
        KILN_OK => {
            assert_eq!(required, 0);
            return Ok(Vec::new());
        }
        KILN_ERR_BUFFER_TOO_SMALL => {}
        _ => {
            assert_eq!(required, usize::MAX);
            return Err(Refusal { rc, error: e });
        }
    }
    let blank = KilnAssetKey {
        guid: [0xcc; 16],
        r#type: 0xcccc_cccc,
        part: 0xcccc_cccc,
    };
    // one short of the count: refused, nothing written
    let mut short = vec![blank; required.max(1)];
    let mut again = 0usize;
    let rc = unsafe {
        call(
            owner,
            view,
            q,
            first,
            short.as_mut_ptr(),
            required - 1,
            &mut again,
            &mut e,
        )
    };
    assert_eq!(
        (rc, again),
        (KILN_ERR_BUFFER_TOO_SMALL, required),
        "no capacity-driven partial page"
    );
    assert!(
        short.iter().all(|k| k.guid == [0xcc; 16]),
        "a short page writes nothing"
    );
    let mut keys = vec![blank; required + 1];
    let rc = unsafe { call(owner, view, q, first, keys.as_mut_ptr(), keys.len(), &mut again, &mut e) };
    assert_eq!((rc, again), (KILN_OK, required));
    assert_eq!(keys[required].guid, [0xcc; 16], "nothing past the count");
    keys.truncate(required);
    Ok(keys)
}

fn contents(owner: u64, view: u64, first: u64) -> Result<Vec<KilnContentHash>, Refusal> {
    let call = api().list_contents.unwrap();
    let mut e = error_out();
    let mut required = usize::MAX;
    let rc = unsafe { call(owner, view, first, std::ptr::null_mut(), 0, &mut required, &mut e) };
    match rc {
        KILN_OK => return Ok(Vec::new()),
        KILN_ERR_BUFFER_TOO_SMALL => {}
        _ => return Err(Refusal { rc, error: e }),
    }
    let mut out = vec![
        KilnContentHash {
            size: 0,
            hash0: 0,
            hash1: 0,
            hash2: 0
        };
        required
    ];
    let mut again = 0usize;
    let rc = unsafe { call(owner, view, first, out.as_mut_ptr(), out.len(), &mut again, &mut e) };
    assert_eq!((rc, again), (KILN_OK, required));
    Ok(out)
}

fn resource_query(id: &ResourceId) -> String {
    format!(
        "resource:{}/{:08x}/{}",
        id.guid().to_string().to_lowercase(),
        id.type_hash(),
        id.part_index()
    )
}

fn hexs(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn created_id() -> ResourceId {
    ResourceId::new(Guid::parse(archive::CREATED_GUID).unwrap(), fnv(types::ROOT), 0)
}

const NEW_ROOT: &str = r##"{child = {base = 1, extra = 2}, fixed = {3, 4}, children = {{base = 5, extra = 6}},
    maybe = {base = 9, extra = 10}, choice = {type = "m6.Child", value = {base = 7, extra = 8}}, text = "replaced"}"##;

/// A real the asset session SF-dialect pass over the fixture: part 3's data replaced and a new resource created, serialized into the
/// one stage and prepared. Returns the generation's transaction as the prepared stage holds it, then publishes it.
fn commit_pass(f: &ArchiveFixture) -> Vec<(ResourceId, Vec<u8>)> {
    let base = Rc::new(KFCFile::from_path(f.game.join(format!("{STEM}.kfc.bak")), false).unwrap());
    let guard = guard(f);
    let mut host = StartupHost::open(f.game.as_std_path(), STEM, f.types.clone(), base.clone(), &guard).unwrap();
    let lent = host.begin_stage().unwrap().lend();
    let lua = Lua::new();
    let types = TypeSession::new(&lua, f.types.clone(), SelectedTarget::from_index(STEM, &base));
    let assets = AssetSession::new(&types, base, host.reference_reader().unwrap(), Some(lent.writer()));
    let table = game::create(&lua, &types).unwrap();
    game::install_assets(&lua, &table, &assets).unwrap();
    lua.globals().set("game", table).unwrap();
    lua.load(format!(
        r##"
        local a = game.assets
        a.get_resource("{guid}", "m6.Root", 3).data = {NEW_ROOT}
        a.create_resource({NEW_ROOT}, "m6.Root", "{created}", 0)
        "##,
        guid = archive::RESOURCE_GUID,
        created = archive::CREATED_GUID,
    ))
    .exec()
    .unwrap();
    let (prepared, applied) = assets.prepare(lent).unwrap();
    assert_eq!(applied, 2);
    let transaction: Vec<(ResourceId, Vec<u8>)> = [archive::resource_id(3), created_id()]
        .into_iter()
        .map(|id| (id, prepared.read_resource(&id).unwrap().unwrap()))
        .collect();
    drop(lua);
    prepared.publish().unwrap();
    transaction
}

/// The bytes the fixture wrote for every part (vanilla).
fn vanilla() -> Vec<u8> {
    let registry = types::registry();
    let ty = registry
        .get_by_name(kfc::reflection::LookupKey::Qualified(types::ROOT))
        .unwrap();
    types::root_value().to_bytes(&registry, ty).unwrap()
}

/// The map entry `name` of a neutral struct tree.
fn field<'a>(tree: &'a Json, name: &str) -> &'a Json {
    assert_eq!(tree["tag"], "map", "{tree}");
    let key = format!("string:{}", hexs(name.as_bytes()));
    &tree["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["key"]["value"] == key.as_str())
        .unwrap_or_else(|| panic!("no field {name} in {tree}"))["value"]
}

fn u64_of(value: &Json) -> u64 {
    u64::from_str_radix(value["value"].as_str().unwrap().strip_prefix("u64:").unwrap(), 16).unwrap()
}

/// Optional raw replies for the schema check (out of band, AJV): written only when M6_ARCHIVE_REPLIES names a folder.
fn save(name: &str, bytes: &[u8]) {
    if let Some(dir) = std::env::var_os("M6_ARCHIVE_REPLIES") {
        let dir = std::path::PathBuf::from(dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join(format!("{name}.json")), bytes).unwrap();
    }
}

// ---- the table and its rows ----

#[test]
fn the_read_group_is_in_the_table_with_its_rows() {
    let table = api();
    assert!(table.resource_json.is_some() && table.find_resource.is_some() && table.list_resources.is_some());
    assert!(table.read_resource.is_some() && table.content_read_range.is_some() && table.list_contents.is_some());
    assert!(table.value_decode.is_none(), "codecs stay null");
    assert!(table.attribute_catalog.is_some(), "the attribute group is m6_archive_attributes'");
    static ROWS: Mutex<Vec<(String, u32)>> = Mutex::new(Vec::new());
    unsafe extern "C" fn report(_: u64, name: KilnStr, state: u32, _: u32, _: KilnStr) -> i32 {
        let name = unsafe { std::slice::from_raw_parts(name.ptr.cast::<u8>(), name.len) };
        ROWS.lock()
            .unwrap()
            .push((String::from_utf8(name.to_vec()).unwrap(), state));
        KILN_OK
    }
    let mut caps: KilnCapsApi = unsafe { std::mem::zeroed() };
    caps.header.size = std::mem::size_of::<KilnCapsApi>() as u32;
    caps.header.version = 1;
    caps.report = Some(report);
    assert_eq!(archive_service::publish(7, |_, _| KILN_OK, Some(&caps)), KILN_OK);
    let rows: BTreeMap<String, u32> = ROWS.lock().unwrap().iter().cloned().collect();
    assert_eq!(rows["eml.archive.read"], KILN_CAP_AVAILABLE as u32);
    for member in [
        "resource_json",
        "find_resource",
        "list_resources",
        "read_resource",
        "content_read_range",
        "list_contents",
    ] {
        assert_eq!(
            rows[&format!("eml.archive.read.{member}")],
            KILN_CAP_AVAILABLE as u32,
            "{member}"
        );
    }
    assert_eq!(rows["eml.archive.codec"], KILN_CAP_UNAVAILABLE as u32);
    assert_eq!(rows["eml.archive.attributes"], KILN_CAP_AVAILABLE as u32);
    assert_eq!(
        rows.len(),
        (1 + 4) + (1 + 2) + (1 + 6) + 1 + (1 + 3),
        "metadata, errors, read and attributes with their members, codec"
    );
}

// ---- generations: the old snapshot and the committed one ----

// The same executable sees an old generation's snapshot and the committed generation's values side by side: find/read
// include the transaction (TRANSACTION source, the staged bytes), lists stay on the reference catalog, a new
// publication never retargets an old view, and the old view still reads the reference after the publication.
#[test]
fn an_old_snapshot_and_the_committed_generation_read_their_own_values() {
    let f = ArchiveFixture::new("g10b-committed");
    let _ = baseline(&f);
    let (old_pins, old) = published(&f);
    let transaction = commit_pass(&f);
    let new_pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        transaction.clone(),
        grant(Phase::Published, &[]),
        &[],
    );
    let new = acquire(OWNER, &new_pins);
    let (edited, created) = (archive::resource_id(3), created_id());
    let vanilla = vanilla();

    // find: source, length, original type index and the view's generation
    let a = find(OWNER, old, &edited).unwrap();
    assert_eq!(
        (a.size, a.schema_version, a.source),
        (64, 1, KILN_ARCHIVE_SOURCE_FILE as u32)
    );
    assert_eq!(
        (a.byte_length, a.generation, a.reserved),
        (vanilla.len() as u64, old_pins.generation, 0)
    );
    assert_eq!(a.type_index, 8, "m6.Root's registry index");
    assert_eq!(
        (a.key.guid, a.key.r#type, a.key.part),
        (*edited.guid().data(), edited.type_hash(), 3)
    );
    let b = find(OWNER, new, &edited).unwrap();
    assert_eq!(b.source, KILN_ARCHIVE_SOURCE_TRANSACTION as u32);
    assert_eq!(
        (b.byte_length, b.generation),
        (transaction[0].1.len() as u64, new_pins.generation)
    );
    assert_eq!(find(OWNER, old, &created).refused().plain(), KILN_ERR_NOT_FOUND);
    assert_eq!(
        find(OWNER, new, &created).unwrap().source,
        KILN_ARCHIVE_SOURCE_TRANSACTION as u32
    );
    assert_eq!(
        find(OWNER, new, &archive::resource_id(9)).unwrap().source,
        KILN_ARCHIVE_SOURCE_FILE as u32
    );

    // read: exact bytes (the old view reads the reference after the publication replaced the live index)
    assert_eq!(read(OWNER, old, &edited).unwrap(), vanilla);
    assert_eq!(read(OWNER, new, &edited).unwrap(), transaction[0].1);
    assert_eq!(read(OWNER, new, &created).unwrap(), transaction[1].1);
    assert_eq!(read(OWNER, new, &archive::resource_id(9)).unwrap(), vanilla);
    assert_eq!(read(OWNER, old, &created).refused().plain(), KILN_ERR_NOT_FOUND);

    // resource_json: each generation's value and pins
    let old_json = json(old, &resource_query(&edited)).unwrap();
    let new_json = json(new, &resource_query(&edited)).unwrap();
    assert_eq!(u64_of(field(field(&old_json["value"], "child"), "base")), 7);
    assert_eq!(u64_of(field(field(&new_json["value"], "child"), "base")), 1);
    assert_eq!(
        old_json["pin"]["generation"],
        format!("u64:{:016x}", old_pins.generation)
    );
    assert_eq!(
        new_json["pin"]["generation"],
        format!("u64:{:016x}", new_pins.generation)
    );
    save("resource-committed-edited", &serde_json::to_vec(&new_json).unwrap());
    save("resource-old-snapshot", &serde_json::to_vec(&old_json).unwrap());
    let made = json(new, &resource_query(&created)).unwrap();
    save("resource-committed-created", &serde_json::to_vec(&made).unwrap());
    assert_eq!(made["resourceId"], format!("guid:{}", hexs(created.guid().data())));
    assert_eq!(
        json(old, &resource_query(&created)).refused().plain(),
        KILN_ERR_NOT_FOUND
    );

    // lists: the reference catalog only, the same for both
    let all = query(0, KILN_ARCHIVE_ORDER_CATALOG, &edited, 0);
    let listed: Vec<u32> = list(OWNER, new, &all, 0).unwrap().iter().map(|k| k.part).collect();
    assert_eq!(
        listed,
        list(OWNER, old, &all, 0)
            .unwrap()
            .iter()
            .map(|k| k.part)
            .collect::<Vec<_>>()
    );
    assert_eq!(listed.len(), 3, "the created resource is not a reference entry");
    assert_eq!(
        contents(OWNER, new, 0).unwrap().len(),
        archive::BLOBS.len(),
        "contents are the reference's"
    );
    release(old);
    release(new);
}

// ---- the LIVE backing (a cached or unchanged start) ----

/// registry_metadata's whole reply without its pin (pins differ per generation).
fn registry_page(view: u64) -> Json {
    let call = api().registry_metadata.unwrap();
    let mut required = 0usize;
    let mut e = error_out();
    assert_eq!(
        unsafe { call(view, 0, 1000, std::ptr::null_mut(), 0, &mut required, &mut e) },
        KILN_ERR_BUFFER_TOO_SMALL
    );
    let mut text = vec![0u8; required];
    assert_eq!(
        unsafe {
            call(
                view,
                0,
                1000,
                text.as_mut_ptr().cast(),
                text.len(),
                &mut required,
                &mut e,
            )
        },
        KILN_OK
    );
    let mut page: Json = serde_json::from_slice(&text).unwrap();
    page.as_object_mut().unwrap().remove("pin");
    page
}

// A view authorized over the live published archive (no staged entries: a cached start) serves the same
// registry_metadata, finds, reads, resource_json values, lists and contents as the staged generation of the same
// content; the live index's changed entries are its TRANSACTION entries, read from the files with the same checks.
#[test]
fn a_live_backing_serves_what_the_staged_backing_does() {
    let f = ArchiveFixture::new("g10b-live");
    let _ = baseline(&f);
    let transaction = commit_pass(&f);
    let staged_pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        transaction,
        grant(Phase::Published, &[]),
        &[],
    );
    let staged = acquire(OWNER, &staged_pins);
    let backing = ArchiveBacking::open_live(f.game.as_std_path(), STEM).unwrap();
    let registry = Arc::new(types::registry());
    let snapshot = RegistrySnapshot::new(&registry, backing.game_version());
    let live_pins =
        archive_service::authorize_with_archive(grant(Phase::Published, &[]), snapshot, registry, Arc::new(backing))
            .unwrap();
    let live = acquire(OWNER, &live_pins);
    assert_eq!(registry_page(live), registry_page(staged));
    let mut ids: Vec<ResourceId> = [0, 3, 9].into_iter().map(archive::resource_id).collect();
    ids.push(created_id());
    for id in &ids {
        let (a, b) = (find(OWNER, staged, id).unwrap(), find(OWNER, live, id).unwrap());
        assert_eq!(
            (a.source, a.byte_length, a.type_index),
            (b.source, b.byte_length, b.type_index),
            "{id:?}"
        );
        assert_eq!(
            read(OWNER, staged, id).unwrap(),
            read(OWNER, live, id).unwrap(),
            "{id:?}"
        );
        let (ja, jb) = (
            json(staged, &resource_query(id)).unwrap(),
            json(live, &resource_query(id)).unwrap(),
        );
        assert_eq!(
            (&ja["value"], &ja["type_index"]),
            (&jb["value"], &jb["type_index"]),
            "{id:?}"
        );
    }
    assert_eq!(
        find(OWNER, live, &archive::resource_id(3)).unwrap().source,
        KILN_ARCHIVE_SOURCE_TRANSACTION as u32
    );
    assert_eq!(
        find(OWNER, live, &archive::resource_id(0)).unwrap().source,
        KILN_ARCHIVE_SOURCE_FILE as u32
    );
    let all = query(0, KILN_ARCHIVE_ORDER_CATALOG, &ids[0], 0);
    let parts = |view| {
        list(OWNER, view, &all, 0)
            .unwrap()
            .iter()
            .map(|k| (k.guid, k.part))
            .collect::<Vec<_>>()
    };
    assert_eq!(parts(live), parts(staged));
    let hashes = |view| {
        contents(OWNER, view, 0)
            .unwrap()
            .iter()
            .map(|h| (h.size, h.hash0, h.hash1, h.hash2))
            .collect::<Vec<_>>()
    };
    assert_eq!(hashes(live), hashes(staged));
    release(staged);
    release(live);

    // the same hostile-input rules: a live chunk that claims more than its zstd frames produce is refused
    let created = created_id();
    let index_bytes = fs::read(f.game.join(format!("{STEM}.kfc"))).unwrap();
    let file = KFCFile::from_path(f.game.join(format!("{STEM}.kfc")), false).unwrap();
    let entry = file.resources().get(&created).unwrap().offset;
    let chunk = file
        .resource_chunks()
        .iter()
        .position(|c| c.uncompressed_offset <= entry && entry < c.uncompressed_offset + c.uncompressed_size)
        .unwrap();
    let (offset, count) = archive::table_location(&index_bytes, 15);
    assert!(chunk < count);
    let mut patched = index_bytes.clone();
    let at = offset + chunk * 20 + 16;
    patched[at..at + 4].copy_from_slice(&u32::MAX.to_le_bytes());
    fs::write(f.game.join(format!("{STEM}.kfc")), patched).unwrap();
    let backing = ArchiveBacking::open_live(f.game.as_std_path(), STEM).unwrap();
    let registry = Arc::new(types::registry());
    let snapshot = RegistrySnapshot::new(&registry, backing.game_version());
    let pins =
        archive_service::authorize_with_archive(grant(Phase::Published, &[]), snapshot, registry, Arc::new(backing))
            .unwrap();
    let view = acquire(OWNER, &pins);
    let (rc, text) = read(OWNER, view, &created).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_INTERNAL);
    assert!(text.contains("claims") && text.contains("zstd"), "{text}");
    // the reference catalog (unchanged chunks) still reads
    assert_eq!(read(OWNER, view, &archive::resource_id(0)).unwrap(), vanilla());
    release(view);
    // not an archive stem: refused before any file is read
    fs::write(f.game.join(format!("{STEM}.kfc")), index_bytes).unwrap();
    assert!(ArchiveBacking::open_live(f.game.as_std_path(), "Bad-Stem").is_err());
}

// ---- list_resources ----

#[test]
fn resource_lists_page_by_filtered_index_with_checked_query_records() {
    let f = ArchiveFixture::new("g10b-list");
    let _ = baseline(&f);
    let (_, view) = published(&f);
    let id = archive::resource_id(0);
    let both = KILN_ARCHIVE_FILTER_GUID | KILN_ARCHIVE_FILTER_TYPE;
    let parts = |q: &KilnArchiveQuery, first: u64| -> Result<Vec<u32>, i32> {
        list(OWNER, view, q, first)
            .map(|keys| keys.iter().map(|k| k.part).collect())
            .map_err(|r| r.plain())
    };
    // catalog order is the reference index's key order; PART order is ascending
    let catalog: Vec<u32> = f.index().resources().keys().iter().map(|k| k.part_index()).collect();
    assert_eq!(catalog.len(), 3);
    assert_ne!(
        catalog,
        [0, 3, 9],
        "the fixture's catalog order is not ascending, else PART order proves nothing"
    );
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_CATALOG, &id, 0), 0),
        Ok(catalog.clone())
    );
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 0), 0),
        Ok(vec![0, 3, 9])
    );
    assert_eq!(
        parts(&query(0, KILN_ARCHIVE_ORDER_CATALOG, &id, 0), 0),
        Ok(catalog.clone())
    );
    assert_eq!(
        parts(&query(KILN_ARCHIVE_FILTER_TYPE, KILN_ARCHIVE_ORDER_CATALOG, &id, 0), 0),
        Ok(catalog.clone())
    );
    let part3 = archive::resource_id(3);
    assert_eq!(
        parts(
            &query(KILN_ARCHIVE_FILTER_PART, KILN_ARCHIVE_ORDER_CATALOG, &part3, 0),
            0
        ),
        Ok(vec![3])
    );
    assert_eq!(
        parts(
            &query(both | KILN_ARCHIVE_FILTER_PART, KILN_ARCHIVE_ORDER_PART, &part3, 0),
            0
        ),
        Ok(vec![3])
    );
    let other = ResourceId::new(Guid::parse(archive::CREATED_GUID).unwrap(), fnv(types::ROOT), 0);
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_PART, &other, 0), 0),
        Ok(vec![]),
        "no match: empty"
    );
    // first indexes the filtered result; limit selects at most that many; first == total is empty
    assert_eq!(parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 0), 1), Ok(vec![3, 9]));
    assert_eq!(parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 1), 1), Ok(vec![3]));
    assert_eq!(parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 99), 2), Ok(vec![9]));
    assert_eq!(parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 0), 3), Ok(vec![]));
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, 0), 4),
        Err(KILN_ERR_INVALID_ARGUMENT)
    );
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, u64::MAX), 1),
        Err(KILN_ERR_INVALID_ARGUMENT),
        "first+limit overflows"
    );
    assert_eq!(
        parts(&query(both, KILN_ARCHIVE_ORDER_PART, &id, u64::MAX), 0),
        Ok(vec![0, 3, 9])
    );
    // malformed records
    let good = query(both, KILN_ARCHIVE_ORDER_PART, &id, 0);
    let bad = |edit: &dyn Fn(&mut KilnArchiveQuery)| {
        let mut q = good;
        edit(&mut q);
        parts(&q, 0)
    };
    assert_eq!(bad(&|q| q.size = 47), Err(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(bad(&|q| q.schema_version = 2), Err(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(bad(&|q| q.flags |= 8), Err(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(bad(&|q| q.order = 2), Err(KILN_ERR_INVALID_ARGUMENT));
    assert_eq!(
        bad(&|q| q.flags = KILN_ARCHIVE_FILTER_GUID as u32),
        Err(KILN_ERR_INVALID_ARGUMENT),
        "PART order needs GUID+TYPE"
    );
    assert_eq!(
        bad(&|q| q.key.part = 1),
        Err(KILN_ERR_INVALID_ARGUMENT),
        "an unselected field is zero"
    );
    let mut loose = query(0, KILN_ARCHIVE_ORDER_CATALOG, &id, 0);
    loose.key.r#type = 1;
    assert_eq!(parts(&loose, 0), Err(KILN_ERR_INVALID_ARGUMENT));
    // a larger record is accepted (and not written)
    #[repr(C)]
    #[derive(Clone, Copy)]
    struct Bigger {
        q: KilnArchiveQuery,
        tail: [u8; 16],
    }
    let mut big = Bigger {
        q: good,
        tail: [0x77; 16],
    };
    big.q.size = 64;
    assert_eq!(
        list(
            OWNER,
            view,
            unsafe { &*(&big as *const Bigger as *const KilnArchiveQuery) },
            0
        )
        .unwrap()
        .len(),
        3
    );
    assert_eq!(big.tail, [0x77; 16]);
    // element-count overflow and NULL data with capacity
    let mut e = error_out();
    let mut required = 7usize;
    let call = api().list_resources.unwrap();
    let mut one = [key(&id)];
    let rc = unsafe {
        call(
            OWNER,
            view,
            &good,
            0,
            one.as_mut_ptr(),
            usize::MAX / 2,
            &mut required,
            &mut e,
        )
    };
    assert_eq!(
        (rc, required),
        (KILN_ERR_INVALID_ARGUMENT, 7),
        "capacity * stride overflows"
    );
    let rc = unsafe { call(OWNER, view, &good, 0, std::ptr::null_mut(), 1, &mut required, &mut e) };
    assert_eq!(
        (rc, required, e.result),
        (KILN_ERR_INVALID_ARGUMENT, 7, KILN_ERR_INVALID_ARGUMENT)
    );
    let rc = unsafe { call(OWNER, view, &good, 0, one.as_mut_ptr(), 1, std::ptr::null_mut(), &mut e) };
    assert_eq!(rc, KILN_ERR_INVALID_ARGUMENT, "required is mandatory");
    release(view);
}

// ---- known sizes before IO, real IO for everything else ----

// A known length (the index's resource length, a content range's count) answers BUFFER_TOO_SMALL without opening a
// file, so it still answers with the data files gone; a real read (a count-zero one included) opens, checks and seeks,
// and fails with its retained cause.
#[test]
fn known_sizes_answer_before_io_and_every_read_is_a_real_source_operation() {
    let f = ArchiveFixture::new("g10b-io");
    let _ = baseline(&f);
    let (_, view) = published(&f);
    let id = archive::resource_id(0);
    let vanilla = vanilla();
    let blob = ContentHash::from_data(archive::BLOBS[1]);
    let hash = chash(&blob);
    // exact ranges first
    assert_eq!(
        content(OWNER, view, &hash, 0, blob.size() as u64).unwrap(),
        archive::BLOBS[1]
    );
    assert_eq!(content(OWNER, view, &hash, 3, 4).unwrap(), &archive::BLOBS[1][3..7]);
    assert_eq!(
        content(OWNER, view, &hash, blob.size() as u64, 0).unwrap(),
        b"",
        "zero at EOF"
    );
    let size = blob.size() as u64;
    for (start, count) in [(size + 1, 0), (0, size + 1), (size, 1), (1, size), (u64::MAX, 2)] {
        assert_eq!(
            content(OWNER, view, &hash, start, count).refused().plain(),
            KILN_ERR_INVALID_ARGUMENT
        );
    }
    let unknown = chash(&ContentHash::from_data(b"not in the archive"));
    assert_eq!(
        content(OWNER, view, &unknown, 0, 1).refused().plain(),
        KILN_ERR_NOT_FOUND
    );
    // the data files go away
    fs::remove_file(f.game.join(format!("{STEM}.kfc_resources"))).unwrap();
    let dat = f.game.join(format!(
        "{STEM}_{:03}.dat",
        f.index().contents().get(&blob).unwrap().container_index
    ));
    fs::remove_file(&dat).unwrap();
    let mut e = error_out();
    let mut required = 0usize;
    let rc =
        unsafe { api().read_resource.unwrap()(OWNER, view, &key(&id), std::ptr::null_mut(), 0, &mut required, &mut e) };
    assert_eq!(
        (rc, required, e.token),
        (KILN_ERR_BUFFER_TOO_SMALL, vanilla.len(), 0),
        "sized from the index"
    );
    let rc = unsafe {
        api().content_read_range.unwrap()(OWNER, view, &hash, 1, 5, std::ptr::null_mut(), 0, &mut required, &mut e)
    };
    assert_eq!((rc, required, e.token), (KILN_ERR_BUFFER_TOO_SMALL, 5, 0));
    let (rc, text) = read(OWNER, view, &id).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_INTERNAL);
    assert!(
        text.starts_with(&format!("could not open {STEM}.kfc_resources")),
        "{text}"
    );
    // count zero is not a sizing shortcut: the container is opened and fails
    let (rc, text) = content(OWNER, view, &hash, 0, 0).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_INTERNAL);
    assert!(text.starts_with("could not open") && text.contains(".dat"), "{text}");
    release(view);
}

// ---- resource_json ----

/// A registry with one struct of every fixed scalar shape plus an absent and a present optional and a binary string.
fn scalar_registry() -> TypeRegistry {
    fn t(index: usize, name: &str, p: P, size: u32) -> TypeMetadata {
        let qualified_name = format!("s.{name}");
        TypeMetadata {
            index: TypeIndex::new(index),
            name: name.into(),
            impact_name: name.into(),
            qualified_hash: fnv(&qualified_name),
            internal_hash: 0x5100 + index as u32,
            name_hash: fnv(name),
            impact_hash: fnv(name),
            qualified_name,
            namespace: vec!["s".into()],
            inner_type: None,
            size,
            alignment: 1,
            element_alignment: 1,
            field_count: 0,
            primitive_type: p,
            flags: F::NONE,
            struct_fields: Default::default(),
            enum_fields: Default::default(),
            default_value: None,
            attributes: Default::default(),
        }
    }
    let mut types = vec![
        t(0, "U8", P::UInt8, 1),
        t(1, "S8", P::SInt8, 1),
        t(2, "S16", P::SInt16, 2),
        t(3, "S32", P::SInt32, 4),
        t(4, "S64", P::SInt64, 8),
        t(5, "U64", P::UInt64, 8),
        t(6, "F32", P::Float32, 4),
        t(7, "F64", P::Float64, 8),
        t(8, "Bool", P::Bool, 1),
        t(9, "Mode", P::Enum, 1),
        t(10, "Guid", P::Guid, 16),
        t(11, "Ref", P::ObjectReference, 16),
        t(12, "MaybeU8", P::BlobOptional, 4),
        t(13, "Text", P::BlobString, 8),
        t(14, "Scalars", P::Struct, 96),
    ];
    types[9].inner_type = Some(TypeIndex::new(1));
    types[11].inner_type = Some(TypeIndex::new(14));
    types[12].inner_type = Some(TypeIndex::new(0));
    let fields = [
        ("u8", 0, 0),
        ("s8", 1, 1),
        ("s16", 2, 2),
        ("s32", 3, 4),
        ("s64", 4, 8),
        ("u64", 5, 16),
        ("f32", 6, 24),
        ("f64", 7, 32),
        ("bool", 8, 40),
        ("mode", 9, 41),
        ("guid", 10, 48),
        ("nil_guid", 10, 64),
        ("ref", 11, 0),
        ("none", 12, 80),
        ("some", 12, 84),
        ("text", 13, 88),
    ];
    for (name, index, offset) in fields {
        types[14].struct_fields.insert(
            name.into(),
            StructFieldMetadata {
                name: name.into(),
                r#type: TypeIndex::new(index),
                data_offset: offset,
                attributes: Default::default(),
            },
        );
    }
    types[14].field_count = fields.len() as u32;
    serde_json::from_value(serde_json::json!({"version": "s-1", "types": types})).unwrap()
}

/// The hand-laid Scalars resource: every field's exact bits.
fn scalar_bytes() -> Vec<u8> {
    let mut b = vec![0u8; 96];
    b[0] = 0xfe;
    b[1] = 0x80; // -128
    b[2..4].copy_from_slice(&(-2i16).to_le_bytes());
    b[4..8].copy_from_slice(&i32::MIN.to_le_bytes());
    b[8..16].copy_from_slice(&(-1i64).to_le_bytes());
    b[16..24].copy_from_slice(&u64::MAX.to_le_bytes());
    b[24..28].copy_from_slice(&0x7fc0_0001u32.to_le_bytes()); // a NaN with payload
    b[32..40].copy_from_slice(&(-0.0f64).to_bits().to_le_bytes());
    b[40] = 1;
    b[41] = 0xff; // enum -1
    for (i, v) in b[48..64].iter_mut().enumerate() {
        *v = i as u8 + 1;
    }
    // 64..80: a nil GUID; the reference field overlays bytes 0..16 (its GUID is those bytes); 80..84: none (absent)
    b[84..88].copy_from_slice(&12u32.to_le_bytes()); // some: its u8 at 84 + 12 = 96
    b[88..92].copy_from_slice(&9u32.to_le_bytes()); // text: its blob at 88 + 9 = 97, 3 bytes
    b[92..96].copy_from_slice(&3u32.to_le_bytes());
    b.push(0x2a);
    b.extend_from_slice(b"a\0b");
    b
}

fn scalar_id() -> ResourceId {
    ResourceId::new(
        Guid::parse("00112233-4455-6677-8899-aabbccddeeff").unwrap(),
        fnv("s.Scalars"),
        1,
    )
}

/// A game folder holding only `resources` over `registry` (and no contents), with its baseline.
fn custom_archive(tag: &str, registry: TypeRegistry, resources: &[(ResourceId, Vec<u8>)]) -> fixture::TestRoot {
    let root = fixture::TestRoot::new(tag);
    let game = root.0.join("game");
    fs::create_dir_all(&game).unwrap();
    fs::File::create(game.join(format!("{STEM}.kfc"))).unwrap();
    fs::File::create(game.join(format!("{STEM}.kfc_resources"))).unwrap();
    let types = Rc::new(registry);
    let mut writer = KFCWriter::<Rc<KFCFile>, _>::new(&game, STEM, types.clone(), "s-1").unwrap();
    for (id, bytes) in resources {
        writer.write_resource(id, bytes).unwrap();
    }
    writer.finalize().unwrap();
    for ext in ["kfc", "kfc_resources"] {
        fs::copy(
            game.join(format!("{STEM}.{ext}")),
            game.join(format!("{STEM}.{ext}.bak")),
        )
        .unwrap();
    }
    root
}

// resource_json is the versioned lossless envelope: the generation's pins, the key, the original type index and the
// neutral tree with every shape the bytes have (signedness, f32/f64 bits incl. NaN payload and -0.0, a nil GUID kept,
// an object reference, optional presence, binary strings, enum bits), each node's declared type.
#[test]
fn resource_json_is_the_lossless_envelope() {
    let root = custom_archive("g10b-scalars", scalar_registry(), &[(scalar_id(), scalar_bytes())]);
    let game = root.0.join("game");
    let pins = authorize_in(
        game.as_std_path(),
        scalar_registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[],
    );
    let view = acquire(OWNER, &pins);
    let bytes = json_bytes(view, &resource_query(&scalar_id())).unwrap();
    save("resource-scalars", &bytes);
    let reply: Json = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(reply["schema_version"], 1);
    assert_eq!(reply["resourceId"], format!("guid:{}", hexs(scalar_id().guid().data())));
    assert_eq!(reply["typeHash"], fnv("s.Scalars"));
    assert_eq!(reply["part"], 1);
    assert_eq!(reply["type_index"], "u64:000000000000000e");
    let pin = &reply["pin"];
    assert_eq!(pin["generation"], format!("u64:{:016x}", pins.generation));
    assert_eq!(pin["registry_id"], format!("u64:{:016x}", pins.registry_id));
    assert_eq!(pin["registry_sha256"], hexs(&pins.registry_sha256));
    let v = &reply["value"];
    assert_eq!(v["type"], "s.Scalars");
    let keys: Vec<&str> = v["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|e| e["key"]["tag"].as_str().unwrap())
        .collect();
    assert!(
        keys.iter().all(|t| *t == "string") && keys.len() == 16,
        "struct keys are strings in field order"
    );
    let first = &v["entries"][0]["key"]["value"];
    assert_eq!(first, &format!("string:{}", hexs(b"u8")));
    let tagged = |name: &str| {
        (
            field(v, name)["tag"].as_str().unwrap().to_owned(),
            field(v, name)["value"].clone(),
        )
    };
    assert_eq!(tagged("u8"), ("u64".into(), Json::from("u64:00000000000000fe")));
    assert_eq!(tagged("s8"), ("i64".into(), Json::from("i64:ffffffffffffff80")));
    assert_eq!(tagged("s16"), ("i64".into(), Json::from("i64:fffffffffffffffe")));
    assert_eq!(tagged("s32"), ("i64".into(), Json::from("i64:ffffffff80000000")));
    assert_eq!(tagged("s64"), ("i64".into(), Json::from("i64:ffffffffffffffff")));
    assert_eq!(tagged("u64"), ("u64".into(), Json::from("u64:ffffffffffffffff")));
    assert_eq!(tagged("f32"), ("f32".into(), Json::from("f32:7fc00001")));
    assert_eq!(tagged("f64"), ("f64".into(), Json::from("f64:8000000000000000")));
    assert_eq!(tagged("bool"), ("bool".into(), Json::from(true)));
    assert_eq!(tagged("mode"), ("u64".into(), Json::from("u64:ffffffffffffffff")));
    assert_eq!(field(v, "mode")["type"], "s.Mode");
    assert_eq!(
        tagged("guid"),
        ("guid".into(), Json::from("guid:0102030405060708090a0b0c0d0e0f10"))
    );
    assert_eq!(
        tagged("nil_guid"),
        ("guid".into(), Json::from(format!("guid:{}", "0".repeat(32))))
    );
    assert_eq!(tagged("ref").0, "reference");
    assert_eq!(
        tagged("ref").1,
        Json::from(format!("guid:{}", hexs(&scalar_bytes()[..16])))
    );
    assert_eq!(
        field(v, "none"),
        &serde_json::json!({"tag": "optional", "type": "s.MaybeU8", "present": false})
    );
    let some = field(v, "some");
    assert_eq!(
        (some["present"].clone(), some["value"]["type"].clone()),
        (Json::from(true), Json::from("s.U8"))
    );
    assert_eq!(some["value"]["value"], "u64:000000000000002a");
    assert_eq!(
        tagged("text"),
        ("string".into(), Json::from(format!("string:{}", hexs(b"a\0b"))))
    );
    assert_eq!(field(v, "s16")["type"], "s.S16");
    release(view);
}

// The fixture's Root: nested structs, a static array, a blob array, a present optional, a variant (its discriminant is
// the stored hash of the selected type, its child that type's map) and a string, against the vanilla value.
#[test]
fn resource_json_keeps_containers_variants_and_optionals() {
    let f = ArchiveFixture::new("g10b-root");
    let _ = baseline(&f);
    let (_, view) = published(&f);
    let bytes = json_bytes(view, &resource_query(&archive::resource_id(0))).unwrap();
    save("resource-root", &bytes);
    let v = &serde_json::from_slice::<Json>(&bytes).unwrap()["value"];
    assert_eq!(v["type"], "m6.Root");
    let child = field(v, "child");
    assert_eq!(
        (
            child["type"].clone(),
            u64_of(field(child, "base")),
            u64_of(field(child, "extra"))
        ),
        (Json::from("m6.Child"), 7, 11)
    );
    assert_eq!(
        field(child, "base")["type"],
        "m6.UInt32",
        "an inherited field keeps its type"
    );
    let fixed = field(v, "fixed");
    assert_eq!(
        (fixed["tag"].clone(), fixed["type"].clone()),
        (Json::from("array"), Json::from("m6.Fixed"))
    );
    assert_eq!(
        fixed["items"]
            .as_array()
            .unwrap()
            .iter()
            .map(u64_of)
            .collect::<Vec<_>>(),
        [2, 3]
    );
    let children = field(v, "children")["items"].as_array().unwrap();
    assert_eq!(
        children.iter().map(|c| u64_of(field(c, "base"))).collect::<Vec<_>>(),
        [13, 19]
    );
    let maybe = field(v, "maybe");
    assert_eq!(
        (maybe["tag"].clone(), maybe["present"].clone()),
        (Json::from("optional"), Json::from(true))
    );
    assert_eq!(u64_of(field(&maybe["value"], "extra")), 31);
    let choice = field(v, "choice");
    assert_eq!(
        (choice["tag"].clone(), choice["type"].clone()),
        (Json::from("variant"), Json::from("m6.Choice"))
    );
    assert_eq!(choice["discriminant"], format!("u64:{:016x}", fnv(types::CHILD)));
    assert_eq!(choice["value"]["type"], "m6.Child");
    assert_eq!(u64_of(field(&choice["value"], "extra")), 41);
    assert_eq!(field(v, "text")["value"], format!("string:{}", hexs(b"synthetic")));
    release(view);
}

// Queries: the header's grammar only; a missing resource NOT_FOUND; a named kind without its index UNAVAILABLE; with
// one, its names (skill hashes in any case); a generation without an archive UNAVAILABLE, never an empty success.
#[test]
fn resource_json_queries_named_indexes_and_missing_resources() {
    let f = ArchiveFixture::new("g10b-queries");
    let _ = baseline(&f);
    let (_, plain) = published(&f);
    let id = archive::resource_id(3);
    let pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[
            (NamedIndex::Item, &[("Sword_01", id)]),
            (NamedIndex::Skill, &[("0xDEADBEEF", archive::resource_id(9))]),
        ],
    );
    let named = acquire(OWNER, &pins);
    for bad in [
        "",
        "resource",
        "resource:",
        "names:items",
        "item:",
        "item:a..b",
        "item:a/b",
        "material:a.b",
        "skill:0xdeadbee",
        "resource:01234567-89AB-cdef-0123-456789abcdef/2a7b3c4d/0",
        "resource:01234567-89ab-cdef-0123-456789abcdef/2a7b3c4d/01",
        "resource:01234567-89ab-cdef-0123-456789abcdef/m6.Root/0",
    ] {
        assert_eq!(json(named, bad).refused().plain(), KILN_ERR_INVALID_ARGUMENT, "{bad:?}");
    }
    let nul = b"item:a\0b";
    let mut e = error_out();
    let mut required = 0usize;
    let rc = unsafe { api().resource_json.unwrap()(named, kstr(nul), std::ptr::null_mut(), 0, &mut required, &mut e) };
    assert_eq!(rc, KILN_ERR_INVALID_ARGUMENT);
    let missing = ResourceId::new(Guid::parse(archive::CREATED_GUID).unwrap(), fnv(types::ROOT), 0);
    assert_eq!(
        json(named, &resource_query(&missing)).refused().plain(),
        KILN_ERR_NOT_FOUND
    );
    // named kinds
    assert_eq!(
        json(named, "item:Sword_01").unwrap(),
        json(named, &resource_query(&id)).unwrap()
    );
    assert_eq!(json(named, "item:Sword_02").refused().plain(), KILN_ERR_NOT_FOUND);
    assert_eq!(json(named, "skill:0xdeadbeef").unwrap()["part"], 9);
    assert_eq!(json(named, "skill:0xDeadBeef").unwrap()["part"], 9);
    assert_eq!(json(named, "template:Anything").refused().plain(), KILN_ERR_UNAVAILABLE);
    assert_eq!(json(named, "material:Iron").refused().plain(), KILN_ERR_UNAVAILABLE);
    assert_eq!(
        json(plain, "item:Sword_01").refused().plain(),
        KILN_ERR_UNAVAILABLE,
        "no index in this generation"
    );
    // a generation without an archive
    let snapshot = RegistrySnapshot::new(&types::registry(), types::VERSION);
    let bare_pins =
        archive_service::authorize_with_registry(grant(Phase::Published, &[]), snapshot, Arc::new(types::registry()))
            .unwrap();
    let bare = acquire(OWNER, &bare_pins);
    assert_eq!(json(bare, &resource_query(&id)).refused().plain(), KILN_ERR_UNAVAILABLE);
    assert_eq!(find(OWNER, bare, &id).refused().plain(), KILN_ERR_UNAVAILABLE);
    assert_eq!(read(OWNER, bare, &id).refused().plain(), KILN_ERR_UNAVAILABLE);
    assert_eq!(
        list(OWNER, bare, &query(0, 0, &id, 0), 0).refused().plain(),
        KILN_ERR_UNAVAILABLE
    );
    assert_eq!(contents(OWNER, bare, 0).refused().plain(), KILN_ERR_UNAVAILABLE);
    let hash = chash(&ContentHash::from_data(archive::BLOBS[0]));
    assert_eq!(
        content(OWNER, bare, &hash, 0, 1).refused().plain(),
        KILN_ERR_UNAVAILABLE
    );
    for view in [plain, named, bare] {
        release(view);
    }
}

// The reply is materialized once per resource for the generation: the probe and the copy answer the same bytes even
// when the files change in between, and a reply never built before fails with its cause (captured once: two failing
// calls get two tokens over the same text).
#[test]
fn resource_json_is_materialized_once_and_its_failures_keep_their_cause() {
    let f = ArchiveFixture::new("g10b-materialize");
    let _ = baseline(&f);
    let (_, view) = published(&f);
    let q0 = resource_query(&archive::resource_id(0));
    let mut e = error_out();
    let mut required = 0usize;
    let call = api().resource_json.unwrap();
    let rc = unsafe {
        call(
            view,
            kstr(q0.as_bytes()),
            std::ptr::null_mut(),
            0,
            &mut required,
            &mut e,
        )
    };
    assert_eq!(rc, KILN_ERR_BUFFER_TOO_SMALL);
    fs::remove_file(f.game.join(format!("{STEM}.kfc_resources"))).unwrap();
    let mut text = vec![0u8; required];
    let mut again = 0usize;
    let rc = unsafe {
        call(
            view,
            kstr(q0.as_bytes()),
            text.as_mut_ptr().cast(),
            text.len(),
            &mut again,
            &mut e,
        )
    };
    assert_eq!((rc, again), (KILN_OK, required), "no second read after the probe");
    serde_json::from_slice::<Json>(&text).unwrap();
    // never built: the read fails, twice, each with its own token over the one cause
    let q9 = resource_query(&archive::resource_id(9));
    let first = json(view, &q9).refused();
    let second = json(view, &q9).refused();
    assert_ne!(first.error.token, second.error.token, "distinct tokens");
    let (rc1, text1) = first.cause(OWNER);
    let (rc2, text2) = second.cause(OWNER);
    assert_eq!((rc1, rc2), (KILN_ERR_INTERNAL, KILN_ERR_INTERNAL));
    assert_eq!(text1, text2);
    assert!(
        text1.contains(&format!("could not open {STEM}.kfc_resources")),
        "{text1}"
    );
    // the NULL record opts out of the detail, same result
    let rc = unsafe {
        call(
            view,
            kstr(q9.as_bytes()),
            std::ptr::null_mut(),
            0,
            &mut required,
            std::ptr::null_mut(),
        )
    };
    assert_eq!(rc, KILN_ERR_INTERNAL);
    release(view);
}

// ---- error records ----

// The record is checked before any lookup (a short, flagged or overlapping record is not written and causes no work,
// so a released view's REVOKED is never reached); overlapping outputs are refused with the record initialized; a
// token is copied any number of times without consuming it, released once (idempotently), then REVOKED; another
// owner's copy and release are REVOKED.
#[test]
fn error_records_are_checked_first_and_tokens_are_copied_and_released_once() {
    let f = ArchiveFixture::new("g10b-errors");
    let _ = baseline(&f);
    let (_, view) = published(&f);
    let (_, closed) = published(&f);
    release(closed);
    let id = archive::resource_id(0);
    let k = key(&id);
    let find_rc = |view: u64, e: *mut KilnArchiveErrorV1| {
        let mut out = info_out();
        unsafe { api().find_resource.unwrap()(OWNER, view, &k, &mut out, e) }
    };
    for edit in [
        (|e: &mut KilnArchiveErrorV1| e.size = 31) as fn(&mut KilnArchiveErrorV1),
        |e| e.flags = 1,
        |e| e.reserved = 1,
    ] {
        let mut e = error_out();
        edit(&mut e);
        let before = (e.size, e.result, e.token, e.message_bytes);
        assert_eq!(
            find_rc(closed, &mut e),
            KILN_ERR_INVALID_ARGUMENT,
            "checked before the lookup (else REVOKED)"
        );
        assert_eq!(
            (e.size, e.result, e.token, e.message_bytes),
            before,
            "an invalid record is not written"
        );
    }
    let mut e = error_out();
    assert_eq!(find_rc(closed, &mut e), KILN_ERR_REVOKED);
    assert_eq!((e.result, e.token), (KILN_ERR_REVOKED, 0));
    // a larger record keeps its size and tail
    #[repr(C)]
    struct Big {
        e: KilnArchiveErrorV1,
        tail: [u8; 8],
    }
    let mut big = Big {
        e: error_out(),
        tail: [0x99; 8],
    };
    big.e.size = 40;
    assert_eq!(find_rc(view, &mut big.e), KILN_OK);
    assert_eq!(
        (big.e.size, big.e.result, big.e.token, big.tail),
        (40, KILN_OK, 0, [0x99; 8])
    );
    // overlaps
    let mut buf = [0u8; 256];
    let call = api().read_resource.unwrap();
    let mut required = 0usize;
    let e_in_out = buf.as_mut_ptr().cast::<KilnArchiveErrorV1>();
    unsafe { e_in_out.write_unaligned(error_out()) };
    let rc = unsafe {
        call(
            OWNER,
            view,
            &k,
            buf.as_mut_ptr().cast(),
            buf.len(),
            &mut required,
            e_in_out,
        )
    };
    assert_eq!(rc, KILN_ERR_INVALID_ARGUMENT);
    assert_eq!(
        unsafe { e_in_out.read_unaligned() }.result,
        -1,
        "a record inside the output is not written"
    );
    let mut e = error_out();
    let req_in_out = unsafe { buf.as_mut_ptr().add(8) }.cast::<usize>();
    let rc = unsafe { call(OWNER, view, &k, buf.as_mut_ptr().cast(), buf.len(), req_in_out, &mut e) };
    assert_eq!(
        (rc, e.result, e.token),
        (KILN_ERR_INVALID_ARGUMENT, KILN_ERR_INVALID_ARGUMENT, 0)
    );
    let key_in_out = buf.as_ptr().cast::<KilnAssetKey>();
    let rc = unsafe {
        call(
            OWNER,
            view,
            key_in_out,
            buf.as_mut_ptr().cast(),
            buf.len(),
            &mut required,
            &mut e,
        )
    };
    assert_eq!(
        (rc, e.result),
        (KILN_ERR_INVALID_ARGUMENT, KILN_ERR_INVALID_ARGUMENT),
        "input inside the output"
    );
    // a real failure's token
    fs::remove_file(f.game.join(format!("{STEM}.kfc_resources"))).unwrap();
    let failed = read(OWNER, view, &id).refused();
    let token = failed.error.token;
    let text = copy_error(OWNER, token);
    assert_eq!(copy_error(OWNER, token), text, "copying does not consume");
    let mut short = vec![0x33u8; text.len() - 1];
    let mut need = 0usize;
    let copy = api().error_copy.unwrap();
    assert_eq!(
        unsafe { copy(OWNER, token, short.as_mut_ptr().cast(), short.len(), &mut need) },
        KILN_ERR_BUFFER_TOO_SMALL
    );
    assert_eq!(
        (need, short.iter().all(|b| *b == 0x33)),
        (text.len(), true),
        "a short copy writes nothing"
    );
    assert_eq!(
        unsafe { copy(OTHER, token, short.as_mut_ptr().cast(), short.len(), &mut need) },
        KILN_ERR_REVOKED
    );
    assert_eq!(unsafe { api().error_release.unwrap()(OTHER, token) }, KILN_ERR_REVOKED);
    assert_eq!(unsafe { api().error_release.unwrap()(OWNER, token) }, KILN_OK);
    assert_eq!(
        unsafe { api().error_release.unwrap()(OWNER, token) },
        KILN_OK,
        "idempotent"
    );
    assert_eq!(
        unsafe { copy(OWNER, token, short.as_mut_ptr().cast(), short.len(), &mut need) },
        KILN_ERR_REVOKED
    );
    // two failures at once: distinct tokens and texts (the content's container is gone too)
    let blob = ContentHash::from_data(archive::BLOBS[0]);
    let hash = chash(&blob);
    fs::remove_file(f.game.join(format!(
        "{STEM}_{:03}.dat",
        f.index().contents().get(&blob).unwrap().container_index
    )))
    .unwrap();
    let a = read(OWNER, view, &id).refused();
    let b = content(OWNER, view, &hash, 0, 0).refused();
    assert_ne!(a.error.token, b.error.token);
    let (ra, ta) = a.cause(OWNER);
    let (rb, tb) = b.cause(OWNER);
    assert_eq!((ra, rb), (KILN_ERR_INTERNAL, KILN_ERR_INTERNAL));
    assert!(ta.contains(".kfc_resources") && tb.contains(".dat"), "{ta} / {tb}");
    // the view's release reclaims a token nobody released
    let left = read(OWNER, view, &id).refused().error.token;
    release(view);
    assert_eq!(
        unsafe { copy(OWNER, left, short.as_mut_ptr().cast(), 0, &mut need) },
        KILN_ERR_REVOKED
    );
    assert_eq!(unsafe { api().error_release.unwrap()(OWNER, left) }, KILN_OK);
}

// ---- owners, views and generations ----

#[test]
fn owners_views_phases_and_retirement() {
    let f = ArchiveFixture::new("g10b-owners");
    let _ = baseline(&f);
    // owners of this test only: it revokes its owner, which closes that owner's views everywhere in the process
    const OWNER: u64 = 0x61;
    const OTHER: u64 = 0x62;
    let pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[],
    );
    let view = acquire(OWNER, &pins);
    let id = archive::resource_id(0);
    let hash = chash(&ContentHash::from_data(archive::BLOBS[0]));
    let all = query(0, 0, &id, 0);
    let every = |owner: u64, view: u64| -> Vec<i32> {
        vec![
            find(owner, view, &id).map(|_| KILN_OK).unwrap_or_else(|r| r.plain()),
            read(owner, view, &id).map(|_| KILN_OK).unwrap_or_else(|r| r.plain()),
            list(owner, view, &all, 0)
                .map(|_| KILN_OK)
                .unwrap_or_else(|r| r.plain()),
            content(owner, view, &hash, 0, 1)
                .map(|_| KILN_OK)
                .unwrap_or_else(|r| r.plain()),
            contents(owner, view, 0).map(|_| KILN_OK).unwrap_or_else(|r| r.plain()),
        ]
    };
    assert_eq!(every(OWNER, view), [KILN_OK; 5]);
    assert_eq!(
        every(OTHER, view),
        [KILN_ERR_REVOKED; 5],
        "another owner's token grants nothing"
    );
    assert_eq!(every(0, view), [KILN_ERR_INVALID_ARGUMENT; 5]);
    assert_eq!(
        every(OWNER, 0xdead_beef_0000),
        [KILN_ERR_INVALID_ARGUMENT; 5],
        "unknown view"
    );
    // resource_json has no owner argument: possession of the view is its owner's authority
    assert!(json(view, &resource_query(&id)).is_ok());
    // a second view of OTHER's own reads
    let theirs = acquire(OTHER, &pins);
    assert_eq!(every(OTHER, theirs), [KILN_OK; 5]);
    release(theirs);
    assert_eq!(every(OTHER, theirs), [KILN_ERR_REVOKED; 5]);
    assert_eq!(json(theirs, &resource_query(&id)).refused().plain(), KILN_ERR_REVOKED);
    // PREPARED: only the named owner acquires it, and reads
    let prepared = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        Vec::new(),
        grant(Phase::Prepared, &[OWNER]),
        &[],
    );
    let mut out = 0;
    assert_eq!(
        unsafe { api().acquire.unwrap()(OTHER, &prepared, &mut out) },
        KILN_ERR_UNAVAILABLE
    );
    let staged = acquire(OWNER, &prepared);
    assert_eq!(every(OWNER, staged), [KILN_OK; 5]);
    // retirement and owner revocation close views
    archive_service::retire(prepared.generation);
    assert_eq!(every(OWNER, staged), [KILN_ERR_REVOKED; 5]);
    archive_service::revoke_owner(OWNER);
    assert_eq!(every(OWNER, view), [KILN_ERR_REVOKED; 5]);
}

// ---- hostile archive bytes ----

/// The fixture's index file with one u32 of a table entry replaced (KFCHeader's documented layout).
fn patch_index(f: &ArchiveFixture, table: usize, entry: usize, width: usize, word: usize, value: u32) {
    let path = f.game.join(format!("{STEM}.kfc.bak"));
    let mut bytes = fs::read(&path).unwrap();
    let (offset, count) = archive::table_location(&bytes, table);
    assert!(entry < count);
    let at = offset + entry * width + word * 4;
    bytes[at..at + 4].copy_from_slice(&value.to_le_bytes());
    fs::write(&path, bytes).unwrap();
}

// Truncated or corrupted data files and index entries claiming more than the files hold are refused with an error
// record and a retained cause, never a panic, an abort or a short reply.
#[test]
fn hostile_archive_bytes_are_refused_with_error_records() {
    let id = archive::resource_id(0);
    let failure = |f: &ArchiveFixture, check: &dyn Fn(u64) -> Result<Vec<u8>, Refusal>| -> String {
        let (_, view) = published(f);
        let refusal = match check(view) {
            Ok(bytes) => panic!("{}: read {} bytes, expected a refusal", f.root.0, bytes.len()),
            Err(refusal) => refusal,
        };
        let (rc, text) = refusal.cause(OWNER);
        assert_eq!(rc, KILN_ERR_INTERNAL, "{text}");
        release(view);
        text
    };
    // a truncated resources file: the chunk lies past its end
    let f = ArchiveFixture::new("g10b-truncated");
    let _ = baseline(&f);
    let path = f.game.join(format!("{STEM}.kfc_resources"));
    let chunk = f.index().resource_chunks()[0].clone();
    let end = chunk.offset + chunk.compressed_size;
    assert!(fs::metadata(&path).unwrap().len() >= end);
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_len(end - 1)
        .unwrap();
    let text = failure(&f, &|view| read(OWNER, view, &id));
    assert!(text.contains("past the end") && text.contains("truncated"), "{text}");
    let text = failure(&f, &|view| json_bytes(view, &resource_query(&id)));
    assert!(text.contains("truncated"), "{text}");
    // corrupted compressed bytes
    let f = ArchiveFixture::new("g10b-corrupt");
    let _ = baseline(&f);
    let path = f.game.join(format!("{STEM}.kfc_resources"));
    let chunk = f.index().resource_chunks()[0].clone();
    let mut file = fs::OpenOptions::new().write(true).open(&path).unwrap();
    file.seek(SeekFrom::Start(chunk.offset)).unwrap();
    file.write_all(&[0u8; 4]).unwrap();
    drop(file);
    let text = failure(&f, &|view| read(OWNER, view, &id));
    assert!(text.contains("zstd") || text.contains("resource chunk"), "{text}");
    // a chunk that claims 4 GiB - 1 decompressed bytes (table 15, ResourceChunkInfo: 5 x u32, uncompressed size last)
    let f = ArchiveFixture::new("g10b-claim");
    let _ = baseline(&f);
    patch_index(&f, 15, 0, 20, 4, u32::MAX);
    let text = failure(&f, &|view| read(OWNER, view, &id));
    assert!(text.contains("claims") && text.contains("zstd"), "{text}");
    // a chunk whose zstd frames could write far more than its claim: one 128 KiB RLE block, then 2^23 one-byte
    // compressed blocks a decoder may expand to 128 KiB each (about 1 TiB, more than any machine here can reserve).
    // Refused before the reader reserves or decodes it; the reader's own reservations are infallible (an abort where
    // Windows cannot commit them). The compressed blocks are not valid, so a decoder that got there stops early.
    let f = ArchiveFixture::new("g10b-bomb");
    let _ = baseline(&f);
    let path = f.game.join(format!("{STEM}.kfc_resources"));
    let at = fs::metadata(&path).unwrap().len();
    let mut bomb = 0xfd2f_b528u32.to_le_bytes().to_vec();
    bomb.extend_from_slice(&[0x00, 0x40]); // no content size, a 256 KiB window
    bomb.extend_from_slice(&(1u32 << 1 | (128u32 << 10) << 3).to_le_bytes()[..3]);
    bomb.push(0x2a);
    let blocks = 1usize << 23;
    bomb.reserve(blocks * 4);
    for n in 0..blocks {
        let header = u32::from(n + 1 == blocks) | 2 << 1 | 1 << 3;
        bomb.extend_from_slice(&header.to_le_bytes()[..3]);
        bomb.push(0);
    }
    fs::OpenOptions::new()
        .append(true)
        .open(&path)
        .unwrap()
        .write_all(&bomb)
        .unwrap();
    patch_index(&f, 15, 0, 20, 0, u32::try_from(at).unwrap());
    patch_index(&f, 15, 0, 20, 2, u32::try_from(bomb.len()).unwrap());
    let text = failure(&f, &|view| read(OWNER, view, &id));
    assert!(text.contains("more than this process can reserve"), "{text}");
    // a resource that claims more bytes than its chunk holds (table 11, ResourceEntry: offset, size)
    let f = ArchiveFixture::new("g10b-oversize");
    let _ = baseline(&f);
    let index = f.index();
    let slot = index.resources().keys().iter().position(|k| *k == id).unwrap();
    patch_index(&f, 11, slot, 8, 1, 0x00ff_ffff);
    let (_, view) = published(&f);
    let mut e = error_out();
    let mut required = 0usize;
    let rc =
        unsafe { api().read_resource.unwrap()(OWNER, view, &key(&id), std::ptr::null_mut(), 0, &mut required, &mut e) };
    assert_eq!(
        (rc, e.token),
        (KILN_ERR_BUFFER_TOO_SMALL, 0),
        "the claim sizes the buffer, nothing more"
    );
    assert!(required >= 0x00ff_ffff);
    let (rc, text) = json(view, &resource_query(&id)).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_INTERNAL);
    assert!(text.contains("outside the archive's resource chunks"), "{text}");
    release(view);
    // a truncated container: the content range lies past its end
    let f = ArchiveFixture::new("g10b-dat");
    let _ = baseline(&f);
    let blob = ContentHash::from_data(archive::BLOBS[2]);
    let dat = f.game.join(format!(
        "{STEM}_{:03}.dat",
        f.index().contents().get(&blob).unwrap().container_index
    ));
    fs::OpenOptions::new()
        .write(true)
        .open(&dat)
        .unwrap()
        .set_len(1)
        .unwrap();
    let hash = chash(&blob);
    let text = failure(&f, &|view| content(OWNER, view, &hash, 1, 2));
    assert!(text.contains("past the end") && text.contains(".dat"), "{text}");
}

// ---- a resource type the registry lacks ----

// The generation's registry lacks the archive's resource type (a registry from another build): find_resource and
// resource_json answer UNAVAILABLE and, being SF's own refusal of the read ("resource type not found in current game
// registry", session/asset_resources.rs), keep that text behind a token; read_resource still returns the raw bytes
// (SF reads the bytes before it looks the type up).
#[test]
fn a_type_missing_from_the_registry_is_unavailable_with_its_cause() {
    // the scalar registry has none of the fixture's types
    let f = ArchiveFixture::new("g10b-notype");
    let _ = baseline(&f);
    let pins = authorize_in(
        f.game.as_std_path(),
        scalar_registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[],
    );
    let view = acquire(OWNER, &pins);
    let id = archive::resource_id(0);
    let (rc, text) = find(OWNER, view, &id).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_UNAVAILABLE);
    assert!(
        text.starts_with("resource type not found in current game registry"),
        "{text}"
    );
    let (rc, text) = json(view, &resource_query(&id)).refused().cause(OWNER);
    assert_eq!(rc, KILN_ERR_UNAVAILABLE);
    assert!(
        text.starts_with("resource type not found in current game registry"),
        "{text}"
    );
    assert_eq!(read(OWNER, view, &id).unwrap(), vanilla(), "the raw bytes need no type");
    release(view);
}

// ---- concurrency ----

// Readers on many threads over one view while the generation is retired under them (after some of their calls were
// answered, while they are still calling): every answer is a complete reply or REVOKED, and the first resource_json
// calls racing to materialize all answer the same bytes.
#[test]
fn concurrent_readers_survive_release_and_retirement() {
    let f = ArchiveFixture::new("g10b-threads");
    let _ = baseline(&f);
    /// Counts a reader thread as ended however it ends (a failed assertion included), so the retirer never waits on it.
    struct Ended<'a>(&'a AtomicU64);
    impl Drop for Ended<'_> {
        fn drop(&mut self) {
            self.0.fetch_add(1, Ordering::SeqCst);
        }
    }
    let revoked = AtomicU64::new(0);
    for round in 0..20 {
        let (pins, view) = published(&f);
        let vanilla = vanilla();
        let query = resource_query(&archive::resource_id([0, 3, 9][round % 3]));
        let replies = Mutex::new(Vec::new());
        let (answered, finished) = (AtomicU64::new(0), AtomicU64::new(0));
        std::thread::scope(|scope| {
            for t in 0..8 {
                let (vanilla, query, replies) = (&vanilla, &query, &replies);
                let (answered, finished, revoked) = (&answered, &finished, &revoked);
                scope.spawn(move || {
                    let _ended = Ended(finished);
                    for i in 0..20 {
                        let ok = match t % 3 {
                            0 => match json_bytes(view, query) {
                                Ok(bytes) => {
                                    replies.lock().unwrap().push(bytes);
                                    true
                                }
                                Err(r) => {
                                    assert_eq!(r.plain(), KILN_ERR_REVOKED);
                                    false
                                }
                            },
                            1 => match read(OWNER, view, &archive::resource_id(0)) {
                                Ok(bytes) => {
                                    assert_eq!(&bytes, vanilla);
                                    true
                                }
                                Err(r) => {
                                    assert_eq!(r.rc, KILN_ERR_REVOKED);
                                    false
                                }
                            },
                            _ => {
                                let hash = chash(&ContentHash::from_data(archive::BLOBS[i % 3]));
                                match content(OWNER, view, &hash, 0, hash.size as u64) {
                                    Ok(bytes) => {
                                        assert_eq!(bytes, archive::BLOBS[i % 3]);
                                        true
                                    }
                                    Err(r) => {
                                        assert_eq!(r.rc, KILN_ERR_REVOKED);
                                        false
                                    }
                                }
                            }
                        };
                        let counter = if ok { answered } else { revoked };
                        counter.fetch_add(1, Ordering::SeqCst);
                    }
                });
            }
            scope.spawn(|| {
                // retire mid-loop: once 16 of the 160 calls were answered (or, on a starved scheduler, all ended)
                while answered.load(Ordering::SeqCst) < 16 && finished.load(Ordering::SeqCst) < 8 {
                    std::thread::yield_now();
                }
                archive_service::retire(pins.generation);
            });
        });
        assert!(
            answered.load(Ordering::SeqCst) >= 16,
            "calls were answered before the retirement"
        );
        let replies = replies.into_inner().unwrap();
        assert!(replies.windows(2).all(|w| w[0] == w[1]), "one materialized reply");
        release(view);
    }
    assert!(
        revoked.load(Ordering::SeqCst) > 0,
        "the retirement overlapped readers still calling"
    );
}

// ---- kiln.eml.values RESOURCE roots ----

// RESOURCE opens through the kiln.eml.values C table read the arena's generation through the archive: the committed
// generation's root is the staged value, the old one's the vanilla value, and a missing resource is nil. A generation
// without an archive still answers UNAVAILABLE.
#[test]
fn values_resource_roots_read_the_archive_generation() {
    let f = ArchiveFixture::new("g10b-values");
    let _ = baseline(&f);
    let old_pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        Vec::new(),
        grant(Phase::Published, &[]),
        &[],
    );
    let transaction = commit_pass(&f);
    let new_pins = authorize_in(
        f.game.as_std_path(),
        types::registry(),
        transaction,
        grant(Phase::Published, &[]),
        &[],
    );
    let values = values::table();
    for (pins, base, text) in [(old_pins, 7, "synthetic"), (new_pins, 1, "replaced")] {
        let view = acquire(HOST_OWNER, &pins);
        let host = Host::new(values, view, KILN_VALUE_RUNTIME);
        let mut record = open_record(KILN_VALUE_OPEN_RESOURCE, b"", &[], 0);
        record.key = key(&archive::resource_id(3));
        let opened = host.call(|| host.open(&record)).unwrap();
        assert_ne!(opened.item, 0, "a node");
        let node = host.info(opened.item, 0).unwrap().identity.node_id;
        host.call(|| host.lua.globals().set("R", host.wrap(opened.item)).unwrap());
        host.run(&format!(
            "assert(R.child.base == {base}, R.child.base); assert(R.text == '{text}', R.text)"
        ));
        let mut again = open_record(KILN_VALUE_OPEN_RESOURCE, b"", &[], 0);
        again.key = key(&archive::resource_id(3));
        let second = host.call(|| host.open(&again)).unwrap();
        assert_eq!(
            host.info(second.item, 0).unwrap().identity.node_id,
            node,
            "the shared ResourceInfo root"
        );
        let mut absent = open_record(KILN_VALUE_OPEN_RESOURCE, b"", &[], 0);
        absent.key = key(&ResourceId::new(
            Guid::parse(archive::CREATED_GUID).unwrap(),
            fnv(types::ROOT),
            5,
        ));
        let nil = host.call(|| host.open(&absent)).unwrap();
        assert_ne!(nil.payload, 0, "a scalar reply");
        assert!(
            matches!(host.scalar(nil.payload).unwrap(), mlua::Value::Nil),
            "a missing resource's root is nil"
        );
        host.close(nil.item);
        drop(host);
        release(view);
    }
    let snapshot = RegistrySnapshot::new(&types::registry(), types::VERSION);
    let bare =
        archive_service::authorize_with_registry(grant(Phase::Published, &[]), snapshot, Arc::new(types::registry()))
            .unwrap();
    let view = acquire(HOST_OWNER, &bare);
    let host = Host::new(values, view, KILN_VALUE_RUNTIME);
    let mut record = open_record(KILN_VALUE_OPEN_RESOURCE, b"", &[], 0);
    record.key = key(&archive::resource_id(3));
    assert_eq!(host.call(|| host.open(&record)).err(), Some(KILN_ERR_UNAVAILABLE));
    drop(host);
    release(view);
}

// ---- a C consumer ----

#[repr(C)]
struct ConsumerArgs {
    size: u32,
    reserved: u32,
    api: *const KilnEmlArchiveApiV1,
    view: u64,
    closed_view: u64,
    bare_view: u64,
    broken_view: u64,
    owner: u64,
    other: u64,
    key: KilnAssetKey,
    missing: KilnAssetKey,
    resource_length: u64,
    content: KilnContentHash,
    content_count: u64,
    resource_count: u64,
    query: *const std::ffi::c_char,
    log: *mut u8,
    log_capacity: usize,
    checks: u32,
    failures: u32,
}

type ConsumerRun = unsafe extern "C" fn(*mut ConsumerArgs) -> i32;

#[cfg(unix)]
fn load(path: &Path) -> ConsumerRun {
    unsafe extern "C" {
        fn dlopen(filename: *const std::ffi::c_char, flag: i32) -> *mut c_void;
        fn dlsym(handle: *mut c_void, symbol: *const std::ffi::c_char) -> *mut c_void;
    }
    let name = std::ffi::CString::new(path.to_str().unwrap()).unwrap();
    let module = unsafe { dlopen(name.as_ptr(), 2) };
    assert!(!module.is_null(), "dlopen {}", path.display());
    let symbol = unsafe { dlsym(module, c"m6_archive_read_consumer_run".as_ptr()) };
    assert!(!symbol.is_null());
    unsafe { std::mem::transmute::<*mut c_void, ConsumerRun>(symbol) }
}

#[cfg(windows)]
fn load(path: &Path) -> ConsumerRun {
    use windows::{
        Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW},
        core::{HSTRING, s},
    };
    let module = unsafe { LoadLibraryW(&HSTRING::from(path.as_os_str())) }.expect("LoadLibraryW");
    let symbol = unsafe { GetProcAddress(module, s!("m6_archive_read_consumer_run")) }.expect("GetProcAddress");
    unsafe { std::mem::transmute::<unsafe extern "system" fn() -> isize, ConsumerRun>(symbol) }
}

/// The consumer libraries: `M6_ARCHIVE_READ_CONSUMERS` (paths, `;`-separated), else built here from
/// tests/c/m6_archive_read_consumer.c as C11 and as C++20 (`$CC`/`$CXX` on Unix, MSVC on Windows: support/c_consumer).
fn consumers() -> Vec<std::path::PathBuf> {
    if let Ok(list) = std::env::var("M6_ARCHIVE_READ_CONSUMERS") {
        return list.split(';').filter(|p| !p.is_empty()).map(Into::into).collect();
    }
    c_consumer::build("m6_archive_read_consumer", "M6_ARCHIVE_READ_CONSUMERS")
}

// Red-first: a C consumer, compiled as C11 and as C++20 against the MIT header only, drives the real read group:
// structural checks, record sizes, paging probes, exact bytes, error records copied and released once, refusals and
// concurrent readers (its own checks, counted).
#[test]
fn c_consumer_reads_through_the_real_table() {
    let f = ArchiveFixture::new("g10b-consumer");
    let _ = baseline(&f);
    let broken = ArchiveFixture::new("g10b-consumer-broken");
    let _ = baseline(&broken);
    fs::remove_file(broken.game.join(format!("{STEM}.kfc_resources"))).unwrap();
    let libraries = consumers();
    assert_eq!(libraries.len(), 2, "C11 and C++20");
    let vanilla = vanilla();
    let blob = ContentHash::from_data(archive::BLOBS[1]);
    let query = std::ffi::CString::new(resource_query(&archive::resource_id(0))).unwrap();
    for library in libraries {
        let (_, view) = published(&f);
        let (_, closed_view) = published(&f);
        release(closed_view);
        let snapshot = RegistrySnapshot::new(&types::registry(), types::VERSION);
        let bare = archive_service::authorize_with_registry(
            grant(Phase::Published, &[]),
            snapshot,
            Arc::new(types::registry()),
        )
        .unwrap();
        let bare_view = acquire(OWNER, &bare);
        let (_, broken_view) = published(&broken);
        let run = load(&library);
        let mut log = vec![0u8; 16 * 1024];
        let mut args = ConsumerArgs {
            size: std::mem::size_of::<ConsumerArgs>() as u32,
            reserved: 0,
            api: api(),
            view,
            closed_view,
            bare_view,
            broken_view,
            owner: OWNER,
            other: OTHER,
            key: key(&archive::resource_id(0)),
            missing: key(&created_id()),
            resource_length: vanilla.len() as u64,
            content: chash(&blob),
            content_count: archive::BLOBS.len() as u64,
            resource_count: 3,
            query: query.as_ptr(),
            log: log.as_mut_ptr(),
            log_capacity: log.len(),
            checks: 0,
            failures: 0,
        };
        let failures = unsafe { run(&mut args) };
        let text = String::from_utf8_lossy(&log[..log.iter().position(|b| *b == 0).unwrap_or(0)]).into_owned();
        println!(
            "{}: {} checks, {} failures\n{text}",
            library.display(),
            args.checks,
            args.failures
        );
        assert_eq!((failures, args.failures), (0, 0), "{}: {text}", library.display());
        assert!(args.checks >= 60, "{} ran {} checks", library.display(), args.checks);
        release(view);
        release(bare_view);
        release(broken_view);
    }
}
