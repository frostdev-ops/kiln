//! Splitting messages into packet-sized `Data` frames and putting them back together.

use std::collections::HashMap;

use crate::codec::{encode_packet, MAGIC, MAX_PACKET_LEN};
use crate::frame::Frame;

/// Payload bytes per `Data` frame: 256 chars - magic, base64 (4 chars per 3 bytes), 17 byte header.
pub const CHUNK_PAYLOAD: usize = (MAX_PACKET_LEN - MAGIC.len()) / 4 * 3 - 17;
/// Upper bound for one message, protects against runaway allocations.
const MAX_CHUNKS: u16 = 2048;

/// Encodes `message` as packets for session `nonce`.
pub fn split_message(nonce: u64, msg: u32, message: &[u8]) -> Vec<String> {
    let chunks: Vec<&[u8]> = if message.is_empty() { vec![&[][..]] } else { message.chunks(CHUNK_PAYLOAD).collect() };
    let count = chunks.len() as u16;
    chunks
        .into_iter()
        .enumerate()
        .map(|(i, bytes)| encode_packet(&Frame::Data { nonce, msg, chunk: i as u16, count, bytes: bytes.to_vec() }.encode()))
        .collect()
}

/// Collects `Data` chunks per (session, message id). The transport is reliable
/// and ordered, but chunks of different messages may interleave.
#[derive(Default)]
pub struct Reassembler {
    pending: HashMap<(u64, u32), Vec<Option<Vec<u8>>>>,
}

impl Reassembler {
    /// Returns the complete message once its last missing chunk arrives.
    pub fn push(&mut self, nonce: u64, msg: u32, chunk: u16, count: u16, bytes: Vec<u8>) -> Option<Vec<u8>> {
        if count == 0 || count > MAX_CHUNKS || chunk >= count {
            return None;
        }
        if count == 1 {
            return Some(bytes);
        }
        let parts = self.pending.entry((nonce, msg)).or_insert_with(|| vec![None; count as usize]);
        if parts.len() != count as usize {
            *parts = vec![None; count as usize];
        }
        parts[chunk as usize] = Some(bytes);
        if parts.iter().all(Option::is_some) {
            let parts = self.pending.remove(&(nonce, msg))?;
            return Some(parts.into_iter().flatten().flatten().collect());
        }
        None
    }

    /// Drops half-received messages of a session (disconnect).
    pub fn forget(&mut self, nonce: u64) {
        self.pending.retain(|(n, _), _| *n != nonce);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec::decode_packet;

    #[test]
    fn split_and_join() {
        let message: Vec<u8> = (0..1000u32).map(|i| (i % 251) as u8).collect();
        let packets = split_message(5, 77, &message);
        assert!(packets.len() > 1);
        assert!(packets.iter().all(|p| p.len() <= MAX_PACKET_LEN));
        let mut r = Reassembler::default();
        let mut out = None;
        // deliver in reverse to prove order independence
        for p in packets.iter().rev() {
            let Some(Frame::Data { nonce, msg, chunk, count, bytes }) = Frame::decode(&decode_packet(p.as_bytes()).unwrap()) else {
                panic!("not a data frame")
            };
            out = r.push(nonce, msg, chunk, count, bytes).or(out);
        }
        assert_eq!(out.unwrap(), message);
        assert!(r.pending.is_empty());
    }

    #[test]
    fn single_and_empty() {
        let mut r = Reassembler::default();
        for message in [&b""[..], b"{}"] {
            let p = split_message(1, 1, message);
            assert_eq!(p.len(), 1);
            let Some(Frame::Data { nonce, msg, chunk, count, bytes }) = Frame::decode(&decode_packet(p[0].as_bytes()).unwrap()) else {
                panic!()
            };
            assert_eq!(r.push(nonce, msg, chunk, count, bytes).unwrap(), message);
        }
    }
}
