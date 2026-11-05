// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;

namespace Kiln;

/// <summary>Ids of what a block becomes. They follow from the mod id and the block's name only, so they are the same on every
/// machine and after every restart, and saves keep them. Same rule as <c>NativeIds</c> (quests): UUIDv5 of
/// <c>&lt;mod&gt;:&lt;kind&gt;:&lt;name&gt;</c>; a game id is FNV-1a over the GUID's 16 bytes.</summary>
public static class BlockIds
{
    private static readonly Guid Namespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");

    /// <summary>The derived GUID (game byte order = <see cref="Guid.ToByteArray()"/>).</summary>
    public static Guid Derive(string mod, string kind, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var text = Encoding.UTF8.GetBytes($"{mod}:{kind}:{name}");
        var all = new byte[16 + text.Length];
        ns.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>FNV-1a-32 over a GUID's bytes: the game's id of an item, recipe or buff with that GUID.</summary>
    public static uint Fnv(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        return Fnv(b, 0x811c9dc5u);
    }

    internal static uint Fnv(ReadOnlySpan<byte> bytes, uint h = 0x811c9dc5u)
    {
        foreach (var x in bytes) h = (h ^ x) * 0x01000193u;
        return h;
    }

    internal static uint Fnv(string text) => Fnv(Encoding.UTF8.GetBytes(text));

    /// <summary>16 bytes whose FNV-1a is <paramref name="id"/>: a reference to something the game's data holds only by id. Each byte step of FNV-1a can be
    /// undone, so the last four bytes are found by meeting in the middle.</summary>
    internal static Guid WithFnv(uint id)
    {
        const uint Prime = 0x01000193, Inverse = 0x359c449b;
        for (byte salt = 0; salt < 255; salt++)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 12; i++) bytes[i] = (byte)(0x4b + i * 7 + salt);
            var a = 0x811c9dc5u;
            for (var i = 0; i < 12; i++) a = (a ^ bytes[i]) * Prime;
            var forward = new Dictionary<uint, ushort>(65536);
            for (var x = 0; x < 65536; x++)
                forward[(((a ^ (uint)(x & 0xff)) * Prime) ^ (uint)(x >> 8)) * Prime] = (ushort)x;
            for (var y = 0; y < 65536; y++)
            {
                var back = ((id * Inverse) ^ (uint)(y >> 8)) * Inverse ^ (uint)(y & 0xff);
                if (!forward.TryGetValue(back, out var x)) continue;
                bytes[12] = (byte)x; bytes[13] = (byte)(x >> 8); bytes[14] = (byte)y; bytes[15] = (byte)(y >> 8);
                var guid = new Guid(bytes);
                if (Fnv(guid) == id) return guid;
            }
        }
        throw new InvalidOperationException($"no 16 bytes with the hash 0x{id:x8}");
    }
}
