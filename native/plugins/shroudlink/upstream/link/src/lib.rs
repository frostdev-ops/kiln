//! shroudlink: a mod channel that rides on Enshrouded's own netcode.
//!
//! The game replicates custom player strings (chest names etc.) in both
//! directions: clients send `CustomPlayerStringClientMessage` (rename), the
//! server sends `CustomPlayerStringServerMessage` (string table updates) per
//! player. shroudlink packets are such strings with a magic prefix; the host
//! (server DLL) and the addon (client DLL) take them out before the game sees
//! them, so they never end up in name tables or savegames.
//!
//! A packet is `~sl1~` + base64url(frame) and fits the game's 256 byte limit.
//! Frames:
//! - `Hello { nonce }`         client -> server, announces the addon
//! - `WhoAmI { nonce, slot }`  server -> every connected slot; only the client
//!   whose nonce matches learns its slot (the server cannot tell which peer sent
//!   a rename, but it does know which peer it sends to)
//! - `Claim { nonce, slot }`   client -> server, binds nonce to slot
//! - `Data { nonce, msg, chunk, count, bytes }` a chunk of a JSON message
//!
//! Messages are JSON objects addressed to a mod: `{"mod": "chestsort", ...}`.

mod codec;
mod frame;
mod reassembly;
pub mod ui;

pub use codec::{decode_packet, encode_packet, is_packet, MAGIC, MAX_PACKET_LEN};
pub use frame::{Frame, PROTOCOL_VERSION};
pub use reassembly::{split_message, Reassembler, CHUNK_PAYLOAD};

/// FNV-1a 64, the game's custom string id (the hash of the text).
pub fn string_id(text: &[u8]) -> u64 {
    let mut h: u64 = 0xcbf29ce484222325;
    for &b in text {
        h = (h ^ b as u64).wrapping_mul(0x100000001b3);
    }
    h
}
