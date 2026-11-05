# SPDX-License-Identifier: MIT
"""keen::ContentHash: the game's own hash of a content blob (clean-room, from disassembly; docs/re/vfx.md §6).

    ContentHash = {u32 size, hash0, hash1, hash2}, where hash0..2 are bytes 4..16 of a 128-bit AES-round hash.

The game checks this hash for shader code at run time. Its pipeline streamer recomputes it for every pipeline whose
code range covers a whole blob, and a mismatch ends in a fatal "error streaming vfx pipeline" (client 0xab1350,
0x1af940). So a blob holding SPIR-V must carry the real hash; kiln.assets' sha256-based rule (docs/re/resources.md §3)
is only safe for content the game does not verify. Other blobs are not re-hashed, so they may keep the sha256 rule.

The hash, as in client 0xf99800 (AES-NI) and 0xf999c0 (the software fallback, same result):
- four 16-byte lanes start as the bytes 00..0f, 10..1f, 20..2f, 30..3f;
- every whole 64 bytes: a=aesdec(a,m0) b=aesdec(b,m1) c=aesdec(c,m2) d=aesdec(d,m3);
- the tail (len & 15 bytes, zero padded) goes into d, then the remaining whole 16-byte blocks into c, b, a;
- finish with K = {u64 seed-len, u64 len+1+seed}: a,b,c,d each take K, then c takes d, a takes b, c takes K, a takes
  c, a takes K. The result is a. seed is 0 for content.

aesdec is the x86 instruction: InvShiftRows, InvSubBytes, InvMixColumns, then xor with the key. Pure Python: about
1 MB/s, which is plenty for shader modules.
"""
import struct

_SBOX = [0] * 256


def _build_tables():
    # AES S-box from its definition (inverse in GF(2^8) + affine map)
    p = q = 1
    while True:
        # p *= 3
        p = (p ^ ((p << 1) & 0xff) ^ (0x1b if p & 0x80 else 0)) & 0xff
        # q /= 3
        q ^= (q << 1) & 0xff
        q ^= (q << 2) & 0xff
        q ^= (q << 4) & 0xff
        if q & 0x80:
            q ^= 0x09
        x = q ^ ((q << 1 | q >> 7) & 0xff) ^ ((q << 2 | q >> 6) & 0xff) ^ ((q << 3 | q >> 5) & 0xff) ^ (
            (q << 4 | q >> 4) & 0xff)
        _SBOX[p] = x ^ 0x63
        if p == 1:
            break
    _SBOX[0] = 0x63
    inv = [0] * 256
    for i, s in enumerate(_SBOX):
        inv[s] = i

    def mul(a, b):
        r = 0
        while b:
            if b & 1:
                r ^= a
            a = ((a << 1) & 0xff) ^ (0x1b if a & 0x80 else 0)
            b >>= 1
        return r

    t0 = [0] * 256
    for i in range(256):
        s = inv[i]
        # column (0e 09 0d 0b) x s, little-endian word
        t0[i] = mul(s, 0x0e) | mul(s, 0x09) << 8 | mul(s, 0x0d) << 16 | mul(s, 0x0b) << 24
    t1 = [((v << 8) | (v >> 24)) & 0xffffffff for v in t0]
    t2 = [((v << 16) | (v >> 16)) & 0xffffffff for v in t0]
    t3 = [((v << 24) | (v >> 8)) & 0xffffffff for v in t0]
    return t0, t1, t2, t3


_T0, _T1, _T2, _T3 = _build_tables()


def _aesdec(s, k):
    """One aesdec round on 4 little-endian u32 words (state s, key k)."""
    s0, s1, s2, s3 = s
    return (
        _T0[s0 & 0xff] ^ _T1[(s3 >> 8) & 0xff] ^ _T2[(s2 >> 16) & 0xff] ^ _T3[s1 >> 24] ^ k[0],
        _T0[s1 & 0xff] ^ _T1[(s0 >> 8) & 0xff] ^ _T2[(s3 >> 16) & 0xff] ^ _T3[s2 >> 24] ^ k[1],
        _T0[s2 & 0xff] ^ _T1[(s1 >> 8) & 0xff] ^ _T2[(s0 >> 16) & 0xff] ^ _T3[s3 >> 24] ^ k[2],
        _T0[s3 & 0xff] ^ _T1[(s2 >> 8) & 0xff] ^ _T2[(s1 >> 16) & 0xff] ^ _T3[s0 >> 24] ^ k[3],
    )


def _lane(first):
    return struct.unpack("<4I", bytes(range(first, first + 16)))


def keen_hash128(data, seed=0):
    """The 128-bit hash as 16 bytes."""
    n = len(data)
    a, b, c, d = _lane(0), _lane(16), _lane(32), _lane(48)
    whole = n >> 6
    if whole:
        words = struct.unpack_from(f"<{whole * 16}I", data, 0)
        for i in range(0, whole * 16, 16):
            a = _aesdec(a, words[i:i + 4])
            b = _aesdec(b, words[i + 4:i + 8])
            c = _aesdec(c, words[i + 8:i + 12])
            d = _aesdec(d, words[i + 12:i + 16])
    o = whole * 64
    rem = n & 0x30
    tail = n & 0xf
    if tail:
        block = bytes(data[o + rem:o + rem + tail]) + b"\0" * (16 - tail)
        d = _aesdec(d, struct.unpack("<4I", block))
    if rem == 0x30:
        c = _aesdec(c, struct.unpack_from("<4I", data, o + 0x20))
    if rem >= 0x20:
        b = _aesdec(b, struct.unpack_from("<4I", data, o + 0x10))
    if rem >= 0x10:
        a = _aesdec(a, struct.unpack_from("<4I", data, o))
    k = struct.unpack("<4I", struct.pack("<QQ", (seed - n) & (2**64 - 1), (n + 1 + seed) & (2**64 - 1)))
    a = _aesdec(a, k)
    b = _aesdec(b, k)
    c = _aesdec(c, k)
    d = _aesdec(d, k)
    c = _aesdec(c, d)
    a = _aesdec(a, b)
    c = _aesdec(c, k)
    a = _aesdec(a, c)
    a = _aesdec(a, k)
    return struct.pack("<4I", *a)


def content_hash(data):
    """(size, hash0, hash1, hash2) as the game's ContentHash stores them."""
    h = struct.unpack("<4I", keen_hash128(data))
    return (len(data), h[1], h[2], h[3])
