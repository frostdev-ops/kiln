// SPDX-License-Identifier: MIT
// What a move's `animation` names, and the one seam to the player graph's states. A GUID or a `0x` id is resolved offline (an id becomes 16 bytes with that
// FNV-1a, BlockIds.WithFnv: the engine only ever hashes the reference). A name is a state of this mod or of the game's graph, which only the player-anim
// lookup knows, and it knows it when content is ready: until it is wired (PlayerStateSeam.Lookup), a name is reported then as not available in this build.
namespace Kiln;

/// <summary>How a sequence enters a state: <c>SetAnimationEvent.animationName</c> (a full-body state) or <c>SetAnimationOverlayEvent.overlayName</c> (an overlay).</summary>
internal enum PlayerStateKind { State = 0, Overlay = 1 }

internal enum PlayerStateStatus { Live = 0, Refused = 1, Pending = 2, Unavailable = 3 }

/// <summary>A state of the game's player graph: its machine, its kind, and every id that enters it (none for one no id enters, such as <c>Idle_Block</c>).</summary>
internal sealed record GameStateAnswer(string Machine, string Name, PlayerStateKind Kind, IReadOnlyList<uint> Ids);

/// <summary>A state a mod defines: its GUID (<c>BlockIds.Derive(mod, "state", name)</c>), kind and status, and why when it is not live.</summary>
internal sealed record ModStateAnswer(Guid Guid, PlayerStateKind Kind, PlayerStateStatus Status, string Reason);

/// <summary>The two questions the player-anim lookup answers, by content ready, on both sides, from any thread.</summary>
internal interface IPlayerStateLookup
{
    /// <summary><c>"State"</c> or <c>"Machine/State"</c>. Empty: none; several: the bare name is in several machines.</summary>
    IReadOnlyList<GameStateAnswer> FindGameStates(string name);
    /// <summary>Null: mod <paramref name="mod"/> defines no state <paramref name="name"/>.</summary>
    ModStateAnswer? Find(string mod, string name);
}

/// <summary>Where the lookup plugs in. Null (today): player states are not available in this build. Tests set it.</summary>
internal static class PlayerStateSeam
{
    internal static IPlayerStateLookup? Lookup;
}

/// <summary>One name of a move's <c>animation</c>, as the compile read it: a GUID it already is, or a name the lookup answers for at content ready. Null names keep the motion's own.</summary>
internal sealed record AnimationRef(string Written, Guid? Guid, string? Name)
{
    /// <summary>The kind, when the name says it (a mod's or a game's state by name). A GUID or an id says none: the event follows from what the motion has.</summary>
    public PlayerStateKind? Kind { get; init; }
}

internal static class MoveStates
{
    /// <summary>What a written name is, offline. A GUID and <c>0x</c> + 8 hex digits are a game state the graph is not asked about; anything else waits for the lookup.</summary>
    public static AnimationRef? Parse(BlockContext ctx, string? written, string path)
    {
        if (written == null) return null;
        var w = written.Trim();
        if (w.Length == 0) throw new BlockError(ctx.Block, path, "an animation names a state: its name, its GUID or its 0x id (the catalog's graphs section, docs/kiln/modding/catalogs.md)");
        if (System.Guid.TryParse(w, out var g))
        {
            if (g == System.Guid.Empty) throw new BlockError(ctx.Block, path, "the empty GUID names no state");
            return new AnimationRef(written, g, null);
        }
        if (w.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (w.Length != 10 || !uint.TryParse(w.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var id))
                throw new BlockError(ctx.Block, path, $"\"{written}\" is not a state id: 0x and 8 hex digits, as the catalog's graphs section writes them");
            return new AnimationRef(written, BlockIds.WithFnv(id), null);
        }
        if (w.Length > 129 || w.Count(c => c == '/') > 1 || w.StartsWith('/') || w.EndsWith('/'))
            throw new BlockError(ctx.Block, path, $"\"{written}\" is not a state's name: a state of this mod (up to 64 characters, no '/'), a game state as \"State\" or \"Machine/State\", a GUID or a 0x id");
        return new AnimationRef(written, null, w);
    }

    /// <summary>A name, when content is ready: the GUID and kind it stands for, or why it stands for none. A state of the mod comes first, then the game's.</summary>
    public static (Guid Guid, PlayerStateKind? Kind)? Resolve(string mod, AnimationRef a, IPlayerStateLookup? lookup, out string? problem)
    {
        problem = null;
        if (a.Guid is { } known) return (known, a.Kind);
        var name = a.Name!;
        if (lookup == null)
        {
            problem = $"\"{name}\" cannot be looked up: player states are not available in this build. Name a game state by its GUID or 0x id (the catalog's graphs section) until then; the motion keeps its own";
            return null;
        }
        if (!name.Contains('/') && lookup.Find(mod, name) is { } own)
        {
            if (own.Status == PlayerStateStatus.Live) return (own.Guid, own.Kind);
            problem = $"mod {mod}'s state \"{name}\" is {own.Status.ToString().ToLowerInvariant()}{(own.Reason is { Length: > 0 } r ? $": {r}" : "")}; the motion keeps its own";
            return null;
        }
        var game = lookup.FindGameStates(name);
        if (game.Count == 0)
        {
            problem = $"\"{name}\" is no state of mod {mod} and none of the game's player graph (the catalog's graphs section lists the game's)";
            return null;
        }
        if (game.Count > 1)
        {
            problem = $"\"{name}\" is a state of several machines of the game's player graph: {string.Join(", ", game.Select(x => $"{x.Machine}/{x.Name}"))}. Say which, as \"Machine/State\"";
            return null;
        }
        var s = game[0];
        if (s.Ids.Count == 0)
        {
            problem = $"the game's state {s.Machine}/{s.Name} is entered by no id (it follows from other states, as Idle_Block does under Block_FullBody), so no sequence can name it";
            return null;
        }
        if (s.Ids.Count > 1)
        {
            // a jump attack's state has one per weapon family: which one this weapon's graph path takes is the family's, and only its id says it
            problem = $"the game's state {s.Machine}/{s.Name} is entered by {s.Ids.Count} ids ({string.Join(", ", s.Ids.Select(i => $"0x{i:x8}"))}, one per weapon family): write the one you mean as its 0x id";
            return null;
        }
        return (BlockIds.WithFnv(s.Ids[0]), s.Kind);
    }
}
