// SPDX-License-Identifier: MIT
// Console commands for manual tests in a hosted (offline) world, where there is no chat. Run them through the console
// folder: echo 'give Wood Logs 10' > <game>/kiln/control/console/c.cmd, the answer is in c.result.json beside it. On a
// test server they work from chat too (admins).
//
//   give <item> [count]     materials, consumables, ammunition and animal food the player has found before
//   time [hh:mm]            sets (or prints) the time of day
//   tp <x> <y> <z> | tp base [n] | tp back
//   where                   position, heading, bases
//   heal                    health to full
//   stamina [n|max], mana [n|max]
//   testhelper              which services this world offers
//
// Nothing here grants progress: no experience, levels, skill points, knowledge or recipes. `give` refuses gear,
// blueprints, collectibles and any item the player has never had, because a first pickup unlocks recipes.
//
// The console answers at once, so `give` and `tp` (they finish a few steps later) reply "queued #<token>" and write
// their outcome to kiln.log and to <data>/results.jsonl, one JSON object per line: {token, what, ok, text, time}.
using System.Globalization;
using System.Text.Json;
using Kiln;

namespace KilnTestHelper;

public sealed class KilnTestHelperMod : Mod
{
    private const int MaxCount = 999;

    private static readonly HashSet<ItemCategory> GiveCategories =
        [ItemCategory.Materials, ItemCategory.Consumables, ItemCategory.Ammunition, ItemCategory.AnimalFood];

    private string _results = "";
    private WorldPosition? _back;

    public override void OnLoad()
    {
        _results = Path.Combine(Context.DataDirectory, "results.jsonl");
        Log.Info($"kiln-test-helper: loaded (side {Mods.Side}); queued results go to {_results}; {Services()}");
    }

    // ------------------------------------------------------------------------------------------------- commands ----

    [Command("testhelper", Description = "which services the helper can use in this world", Permission = CommandPermission.Admin)]
    private string Status() => Services() + $"; results file {_results}";

    [Command("give", Description = "gives the player plain items they have found before: give Wood Logs 10 (name, debug name or id; count 1..999)",
        Permission = CommandPermission.Admin)]
    private string Give(CommandContext c, [Greedy] string text)
    {
        if (!Items.Available) throw new CommandException("kiln.items is not available");
        var (name, count) = SplitCount(text);
        var info = Items.Lookup(name);
        if (info is null)
        {
            var near = Items.Search(name, 5).Select(i => i.Label);
            throw new CommandException($"no item '{name}'" + (near.Any() ? $"; similar: {string.Join(", ", near)}" : ""));
        }
        if (Refusal(info) is { } why) throw new CommandException($"{info.Label}: {why}");
        var p = Target(c);
        if (FirstPickup(p, info) is { } never) throw new CommandException(never);

        var token = NewToken();
        _ = GiveAsync(token, p, info, count);
        return $"give {count} {info.Label} ({info.Name}) to {p.Name}: queued #{token}";
    }

    [Command("time", Description = "sets the time of day: time 23:00 (also 7pm, noon, dusk); without a value it prints it",
        Permission = CommandPermission.Admin)]
    private string TimeOfDay([Greedy] string text = "")
    {
        if (!Time.Available || !Time.Ready) throw new CommandException("kiln.time is not ready (no world running)");
        var before = Time.State.Clock;
        if (string.IsNullOrWhiteSpace(text)) return $"time {before} ({(Time.IsDay ? "day" : "night")})";
        if (!Time.TryParse(text, out var hours)) throw new CommandException($"'{text}' is not a time of day (23:00, 7pm, noon)");
        Time.Set(hours);
        var line = $"time {before} -> {Time.Format(hours)}";
        Log.Info("kiln-test-helper: " + line);
        return line;
    }

    [Command("tp", Description = "teleports the player: tp <x> <y> <z> (metres, ground search on) | tp base [n] (a base's flame altar; " +
        "no n: the largest base) | tp back (where the last tp started)", Permission = CommandPermission.Admin)]
    private string Tp(CommandContext c, [Greedy] string text)
    {
        if (!Teleport.Available) throw new CommandException("teleport is not available (kiln.world)");
        var p = Target(c);
        var from = World.Position(p.Entity) ?? throw new CommandException($"{p.Name} has no position yet");
        var words = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var token = NewToken();
        string what;
        Task<TeleportResult> task;
        if (words.Length >= 1 && words[0].Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            var bases = BaseList();
            if (bases.Count == 0) throw new CommandException("no base in this world (kiln.build lists none)");
            PlayerBase b;
            if (words.Length >= 2)
            {
                if (!int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > bases.Count)
                    throw new CommandException($"base number 1..{bases.Count} (see where)");
                b = bases[n - 1];
            }
            else b = bases.OrderByDescending(x => x.Half.X * x.Half.Z).ThenBy(x => x.Altar.Value).First();
            what = $"tp {p.Name} to the base at {Fmt(b.Center)} (altar {b.Altar})";
            task = ToBase(p.Entity, b);
        }
        else if (words.Length == 1 && words[0].Equals("back", StringComparison.OrdinalIgnoreCase))
        {
            var back = _back ?? throw new CommandException("no earlier tp in this world");
            what = $"tp {p.Name} back to {Fmt(back)}";
            task = Teleport.To(p.Entity, back, new TeleportOptions { FindGround = true });
        }
        else if (words.Length == 3 && TryNum(words[0], out var x) && TryNum(words[1], out var y) && TryNum(words[2], out var z))
        {
            var to = new WorldPosition(x, y, z);
            what = $"tp {p.Name} to {Fmt(to)}";
            task = Teleport.To(p.Entity, to, new TeleportOptions { FindGround = true, Checkpoint = Distance(from, to) > 200 });
        }
        else throw new CommandException("tp <x> <y> <z> | tp base [n] | tp back");

        _back = from;
        _ = Finish(token, what, task, r => (r.Moved, r.ToString()));
        return $"{what}: queued #{token}";
    }

    [Command("where", Description = "the player's position (metres: x east, y up, z north), heading and the world's bases",
        Permission = CommandPermission.Admin)]
    private string Where(CommandContext c)
    {
        var p = Target(c);
        var t = World.Transform(p.Entity) ?? throw new CommandException($"{p.Name} has no position yet");
        var pos = t.Position;
        var line = $"{p.Name} pos {pos.X.ToString("F2", CultureInfo.InvariantCulture)} {pos.Y.ToString("F2", CultureInfo.InvariantCulture)} " +
                   $"{pos.Z.ToString("F2", CultureInfo.InvariantCulture)} yaw {t.YawDegrees.ToString("F1", CultureInfo.InvariantCulture)}";
        var bases = BaseList();
        if (bases.Count > 0)
            line += "; bases: " + string.Join(", ", bases.Select((b, i) =>
                $"{i + 1} at {Fmt(b.Center)} ({Distance(pos, b.Center):F0} m{(b.Contains(pos) ? ", inside" : "")})"));
        return line;
    }

    [Command("heal", Description = "health to full", Permission = CommandPermission.Admin)]
    private string Heal(CommandContext c)
    {
        if (!Combat.Available) throw new CommandException("kiln.combat is not available");
        var p = Target(c);
        var h = Combat.GetHealth(p.Entity) ?? throw new CommandException($"{p.Name} has no health");
        if (h.Dead) throw new CommandException($"{p.Name} is dead: respawn first");
        var added = Combat.Heal(p.Entity, Math.Max(0, h.Max - h.Current));
        return $"heal {p.Name}: {h.Current} -> {h.Current + added} of {h.Max}";
    }

    [Command("stamina", Description = "sets stamina: stamina max | stamina <n>; without a value it prints it", Permission = CommandPermission.Admin)]
    private string Stamina(CommandContext c, string value = "") => SetResource(c, AbilityResource.Stamina, value);

    [Command("mana", Description = "sets mana: mana max | mana <n>; without a value it prints it", Permission = CommandPermission.Admin)]
    private string Mana(CommandContext c, string value = "") => SetResource(c, AbilityResource.Mana, value);

    // ---------------------------------------------------------------------------------------------- give rules -----

    /// <summary>Why an item is not handed out, or null.</summary>
    private static string? Refusal(ItemInfo info)
    {
        if (info.IsBlueprint || info.Category == ItemCategory.Blueprints) return "blueprints unlock recipes: not given";
        if (info.IsGear) return "gear (weapons, armour, tools) is not given; only materials, consumables and ammunition";
        if (!GiveCategories.Contains(info.Category))
            return $"category {info.Category} is not given; only materials, consumables, ammunition and animal food";
        return null;
    }

    /// <summary>Null when giving the item changes none of the player's knowledge; else why it is refused. The game's
    /// knowledge object of an item has the item's id: unset means the player never had it, and a first pickup unlocks
    /// the recipes that use it.</summary>
    private static string? FirstPickup(Player p, ItemInfo info)
    {
        if (!Knowledge.Available) return "kiln.knowledge is not available, so the helper cannot check that the item is not new to the player";
        KnowledgeState? s;
        try
        {
            s = Knowledge.Get(p.Entity, info.Id.Value);
        }
        catch (NotSupportedException e)
        {
            return $"cannot read {p.Name}'s knowledge: {e.Message}";
        }
        if (s is null) return null;  // no knowledge object: picking it up unlocks nothing
        if (!s.Value.IsSet)
            return $"{p.Name} has never had {info.Label}; a first pickup unlocks recipes, so it is not given";
        return null;
    }

    private async Task GiveAsync(string token, Player p, ItemInfo info, int count)
    {
        var what = $"give {count} {info.Label} to {p.Name}";
        var gained = new List<string>();
        // Report any knowledge change of this player while the give runs (there should be none: see FirstPickup).
        using var watch = Knowledge.Available
            ? Events.On<KnowledgeChangedEvent>(e =>
            {
                if (e.Target == p.Entity) gained.Add($"0x{e.Id:x8} {e.OldValue}->{e.NewValue}");
            })
            : null;
        bool ok;
        string text;
        try
        {
            var r = await Items.Give(p.Entity, info.Id, count, options: GiveOptions.Notify);
            ok = r.Status == ItemsStatus.Done;
            var have = Items.Read(p.Entity)?.Count(info.Id);
            text = $"{r.Status}: {r.Done} of {r.Requested} given{(have is { } n ? $", {p.Name} now has {n}" : "")}" +
                   (string.IsNullOrEmpty(r.Message) ? "" : $" ({r.Message})");
            for (var i = 0; i < 3; i++) await Context.Scheduler.NextTick();  // knowledge changes post a step later
        }
        catch (Exception e)
        {
            ok = false;
            text = e.Message;
        }
        text += gained.Count == 0 ? "; knowledge unchanged" : $"; knowledge changed: {string.Join(", ", gained)}";
        Record(token, what, ok, text);
    }

    // --------------------------------------------------------------------------------------------------- helpers ----

    private static Player Target(CommandContext c)
    {
        if (!Players.Available) throw new CommandException("kiln.players is not available");
        if (c.Source.IsPlayer && Players.Find(c.Source.Player) is { } self) return self;
        // the console has no player: the host (the first player in game)
        return Players.All.FirstOrDefault() ?? throw new CommandException("no player in game yet");
    }

    private static string SetResource(CommandContext c, AbilityResource resource, string value)
    {
        if (!Abilities.Available) throw new CommandException("kiln.abilities is not available");
        var p = Target(c);
        var name = resource.ToString().ToLowerInvariant();
        var cur = Abilities.Attributes.Get(p.Entity, resource) ?? throw new CommandException($"{p.Name} has no {name}");
        var max = Abilities.Attributes.Max(p.Entity, resource) ?? cur;
        if (string.IsNullOrWhiteSpace(value)) return $"{name} {cur} of {max}";
        int want;
        if (value.Equals("max", StringComparison.OrdinalIgnoreCase) || value.Equals("full", StringComparison.OrdinalIgnoreCase)) want = max;
        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out want))
            throw new CommandException($"{name} max | {name} <n>");
        want = Math.Clamp(want, 0, Math.Max(max, 0));
        if (want != cur && !Abilities.Attributes.Add(p.Entity, resource, want - cur))
            throw new CommandException($"the game refused the {name} change");
        return $"{name} {p.Name}: {cur} -> {want} of {max}";
    }

    private static async Task<TeleportResult> ToBase(EntityId player, PlayerBase b)
    {
        var r = await Teleport.ToSpawnPoint(player, b.Altar, new TeleportOptions { Checkpoint = true });
        if (r.Moved) return r;
        // not a spawn point on this build: next to the altar's zone centre, ground search on
        return await Teleport.To(player, b.Center, new TeleportOptions { FindGround = true, Checkpoint = true });
    }

    private static List<PlayerBase> BaseList()
    {
        if (!Build.Available) return [];
        try
        {
            return Build.Bases.Where(b => !b.Abandoned).OrderBy(b => b.Altar.Value).ToList();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    private async Task Finish<T>(string token, string what, Task<T> task, Func<T, (bool Ok, string Text)> describe)
    {
        bool ok;
        string text;
        try
        {
            (ok, text) = describe(await task);
        }
        catch (Exception e)
        {
            ok = false;
            text = e.Message;
        }
        Record(token, what, ok, text);
    }

    private void Record(string token, string what, bool ok, string text)
    {
        Log.Info($"kiln-test-helper: #{token} {what}: {(ok ? "ok" : "FAILED")}: {text}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_results)!);
            var json = JsonSerializer.Serialize(new { token, what, ok, text, time = DateTime.UtcNow.ToString("O") });
            File.AppendAllText(_results, json + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"kiln-test-helper: cannot write {_results}: {e.Message}");
        }
    }

    private static (string Name, int Count) SplitCount(string text)
    {
        var words = text.Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) throw new CommandException("give <item> [count]");
        var count = 1;
        if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        {
            count = last;
            words.RemoveAt(words.Count - 1);
        }
        else if (words.Count > 1 && int.TryParse(words[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first))
        {
            count = first;
            words.RemoveAt(0);
        }
        if (count < 1 || count > MaxCount) throw new CommandException($"count 1..{MaxCount}");
        return (string.Join(' ', words).Trim('"'), count);
    }

    private static string Services() =>
        $"items {Items.Available}, knowledge {Knowledge.Available}, time {Time.Available && Time.Ready}, " +
        $"teleport {Teleport.Available}, build {Build.Available}, combat {Combat.Available}, abilities {Abilities.Available}, " +
        $"players {Players.Available}";

    private static string NewToken() => Guid.NewGuid().ToString("N")[..8];

    private static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static double Distance(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string Fmt(WorldPosition p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.X:F1} {p.Y:F1} {p.Z:F1}");
}
