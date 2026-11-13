// SPDX-License-Identifier: MIT
// Kiln's own implementation of the three hashes EML documents for its `hasher` module (FNV-1a 32-bit,
// CRC-32/ISO-HDLC, CRC-64/ECMA-182), from the published algorithm definitions. Not derived from EML's code.
using System.Text;

namespace Kiln;

/// <summary>
/// Hashes game data uses and EML mods compute (EML <c>hasher.fnv1a32/crc32/crc64</c>). Strings hash as UTF-8.
/// Check values for "123456789": FNV-1a 32 = 0xBB86B11C, CRC-32 = 0xCBF43926, CRC-64/ECMA-182 = 0x6C40DF5F0B497347.
/// </summary>
[AnyThread]
public static class DataHash
{
    /// <summary>FNV-1a, 32 bit (offset basis 0x811C9DC5, prime 0x01000193).</summary>
    public static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        uint h = 0x811C9DC5;
        foreach (byte b in data) h = (h ^ b) * 0x01000193;
        return h;
    }

    public static uint Fnv1a32(string text) => Fnv1a32(Encoding.UTF8.GetBytes(text));

    private static readonly uint[] Crc32Table = BuildCrc32();
    private static readonly ulong[] Crc64Table = BuildCrc64();

    /// <summary>CRC-32/ISO-HDLC (zlib's): reflected polynomial 0xEDB88320, init and final XOR 0xFFFFFFFF.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public static uint Crc32(string text) => Crc32(Encoding.UTF8.GetBytes(text));

    /// <summary>CRC-64/ECMA-182: polynomial 0x42F0E1EBA9EA3693, not reflected, init 0, no final XOR.</summary>
    public static ulong Crc64(ReadOnlySpan<byte> data)
    {
        ulong c = 0;
        foreach (byte b in data) c = Crc64Table[((c >> 56) ^ b) & 0xFF] ^ (c << 8);
        return c;
    }

    public static ulong Crc64(string text) => Crc64(Encoding.UTF8.GetBytes(text));

    private static uint[] BuildCrc32()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static ulong[] BuildCrc64()
    {
        const ulong poly = 0x42F0E1EBA9EA3693;
        var t = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            ulong c = i << 56;
            for (int k = 0; k < 8; k++) c = (c & 0x8000000000000000) != 0 ? (c << 1) ^ poly : c << 1;
            t[i] = c;
        }
        return t;
    }
}
