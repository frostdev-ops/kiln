// SPDX-License-Identifier: GPL-3.0-or-later
//! `game.assets` of the SF-dialect startup VM: the archive catalog and content functions (the asset session, first half).
//!
//! Adapted from ShroudForge 1.7.9 (MIT, Copyright (c) 2026 bonsaibauer; notice in sf_startup/mod.rs):
//! - modules/mod-runtime/lua/bindings/game/assets/mod.rs:20-40 (the table);
//! - modules/mod-runtime/lua/bindings/game/assets/access_file_resources.rs:2-121 (resource lookups and lists);
//! - modules/mod-runtime/lua/bindings/game/assets/access_file_content.rs:2-42 (content lookups and create_content);
//! - modules/mod-runtime/session/asset_resources.rs:143-155,184-209,288-317 (`kfc_file`, `reader`,
//!   `get_resource_info`, `get_content`, `create_content`);
//! - the asset session: access_file_resources.rs:123-152 (`create_resource`), session/asset_resources.rs:31-138,211-286
//!   (`get_lua_value`, `set_lua_value`, `apply`, `add_resource`, `create_resource`) and
//!   modules/mod-runtime/startup/commit_asset_changes.rs:5-36 (the dirty-resource serialization).
//!
//! One `AssetSession` per startup VM, shared by every package's `game.assets` table (each package gets its own
//! table, as SF builds one per mod): the reference index (the captured baseline), its reader, the one resource-record
//! cache and the staged-content copies. Writes go to the host's one stage, lent to the VM as a `StageWriter`
//! (`StagedArchive::lend`): never a second writer, a path or SF's own transaction. Without a stage the session is
//! PATCH-less (the in-game shape) and every write is refused before any work.
//!
//! Lookups follow SF, not EML:
//! - `get_content` and `get_all_contents` see the reference catalog only, so a content created in this pass is not
//!   found by lookup, although the Content `create_content` returned reads its own staged bytes (EML finds it);
//! - `get_all_resources`, `get_resources_by_type` and `get_resource_parts` enumerate reference keys only and reuse
//!   cached records; `get_resource` asks the cache first, so a resource created in this pass is found by key;
//! - `get_resource_parts` returns ascending parts (FINAL's selected compatibility rule; SF pushes storage order);
//! - `create_resource` refuses an identity the reference catalog OR the cache holds, an archive key nobody fetched
//!   included (an approved safety difference: both SF pins check their cache only), and its random
//!   overload draws until the identity is free of both.
//!
//! Resources are written once, when the pass ends: `AssetSession::prepare` closes the baseline reader, takes the
//! writer back from every Lua handle (`LentStage::prepare_with`), serializes every record whose value was replaced or
//! edited into that taken-back stage, in resource-id order (SF walks its cache map in hash order), and prepares it. A
//! serialization error drops the stage, which aborts it: the attempt is discarded before publication, as SF's commit
//! fails the start. Unread parts of an edited value are converted with the codec's bounded walk (`Converter::mapped`), not
//! kfc's recursive `Value::from`.
//!
//! Where SF panics, this raises a Lua error, none of it reachable in a normal pass:
//! - SF's `expect`s (an archive type missing from the context, the baseline reader closed) raise SF's text;
//! - a write after the stage was taken back (prepared or dropped), which only a finalizer or a coroutine resumed after
//!   the pass can attempt, raises `the staged archive writer is closed`; SF's `create_content` panics there with
//!   `KFCWriter no longer available`. `create_resource` and the `Resource.data` setter raise the same text then
//!   (SF accepts them into a cache it has already serialized, so they are lost silently);
//! - SF's `expect`/`unwrap` while serializing (a missing record type, kfc's encoder) fail the preparation with an
//!   error instead.
//!
//! The loaded-resource functions (`get_loaded_resource` and the rest) are not archive functions: the pass puts its
//! pregame answers on each package's table through [`AssetSession::create_table_with`] at ShroudForge's position (the attribute catalog,
//! `pregame_runtime::attach_loaded_resources`), never archive aliases. The session also holds the VM's attribute
//! catalog (the attribute catalog, [`AssetSession::attribute_catalog`]). The Buffer resource port is `sf_startup::buffer_resource`.
use std::{
    cell::{OnceCell, RefCell},
    collections::{HashMap, hash_map::Entry},
    rc::Rc,
    sync::atomic::{AtomicU64, Ordering},
};

use anyhow::Context as _;
use kfc::{
    container::{KFCCursor, KFCFile, KFCReader},
    guid::{ContentHash, ResourceId},
    reflection::{TypeIndex, TypeRegistry},
};
use mlua::Table;
use mod_loader::lua::startup_host::{LentStage, PreparedArchive, StageWriter, StagedArchive};
use sfi_core::{
    eml::buffer::Buffer,
    lua::{FunctionArgs, LuaError, LuaValue},
};

use super::{
    super::{
        TypeSession,
        attributes::Catalog,
        format_adapter,
        journal::{Journal, Recorder, ResourceOwner},
        journal_paths::{self, Paths},
        resource_values::{self, TypeHandle, ValueObjects, is_dirty_lua_value},
    },
    add_function,
    casts::{ContentHashArg, GuidArg, type_arg},
    content::Content,
    resource::{AssetIdentities, Resource, ResourceInfo, ResourceSource},
};

pub(crate) struct AssetInner {
    pub(crate) types: TypeSession,
    reference: Rc<KFCFile>,
    reader: RefCell<Option<KFCCursor<KFCReader>>>,
    /// Some exactly when the pass may patch (SF's PATCH feature): the lent stage's one writer.
    stage: Option<StageWriter>,
    resources: RefCell<HashMap<ResourceId, Rc<ResourceInfo>>>,
    new_contents: RefCell<HashMap<ContentHash, Vec<u8>>>,
    /// The wrappers' typed-object seam with this session's asset userdata (`AssetIdentities`): every value this session decodes,
    /// validates or converts goes through it.
    objects: ValueObjects,
    /// The reference archive's attribute catalog, built on first use and kept for the VM's life, its failure included
    /// (SF `AppState::attribute_catalog`'s OnceCell).
    attributes: OnceCell<Result<Rc<Catalog>, String>>,
    /// kiln-eml: the startup change journal's recorder: every resource value this session decodes, validates or
    /// copies carries its resource as owner (`resource_objects`), creations and contents are noted here.
    journal: Rc<Recorder>,
    /// kiln-eml (format 2 of the ShroudForge dialect): what each resource a package wrote held before the package that
    /// last changed it ran, as bytes (absent: its archive bytes), and the member paths each package changed there
    /// (`journal_paths.rs`; a resource is in `paths` once some package's writes into it were compared).
    before: RefCell<HashMap<ResourceId, Rc<[u8]>>>,
    paths: RefCell<HashMap<ResourceId, Paths>>,
}

/// A handle to the VM's asset session. Clones share it. Worker-local, like the type session.
#[derive(Clone)]
pub struct AssetSession(Rc<AssetInner>);

impl AssetSession {
    /// The session over `types`' registry, the reference index (the captured baseline), a reader of that baseline
    /// (`StartupHost::reference_reader`) and, for a patching pass, the lent stage's writer.
    pub fn new(
        types: &TypeSession,
        reference: Rc<KFCFile>,
        reader: KFCCursor<KFCReader>,
        stage: Option<StageWriter>,
    ) -> Self {
        Self::with_objects(
            types,
            reference,
            reader,
            stage,
            ValueObjects::with_assets(Rc::new(AssetIdentities)),
        )
    }

    /// `new` with the typed-object seam the caller already handed to the VM's Buffer resource port (the port is
    /// attached before any Lua access, so before this session exists). `objects` must answer this session's asset
    /// userdata (`ValueObjects::with_assets` over `AssetIdentities`).
    pub fn with_objects(
        types: &TypeSession,
        reference: Rc<KFCFile>,
        reader: KFCCursor<KFCReader>,
        stage: Option<StageWriter>,
        objects: ValueObjects,
    ) -> Self {
        Self(Rc::new(AssetInner {
            types: types.clone(),
            reference,
            reader: RefCell::new(Some(reader)),
            stage,
            resources: RefCell::new(HashMap::new()),
            new_contents: RefCell::new(HashMap::new()),
            objects,
            attributes: OnceCell::new(),
            journal: Rc::new(Recorder::default()),
            before: RefCell::new(HashMap::new()),
            paths: RefCell::new(HashMap::new()),
        }))
    }

    /// kiln-eml: `package`'s entrypoint begins; what the session is asked to change from now on is its own.
    pub fn begin_package(&self, package: &str) {
        self.0.journal.begin(package);
    }

    /// kiln-eml: the running entrypoint returned (or failed). Its resources are compared with what they held before it
    /// ran (format 2 of the ShroudForge dialect: the members it changed). No Lua runs here (the values are converted, not
    /// called).
    pub fn end_package(&self) {
        self.0.compare_running();
        self.0.journal.end();
    }

    /// kiln-eml: what each package's entrypoint changed, with the member paths it changed in each resource (format 2 of the
    /// ShroudForge dialect); nothing is noted afterwards.
    pub fn take_journal(&self) -> Journal {
        let journal = self.0.journal.take();
        let paths = self.0.paths.take();
        self.0.before.take();
        journal.with_member_paths(|bucket, id| paths.get(id).map(|p| p.of(bucket)))
    }

    /// SF `AppState::attribute_catalog` (session/mod.rs:123-136): the reference archive's attribute catalog, built once
    /// from the reference index and the baseline reader, its result (a failure too) kept for the VM's life. Building it
    /// runs no Lua. After the reader is closed an unbuilt catalog answers the closed-reader text and is not kept.
    pub fn attribute_catalog(&self) -> Result<Rc<Catalog>, String> {
        if let Some(catalog) = self.0.attributes.get() {
            return catalog.clone();
        }
        let built = {
            let mut reader = self.0.reader.borrow_mut();
            let Some(reader) = reader.as_mut() else {
                return Err("asset reader is closed after publication".into());
            };
            Catalog::load(self.0.types.registry(), &self.0.reference, |id| {
                reader.read_resource(id).map_err(|e| e.to_string())
            })
            .map(Rc::new)
        };
        self.0.attributes.get_or_init(|| built).clone()
    }

    /// The typed-object seam this session decodes, validates and converts values with (wrappers made from it answer
    /// Resource and Content userdata as GUID sources). The pass hands the same one to its Buffer resource port.
    pub fn objects(&self) -> ValueObjects {
        self.0.objects.clone()
    }

    /// The end of a patching pass (SF commit_asset_changes.rs:11-36, on Kiln's one stage): closes the baseline reader,
    /// takes the writer back from every Lua handle, serializes every replaced or edited record into the taken-back
    /// stage and prepares it. Returns the prepared generation (not published) and how many resources were written.
    /// An error drops the stage, which aborts it. The caller publishes; no Lua must run between here and there that
    /// could need the reader (a later reference read raises SF's closed-reader text). The VM must still be alive here:
    /// the records' values live in it (SF commits with its VM too, commit_asset_changes.rs:5).
    pub fn prepare<'guard>(&self, lent: LentStage<'guard>) -> anyhow::Result<(PreparedArchive<'guard>, usize)> {
        self.close_reader();
        let mut applied = 0;
        let prepared = lent.prepare_with(|stage| {
            applied = self.0.serialize(stage)?;
            Ok(())
        })?;
        Ok((prepared, applied))
    }

    /// Closes the baseline reader (SF closes its cursor before publication, which replaces the files it reads).
    /// Afterwards reference payload reads raise; metadata and staged copies stay available.
    pub fn close_reader(&self) {
        drop(self.0.reader.borrow_mut().take());
    }

    /// How many resource records the cache holds.
    pub fn cached_resources(&self) -> usize {
        self.0.resources.borrow().len()
    }

    /// This session's `game.assets` table for one package.
    pub fn create_table(&self, lua: &mlua::Lua) -> mlua::Result<Table> {
        self.create_table_with(lua, |_, _| Ok(()))
    }

    /// [`Self::create_table`] with `loaded` adding the loaded-resource functions where ShroudForge attaches them
    /// (game/assets/mod.rs:31: after `get_resources_by_type`, before `get_all_resources`).
    pub fn create_table_with(
        &self,
        lua: &mlua::Lua,
        loaded: impl FnOnce(&mlua::Lua, &Table) -> mlua::Result<()>,
    ) -> mlua::Result<Table> {
        let table = lua.create_table()?;
        let s = self.0.clone();
        add_function(lua, &table, "get_resource", move |_, args| s.lua_get_resource(&s, args))?;
        let s = self.0.clone();
        add_function(lua, &table, "get_resource_parts", move |lua, args| {
            s.lua_get_resource_parts(&s, lua, args)
        })?;
        let s = self.0.clone();
        add_function(lua, &table, "get_resources_by_type", move |lua, args| {
            s.lua_get_resources_by_type(&s, lua, args)
        })?;
        loaded(lua, &table)?;
        let s = self.0.clone();
        add_function(lua, &table, "get_all_resources", move |lua, _: FunctionArgs| {
            s.lua_get_all_resources(&s, lua)
        })?;
        let s = self.0.clone();
        add_function(lua, &table, "get_resource_types", move |lua, _: FunctionArgs| {
            s.lua_get_resource_types(lua)
        })?;
        let s = self.0.clone();
        add_function(lua, &table, "create_resource", move |lua, args| {
            s.lua_create_resource(&s, lua, args)
        })?;
        let s = self.0.clone();
        add_function(lua, &table, "get_content", move |_, args| s.lua_get_content(&s, args))?;
        let s = self.0.clone();
        add_function(lua, &table, "get_all_contents", move |lua, _: FunctionArgs| {
            s.lua_get_all_contents(&s, lua)
        })?;
        let s = self.0.clone();
        add_function(lua, &table, "create_content", move |_, args| {
            s.lua_create_content(&s, args)
        })?;
        Ok(table)
    }
}

impl AssetInner {
    /// The running entrypoint's resources against what they held before it (`AssetSession::end_package`): a created one
    /// is kept as its creator left it (the creator is named on it whole); a written one, encoded as the commit will, is
    /// compared with its bytes before (its archive bytes the first time), and only when they differ decoded and walked
    /// member by member. A resource that cannot be compared names the package on the whole of it.
    fn compare_running(&self) {
        let (bucket, written, created) = self.journal.running();
        if bucket == 0 {
            return;
        }
        for id in created.iter().chain(&written) {
            let Some(record) = self.resources.borrow().get(id).cloned() else {
                continue;
            };
            let now = match self.current(&record) {
                Ok(Some(now)) => now,
                Ok(None) => {
                    // never edited nor replaced: what it held before
                    self.paths.borrow_mut().entry(*id).or_default();
                    continue;
                }
                Err(_) => {
                    self.paths.borrow_mut().entry(*id).or_default().whole(bucket);
                    continue;
                }
            };
            if created.contains(id) {
                self.before.borrow_mut().insert(*id, now.1.into());
                continue;
            }
            let was = match self.before.borrow().get(id).cloned() {
                Some(was) => Some(was),
                None => self.archive_bytes(id),
            };
            let mut paths = self.paths.borrow_mut();
            let entry = paths.entry(*id).or_default();
            match was {
                Some(was) if *was == *now.1 => {}
                Some(was) => match self.decoded(id, was) {
                    Some(before) => entry.changed(bucket, journal_paths::diff(&before, &now.0, super::super::journal::RESOURCE_PATHS)),
                    None => entry.whole(bucket),
                },
                None => entry.whole(bucket),
            }
            drop(paths);
            self.before.borrow_mut().insert(*id, now.1.into());
        }
    }

    /// A record's value now as the commit encodes it (`apply`), with the converted value; None: never edited nor replaced.
    fn current(&self, record: &ResourceInfo) -> anyhow::Result<Option<(kfc::resource::value::Value, Vec<u8>)>> {
        let value = record.value.borrow();
        let Some(value) = value.as_ref() else {
            return Ok(None);
        };
        if !record.replaced.get() && !is_dirty_lua_value(value)? {
            return Ok(None);
        }
        let r#type = self.resource_type(&record.resource_id)?;
        let converted = resource_values::convert_lua_to_value(value, &r#type, &self.objects)?;
        let bytes = resource_values::value_bytes(&converted, &r#type).map_err(anyhow::Error::msg)?;
        Ok(Some((converted, bytes)))
    }

    /// A resource's bytes in the archive (the baseline the pass reads), None when it has none or the reader is closed.
    fn archive_bytes(&self, id: &ResourceId) -> Option<Rc<[u8]>> {
        let mut reader = self.reader.borrow_mut();
        reader.as_mut()?.read_resource(id).ok().flatten().map(Rc::from)
    }

    /// Bytes of resource `id`'s type as a value (None: they do not decode).
    fn decoded(&self, id: &ResourceId, bytes: Rc<[u8]>) -> Option<kfc::resource::value::Value> {
        let r#type = self.resource_type(id).ok()?;
        let len = bytes.len();
        let mapped = resource_values::decode_mapped(bytes, &r#type).ok()?;
        resource_values::mapped_to_value(mapped, len).ok()
    }

    fn resource(this: &Rc<Self>, info: Rc<ResourceInfo>) -> Resource {
        Resource::new(info, Rc::downgrade(this))
    }

    /// kiln-eml: the typed-object seam of resource `id`'s values: everything made from it reports its writes to
    /// the journal as writes of `id`.
    fn resource_objects(&self, id: ResourceId) -> ValueObjects {
        self.objects
            .owned_by(Rc::new(ResourceOwner::new(id, self.journal.clone())))
    }

    /// SF `get_resource_info`: the cached record, else a new `kfc-file` record when the reference has the key.
    fn resource_info(&self, id: &ResourceId) -> Option<Rc<ResourceInfo>> {
        match self.resources.borrow_mut().entry(*id) {
            Entry::Occupied(entry) => Some(entry.get().clone()),
            Entry::Vacant(entry) => {
                if !self.reference.resources().contains_key(id) {
                    return None;
                }
                let info = Rc::new(ResourceInfo::new(*id, ResourceSource::KfcFile));
                entry.insert(info.clone());
                Some(info)
            }
        }
    }

    fn type_name(&self, args: &FunctionArgs, index: usize) -> mlua::Result<String> {
        let registry = self.types.registry();
        let index = type_arg(args, index, registry)?;
        Ok(registry
            .get(index)
            .ok_or_else(|| LuaError::generic("type not found in context"))?
            .qualified_name
            .clone())
    }

    fn lua_get_resource(&self, this: &Rc<Self>, args: FunctionArgs) -> mlua::Result<Option<Resource>> {
        let guid = args.get::<GuidArg>(0)?;
        let name = self.type_name(&args, 1)?;
        let part = args.get::<Option<u32>>(2)?;
        let id = format_adapter::resource_id(self.types.registry(), &name, guid, part.unwrap_or(0))
            .ok_or_else(|| LuaError::generic(format!("type not found: {name}")))?;
        Ok(self.resource_info(&id).map(|info| Self::resource(this, info)))
    }

    fn lua_get_resource_parts(&self, this: &Rc<Self>, lua: &mlua::Lua, args: FunctionArgs) -> mlua::Result<Table> {
        let guid = args.get::<GuidArg>(0)?;
        let name = self.type_name(&args, 1)?;
        let target = format_adapter::resource_id(self.types.registry(), &name, guid, 0)
            .ok_or_else(|| LuaError::generic(format!("type not found: {name}")))?;
        let mut parts: Vec<ResourceId> = self
            .reference
            .resources()
            .keys()
            .iter()
            .filter(|id| id.guid() == target.guid() && format_adapter::same_type(id, &target))
            .copied()
            .collect();
        // FINAL: ascending part order, whatever the archive's storage order.
        parts.sort_by_key(ResourceId::part_index);
        let result = lua.create_table()?;
        for id in parts {
            if let Some(info) = self.resource_info(&id) {
                result.push(Self::resource(this, info))?;
            }
        }
        Ok(result)
    }

    fn lua_get_resources_by_type(&self, this: &Rc<Self>, lua: &mlua::Lua, args: FunctionArgs) -> mlua::Result<Table> {
        let name = self.type_name(&args, 0)?;
        let result = lua.create_table_with_capacity(self.reference.resources().len(), 0)?;
        for id in format_adapter::resources_by_type(&self.reference, self.types.registry(), &name) {
            if let Some(info) = self.resource_info(&id) {
                result.push(Self::resource(this, info))?;
            }
        }
        Ok(result)
    }

    fn lua_get_all_resources(&self, this: &Rc<Self>, lua: &mlua::Lua) -> mlua::Result<Table> {
        let result = lua.create_table_with_capacity(self.reference.resources().len(), 0)?;
        for id in self.reference.resources().keys().iter() {
            if let Some(info) = self.resource_info(id) {
                result.push(Self::resource(this, info))?;
            }
        }
        Ok(result)
    }

    fn lua_get_resource_types(&self, lua: &mlua::Lua) -> mlua::Result<Table> {
        let result = lua.create_table_with_capacity(self.reference.resource_bundles().len(), 0)?;
        for index in format_adapter::resource_type_indices(&self.reference, self.types.registry()) {
            // SF `expect`s this; a Lua error with its text instead of a panic.
            let value = self
                .types
                .get_type(lua, index)?
                .ok_or_else(|| LuaError::generic("type not found in context"))?;
            result.push(value)?;
        }
        Ok(result)
    }

    fn lua_get_content(&self, this: &Rc<Self>, args: FunctionArgs) -> mlua::Result<Option<Content>> {
        let guid = args.get::<ContentHashArg>(0)?;
        if !self.reference.contents().contains_key(&guid) {
            return Ok(None);
        }
        Ok(Some(Content::new(guid, Rc::downgrade(this))))
    }

    fn lua_get_all_contents(&self, this: &Rc<Self>, lua: &mlua::Lua) -> mlua::Result<Table> {
        let result = lua.create_table_with_capacity(self.reference.contents().len(), 0)?;
        for guid in self.reference.contents().keys().iter() {
            result.push(Content::new(*guid, Rc::downgrade(this)))?;
        }
        Ok(result)
    }

    fn lua_create_content(&self, this: &Rc<Self>, args: FunctionArgs) -> mlua::Result<Content> {
        let buffer = args.get::<&Buffer>(0)?;
        let data = buffer.data()?;
        if data.len() > u32::MAX as usize {
            return Err(LuaError::generic("content size exceeds maximum limit"));
        }
        self.create_content(data)
            .map(|guid| Content::new(guid, Rc::downgrade(this)))
    }

    /// SF `create_content`: PATCH first, then the content-derived identity; the first occurrence in this pass is
    /// written to the stage at once and its copy kept, a repeat is not written again.
    fn create_content(&self, data: &[u8]) -> mlua::Result<ContentHash> {
        let stage = self
            .stage
            .as_ref()
            .ok_or_else(|| LuaError::generic("game.assets.write is unavailable"))?;
        let guid = ContentHash::from_data(data);
        if let Entry::Vacant(entry) = self.new_contents.borrow_mut().entry(guid) {
            stage.write_content(&guid, data).map_err(LuaError::external)?;
            entry.insert(data.to_vec());
        }
        self.journal.content(guid);
        Ok(guid)
    }

    /// SF `create_resource` (access_file_resources.rs:123-152): the value and the type first; with a third argument
    /// the explicit identity (GUID and part, the part then required), else a random one; the new record as a Resource.
    fn lua_create_resource(&self, this: &Rc<Self>, lua: &mlua::Lua, args: FunctionArgs) -> mlua::Result<Resource> {
        let value = args.get::<LuaValue>(0)?;
        let index = type_arg(&args, 1, self.types.registry())?;
        let name = self.type_name(&args, 1)?;
        let explicit = args.len() > 2;
        let id = if explicit {
            let guid = args.get::<GuidArg>(2)?;
            let part = args.get::<u32>(3)?;
            let id = format_adapter::resource_id(self.types.registry(), &name, guid, part)
                .ok_or_else(|| LuaError::generic(format!("type not found: {name}")))?;
            self.add_resource(value, id, lua)?;
            id
        } else {
            // SF asks PATCH before it draws an identity, then again in add_resource
            self.write_gate()?;
            let id = pick_id(self.types.registry(), index, |id| self.taken(id), random_uuid)?;
            self.add_resource(value, id, lua)?;
            id
        };
        self.journal.created(id, explicit);
        let info = self
            .resource_info(&id)
            .ok_or_else(|| LuaError::generic("just created resource info must exist"))?;
        Ok(Self::resource(this, info))
    }

    /// SF `add_resource` (asset_resources.rs:211-253): PATCH, the identity free (here: of the reference catalog AND the
    /// cache), the record's type, then validation and a deep copy; a new dirty `asset-transaction` record.
    fn add_resource(&self, value: &LuaValue, id: ResourceId, lua: &mlua::Lua) -> mlua::Result<()> {
        self.write_gate()?;
        let exists = || LuaError::generic(format!("resource with GUID {id} already exists"));
        if self.taken(&id) {
            return Err(exists());
        }
        let r#type = self.resource_type(&id)?;
        let value = resource_values::validate_and_clone(value, &r#type, lua, &self.resource_objects(id))
            .map_err(LuaError::external)?;
        // Validation runs Lua (an input table's metamethods), which may have created this identity meanwhile: never
        // replace that record (SF keeps its cache borrowed instead, so such a metamethod fails on the borrow).
        match self.resources.borrow_mut().entry(id) {
            Entry::Occupied(_) => Err(exists()),
            Entry::Vacant(entry) => {
                entry.insert(Rc::new(ResourceInfo::created(id, value)));
                Ok(())
            }
        }
    }

    /// Whether an identity is in the reference catalog or the cache.
    fn taken(&self, id: &ResourceId) -> bool {
        self.reference.resources().contains_key(id) || self.resources.borrow().contains_key(id)
    }

    /// PATCH (a stage) and, once the stage is taken back, the closed writer's refusal.
    fn write_gate(&self) -> mlua::Result<()> {
        match &self.stage {
            None => Err(LuaError::generic("game.assets.write is unavailable")),
            Some(stage) if !stage.is_open() => Err(LuaError::generic("the staged archive writer is closed")),
            Some(_) => Ok(()),
        }
    }

    /// A record's reflected type (SF `type_for_resource` with its error text).
    fn resource_type(&self, id: &ResourceId) -> mlua::Result<TypeHandle> {
        let registry = self.types.registry();
        format_adapter::type_for_resource(registry, id)
            .map(|metadata| TypeHandle::new(registry.clone(), metadata.index))
            .ok_or_else(|| LuaError::generic("resource type not found in current game registry"))
    }

    /// SF `get_lua_value` (asset_resources.rs:31-44,101-138): the record's value, decoded from the baseline on first
    /// read (a created record always has one).
    pub(crate) fn resource_value(&self, info: &ResourceInfo, lua: &mlua::Lua) -> mlua::Result<LuaValue> {
        if let Some(value) = info.value.borrow().as_ref() {
            return Ok(value.clone());
        }
        let value = match info.source {
            ResourceSource::KfcFile => {
                let bytes = {
                    let mut reader = self.reader.borrow_mut();
                    let reader = reader
                        .as_mut()
                        .ok_or_else(|| LuaError::generic("asset reader is closed after publication"))?;
                    reader.read_resource(&info.resource_id).map_err(LuaError::external)?
                };
                match bytes {
                    Some(bytes) => {
                        let r#type = self.resource_type(&info.resource_id)?;
                        resource_values::decode(
                            Rc::from(bytes),
                            &r#type,
                            lua,
                            &self.resource_objects(info.resource_id),
                        )
                        .map_err(LuaError::external)?
                    }
                    None => LuaValue::Nil,
                }
            }
            ResourceSource::AssetTransaction => LuaValue::Nil,
        };
        // A finalizer run by the decode's allocations may have read or replaced this record first: keep that value.
        Ok(info.value.borrow_mut().get_or_insert(value).clone())
    }

    /// SF `set_lua_value` (asset_resources.rs:46-70): PATCH, the record's type, validation and a deep copy, then the
    /// record is replaced and dirty. A failure leaves the record as it was.
    pub(crate) fn replace_resource_value(
        &self,
        info: &ResourceInfo,
        value: LuaValue,
        lua: &mlua::Lua,
    ) -> mlua::Result<()> {
        self.write_gate()?;
        let r#type = self.resource_type(&info.resource_id)?;
        let value = resource_values::validate_and_clone(&value, &r#type, lua, &self.resource_objects(info.resource_id))
            .map_err(LuaError::external)?;
        info.value.replace(Some(value));
        info.replaced.set(true);
        self.journal.wrote(info.resource_id);
        Ok(())
    }

    /// SF commit_asset_changes.rs:13-33 into the taken-back stage: each record's `apply`, in resource-id order. No Lua
    /// value is created here, so no finalizer runs while it converts and writes.
    fn serialize(&self, stage: &mut StagedArchive<'_>) -> anyhow::Result<usize> {
        let mut records: Vec<Rc<ResourceInfo>> = self.resources.borrow().values().cloned().collect();
        records.sort_by_key(|record| record.resource_id);
        let mut applied = 0;
        for record in records {
            let id = record.resource_id;
            let bytes = self
                .apply(&record)
                .with_context(|| format!("resource {}/{:08x}/{}", id.guid(), id.type_hash(), id.part_index()))?;
            if let Some(bytes) = bytes {
                stage.write_resource(&id, &bytes)?;
                applied += 1;
            }
        }
        Ok(applied)
    }

    /// SF `ResourceInfo::apply` (asset_resources.rs:72-99) plus the encode of commit_asset_changes.rs:20-29: the bytes
    /// of a record whose value was replaced or edited, `None` for one never read or unchanged.
    fn apply(&self, record: &ResourceInfo) -> anyhow::Result<Option<Vec<u8>>> {
        let value = record.value.borrow();
        let Some(value) = value.as_ref() else {
            return Ok(None);
        };
        if !record.replaced.get() && !is_dirty_lua_value(value)? {
            return Ok(None);
        }
        let r#type = self.resource_type(&record.resource_id)?;
        let converted = resource_values::convert_lua_to_value(value, &r#type, &self.objects)?;
        resource_values::value_bytes(&converted, &r#type)
            .map(Some)
            .map_err(anyhow::Error::msg)
    }

    /// SF `get_content`: this pass's staged copy first, else the baseline reader.
    pub(crate) fn content_bytes(&self, guid: &ContentHash) -> mlua::Result<Option<Vec<u8>>> {
        if let Some(bytes) = self.new_contents.borrow().get(guid) {
            return Ok(Some(bytes.clone()));
        }
        let mut reader = self.reader.borrow_mut();
        let reader = reader
            .as_mut()
            .ok_or_else(|| LuaError::generic("asset reader is closed after publication"))?;
        reader.read_content(guid).map_err(LuaError::external)
    }
}

/// SF `create_resource`'s draw (asset_resources.rs:255-286): v4 UUIDs as the GUID, part 0, until the identity is free.
pub(crate) fn pick_id(
    registry: &TypeRegistry,
    index: TypeIndex,
    taken: impl Fn(&ResourceId) -> bool,
    mut next: impl FnMut() -> [u8; 16],
) -> mlua::Result<ResourceId> {
    loop {
        let id = format_adapter::resource_id_for_type(registry, index, next().into(), 0)
            .ok_or_else(|| LuaError::generic("resource type not found in current game registry"))?;
        if !taken(&id) {
            return Ok(id);
        }
    }
}

/// A random (version 4) UUID's bytes without a new dependency: two SipHash-1-3 outputs under the standard library's
/// `RandomState` (128 key bits from the operating system once per thread, the first key word stepped for every new
/// state) over a process-wide counter, so no two draws hash the same input under the same key. Uniqueness is checked by
/// `pick_id`, so this needs to be unpredictable only in the way SF's `Uuid::new_v4` is.
fn random_uuid() -> [u8; 16] {
    use std::hash::{BuildHasher, Hasher, RandomState};
    static DRAWS: AtomicU64 = AtomicU64::new(0);
    let draw = DRAWS.fetch_add(1, Ordering::Relaxed);
    let mut bytes = [0u8; 16];
    for (half, chunk) in bytes.chunks_mut(8).enumerate() {
        let mut hasher = RandomState::new().build_hasher();
        hasher.write_u64(draw);
        hasher.write_usize(half);
        chunk.copy_from_slice(&hasher.finish().to_le_bytes());
    }
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    bytes
}

#[cfg(test)]
mod tests {
    use super::*;
    use kfc::{guid::Guid, reflection::TypeMetadata};

    fn registry() -> TypeRegistry {
        let t = TypeMetadata {
            index: TypeIndex::new(0),
            name: "Root".into(),
            impact_name: "Root".into(),
            qualified_name: "t.Root".into(),
            qualified_hash: kfc::hash::fnv("t.Root"),
            internal_hash: 1,
            name_hash: kfc::hash::fnv("Root"),
            impact_hash: kfc::hash::fnv("Root"),
            namespace: vec!["t".into()],
            inner_type: None,
            size: 4,
            alignment: 4,
            element_alignment: 4,
            field_count: 0,
            primitive_type: kfc::reflection::PrimitiveType::Struct,
            flags: kfc::reflection::TypeFlags::NONE,
            struct_fields: Default::default(),
            enum_fields: Default::default(),
            default_value: None,
            attributes: Default::default(),
        };
        serde_json::from_value(serde_json::json!({"version": "t", "types": [t]})).unwrap()
    }

    // The random overload draws again while the identity is taken (the caller's `taken`: the reference catalog or
    // the cache) and returns the first free one, part 0, of the type's hash; the draws are v4 UUIDs.
    #[test]
    fn a_random_identity_is_drawn_again_until_it_is_free() {
        let registry = registry();
        let draws = [[1u8; 16], [2u8; 16], [3u8; 16]];
        let taken = [Guid::from(draws[0]), Guid::from(draws[1])];
        let mut next = draws.into_iter();
        let id = pick_id(
            &registry,
            TypeIndex::new(0),
            |id| taken.contains(&id.guid()),
            || next.next().unwrap(),
        )
        .unwrap();
        assert_eq!(id.guid(), Guid::from(draws[2]));
        assert_eq!(id.part_index(), 0);
        assert_eq!(id.type_hash(), kfc::hash::fnv("t.Root"));
        assert!(pick_id(&registry, TypeIndex::new(7), |_| false, random_uuid).is_err());
        let (a, b) = (random_uuid(), random_uuid());
        assert_ne!(a, b);
        assert_eq!(a[6] >> 4, 4, "version 4");
        assert_eq!(a[8] >> 6, 0b10, "RFC 4122 variant");
    }
}
