//! Packet text encoding: `~sl1~` + unpadded base64url, so packets stay plain
//! ASCII whatever the game does with string contents.

pub const MAGIC: &str = "~sl1~";
/// Longest string the game transports (`CustomPlayerStringServerMessage` copies at most 0x100 bytes).
pub const MAX_PACKET_LEN: usize = 256;

const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

pub fn is_packet(text: &[u8]) -> bool {
    text.starts_with(MAGIC.as_bytes())
}

pub fn encode_packet(frame: &[u8]) -> String {
    let mut out = String::with_capacity(MAGIC.len() + frame.len() * 4 / 3 + 3);
    out.push_str(MAGIC);
    for chunk in frame.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (b[0] as u32) << 16 | (b[1] as u32) << 8 | b[2] as u32;
        let chars = chunk.len() + 1;
        for i in 0..chars {
            out.push(ALPHABET[(n >> (18 - 6 * i) & 63) as usize] as char);
        }
    }
    out
}

pub fn decode_packet(text: &[u8]) -> Option<Vec<u8>> {
    let body = text.strip_prefix(MAGIC.as_bytes())?;
    if body.len() % 4 == 1 {
        return None;
    }
    let mut out = Vec::with_capacity(body.len() * 3 / 4);
    for chunk in body.chunks(4) {
        let mut n = 0u32;
        for (i, &c) in chunk.iter().enumerate() {
            let v = ALPHABET.iter().position(|&a| a == c)? as u32;
            n |= v << (18 - 6 * i);
        }
        for i in 0..chunk.len() - 1 {
            out.push((n >> (16 - 8 * i)) as u8);
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_all_lengths() {
        for len in 0..200 {
            let data: Vec<u8> = (0..len).map(|i| (i * 37 + 11) as u8).collect();
            let text = encode_packet(&data);
            assert!(text.is_ascii() && is_packet(text.as_bytes()));
            assert_eq!(decode_packet(text.as_bytes()).unwrap(), data);
        }
    }

    #[test]
    fn rejects_garbage() {
        assert!(decode_packet(b"chest name").is_none());
        assert!(decode_packet(b"~sl1~a").is_none());
        assert!(decode_packet(b"~sl1~ab!d").is_none());
    }
}
