// SPDX-License-Identifier: MIT
// Engine room: the advanced tier in one mod (docs/kiln/modding/typed-fields.md "Any resource", ecs-programs.md "Hooks
// around game systems"). An admin's look at what the game runs, and one field of the game's data changed with a way back.
//   /engine systems [word]         the game's systems whose names hold the word, and whether each is on
//   /engine profile                starts the game's system profiler; again: stops it and lists the five costliest
//   /engine watch <system>         counts the runs of one system (a hook around it); /engine watch alone reports and ends
//   /engine stack <item> <count>   an item's maxStackSize, by the item's debug name, as a claim on a field of its resource
//   /engine claims                 every claim in force, of every mod
//   /engine undo                   gives back everything this mod claimed
// What to take from it:
//   - the names here are the game's own (systems, types, fields) and a game update can rename or drop any of them: ask
//     Available first, and treat a name that is gone as an answer for the admin, not as a crash;
//   - a change to something shared is a claim. The first mod keeps it, the second gets a result that names the holder
//     and changes nothing, and everything a mod claimed is given back when it unloads;
//   - a hook's callbacks run on the game's worker threads, around every run of the system: count with Interlocked and
//     touch nothing of Kiln there. Commands, like all mod code, run on the tick thread, where a hook is made and ended.
using Kiln;

namespace EngineRoom;

public sealed class EngineRoomMod : Mod
{
    private const string Usage = "systems [word], profile, watch [system], stack <item> <count>, claims, or undo";
    private const string ItemType = "keen::ItemInfo";
    // One name for the one profiler the game has: a mod that claims it under another name would not notice this one.
    private const string ProfilerClaim = "kiln:system-profiler";
    private const int Shown = 12;

    private bool _profiling;
    private IDisposable? _hook;
    private string _watched = "";
    private DateTime _since;
    private long _runs;  // written on the game's worker threads

    public override void OnLoad() =>
        Log.Info($"engine-room: systems {(GameSystems.Available ? "available" : "not available")}, resources {(ReflectedResources.Available ? "available" : "not available")}");

    [Command("engine", Description = "what the game runs: systems [word], profile, watch [system], stack <item> <count>, claims, undo", Permission = CommandPermission.Admin)]
    private string Engine(CommandContext c, string action, string? what = null, int count = 0) => action.ToLowerInvariant() switch
    {
        "systems" => Systems(c, what),
        "profile" => Profile(c),
        "watch" => Watch(what),
        "stack" => Stack(what, count),
        "claims" => Claims(c),
        "undo" => Undo(),
        _ => throw new CommandException(Usage),
    };

    private static string Systems(CommandContext c, string? word)
    {
        NeedSystems();
        var found = (word is null ? GameSystems.All : GameSystems.Search(word)).ToList();
        foreach (var system in found.Take(Shown))
            c.Reply($"  {system.Name}{(system.Enabled ? "" : " (off)")}");
        return found.Count > Shown ? $"{found.Count} systems, the first {Shown} shown: give a word of the name" : $"{found.Count} system(s)";
    }

    private string Profile(CommandContext c)
    {
        NeedSystems();
        if (!_profiling)
        {
            // The claim runs the first action now and keeps the second for the way back: Release below, /engine undo,
            // or the unload of this mod. When another mod holds the name, nothing runs and the result says who.
            var claim = Overrides.Claim(ProfilerClaim, () => GameSystems.Profile(true), () => GameSystems.Profile(false));
            if (!claim.Ok) return $"the profiler was not started: {claim.Message}";
            _profiling = true;
            return "profiling: /engine profile again for the numbers";
        }
        Overrides.Release(ProfilerClaim);  // stops the profiler; its numbers stay until it is started again
        _profiling = false;
        foreach (var system in GameSystems.Stats().Take(5))
            c.Reply($"  {system.Name}: {system.Calls} run(s), {system.Milliseconds:0.0} ms on all threads together");
        return $"the costliest systems of {GameSystems.ProfileSeconds:0.0} s";
    }

    private string Watch(string? system)
    {
        NeedSystems();
        if (system is null)
        {
            if (_hook is null) return "nothing is watched: /engine watch <system>";
            // Ending a hook waits for a callback that is running, then no callback comes any more.
            _hook.Dispose();
            _hook = null;
            return $"{_watched}: ran {Interlocked.Read(ref _runs)} time(s) in {(DateTime.UtcNow - _since).TotalSeconds:0.0} s";
        }
        if (_hook is not null) throw new CommandException($"already watching {_watched}: /engine watch ends it");
        if (GameSystems.Find(system) is null) throw new CommandException($"no system named {system}: /engine systems <word> lists them");
        Interlocked.Exchange(ref _runs, 0);
        try
        {
            _hook = GameSystems.Hook(system, before: () => Interlocked.Increment(ref _runs));
        }
        catch (NotSupportedException e)
        {
            // Hooks are not available on this game build, another mod hooks the same update function (two systems can
            // share one), or 64 functions are hooked already.
            throw new CommandException($"{system} cannot be watched: {e.Message}");
        }
        _watched = system;
        _since = DateTime.UtcNow;
        return $"watching {system}: /engine watch reports and ends it";
    }

    private static string Stack(string? item, int count)
    {
        if (!ReflectedResources.Available) throw new CommandException("the game's resources cannot be read here (kiln.assets or kiln.types is not running)");
        if (item is null || count is < 1 or > 9999) throw new CommandException("stack <the item's debug name> <1 to 9999>");
        try
        {
            // Every resource of the type is looked at: fine for a command, too slow for a tick handler.
            var found = ReflectedResources.FindByField(ItemType, "debugName", item);
            if (found.Count != 1) return found.Count == 0 ? $"no item has the debug name {item}" : $"{found.Count} items have the debug name {item}";
            var resource = found[0];
            // A write is a claim on that one field. Conflict (another mod has the field), NotFound and Unavailable are
            // results: Message is a sentence, and the log has it too.
            var result = resource.Set("maxStackSize", count);
            if (!result.Ok) return result.Message;
            return $"{item} stacks to {count} (the game's own value: {resource.Original("maxStackSize")}). The game reads the " +
                   "change when it next loads the item; a mod that needs it from the start makes it in OnLoad.";
        }
        catch (ArgumentException e)
        {
            // The type or a field is not what it was (a game update), or the value does not fit the field.
            return $"not changed: {e.Message}";
        }
    }

    private static string Claims(CommandContext c)
    {
        var active = Overrides.Active;
        foreach (var claim in active)
            c.Reply($"  {claim.Mod}: {claim.Target}{(claim.Detail.Length > 0 ? $" ({claim.Detail})" : "")}");
        return $"{active.Count} claim(s) in force";
    }

    private string Undo()
    {
        // Every claim of this mod, field writes and the profiler alike, in reverse order. The unload does the same.
        var given = Overrides.RestoreAll();
        _profiling = false;
        return $"{given} claim(s) given back";
    }

    private static void NeedSystems()
    {
        if (!GameSystems.Available) throw new CommandException($"the game's systems are not known on this build ({GameTypes.Status})");
    }
}
