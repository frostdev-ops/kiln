// SPDX-License-Identifier: MIT
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer)
// Sources (1.7.9 d8a394bc): modules/mod-runtime/lua/ingame/ecs/access_template_component.rs:15-460 (the view, its
// registration and variant checks, read_fields and write_field, their texts and order), game-runtime/native/ecs/
// registry/refresh_component_registry.cpp:254-296 (the registration report's rows), game-runtime/bridge/windows/ecs/
// read_component_registration.rs:5-17, mod-runtime/lua/values/resolve_resource_field.rs:18-223 (the checks on the way
// to a view's field and on its leaf).
//! `runtime.ecs.get_template_component(entity, component_type)`: a checked view of one component's configuration in
//! the template an entity was spawned from (ShroudForge's `RuntimeTemplateComponent`, scope `shared-template`).
//!
//! ShroudForge's checks in its order: the read denial and availability, two arguments, a live world session, the
//! entity, the selector in the archive registry, the selector's live registration (kiln.world's `ecs_registration`:
//! the engine catalog's row with its storage and template types) and its template role checked against reflection,
//! the entity's template uuid, the `TemplateResource` of that uuid, its loaded snapshot (kiln.assets'
//! `loaded_snapshot`), the one variant of the component in the template's `components` blob array, the template uuid
//! again and the session again. The view owns the resource identity, not the entity, as ShroudForge's does.
//!
//! **Kiln's registration lookup.** ShroudForge's provider answers the rows whose registration, storage or template
//! name is the selector's; kiln.world answers the one registration whose hash is the selector's. For a template role
//! the two agree: a registration with a template type is identified by it (the template type's name is the
//! registration's, kiln.world checks it), and a row found only by its storage name is never a match of
//! ShroudForge's own filter. So "ambiguous live template registration" cannot happen here (the catalog's hashes are
//! unique, which ShroudForge's reader checks too).
//!
//! **Variants.** The template's `components` field is a `BlobArray` of 12-byte `BlobVariant`s (ShroudForge checks this
//! layout before its mapper runs): the array is {u32 offset from its own position, u32 count}, a variant is {u32 type
//! hash, u32 offset from its own offset word, u32 size}. Kiln decodes exactly that layout; a variant of the component's
//! hash is the component's (ShroudForge's mapper names it by the same hash). Field paths are the component's inline
//! fields (`components[n].value.<path>` in ShroudForge's resolver), placed at the variant's value, with that
//! resolver's checks on the way and on the leaf: the array's payload past its descriptor and the root, the variant's
//! payload of the reflected size past its own descriptor and the root, and one naturally aligned 1/2/4/8-byte scalar
//! on no traversed descriptor that holds a valid value. A malformed template is refused per path, never read or
//! written through its own descriptors.

use super::{Ecs, available};
use crate::runtime::assets::resource::{authority, guid_text, ownership_key};
use crate::runtime::assets::{Assets, Read};
use crate::runtime::types::{Registry, selectors};
use crate::runtime::unavailable::NO_RESOURCE_READ;
use crate::service_member;
use kiln_abi::services::kiln_assets::KilnAssetKey;
use kiln_abi::services::kiln_world::{
    KILN_ECS_ERR_UNREADABLE, KILN_ECS_UNAVAILABLE, KilnEcsRegistrationDetail, KilnWorldApi,
};
use kiln_abi::{KILN_ERR_INVALID_ARGUMENT, KILN_ERR_NOT_FOUND, KILN_ERR_UNAVAILABLE, KILN_OK};
use mlua::{Lua, MultiValue, Table, UserData, Value};
use sfi_core::bindings::runtime::{RuntimeHost, RuntimeOperation as Op, runtime_denial_reason};
use sfi_core::lua::FunctionArgs;
use std::cell::RefCell;
use std::collections::BTreeSet;
use std::rc::Rc;

/// The reflected type of an entity's template.
pub const TEMPLATE_TYPE: &str = super::template::TEMPLATE_TYPE;
/// The most field paths one read or one expectation selects (ShroudForge's bound).
pub const MAX_PATHS: usize = 1024;

/// ShroudForge's texts (access_template_component.rs).
pub mod text {
    pub const ARGS: &str = "get_template_component expects entity and component type";
    pub const SESSION: &str = "template view belongs to another or an inactive world session";
    pub const UNRESOLVED_SELECTOR: &str = "unresolved template selector";
    pub const REGISTRATION_UNAVAILABLE: &str = "live template registration is unavailable";
    pub const OWNER_DISAGREES: &str =
        "template registration owner identity disagrees with reflection";
    pub const NO_LAYOUT: &str = "registration has no template configuration layout";
    pub const LAYOUT_ABSENT: &str = "template layout is absent from reflection";
    pub const LAYOUT_DISAGREES: &str = "template registration layout disagrees with reflection";
    pub const NO_UUID: &str = "entity template UUID is unavailable or unstable";
    pub const NO_TEMPLATE_TYPE: &str = "TemplateResource is absent from active reflection";
    pub const ROOT_LAYOUT: &str = "unresolved template resource layout";
    pub const COMPONENT_LAYOUT: &str = "unresolved template component layout";
    pub const INCOMPLETE_ROOT: &str = "TemplateResource is not a complete reflected struct";
    pub const NO_COMPONENTS_FIELD: &str = "TemplateResource has no reflected components field";
    pub const ARRAY_LAYOUT: &str = "unresolved components array layout";
    pub const ELEMENT_LAYOUT: &str = "components array has no reflected element layout";
    pub const UNSUPPORTED: &str =
        "template components do not use the supported owned BlobVariant array";
    pub const NO_BASE: &str = "variant has no base layout";
    pub const NOT_SUBTYPE: &str =
        "template component is not a subtype of its reflected variant base";
    pub const CHANGED: &str = "entity template changed during configuration resolution";
    pub const REGISTRATION_CHANGED: &str = "template registration changed since view creation";
    pub const READ_ARGS: &str = "read_fields expects one dense array of field paths";
    pub const READ_TABLE: &str = "read_fields expects a dense array of field paths";
    pub const DENSE: &str = "field paths must be a dense array";
    pub const DENSE_BOUND: &str = "select a dense array of at most 1024 field paths";
    pub const PATH_STRINGS: &str = "field paths must be strings";
    pub const DUPLICATE: &str = "duplicate field path";
    pub const NONEMPTY: &str = "select a nonempty dense array of field paths";
    pub const WRITE_ARGS: &str =
        "write_field expects path, value, expected current value and optional expected_fields";
    pub const WRITE_PATH: &str = "write_field expects a reflected field path";
    pub const WRITE_EXPECTED: &str =
        "template scalar changed before update. Read current fields and retry";
    pub const EXPECTED_MAP: &str = "expected_fields must be a nonempty field-path/value map";
    pub const EXPECTED_KEYS: &str =
        "expected_fields keys must be component-relative reflected field paths";
    pub const EXPECTED_BOUND: &str = "at most 1024 expected field paths may be selected";
    pub const EXPECTED_EMPTY: &str = "expected_fields must be nonempty when provided";
    pub const SNAPSHOT_CHANGED: &str =
        "Template field snapshot changed before update. Read current fields and retry";
    pub const OUTSIDE: &str = "resource field outside current snapshot";
    pub const WRITE_SHAPE: &str =
        "live resource writes support one contiguous 1/2/4/8-byte scalar per call";
    // resolve_resource_field.rs: the resolver of components[n].value.<path>
    pub const PARENT_STORAGE: &str = "field exceeds its reflected parent storage";
    pub const ARRAY_OVERLAP: &str = "blob array payload overlaps its resource descriptors";
    pub const VARIANT_DESCRIPTOR: &str =
        "invalid, absent or mismatched resource variant descriptor";
    pub const VARIANT_OVERLAP: &str = "variant payload overlaps its resource descriptors";
    pub const LEAF_SHAPE: &str =
        "resource writes require one naturally aligned, contiguous 1/2/4/8-byte value leaf";
    pub const DESCRIPTOR_OVERLAP: &str = "resource field overlaps a traversed container descriptor";
}

/// ShroudForge's `require_access`: the denial, then the operation's availability.
fn require_access(host: &dyn RuntimeHost, operation: Op) -> Result<(), String> {
    if let Some(reason) = runtime_denial_reason(host, operation) {
        return Err(reason);
    }
    let open = match operation {
        Op::AssetsWrite => crate::runtime::assets::resource::write_available(host, operation),
        _ => available(host, operation),
    };
    if !open {
        return Err(format!("{} is currently unavailable", operation.as_str()));
    }
    Ok(())
}

fn require_session(ecs: &Ecs, session: u64) -> Result<(), String> {
    if session == 0 || ecs.world_session() != session {
        return Err(text::SESSION.into());
    }
    Ok(())
}

/// The live registration of `hash` (ShroudForge's `runtime_provider::registration(name)` report): `Ok(None)` when
/// the report is unavailable, `Ok(Some(None))` when no row matches, the row otherwise; `Err` when the read failed.
pub fn registration(
    ecs: &Ecs,
    hash: u32,
) -> Result<Option<Option<KilnEcsRegistrationDetail>>, String> {
    let lacks = |_| super::registry::text::LACKS.to_owned();
    let table = ecs.world().map_err(lacks)?;
    let member = service_member!(table, KilnWorldApi, ecs_registration).map_err(lacks)?;
    let kind = ecs.world_kind();
    for _attempt in 0..4 {
        // SAFETY: all-zero records are valid (integers only).
        let mut detail: KilnEcsRegistrationDetail = unsafe { std::mem::zeroed() };
        detail.size = std::mem::size_of::<KilnEcsRegistrationDetail>() as u32;
        // SAFETY: kiln_world.h ecs_registration: a sized caller record.
        let result = unsafe { ecs.services().call(member, (kind, hash, &raw mut detail)) };
        match result {
            KILN_OK => return Ok(Some(Some(detail))),
            KILN_ERR_NOT_FOUND => return Ok(Some(None)),
            KILN_ERR_UNAVAILABLE => return Ok(None),
            r if r == KILN_ECS_ERR_UNREADABLE => continue,
            KILN_ERR_INVALID_ARGUMENT => return Err(super::registry::text::INVALID_RESPONSE.into()),
            _ if detail.status == KILN_ECS_UNAVAILABLE as u32 => return Ok(None),
            _ => return Err(super::registry::text::INVALID_RESPONSE.into()),
        }
    }
    Err(super::registry::text::CHANGED.into())
}

/// ShroudForge's `template_registration`: the template layout of the selector's registration, checked against the
/// archive registry ("select the actual configuration role, never a similarly named runtime layout").
pub fn template_registration(
    types: &Registry,
    report: Option<Option<KilnEcsRegistrationDetail>>,
    selector: usize,
) -> Result<usize, String> {
    let requested = types.get(selector).ok_or(text::UNRESOLVED_SELECTOR)?;
    let Some(row) = report else {
        return Err(text::REGISTRATION_UNAVAILABLE.into());
    };
    let row = row
        .filter(|row| {
            row.component == requested.qualified_hash
                || row.template_hash == requested.qualified_hash
        })
        .ok_or_else(|| {
            format!(
                "no live template registration for {}",
                requested.qualified_name
            )
        })?;
    // The registration's owner: the type whose hash the registration carries (the selector's, as looked up).
    if row.component != requested.qualified_hash || row.index > u32::from(u16::MAX) {
        return Err(text::OWNER_DISAGREES.into());
    }
    if row.template_hash == 0 {
        return Err(text::NO_LAYOUT.into());
    }
    let ty = selectors::by_hash(types, row.template_hash, selectors::HashKind::Qualified)?
        .ok_or(text::LAYOUT_ABSENT)?;
    if ty.primitive_type != "Struct" || ty.size == 0 || row.template_size != ty.size {
        return Err(text::LAYOUT_DISAGREES.into());
    }
    Ok(ty.index)
}

/// The selected variant of a `TemplateResource` snapshot: its one-based position, the byte offset of its value, and
/// what a field path traverses to reach it (the components array's descriptor and payload, the variant's descriptor).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Variant {
    pub position: usize,
    pub value: usize,
    array: usize,
    payload: usize,
    descriptor: usize,
    relative: u32,
    size: u32,
}

/// The one variant of `component` in a `TemplateResource` snapshot (ShroudForge's `select_variant`, which gives only
/// the position; its resolver finds the same value).
pub fn select_variant(
    types: &Registry,
    root: usize,
    component: usize,
    bytes: &[u8],
) -> Result<Variant, String> {
    let root = types.get(root).ok_or(text::ROOT_LAYOUT)?;
    let expected = types.get(component).ok_or(text::COMPONENT_LAYOUT)?;
    if root.primitive_type != "Struct" || root.size as usize > bytes.len() {
        return Err(text::INCOMPLETE_ROOT.into());
    }
    let fields = selectors::fields(types, root.index)?;
    let (_, components_field) = fields
        .iter()
        .find(|(_, field)| field.name == "components")
        .ok_or(text::NO_COMPONENTS_FIELD)?;
    let array = types
        .get(components_field.type_index)
        .ok_or(text::ARRAY_LAYOUT)?;
    let variant = array
        .inner_type
        .and_then(|index| types.get(index))
        .ok_or(text::ELEMENT_LAYOUT)?;
    if array.primitive_type != "BlobArray"
        || array.size != 8
        || variant.primitive_type != "BlobVariant"
        || variant.size != 12
    {
        return Err(text::UNSUPPORTED.into());
    }
    let u32_at = |at: usize| -> Option<u32> {
        bytes
            .get(at..at + 4)
            .map(|word| u32::from_le_bytes(word.try_into().unwrap_or_default()))
    };
    let at = usize::try_from(components_field.data_offset).map_err(|_| text::UNSUPPORTED)?;
    let (relative, count) = match (u32_at(at), at.checked_add(4).and_then(u32_at)) {
        (Some(relative), Some(count)) => (relative as usize, count as usize),
        _ => return Err(text::INCOMPLETE_ROOT.into()),
    };
    if count > bytes.len() / 12 {
        return Err(text::UNSUPPORTED.into());
    }
    let start = at.checked_add(relative).ok_or(text::OUTSIDE)?;
    if count != 0
        && start
            .checked_add(count * 12)
            .is_none_or(|end| end > bytes.len())
    {
        return Err(text::OUTSIDE.into());
    }
    let base = variant.inner_type.ok_or(text::NO_BASE)?;
    if !selectors::inheritance(types, expected.index)?.contains(&base) {
        return Err(text::NOT_SUBTYPE.into());
    }
    let mut found = None;
    for index in 0..count {
        let descriptor = start + index * 12;
        let (Some(hash), Some(offset), Some(size)) = (
            u32_at(descriptor),
            u32_at(descriptor + 4),
            u32_at(descriptor + 8),
        ) else {
            return Err(text::OUTSIDE.into());
        };
        if hash != expected.qualified_hash {
            continue;
        }
        let value = (descriptor + 4)
            .checked_add(offset as usize)
            .ok_or(text::OUTSIDE)?;
        if (size as usize) < expected.size as usize
            || value
                .checked_add(expected.size as usize)
                .is_none_or(|end| end > bytes.len())
        {
            return Err(text::OUTSIDE.into());
        }
        let selected = Variant {
            position: index + 1,
            value,
            array: at,
            payload: start,
            descriptor,
            relative: offset,
            size,
        };
        if found.replace(selected).is_some() {
            return Err(format!(
                "template has multiple {} variants",
                expected.qualified_name
            ));
        }
    }
    found.ok_or_else(|| format!("template has no {} configuration", expected.qualified_name))
}

/// ShroudForge's `RuntimeTemplateComponent`.
pub struct TemplateComponent {
    ecs: Rc<Ecs>,
    assets: Rc<Assets>,
    host: Rc<dyn RuntimeHost>,
    key: KilnAssetKey,
    /// The package the view was made for (ShroudForge's `owner_mod`).
    owner: String,
    root_type: usize,
    component_type: usize,
    text_key: String,
    session: u64,
    write_fault: RefCell<Option<String>>,
}

/// `runtime.ecs.get_template_component(entity, component_type)`: the view, or `nil, reason`.
pub fn get_template_component(
    lua: &Lua,
    ecs: &Rc<Ecs>,
    host: &Rc<dyn RuntimeHost>,
    args: &FunctionArgs,
) -> mlua::Result<(Value, Option<String>)> {
    let get = || -> Result<TemplateComponent, String> {
        require_access(host.as_ref(), Op::EcsRead)?;
        if args.len() != 2 {
            return Err(text::ARGS.into());
        }
        let session = ecs.world_session();
        require_session(ecs, session)?;
        let entity = args.get::<u32>(0).map_err(|error| error.to_string())?;
        let types = ecs.types();
        let selector = selectors::resolve(
            types,
            args.get::<Value>(1).map_err(|error| error.to_string())?,
        )?;
        let requested = types.get(selector).ok_or(text::UNRESOLVED_SELECTOR)?;
        let report = registration(ecs, requested.qualified_hash)?;
        let component = template_registration(types, report, selector)?;
        let guid = ecs.template_uuid(entity).ok_or(text::NO_UUID)?;
        let root = types
            .by_qualified_name(TEMPLATE_TYPE)
            .ok_or(text::NO_TEMPLATE_TYPE)?;
        let key = KilnAssetKey {
            guid,
            r#type: root.qualified_hash,
            part: 0,
        };
        let assets = crate::runtime::assets::of_vm(lua).ok_or(NO_RESOURCE_READ)?;
        let bytes = read(&assets, &key)?;
        select_variant(types, root.index, component, &bytes)?;
        if ecs.template_uuid(entity) != Some(guid) {
            return Err(text::CHANGED.into());
        }
        require_session(ecs, session)?;
        let component_name = &types
            .get(component)
            .ok_or(text::COMPONENT_LAYOUT)?
            .qualified_name;
        Ok(TemplateComponent {
            ecs: ecs.clone(),
            assets,
            host: host.clone(),
            key,
            owner: host.owner_context().id().to_owned(),
            root_type: root.index,
            component_type: component,
            text_key: format!("{}/{TEMPLATE_TYPE}/0/{component_name}", guid_text(&guid)),
            session,
            write_fault: RefCell::new(None),
        })
    };
    Ok(match get() {
        Ok(view) => (Value::UserData(lua.create_userdata(view)?), None),
        Err(reason) => (Value::Nil, Some(reason)),
    })
}

/// The bridge's `resource_read`: the loaded snapshot, or ShroudForge's text for a resource it cannot find.
fn read(assets: &Assets, key: &KilnAssetKey) -> Result<Vec<u8>, String> {
    match assets.read(key)? {
        Read::Bytes(bytes) => Ok(bytes),
        Read::Missing => Err(crate::runtime::assets::text::RESOURCE_CHANGED.into()),
    }
}

/// A field of the selected variant, as ShroudForge's resolver finds `components[n].value.<path>`
/// (resolve_resource_field.rs:18-223): the components array's step (its descriptor inside the root, its payload past
/// both), the variant's step (a payload of the reflected size, past its own descriptor and the root), the
/// component-relative plan placed at the variant's value, then the leaf (one naturally aligned 1/2/4/8-byte scalar in
/// the snapshot, on no traversed descriptor, holding a valid value).
fn selected_field(
    assets: &Assets,
    root: usize,
    component: usize,
    variant: &Variant,
    path: &str,
    bytes: &[u8],
) -> Result<crate::runtime::ecs::fields::InlineField, String> {
    let resolve = || -> Result<_, String> {
        let types = assets.types();
        let root_size = types.get(root).ok_or(text::ROOT_LAYOUT)?.size as usize;
        let expected = types.get(component).ok_or(text::COMPONENT_LAYOUT)?;
        let array_end = variant.array.checked_add(8).ok_or(text::OUTSIDE)?;
        if array_end > root_size {
            return Err(text::PARENT_STORAGE.into());
        }
        if variant.payload < array_end || variant.payload < root_size {
            return Err(text::ARRAY_OVERLAP.into());
        }
        if variant.relative == 0 || variant.size != expected.size {
            return Err(text::VARIANT_DESCRIPTOR.into());
        }
        let descriptor_end = variant.descriptor.checked_add(12).ok_or(text::OUTSIDE)?;
        if variant.value < descriptor_end || variant.value < root_size {
            return Err(text::VARIANT_OVERLAP.into());
        }
        let mut field = (*assets.inline_field(component, path)?).clone();
        field.offset = field
            .offset
            .checked_add(variant.value)
            .ok_or(text::OUTSIDE)?;
        let range = field.byte_range();
        let width = range.len();
        if !matches!(width, 1 | 2 | 4 | 8)
            || range.start % width != 0
            || field.leaves.len() != 1
            || field.leaves[0].1 != (0..width)
        {
            return Err(text::LEAF_SHAPE.into());
        }
        let leaf = bytes.get(range.clone()).ok_or(text::OUTSIDE)?;
        let on = |start: usize, end: usize| start < range.end && range.start < end;
        if on(variant.array, array_end) || on(variant.descriptor, descriptor_end) {
            return Err(text::DESCRIPTOR_OVERLAP.into());
        }
        super::cas::validate_runtime_scalar(field.leaves[0].0, leaf)?;
        Ok(field)
    };
    resolve().map_err(|reason| format!("{path}: {reason}"))
}

impl TemplateComponent {
    /// ShroudForge's `ownership_identity`: the view's resource and session, for the view's own package only.
    pub fn ownership_identity(&self, owner: &str) -> Result<(KilnAssetKey, u64), String> {
        if self.owner != owner {
            return Err(crate::runtime::overrides::targets::template::text::OTHER_MOD.into());
        }
        Ok((self.key, self.session))
    }

    /// ShroudForge's `snapshot`: the access and session again, the registration unchanged, the current bytes and the
    /// selected variant.
    fn snapshot(&self) -> Result<(Vec<u8>, Variant), String> {
        require_access(self.host.as_ref(), Op::EcsRead)?;
        require_session(&self.ecs, self.session)?;
        let types = self.ecs.types();
        let hash = types
            .get(self.component_type)
            .ok_or(text::COMPONENT_LAYOUT)?
            .qualified_hash;
        let report = registration(&self.ecs, hash)?;
        if template_registration(types, report, self.component_type)? != self.component_type {
            return Err(text::REGISTRATION_CHANGED.into());
        }
        let bytes = read(&self.assets, &self.key)?;
        let variant = select_variant(types, self.root_type, self.component_type, &bytes)?;
        require_session(&self.ecs, self.session)?;
        Ok((bytes, variant))
    }

    fn read_fields(&self, lua: &Lua, args: MultiValue) -> Result<Table, String> {
        if args.len() != 1 {
            return Err(text::READ_ARGS.into());
        }
        let Some(Value::Table(paths)) = args.front() else {
            return Err(text::READ_TABLE.into());
        };
        let mut selected = BTreeSet::new();
        for pair in paths.clone().pairs::<Value, Value>() {
            let (key, path) = pair.map_err(|error| error.to_string())?;
            let Value::Integer(index) = key else {
                return Err(text::DENSE.into());
            };
            if index <= 0 || index as usize > paths.raw_len() || selected.len() >= MAX_PATHS {
                return Err(text::DENSE_BOUND.into());
            }
            let Value::String(path) = path else {
                return Err(text::PATH_STRINGS.into());
            };
            if !selected.insert(path.to_str().map_err(|error| error.to_string())?.to_owned()) {
                return Err(text::DUPLICATE.into());
            }
        }
        if selected.is_empty() || selected.len() != paths.raw_len() {
            return Err(text::NONEMPTY.into());
        }
        let (bytes, variant) = self.snapshot()?;
        let values = self.ecs.values().map_err(|error| error.to_string())?;
        let result = lua
            .create_table_with_capacity(0, selected.len())
            .map_err(|error| error.to_string())?;
        for path in selected {
            let field = selected_field(
                &self.assets,
                self.root_type,
                self.component_type,
                &variant,
                &path,
                &bytes,
            )?;
            result
                .raw_set(path.as_str(), field.read(lua, &values, &bytes)?)
                .map_err(|error| error.to_string())?;
        }
        require_session(&self.ecs, self.session)?;
        Ok(result)
    }

    fn expect_fields(
        &self,
        variant: &Variant,
        before: &[u8],
        expected: &Table,
    ) -> Result<(), String> {
        let values = self.ecs.values().map_err(|error| error.to_string())?;
        let mut candidate = before.to_vec();
        let mut mask = vec![0; before.len()];
        let mut count = 0;
        for pair in expected.clone().pairs::<Value, Value>() {
            let (path, expected) = pair.map_err(|error| error.to_string())?;
            let Value::String(path) = path else {
                return Err(text::EXPECTED_KEYS.into());
            };
            count += 1;
            if count > MAX_PATHS {
                return Err(text::EXPECTED_BOUND.into());
            }
            let path = path.to_str().map_err(|error| error.to_string())?.to_owned();
            selected_field(
                &self.assets,
                self.root_type,
                self.component_type,
                variant,
                &path,
                before,
            )?
            .write_into(&values, &expected, &mut candidate, &mut mask)
            .map_err(|reason| format!("expected {path}: {reason}"))?;
        }
        if count == 0 {
            return Err(text::EXPECTED_EMPTY.into());
        }
        if candidate.as_slice() != before {
            return Err(text::SNAPSHOT_CHANGED.into());
        }
        Ok(())
    }

    fn write_field(&self, args: MultiValue) -> Result<(), String> {
        if let Some(reason) = self.write_fault.borrow().as_ref() {
            return Err(reason.clone());
        }
        if args.len() != 3 && args.len() != 4 {
            return Err(text::WRITE_ARGS.into());
        }
        let Some(Value::String(path)) = args.front() else {
            return Err(text::WRITE_PATH.into());
        };
        let path = path.to_str().map_err(|error| error.to_string())?.to_owned();
        require_access(self.host.as_ref(), Op::EcsWrite)?;
        require_access(self.host.as_ref(), Op::AssetsWrite)?;
        let (before, variant) = self.snapshot()?;
        let field = selected_field(
            &self.assets,
            self.root_type,
            self.component_type,
            &variant,
            &path,
            &before,
        )?;
        let range = field.byte_range();
        let values = self.ecs.values().map_err(|error| error.to_string())?;
        let mut expected = before.clone();
        field.write_into(&values, &args[2], &mut expected, &mut vec![0; before.len()])?;
        if expected[range.clone()] != before[range.clone()] {
            return Err(text::WRITE_EXPECTED.into());
        }
        if args.len() == 4 {
            let Value::Table(expected_fields) = &args[3] else {
                return Err(text::EXPECTED_MAP.into());
            };
            self.expect_fields(&variant, &before, expected_fields)?;
        }
        let mut after = before.clone();
        let mut mask = vec![0u8; before.len()];
        field.write_into(&values, &args[1], &mut after, &mut mask)?;
        // kiln.assets writes one naturally aligned scalar: ShroudForge's provider refuses anything wider the same way.
        if !matches!(range.len(), 1 | 2 | 4 | 8)
            || mask.iter().filter(|byte| **byte != 0).count() != range.len()
        {
            return Err(text::WRITE_SHAPE.into());
        }
        require_session(&self.ecs, self.session)?;
        if let Some(facts) = self.assets.facts() {
            crate::runtime::overrides::check_write(
                facts.ownership(),
                authority(&self.host, &self.assets).as_ref(),
                &crate::runtime::overrides::StorageKey::Resource(ownership_key(&self.key)),
            )?;
        }
        // sfi R3: the write names the view's package and the field path it gave, for Kiln's registry report
        if let Err(reason) = self.assets.compare_exchange(
            &self.key,
            &before,
            range.start,
            &after[range.clone()],
            self.session,
            &crate::runtime::assets::WriteName::package(&self.owner, &path),
        ) {
            if reason.to_lowercase().contains("uncertain") {
                *self.write_fault.borrow_mut() = Some(reason.clone());
            }
            return Err(reason);
        }
        Ok(())
    }
}

impl UserData for TemplateComponent {
    fn add_fields<F: mlua::UserDataFields<Self>>(fields: &mut F) {
        fields.add_field_method_get("key", |_, this| Ok(this.text_key.clone()));
        fields.add_field_method_get("scope", |_, _| Ok("shared-template"));
        fields.add_field_method_get("session_id", |_, this| Ok(this.session));
        fields.add_field_method_get("type", |lua, this| {
            this.ecs.type_value(lua, this.component_type)
        });
    }
    fn add_methods<M: mlua::UserDataMethods<Self>>(methods: &mut M) {
        methods.add_method("read_fields", |lua, this, args: MultiValue| {
            Ok(match this.read_fields(lua, args) {
                Ok(values) => (Some(values), None),
                Err(reason) => (None, Some(reason)),
            })
        });
        methods.add_method("write_field", |_, this, args: MultiValue| {
            Ok(match this.write_field(args) {
                Ok(()) => (true, None),
                Err(reason) => (false, Some(reason)),
            })
        });
    }
}
