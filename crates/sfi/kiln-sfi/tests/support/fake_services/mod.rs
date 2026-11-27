// SPDX-License-Identifier: MIT
//! Real C-ABI fake service tables for the interpreter runtime's tests,
//! built from kiln-abi's generated types, so a runtime family can be coded and tested
//! against Kiln's agreed signatures before (or without) the real core.
//!
//! - Tables: a kiln.world v4 subset (with the named services over a per-owner [`NamedServices`]), kiln.types (status, three types through
//!   `type_count` / `type_at` / `type_info`: [`FAKE_TYPES`]; `ecs_registry` over a
//!   per-owner [`EcsCatalog`], whose types `type_at` / `type_info` also answer), kiln.launch, kiln.hooks, kiln.client,
//!   kiln.assets, kiln.build (with its task-thread grid `grid_for_session`: [`set_grid_spec`], [`grid_calls`]),
//!   kiln.caps (rows a test sets, read with `find`, `count`,
//!   `at` and `generation`; kiln.world's `game_world_session` answers per thread owner, and a
//!   scripted sequence first),
//!   kiln.net (kiln-abi's `KilnNetApi` with the Steam-messages members
//!   `steam_*` at 128..176, as kiln_net.h describes them; per-owner state [`NetFake`]),
//!   plus `kiln.fake.timed`, a member whose reply carries
//!   `source_wait_ns` (the shape the native rows with bounded waits report).
//! - Knobs per owner: busy, stale, unreadable, unavailable, uncertain, conflict. Each ECS
//!   member answers a knob only with a code kiln_world.h lists for that member, mirrored
//!   in its record exactly as the real member does (`KilnObjectInfo.status`, a query's
//!   `*state`, `KilnMutationReply.status`, `KilnEcsWritePolicy.status`). A knob the member
//!   cannot produce is not applied and the call answers as with `Knob::Ok`: busy only for
//!   a frame-quiet query and `ecs_compare_exchange`, conflict and uncertain only for
//!   `ecs_compare_exchange`, nothing but unavailable for `ecs_write_policy`. Members of the
//!   other services know only "unavailable" and answer every other knob with it too.
//! - Arguments are checked as the real members check them before anything else: owner 0,
//!   NULL or short caller records (`size` below the record's), a ref with a short size, no
//!   entity or an unknown world kind answer `KILN_ERR_INVALID_ARGUMENT` and count the call.
//!   Optional out pointers (`*required`, `*state`, `confirmed`) may be NULL.
//! - Counters per owner: calls per member, retries (a call right after a refused call of
//!   the same member), waits (calls that reported a source wait), and hops
//!   (`support::posts_total`).
//! - Table shapes per lookup thread: missing, short prefix, older version, misaligned
//!   header, a NULL member, or a private copy the test may change after a bind.
//!
//! Include it next to `support` (`#[path = "support/fake_services/mod.rs"] mod
//! fake_services;`); it is not part of `support`, so shared bodies that other test packages include do not
//! compile it. It uses no test-only environment variable and no dev-dependency.
#![allow(dead_code)]

use super::support::{self, posts_total};
use kiln_abi::services::kiln_assets::*;
use kiln_abi::services::kiln_build::*;
use kiln_abi::services::kiln_caps::*;
use kiln_abi::services::kiln_client::*;
use kiln_abi::services::kiln_hooks::*;
use kiln_abi::services::kiln_launch::*;
use kiln_abi::*;
use std::cell::{Cell, RefCell};
use std::collections::{BTreeMap, HashMap};
use std::ffi::c_void;
use std::sync::{LazyLock, Mutex};

// ---- knobs and counters ----

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Knob {
    #[default]
    Ok,
    Busy,
    Stale,
    Unreadable,
    Unavailable,
    Uncertain,
    Conflict,
}
impl Knob {
    /// The ECS members' answer (kiln_world.h result codes).
    pub fn ecs_result(self) -> KilnResult {
        match self {
            Knob::Ok => KILN_OK,
            Knob::Busy => KILN_ECS_ERR_BUSY,
            Knob::Stale => KILN_ECS_ERR_STALE,
            Knob::Unreadable => KILN_ECS_ERR_UNREADABLE,
            Knob::Unavailable => KILN_ERR_UNAVAILABLE,
            Knob::Uncertain => KILN_ECS_ERR_UNCERTAIN,
            Knob::Conflict => KILN_ECS_ERR_CONFLICT,
        }
    }
    /// `KilnMutationReply.status` for `ecs_compare_exchange`.
    pub fn ecs_status(self) -> u32 {
        (match self {
            Knob::Ok => KILN_ECS_OK,
            Knob::Busy => KILN_ECS_BUSY,
            Knob::Stale => KILN_ECS_STALE,
            Knob::Unreadable => KILN_ECS_UNREADABLE,
            Knob::Unavailable => KILN_ECS_UNAVAILABLE,
            Knob::Uncertain => KILN_ECS_UNCERTAIN,
            Knob::Conflict => KILN_ECS_CONFLICT,
        }) as u32
    }
    /// The knob as a stable read can answer it (kiln_world.h: busy is frame-quiet only;
    /// conflict and uncertain are write outcomes): those three read as `Knob::Ok`.
    pub fn read(self) -> Knob {
        match self {
            Knob::Busy | Knob::Conflict | Knob::Uncertain => Knob::Ok,
            other => other,
        }
    }
    /// `KILN_ECS_*` for the knob (the status a record carries).
    pub fn status(self) -> u32 {
        self.ecs_status()
    }
    /// The other services' answer: only "unavailable" is theirs.
    pub fn plain_result(self) -> KilnResult {
        if self == Knob::Ok {
            KILN_OK
        } else {
            KILN_ERR_UNAVAILABLE
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Counters {
    pub calls: HashMap<&'static str, u64>,
    pub retries: u64,
    pub waits: u64,
    pub hops: u64,
}
impl Counters {
    pub fn calls(&self, member: &str) -> u64 {
        self.calls.get(member).copied().unwrap_or(0)
    }
    pub fn total_calls(&self) -> u64 {
        self.calls.values().sum()
    }
}

#[derive(Default)]
struct OwnerState {
    knob: Knob,
    counters: Counters,
    last_refused: Option<&'static str>,
    /// What `kiln.fake.timed` reports as `source_wait_ns`.
    report_wait_ns: u64,
    subscriptions: Vec<(usize, usize)>,
}
static STATE: LazyLock<Mutex<HashMap<KilnOwner, OwnerState>>> = LazyLock::new(Default::default);

fn with<R>(owner: KilnOwner, f: impl FnOnce(&mut OwnerState) -> R) -> R {
    f(STATE.lock().unwrap().entry(owner).or_default())
}
pub fn set_knob(owner: KilnOwner, knob: Knob) {
    with(owner, |s| s.knob = knob);
}
pub fn counters(owner: KilnOwner) -> Counters {
    let mut counters = with(owner, |s| s.counters.clone());
    counters.hops = posts_total(owner);
    counters
}
/// What `kiln.fake.timed` reports as `source_wait_ns` for `owner`.
pub fn set_reported_wait(owner: KilnOwner, wait_ns: u64) {
    with(owner, |s| s.report_wait_ns = wait_ns);
}

thread_local! {
    /// Which owner counts for the members that take none (status, capabilities, ...).
    static THREAD_OWNER: Cell<KilnOwner> = const { Cell::new(0) };
    /// Runs inside `kiln.fake.timed`'s call, on the calling thread (a test advances its
    /// fake clock here: the duration of the native call).
    static ON_TIMED_CALL: RefCell<Option<Box<dyn Fn()>>> = const { RefCell::new(None) };
}
/// Owner-less members called on this thread count for `owner`.
pub fn bind_thread(owner: KilnOwner) {
    THREAD_OWNER.set(owner);
}
pub fn on_timed_call(f: impl Fn() + 'static) {
    ON_TIMED_CALL.with_borrow_mut(|slot| *slot = Some(Box::new(f)));
}

/// Counts one call of `member` and gives the knob it answers with.
fn enter(owner: KilnOwner, member: &'static str) -> Knob {
    with(owner, |s| {
        *s.counters.calls.entry(member).or_default() += 1;
        if s.last_refused == Some(member) {
            s.counters.retries += 1;
        }
        s.last_refused = (s.knob != Knob::Ok).then_some(member);
        s.knob
    })
}
fn enter_thread(member: &'static str) -> Knob {
    enter(THREAD_OWNER.get(), member)
}

// ---- kiln.world v4 subset ----

unsafe extern "C" fn world_status(out: *mut KilnWorldStatus) -> KilnResult {
    let knob = enter_thread("status");
    if out.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    knob.plain_result()
}
unsafe extern "C" fn world_capabilities() -> u32 {
    if enter_thread("capabilities") == Knob::Ok {
        u32::MAX
    } else {
        0
    }
}
fn bad_ref(object: *const KilnObjectRef) -> bool {
    // SAFETY: read only when non-NULL; the caller's ref.
    object.is_null()
        || unsafe {
            (*object).size < core::mem::size_of::<KilnObjectRef>() as u32
                || (*object).entity == 0
                || (*object).world_kind >= KILN_WORLD_KINDS as u32
        }
}
/// The live session of every world kind in these fakes.
pub const FAKE_SESSION: u64 = 1;
/// kiln_world.h: `entity_open(owner, reload_epoch, world_kind, expected_session, entity,
/// out)`. The ref carries the caller's `reload_epoch` and the live session
/// ([`FAKE_SESSION`]); a nonzero `expected_session` that is not the live one is stale.
unsafe extern "C" fn entity_open(
    owner: KilnOwner,
    reload_epoch: u64,
    world_kind: u32,
    expected_session: u64,
    entity: KilnEntity,
    out: *mut KilnObjectRef,
) -> KilnResult {
    let knob = enter(owner, "entity_open").read();
    if owner == 0 || out.is_null() || entity == 0 || world_kind >= KILN_WORLD_KINDS as u32 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's out record; cleared first, as the real member does.
    unsafe { *out = core::mem::zeroed() };
    if knob != Knob::Ok {
        return knob.ecs_result();
    }
    if let Some(answer) = ecs_world(owner, |world| {
        if world_kind != world.world_kind {
            return Err(KILN_ERR_UNAVAILABLE);
        }
        if expected_session != 0 && expected_session != world.session {
            return Err(KILN_ECS_ERR_STALE);
        }
        let found = world.entities.get(&entity).ok_or(KILN_ERR_NOT_FOUND)?;
        Ok(model_ref(world, entity, found.generation, reload_epoch))
    }) {
        return match answer {
            // SAFETY: as above.
            Ok(object) => unsafe {
                *out = object;
                KILN_OK
            },
            Err(code) => code,
        };
    }
    if expected_session != 0 && expected_session != FAKE_SESSION {
        return KILN_ECS_ERR_STALE;
    }
    // SAFETY: as above.
    unsafe {
        *out = KilnObjectRef {
            size: core::mem::size_of::<KilnObjectRef>() as u32,
            entity,
            generation: 1,
            world_kind,
            session: FAKE_SESSION,
            layout_stamp: 1,
            reload_epoch,
        }
    };
    KILN_OK
}
/// `*out` (its size set by the caller) always gets the facts and the `KILN_ECS_*` status.
unsafe extern "C" fn entity_validate(
    owner: KilnOwner,
    object: *const KilnObjectRef,
    out: *mut KilnObjectInfo,
) -> KilnResult {
    let knob = enter(owner, "entity_validate").read();
    // SAFETY: `out` read only when non-NULL.
    if owner == 0
        || bad_ref(object)
        || out.is_null()
        || unsafe { (*out).size } < core::mem::size_of::<KilnObjectInfo>() as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: both checked above.
    unsafe {
        let object = &*object;
        let size = (*out).size;
        *out = KilnObjectInfo {
            size,
            status: knob.status(),
            entity: object.entity,
            generation: object.generation,
            world_kind: object.world_kind,
            flags: if knob == Knob::Ok {
                KILN_OBJECT_ALIVE as u32
            } else {
                0
            },
            session: object.session,
            layout_stamp: object.layout_stamp,
            reload_epoch: object.reload_epoch,
        };
    }
    if knob == Knob::Ok
        // SAFETY: checked above.
        && let Some(result) = ecs_world(owner, |world| model_entity(world, unsafe { &*object }).map(|_| ()))
    {
        let code = result.err().unwrap_or(KILN_OK);
        // SAFETY: checked above.
        unsafe {
            (*out).status = status_of(code);
            (*out).flags = if code == KILN_OK {
                KILN_OBJECT_ALIVE as u32
            } else {
                0
            };
        }
        return code;
    }
    knob.ecs_result()
}
/// `*out` (its size set) gets the status and, with `Knob::Ok`, a server world in
/// session 1 with an exact profile.
unsafe extern "C" fn ecs_context_snapshot(out: *mut KilnEcsContextInfo) -> KilnResult {
    let knob = enter_thread("ecs_context_snapshot").read();
    // SAFETY: read only when non-NULL.
    if out.is_null() || unsafe { (*out).size } < core::mem::size_of::<KilnEcsContextInfo>() as u32 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let knob = match knob {
        Knob::Stale => Knob::Ok, // a context read is never stale
        other => other,
    };
    // SAFETY: checked above.
    unsafe {
        let size = (*out).size;
        *out = core::mem::zeroed();
        (*out).size = size;
        (*out).status = knob.status();
        if knob == Knob::Ok {
            (*out).present_mask = 1u32 << KILN_WORLD_SERVER;
            (*out).exact_profile = 1;
            (*out).sessions[KILN_WORLD_SERVER as usize] = FAKE_SESSION;
            (*out).layout_stamp = 1;
            if let Some((kind, session, exact)) = ecs_world(THREAD_OWNER.get(), |w| {
                (w.world_kind, w.session, w.exact_profile)
            }) {
                (*out).present_mask = 1u32 << kind;
                (*out).exact_profile = u32::from(exact);
                (*out).sessions = [0; KILN_WORLD_KINDS as usize];
                (*out).sessions[kind as usize] = session;
            }
        }
    }
    knob.ecs_result()
}
/// No matches. `*required` and `*state` are optional; `*state` mirrors the result and an
/// invalid argument leaves it untouched. Busy only for a frame-quiet request.
unsafe extern "C" fn ecs_query(
    owner: KilnOwner,
    request: *const KilnEcsQueryRequest,
    out: *mut KilnObjectRef,
    capacity: u32,
    required: *mut u32,
    state: *mut u32,
) -> KilnResult {
    let mut knob = enter(owner, "ecs_query");
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = 0 };
    }
    // SAFETY: read only when non-NULL.
    if owner == 0
        || request.is_null()
        || unsafe { (*request).size } < core::mem::size_of::<KilnEcsQueryRequest>() as u32
        || unsafe { (*request).world_kind } >= KILN_WORLD_KINDS as u32
        || (capacity != 0 && out.is_null())
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let quiet = unsafe { (*request).flags } & KILN_ECS_QUERY_FRAME_QUIET as u32 != 0;
    if knob != Knob::Busy || !quiet {
        knob = knob.read();
    }
    if knob == Knob::Ok
        && let Some(answer) = ecs_world(owner, |world| {
            // SAFETY: checked above; the component list is the caller's.
            let request = unsafe { &*request };
            if request.world_kind != world.world_kind {
                return (KILN_ERR_UNAVAILABLE, Vec::new());
            }
            if world.query_changed > 0 {
                world.query_changed -= 1;
                return (KILN_WORLD_ERR_CHANGED, Vec::new());
            }
            let wanted = unsafe {
                std::slice::from_raw_parts(request.components, request.component_count as usize)
            };
            let matches = world
                .entities
                .iter()
                .filter(|(_, e)| wanted.iter().all(|c| e.components.contains_key(c)))
                .map(|(id, e)| model_ref(world, *id, e.generation, owner))
                .collect::<Vec<_>>();
            (KILN_OK, matches)
        })
    {
        let (code, matches) = answer;
        let code = if code == KILN_OK && (capacity as usize) < matches.len() {
            KILN_ERR_BUFFER_TOO_SMALL
        } else {
            code
        };
        if !required.is_null() && (code == KILN_OK || code == KILN_ERR_BUFFER_TOO_SMALL) {
            // SAFETY: live out pointer.
            unsafe { *required = matches.len() as u32 };
        }
        if code == KILN_OK {
            // SAFETY: `out` holds `capacity` refs.
            unsafe { std::ptr::copy_nonoverlapping(matches.as_ptr(), out, matches.len()) };
        }
        if !state.is_null() {
            // SAFETY: live out pointer.
            unsafe {
                *state = if code == KILN_ERR_BUFFER_TOO_SMALL {
                    KILN_ECS_OK as u32
                } else {
                    status_of(code)
                }
            };
        }
        return code;
    }
    if !state.is_null() {
        // SAFETY: live out pointer.
        unsafe { *state = knob.status() };
    }
    knob.ecs_result()
}
/// `*required` (optional) is 0 on a refusal and the component size (4) on success; a
/// short capacity is `KILN_ERR_BUFFER_TOO_SMALL` with nothing copied. Always a stable copy.
unsafe extern "C" fn ecs_snapshot(
    owner: KilnOwner,
    object: *const KilnObjectRef,
    component: u32,
    out: *mut c_void,
    capacity: u32,
    required: *mut u32,
) -> KilnResult {
    let knob = enter(owner, "ecs_snapshot").read();
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = 0 };
    }
    if owner == 0 || bad_ref(object) || component == 0 || (capacity != 0 && out.is_null()) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.ecs_result();
    }
    // SAFETY: checked above.
    let object_ref = unsafe { *object };
    if let Some(answer) = ecs_world(owner, |world| {
        if world.unreadable {
            return Err(KILN_ECS_ERR_UNREADABLE);
        }
        model_entity(world, &object_ref).and_then(|entity| {
            entity
                .components
                .get(&component)
                .cloned()
                .ok_or(KILN_ERR_NOT_FOUND)
        })
    }) {
        let bytes = match answer {
            Ok(bytes) => bytes,
            Err(code) => return code,
        };
        if !required.is_null() {
            // SAFETY: live out pointer.
            unsafe { *required = bytes.len() as u32 };
        }
        if (capacity as usize) < bytes.len() {
            return KILN_ERR_BUFFER_TOO_SMALL;
        }
        // SAFETY: `out` holds `capacity` bytes.
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out.cast::<u8>(), bytes.len()) };
        return KILN_OK;
    }
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = FAKE_COMPONENT_SIZE };
    }
    if capacity < FAKE_COMPONENT_SIZE {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    // SAFETY: `out` holds `capacity` bytes (the caller's contract).
    unsafe { std::ptr::write_bytes(out.cast::<u8>(), 0, FAKE_COMPONENT_SIZE as usize) };
    KILN_OK
}
/// Every component of these fakes is this many bytes.
pub const FAKE_COMPONENT_SIZE: u32 = 4;
/// `ecs_snapshot_many` over an owner's [`EcsWorld`] as kiln_world.h describes it: the specs' components packed in
/// spec order (an absent one takes no room, its presence says ABSENT), `*required` the packed size (0 on a refusal), a
/// short capacity `KILN_ERR_BUFFER_TOO_SMALL` with nothing copied, every presence the refusal when the call fails. A
/// spec flag other than `KILN_ECS_READ_FRAME_QUIET` is an invalid argument. Without a world: KILN_ERR_UNAVAILABLE.
unsafe extern "C" fn ecs_snapshot_many(
    owner: KilnOwner,
    object: *const KilnObjectRef,
    specs: *const KilnEcsSnapshotSpec,
    count: u32,
    out: *mut c_void,
    capacity: u32,
    required: *mut u32,
    presence: *mut u32,
) -> KilnResult {
    let knob = enter(owner, "ecs_snapshot_many").read();
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = 0 };
    }
    if owner == 0
        || bad_ref(object)
        || count == 0
        || specs.is_null()
        || (capacity != 0 && out.is_null())
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's `count` specs.
    let specs = unsafe { std::slice::from_raw_parts(specs, count as usize) };
    if specs
        .iter()
        .any(|spec| spec.component == 0 || spec.flags & !(KILN_ECS_READ_FRAME_QUIET as u32) != 0)
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let mark = |status: u32| {
        if !presence.is_null() {
            for index in 0..count as usize {
                // SAFETY: the caller's `count` presence slots.
                unsafe { *presence.add(index) = status };
            }
        }
    };
    if knob != Knob::Ok {
        mark(knob.status());
        return knob.ecs_result();
    }
    // SAFETY: checked above.
    let object_ref = unsafe { *object };
    let answer = ecs_world(owner, |world| {
        if world.unreadable {
            return Err(KILN_ECS_ERR_UNREADABLE);
        }
        model_entity(world, &object_ref).map(|entity| {
            specs
                .iter()
                .map(|spec| entity.components.get(&spec.component).cloned())
                .collect::<Vec<_>>()
        })
    })
    .unwrap_or(Err(KILN_ERR_UNAVAILABLE));
    let parts = match answer {
        Ok(parts) => parts,
        Err(code) => {
            mark(status_of(code));
            return code;
        }
    };
    let total: usize = parts.iter().flatten().map(Vec::len).sum();
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = total as u32 };
    }
    for (index, part) in parts.iter().enumerate() {
        if !presence.is_null() {
            let status = if part.is_some() {
                KILN_ECS_OK
            } else {
                KILN_ECS_ABSENT
            } as u32;
            // SAFETY: the caller's `count` presence slots.
            unsafe { *presence.add(index) = status };
        }
    }
    if (capacity as usize) < total {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    let mut at = 0usize;
    for part in parts.iter().flatten() {
        // SAFETY: `out` holds `capacity` >= `total` bytes.
        unsafe {
            std::ptr::copy_nonoverlapping(part.as_ptr(), out.cast::<u8>().add(at), part.len())
        };
        at += part.len();
    }
    KILN_OK
}
/// KILN_OK with `out->status` OK (writable), or KILN_ERR_UNAVAILABLE with UNAVAILABLE; no
/// other knob applies.
unsafe extern "C" fn ecs_write_policy(
    world_kind: u32,
    component: u32,
    out: *mut KilnEcsWritePolicy,
) -> KilnResult {
    let knob = match enter_thread("ecs_write_policy") {
        Knob::Unavailable => Knob::Unavailable,
        _ => Knob::Ok,
    };
    // SAFETY: read only when non-NULL.
    if out.is_null()
        || unsafe { (*out).size } < core::mem::size_of::<KilnEcsWritePolicy>() as u32
        || world_kind >= KILN_WORLD_KINDS as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    unsafe {
        let size = (*out).size;
        *out = core::mem::zeroed();
        (*out).size = size;
        (*out).world_kind = world_kind;
        (*out).component = component;
        (*out).status = knob.status();
        if knob == Knob::Ok {
            (*out).component_size = FAKE_COMPONENT_SIZE;
            (*out).flags = (KILN_ECS_WRITE_POLICY_WRITABLE
                | KILN_ECS_WRITE_POLICY_TRACKING_KNOWN
                | KILN_ECS_WRITE_POLICY_PLAIN) as u32;
        } else {
            (*out).why_code = KILN_ECS_WRITE_WHY_NO_PROFILE as u32;
        }
    }
    if knob == Knob::Ok
        && let Some((status, why_code)) = write_policy_override(component)
    {
        // SAFETY: checked above.
        unsafe {
            (*out).status = status;
            (*out).why_code = why_code;
            (*out).flags &= !(KILN_ECS_WRITE_POLICY_WRITABLE as u32);
            if status == KILN_ECS_UNAVAILABLE as u32 {
                (*out).component_size = 0;
            }
        }
        // ecs_write_policy.cpp: NOT_WRITABLE answers KILN_OK, UNAVAILABLE KILN_ERR_UNAVAILABLE
        return if status == KILN_ECS_UNAVAILABLE as u32 {
            KILN_ERR_UNAVAILABLE
        } else {
            KILN_OK
        };
    }
    knob.plain_result()
}
/// `(owner, component)` → the status and why_code `ecs_write_policy` answers for that
/// component on a thread bound to that owner, instead of writable.
type PolicyAnswers = HashMap<(KilnOwner, u32), (u32, u32)>;
static WRITE_POLICIES: LazyLock<Mutex<PolicyAnswers>> = LazyLock::new(Default::default);
pub fn set_write_policy(owner: KilnOwner, component: u32, answer: Option<(u32, u32)>) {
    let mut all = WRITE_POLICIES.lock().unwrap();
    match answer {
        Some(answer) => all.insert((owner, component), answer),
        None => all.remove(&(owner, component)),
    };
}
fn write_policy_override(component: u32) -> Option<(u32, u32)> {
    WRITE_POLICIES
        .lock()
        .unwrap()
        .get(&(THREAD_OWNER.get(), component))
        .copied()
}
/// `reply` (its size set) gets the knob's status; an invalid argument leaves it untouched.
#[allow(clippy::too_many_arguments)]
unsafe extern "C" fn ecs_compare_exchange(
    owner: KilnOwner,
    object: *const KilnObjectRef,
    _component: u32,
    expected: *const c_void,
    _mask: *const c_void,
    desired: *const c_void,
    bytes: u32,
    _confirmed: *mut c_void,
    reply: *mut KilnMutationReply,
) -> KilnResult {
    let knob = enter(owner, "ecs_compare_exchange");
    // SAFETY: `reply` read only when non-NULL.
    if owner == 0
        || bad_ref(object)
        || expected.is_null()
        || desired.is_null()
        || bytes == 0
        || reply.is_null()
        || unsafe { (*reply).size } < core::mem::size_of::<KilnMutationReply>() as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let mut knob = knob;
    if knob == Knob::Ok {
        // SAFETY: checked above; the buffers are `bytes` long (the caller's contract).
        let (object_ref, expected_bytes, desired_bytes, mask_bytes) = unsafe {
            (
                *object,
                std::slice::from_raw_parts(expected.cast::<u8>(), bytes as usize).to_vec(),
                std::slice::from_raw_parts(desired.cast::<u8>(), bytes as usize).to_vec(),
                (!_mask.is_null()).then(|| {
                    std::slice::from_raw_parts(_mask.cast::<u8>(), bytes as usize).to_vec()
                }),
            )
        };
        let policy = write_policy_override(_component);
        let answer = ecs_world(owner, |world| -> Result<(u32, u32, u32, Vec<u8>), Knob> {
            match world.cas.pop_front().unwrap_or(CasStep::Plain) {
                CasStep::Foreign {
                    entity,
                    component,
                    offset,
                    bytes,
                } => {
                    if let Some(stored) = world
                        .entities
                        .get_mut(&entity)
                        .and_then(|e| e.components.get_mut(&component))
                    {
                        stored[offset..offset + bytes.len()].copy_from_slice(&bytes);
                    }
                }
                CasStep::Answer(knob) => return Err(knob),
                CasStep::Plain => {}
            }
            let why = |w: i32| w as u32;
            let entity = match model_entity(world, &object_ref) {
                Ok(entity) => entity,
                Err(KILN_ERR_NOT_FOUND) => {
                    return Ok((
                        KILN_ECS_ABSENT as u32,
                        0,
                        why(KILN_ECS_WRITE_WHY_REF),
                        Vec::new(),
                    ));
                }
                Err(_) => {
                    return Ok((
                        KILN_ECS_STALE as u32,
                        0,
                        why(KILN_ECS_WRITE_WHY_REF),
                        Vec::new(),
                    ));
                }
            };
            if object_ref.reload_epoch != owner {
                return Ok((
                    KILN_ECS_STALE as u32,
                    0,
                    why(KILN_ECS_WRITE_WHY_NOT_OWNERS_REF),
                    Vec::new(),
                ));
            }
            if let Some((status, why_code)) = policy {
                return Ok((status, 0, why_code, Vec::new()));
            }
            let Some(current) = entity.components.get(&_component).cloned() else {
                return Ok((
                    KILN_ECS_ABSENT as u32,
                    0,
                    why(KILN_ECS_WRITE_WHY_REF),
                    Vec::new(),
                ));
            };
            if current.len() != expected_bytes.len() {
                return Ok((
                    KILN_ECS_NOT_WRITABLE as u32,
                    0,
                    why(KILN_ECS_WRITE_WHY_SIZE),
                    Vec::new(),
                ));
            }
            world.masks.push(mask_bytes.clone());
            if current != expected_bytes {
                return Ok((
                    KILN_ECS_CONFLICT as u32,
                    KILN_MUTATION_CONFIRMED as u32,
                    why(KILN_ECS_WRITE_WHY_COMPARE),
                    current,
                ));
            }
            let merged: Vec<u8> = current
                .iter()
                .zip(&desired_bytes)
                .enumerate()
                .map(|(i, (old, new))| match &mask_bytes {
                    Some(mask) if mask[i] == 0 => *old,
                    _ => *new,
                })
                .collect();
            if merged == current {
                return Ok((
                    KILN_ECS_OK as u32,
                    (KILN_MUTATION_NO_OP | KILN_MUTATION_CONFIRMED) as u32,
                    why(KILN_ECS_WRITE_WHY_NONE),
                    current,
                ));
            }
            let id = object_ref.entity;
            world
                .entities
                .get_mut(&id)
                .unwrap()
                .components
                .insert(_component, merged.clone());
            world.writes.push((id, _component, merged.clone()));
            Ok((
                KILN_ECS_OK as u32,
                (KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED | KILN_MUTATION_EVENT_QUEUED)
                    as u32,
                why(KILN_ECS_WRITE_WHY_NONE),
                merged,
            ))
        });
        match answer {
            Some(Ok((status, flags, why_code, seen))) => {
                // SAFETY: the caller's records, checked above.
                unsafe {
                    let size = (*reply).size;
                    *reply = core::mem::zeroed();
                    (*reply).size = size;
                    (*reply).status = status;
                    (*reply).flags = flags;
                    (*reply).why_code = why_code;
                    (*reply).generation = object_ref.generation;
                    (*reply).session = object_ref.session;
                    if !_confirmed.is_null() && seen.len() == bytes as usize {
                        std::ptr::copy_nonoverlapping(
                            seen.as_ptr(),
                            _confirmed.cast::<u8>(),
                            seen.len(),
                        );
                    }
                }
                return match status as i32 {
                    KILN_ECS_OK => KILN_OK,
                    KILN_ECS_ABSENT => KILN_ERR_NOT_FOUND,
                    KILN_ECS_STALE => KILN_ECS_ERR_STALE,
                    KILN_ECS_CONFLICT => KILN_ECS_ERR_CONFLICT,
                    KILN_ECS_NOT_WRITABLE => KILN_ECS_ERR_NOT_WRITABLE,
                    _ => KILN_ERR_UNAVAILABLE,
                };
            }
            Some(Err(scripted)) => knob = scripted,
            None => {}
        }
    }
    // SAFETY: the caller's reply record, checked above.
    unsafe {
        let size = (*reply).size;
        *reply = core::mem::zeroed();
        (*reply).size = size;
        (*reply).generation = (*object).generation;
        (*reply).session = (*object).session;
        (*reply).status = knob.ecs_status();
        (*reply).why_code = match knob {
            Knob::Ok => KILN_ECS_WRITE_WHY_NONE,
            Knob::Busy => KILN_ECS_WRITE_WHY_GATE,
            Knob::Stale => KILN_ECS_WRITE_WHY_REF,
            Knob::Unreadable => KILN_ECS_WRITE_WHY_FAULT,
            Knob::Unavailable => KILN_ECS_WRITE_WHY_NO_PROFILE,
            Knob::Uncertain => KILN_ECS_WRITE_WHY_READBACK,
            Knob::Conflict => KILN_ECS_WRITE_WHY_COMPARE,
        } as u32;
    }
    knob.ecs_result()
}
unsafe extern "C" fn ecs_write_subscribe(
    owner: KilnOwner,
    function: KilnEcsWriteEventFn,
    user: *mut c_void,
    handle: *mut KilnHandle,
) -> KilnResult {
    let knob = enter(owner, "ecs_write_subscribe");
    let Some(function) = function else {
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    let index = with(owner, |s| {
        s.subscriptions.push((function as usize, user as usize));
        s.subscriptions.len()
    });
    if !handle.is_null() {
        // SAFETY: live out pointer.
        unsafe { *handle = (owner << 16) | index as u64 };
    }
    KILN_OK
}
unsafe extern "C" fn ecs_write_unsubscribe(handle: KilnHandle) -> KilnResult {
    let (owner, index) = (handle >> 16, (handle & 0xffff) as usize);
    enter(owner, "ecs_write_unsubscribe");
    with(owner, |s| {
        match s.subscriptions.get_mut(index.wrapping_sub(1)) {
            Some(slot) if slot.0 != 0 => {
                *slot = (0, 0);
                KILN_OK
            }
            _ => KILN_ERR_NOT_FOUND,
        }
    })
}
/// Fires `owner`'s ECS write subscriptions on this thread with an event Kiln lends for
/// the callback only (the caller may reuse it right after). Returns how many ran.
pub fn fire_ecs_write(owner: KilnOwner, event: *const KilnEcsWriteEvent) -> usize {
    let subscriptions = with(owner, |s| s.subscriptions.clone());
    let mut ran = 0;
    for (function, user) in subscriptions {
        if function == 0 {
            continue;
        }
        // SAFETY: a registered KilnEcsWriteEventFn.
        let function: unsafe extern "C" fn(*const KilnEcsWriteEvent, *mut c_void) =
            unsafe { std::mem::transmute(function) };
        unsafe { function(event, user as *mut c_void) };
        ran += 1;
    }
    ran
}

// ---- an ECS world for the ECS members (opt-in per owner) ----

/// One entity of an [`EcsWorld`]: its generation, its components' bytes (by component hash) and its template uuid.
#[derive(Clone, Debug, Default)]
pub struct EcsEntity {
    pub generation: u32,
    pub components: BTreeMap<u32, Vec<u8>>,
    pub template: Option<[u8; 16]>,
}

/// What one `ecs_compare_exchange` call meets first (one step per call, front first; none left: [`CasStep::Plain`]).
#[derive(Clone, Debug)]
pub enum CasStep {
    /// Another writer (a game frame) stores `bytes` at `offset` of the component, then the call runs as usual.
    Foreign {
        entity: KilnEntity,
        component: u32,
        offset: usize,
        bytes: Vec<u8>,
    },
    /// The call answers this knob's outcome as `Knob` does (reply status and why_code), writing nothing.
    Answer(Knob),
    /// The compare and the write as kiln_world.h describes them.
    Plain,
}

/// An owner's ECS world: with one, the ECS members answer from it (after the owner's knob, which still comes first):
/// `entity_open`, `entity_validate`, `ecs_query` (no bounds), `ecs_snapshot`, `ecs_template_uuid` and
/// `ecs_compare_exchange` as kiln_world.h describes them, `ecs_context_snapshot` (on a thread bound to the owner)
/// with this world present. Refs carry the world's session and generation; a query's refs the owner as reload epoch.
#[derive(Clone, Debug)]
pub struct EcsWorld {
    pub world_kind: u32,
    pub session: u64,
    pub exact_profile: bool,
    pub entities: BTreeMap<KilnEntity, EcsEntity>,
    pub cas: std::collections::VecDeque<CasStep>,
    /// Every committed write: (entity, component, the bytes written).
    pub writes: Vec<(KilnEntity, u32, Vec<u8>)>,
    /// The mask of every compare-exchange that got as far as its compare (None for a NULL mask).
    pub masks: Vec<Option<Vec<u8>>>,
    /// The next this many queries answer KILN_WORLD_ERR_CHANGED.
    pub query_changed: u32,
    /// Every `ecs_snapshot` answers KILN_ECS_ERR_UNREADABLE (the copy changed under the read).
    pub unreadable: bool,
}
impl EcsWorld {
    /// A server world in session [`FAKE_SESSION`] with an exact profile and no entities.
    pub fn server() -> Self {
        Self {
            world_kind: KILN_WORLD_SERVER as u32,
            session: FAKE_SESSION,
            exact_profile: true,
            entities: BTreeMap::new(),
            cas: Default::default(),
            writes: Vec::new(),
            masks: Vec::new(),
            query_changed: 0,
            unreadable: false,
        }
    }
    /// The world with entity `id` (generation 1) holding `components`.
    pub fn with(mut self, id: KilnEntity, components: &[(u32, Vec<u8>)]) -> Self {
        self.entities.insert(
            id,
            EcsEntity {
                generation: 1,
                components: components.iter().cloned().collect(),
                template: None,
            },
        );
        self
    }
}
static ECS_WORLDS: LazyLock<Mutex<HashMap<KilnOwner, EcsWorld>>> = LazyLock::new(Default::default);
/// Sets (or, with None, removes) `owner`'s ECS world.
pub fn set_ecs_world(owner: KilnOwner, world: Option<EcsWorld>) {
    let mut all = ECS_WORLDS.lock().unwrap();
    match world {
        Some(world) => all.insert(owner, world),
        None => all.remove(&owner),
    };
}
/// Changes (or reads) `owner`'s ECS world; None without one.
pub fn ecs_world<R>(owner: KilnOwner, f: impl FnOnce(&mut EcsWorld) -> R) -> Option<R> {
    ECS_WORLDS.lock().unwrap().get_mut(&owner).map(f)
}
/// The `KILN_ECS_*` status a record carries for a member's result.
fn status_of(result: KilnResult) -> u32 {
    (match result {
        KILN_OK => KILN_ECS_OK,
        KILN_ERR_NOT_FOUND => KILN_ECS_ABSENT,
        KILN_ECS_ERR_STALE => KILN_ECS_STALE,
        KILN_ECS_ERR_UNREADABLE | KILN_WORLD_ERR_CHANGED => KILN_ECS_UNREADABLE,
        KILN_ECS_ERR_BUSY => KILN_ECS_BUSY,
        KILN_ECS_ERR_CONFLICT => KILN_ECS_CONFLICT,
        KILN_ECS_ERR_NOT_WRITABLE => KILN_ECS_NOT_WRITABLE,
        KILN_ECS_ERR_UNCERTAIN => KILN_ECS_UNCERTAIN,
        _ => KILN_ECS_UNAVAILABLE,
    }) as u32
}
/// The entity a ref names in `world`, or the member's refusal.
fn model_entity<'w>(
    world: &'w EcsWorld,
    object: &KilnObjectRef,
) -> Result<&'w EcsEntity, KilnResult> {
    if object.world_kind != world.world_kind {
        return Err(KILN_ERR_UNAVAILABLE);
    }
    if object.session != world.session {
        return Err(KILN_ECS_ERR_STALE);
    }
    let entity = world
        .entities
        .get(&object.entity)
        .ok_or(KILN_ERR_NOT_FOUND)?;
    if entity.generation != object.generation {
        return Err(KILN_ECS_ERR_STALE);
    }
    Ok(entity)
}
fn model_ref(world: &EcsWorld, id: KilnEntity, generation: u32, epoch: u64) -> KilnObjectRef {
    KilnObjectRef {
        size: core::mem::size_of::<KilnObjectRef>() as u32,
        entity: id,
        generation,
        world_kind: world.world_kind,
        session: world.session,
        layout_stamp: 1,
        reload_epoch: epoch,
    }
}
unsafe extern "C" fn ecs_template_uuid(
    owner: KilnOwner,
    object: *const KilnObjectRef,
    guid: *mut u8,
) -> KilnResult {
    let knob = enter(owner, "ecs_template_uuid").read();
    if owner == 0 || bad_ref(object) || guid.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.ecs_result();
    }
    // SAFETY: checked above.
    let object = unsafe { *object };
    let answer = ecs_world(owner, |world| {
        model_entity(world, &object).and_then(|entity| entity.template.ok_or(KILN_ERR_NOT_FOUND))
    });
    match answer {
        Some(Ok(uuid)) => {
            // SAFETY: the caller's 16 bytes.
            unsafe { std::ptr::copy_nonoverlapping(uuid.as_ptr(), guid, 16) };
            KILN_OK
        }
        Some(Err(code)) => code,
        None => KILN_ERR_NOT_FOUND,
    }
}

// ---- kiln.world's named services (since 0.2.0-dev.30.3) ----

/// What one `named_service_snapshot` or `named_service_compare_exchange` meets first (one step per call, front first;
/// none left: [`NamedStep::Plain`]).
#[derive(Clone, Debug)]
pub enum NamedStep {
    /// Another writer (a game system) stores `bytes` at `offset` of the service, then the call runs as usual.
    Foreign {
        key: String,
        offset: usize,
        bytes: Vec<u8>,
    },
    /// The call answers this `KilnResult` (a compare-exchange: with the matching reply status), copying and writing
    /// nothing.
    Answer(KilnResult),
    /// The snapshot or the compare-exchange as kiln_world.h describes them.
    Plain,
}

/// An owner's named services: with them, `named_service_open`, `_snapshot`, `_close` and `_compare_exchange` answer
/// as kiln_world.h describes them (after the owner's knob, which still comes first). Views are bound to the session
/// they were opened in; `session` 0 is "no server world".
#[derive(Clone, Debug)]
pub struct NamedServices {
    pub session: u64,
    /// The services by key: their bytes (the size is their length).
    pub services: BTreeMap<String, Vec<u8>>,
    /// Open views: handle -> (key, session).
    pub views: BTreeMap<KilnHandle, (String, u64)>,
    pub next: KilnHandle,
    pub steps: std::collections::VecDeque<NamedStep>,
    /// Every committed write: (key, the bytes written).
    pub writes: Vec<(String, Vec<u8>)>,
    /// The mask of every compare-exchange that got as far as its compare (None for a NULL mask).
    pub masks: Vec<Option<Vec<u8>>>,
    pub closed: Vec<KilnHandle>,
}
impl NamedServices {
    /// Session [`FAKE_SESSION`], no service.
    pub fn new() -> Self {
        Self {
            session: FAKE_SESSION,
            services: BTreeMap::new(),
            views: BTreeMap::new(),
            next: 0x5e00,
            steps: Default::default(),
            writes: Vec::new(),
            masks: Vec::new(),
            closed: Vec::new(),
        }
    }
    /// With service `key` holding `bytes`.
    pub fn with(mut self, key: &str, bytes: Vec<u8>) -> Self {
        self.services.insert(key.to_owned(), bytes);
        self
    }
}
impl Default for NamedServices {
    fn default() -> Self {
        Self::new()
    }
}
static NAMED: LazyLock<Mutex<HashMap<KilnOwner, NamedServices>>> = LazyLock::new(Default::default);
/// Sets (or, with None, removes) `owner`'s named services.
pub fn set_named_services(owner: KilnOwner, named: Option<NamedServices>) {
    let mut all = NAMED.lock().unwrap();
    match named {
        Some(named) => all.insert(owner, named),
        None => all.remove(&owner),
    };
}
/// Changes (or reads) `owner`'s named services; None without them.
pub fn named_services<R>(owner: KilnOwner, f: impl FnOnce(&mut NamedServices) -> R) -> Option<R> {
    NAMED.lock().unwrap().get_mut(&owner).map(f)
}
/// The `KILN_ECS_*` reply status of a compare-exchange's result.
fn named_status(result: KilnResult) -> u32 {
    status_of(result)
}
/// The view `handle` of `owner` in `named`: its key, or the member's refusal.
fn named_view(named: &NamedServices, handle: KilnHandle) -> Result<String, KilnResult> {
    let (key, session) = named.views.get(&handle).ok_or(KILN_ERR_NOT_FOUND)?;
    if named.session == 0 {
        return Err(KILN_ERR_UNAVAILABLE);
    }
    if *session != named.session {
        return Err(KILN_ECS_ERR_STALE);
    }
    Ok(key.clone())
}
fn named_foreign(named: &mut NamedServices) -> Option<KilnResult> {
    match named.steps.pop_front().unwrap_or(NamedStep::Plain) {
        NamedStep::Foreign { key, offset, bytes } => {
            if let Some(stored) = named.services.get_mut(&key) {
                stored[offset..offset + bytes.len()].copy_from_slice(&bytes);
            }
            None
        }
        NamedStep::Answer(code) => Some(code),
        NamedStep::Plain => None,
    }
}
unsafe extern "C" fn named_service_open(
    owner: KilnOwner,
    name: KilnStr,
    expected_session: u64,
    view: *mut KilnHandle,
    ty: *mut *const KilnType,
) -> KilnResult {
    let knob = enter(owner, "named_service_open").read();
    if owner == 0 || view.is_null() || name.ptr.is_null() || name.len == 0 || name.len > 127 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's counted name.
    let key = unsafe { std::slice::from_raw_parts(name.ptr.cast::<u8>(), name.len) };
    let Ok(key) = std::str::from_utf8(key) else {
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if key.contains('\0') || key.starts_with("keen::") {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above; the type is optional.
    unsafe {
        *view = 0;
        if !ty.is_null() {
            *ty = std::ptr::null();
        }
    }
    if knob != Knob::Ok {
        return knob.ecs_result();
    }
    let answer = named_services(owner, |named| -> Result<KilnHandle, KilnResult> {
        if !named.services.contains_key(key) {
            return Err(KILN_ERR_NOT_FOUND);
        }
        if named.session == 0 {
            return Err(KILN_ERR_UNAVAILABLE);
        }
        if expected_session != 0 && expected_session != named.session {
            return Err(KILN_ECS_ERR_STALE);
        }
        named.next += 1;
        let handle = named.next;
        named.views.insert(handle, (key.to_owned(), named.session));
        Ok(handle)
    });
    match answer {
        Some(Ok(handle)) => {
            // SAFETY: checked above.
            unsafe { *view = handle };
            KILN_OK
        }
        Some(Err(code)) => code,
        None => KILN_ERR_UNAVAILABLE,
    }
}
unsafe extern "C" fn named_service_snapshot(
    owner: KilnOwner,
    view: KilnHandle,
    out: *mut c_void,
    capacity: u32,
    required: *mut u32,
) -> KilnResult {
    let knob = enter(owner, "named_service_snapshot").read();
    if owner == 0 || (capacity != 0 && out.is_null()) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.ecs_result();
    }
    let answer = named_services(owner, |named| -> Result<Vec<u8>, (KilnResult, u32)> {
        let key = named_view(named, view).map_err(|code| (code, 0))?;
        let size = named.services[&key].len() as u32;
        if capacity < size {
            return Err((KILN_ERR_BUFFER_TOO_SMALL, size));
        }
        if let Some(code) = named_foreign(named) {
            return Err((code, size));
        }
        Ok(named.services[&key].clone())
    });
    let (result, size, bytes) = match answer {
        Some(Ok(bytes)) => (KILN_OK, bytes.len() as u32, Some(bytes)),
        Some(Err((code, size))) => (code, size, None),
        None => (KILN_ERR_NOT_FOUND, 0, None),
    };
    if !required.is_null() {
        // SAFETY: the caller's optional out count.
        unsafe { *required = size };
    }
    if let Some(bytes) = bytes {
        // SAFETY: `capacity` >= the size, the caller's buffer.
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out.cast::<u8>(), bytes.len()) };
    }
    result
}
unsafe extern "C" fn named_service_close(owner: KilnOwner, view: KilnHandle) -> KilnResult {
    enter(owner, "named_service_close");
    if owner == 0 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    named_services(owner, |named| {
        if named.views.remove(&view).is_some() {
            named.closed.push(view);
            KILN_OK
        } else {
            KILN_ERR_NOT_FOUND
        }
    })
    .unwrap_or(KILN_ERR_NOT_FOUND)
}
unsafe extern "C" fn named_service_compare_exchange(
    owner: KilnOwner,
    view: KilnHandle,
    expected: *const c_void,
    mask: *const c_void,
    desired: *const c_void,
    bytes: u32,
    confirmed: *mut c_void,
    reply: *mut KilnMutationReply,
) -> KilnResult {
    let knob = enter(owner, "named_service_compare_exchange");
    // SAFETY: `reply` read only when non-NULL.
    if owner == 0
        || expected.is_null()
        || desired.is_null()
        || bytes == 0
        || reply.is_null()
        || unsafe { (*reply).size } < core::mem::size_of::<KilnMutationReply>() as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let n = bytes as usize;
    // SAFETY: checked above; the buffers are `bytes` long (the caller's contract).
    let (expected, desired, mask) = unsafe {
        (
            std::slice::from_raw_parts(expected.cast::<u8>(), n).to_vec(),
            std::slice::from_raw_parts(desired.cast::<u8>(), n).to_vec(),
            (!mask.is_null()).then(|| std::slice::from_raw_parts(mask.cast::<u8>(), n).to_vec()),
        )
    };
    let (result, status, flags, why, session, seen) = if knob != Knob::Ok {
        (
            knob.ecs_result(),
            knob.ecs_status(),
            0u32,
            0u32,
            FAKE_SESSION,
            None,
        )
    } else {
        named_services(owner, |named| {
            let session = named.session;
            let key = match named_view(named, view) {
                Ok(key) => key,
                Err(code) => {
                    return (
                        code,
                        named_status(code),
                        0,
                        KILN_ECS_WRITE_WHY_REF as u32,
                        session,
                        None,
                    );
                }
            };
            if let Some(code) = named_foreign(named) {
                return (code, named_status(code), 0, 0, session, None);
            }
            let current = named.services[&key].clone();
            if current.len() != n {
                let code = KILN_ECS_ERR_NOT_WRITABLE;
                return (
                    code,
                    named_status(code),
                    0,
                    KILN_ECS_WRITE_WHY_SIZE as u32,
                    session,
                    None,
                );
            }
            named.masks.push(mask.clone());
            if current != expected {
                let code = KILN_ECS_ERR_CONFLICT;
                return (
                    code,
                    named_status(code),
                    KILN_MUTATION_CONFIRMED as u32,
                    KILN_ECS_WRITE_WHY_COMPARE as u32,
                    session,
                    Some(current),
                );
            }
            let merged: Vec<u8> = current
                .iter()
                .zip(&desired)
                .enumerate()
                .map(|(i, (old, new))| match &mask {
                    Some(mask) if mask[i] == 0 => *old,
                    _ => *new,
                })
                .collect();
            if merged == current {
                let flags = (KILN_MUTATION_NO_OP | KILN_MUTATION_CONFIRMED) as u32;
                return (
                    KILN_OK,
                    KILN_ECS_OK as u32,
                    flags,
                    0,
                    session,
                    Some(current),
                );
            }
            named.services.insert(key.clone(), merged.clone());
            named.writes.push((key, merged.clone()));
            let flags = (KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED) as u32;
            (KILN_OK, KILN_ECS_OK as u32, flags, 0, session, Some(merged))
        })
        .unwrap_or((KILN_ERR_NOT_FOUND, KILN_ECS_ABSENT as u32, 0, 0, 0, None))
    };
    // SAFETY: checked above; `confirmed` is optional and `bytes` long.
    unsafe {
        let size = (*reply).size;
        std::ptr::write_bytes(reply, 0, 1);
        (*reply).size = size;
        (*reply).status = status;
        (*reply).why_code = why;
        (*reply).session = session;
        match (&seen, confirmed.is_null()) {
            (Some(bytes), false) if flags & KILN_MUTATION_CONFIRMED as u32 != 0 => {
                (*reply).flags = flags;
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), confirmed.cast::<u8>(), n);
            }
            _ => (*reply).flags = flags & !(KILN_MUTATION_CONFIRMED as u32),
        }
    }
    result
}

// ---- kiln.world's ecs_registration (since 0.2.0-dev.30.3) ----

/// The registration rows one owner's kiln.world answers, and its scripted refusals.
#[derive(Clone, Default)]
pub struct RegistrationFake {
    /// The catalog's rows, found by their `component` hash.
    pub rows: Vec<KilnEcsRegistrationDetail>,
    /// `Some(result)`: every call answers that result (status UNAVAILABLE), writing no row.
    pub refuse: Option<KilnResult>,
    /// The next this many calls answer KILN_ECS_ERR_UNREADABLE (a catalog that moved during the read).
    pub unreadable: u32,
}
static REGISTRATIONS: LazyLock<Mutex<HashMap<KilnOwner, RegistrationFake>>> =
    LazyLock::new(Default::default);
pub fn set_registrations(owner: KilnOwner, fake: Option<RegistrationFake>) {
    let mut map = REGISTRATIONS.lock().unwrap();
    match fake {
        Some(fake) => map.insert(owner, fake),
        None => map.remove(&owner),
    };
}
pub fn registrations<R>(owner: KilnOwner, f: impl FnOnce(&mut RegistrationFake) -> R) -> Option<R> {
    REGISTRATIONS.lock().unwrap().get_mut(&owner).map(f)
}
/// One row of a template-capable registration: `name`'s own template type of `size` bytes, and a storage type
/// (`storage`: its hash and size, (0, 0) for a template-only registration).
pub fn template_row(
    index: u32,
    name: &str,
    size: u32,
    storage: (u32, u32),
) -> KilnEcsRegistrationDetail {
    // SAFETY: all-zero records are valid (integers only).
    let mut row: KilnEcsRegistrationDetail = unsafe { std::mem::zeroed() };
    row.size = std::mem::size_of::<KilnEcsRegistrationDetail>() as u32;
    row.index = index;
    row.component = fnv1a(name);
    row.runtime_size = storage.1;
    row.storage_hash = storage.0;
    row.storage_size = storage.1;
    row.template_hash = fnv1a(name);
    row.template_size = size;
    row.layout_stamp = 0x303e_c500_0001;
    row
}
fn fnv1a(text: &str) -> u32 {
    text.bytes().fold(0x811c_9dc5_u32, |hash, byte| {
        (hash ^ u32::from(byte)).wrapping_mul(0x0100_0193)
    })
}
unsafe extern "C" fn ecs_registration(
    world_kind: u32,
    component: u32,
    out: *mut KilnEcsRegistrationDetail,
) -> KilnResult {
    let owner = THREAD_OWNER.get();
    let knob = enter(owner, "ecs_registration");
    // SAFETY: read only when non-NULL.
    if out.is_null()
        || unsafe { (*out).size } < core::mem::size_of::<KilnEcsRegistrationDetail>() as u32
        || world_kind >= KILN_WORLD_KINDS as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let out = unsafe { &mut *out };
    let size = out.size;
    // SAFETY: all-zero records are valid (integers only).
    *out = unsafe { core::mem::zeroed() };
    out.size = size;
    out.component = component;
    if knob != Knob::Ok {
        out.status = KILN_ECS_UNAVAILABLE as u32;
        return KILN_ERR_UNAVAILABLE;
    }
    let answer = registrations(owner, |fake| {
        if let Some(result) = fake.refuse {
            return Err(result);
        }
        if fake.unreadable > 0 {
            fake.unreadable -= 1;
            return Err(KILN_ECS_ERR_UNREADABLE);
        }
        Ok(fake
            .rows
            .iter()
            .find(|row| row.component == component)
            .copied())
    })
    .unwrap_or(Err(KILN_ERR_UNAVAILABLE));
    match answer {
        Ok(Some(row)) => {
            *out = row;
            out.size = size;
            KILN_OK
        }
        Ok(None) => KILN_ERR_NOT_FOUND,
        Err(result) => {
            out.status = if result == KILN_ECS_ERR_UNREADABLE {
                KILN_ECS_UNREADABLE
            } else {
                KILN_ECS_UNAVAILABLE
            } as u32;
            result
        }
    }
}

pub static WORLD: KilnWorldApi = {
    // SAFETY: every member is a nullable function pointer or plain data.
    let mut table: KilnWorldApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnWorldApi>() as u32,
        version: KILN_WORLD_VERSION,
    };
    table.status = Some(world_status);
    table.capabilities = Some(world_capabilities);
    table.entity_open = Some(entity_open);
    table.entity_validate = Some(entity_validate);
    table.ecs_context_snapshot = Some(ecs_context_snapshot);
    table.ecs_query = Some(ecs_query);
    table.ecs_snapshot = Some(ecs_snapshot);
    table.ecs_snapshot_many = Some(ecs_snapshot_many);
    table.ecs_template_uuid = Some(ecs_template_uuid);
    table.ecs_write_policy = Some(ecs_write_policy);
    table.ecs_compare_exchange = Some(ecs_compare_exchange);
    table.ecs_write_subscribe = Some(ecs_write_subscribe);
    table.ecs_write_unsubscribe = Some(ecs_write_unsubscribe);
    table.game_world_session = Some(game_world_session);
    table.named_service_open = Some(named_service_open);
    table.named_service_snapshot = Some(named_service_snapshot);
    table.named_service_close = Some(named_service_close);
    table.named_service_compare_exchange = Some(named_service_compare_exchange);
    table.ecs_registration = Some(ecs_registration);
    table
};

/// `(owner, world kind)` → the session `game_world_session` answers on a thread bound to
/// that owner. Without one: `FAKE_SESSION` for the server world, 0 for the others.
static WORLD_SESSIONS: LazyLock<Mutex<HashMap<(KilnOwner, u32), u64>>> =
    LazyLock::new(Default::default);
pub fn set_world_session(owner: KilnOwner, world_kind: u32, session: u64) {
    WORLD_SESSIONS
        .lock()
        .unwrap()
        .insert((owner, world_kind), session);
}
/// `(owner, world kind)` → values `game_world_session` answers first, one per call (last first),
/// before the fixed one (a session that moves while a caller reads it).
type SessionScripts = HashMap<(KilnOwner, u32), Vec<u64>>;
static WORLD_SESSION_SCRIPT: LazyLock<Mutex<SessionScripts>> = LazyLock::new(Default::default);
pub fn script_world_session(owner: KilnOwner, world_kind: u32, values: &[u64]) {
    WORLD_SESSION_SCRIPT
        .lock()
        .unwrap()
        .insert((owner, world_kind), values.iter().rev().copied().collect());
}
/// kiln_world.h: any thread, lock-free; 0 when that world has no session.
unsafe extern "C" fn game_world_session(world_kind: u32) -> u64 {
    let owner = THREAD_OWNER.get();
    enter(owner, "game_world_session");
    if let Some(value) = WORLD_SESSION_SCRIPT
        .lock()
        .unwrap()
        .get_mut(&(owner, world_kind))
        .and_then(Vec::pop)
    {
        return value;
    }
    WORLD_SESSIONS
        .lock()
        .unwrap()
        .get(&(owner, world_kind))
        .copied()
        .unwrap_or(if world_kind == KILN_WORLD_SERVER as u32 {
            FAKE_SESSION
        } else {
            0
        })
}

// ---- the other services: their status reads and one read each ----

/// The reflected types the fake kiln.types reports, in index order: (qualified name,
/// size). Its status says the type table was found (`types = 1`) with these.
pub const FAKE_TYPES: [(&str, u32); 3] =
    [("fake::UInt32", 4), ("fake::Base", 4), ("fake::Child", 8)];
/// One byte per fake type: its address is the type's opaque `KilnType*`.
static FAKE_TYPE_HANDLES: [u8; 3] = [0; 3];

unsafe extern "C" fn types_status(out: *mut KilnTypesStatus) -> KilnResult {
    let knob = enter_thread("types.status");
    if out.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's record; fields written only when its size covers them.
    unsafe {
        if knob == Knob::Ok && (*out).size as usize >= std::mem::size_of::<KilnTypesStatus>() {
            (*out).types = 1;
            (*out).type_count = FAKE_TYPES.len() as u32;
        }
    }
    knob.plain_result()
}
unsafe extern "C" fn type_count() -> u32 {
    if enter_thread("types.type_count") == Knob::Ok {
        FAKE_TYPES.len() as u32
    } else {
        0
    }
}
/// kiln_types.h: `type_at(index)`, NULL past the count. An owner with an ECS catalog
/// ([`set_ecs_catalog`]) also has its types at [`ECS_TYPE_BASE`] + position.
unsafe extern "C" fn type_at(index: u32) -> *const KilnType {
    if enter_thread("types.type_at") != Knob::Ok {
        return std::ptr::null();
    }
    if let Some(handle) = ecs_type_handle(index) {
        return handle;
    }
    FAKE_TYPE_HANDLES
        .get(index as usize)
        .map_or(std::ptr::null(), |handle| (handle as *const u8).cast())
}
/// kiln_types.h: `type_info(type, out)` fills a sized `KilnTypeInfo`.
unsafe extern "C" fn type_info(ty: *const KilnType, out: *mut KilnTypeInfo) -> KilnResult {
    let knob = enter_thread("types.type_info");
    if let Some((identity, qualified, size, hash)) = ecs_type_of(ty) {
        // SAFETY: the record is read only when non-NULL.
        if out.is_null() || unsafe { (*out).size as usize } < std::mem::size_of::<KilnTypeInfo>() {
            return KILN_ERR_INVALID_ARGUMENT;
        }
        if knob != Knob::Ok {
            return knob.plain_result();
        }
        let name = qualified.rsplit("::").next().unwrap_or(qualified);
        // SAFETY: a sized caller record; the leaked names live for the process.
        unsafe {
            (*out).index = identity;
            (*out).name = KilnStr {
                ptr: name.as_ptr().cast(),
                len: name.len(),
            };
            (*out).qualified_name = KilnStr {
                ptr: qualified.as_ptr().cast(),
                len: qualified.len(),
            };
            (*out).type_size = size;
            (*out).hash = hash;
        }
        return KILN_OK;
    }
    let index = (ty as usize).wrapping_sub(FAKE_TYPE_HANDLES.as_ptr() as usize);
    // SAFETY: the record is read only when non-NULL.
    if out.is_null()
        || unsafe { (*out).size as usize } < std::mem::size_of::<KilnTypeInfo>()
        || index >= FAKE_TYPES.len()
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    let (qualified, size) = FAKE_TYPES[index];
    let name = qualified.rsplit("::").next().unwrap_or(qualified);
    let text = |s: &'static str| KilnStr {
        ptr: s.as_ptr().cast(),
        len: s.len(),
    };
    // SAFETY: a sized caller record.
    unsafe {
        (*out).index = index as u32;
        (*out).name = text(name);
        (*out).qualified_name = text(qualified);
        (*out).type_size = size;
        (*out).hash = qualified.bytes().fold(0x811c_9dc5_u32, |hash, byte| {
            (hash ^ u32::from(byte)).wrapping_mul(0x0100_0193)
        });
    }
    KILN_OK
}
unsafe extern "C" fn type_by_name(_: KilnStr) -> *const KilnType {
    enter_thread("types.type_by_name");
    std::ptr::null()
}

// ---- kiln.types' ecs_registry (kiln_types.h, appended to v1) ----

/// One owner's engine component catalog, as kiln.types' `ecs_registry` copies it (the real member's
/// builder is native/services/types/ecs_registry.h): the reflected types its rows name and the rows.
#[derive(Clone, Debug, Default)]
pub struct EcsCatalog {
    /// (qualified name, reflected size, component hash): `type_at(ECS_TYPE_BASE + i)` and
    /// `type_info` answer type i with `index` (its identity) = `ECS_TYPE_BASE + i`.
    pub types: Vec<(String, u32, u32)>,
    /// (component hash, type_size, flags, type_identity), in registration order; `index` is the
    /// position.
    pub rows: Vec<(u32, u32, u32, u64)>,
    /// `KilnEcsRegistryInfo.layout_stamp`.
    pub stamp: u64,
    /// The next this-many full reads answer UNREADABLE (the catalog was replaced during the read).
    pub unreadable: u32,
    /// The build has no component table: UNAVAILABLE with `status` 4.
    pub unavailable: bool,
}
/// The first type identity of an ECS catalog's types (away from [`FAKE_TYPES`]' indices).
pub const ECS_TYPE_BASE: u32 = 1000;
struct EcsState {
    catalog: EcsCatalog,
    /// One byte per type: its address is the type's opaque `KilnType*`.
    handles: &'static [u8],
    /// The types' qualified names, leaked once (kiln.types names live for the process).
    names: Vec<&'static str>,
}
static ECS: LazyLock<Mutex<HashMap<KilnOwner, EcsState>>> = LazyLock::new(Default::default);
/// Sets (or, with None, removes) `owner`'s catalog; without one `ecs_registry` answers UNAVAILABLE.
pub fn set_ecs_catalog(owner: KilnOwner, catalog: Option<EcsCatalog>) {
    let mut all = ECS.lock().unwrap();
    match catalog {
        Some(catalog) => {
            let handles: &'static [u8] =
                Box::leak(vec![0u8; catalog.types.len().max(1)].into_boxed_slice());
            let names = catalog
                .types
                .iter()
                .map(|(name, _, _)| &*Box::leak(name.clone().into_boxed_str()))
                .collect();
            all.insert(
                owner,
                EcsState {
                    catalog,
                    handles,
                    names,
                },
            );
        }
        None => {
            all.remove(&owner);
        }
    }
}
/// Changes `owner`'s catalog in place (a replaced catalog, the next unreadable reads).
pub fn edit_ecs_catalog(owner: KilnOwner, f: impl FnOnce(&mut EcsCatalog)) {
    if let Some(state) = ECS.lock().unwrap().get_mut(&owner) {
        f(&mut state.catalog);
    }
}
fn ecs_type_handle(index: u32) -> Option<*const KilnType> {
    let all = ECS.lock().unwrap();
    let state = all.get(&THREAD_OWNER.get())?;
    let at = index.checked_sub(ECS_TYPE_BASE)? as usize;
    (at < state.catalog.types.len()).then(|| (&state.handles[at] as *const u8).cast())
}
fn ecs_type_of(ty: *const KilnType) -> Option<(u32, &'static str, u32, u32)> {
    let all = ECS.lock().unwrap();
    let state = all.get(&THREAD_OWNER.get())?;
    let at = (ty as usize).checked_sub(state.handles.as_ptr() as usize)?;
    let &(_, size, hash) = state.catalog.types.get(at)?;
    Some((ECS_TYPE_BASE + at as u32, state.names[at], size, hash))
}
/// kiln_types.h `ecs_registry`: the arguments first (INVALID_ARGUMENT, `*required` 0), UNAVAILABLE
/// with `status` 4 without a component table, then the builder: `info` filled, `*required` the
/// count, BUFFER_TOO_SMALL (nothing copied) when `capacity` is short, UNREADABLE (`status` 3, count
/// and `*required` 0, nothing copied) when the catalog changed during the read, else every row.
unsafe extern "C" fn ecs_registry(
    world_kind: u32,
    info: *mut KilnEcsRegistryInfo,
    rows: *mut KilnEcsRegistryRow,
    capacity: u32,
    required: *mut u32,
) -> KilnResult {
    let knob = enter_thread("types.ecs_registry");
    let set_required = |value: u32| {
        if !required.is_null() {
            // SAFETY: a live out pointer.
            unsafe { *required = value };
        }
    };
    // SAFETY: the record is read only when non-NULL.
    if world_kind >= KILN_WORLD_KINDS as u32
        || info.is_null()
        || unsafe { (*info).size as usize } < std::mem::size_of::<KilnEcsRegistryInfo>()
        || (capacity != 0 && rows.is_null())
    {
        set_required(0);
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let size = unsafe { (*info).size };
    let fill = |status: i32, count: u32, stamp: u64| {
        // SAFETY: checked above.
        unsafe {
            *info = std::mem::zeroed();
            (*info).size = size;
            (*info).status = status as u32;
            (*info).world_kind = world_kind;
            (*info).count = count;
            (*info).layout_stamp = stamp;
        }
    };
    let mut all = ECS.lock().unwrap();
    let state = all.get_mut(&THREAD_OWNER.get());
    let Some(catalog) = state
        .map(|s| &mut s.catalog)
        .filter(|c| knob == Knob::Ok && !c.unavailable)
    else {
        fill(KILN_ECS_UNAVAILABLE, 0, 0);
        set_required(0);
        return KILN_ERR_UNAVAILABLE;
    };
    let count = catalog.rows.len() as u32;
    fill(KILN_ECS_OK, count, catalog.stamp);
    set_required(count);
    if count > capacity {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    if catalog.unreadable > 0 {
        catalog.unreadable -= 1;
        fill(KILN_ECS_UNREADABLE, 0, catalog.stamp);
        set_required(0);
        return KILN_ECS_ERR_UNREADABLE;
    }
    for (index, &(component, type_size, flags, type_identity)) in catalog.rows.iter().enumerate() {
        // SAFETY: `rows` holds `capacity` >= count records (the caller's contract).
        unsafe {
            rows.add(index).write(KilnEcsRegistryRow {
                size: std::mem::size_of::<KilnEcsRegistryRow>() as u32,
                index: index as u32,
                component,
                type_size,
                flags,
                reserved: 0,
                type_identity,
            });
        }
    }
    KILN_OK
}
pub static TYPES: KilnTypesApi = {
    let mut table: KilnTypesApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnTypesApi>() as u32,
        version: KILN_TYPES_VERSION,
    };
    table.status = Some(types_status);
    table.type_count = Some(type_count);
    table.type_at = Some(type_at);
    table.type_info = Some(type_info);
    table.type_by_name = Some(type_by_name);
    table.ecs_registry = Some(ecs_registry);
    table
};

unsafe extern "C" fn launch_status(out: *mut KilnLaunchStatus) -> KilnResult {
    let knob = enter_thread("launch.status");
    if out.is_null() {
        KILN_ERR_INVALID_ARGUMENT
    } else {
        knob.plain_result()
    }
}
unsafe extern "C" fn launch_notice_copy(
    _buffer: *mut core::ffi::c_char,
    _capacity: usize,
    len: *mut usize,
) -> KilnResult {
    let knob = enter_thread("launch.notice_copy");
    if !len.is_null() {
        // SAFETY: live out pointer.
        unsafe { *len = 0 };
    }
    knob.plain_result()
}
pub static LAUNCH: KilnLaunchApi = {
    let mut table: KilnLaunchApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnLaunchApi>() as u32,
        version: KILN_LAUNCH_VERSION,
    };
    table.status = Some(launch_status);
    table.notice_copy = Some(launch_notice_copy);
    table
};

/// The fake game image kiln.hooks reads from.
pub static IMAGE: [u8; 64] = [0xcc; 64];
unsafe extern "C" fn image_base() -> usize {
    enter_thread("hooks.image_base");
    IMAGE.as_ptr() as usize
}
unsafe extern "C" fn image_size() -> usize {
    enter_thread("hooks.image_size");
    IMAGE.len()
}
unsafe extern "C" fn hooks_read(address: usize, out: *mut c_void, len: usize) -> KilnResult {
    let knob = enter_thread("hooks.read");
    let base = IMAGE.as_ptr() as usize;
    if out.is_null() || address < base || address.saturating_add(len) > base + IMAGE.len() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    // SAFETY: inside IMAGE; `out` holds `len` bytes (the caller's contract).
    unsafe { std::ptr::copy_nonoverlapping(address as *const u8, out.cast::<u8>(), len) };
    KILN_OK
}
pub static HOOKS: KilnHooksApi = {
    let mut table: KilnHooksApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnHooksApi>() as u32,
        version: KILN_HOOKS_VERSION,
    };
    table.image_base = Some(image_base);
    table.image_size = Some(image_size);
    table.read = Some(hooks_read);
    table
};

/// A test's kiln.client (opt-in per owner): `state` answers from it on a thread bound to that owner (the next scripted
/// state first, then `state`), and the building input members model kiln_client.h's one request slot: ids from 1,
/// QUEUED at submit, `status` and `cancel` answer only the latest id (UNKNOWN and not cancelled otherwise), a queued
/// or pressed request makes the next submit BUSY, `refuse` (when set) answers every submit.
#[derive(Clone, Default)]
pub struct ClientModel {
    pub state: Option<KilnClientState>,
    pub scripted: std::collections::VecDeque<KilnClientState>,
    pub refuse: Option<KilnResult>,
    pub next_id: u32,
    pub latest: u32,
    pub states: HashMap<u32, u32>,
    pub submitted: Vec<KilnBuildingInput>,
    pub cancelled: Vec<u32>,
}
impl ClientModel {
    /// A client hosting a world (session 1) whose local player is `player`.
    pub fn local(player: KilnEntity) -> Self {
        Self {
            state: Some(KilnClientState {
                size: std::mem::size_of::<KilnClientState>() as u32,
                side: KILN_SIDE_CLIENT as u32,
                session: KILN_SESSION_HOST as u32,
                generation: 1,
                local_player: player,
                flags: (KILN_CLIENT_LOCAL_PLAYER | KILN_CLIENT_LIFECYCLE) as u32,
                client_session: 1,
                server_session: 1,
                client_steps: 0,
                server_steps: 0,
            }),
            ..Self::default()
        }
    }
}
static CLIENTS: LazyLock<Mutex<HashMap<KilnOwner, ClientModel>>> = LazyLock::new(Default::default);
/// Sets (or, with None, removes) `owner`'s kiln.client model.
pub fn set_client(owner: KilnOwner, model: Option<ClientModel>) {
    let mut all = CLIENTS.lock().unwrap();
    match model {
        Some(model) => all.insert(owner, model),
        None => all.remove(&owner),
    };
}
/// Changes (or reads) `owner`'s kiln.client model; None without one.
pub fn client<R>(owner: KilnOwner, f: impl FnOnce(&mut ClientModel) -> R) -> Option<R> {
    CLIENTS.lock().unwrap().get_mut(&owner).map(f)
}
unsafe extern "C" fn building_input_submit(
    owner: KilnOwner,
    _expected_session: u64,
    input: *const KilnBuildingInput,
    request_id: *mut u32,
) -> KilnResult {
    enter(owner, "client.building_input_submit");
    // SAFETY: read only when non-NULL.
    if owner == 0
        || input.is_null()
        || request_id.is_null()
        || unsafe { (*input).size } < std::mem::size_of::<KilnBuildingInput>() as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let input = unsafe { *input };
    client(owner, |model| {
        model.submitted.push(input);
        if let Some(code) = model.refuse {
            return code;
        }
        if model.states.values().any(|s| {
            *s == KILN_BUILDING_INPUT_QUEUED as u32 || *s == KILN_BUILDING_INPUT_PRESSED as u32
        }) {
            return KILN_CLIENT_ERR_BUILDING_BUSY;
        }
        model.next_id = model.next_id.wrapping_add(1).max(1);
        model.latest = model.next_id;
        model
            .states
            .insert(model.latest, KILN_BUILDING_INPUT_QUEUED as u32);
        // SAFETY: checked above.
        unsafe { *request_id = model.latest };
        KILN_OK
    })
    .unwrap_or(KILN_ERR_UNAVAILABLE)
}
unsafe extern "C" fn building_input_status(
    owner: KilnOwner,
    id: u32,
    state: *mut u32,
) -> KilnResult {
    enter(owner, "client.building_input_status");
    if owner == 0 || state.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    client(owner, |model| {
        let answer = if id == model.latest {
            model.states.get(&id).copied().unwrap_or(0)
        } else {
            0
        };
        // SAFETY: checked above.
        unsafe { *state = answer };
        KILN_OK
    })
    .unwrap_or(KILN_ERR_UNAVAILABLE)
}
unsafe extern "C" fn building_input_cancel(
    owner: KilnOwner,
    id: u32,
    cancelled: *mut u32,
) -> KilnResult {
    enter(owner, "client.building_input_cancel");
    if owner == 0 || cancelled.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    client(owner, |model| {
        let latest = id != 0 && id == model.latest;
        if latest {
            model.cancelled.push(id);
            if let Some(state) = model.states.get_mut(&id)
                && *state != KILN_BUILDING_INPUT_DISPATCHED as u32
            {
                *state = KILN_BUILDING_INPUT_CANCELLED as u32;
            }
        }
        // SAFETY: checked above.
        unsafe { *cancelled = u32::from(latest) };
        KILN_OK
    })
    .unwrap_or(KILN_ERR_UNAVAILABLE)
}
unsafe extern "C" fn client_state(out: *mut KilnClientState) -> KilnResult {
    let knob = enter_thread("client.state");
    if out.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob == Knob::Ok
        && let Some(state) = client(THREAD_OWNER.get(), |model| {
            model.scripted.pop_front().or(model.state)
        })
    {
        let Some(state) = state else {
            return KILN_ERR_UNAVAILABLE;
        };
        // SAFETY: the caller's record (its size was set by the caller).
        unsafe {
            let size = (*out).size;
            *out = state;
            (*out).size = size;
        }
        return KILN_OK;
    }
    knob.plain_result()
}
unsafe extern "C" fn on_client_thread() -> bool {
    enter_thread("client.on_client_thread");
    false
}
pub static CLIENT: KilnClientApi = {
    let mut table: KilnClientApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnClientApi>() as u32,
        version: KILN_CLIENT_VERSION,
    };
    table.state = Some(client_state);
    table.on_client_thread = Some(on_client_thread);
    table.building_input_submit = Some(building_input_submit);
    table.building_input_status = Some(building_input_status);
    table.building_input_cancel = Some(building_input_cancel);
    table
};

unsafe extern "C" fn assets_status(
    _buffer: *mut core::ffi::c_char,
    _capacity: usize,
    len: *mut usize,
) -> KilnResult {
    let knob = enter_thread("assets.status");
    if !len.is_null() {
        // SAFETY: live out pointer.
        unsafe { *len = 0 };
    }
    knob.plain_result()
}
unsafe extern "C" fn resource_read(
    key: *const KilnAssetKey,
    _out: *mut c_void,
    _capacity: usize,
    required: *mut usize,
) -> KilnResult {
    let knob = enter_thread("assets.resource_read");
    if key.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if !required.is_null() {
        // SAFETY: live out pointer.
        unsafe { *required = 0 };
    }
    knob.plain_result()
}
// ---- kiln.assets' loaded resources (loaded_snapshot, loaded_list) ----

/// The loaded resources one owner's kiln.assets answers.
#[derive(Clone, Default)]
pub struct LoadedFake {
    /// The loaded resources, in listing order.
    pub resources: Vec<(KilnAssetKey, Vec<u8>)>,
    /// `Some(status)`: every read answers KILN_ERR_UNAVAILABLE with that KilnLoadedInfo status.
    pub refuse: Option<u32>,
    /// The next this many answers report a size larger than the buffer they got (a resource or list that keeps
    /// growing): KILN_ERR_BUFFER_TOO_SMALL.
    pub growing: u32,
    /// A size reported instead of the real one (the safety limits), with KILN_ERR_BUFFER_TOO_SMALL.
    pub reported: Option<usize>,
    /// `loaded_scalar_compare_exchange`'s server session (0: [`FAKE_SESSION`]).
    pub session: u64,
    /// `Some(status)`: the next compare-exchange answers that KILN_ECS_* status, writing nothing.
    pub cas_answer: Option<u32>,
    /// The reply's why_code of that answer (KILN_ECS_WRITE_WHY_*).
    pub cas_why: u32,
    /// `Some(result)`: the next compare-exchange returns that KilnResult without touching the reply.
    pub cas_refuse: Option<KilnResult>,
    /// The next this many reads with a buffer (capacity > 0) answer KILN_ERR_UNAVAILABLE with KILN_LOADED_CHANGED (a
    /// copy that changed meanwhile), while a size query still finds the resource.
    pub torn_reads: u32,
    /// Every committed write: (key, offset, the scalar's bytes, session).
    pub writes: Vec<(KilnAssetKey, usize, Vec<u8>, u64)>,
    /// The names of every committed write, as `writes` (sfi R3): (writer, field), both empty for the unnamed member.
    pub names: Vec<(String, String)>,
}
static LOADED: LazyLock<Mutex<HashMap<KilnOwner, LoadedFake>>> = LazyLock::new(Default::default);
pub fn set_loaded(owner: KilnOwner, fake: Option<LoadedFake>) {
    let mut map = LOADED.lock().unwrap();
    match fake {
        Some(fake) => map.insert(owner, fake),
        None => map.remove(&owner),
    };
}
pub fn loaded<R>(owner: KilnOwner, f: impl FnOnce(&mut LoadedFake) -> R) -> Option<R> {
    LOADED.lock().unwrap().get_mut(&owner).map(f)
}
thread_local! {
    /// Called inside every loaded read (a world session that moves between the caller's two samples).
    static ON_LOADED: RefCell<Option<Box<dyn Fn()>>> = const { RefCell::new(None) };
}
pub fn on_loaded_call(f: Option<Box<dyn Fn()>>) {
    ON_LOADED.with_borrow_mut(|slot| *slot = f);
}
fn same_key(a: &KilnAssetKey, b: &KilnAssetKey) -> bool {
    a.guid == b.guid && a.r#type == b.r#type && a.part == b.part
}
/// One loaded answer: Ok(bytes or keys count), or the refusal (result, status).
enum LoadedAnswer {
    Grow(usize),
    Refuse(KilnResult, u32),
    Size(usize),
}
fn loaded_answer(
    owner: KilnOwner,
    size: impl FnOnce(&LoadedFake) -> Option<usize>,
    capacity: usize,
) -> LoadedAnswer {
    ON_LOADED.with_borrow(|hook| {
        if let Some(hook) = hook {
            hook();
        }
    });
    let mut map = LOADED.lock().unwrap();
    let Some(fake) = map.get_mut(&owner) else {
        return LoadedAnswer::Refuse(KILN_ERR_UNAVAILABLE, KILN_LOADED_NOT_READY as u32);
    };
    if let Some(status) = fake.refuse {
        return LoadedAnswer::Refuse(KILN_ERR_UNAVAILABLE, status);
    }
    if let Some(reported) = fake.reported {
        return LoadedAnswer::Grow(reported);
    }
    if fake.growing > 0 {
        fake.growing -= 1;
        return LoadedAnswer::Grow(capacity + 1);
    }
    match size(fake) {
        Some(size) => LoadedAnswer::Size(size),
        None => LoadedAnswer::Refuse(KILN_ERR_NOT_FOUND, KILN_LOADED_MISSING as u32),
    }
}
unsafe extern "C" fn loaded_snapshot(
    owner: KilnOwner,
    key: *const KilnAssetKey,
    bytes: *mut c_void,
    capacity: usize,
    required: *mut usize,
    info: *mut KilnLoadedInfo,
) -> KilnResult {
    let knob = enter(owner, "loaded_snapshot");
    // SAFETY: the caller's record, when not NULL.
    if owner == 0
        || key.is_null()
        || required.is_null()
        || info.is_null()
        || unsafe { (*info).size } < 24
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    // SAFETY: checked above.
    let (key, info) = unsafe { (*key, &mut *info) };
    let torn = capacity > 0
        && loaded(owner, |fake| {
            let torn = fake.torn_reads > 0;
            fake.torn_reads -= u32::from(torn);
            torn
        })
        .unwrap_or(false);
    if torn {
        info.status = KILN_LOADED_CHANGED as u32;
        // SAFETY: the caller's out pointer.
        unsafe { *required = 0 };
        return KILN_ERR_UNAVAILABLE;
    }
    let answer = loaded_answer(
        owner,
        |fake| {
            fake.resources
                .iter()
                .find(|(k, _)| same_key(k, &key))
                .map(|(_, b)| b.len())
        },
        capacity,
    );
    info.status = 0;
    info.flags = 0;
    info.handle = 0;
    // SAFETY: the caller's out pointer.
    unsafe { *required = 0 };
    let size = match answer {
        LoadedAnswer::Refuse(result, status) => {
            info.status = status;
            return result;
        }
        LoadedAnswer::Grow(size) => {
            info.bytes = size as u64;
            // SAFETY: as above.
            unsafe { *required = size };
            return KILN_ERR_BUFFER_TOO_SMALL;
        }
        LoadedAnswer::Size(size) => size,
    };
    info.bytes = size as u64;
    info.handle = 1;
    // SAFETY: as above.
    unsafe { *required = size };
    if capacity < size {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    let data = LOADED.lock().unwrap()[&owner]
        .resources
        .iter()
        .find(|(k, _)| same_key(k, &key))
        .map(|(_, b)| b.clone())
        .unwrap_or_default();
    // SAFETY: the caller's buffer of `capacity` bytes.
    unsafe { std::ptr::copy_nonoverlapping(data.as_ptr(), bytes.cast::<u8>(), data.len()) };
    KILN_OK
}
unsafe extern "C" fn loaded_list(
    owner: KilnOwner,
    type_hash: u32,
    out: *mut KilnAssetKey,
    capacity: usize,
    required: *mut usize,
    info: *mut KilnLoadedInfo,
) -> KilnResult {
    let knob = enter(owner, "loaded_list");
    // SAFETY: the caller's record, when not NULL.
    if owner == 0 || required.is_null() || info.is_null() || unsafe { (*info).size } < 24 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    // SAFETY: checked above.
    let info = unsafe { &mut *info };
    let answer = loaded_answer(
        owner,
        |fake| {
            Some(
                fake.resources
                    .iter()
                    .filter(|(k, _)| k.r#type == type_hash)
                    .count(),
            )
        },
        capacity,
    );
    // SAFETY: the caller's out pointer.
    unsafe { *required = 0 };
    let count = match answer {
        LoadedAnswer::Refuse(result, status) => {
            info.status = status;
            return result;
        }
        LoadedAnswer::Grow(count) => {
            // SAFETY: as above.
            unsafe { *required = count };
            return KILN_ERR_BUFFER_TOO_SMALL;
        }
        LoadedAnswer::Size(count) => count,
    };
    info.status = 0;
    info.bytes = count as u64;
    // SAFETY: as above.
    unsafe { *required = count };
    if capacity < count {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    let keys: Vec<KilnAssetKey> = LOADED.lock().unwrap()[&owner]
        .resources
        .iter()
        .filter(|(k, _)| k.r#type == type_hash)
        .map(|(k, _)| *k)
        .collect();
    // SAFETY: room for `capacity` keys.
    unsafe { std::ptr::copy_nonoverlapping(keys.as_ptr(), out, keys.len()) };
    KILN_OK
}

#[allow(clippy::too_many_arguments)]
unsafe extern "C" fn loaded_scalar_compare_exchange(
    owner: KilnOwner,
    key: *const KilnAssetKey,
    expected_session: u64,
    expected_full: *const c_void,
    resource_size: usize,
    offset: usize,
    desired: *const c_void,
    width: u32,
    reply: *mut KilnMutationReply,
) -> KilnResult {
    let knob = enter(owner, "loaded_scalar_compare_exchange");
    // SAFETY: the caller's arguments, passed on as they came.
    unsafe {
        loaded_cas(
            knob,
            owner,
            key,
            expected_session,
            expected_full,
            resource_size,
            offset,
            desired,
            width,
            (String::new(), String::new()),
            reply,
        )
    }
}

/// kiln.assets' name check (kiln_assets.h loaded_scalar_compare_exchange_named): at most 127 bytes of UTF-8 without a
/// control character; `None` when the name is not such text.
fn loaded_name(text: KilnStr) -> Option<String> {
    if text.len == 0 {
        return Some(String::new());
    }
    if text.ptr.is_null() || text.len > 127 {
        return None;
    }
    // SAFETY: the caller's text, readable for `len` bytes.
    let bytes = unsafe { std::slice::from_raw_parts(text.ptr.cast::<u8>(), text.len) };
    let name = std::str::from_utf8(bytes).ok()?;
    (!name.chars().any(char::is_control)).then(|| name.to_owned())
}

#[allow(clippy::too_many_arguments)]
unsafe extern "C" fn loaded_scalar_compare_exchange_named(
    owner: KilnOwner,
    key: *const KilnAssetKey,
    expected_session: u64,
    expected_full: *const c_void,
    resource_size: usize,
    offset: usize,
    desired: *const c_void,
    width: u32,
    writer: KilnStr,
    field: KilnStr,
    reply: *mut KilnMutationReply,
) -> KilnResult {
    let knob = enter(owner, "loaded_scalar_compare_exchange_named");
    let (Some(writer), Some(field)) = (loaded_name(writer), loaded_name(field)) else {
        return KILN_ERR_INVALID_ARGUMENT;
    };
    // SAFETY: the caller's arguments, passed on as they came.
    unsafe {
        loaded_cas(
            knob,
            owner,
            key,
            expected_session,
            expected_full,
            resource_size,
            offset,
            desired,
            width,
            (writer, field),
            reply,
        )
    }
}

/// Both members' write: the unnamed one passes empty names.
#[allow(clippy::too_many_arguments)]
unsafe fn loaded_cas(
    knob: Knob,
    owner: KilnOwner,
    key: *const KilnAssetKey,
    expected_session: u64,
    expected_full: *const c_void,
    resource_size: usize,
    offset: usize,
    desired: *const c_void,
    width: u32,
    name: (String, String),
    reply: *mut KilnMutationReply,
) -> KilnResult {
    let width = width as usize;
    // SAFETY: the caller's record, when not NULL.
    if owner == 0
        || key.is_null()
        || expected_full.is_null()
        || desired.is_null()
        || reply.is_null()
        || unsafe { (*reply).size } < std::mem::size_of::<KilnMutationReply>() as u32
        || expected_session == 0
        || !matches!(width, 1 | 2 | 4 | 8)
        || !offset.is_multiple_of(width)
        || offset + width > resource_size
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if knob != Knob::Ok {
        return knob.plain_result();
    }
    if let Some(result) = loaded(owner, |f| f.cas_refuse.take()).flatten() {
        return result;
    }
    // SAFETY: checked above; the caller's buffers of those sizes.
    let (key, reply, expected, desired) = unsafe {
        (
            *key,
            &mut *reply,
            std::slice::from_raw_parts(expected_full.cast::<u8>(), resource_size).to_vec(),
            std::slice::from_raw_parts(desired.cast::<u8>(), width).to_vec(),
        )
    };
    reply.session = expected_session;
    let mut why = 0u32;
    let status = {
        let mut map = LOADED.lock().unwrap();
        match map.get_mut(&owner) {
            None => KILN_ECS_UNAVAILABLE,
            Some(fake) => {
                let session = if fake.session == 0 {
                    FAKE_SESSION
                } else {
                    fake.session
                };
                if let Some(answer) = fake.cas_answer.take() {
                    why = std::mem::take(&mut fake.cas_why);
                    answer as i32
                } else if session != expected_session {
                    KILN_ECS_STALE
                } else {
                    match fake.resources.iter_mut().find(|(k, _)| same_key(k, &key)) {
                        None => KILN_ECS_STALE,
                        Some((_, bytes)) if bytes.len() != resource_size => KILN_ECS_STALE,
                        Some((_, bytes)) if *bytes != expected => KILN_ECS_CONFLICT,
                        Some((_, bytes)) => {
                            bytes[offset..offset + width].copy_from_slice(&desired);
                            fake.writes
                                .push((key, offset, desired.clone(), expected_session));
                            fake.names.push(name.clone());
                            KILN_ECS_OK
                        }
                    }
                }
            }
        }
    };
    reply.status = status as u32;
    reply.why_code = why;
    reply.flags = if status == KILN_ECS_OK {
        (KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED) as u32
    } else {
        0
    };
    match status {
        KILN_ECS_OK => KILN_OK,
        KILN_ECS_CONFLICT => KILN_ECS_ERR_CONFLICT,
        KILN_ECS_STALE => KILN_ECS_ERR_STALE,
        KILN_ECS_UNCERTAIN => KILN_ECS_ERR_UNCERTAIN,
        KILN_ECS_BUSY => KILN_ECS_ERR_BUSY,
        _ => KILN_ERR_UNAVAILABLE,
    }
}

pub static ASSETS: KilnAssetsApi = {
    let mut table: KilnAssetsApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnAssetsApi>() as u32,
        version: KILN_ASSETS_VERSION,
    };
    table.status = Some(assets_status);
    table.resource_read = Some(resource_read);
    table.loaded_snapshot = Some(loaded_snapshot);
    table.loaded_list = Some(loaded_list);
    table.loaded_scalar_compare_exchange = Some(loaded_scalar_compare_exchange);
    table.loaded_scalar_compare_exchange_named = Some(loaded_scalar_compare_exchange_named);
    table
};

unsafe extern "C" fn build_status(out: *mut KilnBuildStatus) -> KilnResult {
    let knob = enter_thread("build.status");
    if out.is_null() {
        KILN_ERR_INVALID_ARGUMENT
    } else {
        knob.plain_result()
    }
}
unsafe extern "C" fn build_read(
    _min: *const i32,
    _extent: *const u32,
    _out: *mut KilnVoxel,
    _capacity: usize,
) -> KilnResult {
    enter_thread("build.read").plain_result()
}
/// The grid `grid_for_session` answers on a thread bound to `owner` (its record, size aside); without one the current
/// builds' grid ([`current_grid`]).
static GRID_SPECS: LazyLock<Mutex<HashMap<KilnOwner, KilnBuildGridSpec>>> =
    LazyLock::new(Default::default);
/// Every `grid_for_session` call per owner: (expected session, id bytes).
type GridCalls = HashMap<KilnOwner, Vec<(u64, Vec<u8>)>>;
static GRID_CALLS: LazyLock<Mutex<GridCalls>> = LazyLock::new(Default::default);
/// The caller owner each `grid_for_session` call named, per thread owner.
static GRID_CALLERS: LazyLock<Mutex<HashMap<KilnOwner, Vec<KilnOwner>>>> =
    LazyLock::new(Default::default);
/// The current builds' grid: origin (0, -4, 0), 0.5 m cells, 20480 x (2^31 - 8) x 20480 cells.
pub fn current_grid() -> KilnBuildGridSpec {
    // SAFETY: plain C record; every field valid when zeroed.
    let mut spec: KilnBuildGridSpec = unsafe { std::mem::zeroed() };
    spec.size = std::mem::size_of::<KilnBuildGridSpec>() as u32;
    for (slot, byte) in spec.id.iter_mut().zip(b"voxel") {
        *slot = *byte as core::ffi::c_char;
    }
    spec.origin = [0.0, -4.0, 0.0];
    spec.cell_size = [0.5; 3];
    spec.maximum = [20480, 0x7fff_ffff - 7, 20480];
    spec
}
pub fn set_grid_spec(owner: KilnOwner, spec: Option<KilnBuildGridSpec>) {
    let mut specs = GRID_SPECS.lock().unwrap();
    match spec {
        Some(spec) => specs.insert(owner, spec),
        None => specs.remove(&owner),
    };
}
pub fn grid_callers(owner: KilnOwner) -> Vec<KilnOwner> {
    GRID_CALLERS
        .lock()
        .unwrap()
        .get(&owner)
        .cloned()
        .unwrap_or_default()
}
pub fn grid_calls(owner: KilnOwner) -> Vec<(u64, Vec<u8>)> {
    GRID_CALLS
        .lock()
        .unwrap()
        .get(&owner)
        .cloned()
        .unwrap_or_default()
}
/// kiln_build.h grid_for_session (the caller's owner recorded: [`grid_callers`]): the real member's argument checks first, then the knob (busy, stale, unreadable,
/// unavailable; conflict and uncertain are not its outcomes and answer as ok), then the session: the owner's server
/// world session (`game_world_session`'s fixed value) or stale. The record is written on KILN_OK only.
unsafe extern "C" fn grid_for_session(
    caller: KilnOwner,
    session: u64,
    id: KilnStr,
    out: *mut KilnBuildGridSpec,
) -> KilnResult {
    let owner = THREAD_OWNER.get();
    let knob = enter(owner, "build.grid_for_session");
    // SAFETY: the caller's id, readable for len bytes when not NULL.
    let bytes = if id.ptr.is_null() {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(id.ptr.cast::<u8>(), id.len) }.to_vec()
    };
    GRID_CALLS
        .lock()
        .unwrap()
        .entry(owner)
        .or_default()
        .push((session, bytes.clone()));
    GRID_CALLERS
        .lock()
        .unwrap()
        .entry(owner)
        .or_default()
        .push(caller);
    // SAFETY: a live record when not NULL (the caller's contract).
    if out.is_null()
        || unsafe { (*out).size } < std::mem::size_of::<KilnBuildGridSpec>() as u32
        || session == 0
        || caller == 0
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if bytes.is_empty() || bytes.len() >= 16 || bytes.contains(&0) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if bytes != b"voxel" {
        return KILN_ERR_NOT_FOUND;
    }
    match knob {
        Knob::Busy | Knob::Stale | Knob::Unreadable | Knob::Unavailable => {
            return knob.ecs_result();
        }
        _ => {}
    }
    let live = WORLD_SESSIONS
        .lock()
        .unwrap()
        .get(&(owner, KILN_WORLD_SERVER as u32))
        .copied()
        .unwrap_or(FAKE_SESSION);
    if session != live {
        return KILN_ECS_ERR_STALE;
    }
    let spec = GRID_SPECS
        .lock()
        .unwrap()
        .get(&owner)
        .copied()
        .unwrap_or_else(current_grid);
    // SAFETY: as above; the caller's size is kept.
    unsafe {
        let size = (*out).size;
        *out = spec;
        (*out).size = size;
    }
    KILN_OK
}
pub static BUILD: KilnBuildApi = {
    let mut table: KilnBuildApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnBuildApi>() as u32,
        version: KILN_BUILD_VERSION,
    };
    table.status = Some(build_status);
    table.read = Some(build_read);
    table.grid_for_session = Some(grid_for_session);
    table
};

// ---- kiln.caps: the rows a test reports ----

/// name → (state, reason, ever pending). Process-wide (kiln.caps has no owner): tests use
/// names of their own.
type CapRows = BTreeMap<String, (u32, u32, bool)>;
static CAPS: LazyLock<Mutex<CapRows>> = LazyLock::new(Default::default);
static CAPS_GENERATION: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
/// Sets (`Some((KILN_CAP_*, KILN_CAP_REASON_*))`) or removes capability row `name`.
pub fn set_cap(name: &str, row: Option<(i32, i32)>) {
    let mut caps = CAPS.lock().unwrap();
    match row {
        Some((state, reason)) => {
            let pending =
                state == KILN_CAP_PENDING || caps.get(name).is_some_and(|(_, _, pending)| *pending);
            caps.insert(name.to_owned(), (state as u32, reason as u32, pending));
        }
        None => {
            caps.remove(name);
        }
    }
    CAPS_GENERATION.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
}
thread_local! {
    /// The strings of the last row this thread read (kiln_caps.h: valid until the
    /// thread's next at()/find()).
    static CAP_STRINGS: RefCell<(String, String)> = const { RefCell::new((String::new(), String::new())) };
}
/// Fills `out` as kiln_caps.h says; the caller checked it.
unsafe fn fill_cap(out: *mut KilnCapRow, name: &str, (state, reason, pending): (u32, u32, bool)) {
    let (name, source) = CAP_STRINGS.with_borrow_mut(|s| {
        *s = (name.to_owned(), "test".to_owned());
        (
            KilnStr {
                ptr: s.0.as_ptr().cast(),
                len: s.0.len(),
            },
            KilnStr {
                ptr: s.1.as_ptr().cast(),
                len: s.1.len(),
            },
        )
    });
    // SAFETY: the caller's record, at least v1 long.
    unsafe {
        (*out).state = state;
        (*out).reason = reason;
        (*out).flags = if pending {
            KILN_CAP_FLAG_SESSION as u32
        } else {
            0
        };
        (*out).name = name;
        (*out).text = KilnStr {
            ptr: std::ptr::null(),
            len: 0,
        };
        (*out).source = source;
    }
}
fn short_row(out: *const KilnCapRow) -> bool {
    // SAFETY: read only when non-null.
    out.is_null() || unsafe { (*out).size } < std::mem::size_of::<KilnCapRow>() as u32
}
unsafe extern "C" fn caps_count() -> u32 {
    enter_thread("caps.count");
    CAPS.lock().unwrap().len() as u32
}
unsafe extern "C" fn caps_at(index: u32, out: *mut KilnCapRow) -> KilnResult {
    enter_thread("caps.at");
    if short_row(out) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let caps = CAPS.lock().unwrap();
    let Some((name, row)) = caps.iter().nth(index as usize) else {
        return KILN_ERR_NOT_FOUND;
    };
    // SAFETY: checked above.
    unsafe { fill_cap(out, name, *row) };
    KILN_OK
}
/// KILN_ERR_NOT_FOUND (out left alone) when nobody reported `name`.
unsafe extern "C" fn caps_find(name: KilnStr, out: *mut KilnCapRow) -> KilnResult {
    enter_thread("caps.find");
    if short_row(out) || (name.ptr.is_null() && name.len != 0) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's name, readable for `len` bytes.
    let text = unsafe { std::slice::from_raw_parts(name.ptr.cast::<u8>(), name.len) };
    let text = String::from_utf8_lossy(text).into_owned();
    let Some(row) = CAPS.lock().unwrap().get(&text).copied() else {
        return KILN_ERR_NOT_FOUND;
    };
    // SAFETY: checked above.
    unsafe { fill_cap(out, &text, row) };
    KILN_OK
}
unsafe extern "C" fn caps_generation() -> u64 {
    CAPS_GENERATION.load(std::sync::atomic::Ordering::SeqCst)
}
pub static CAPS_TABLE: KilnCapsApi = {
    // SAFETY: every member is a nullable function pointer or plain data.
    let mut table: KilnCapsApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnCapsApi>() as u32,
        version: KILN_CAPS_VERSION,
    };
    table.count = Some(caps_count);
    table.at = Some(caps_at);
    table.find = Some(caps_find);
    table.generation = Some(caps_generation);
    table
};

// ---- kiln.fake.timed: a member that reports source_wait_ns ----

pub const FAKE_TIMED_SERVICE: &[u8] = b"kiln.fake.timed\0";
pub const FAKE_TIMED_VERSION: u32 = 1;
/// The reply of a native member with ShroudForge-bounded waits (`source_wait_ns`).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct FakeTimedReply {
    pub size: u32,
    pub status: u32,
    pub source_wait_ns: u64,
}
#[repr(C)]
pub struct FakeTimedApi {
    pub header: KilnServiceHeader,
    pub call: Option<unsafe extern "C" fn(KilnOwner, *mut FakeTimedReply) -> KilnResult>,
}
unsafe extern "C" fn timed_call(owner: KilnOwner, reply: *mut FakeTimedReply) -> KilnResult {
    let knob = enter(owner, "timed.call");
    ON_TIMED_CALL.with_borrow(|hook| {
        if let Some(hook) = hook {
            hook();
        }
    });
    let wait = with(owner, |s| {
        if s.report_wait_ns > 0 {
            s.counters.waits += 1;
        }
        s.report_wait_ns
    });
    if reply.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: the caller's reply record.
    unsafe {
        *reply = FakeTimedReply {
            size: core::mem::size_of::<FakeTimedReply>() as u32,
            status: knob.ecs_status(),
            source_wait_ns: wait,
        }
    };
    knob.ecs_result()
}
pub static TIMED: FakeTimedApi = FakeTimedApi {
    header: KilnServiceHeader {
        size: std::mem::size_of::<FakeTimedApi>() as u32,
        version: FAKE_TIMED_VERSION,
    },
    call: Some(timed_call),
};

// ---- kiln.net: v1 plus the Steam-messages members ----

use kiln_abi::services::kiln_net::KilnNetApi;
use kiln_sfi::runtime::network::l16::*;
use std::collections::VecDeque;

/// One step of a channel's inbound queue.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Inbound {
    /// A message: sender, bytes, and whether it came reliably.
    Message {
        peer: u64,
        data: Vec<u8>,
        reliable: bool,
    },
    /// `receive` answers this code once (nothing taken).
    Result(KilnResult),
}
/// What one `send` was given.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Sent {
    pub lease: KilnHandle,
    pub peer: u64,
    pub data: Vec<u8>,
    pub channel: u32,
    pub flags: u32,
}
/// The lease's state for one owner (`status` and `open` count for the thread's owner, the
/// others for their lease handle's owner).
#[derive(Clone, Debug)]
pub struct NetFake {
    /// What `status` answers, and its flags and identity.
    pub status_result: KilnResult,
    pub flags: u32,
    pub local_steam_id: u64,
    /// `open`: a code it answers instead of opening (`KILN_ERR_UNAVAILABLE`: not the designated
    /// owner), and the leases opened: (owner, first, last, flags, open).
    pub open_result: KilnResult,
    pub leases: Vec<(KilnOwner, u32, u32, u32, bool)>,
    /// `send`: its code and the EResult it reports; every message it took.
    pub send_result: KilnResult,
    pub eresult: i32,
    pub sent: Vec<Sent>,
    /// `receive`: each channel's queue.
    pub inbound: HashMap<u32, VecDeque<Inbound>>,
    /// `accept`: its code, `*accepted`, and the peers asked.
    pub accept_result: KilnResult,
    pub accepted_answer: u32,
    pub accepted: Vec<u64>,
    /// `maintain` as kiln.net's real member answers it (steam_messages.cpp `st_maintain`):
    /// the session policy in force (`None` until the first `maintain`; from then on REGISTERED
    /// is "registered and the mode is not NONE", whatever `flags` says), every policy written,
    /// and how often the session callback was registered (once per process). NONE is in force at
    /// once in any Steam state; a policy that accepts waits for Steam (AVAILABLE). One fake owner
    /// stands for one process: the real block's state is per process, and only the designated
    /// owner may open a lease there.
    pub policy: Option<(u32, Vec<u64>)>,
    pub policies: Vec<(u32, Vec<u64>)>,
    pub registrations: u32,
    /// The callback's registration faults (`g_register_faulted`): REGISTERED never comes.
    pub register_fails: bool,
    /// The owner every lease call (close, maintain, send, receive, accept) on this owner's leases passed, in order.
    /// kiln.net answers a lease of another owner as a closed one (`KILN_ERR_NOT_FOUND`), and so does this fake.
    pub lease_call_owners: Vec<(&'static str, KilnOwner)>,
}
impl Default for NetFake {
    fn default() -> Self {
        Self {
            status_result: KILN_OK,
            flags: KILN_STEAM_MSG_AVAILABLE | KILN_STEAM_MSG_REGISTERED | KILN_STEAM_MSG_IDENTITY,
            local_steam_id: 76_561_198_000_000_001,
            open_result: KILN_OK,
            leases: Vec::new(),
            send_result: KILN_OK,
            eresult: 1,
            sent: Vec::new(),
            inbound: HashMap::new(),
            accept_result: KILN_OK,
            accepted_answer: 1,
            accepted: Vec::new(),
            policy: None,
            policies: Vec::new(),
            registrations: 0,
            register_fails: false,
            lease_call_owners: Vec::new(),
        }
    }
}
static NETS: LazyLock<Mutex<HashMap<KilnOwner, NetFake>>> = LazyLock::new(Default::default);
/// Changes `owner`'s lease state.
pub fn net<R>(owner: KilnOwner, f: impl FnOnce(&mut NetFake) -> R) -> R {
    f(NETS.lock().unwrap().entry(owner).or_default())
}
impl NetFake {
    /// The flags `status` answers (st_status / fill_status).
    fn status_flags(&self) -> u32 {
        let leased = self
            .leases
            .iter()
            .any(|l| l.4 && l.3 & KILN_STEAM_OPEN_SESSIONS != 0);
        let mut flags = self.flags | if leased { KILN_STEAM_MSG_LEASED } else { 0 };
        if let Some((mode, _)) = &self.policy {
            flags &= !KILN_STEAM_MSG_REGISTERED;
            if self.registrations > 0 && *mode != KILN_STEAM_ACCEPT_NONE {
                flags |= KILN_STEAM_MSG_REGISTERED;
            }
        }
        flags
    }
    /// The open leases.
    pub fn open_leases(&self) -> usize {
        self.leases.iter().filter(|l| l.4).count()
    }
}
/// Queues messages or codes on `channel` for `owner`.
pub fn queue(owner: KilnOwner, channel: u32, steps: impl IntoIterator<Item = Inbound>) {
    net(owner, |n| {
        n.inbound.entry(channel).or_default().extend(steps)
    });
}
fn lease_owner(lease: KilnHandle) -> (KilnOwner, usize) {
    (lease >> 16, (lease & 0xffff) as usize)
}
/// kiln.net's owner check of a lease member (steam_messages.cpp): owner 0 is an invalid argument, and a lease of another
/// owner answers as a closed one. Records the owner passed under the lease's owner.
fn lease_caller(
    member: &'static str,
    owner: KilnOwner,
    lease: KilnHandle,
) -> Result<(), KilnResult> {
    if owner == 0 {
        return Err(KILN_ERR_INVALID_ARGUMENT);
    }
    let (holder, _) = lease_owner(lease);
    net(holder, |n| n.lease_call_owners.push((member, owner)));
    if holder != owner {
        return Err(KILN_ERR_NOT_FOUND);
    }
    Ok(())
}
/// The open lease `lease` names: (owner, first, last).
fn open_lease(lease: KilnHandle) -> Option<(KilnOwner, u32, u32)> {
    let (owner, index) = lease_owner(lease);
    net(owner, |n| {
        n.leases
            .get(index.wrapping_sub(1))
            .filter(|l| l.4)
            .map(|l| (owner, l.1, l.2))
    })
}
unsafe extern "C" fn net_v1_status(
    out: *mut kiln_abi::services::kiln_net::KilnNetStatus,
) -> KilnResult {
    enter_thread("net.v1.status");
    if out.is_null() {
        KILN_ERR_INVALID_ARGUMENT
    } else {
        KILN_OK
    }
}
unsafe extern "C" fn steam_status(out: *mut KilnSteamMessagesStatus) -> KilnResult {
    let owner = THREAD_OWNER.get();
    enter(owner, "net.status");
    // SAFETY: read only when non-NULL.
    if out.is_null() || unsafe { (*out).size } < size_of::<KilnSteamMessagesStatus>() as u32 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    net(owner, |n| {
        if n.status_result != KILN_OK {
            return n.status_result;
        }
        // SAFETY: the caller's record, checked above.
        unsafe {
            (*out).flags = n.status_flags();
            (*out).local_steam_id = if n.flags & KILN_STEAM_MSG_IDENTITY != 0 {
                n.local_steam_id
            } else {
                0
            };
        }
        KILN_OK
    })
}
unsafe extern "C" fn steam_open(
    owner: KilnOwner,
    request: *const KilnSteamMessagesOpen,
    lease: *mut KilnHandle,
) -> KilnResult {
    enter(owner, "net.open");
    // SAFETY: read only when non-NULL.
    if owner == 0
        || request.is_null()
        || lease.is_null()
        || unsafe { (*request).size } < size_of::<KilnSteamMessagesOpen>() as u32
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let request = unsafe { *request };
    net(owner, |n| {
        if n.open_result != KILN_OK {
            return n.open_result;
        }
        let overlaps = n
            .leases
            .iter()
            .any(|l| l.4 && request.first_channel <= l.2 && l.1 <= request.last_channel);
        let sessions_twice = request.flags & KILN_STEAM_OPEN_SESSIONS != 0
            && n.leases
                .iter()
                .any(|l| l.4 && l.3 & KILN_STEAM_OPEN_SESSIONS != 0);
        if request.flags & !KILN_STEAM_OPEN_SESSIONS != 0
            || request.first_channel > request.last_channel
            || request.last_channel > 65_535
            || overlaps
            || sessions_twice
        {
            return KILN_ERR_INVALID_ARGUMENT;
        }
        n.leases.push((
            owner,
            request.first_channel,
            request.last_channel,
            request.flags,
            true,
        ));
        // SAFETY: checked above.
        unsafe { *lease = (owner << 16) | n.leases.len() as u64 };
        KILN_OK
    })
}
unsafe extern "C" fn steam_close(caller: KilnOwner, lease: KilnHandle) -> KilnResult {
    let (owner, index) = lease_owner(lease);
    enter(owner, "net.close");
    match lease_caller("close", caller, lease) {
        Err(KILN_ERR_NOT_FOUND) => return KILN_OK, // another owner's lease: nothing to close
        Err(code) => return code,
        Ok(()) => {}
    }
    net(owner, |n| {
        if let Some(l) = n.leases.get_mut(index.wrapping_sub(1))
            && l.4
        {
            l.4 = false;
            // Closing the session lease: the callback answers nothing now (st_close).
            if l.3 & KILN_STEAM_OPEN_SESSIONS != 0 && n.policy.is_some() {
                n.policy = Some((KILN_STEAM_ACCEPT_NONE, Vec::new()));
            }
        }
    });
    // Closed already, or never a lease: nothing to do.
    KILN_OK
}
unsafe extern "C" fn steam_maintain(
    caller: KilnOwner,
    lease: KilnHandle,
    policy: *const KilnSteamAcceptPolicy,
    out: *mut KilnSteamMessagesStatus,
) -> KilnResult {
    let (owner, index) = lease_owner(lease);
    enter(owner, "net.maintain");
    if let Err(code) = lease_caller("maintain", caller, lease) {
        return code;
    }
    // SAFETY: read only when non-NULL.
    if !out.is_null() && unsafe { (*out).size } < size_of::<KilnSteamMessagesStatus>() as u32 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let Some(sessions) = net(owner, |n| {
        n.leases
            .get(index.wrapping_sub(1))
            .filter(|l| l.4)
            .map(|l| l.3 & KILN_STEAM_OPEN_SESSIONS != 0)
    }) else {
        return KILN_ERR_NOT_FOUND;
    };
    if !sessions {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: read only when non-NULL and sized.
    if policy.is_null() || unsafe { (*policy).size } < size_of::<KilnSteamAcceptPolicy>() as u32 {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    let policy = unsafe { *policy };
    if policy.mode > KILN_STEAM_ACCEPT_LISTED
        || policy.count > 256
        || (policy.mode == KILN_STEAM_ACCEPT_LISTED && policy.count != 0 && policy.ids.is_null())
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    let ids: Vec<u64> = if policy.mode == KILN_STEAM_ACCEPT_LISTED && policy.count != 0 {
        // SAFETY: the caller's `count` ids.
        unsafe { std::slice::from_raw_parts(policy.ids, policy.count as usize) }.to_vec()
    } else {
        Vec::new()
    };
    if ids.contains(&0) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    net(owner, |n| {
        if policy.mode == KILN_STEAM_ACCEPT_NONE {
            n.policy = Some((KILN_STEAM_ACCEPT_NONE, Vec::new()));
            n.policies.push((KILN_STEAM_ACCEPT_NONE, Vec::new()));
        }
        let ready = n.status_result == KILN_OK && n.flags & KILN_STEAM_MSG_AVAILABLE != 0;
        if !ready {
            if !out.is_null() {
                // SAFETY: checked above.
                unsafe {
                    (*out).flags = 0;
                    (*out).local_steam_id = 0;
                }
            }
            return KILN_OK;
        }
        if policy.mode != KILN_STEAM_ACCEPT_NONE {
            n.policy = Some((policy.mode, ids.clone()));
            n.policies.push((policy.mode, ids.clone()));
            if n.registrations == 0 && !n.register_fails {
                n.registrations = 1;
            }
        }
        if !out.is_null() {
            // SAFETY: checked above.
            unsafe {
                (*out).flags = n.status_flags();
                (*out).local_steam_id = if n.flags & KILN_STEAM_MSG_IDENTITY != 0 {
                    n.local_steam_id
                } else {
                    0
                };
                (*out).accept_mode = n.policy.as_ref().map_or(0, |p| p.0);
                (*out).listed = n.policy.as_ref().map_or(0, |p| p.1.len() as u32);
            }
        }
        KILN_OK
    })
}
#[allow(clippy::too_many_arguments)]
unsafe extern "C" fn steam_send(
    caller: KilnOwner,
    lease: KilnHandle,
    peer: u64,
    data: *const c_void,
    len: u32,
    channel: u32,
    flags: u32,
    steam_result: *mut i32,
) -> KilnResult {
    let (owner, _) = lease_owner(lease);
    enter(owner, "net.send");
    if let Err(code) = lease_caller("send", caller, lease) {
        return code;
    }
    let Some((_, first, last)) = open_lease(lease) else {
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if peer == 0
        || len > KILN_STEAM_MESSAGE_MAX
        || !(first..=last).contains(&channel)
        || ![0, 8, 32, 40].contains(&flags)
        || steam_result.is_null()
        || (len != 0 && data.is_null())
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: `data` holds `len` bytes (the caller's contract).
    let bytes = if len == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(data.cast::<u8>(), len as usize) }.to_vec()
    };
    net(owner, |n| {
        if n.send_result != KILN_OK {
            return n.send_result;
        }
        n.sent.push(Sent {
            lease,
            peer,
            data: bytes,
            channel,
            flags,
        });
        // SAFETY: checked above.
        unsafe { *steam_result = n.eresult };
        KILN_OK
    })
}
unsafe extern "C" fn steam_receive(
    caller: KilnOwner,
    lease: KilnHandle,
    channel: u32,
    buffer: *mut c_void,
    cap: u32,
    out: *mut KilnSteamMessageInfo,
    got: *mut u32,
) -> KilnResult {
    let (owner, _) = lease_owner(lease);
    enter(owner, "net.receive");
    if let Err(code) = lease_caller("receive", caller, lease) {
        return code;
    }
    let Some((_, first, last)) = open_lease(lease) else {
        return KILN_ERR_INVALID_ARGUMENT;
    };
    // SAFETY: read only when non-NULL.
    if got.is_null()
        || out.is_null()
        || unsafe { (*out).size } < size_of::<KilnSteamMessageInfo>() as u32
        || !(first..=last).contains(&channel)
        || (cap != 0 && buffer.is_null())
    {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // SAFETY: checked above.
    unsafe { *got = 0 };
    let step = net(owner, |n| {
        n.inbound.get_mut(&channel).and_then(VecDeque::pop_front)
    });
    match step {
        None => KILN_OK,
        Some(Inbound::Result(code)) => code,
        Some(Inbound::Message {
            peer,
            data,
            reliable,
        }) => {
            // An oversize message or one without a Steam id is released and refused.
            if data.len() > cap as usize || peer == 0 {
                return KILN_STEAM_ERR_INVALID_DATA;
            }
            // SAFETY: `buffer` holds `cap` bytes; the record and `got` are checked above.
            unsafe {
                std::ptr::copy_nonoverlapping(data.as_ptr(), buffer.cast::<u8>(), data.len());
                (*out).length = data.len() as u32;
                (*out).peer_steam_id = peer;
                (*out).channel = channel;
                (*out).flags = if reliable {
                    KILN_STEAM_SEND_RELIABLE
                } else {
                    0
                };
                *got = 1;
            }
            KILN_OK
        }
    }
}
unsafe extern "C" fn steam_accept(
    caller: KilnOwner,
    lease: KilnHandle,
    peer: u64,
    accepted: *mut u32,
) -> KilnResult {
    let (owner, _) = lease_owner(lease);
    enter(owner, "net.accept");
    if let Err(code) = lease_caller("accept", caller, lease) {
        return code;
    }
    if open_lease(lease).is_none() || peer == 0 || accepted.is_null() {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    net(owner, |n| {
        if n.accept_result != KILN_OK {
            return n.accept_result;
        }
        n.accepted.push(peer);
        // SAFETY: checked above.
        unsafe { *accepted = n.accepted_answer };
        KILN_OK
    })
}
pub static NET: KilnNetApi = {
    // SAFETY: every member is a nullable function pointer or plain data.
    let mut table: KilnNetApi = unsafe { std::mem::zeroed() };
    table.header = KilnServiceHeader {
        size: std::mem::size_of::<KilnNetApi>() as u32,
        version: kiln_abi::services::kiln_net::KILN_NET_VERSION,
    };
    table.status = Some(net_v1_status);
    table.steam_status = Some(steam_status);
    table.steam_open = Some(steam_open);
    table.steam_close = Some(steam_close);
    table.steam_maintain = Some(steam_maintain);
    table.steam_send = Some(steam_send);
    table.steam_receive = Some(steam_receive);
    table.steam_accept = Some(steam_accept);
    table
};

// ---- the lookup and its table shapes ----

/// How a lookup on this thread serves a service.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Shape {
    /// NULL: not provided.
    Missing,
    /// A heap copy exactly `size` bytes long that says `size` (a short prefix).
    Short(u32),
    /// A copy that says it is this (older) version.
    Version(u32),
    /// A copy at an address misaligned for the table.
    Misaligned,
    /// A copy whose member slot at this byte offset is NULL.
    NullAt(usize),
    /// A private, full copy (see [`last_copy`]).
    Copy,
}
thread_local! {
    static SHAPES: RefCell<HashMap<&'static str, Shape>> = RefCell::new(HashMap::new());
    static LAST_COPY: Cell<usize> = const { Cell::new(0) };
}
/// Lookups of `service` (without the NUL) on this thread now return this shape.
pub fn set_shape(service: &'static str, shape: Option<Shape>) {
    SHAPES.with_borrow_mut(|shapes| match shape {
        Some(shape) => shapes.insert(service, shape),
        None => shapes.remove(service),
    });
}
/// The table a `Shape::Short`/`Version`/`NullAt`/`Copy`/`Misaligned` lookup on this
/// thread returned last (leaked for the process).
pub fn last_copy() -> *mut u8 {
    LAST_COPY.get() as *mut u8
}

fn table_of(service: &str) -> Option<(*const u8, usize)> {
    fn of<T>(table: &'static T) -> Option<(*const u8, usize)> {
        Some(((table as *const T).cast(), std::mem::size_of::<T>()))
    }
    match service {
        "kiln.world" => of(&WORLD),
        "kiln.types" => of(&TYPES),
        "kiln.launch" => of(&LAUNCH),
        "kiln.hooks" => of(&HOOKS),
        "kiln.client" => of(&CLIENT),
        "kiln.assets" => of(&ASSETS),
        "kiln.build" => of(&BUILD),
        "kiln.caps" => of(&CAPS_TABLE),
        "kiln.fake.timed" => of(&TIMED),
        "kiln.net" => of(&NET),
        "kiln.tasks" => Some((
            support::tasks_table().cast(),
            std::mem::size_of::<kiln_abi::services::kiln_tasks::KilnTasksApi>(),
        )),
        _ => None,
    }
}

/// A leaked heap block of exactly `len` bytes, aligned to 8, holding `bytes`.
fn leak(bytes: &[u8], len: usize, offset: usize) -> *const u8 {
    let layout = std::alloc::Layout::from_size_align(len + offset, 8).unwrap();
    // SAFETY: non-zero size (a header at least).
    let block = unsafe { std::alloc::alloc_zeroed(layout) };
    assert!(!block.is_null());
    // SAFETY: `block` holds `len + offset` bytes.
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), block.add(offset), bytes.len().min(len))
    };
    // SAFETY: inside the block.
    let table = unsafe { block.add(offset) };
    LAST_COPY.set(table as usize);
    table
}

/// `KilnApi.service` for these fakes (min_version is the adapter's own check here,
/// so an older table is still returned).
pub unsafe extern "C" fn lookup(text: KilnStr, _min_version: u32) -> *const c_void {
    // SAFETY: the caller's name, readable for `len` bytes.
    let name = unsafe { std::slice::from_raw_parts(text.ptr.cast::<u8>(), text.len) };
    let name = String::from_utf8_lossy(name).into_owned();
    let Some((table, size)) = table_of(&name) else {
        return std::ptr::null();
    };
    let shape = SHAPES.with_borrow(|shapes| shapes.get(name.as_str()).copied());
    // SAFETY: a static table of `size` bytes.
    let bytes = unsafe { std::slice::from_raw_parts(table, size) };
    let set_u32 = |table: *const u8, offset: usize, value: u32| {
        // SAFETY: inside the leaked copy, aligned header field.
        unsafe { table.cast_mut().add(offset).cast::<u32>().write(value) }
    };
    let served = match shape {
        None => table,
        Some(Shape::Missing) => std::ptr::null(),
        Some(Shape::Short(len)) => {
            let copy = leak(bytes, len as usize, 0);
            set_u32(copy, 0, len);
            copy
        }
        Some(Shape::Version(version)) => {
            let copy = leak(bytes, size, 0);
            set_u32(copy, 4, version);
            copy
        }
        Some(Shape::Misaligned) => leak(bytes, size, 4),
        Some(Shape::NullAt(offset)) => {
            let copy = leak(bytes, size, 0);
            // SAFETY: a pointer-sized slot inside the copy.
            unsafe { copy.cast_mut().add(offset).cast::<usize>().write(0) };
            copy
        }
        Some(Shape::Copy) => leak(bytes, size, 0),
    };
    served.cast()
}
/// The fake `KilnApi` (support's, with log, tasks pool, sessions and posts) whose
/// service lookup serves these fake tables; `lookup: false` leaves `service` NULL.
pub fn api(lookup_on: bool) -> Box<KilnApi> {
    let mut api = support::api(true);
    api.service =
        lookup_on.then_some(lookup as unsafe extern "C" fn(KilnStr, u32) -> *const c_void);
    api
}
