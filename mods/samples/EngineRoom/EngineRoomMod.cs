// SPDX-License-Identifier: MIT
// Engine room: the advanced tier in one mod (docs/kiln/modding/typed-fields.md "Any resource", ecs-programs.md "Hooks
// around game systems"). An admin's look at what the game runs, and one field of the game's data changed with a way back.
//   /engine systems [word]         the game's systems whose names hold the word, and whether each is on
//                                  (several words in quotes: /engine systems "fall damage")
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
//   - commands run on the tick thread, and that is where a hook is made and ended. The hook's callbacks do not: they
//     run on the game's worker threads, once for each slice of the system's update. Count with Interlocked there, and
//     hand anything that needs the tick thread over with Context.Scheduler.Post.
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
        // The list can be read whenever the game's types are known, also where systems cannot be switched or profiled.
        var found = (word is null ? GameSystems.All : GameSystems.Search(word)).ToList();
        if (found.Count == 0 && GameSystems.All.Count == 0) return $"the game's systems are not known here ({GameTypes.Status})";
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
            OverrideResult claim;
            try
            {
                claim = Overrides.Claim(ProfilerClaim, () => GameSystems.Profile(true), () => GameSystems.Profile(false));
            }
            catch (NotSupportedException e)
            {
                // The first action threw: the claim is dropped again and nothing is held.
                throw new CommandException($"the profiler cannot be started: {e.Message}");
            }
            if (!claim.Ok) return $"the profiler was not started: {claim.Message}";
            _profiling = true;
            return "profiling: /engine profile again for the numbers";
        }
        // Giving a claim back runs its way back. If that throws, the claim is gone all the same and the log has the
        // error, so look at the thing itself before saying it stopped.
        Overrides.Release(ProfilerClaim);
        _profiling = false;
        foreach (var system in GameSystems.Stats().Take(5))
            c.Reply($"  {system.Name}: {system.Calls} run(s), {system.Milliseconds:0.0} ms on all threads together");
        return GameSystems.Profiling
            ? "the profiler did not stop (kiln.log says why); the numbers so far are above"
            : $"the costliest systems of {GameSystems.ProfileSeconds:0.0} s";
    }

    private string Watch(string? system)
    {
        if (system is null)
        {
            // Ending a hook must always be possible, also when systems can no longer be switched: no question first.
            if (_hook is null) return "nothing is watched: /engine watch <system>";
            // It waits for a callback that is running, then no callback comes any more.
            _hook.Dispose();
            _hook = null;
            return $"{_watched}: {Interlocked.Read(ref _runs)} slice(s) of it ran in {(DateTime.UtcNow - _since).TotalSeconds:0.0} s";
        }
        NeedSystems();
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
            // Everything that can fail is done before the write, so "not changed" below is always true.
            var before = resource["maxStackSize"];
            // A write is a claim on that one field. Conflict (another mod has the field), NotFound and Unavailable are
            // results: Message is a sentence, and the log has it too. A value that does not fit the field throws and
            // changes nothing.
            var result = resource.Set("maxStackSize", count);
            if (!result.Ok) return result.Message;
            return $"{item} stacks to {count} (before: {before}). The game reads the change when it next loads the item; " +
                   "a mod that needs it from the start makes it in OnLoad.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException or KeyNotFoundException)
        {
            // The type or a field is not what it was (a game update), the resource could not be read, or the value
            // does not fit the field.
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
        return $"{given} claim(s) given back" + (GameSystems.Profiling ? "; the profiler did not stop (kiln.log says why)" : "");
    }

    private static void NeedSystems()
    {
        if (!GameSystems.Available) throw new CommandException($"the game's systems are not known on this build ({GameTypes.Status})");
    }
}
