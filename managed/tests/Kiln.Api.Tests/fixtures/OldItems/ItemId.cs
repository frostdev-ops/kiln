// SPDX-License-Identifier: MIT
// The old layout: Kiln.ItemId as Kiln.Items declared it up to dev.24 (test fixture only).
namespace Kiln;

/// <summary>An item type: FNV-1a-32 of the item's 16 GUID bytes, as the game computes it (and the dashboard catalog
/// uses). 0 = none.</summary>
public readonly record struct ItemId(uint Value)
{
    public static readonly ItemId None = default;
    public bool IsNone => Value == 0;
    public override string ToString() => $"0x{Value:x8}";

    /// <summary>The id the game gives the item with these GUID bytes (as stored in the game's data).</summary>
    public static ItemId FromGuidBytes(ReadOnlySpan<byte> guid)
    {
        if (guid.Length != 16) throw new ArgumentException("a GUID has 16 bytes", nameof(guid));
        uint h = 0x811c9dc5;
        var zero = true;
        foreach (var b in guid)
        {
            zero &= b == 0;
            h = (h ^ b) * 0x01000193;
        }
        return new ItemId(zero ? 0 : h);
    }
}
