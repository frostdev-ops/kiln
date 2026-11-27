// SPDX-License-Identifier: MIT
// the interpreter's game.assets loaded-resource members.
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer)
// Sources (1.7.9 d8a394bc): modules/mod-runtime/lua/bindings/game/assets/resource.rs:24-50 (the Resource object's
// fields), session/asset_resources.rs:45-92 (source_name, get_lua_value, set_lua_value), session/loaded_resources.rs:
// 55-150 (ResourceInfo::loaded, loaded_snapshot, creation_session, loaded_snapshot_bytes), lua/ingame/resources/
// read_loaded_resources.rs (resolve_loaded_resource, get_loaded_resource, get_loaded_resources_by_type,
// read_loaded_field), lua/values/resolve_loaded_field.rs:7-46 (loaded_field's inline plan).
//! The `Resource` object of a loaded resource and the four members.
//!
//! A resource from `get_loaded_resource` carries its one confirmed snapshot; one from a listing reads its snapshot
//! on first use (`data`, `read_loaded_field`), bound to the session it was listed in. `data` is the snapshot decoded
//! by the VM's typed values as one loaded root per resource object (its edits stay on that object), and is read-only:
//! writing it raises ShroudForge's text. `read_loaded_field` reads an inline reflected field (one naturally aligned
//! scalar leaf); a path into a resource's dynamic storage (a BlobArray or a variant) is refused with the inline
//! resolver's reason (ShroudForge resolves those against each snapshot's descriptors; Kiln does not yet).

use super::{Assets, Read};
use kiln_abi::services::kiln_assets::KilnAssetKey;
use mlua::{Lua, MultiValue, Table, UserData, UserDataFields, Value};
use sfi_core::bindings::runtime::{RuntimeHost, RuntimeOperation as Op, runtime_denial_reason};
use sfi_core::lua::{FunctionArgs, LuaError};
use std::cell::{OnceCell, RefCell};
use std::rc::Rc;

/// ShroudForge's texts (read_loaded_resources.rs, loaded_resources.rs, resolve_loaded_field.rs).
pub mod text {
    pub const SESSION_LOOKUP: &str = "world session changed during loaded resource lookup";
    pub const SESSION_LISTING: &str = "world session changed during loaded resource listing";
    pub const SESSION_BEFORE: &str =
        "world session changed before this resource snapshot was captured";
    pub const SESSION_DURING: &str = "world session changed during resource snapshot capture";
    pub const READ_ONLY: &str = "loaded runtime resources are read-only";
    pub const FIELD_ARGS: &str =
        "read_loaded_field expects a loaded Resource and reflected field path";
    pub const NO_TYPE: &str = "resource type is absent from active reflection";
    pub const NO_FIELD_TYPE: &str = "resource field type is unavailable";
    pub const FIELD_SHAPE: &str =
        "resource field requires a complete root and one naturally aligned 1/2/4/8-byte leaf";
    pub const FIELD_OUTSIDE: &str = "resource field outside current snapshot";
    pub const FIELD_RANGE: &str = "resource snapshot exceeds 32-bit blob offset range";
    pub const RESOLVE_ARGS: &str =
        "resolve_loaded_resource expects an original registry name and typed key";
    pub const ITEM_REGISTRY: &str = "g38_items::ItemRegistry";
    pub const NO_ITEM_INFO: &str = "ItemInfo is absent from active reflection";
    pub const ITEM_FIELDS: &str = "original ItemRegistry fields do not match the active reflection";
    pub const NO_RESOLVER: &str = "provider lacks the original ItemRegistry resolver";
    // write_loaded_field.rs, loaded_resources.rs:100-112
    pub const WRITE_ARGS: &str = "write_loaded_field expects a loaded Resource, field path, value, and optional expected value";
    pub const WRITE_SESSION: &str = "loaded resource belongs to another or an inactive world session. Get a fresh world-bound resource snapshot";
    pub const WRITE_NO_TYPE: &str = "resource type is absent from the active reflection";
    pub const WRITE_EXPECTED: &str =
        "Resource scalar snapshot changed before update. Read current values and retry";
    pub const WRITE_SOURCE: &str = "Resource source snapshot changed before update. Get a fresh resource or supply the observed expected scalar value";
    pub const WRITE_SHAPE: &str =
        "live resource writes support one contiguous 1/2/4/8-byte scalar per call";
}

/// The reflected type of an item's resource.
pub const ITEM_INFO: &str = "keen::ItemInfo";

/// A loaded resource (ShroudForge's Resource over `ResourceInfo::loaded`).
pub struct LoadedResource {
    assets: Rc<Assets>,
    key: KilnAssetKey,
    /// The resource's registry type (the one the lookup or the listing named).
    type_index: usize,
    /// The world session it was made in (0 in none).
    session: u64,
    instance: u64,
    snapshot: OnceCell<Rc<[u8]>>,
    value: RefCell<Option<Value>>,
    /// An uncertain write through this object stops its writes (ShroudForge's `stop_runtime_writes`).
    write_fault: RefCell<Option<String>>,
}

impl LoadedResource {
    fn new(
        assets: Rc<Assets>,
        key: KilnAssetKey,
        type_index: usize,
        session: u64,
        snapshot: Option<Vec<u8>>,
    ) -> Self {
        let instance = assets.next_instance();
        let cell = OnceCell::new();
        if let Some(bytes) = snapshot {
            let _ = cell.set(Rc::from(bytes));
        }
        Self {
            assets,
            key,
            type_index,
            session,
            instance,
            snapshot: cell,
            value: RefCell::new(None),
            write_fault: RefCell::new(None),
        }
    }
    /// ShroudForge's `require_runtime_write_session` (loaded_resources.rs:100-112).
    pub fn require_write_session(&self, current: u64) -> Result<u64, String> {
        if let Some(reason) = self.write_fault.borrow().as_ref() {
            return Err(reason.clone());
        }
        if self.session == 0 || current == 0 || self.session != current {
            return Err(text::WRITE_SESSION.into());
        }
        Ok(self.session)
    }
    pub fn stop_writes(&self, reason: &str) {
        *self.write_fault.borrow_mut() = Some(reason.to_owned());
    }
    pub fn assets(&self) -> &Rc<Assets> {
        &self.assets
    }
    /// A resource of `key` carrying its confirmed `snapshot`, made in `session` (ShroudForge's
    /// `get_loaded_resource_info_from_snapshot`).
    pub fn from_snapshot(
        assets: Rc<Assets>,
        key: KilnAssetKey,
        type_index: usize,
        session: u64,
        snapshot: Vec<u8>,
    ) -> Self {
        Self::new(assets, key, type_index, session, Some(snapshot))
    }
    /// A resource of `key` that reads its snapshot on first use, bound to `session` (ShroudForge's
    /// `get_loaded_resource_info_for_session`).
    pub fn for_session(
        assets: Rc<Assets>,
        key: KilnAssetKey,
        type_index: usize,
        session: u64,
    ) -> Self {
        Self::new(assets, key, type_index, session, None)
    }
    pub fn key(&self) -> &KilnAssetKey {
        &self.key
    }
    pub fn session(&self) -> u64 {
        self.session
    }
    pub fn type_index(&self) -> usize {
        self.type_index
    }

    /// `loaded_snapshot_bytes` (loaded_resources.rs:118-136): the snapshot it carries, or one read now in its session.
    pub fn snapshot(&self) -> Result<Rc<[u8]>, String> {
        if let Some(bytes) = self.snapshot.get() {
            return Ok(bytes.clone());
        }
        let now = self.assets.world_session();
        if self.session != 0 && self.session != now {
            return Err(text::SESSION_BEFORE.into());
        }
        let bytes = match self.assets.read(&self.key)? {
            Read::Bytes(bytes) => bytes,
            // the bridge's read of a GUID it cannot find
            Read::Missing => return Err(super::text::RESOURCE_CHANGED.into()),
        };
        if self.session != 0 && self.assets.world_session() != self.session {
            return Err(text::SESSION_DURING.into());
        }
        Ok(self.snapshot.get_or_init(|| Rc::from(bytes)).clone())
    }

    fn qualified_name(&self) -> Option<String> {
        self.assets
            .types()
            .get(self.type_index)
            .map(|ty| ty.qualified_name.clone())
    }

    /// `get_lua_value`: the snapshot decoded once per object (the typed values' loaded root of this instance).
    fn data(&self, lua: &Lua) -> mlua::Result<Value> {
        if let Some(value) = self.value.borrow().as_ref() {
            return Ok(value.clone());
        }
        let bytes = self.snapshot().map_err(LuaError::generic)?;
        let Some(name) = self.qualified_name() else {
            return Ok(Value::Nil);
        };
        let values = self
            .assets
            .values()
            .ok_or_else(|| mlua::Error::runtime(crate::runtime::ecs::NO_VALUES))?;
        let value = values.open_loaded(lua, &name, &bytes, self.instance, self.session)?;
        self.value.replace(Some(value.clone()));
        Ok(value)
    }
}

/// The GUID's text in the game's byte order (.NET's: the first three groups little-endian), lower-case.
pub fn guid_text(guid: &[u8; 16]) -> String {
    let mut out = String::with_capacity(36);
    let hex = |out: &mut String, bytes: &[u8]| {
        for byte in bytes {
            out.push_str(&format!("{byte:02x}"));
        }
    };
    hex(&mut out, &[guid[3], guid[2], guid[1], guid[0]]);
    out.push('-');
    hex(&mut out, &[guid[5], guid[4]]);
    out.push('-');
    hex(&mut out, &[guid[7], guid[6]]);
    out.push('-');
    hex(&mut out, &guid[8..10]);
    out.push('-');
    hex(&mut out, &guid[10..16]);
    out
}

/// A GUID's text (36 characters, hyphens at 8, 13, 18 and 23; anything after them ignored) in the game's byte order.
pub fn parse_guid(text: &str) -> Option<[u8; 16]> {
    let s = text.as_bytes();
    if s.len() < 36 || s[8] != b'-' || s[13] != b'-' || s[18] != b'-' || s[23] != b'-' {
        return None;
    }
    let byte = |at: usize| -> Option<u8> {
        let digit = |c: u8| (c as char).to_digit(16).map(|d| d as u8);
        Some(digit(s[at])? << 4 | digit(s[at + 1])?)
    };
    let mut guid = [0u8; 16];
    let groups: [(usize, &[usize]); 5] = [
        (0, &[3, 2, 1, 0]),
        (9, &[5, 4]),
        (14, &[7, 6]),
        (19, &[8, 9]),
        (24, &[10, 11, 12, 13, 14, 15]),
    ];
    for (start, slots) in groups {
        for (i, slot) in slots.iter().enumerate() {
            guid[*slot] = byte(start + i * 2)?;
        }
    }
    Some(guid)
}

impl UserData for LoadedResource {
    fn add_fields<F: UserDataFields<Self>>(fields: &mut F) {
        fields.add_field_method_get("guid", |_, this| Ok(guid_text(&this.key.guid)));
        fields.add_field_method_get("source", |_, _| Ok("loaded-runtime"));
        fields.add_field_method_get("loaded", |_, _| Ok(true));
        fields.add_field_method_get("type", |lua, this| {
            Ok(this
                .assets
                .type_session()
                .get_type(lua, this.type_index)?
                .unwrap_or(Value::Nil))
        });
        fields.add_field_method_get("part", |_, this| Ok(this.key.part));
        fields.add_field_method_get("session_id", |_, this| Ok(this.session));
        fields.add_field_method_get("data", |lua, this| this.data(lua));
        fields.add_field_method_set("data", |_, _, _: Value| -> mlua::Result<()> {
            Err(LuaError::generic(text::READ_ONLY))
        });
    }
}

/// The selector at `index` as a registry type (ShroudForge's `get_type`: its reason is raised).
fn selected_type(assets: &Assets, args: &FunctionArgs, index: usize) -> mlua::Result<usize> {
    crate::runtime::types::selectors::resolve(assets.types(), args.get::<Value>(index)?)
        .map_err(LuaError::generic)
}

fn type_hash(assets: &Assets, index: usize) -> mlua::Result<u32> {
    assets
        .types()
        .get(index)
        .map(|ty| ty.qualified_hash)
        .ok_or_else(|| LuaError::generic(format!("type not found: {index}")))
}

/// `game.assets.get_loaded_resource(guid, type, part?)` (read_loaded_resources.rs:83-127).
pub fn get_loaded_resource(
    lua: &Lua,
    assets: &Rc<Assets>,
    args: &FunctionArgs,
) -> mlua::Result<Option<LoadedResource>> {
    let session = assets.world_session();
    let guid = match args.get::<Value>(0)? {
        Value::String(text) => {
            let text = text.to_str()?.to_owned();
            parse_guid(&text)
                .ok_or_else(|| LuaError::generic(format!("invalid guid string: {text}")))?
        }
        other => {
            return Err(LuaError::generic(format!(
                "error converting Lua {} to Guid",
                other.type_name()
            )));
        }
    };
    let index = selected_type(assets, args, 1)?;
    let part = args.get::<Option<u32>>(2)?.unwrap_or(0);
    let key = KilnAssetKey {
        guid,
        r#type: type_hash(assets, index)?,
        part,
    };
    // one confirmed snapshot; a missing GUID is nil, an unavailable or changing service its reason, and a read that
    // failed raises it only while the resource is still there (ShroudForge's resource_read, then resource_exists)
    let snapshot = match assets.read(&key) {
        Ok(Read::Bytes(bytes)) => Some(bytes),
        Ok(Read::Missing) => None,
        Err(reason) => {
            if assets.exists(&key).map_err(LuaError::generic)? {
                return Err(LuaError::generic(reason));
            }
            None
        }
    };
    if assets.world_session() != session {
        return Err(LuaError::generic(text::SESSION_LOOKUP));
    }
    let _ = lua;
    Ok(snapshot.map(|bytes| LoadedResource::new(assets.clone(), key, index, session, Some(bytes))))
}

/// `game.assets.get_loaded_resources_by_type(type)` (read_loaded_resources.rs:129-162).
pub fn get_loaded_resources_by_type(
    lua: &Lua,
    assets: &Rc<Assets>,
    args: &FunctionArgs,
) -> mlua::Result<Table> {
    let session = assets.world_session();
    let index = selected_type(assets, args, 0)?;
    let keys = assets
        .list(type_hash(assets, index)?)
        .map_err(LuaError::generic)?;
    let result = lua.create_table_with_capacity(keys.len(), 0)?;
    for key in keys {
        result.push(LoadedResource::new(
            assets.clone(),
            key,
            index,
            session,
            None,
        ))?;
    }
    if assets.world_session() != session {
        return Err(LuaError::generic(text::SESSION_LISTING));
    }
    Ok(result)
}

/// `game.assets.read_loaded_field(resource, path)` (read_loaded_resources.rs:164-190): `value, nil, type` or
/// `nil, reason, nil`.
pub fn read_loaded_field(
    lua: &Lua,
    assets: &Assets,
    args: &FunctionArgs,
) -> mlua::Result<MultiValue> {
    let read = || -> Result<(Value, Value), String> {
        if args.len() != 2 {
            return Err(text::FIELD_ARGS.into());
        }
        let resource = args
            .get::<&LoadedResource>(0)
            .map_err(|error| error.to_string())?;
        let path = args.get::<String>(1).map_err(|error| error.to_string())?;
        let bytes = resource.snapshot()?;
        let root = assets
            .types()
            .get(resource.type_index)
            .filter(|ty| ty.qualified_hash == resource.key.r#type)
            .ok_or(text::NO_TYPE)?;
        let field = loaded_field(assets, root.index, root.size as usize, &path, &bytes)?;
        let values = assets
            .values()
            .ok_or_else(|| crate::runtime::ecs::NO_VALUES.to_owned())?;
        let value = field.read(lua, values, &bytes)?;
        let ty = assets
            .type_session()
            .get_type(lua, field.type_index)
            .map_err(|error| error.to_string())?
            .ok_or(text::NO_FIELD_TYPE)?;
        Ok((value, ty))
    };
    Ok(match read() {
        Ok((value, ty)) => MultiValue::from_iter([value, Value::Nil, ty]),
        Err(reason) => MultiValue::from_iter([
            Value::Nil,
            Value::String(lua.create_string(reason)?),
            Value::Nil,
        ]),
    })
}

/// `loaded_field` (resolve_loaded_field.rs:7-46), its inline plan: one naturally aligned 1/2/4/8-byte scalar leaf
/// of a complete root. A path the inline resolver refuses answers its reason.
fn loaded_field(
    assets: &Assets,
    root: usize,
    root_size: usize,
    path: &str,
    bytes: &[u8],
) -> Result<Rc<super::InlineField>, String> {
    if bytes.len() > u32::MAX as usize {
        return Err(text::FIELD_RANGE.into());
    }
    let field = assets.inline_field(root, path)?;
    let range = field.byte_range();
    let width = range.len();
    if bytes.len() < root_size
        || !matches!(width, 1 | 2 | 4 | 8)
        || range.start % width != 0
        || field.leaves.len() != 1
        || field.leaves[0].1 != (0..width)
    {
        return Err(text::FIELD_SHAPE.into());
    }
    crate::runtime::ecs::cas::validate_runtime_scalar(
        field.leaves[0].0,
        bytes.get(range).ok_or(text::FIELD_OUTSIDE)?,
    )?;
    Ok(field)
}

/// ShroudForge's `available(state, mod, operation)` for the two write operations (check_ingame_access.rs:37-54).
pub(crate) fn write_available(host: &dyn RuntimeHost, operation: Op) -> bool {
    let owner = host.owner_context();
    if owner.phase() != sfi_core::host::Phase::Runtime
        || !host.static_support(operation).is_available()
        || !owner.has_runtime()
    {
        return false;
    }
    let facts = host.provider_facts();
    match operation {
        Op::AssetsWrite => facts.resource_can_write,
        Op::EcsWrite => facts.can_write,
        _ => false,
    }
}

/// The ownership authority of a package's resource writes: its host and the ECS world's live session.
pub fn authority(
    host: &Rc<dyn RuntimeHost>,
    assets: &Rc<Assets>,
) -> Rc<crate::runtime::overrides::HostAuthority> {
    let live = assets.clone();
    Rc::new(crate::runtime::overrides::HostAuthority::new(
        host.clone(),
        move || live.world_session(),
    ))
}

/// The ownership key of a loaded resource.
pub fn ownership_key(key: &KilnAssetKey) -> crate::runtime::overrides::ResourceKey {
    crate::runtime::overrides::ResourceKey {
        guid: guid_text(&key.guid),
        type_hash: key.r#type,
        part_index: key.part,
    }
}

/// `game.assets.write_loaded_field(resource, path, value, expected?)` (write_loaded_field.rs:2-108): the updated
/// `Resource` (the proven snapshot) or `nil, reason`.
pub fn write_loaded_field(
    lua: &Lua,
    assets: &Rc<Assets>,
    package_id: &str,
    host: Option<&Rc<dyn RuntimeHost>>,
    args: &FunctionArgs,
) -> (Option<LoadedResource>, Option<String>) {
    let apply = || -> Result<LoadedResource, String> {
        let Some(host) = host else {
            return Err(format!(
                "mod '{package_id}' requires capabilities: [\"runtime\"]"
            ));
        };
        for operation in [Op::AssetsWrite, Op::EcsWrite] {
            if let Some(reason) = runtime_denial_reason(host.as_ref(), operation) {
                return Err(reason);
            }
            if !write_available(host.as_ref(), operation) {
                return Err(format!("{} is currently unavailable", operation.as_str()));
            }
        }
        if args.len() != 3 && args.len() != 4 {
            return Err(text::WRITE_ARGS.into());
        }
        let resource = args.get::<&LoadedResource>(0).map_err(|e| e.to_string())?;
        let session = resource.require_write_session(assets.world_session())?;
        let path = args.get::<String>(1).map_err(|e| e.to_string())?;
        let value = args.get::<Value>(2).map_err(|e| e.to_string())?;
        let root = assets
            .types()
            .get(resource.type_index)
            .filter(|ty| ty.qualified_hash == resource.key.r#type)
            .ok_or(text::WRITE_NO_TYPE)?;
        let (root_index, root_size) = (root.index, root.size as usize);
        let before = match assets.read(&resource.key)? {
            Read::Bytes(bytes) => bytes,
            Read::Missing => return Err(super::text::RESOURCE_CHANGED.into()),
        };
        let field = loaded_field(assets, root_index, root_size, &path, &before)?;
        let range = field.byte_range();
        let values = assets
            .values()
            .ok_or_else(|| crate::runtime::ecs::NO_VALUES.to_owned())?;
        if args.len() == 4 {
            let expected = args.get::<Value>(3).map_err(|e| e.to_string())?;
            let mut candidate = before.clone();
            field.write_into(values, expected, &mut candidate, &mut vec![0; before.len()])?;
            if candidate[range.clone()] != before[range.clone()] {
                return Err(text::WRITE_EXPECTED.into());
            }
        } else {
            // A prior data read must not silently turn into a blind assignment: the object's own snapshot must be the
            // current one unless the caller supplies the observed value.
            let source = resource.snapshot()?;
            let source_field = loaded_field(assets, root_index, root_size, &path, &source)?;
            if source.as_ref() != before.as_slice()
                || source_field.byte_range() != range
                || source_field.type_index != field.type_index
                || source[range.clone()] != before[range.clone()]
            {
                return Err(text::WRITE_SOURCE.into());
            }
        }
        let mut after = before.clone();
        let mut mask = vec![0u8; before.len()];
        field.write_into(values, value, &mut after, &mut mask)?;
        let (offset, width) = (range.start, range.len());
        if !matches!(width, 1 | 2 | 4 | 8)
            || mask.iter().filter(|byte| **byte != 0).count() != width
            || mask.get(range.clone()).is_none_or(|span| span.contains(&0))
        {
            return Err(text::WRITE_SHAPE.into());
        }
        resource.require_write_session(assets.world_session())?;
        if let Some(facts) = assets.facts() {
            crate::runtime::overrides::check_write(
                facts.ownership(),
                authority(host, assets).as_ref(),
                &crate::runtime::overrides::StorageKey::Resource(ownership_key(&resource.key)),
            )?;
        }
        // sfi R3: the write names its package and the field path it gave, for Kiln's registry report
        if let Err(reason) = assets.compare_exchange(
            &resource.key,
            &before,
            offset,
            &after[range.clone()],
            session,
            &super::WriteName::package(package_id, &path),
        ) {
            if reason.to_lowercase().contains("uncertain") {
                resource.stop_writes(&reason);
            }
            return Err(reason);
        }
        // the native success read the whole expected-and-desired resource back under the gate: keep that result
        let _ = lua;
        Ok(LoadedResource::new(
            assets.clone(),
            resource.key,
            resource.type_index,
            session,
            Some(after),
        ))
    };
    match apply() {
        Ok(resource) => (Some(resource), None),
        Err(reason) => (None, Some(reason)),
    }
}

/// `game.assets.resolve_loaded_resource(registry, key)` (read_loaded_resources.rs:3-81): ShroudForge's checks up to
/// its provider's original ItemRegistry resolver, which Kiln does not have: `nil, reason`.
pub fn resolve_loaded_resource(
    assets: &Assets,
    denial: Option<String>,
    args: &FunctionArgs,
) -> (Option<LoadedResource>, Option<String>) {
    let resolve = || -> Result<(), String> {
        if let Some(reason) = denial {
            return Err(reason);
        }
        if args.len() != 2 {
            return Err(text::RESOLVE_ARGS.into());
        }
        let registry_name = args.get::<String>(0).map_err(|e| e.to_string())?;
        if registry_name != text::ITEM_REGISTRY {
            return Err(format!(
                "no validated resource relation for {registry_name}"
            ));
        }
        let _item_id = args.get::<u32>(1).map_err(|e| e.to_string())?;
        let registry = assets.types();
        let ty = registry
            .by_qualified_name(ITEM_INFO)
            .ok_or(text::NO_ITEM_INFO)?;
        let key = assets.inline_field(ty.index, "itemId.value")?;
        let guid = assets.inline_field(ty.index, "objectId")?;
        if key.byte_range().len() != 4
            || guid.byte_range().len() != 16
            || registry
                .get(guid.type_index)
                .is_none_or(|field| field.primitive_type != "Guid")
        {
            return Err(text::ITEM_FIELDS.into());
        }
        // `runtime_provider::resource_resolve_item`: Kiln has no original ItemRegistry resolver
        Err(text::NO_RESOLVER.into())
    };
    match resolve() {
        Ok(()) => (None, Some(text::NO_RESOLVER.into())),
        Err(reason) => (None, Some(reason)),
    }
}

/// ShroudForge's `runtime_denial_reason(state, mod, "runtime.ecs.read")` for a package: its host's, or, without the
/// `runtime` capability, check_ingame_access.rs:400-405's refusal.
pub(super) fn read_denial(package_id: &str, host: Option<&Rc<dyn RuntimeHost>>) -> Option<String> {
    match host {
        Some(host) => runtime_denial_reason(host.as_ref(), Op::EcsRead),
        None => Some(format!(
            "mod '{package_id}' requires capabilities: [\"runtime\"]"
        )),
    }
}

/// The four members into one package's `game.assets`.
pub fn install(
    lua: &Lua,
    table: &Table,
    package_id: &str,
    host: Option<Rc<dyn RuntimeHost>>,
    assets: Rc<Assets>,
) -> mlua::Result<()> {
    let a = assets.clone();
    table.raw_set(
        "get_loaded_resource",
        lua.create_function(move |lua, args: FunctionArgs| get_loaded_resource(lua, &a, &args))?,
    )?;
    let a = assets.clone();
    table.raw_set(
        "get_loaded_resources_by_type",
        lua.create_function(move |lua, args: FunctionArgs| {
            get_loaded_resources_by_type(lua, &a, &args)
        })?,
    )?;
    let a = assets.clone();
    table.raw_set(
        "read_loaded_field",
        lua.create_function(move |lua, args: FunctionArgs| read_loaded_field(lua, &a, &args))?,
    )?;
    let (a, id, h) = (assets.clone(), package_id.to_owned(), host.clone());
    table.raw_set(
        "write_loaded_field",
        lua.create_function(move |lua, args: FunctionArgs| {
            Ok(write_loaded_field(lua, &a, &id, h.as_ref(), &args))
        })?,
    )?;
    let a = assets;
    let id = package_id.to_owned();
    table.raw_set(
        "resolve_loaded_resource",
        lua.create_function(move |_, args: FunctionArgs| {
            Ok(resolve_loaded_resource(
                &a,
                read_denial(&id, host.as_ref()),
                &args,
            ))
        })?,
    )?;
    Ok(())
}
