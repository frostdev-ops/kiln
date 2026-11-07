// SPDX-License-Identifier: MIT
// kiln: parse only a file-backed executable .text section at the live section's RVA. Never scan raw padding or a
// different section, and reject truncated/invalid PE headers before interpreting any section-table offset.
pub(crate) fn file_text(file: &[u8], rva: usize, live_len: usize) -> Option<&[u8]> {
    let u16_at = |o: usize| {
        Some(u16::from_le_bytes(
            file.get(o..o.checked_add(2)?)?.try_into().ok()?,
        ))
    };
    let u32_at = |o: usize| {
        Some(u32::from_le_bytes(
            file.get(o..o.checked_add(4)?)?.try_into().ok()?,
        ))
    };
    if file.get(..2)? != b"MZ" {
        return None;
    }
    let pe = u32_at(0x3c)? as usize;
    if u32_at(pe)? != 0x4550 || u16_at(pe + 4)? != 0x8664 {
        return None;
    }
    let count = u16_at(pe + 6)? as usize;
    let opt = u16_at(pe + 20)? as usize;
    if count == 0 || count > 96 || opt < 112 || u16_at(pe + 24)? != 0x20b {
        return None;
    }
    let first = pe.checked_add(24)?.checked_add(opt)?;
    file.get(first..first.checked_add(count.checked_mul(40)?)?)?;
    for i in 0..count {
        let s = first + i * 40;
        if file.get(s..s + 8)? != b".text\0\0\0" || u32_at(s + 12)? as usize != rva {
            continue;
        }
        if u32_at(s + 36)? & 0x20000000 == 0 {
            return None;
        }
        let raw = u32_at(s + 20)? as usize;
        let raw_len = u32_at(s + 16)? as usize;
        file.get(raw..raw.checked_add(raw_len)?)?;
        let len = raw_len.min(u32_at(s + 8)? as usize).min(live_len);
        return file.get(raw..raw.checked_add(len)?);
    }
    None
}

#[cfg(test)]
mod kiln_tests {
    use super::*;
    fn image() -> Vec<u8> {
        let mut f = vec![0; 0x300];
        f[..2].copy_from_slice(b"MZ");
        f[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());
        f[0x80..0x84].copy_from_slice(&0x4550u32.to_le_bytes());
        f[0x84..0x86].copy_from_slice(&0x8664u16.to_le_bytes());
        f[0x86..0x88].copy_from_slice(&1u16.to_le_bytes());
        f[0x94..0x96].copy_from_slice(&112u16.to_le_bytes());
        f[0x98..0x9a].copy_from_slice(&0x20bu16.to_le_bytes());
        let s = 0x108;
        f[s..s + 8].copy_from_slice(b".text\0\0\0");
        for (off, n) in [
            (8, 8u32),
            (12, 0x1000),
            (16, 0x100),
            (20, 0x200),
            (36, 0x20000000),
        ] {
            f[s + off..s + off + 4].copy_from_slice(&n.to_le_bytes());
        }
        f[0x200..0x208].copy_from_slice(&[0x90, 0x90, 0xc3, 0, 0, 0, 0, 0]);
        f
    }
    #[test]
    fn kiln_file_scan_bounds_and_section_identity() {
        let f = image();
        assert_eq!(file_text(&f, 0x1000, 100).unwrap().len(), 8);
        assert!(file_text(&f, 0x2000, 100).is_none());
        for n in 0..f.len() {
            if n < f.len() {
                assert!(file_text(&f[..n], 0x1000, 100).is_none());
            }
        }
        let mut bad = f.clone();
        bad[0] = 0;
        assert!(file_text(&bad, 0x1000, 100).is_none());
        bad = f.clone();
        bad[0x108] = b'x';
        assert!(file_text(&bad, 0x1000, 100).is_none());
        bad = f.clone();
        bad[0x108 + 36..0x108 + 40].fill(0);
        assert!(file_text(&bad, 0x1000, 100).is_none());
        bad = f;
        bad[0x108 + 20..0x108 + 24].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(file_text(&bad, 0x1000, 100).is_none());
    }
}
