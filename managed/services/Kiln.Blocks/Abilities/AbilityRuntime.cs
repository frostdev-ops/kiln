// SPDX-License-Identifier: MIT
// What ability blocks need while the game runs: abilities registered once however often a block is compiled, what a program asks of the server through kiln.Signal (an effect that casts, a
// cast that takes more than the game's one charge), and which talent an ability hangs on in the game's own tree.
namespace Kiln;

internal static class AbilityRuntime
{
    private sealed record Cast(string Ability, string? Item, int Took, int More);

    private const uint CastBit = 0xB10F0000, TakeBit = 0xB10E8000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AbilityHandle> Handles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, Cast> Casts = new();
    private static readonly Dictionary<uint, (string Item, int Count)> Takes = new();
    private static readonly Dictionary<string, (uint Node, string Talent)> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static IDisposable? _signals, _gates;

    /// <summary>Registers an ability, in place of what the same block made before: a data file's block has no mod to take it away at a reload.</summary>
    public static AbilityHandle Register(AbilityDefinition d)
    {
        lock (Gate)
        {
            if (Handles.Remove(d.Id, out var old)) old.Dispose();
            return Handles[d.Id] = Abilities.Register(d);
        }
    }

    internal static AbilityHandle? Find(string id)
    {
        lock (Gate) return Handles.GetValueOrDefault(id);
    }

    /// <summary>UI-generated abilities only: an old UI lifetime must not remove a newer registration.</summary>
    internal static void Remove(string id, AbilityHandle expected)
    {
        lock (Gate)
        {
            if (!Handles.TryGetValue(id, out var current) || !ReferenceEquals(current, expected)) return;
            Handles.Remove(id);
            if (ReferenceEquals(AbilityRegistry.Get(id), expected.Definition)) expected.Dispose();
        }
    }

    /// <summary>The id an ability is registered under.</summary>
    public static string IdOf(BlockContext ctx, AbilityRef r, string path)
    {
        if (string.IsNullOrWhiteSpace(r.Value)) throw new BlockError(ctx.Block, path, "this needs an ability: a block's name, or \"other-mod:name\"");
        var handle = ctx.Find(r.Value, path);
        if (handle is BlockHandle<AbilityBlock> ability) return ability.GameName;
        if (handle != null) throw new BlockError(ctx.Block, path, $"\"{r.Value}\" is a {handle.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not an ability");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<AbilityBlock>).Select(x => x.Key).Order();
        throw new BlockError(ctx.Block, path, $"no ability \"{r.Value}\" in this mod; define it first, or say \"other-mod:{r.Value}\"; there are: {string.Join(", ", own)}");
    }

    /// <summary>The tag a program signals, with the entity that casts as its value.</summary>
    public static uint CastTag(BlockContext ctx, AbilityRef r, (string Item, int Took, int More)? from, string path)
    {
        var id = IdOf(ctx, r, path);
        var cast = new Cast(id, from?.Item, from?.Took ?? 0, from?.More ?? 0);
        var low = BlockIds.Fnv($"{id}|{cast.Item}|{cast.Took}|{cast.More}") & 0x7FFF;
        lock (Gate)
            for (;; low = (low + 1) & 0x7FFF)
            {
                var tag = CastBit | low;
                if (Casts.TryGetValue(tag, out var have) && have != cast) continue;
                Casts[tag] = cast;
                return tag;
            }
    }

    /// <summary>The tag a program signals, with the entity that loses them as its value.</summary>
    public static uint TakeTag(string item, int count)
    {
        var low = BlockIds.Fnv($"{item}|{count}") & 0x7FFF;
        lock (Gate)
            for (;; low = (low + 1) & 0x7FFF)
            {
                var tag = TakeBit | low;
                if (Takes.TryGetValue(tag, out var have) && have != (item, count)) continue;
                Takes[tag] = (item, count);
                return tag;
            }
    }

    /// <summary>Only those with this node of the game's tree can cast it.</summary>
    public static void GateOn(string ability, uint node, string talent)
    {
        lock (Gate) Gates[ability] = (node, talent);
    }

    // From the service's own tick, so that the subscriptions are the service's: a mod's would end with the mod.
    public static void Subscribe()
    {
        lock (Gate)
        {
            if (Casts.Count > 0 || Takes.Count > 0) _signals ??= Events.On<ImpactSignalEvent>(OnSignal);
            if (Gates.Count > 0) _gates ??= Events.On<AbilityCastEvent>(OnCast);
        }
    }

    private static void OnSignal(ImpactSignalEvent e)
    {
        Cast? cast;
        (string Item, int Count) take;
        lock (Gate)
        {
            if (!Casts.TryGetValue(e.Tag, out cast)) cast = null;
            if (!Takes.TryGetValue(e.Tag, out take)) take = default;
        }
        var who = e.AsEntity;
        if (who.IsNone) return;
        try
        {
            if (take.Item != null)
            {
                _ = Kiln.Items.Take(who, new ItemId(Content.Items.Get(take.Item)!.Id), take.Count);
                return;
            }
            if (cast == null) return;
            var result = Abilities.Cast(who, cast.Ability);
            if (cast.Item == null) return;
            if (!result.Ok)
            {
                if (cast.Took > 0) _ = Kiln.Items.Give(who, cast.Item, cast.Took);
                BlocksBridge.Log(LogLevel.Info, $"blocks: {cast.Ability} from {cast.Item} is refused for {who}: {result.Reason}");
            }
            else if (cast.More > 0) _ = Kiln.Items.Take(who, new ItemId(Content.Items.Get(cast.Item)!.Id), cast.More);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: a signal of a program did not go through ({who}): {ex.Message}");
        }
    }

    private static void OnCast(AbilityCastEvent e)
    {
        (uint Node, string Talent) gate;
        lock (Gate)
            if (!Gates.TryGetValue(e.Cast.Ability.Id, out gate)) return;
        if (GameTree.Query(e.Cast.Caster, gate.Node)?.Unlocked == true) return;
        e.Reason = $"needs the talent {gate.Talent}";
        e.Cancel();
    }
}
