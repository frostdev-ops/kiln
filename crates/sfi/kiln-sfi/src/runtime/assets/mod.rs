// SPDX-License-Identifier: MIT
// the interpreter's game.assets loaded-resource members.
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer)
// Sources (1.7.9 d8a394bc): modules/mod-runtime/lua/ingame/resources/{mod.rs:16-54,read_loaded_resources.rs},
// lua/bindings/game/assets/resource.rs (the Resource object), session/loaded_resources.rs:55-150 (ResourceInfo::loaded,
// loaded_snapshot_bytes), game-runtime/bridge/windows/resources/{read_resource.rs:4-50,find_resources_by_type.rs:4-48,
// check_resource_exists.rs:5-35,resolve_item_resource.rs:14-16}.
//! `game.assets`' loaded-resource members in kiln mode: `get_loaded_resource`, `get_loaded_resources_by_type`,
//! `read_loaded_field` and `resolve_loaded_resource`, on kiln.assets' `loaded_snapshot` and `loaded_list` (the
//! resources the game has loaded now, read from any thread on the exact builds).
//!
//! The provider side ([`Assets::read`], [`Assets::list`]) keeps ShroudForge's bridge: a 16 KiB first buffer and three
//! attempts for a snapshot, 256 keys and three attempts for a listing, its limits and its texts, with kiln.assets'
//! answers in place of its provider's (`KILN_LOADED_MISSING` is the missing GUID, `NOT_READY` the unavailable
//! service, `CHANGED` a read that moved). Without kiln.assets' members the two lookups raise ShroudForge's text for a
//! provider without the ResourceService extensions, as before this family.
//!
//! [`by_field`] has `get_loaded_resource_by_field` and `prepare_loaded_index` over the VM's field indexes ([`index`]),
//! which the hook-only family [`index_family`] advances once per worker turn.
//!
//! [`resource`] has the `Resource` object (ShroudForge's loaded-runtime Resource) and the members. The world session the
//! resources bind to is ShroudForge's `world_session_id`: the ECS world's live session
//! ([`crate::runtime::RuntimeFacts::ecs_world`]).

pub mod by_field;
pub mod index;
pub mod resource;

use super::availability::RuntimeFacts;
use super::context::RuntimeContext;
use super::ecs::fields::InlineField;
use super::install::Family;
use super::services::{ServiceAdapter, ServiceTable};
use super::types::{Registry, Session, TypesHandle};
use super::unavailable::{NO_RESOURCE_LISTING, NO_RESOURCE_READ};
use super::values::Values;
use super::write_fault::WriteOutcome;
use crate::service_member;
use kiln_abi::services::kiln_assets::{
    KILN_ASSETS_SERVICE, KILN_ASSETS_VERSION, KILN_ECS_CONFLICT, KILN_ECS_OK, KILN_ECS_UNCERTAIN,
    KILN_ECS_WRITE_WHY_TARGET_LATCHED, KILN_LOADED_CHANGED, KILN_LOADED_WRITE_NAME_MAX, KilnAssetKey,
    KilnAssetsApi, KilnLoadedInfo, KilnMutationReply,
};
use kiln_abi::{KILN_ERR_BUFFER_TOO_SMALL, KILN_ERR_NOT_FOUND, KILN_OK, KilnOwner, KilnStr};
use mlua::{Table, Value};
use std::cell::{Cell, RefCell, RefMut};
use std::collections::HashMap;
use std::rc::{Rc, Weak};

/// The family's name.
pub const FAMILY: &str = "assets-loaded";

/// The names this family adds to the package's `game.assets` and `runtime.overrides`.
pub const CLAIMS: [&str; 9] = [
    "game.assets.get_loaded_resource",
    "game.assets.get_loaded_resource_by_field",
    "game.assets.get_loaded_resources_by_type",
    "game.assets.prepare_loaded_index",
    "game.assets.read_loaded_field",
    "game.assets.resolve_loaded_resource",
    "game.assets.write_loaded_field",
    "runtime.overrides.get_resource_claim",
    "runtime.overrides.resource",
];

/// kiln.assets' capability row of the loaded-resource reads (kiln.caps; docs/kiln/services/caps.md).
pub const CAPABILITY: &str = "assets.loaded";
/// kiln.assets' capability row of the loaded-resource scalar writes.
pub const WRITE_CAPABILITY: &str = "assets.loaded_write";

/// ShroudForge's bridge texts (read_resource.rs, find_resources_by_type.rs, check_resource_exists.rs).
pub mod text {
    pub const RESOURCE_LIMIT: &str =
        "live ResourceService resource exceeded the 256 MiB safety limit";
    pub const RESOURCE_UNAVAILABLE: &str = "live ResourceService resource is unavailable";
    pub const RESOURCE_CHANGED: &str =
        "live ResourceService resource is unavailable or changed during retrieval";
    pub const RESOURCE_SIZE_CHANGING: &str =
        "live ResourceService resource size kept changing during retrieval";
    pub const LISTING_LIMIT: &str =
        "live ResourceService listing exceeded the resource safety limit";
    pub const LISTING_UNAVAILABLE: &str = "live ResourceService listing is unavailable";
    pub const LISTING_CHANGED: &str = "live ResourceService listing changed during retrieval";
    // update_resource_scalar.rs:13-75
    pub const WRITE_OUTSIDE: &str = "resource scalar lies outside the complete expected snapshot";
    pub const WRITE_SCALAR: &str =
        "resource writes require one naturally aligned 1/2/4/8-byte scalar inside the snapshot";
    pub const WRITE_SESSION: &str =
        "resource scalar update belongs to another or an inactive world session";
    pub const NO_WRITE: &str =
        "native provider lacks the session-bound ResourceService scalar write extension";
    pub const WRITE_CONFLICT: &str =
        "Resource scalar snapshot changed before update. Read current values and retry";
    pub const WRITE_FAILED: &str = "guarded resource scalar update failed";
}

/// ShroudForge's bounds: a resource of at most 256 MiB, a listing of at most 2^20 keys.
pub const RESOURCE_BYTES: usize = 256 * 1024 * 1024;
pub const LISTING_KEYS: usize = 1 << 20;
const FIRST_BUFFER: usize = 16 * 1024;
const FIRST_LISTING: usize = 256;

/// One snapshot read: the bytes, or the service's "no such key".
#[derive(Debug, PartialEq, Eq)]
pub enum Read {
    Bytes(Vec<u8>),
    Missing,
}

/// What the packages of one VM share: its type session, the adapter, kiln-sfi's owner, the VM's typed values,
/// ShroudForge's world session marker, the resource instances' ids and the inline field plans.
pub struct Assets {
    session: Rc<Session>,
    services: Rc<ServiceAdapter>,
    owner: KilnOwner,
    values: Option<Values>,
    world_session: Rc<dyn Fn() -> u64>,
    facts: Option<Rc<RuntimeFacts>>,
    instances: Cell<u64>,
    fields: RefCell<HashMap<(usize, String), Rc<InlineField>>>,
    indexes: RefCell<index::Indexes>,
    /// The packages that declared an index job, with their hosts ([`by_field::tick`] drops a stopped one's jobs).
    index_owners: RefCell<Vec<(String, Rc<dyn sfi_core::bindings::runtime::RuntimeHost>)>>,
}

/// The most inline field plans one VM keeps (as the ECS family's).
const FIELD_PLANS: usize = 4096;

impl Assets {
    pub fn new(
        session: Rc<Session>,
        services: Rc<ServiceAdapter>,
        owner: KilnOwner,
        values: Option<Values>,
        world_session: Rc<dyn Fn() -> u64>,
    ) -> Self {
        Self {
            session,
            services,
            owner,
            values,
            world_session,
            facts: None,
            instances: Cell::new(0),
            fields: RefCell::new(HashMap::new()),
            indexes: RefCell::new(index::Indexes::default()),
            index_owners: RefCell::new(Vec::new()),
        }
    }
    /// The VM's loaded-resource field indexes (ShroudForge's `loaded_resource_indexes`).
    pub fn indexes(&self) -> RefMut<'_, index::Indexes> {
        self.indexes.borrow_mut()
    }
    /// The same with the runtime-create's facts: the ownership store and the session write latch the writes use.
    pub fn with_facts(mut self, facts: Rc<RuntimeFacts>) -> Self {
        self.facts = Some(facts);
        self
    }
    pub fn facts(&self) -> Option<&Rc<RuntimeFacts>> {
        self.facts.as_ref()
    }
    /// The checked archive registry.
    pub fn types(&self) -> &Rc<Registry> {
        self.session.registry()
    }
    pub fn type_session(&self) -> &Rc<Session> {
        &self.session
    }
    pub fn values(&self) -> Option<&Values> {
        self.values.as_ref()
    }
    /// ShroudForge's `world_session_id`: the ECS world's live session, 0 without one.
    pub fn world_session(&self) -> u64 {
        (self.world_session)()
    }
    /// A new resource instance's id (the typed values' loaded-snapshot source instance).
    pub fn next_instance(&self) -> u64 {
        let id = self.instances.get() + 1;
        self.instances.set(id);
        id
    }

    /// The inline field plan of `path` in registry type `root`, kept per VM.
    pub fn inline_field(&self, root: usize, path: &str) -> Result<Rc<InlineField>, String> {
        if let Some(plan) = self.fields.borrow().get(&(root, path.to_owned())) {
            return Ok(plan.clone());
        }
        let plan = Rc::new(super::ecs::fields::inline_field(self.types(), root, path)?);
        let mut fields = self.fields.borrow_mut();
        if fields.len() < FIELD_PLANS {
            fields.insert((root, path.to_owned()), plan.clone());
        }
        Ok(plan)
    }

    fn table(&self) -> Option<ServiceTable<KilnAssetsApi>> {
        self.services
            .bind::<KilnAssetsApi>(KILN_ASSETS_SERVICE, KILN_ASSETS_VERSION)
            .ok()
    }

    /// The bridge's `resource_read` (read_resource.rs:4-50): one confirmed snapshot of `key`, or the service's
    /// "no such key"; Err with ShroudForge's text.
    pub fn read(&self, key: &KilnAssetKey) -> Result<Read, String> {
        let snapshot = self
            .table()
            .and_then(|table| service_member!(table, KilnAssetsApi, loaded_snapshot).ok())
            .ok_or(NO_RESOURCE_READ)?;
        let mut capacity = FIRST_BUFFER;
        for attempt in 0..3 {
            let mut bytes = vec![0u8; capacity];
            let mut required = 0usize;
            let mut info = info();
            // SAFETY: kiln_assets.h loaded_snapshot: the caller's key, a buffer of `capacity` bytes and records.
            let result = unsafe {
                self.services.call(
                    snapshot,
                    (
                        self.owner,
                        std::ptr::from_ref(key),
                        bytes.as_mut_ptr().cast::<core::ffi::c_void>(),
                        capacity,
                        &raw mut required,
                        &raw mut info,
                    ),
                )
            };
            if required > RESOURCE_BYTES {
                return Err(text::RESOURCE_LIMIT.into());
            }
            match result {
                KILN_OK if required <= capacity => {
                    bytes.truncate(required);
                    return Ok(Read::Bytes(bytes));
                }
                KILN_ERR_BUFFER_TOO_SMALL if required > capacity => {
                    if attempt == 2 {
                        break;
                    }
                    capacity = required;
                }
                KILN_ERR_NOT_FOUND => return Ok(Read::Missing),
                _ if info.status == KILN_LOADED_CHANGED as u32 => {
                    return Err(text::RESOURCE_CHANGED.into());
                }
                _ => return Err(text::RESOURCE_UNAVAILABLE.into()),
            }
        }
        Err(text::RESOURCE_SIZE_CHANGING.into())
    }

    /// The bridge's `resource_exists` (check_resource_exists.rs:5-35): whether the service has `key` now (a size query);
    /// Err when the service is unavailable, as ShroudForge raises it.
    pub fn exists(&self, key: &KilnAssetKey) -> Result<bool, String> {
        let snapshot = self
            .table()
            .and_then(|table| service_member!(table, KilnAssetsApi, loaded_snapshot).ok())
            .ok_or(NO_RESOURCE_READ)?;
        let mut required = 0usize;
        let mut info = info();
        // SAFETY: kiln_assets.h loaded_snapshot: a size query (no buffer, capacity 0) and the caller's records.
        let result = unsafe {
            self.services.call(
                snapshot,
                (
                    self.owner,
                    std::ptr::from_ref(key),
                    std::ptr::null_mut::<core::ffi::c_void>(),
                    0usize,
                    &raw mut required,
                    &raw mut info,
                ),
            )
        };
        match result {
            KILN_OK | KILN_ERR_BUFFER_TOO_SMALL if required > RESOURCE_BYTES => {
                Err(text::RESOURCE_LIMIT.into())
            }
            KILN_OK | KILN_ERR_BUFFER_TOO_SMALL => Ok(true),
            KILN_ERR_NOT_FOUND => Ok(false),
            // a lookup that moved: the service itself is up, so the resource is not found (the bridge's status check)
            _ if info.status == KILN_LOADED_CHANGED as u32 => Ok(false),
            _ => Err(text::RESOURCE_UNAVAILABLE.into()),
        }
    }

    /// The bridge's `resource_compare_exchange_scalar_for_session` (update_resource_scalar.rs:5-75) and the session's
    /// `compare_exchange_loaded_resource_scalar`: one scalar `desired` at `offset` of the complete snapshot `expected`,
    /// in `session`. An uncertain outcome latches the session's writes (the runtime-create's write fault). The write
    /// names `name` (sfi R3: kiln.assets' `loaded_scalar_compare_exchange_named`, since 0.2.0-dev.32), so Kiln's registry
    /// report names the package on the field it changed; an older kiln.assets without that member gets the unnamed write.
    pub fn compare_exchange(
        &self,
        key: &KilnAssetKey,
        expected: &[u8],
        offset: usize,
        desired: &[u8],
        session: u64,
        name: &WriteName,
    ) -> Result<(), String> {
        let end = offset
            .checked_add(desired.len())
            .filter(|end| *end <= expected.len())
            .ok_or(text::WRITE_OUTSIDE)?;
        let width = desired.len();
        if !matches!(width, 1 | 2 | 4 | 8) || !offset.is_multiple_of(width) || end > expected.len()
        {
            return Err(text::WRITE_SCALAR.into());
        }
        if session == 0 || self.world_session() != session {
            return Err(text::WRITE_SESSION.into());
        }
        let table = self.table().ok_or(text::NO_WRITE)?;
        let cas = service_member!(table, KilnAssetsApi, loaded_scalar_compare_exchange)
            .map_err(|_| text::NO_WRITE)?;
        let named = service_member!(table, KilnAssetsApi, loaded_scalar_compare_exchange_named).ok();
        // SAFETY: an all-zero reply is valid (integers only).
        let mut reply: KilnMutationReply = unsafe { std::mem::zeroed() };
        reply.size = std::mem::size_of::<KilnMutationReply>() as u32;
        let result = match named {
            // SAFETY: kiln_assets.h loaded_scalar_compare_exchange_named: the caller's key, snapshot, scalar, the two
            // names (checked text, borrowed for the call) and reply.
            Some(named) => unsafe {
                self.services.call(
                    named,
                    (
                        self.owner,
                        std::ptr::from_ref(key),
                        session,
                        expected.as_ptr().cast::<core::ffi::c_void>(),
                        expected.len(),
                        offset,
                        desired.as_ptr().cast::<core::ffi::c_void>(),
                        width as u32,
                        name.writer_str(),
                        name.field_str(),
                        &raw mut reply,
                    ),
                )
            },
            // SAFETY: kiln_assets.h loaded_scalar_compare_exchange: the caller's key, snapshot, scalar and reply.
            None => unsafe {
                self.services.call(
                    cas,
                    (
                        self.owner,
                        std::ptr::from_ref(key),
                        session,
                        expected.as_ptr().cast::<core::ffi::c_void>(),
                        expected.len(),
                        offset,
                        desired.as_ptr().cast::<core::ffi::c_void>(),
                        width as u32,
                        &raw mut reply,
                    ),
                )
            },
        };
        let outcome = match reply.status {
            // a refusal leaves the zeroed reply (KILN_ECS_OK): only the member's KILN_OK is a written scalar
            s if s == KILN_ECS_OK as u32 && result == KILN_OK => Ok(()),
            s if s == KILN_ECS_CONFLICT as u32 => Err(text::WRITE_CONFLICT.into()),
            // kiln.assets' writes were latched by an earlier uncertain write: nothing was attempted now, so this write
            // fails as the latched provider answers (its status 0) without a new session fault.
            s if s == KILN_ECS_UNCERTAIN as u32
                && reply.why_code == KILN_ECS_WRITE_WHY_TARGET_LATCHED as u32 =>
            {
                if let Some(facts) = &self.facts {
                    facts.write_faults().record_resource_latch();
                }
                Err(text::WRITE_FAILED.into())
            }
            s if s == KILN_ECS_UNCERTAIN as u32 => Err(self.report_uncertain(session)),
            _ => Err(text::WRITE_FAILED.into()),
        };
        // ShroudForge's compare_exchange_loaded_resource_scalar (loaded_resources.rs:18-46) on the outcome: a written
        // scalar requeues the field indexes it overlaps, a failure whose reason is uncertain every index; a refusal, a
        // conflict or a latched write leaves them
        match &outcome {
            Ok(()) => self
                .indexes()
                .invalidate_resource(key.r#type, offset, width),
            Err(reason) if reason.to_lowercase().contains("uncertain") => {
                self.indexes().invalidate()
            }
            Err(_) => {}
        }
        outcome
    }

    /// The session write latch's text for an uncertain loaded-resource write (latching it).
    fn report_uncertain(&self, session: u64) -> String {
        match &self.facts {
            Some(facts) => facts
                .write_faults()
                .report(
                    facts.ecs_world_kind(),
                    session,
                    WriteOutcome::ResourceWriteUncertain,
                )
                .to_owned(),
            None => super::write_fault::reason(WriteOutcome::ResourceWriteUncertain)
                .reason
                .to_owned(),
        }
    }

    /// The bridge's `resources_by_type` (find_resources_by_type.rs:4-48): every loaded key of `type_hash`.
    pub fn list(&self, type_hash: u32) -> Result<Vec<KilnAssetKey>, String> {
        let list = self
            .table()
            .and_then(|table| service_member!(table, KilnAssetsApi, loaded_list).ok())
            .ok_or(NO_RESOURCE_LISTING)?;
        let mut capacity = FIRST_LISTING;
        for attempt in 0..3 {
            let mut keys = vec![
                KilnAssetKey {
                    guid: [0; 16],
                    r#type: 0,
                    part: 0
                };
                capacity
            ];
            let mut required = 0usize;
            let mut info = info();
            // SAFETY: kiln_assets.h loaded_list: room for `capacity` keys and the caller's records.
            let result = unsafe {
                self.services.call(
                    list,
                    (
                        self.owner,
                        type_hash,
                        keys.as_mut_ptr(),
                        capacity,
                        &raw mut required,
                        &raw mut info,
                    ),
                )
            };
            if required > LISTING_KEYS {
                return Err(text::LISTING_LIMIT.into());
            }
            match result {
                KILN_OK if required <= capacity => {
                    keys.truncate(required);
                    return Ok(keys);
                }
                KILN_OK => return Err(text::LISTING_CHANGED.into()),
                KILN_ERR_BUFFER_TOO_SMALL if required > capacity => {
                    if attempt == 2 {
                        return Err(text::LISTING_CHANGED.into());
                    }
                    capacity = required;
                }
                _ => {
                    return Err(if attempt == 0 {
                        text::LISTING_UNAVAILABLE.into()
                    } else {
                        text::LISTING_CHANGED.into()
                    });
                }
            }
        }
        Err(text::LISTING_CHANGED.into())
    }
}

/// Who a loaded-resource write is for (sfi R3; kiln_assets.h named loaded writes): kiln.assets records it with the write,
/// and kiln.content's registry report names the package on the field it changed instead of "another writer".
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WriteName {
    writer: String,
    field: String,
}

impl WriteName {
    /// A package's write: writer `shroudforge:<package id>` (empty when that is not a plain id of at most
    /// [`NAME_MAX`] bytes: the write then names nobody), and the field path the package gave, cut to [`NAME_MAX`] bytes on a
    /// character boundary with control characters as `?` (diagnostics only: a name never refuses a write).
    pub fn package(package_id: &str, field: &str) -> Self {
        let writer = format!("shroudforge:{package_id}");
        let plain = !package_id.is_empty()
            && writer.len() <= NAME_MAX
            && package_id
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'));
        let mut text = String::new();
        for c in field.chars() {
            let c = if c.is_control() { '?' } else { c };
            if text.len() + c.len_utf8() > NAME_MAX {
                break;
            }
            text.push(c);
        }
        Self {
            writer: if plain { writer } else { String::new() },
            field: text,
        }
    }
    pub fn writer(&self) -> &str {
        &self.writer
    }
    pub fn field(&self) -> &str {
        &self.field
    }
    fn writer_str(&self) -> KilnStr {
        KilnStr {
            ptr: self.writer.as_ptr().cast(),
            len: self.writer.len(),
        }
    }
    fn field_str(&self) -> KilnStr {
        KilnStr {
            ptr: self.field.as_ptr().cast(),
            len: self.field.len(),
        }
    }
}

/// The longest writer or field name kiln.assets keeps (`KILN_LOADED_WRITE_NAME_MAX`).
pub const NAME_MAX: usize = KILN_LOADED_WRITE_NAME_MAX as usize;

fn info() -> KilnLoadedInfo {
    KilnLoadedInfo {
        size: std::mem::size_of::<KilnLoadedInfo>() as u32,
        status: 0,
        flags: 0,
        handle: 0,
        bytes: 0,
    }
}

/// The loaded reads' members (presence only): kiln.assets' loaded_snapshot and loaded_list.
pub fn probe(services: &ServiceAdapter) -> Result<(), super::services::ServiceError> {
    let table = services.bind::<KilnAssetsApi>(KILN_ASSETS_SERVICE, KILN_ASSETS_VERSION)?;
    service_member!(table, KilnAssetsApi, loaded_snapshot)?;
    service_member!(table, KilnAssetsApi, loaded_list)?;
    Ok(())
}

/// The loaded writes' members (presence only): the reads and kiln.assets' loaded_scalar_compare_exchange.
pub fn probe_writes(services: &ServiceAdapter) -> Result<(), super::services::ServiceError> {
    probe(services)?;
    let table = services.bind::<KilnAssetsApi>(KILN_ASSETS_SERVICE, KILN_ASSETS_VERSION)?;
    service_member!(table, KilnAssetsApi, loaded_scalar_compare_exchange)?;
    Ok(())
}

/// The VM's shared [`Assets`], as Lua app data.
struct VmAssets(Rc<Assets>);

/// The VM's shared [`Assets`] once the family installed one of its packages (`runtime.ecs.get_template` answers its
/// loaded resources with it); `None` before, or in a VM without the family.
pub fn of_vm(lua: &mlua::Lua) -> Option<Rc<Assets>> {
    lua.app_data_ref::<VmAssets>()
        .map(|assets| assets.0.clone())
}

/// Puts the members into one package's `game.assets` (made when the composition has none yet), and shares `assets`
/// with the VM ([`of_vm`]).
pub fn install_into(
    lua: &mlua::Lua,
    game: &Table,
    package_id: &str,
    host: Option<Rc<dyn sfi_core::bindings::runtime::RuntimeHost>>,
    assets: Rc<Assets>,
) -> mlua::Result<()> {
    lua.set_app_data(VmAssets(assets.clone()));
    let table = match game.raw_get::<Value>("assets")? {
        Value::Table(table) => table,
        Value::Nil => {
            let table = lua.create_table()?;
            game.raw_set("assets", &table)?;
            table
        }
        // something else holds the name: leave it as it is
        _ => return Ok(()),
    };
    resource::install(lua, &table, package_id, host.clone(), assets.clone())?;
    by_field::install(lua, &table, package_id, host, assets)
}

thread_local! {
    /// The worker's VMs' [`Assets`] whose field indexes [`index_family`]'s hook advances (a VM's go when it closes).
    static DRIVERS: RefCell<Vec<Weak<Assets>>> = const { RefCell::new(Vec::new()) };
}

/// Lets [`advance_indexes`] advance `assets`' field indexes on this thread while it lives.
pub fn drive(assets: &Rc<Assets>) {
    DRIVERS.with_borrow_mut(|drivers| {
        drivers.retain(|weak| weak.strong_count() > 0);
        if !drivers
            .iter()
            .any(|weak| weak.as_ptr() == Rc::as_ptr(assets))
        {
            drivers.push(Rc::downgrade(assets));
        }
    });
}

/// One runner round ([`by_field::tick`]) for every live VM's field indexes on this thread.
pub fn advance_indexes() {
    let live: Vec<Rc<Assets>> = DRIVERS.with_borrow_mut(|drivers| {
        drivers.retain(|weak| weak.strong_count() > 0);
        drivers.iter().filter_map(Weak::upgrade).collect()
    });
    for assets in live {
        by_field::tick(&assets);
    }
}

/// The index family's name.
pub const INDEX_FAMILY: &str = "assets-loaded-index";

/// ShroudForge's runner (update_ingame_runtime.rs:48-50, `advance_loaded_resource_indexes` after each update):
/// the worker's turn hook.
struct IndexRunner;

impl super::install::RuntimeHook for IndexRunner {
    fn turn(&self, _: &crate::worker::WorkerContext<'_>) {
        advance_indexes();
    }
    fn stopped(&self, _: &crate::worker::WorkerContext<'_>) {
        DRIVERS.with_borrow_mut(Vec::clear);
    }
}

/// The hook-only family that advances the field indexes once per worker turn. It claims no name; being hooked, a
/// reload keeps runtime-create's instance, and the VMs it drives register themselves ([`drive`]).
pub fn index_family() -> Family {
    Family::new(INDEX_FAMILY, &[], |_, _, _, _, _| Ok(())).with_hook(Rc::new(IndexRunner))
}

/// `runtime.overrides.get_resource_claim` and `runtime.overrides.resource` into one package's `runtime` table (a
/// package with the `runtime` capability), reading through its `game.assets`.
pub fn install_overrides(
    lua: &mlua::Lua,
    runtime: &Table,
    game: &Table,
    host: Rc<dyn sfi_core::bindings::runtime::RuntimeHost>,
    assets: Rc<Assets>,
) -> mlua::Result<()> {
    let assets_table: Table = game.raw_get("assets")?;
    super::overrides::targets::resource::install_into(lua, runtime, &assets_table, host, assets)
}

/// The production family on `types` (the wiring's handle). Every package gets the members in its `game.assets`
/// (ShroudForge installs them for every in-game package, with or without the `runtime` capability); the packages of
/// one VM share one [`Assets`].
pub fn family(types: TypesHandle) -> Family {
    let shared: RefCell<Vec<Rc<Assets>>> = RefCell::new(Vec::new());
    Family::new(
        FAMILY,
        &CLAIMS,
        move |lua, _, package, composed, cx: &RuntimeContext| {
            let session = types.session(lua)?;
            let known = shared
                .borrow()
                .iter()
                .find(|assets| Rc::ptr_eq(&assets.session, &session))
                .cloned();
            let assets = match known {
                Some(assets) => assets,
                None => {
                    let facts = cx.facts().clone();
                    let assets = Rc::new(
                        Assets::new(
                            session,
                            cx.services().clone(),
                            cx.owner(),
                            cx.values(),
                            // the ECS world's live session (`RuntimeFacts::ecs_world`): ShroudForge's world_session_id
                            Rc::new(move || facts.ecs_world().1),
                        )
                        .with_facts(cx.facts().clone()),
                    );
                    shared.borrow_mut().push(assets.clone());
                    drive(&assets);
                    assets
                }
            };
            install_into(
                lua,
                &composed.game,
                package.id(),
                cx.package_host(),
                assets.clone(),
            )?;
            match (&composed.runtime, cx.package_host()) {
                (Some(runtime), Some(host)) => {
                    install_overrides(lua, runtime, &composed.game, host, assets)
                }
                _ => Ok(()),
            }
        },
    )
}
