// SPDX-License-Identifier: MIT
// A sequence event that consumes the used item, played with no used item, takes the server down: the game's spawn path reads
// ItemRegistryFind(0)+0x42c without a check (0x42586). A player's own use always has an item, and every potion and food sequence has such an
// event, so Abilities.PlaySequence, which has no item to give, refuses them. Reads the live data, so a layer that turned consume off counts.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class SequenceGuard
{
    private static readonly object Gate = new();
    private static ulong _generation = ulong.MaxValue;
    private static Dictionary<Guid, List<GameDataEntry>>? _subs;

    /// <summary>The first event of a sequence, in any of its sub-sequences, that consumes the used item, as "sub-sequence: type"; or null.</summary>
    public static string? ConsumingEvent(Guid sequence)
    {
        if (!Content.GameDataAvailable) return null;
        var status = Content.Status();
        if (status is not { Ready: true }) return null;
        lock (Gate)
        {
            if (_generation != status.Generation || _subs == null)
            {
                _generation = status.Generation;
                _subs = new Dictionary<Guid, List<GameDataEntry>>();
                var all = Content.Sequences;
                for (var i = 0; i < all.Count; i++)
                {
                    var e = all[i];
                    if (!_subs.TryGetValue(e.Guid, out var list)) _subs[e.Guid] = list = new List<GameDataEntry>();
                    list.Add(e);
                }
            }
            if (!_subs.TryGetValue(sequence, out var subs)) return null;
            foreach (var sub in subs)
            {
                if (sub.GetJson("events")?["value"] is not JsonArray events) continue;
                foreach (var ev in events)
                    if (ev?["consume"] is JsonValue v && v.TryGetValue<bool>(out var consume) && consume && (string?)ev["$type"] is { } type
                        // the cost payer checks for no item (0x40d00), and staff attacks have it on
                        && !type.EndsWith("PayUsageCost", StringComparison.Ordinal))
                        return $"{sub.Name}: {type}";
            }
            return null;
        }
    }
}
