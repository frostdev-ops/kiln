// SPDX-License-Identifier: MIT
// The hash the game (and EML's `game.guid.hash`) makes of a GUID: FNV-1a 32 over its 16 bytes in the game's order.
// Kiln's own implementation from the published algorithm and Kiln's reverse-engineering notes (docs/re/items.md:
// `GuidToItemId`), not derived from EML's or ShroudForge's code.

namespace Kiln;

/// <summary>
/// The hash of a GUID. Item ids, buff type ids and many other ids of the game are the FNV-1a-32 of the GUID's bytes in the
/// game's byte order (<see cref="Guid.ToByteArray()"/>'s order, the bytes <c>ItemInfo.objectId</c> holds). EML's
/// <c>game.guid.hash</c> is <see cref="Of(Guid)"/>.
/// </summary>
/// <example>
/// <code>
/// var id = GuidHash.IdOf(Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95")); // 0x43108313, the Bronze Sword's itemId
/// </code>
/// </example>
public static class GuidHash
{
    /// <summary>FNV-1a-32 of the 16 bytes (EML <c>game.guid.hash</c>). The all-zero GUID gives 0x69691905.</summary>
    public static uint Of(ReadOnlySpan<byte> guidBytes)
    {
        if (guidBytes.Length != 16) throw new ArgumentException("a GUID is 16 bytes", nameof(guidBytes));
        return DataHash.Fnv1a32(guidBytes);
    }

    /// <summary>FNV-1a-32 of the GUID's bytes in the game's order.</summary>
    public static uint Of(Guid guid)
    {
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b, bigEndian: false, out _);
        return DataHash.Fnv1a32(b);
    }

    /// <summary>The id the game derives from a GUID (item id, buff type id, node id): 0 for the zero GUID (it means
    /// "none"), otherwise <see cref="Of(Guid)"/>.</summary>
    public static uint IdOf(Guid guid) => guid == Guid.Empty ? 0 : Of(guid);

    /// <summary>The id the game derives from 16 GUID bytes in its order; 0 for all zeros.</summary>
    public static uint IdOf(ReadOnlySpan<byte> guidBytes) => guidBytes.IndexOfAnyExcept((byte)0) < 0 && guidBytes.Length == 16 ? 0 : Of(guidBytes);

    /// <summary>The GUID the game's 16 bytes mean (its order is <see cref="Guid(ReadOnlySpan{byte})"/>'s).</summary>
    public static Guid FromBytes(ReadOnlySpan<byte> guidBytes) =>
        guidBytes.Length == 16 ? new Guid(guidBytes) : throw new ArgumentException("a GUID is 16 bytes", nameof(guidBytes));

    /// <summary>The 16 bytes of a GUID in the game's order.</summary>
    public static byte[] ToBytes(Guid guid) => guid.ToByteArray();
}
