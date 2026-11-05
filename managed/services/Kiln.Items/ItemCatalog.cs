// SPDX-License-Identifier: MIT
using System.Globalization;

namespace Kiln;

/// <summary>The game's item registry (through kiln.items) plus display names from the shipped item catalog
/// (Data/items.tsv, generated from the Enshrouded Save Editor's data/items.json, MIT).</summary>
internal static unsafe class ItemCatalog
{
    private sealed record Shipped(uint Id, string Debug, string Name, string Category);

    private static readonly object Gate = new();
    private static IReadOnlyList<ItemInfo> _all = Array.Empty<ItemInfo>();
    private static Dictionary<uint, ItemInfo> _byId = new();
    private static Dictionary<string, ItemInfo> _byName = new(StringComparer.Ordinal);
    private static Dictionary<string, ItemInfo> _byNameCi = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, List<ItemInfo>> _byDisplay = new(StringComparer.OrdinalIgnoreCase);
    private static uint _loadedCount;
    private static Dictionary<uint, Shipped>? _shipped;

    private static Dictionary<uint, Shipped> ShippedNames()
    {
        if (_shipped != null) return _shipped;
        var d = new Dictionary<uint, Shipped>();
        using var s = typeof(ItemCatalog).Assembly.GetManifestResourceStream("Kiln.Items.items.tsv");
        if (s != null)
        {
            using var r = new StreamReader(s);
            while (r.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f.Length < 3 || !uint.TryParse(f[0], out var id)) continue;
                d[id] = new Shipped(id, f[1], f[2], f.Length > 3 ? f[3] : "");
            }
        }
        return _shipped = d;
    }

    public static IReadOnlyList<ItemInfo> Load(ItemsNative.Table* t)
    {
        if (t == null) return _all;
        var count = t->ItemCount();
        if (count == 0 || count == _loadedCount) return _all;
        lock (Gate)
        {
            if (count == _loadedCount) return _all;
            var shipped = ShippedNames();
            var list = new List<ItemInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                ItemsNative.ItemInfo n = default;
                n.Size = (uint)sizeof(ItemsNative.ItemInfo);
                if (t->ItemAt(i, &n) != ItemsNative.Ok) continue;
                list.Add(Convert(in n, shipped));
            }
            var byId = new Dictionary<uint, ItemInfo>(list.Count);
            var byName = new Dictionary<string, ItemInfo>(StringComparer.Ordinal);
            var byNameCi = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
            var byDisplay = new Dictionary<string, List<ItemInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in list)
            {
                byId[it.Id.Value] = it;
                if (it.Name.Length > 0)
                {
                    byName.TryAdd(it.Name, it);
                    byNameCi.TryAdd(it.Name, it);
                }
                if (it.DisplayName is { Length: > 0 } dn)
                {
                    if (!byDisplay.TryGetValue(dn, out var l)) byDisplay[dn] = l = new List<ItemInfo>();
                    l.Add(it);
                }
            }
            foreach (var l in byDisplay.Values)  // a non-blueprint first, then the lowest id
                l.Sort((a, b) => a.IsBlueprint != b.IsBlueprint ? a.IsBlueprint.CompareTo(b.IsBlueprint) : a.Id.Value.CompareTo(b.Id.Value));
            _byId = byId;
            _byName = byName;
            _byNameCi = byNameCi;
            _byDisplay = byDisplay;
            _all = list;
            _loadedCount = count;
            return _all;
        }
    }

    private static ItemInfo Convert(in ItemsNative.ItemInfo n, Dictionary<uint, Shipped> shipped)
    {
        var name = n.Name.ToString();
        string? display = shipped.TryGetValue(n.Id, out var s) && s.Name.Length > 0 ? s.Name : null;
        if (name.Length == 0 && s != null) name = s.Debug;
        Guid guid;
        fixed (byte* g = n.Guid) guid = new Guid(new ReadOnlySpan<byte>(g, 16));
        return new ItemInfo(new ItemId(n.Id), name, display, n.MaxStack, (ItemCategory)n.Category,
            n.Rarity <= 4 ? (ItemRarity)n.Rarity : ItemRarity.Common, n.Gear != 0, (int)n.LevelMin, (int)n.LevelMax,
            n.EquipSlot, n.ItemFlags, guid);
    }

    public static ItemInfo? Get(ItemsNative.Table* t, ItemId id)
    {
        Load(t);
        return _byId.GetValueOrDefault(id.Value);
    }

    public static ItemInfo? Lookup(ItemsNative.Table* t, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Load(t);
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return _byId.GetValueOrDefault(hex);
        if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) && _byId.TryGetValue(dec, out var byDec))
            return byDec;
        if (_byName.TryGetValue(s, out var exact)) return exact;
        if (_byDisplay.TryGetValue(s, out var shown) && shown.Count > 0)
        {
            var same = shown.FirstOrDefault(i => string.Equals(i.DisplayName, s, StringComparison.Ordinal));
            return same ?? shown[0];
        }
        return _byNameCi.GetValueOrDefault(s);
    }

    public static IReadOnlyList<ItemInfo> Search(ItemsNative.Table* t, string text, int max)
    {
        var all = Load(t);
        if (string.IsNullOrWhiteSpace(text) || max <= 0) return Array.Empty<ItemInfo>();
        var s = text.Trim();
        return all.Where(i => i.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                              (i.DisplayName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(i => i.DisplayName is { } d && d.Equals(s, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Label.Length)
            .Take(max).ToList();
    }
}
