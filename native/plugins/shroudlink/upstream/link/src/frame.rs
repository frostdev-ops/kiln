//! Binary frames inside a packet (little endian).

pub const PROTOCOL_VERSION: u16 = 1;

const HELLO: u8 = 1;
const WHO_AM_I: u8 = 2;
const CLAIM: u8 = 3;
const DATA: u8 = 4;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Frame {
    Hello { nonce: u64, version: u16 },
    WhoAmI { nonce: u64, slot: u8 },
    Claim { nonce: u64, slot: u8 },
    /// One chunk of message `msg` from/to the client session `nonce`.
    Data { nonce: u64, msg: u32, chunk: u16, count: u16, bytes: Vec<u8> },
}

impl Frame {
    pub fn encode(&self) -> Vec<u8> {
        let mut out = Vec::new();
        match self {
            Frame::Hello { nonce, version } => {
                out.push(HELLO);
                out.extend(nonce.to_le_bytes());
                out.extend(version.to_le_bytes());
            }
            Frame::WhoAmI { nonce, slot } => {
                out.push(WHO_AM_I);
                out.extend(nonce.to_le_bytes());
                out.push(*slot);
            }
            Frame::Claim { nonce, slot } => {
                out.push(CLAIM);
                out.extend(nonce.to_le_bytes());
                out.push(*slot);
            }
            Frame::Data { nonce, msg, chunk, count, bytes } => {
                out.push(DATA);
                out.extend(nonce.to_le_bytes());
                out.extend(msg.to_le_bytes());
                out.extend(chunk.to_le_bytes());
                out.extend(count.to_le_bytes());
                out.extend(bytes);
            }
        }
        out
    }

    pub fn decode(data: &[u8]) -> Option<Frame> {
        let (&kind, rest) = data.split_first()?;
        let mut r = Reader(rest);
        let frame = match kind {
            HELLO => Frame::Hello { nonce: r.u64()?, version: r.u16()? },
            WHO_AM_I => Frame::WhoAmI { nonce: r.u64()?, slot: r.u8()? },
            CLAIM => Frame::Claim { nonce: r.u64()?, slot: r.u8()? },
            DATA => Frame::Data { nonce: r.u64()?, msg: r.u32()?, chunk: r.u16()?, count: r.u16()?, bytes: r.0.to_vec() },
            _ => return None,
        };
        Some(frame)
    }
}

struct Reader<'a>(&'a [u8]);

impl Reader<'_> {
    fn take<const N: usize>(&mut self) -> Option<[u8; N]> {
        let (head, rest) = self.0.split_first_chunk::<N>()?;
        self.0 = rest;
        Some(*head)
    }
    fn u8(&mut self) -> Option<u8> {
        self.take::<1>().map(|b| b[0])
    }
    fn u16(&mut self) -> Option<u16> {
        self.take().map(u16::from_le_bytes)
    }
    fn u32(&mut self) -> Option<u32> {
        self.take().map(u32::from_le_bytes)
    }
    fn u64(&mut self) -> Option<u64> {
        self.take().map(u64::from_le_bytes)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip() {
        let frames = [
            Frame::Hello { nonce: 0x1122334455667788, version: PROTOCOL_VERSION },
            Frame::WhoAmI { nonce: 7, slot: 16 },
            Frame::Claim { nonce: 7, slot: 3 },
            Frame::Data { nonce: 9, msg: 42, chunk: 1, count: 3, bytes: b"{\"mod\":1}".to_vec() },
        ];
        for f in frames {
            assert_eq!(Frame::decode(&f.encode()), Some(f));
        }
        assert_eq!(Frame::decode(&[HELLO, 1, 2]), None);
        assert_eq!(Frame::decode(&[99]), None);
    }
}
