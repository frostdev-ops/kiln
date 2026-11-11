// SPDX-License-Identifier: MIT
// Private addon glue. Every entry/message/session comes from the stock receive
// path; outgoing events run only on the already hooked client update thread.
use std::sync::{Mutex, OnceLock};
use std::time::Instant;
#[link(name = "bcrypt")]
extern "system" {
    fn BCryptGenRandom(
        algorithm: *mut std::ffi::c_void,
        buffer: *mut u8,
        len: u32,
        flags: u32,
    ) -> i32;
}
use crate::bridge_core::{Bridge, Error, Ticket, MAX_REPLY, MAX_REQUEST};
use serde_json::Value;
use shroudlink::ui::ToClient;
static CLOCK: OnceLock<Instant> = OnceLock::new();
static CORE: Mutex<Option<Bridge>> = Mutex::new(None);
fn now() -> u64 {
    CLOCK
        .get_or_init(Instant::now)
        .elapsed()
        .as_millis()
        .min(u64::MAX as u128) as u64
}
fn with<R>(f: impl FnOnce(&mut Bridge) -> R) -> Option<R> {
    CORE.lock().ok()?.as_mut().map(f)
}
pub fn start() {
    let mut epoch = [0u8; 16];
    if unsafe { BCryptGenRandom(std::ptr::null_mut(), epoch.as_mut_ptr(), 16, 2) } != 0 {
        return;
    }
    *CORE.lock().unwrap() = Some(Bridge::new(
        epoch.iter().map(|b| format!("{b:02x}")).collect(),
    ));
}
pub fn stop() {
    with(|b| b.stop());
}
pub fn session() {
    with(|b| b.begin_session());
}
pub fn welcome(protocol: u64) {
    with(|b| b.welcome(protocol));
}
pub fn observe(op: &ToClient) {
    with(|b| b.observe(op));
}
pub fn world(present: bool) {
    with(|b| b.world(present));
}
pub fn presenting() -> bool {
    with(|b| b.presenting(now())).unwrap_or(false)
}
pub fn tick(client: usize, packet_budget: usize) -> Option<(Ticket, Value)> {
    with(|b| {
        b.tick(now(), client);
        b.take(now(), packet_budget * shroudlink::CHUNK_PAYLOAD)
    })?
}
pub fn valid(ticket: &Ticket) -> bool {
    with(|b| b.valid_ticket(ticket, now())).unwrap_or(false)
}
pub fn committed(ticket: &Ticket) {
    if let Some(Some((owner, id))) = with(|b| b.committed(ticket, now())) {
        crate::ui::bridge_closed(&owner, &id);
    }
}
pub fn closed(owner: &str, id: &str) {
    observe(&ToClient::Close {
        owner: owner.into(),
        id: id.into(),
    });
}

// v1: C calling convention, UTF-8 JSON, lengths exclude NUL, no borrowed pointers
// or cross-heap frees. Never call from DllMain. Status codes are bridge_core::Error.
// Valid caller-owned pointers are a standard C ABI precondition; length/null
// guards prevent accidental probes from reading arbitrary caller lengths.
#[no_mangle]
pub unsafe extern "C" fn shroudlink_client_bridge_v1(
    request: *const u8,
    len: usize,
    out: *mut u8,
    capacity: usize,
    written: *mut usize,
) -> u32 {
    if written.is_null() {
        return Error::Invalid as u32;
    }
    *written = 0;
    if request.is_null()
        || len == 0
        || len > MAX_REQUEST
        || capacity > MAX_REPLY
        || (out.is_null() && capacity != 0)
    {
        return Error::Invalid as u32;
    }
    let Ok(mut guard) = CORE.try_lock() else {
        return Error::Busy as u32;
    };
    let Some(b) = guard.as_mut() else {
        return Error::Inactive as u32;
    };
    match b.rpc(std::slice::from_raw_parts(request, len), capacity, now()) {
        Ok(bytes) => {
            *written = bytes.len();
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), out, bytes.len());
            0
        }
        Err((e, n)) => {
            *written = n;
            e as u32
        }
    }
}
