// SPDX-License-Identifier: MIT
// What a placed thing does when used, beyond what its game components say, on the server world (dedicated server and host):
//  - the checks of an interaction on the player (states, code): kiln.gameplay's InteractEvent is the game's AcceptOfferedInteractionEvent in the Pre of the step that
//    handles it, and a cancel turns it into the game's own DeclinedInteraction, so the host never accepts and the guest's sequence ends (docs/re/gameplay.md 4).
//  - effects once the game accepted: InteractionResultEvent, the next step's Pre.
//  - beds: the last spawn bed a player slept in is where a respawn's teleport goes (TeleportEvent), and a night ends when every player in the world sleeps in a bed that says so.
// Nothing subscribes until a block needs it, and only on the server world. A player's action events (Kiln.Players) see the interaction sequence start, but the accept event is
// already sent by then, so they can't refuse it: the checks are on the accept itself.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class InteractRuntime
{
    internal sealed record Entry(string Mod, string Block, Guid? Effects, Guid? Refused, PlayerStateId[]? When, PlayerStateId[]? Unless, Func<Player, bool>? If);
    private sealed record Bed(string Mod, string Block, bool SpawnPoint, bool SkipsNight);

    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, Entry> Entries = new();
    private static readonly Dictionary<Guid, Bed> Beds = new();
    private static readonly Dictionary<Guid, (string Block, List<(string Item, int Count)> Items)> Holds = new();
    private static IDisposable? _tick, _attempts, _results, _teleports, _joins, _watch;
    private static Dictionary<string, JsonObject>? _spawns;
    private static string? _spawnFile;
    private static ulong _nightSkipped;

    private static bool ServerSide => Client.IsDedicatedServer || Client.IsHosting;

    /// <summary>What Kiln does for a template's interaction. A mod that makes its blocks again replaces it.</summary>
    public static void Add(ModBlocks mod, Guid template, Entry e)
    {
        lock (Gate)
        {
            Entries[template] = e;
            _tick ??= BlocksBridge.AddTick("kiln blocks interactions", OnTick);
        }
    }

    public static void AddBed(ModBlocks mod, Guid template, string block, bool spawnPoint, bool skipsNight)
    {
        lock (Gate)
        {
            Beds[template] = new Bed(mod.Id, block, spawnPoint, skipsNight);
            _spawnFile ??= SpawnFile(mod.Id);
            _tick ??= BlocksBridge.AddTick("kiln blocks interactions", OnTick);
        }
    }

    /// <summary>What a container of this template holds when it is first placed.</summary>
    public static void AddHolds(ModBlocks mod, Guid template, string block, List<(string Item, int Count)> items)
    {
        lock (Gate)
        {
            Holds[template] = (block, items);
            _tick ??= BlocksBridge.AddTick("kiln blocks interactions", OnTick);
        }
    }

    /// <summary>The checks and effects for a template, for tests.</summary>
    internal static Entry? EntryOf(Guid template)
    {
        lock (Gate) return Entries.GetValueOrDefault(template);
    }

    private static Guid? TemplateOf(EntityId e) => Spawn.Inspect(e)?.Template;

    private static void OnTick(ulong step)
    {
        if (!ServerSide) return;
        if (step % 10 == 4) Subscribe();
        if (step % 50 == 9) Night(step);
    }

    // From the service's own tick, so that the subscriptions are the service's.
    private static void Subscribe()
    {
        lock (Gate)
        {
            if (Entries.Values.Any(e => e.When != null || e.Unless != null || e.If != null)) _attempts ??= Events.On<InteractEvent>(OnAttempt);
            if (Entries.Values.Any(e => e.Effects != null) || Beds.Values.Any(b => b.SpawnPoint)) _results ??= Events.On<InteractionResultEvent>(OnResult);
            if (Beds.Values.Any(b => b.SpawnPoint)) _teleports ??= Events.On<TeleportEvent>(OnTeleport);
            if (Holds.Count > 0)
            {
                _joins ??= Events.On<EntityJoinLevelEvent>(OnJoin);
                _watch ??= Gameplay.WatchEntities(EntityCategories.Prop);
            }
        }
    }

    // ---- checks and effects --------------------------------------------------------------------------------------------

    /// <summary>Whether a guest may use a thing of this template now. Null: Kiln has nothing to say about it.</summary>
    internal static bool? Allows(EntityId guest, Guid template)
    {
        Entry? e;
        lock (Gate) e = Entries.GetValueOrDefault(template);
        if (e == null || (e.When == null && e.Unless == null && e.If == null)) return null;
        if (e.When is { } when && !when.Any(s => PlayerStates.Is(guest, s))) return false;
        if (e.Unless is { } unless && unless.Any(s => PlayerStates.Is(guest, s))) return false;
        if (e.If is { } check)
        {
            try
            {
                var player = (Players.Available ? Players.Find(guest) : null) ?? new Player { Entity = guest };
                if (!check(player)) return false;
            }
            catch (Exception x)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: block {e.Block}: its check threw: {x.Message}");
                return false;
            }
        }
        return true;
    }

    // A player is a guest that has a player's state: villagers use beds too, and get nothing here.
    private static bool IsPlayer(EntityId guest) => guest.IsPlayer || PlayerStates.Read(guest) != null;

    private static void OnAttempt(InteractEvent e)
    {
        if (e.Canceled || TemplateOf(e.Host) is not { } template) return;
        if (Allows(e.Guest, template) != false || !IsPlayer(e.Guest)) return;
        e.Cancel();
        Entry? entry;
        lock (Gate) entry = Entries.GetValueOrDefault(template);
        Run(entry?.Refused, e.Guest, entry?.Block ?? "");
    }

    private static void OnResult(InteractionResultEvent e)
    {
        if (!e.Accepted || TemplateOf(e.Host) is not { } template) return;
        Entry? entry;
        Bed? bed;
        lock (Gate)
        {
            entry = Entries.GetValueOrDefault(template);
            bed = Beds.GetValueOrDefault(template);
        }
        if ((entry?.Effects == null && bed is not { SpawnPoint: true }) || !IsPlayer(e.Guest)) return;
        Run(entry?.Effects, e.Guest, entry?.Block ?? "");
        if (bed is { SpawnPoint: true }) SleptIn(e.Guest, e.Host, bed);
    }

    internal static void Run(Guid? program, EntityId who, string block)
    {
        if (program is not { } g || !World.Exists(who) || World.Position(who) is not { } at) return;
        var started = Abilities.RunProgram(g, at, new ImpactOptions { Root = who, Tag = "blocks-interact" });
        if (started.IsFaulted) BlocksBridge.Log(LogLevel.Warn, $"blocks: block {block}: its program did not start on {who}: {started.Exception?.GetBaseException().Message}");
    }

    // ---- containers -----------------------------------------------------------------------------------------------------

    // keen::ecs::DefaultInventory: the game's flag that a container got its default inventory, saved with the entity
    private const int DefaultInventorySize = 20, Applied = 0x11;

    private static void OnJoin(EntityJoinLevelEvent e)
    {
        if (e.Template is not { } template) return;
        (string Block, List<(string Item, int Count)> Items) holds;
        lock (Gate)
            if (!Holds.TryGetValue(template, out holds)) return;
        var who = e.Entity;
        // its components are filled in the step it is made: look a few steps later
        BlocksBridge.After(4, () => _ = Fill(who, holds.Block, holds.Items));
    }

    /// <summary>Puts what a container starts with into it, once: the game's own flag says whether it has had it.</summary>
    internal static async Task<bool> Fill(EntityId box, string block, IReadOnlyList<(string Item, int Count)> items)
    {
        if (World.ReadBytes(box, "DefaultInventory") is not { Length: DefaultInventorySize } d || d[Applied] != 0) return false;
        World.TryWrite(box, "DefaultInventory", Applied, (byte)1);
        foreach (var (item, count) in items)
        {
            var r = await Items.Give(box, item, count);
            if (r.Status != ItemsStatus.Done) BlocksBridge.Log(LogLevel.Warn, $"blocks: block {block}: {item} x{count} did not go into {box}: {r}");
        }
        return true;
    }

    // ---- beds -----------------------------------------------------------------------------------------------------------

    private static string? SpawnFile(string mod)
    {
        try { return Path.Combine(Path.GetDirectoryName(BlocksBridge.BlockReportFile(mod))!, "spawn-beds.json"); }
        catch (InvalidOperationException) { return null; }
    }

    // Per player: the bed, by its place and template, so a bed that is gone is no spawn point. Kept in kiln/data/blocks, across restarts.
    private static Dictionary<string, JsonObject> Spawns()
    {
        if (_spawns != null) return _spawns;
        _spawns = new();
        try
        {
            if (_spawnFile != null && File.Exists(_spawnFile) && JsonNode.Parse(File.ReadAllText(_spawnFile)) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonObject x) _spawns[k] = x;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: spawn beds: {_spawnFile} is not readable: {x.Message}");
        }
        return _spawns;
    }

    private static string? PlayerKey(EntityId guest)
    {
        if (!Players.Available || Players.Find(guest) is not { } p) return null;
        return p.SteamId != 0 ? $"steam:{p.SteamId}" : p.AccountId != 0 ? $"account:{p.AccountId}" : null;
    }

    /// <summary>A player slept in a spawn bed: where they come back after dying. Only players of the roster: others have nothing to come back as.</summary>
    internal static void SleptIn(EntityId guest, EntityId bed, string key)
    {
        if (World.Position(bed) is not { } at || TemplateOf(bed) is not { } template) return;
        lock (Gate)
        {
            Spawns()[key] = new JsonObject { ["x"] = at.X, ["y"] = at.Y, ["z"] = at.Z, ["template"] = template.ToString(), ["entity"] = bed.Value };
            Save();
        }
    }

    private static void SleptIn(EntityId guest, EntityId bed, Bed b)
    {
        if (PlayerKey(guest) is { } key) SleptIn(guest, bed, key);
    }

    private static void Save()
    {
        if (_spawnFile == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_spawnFile)!);
            var o = new JsonObject();
            foreach (var (k, v) in Spawns()) o[k] = v.DeepClone();
            File.WriteAllText(_spawnFile + ".tmp", o.ToJsonString());
            File.Move(_spawnFile + ".tmp", _spawnFile, overwrite: true);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: spawn beds: cannot write {_spawnFile}: {x.Message}");
        }
    }

    /// <summary>Where a player with this key comes back, if their bed still stands. For tests.</summary>
    internal static WorldPosition? SpawnOf(string key)
    {
        JsonObject? s;
        lock (Gate) s = Spawns().GetValueOrDefault(key);
        if (s == null) return null;
        var at = new WorldPosition((double)s["x"]!, (double)s["y"]!, (double)s["z"]!);
        var template = Guid.Parse((string)s["template"]!);
        // the bed must still stand there: one within a quarter metre, of the same template
        foreach (var e in Spawn.Find(template, at, 0.25, 4))
            if (World.Position(e) is { } p && Math.Abs(p.X - at.X) < 0.25 && Math.Abs(p.Z - at.Z) < 0.25) return new WorldPosition(at.X, at.Y + 0.6, at.Z);
        return null;
    }

    // A respawn is a teleport of a dead player; fast travel is one of a living one.
    private static void OnTeleport(TeleportEvent e)
    {
        if (!e.IsPlayer || !PlayerStates.Is(e.Entity, PlayerStateId.Dead) || PlayerKey(e.Entity) is not { } key || SpawnOf(key) is not { } bed) return;
        BlocksBridge.Log(LogLevel.Info, $"blocks: {key} comes back at their bed ({bed.X:F1}, {bed.Y:F1}, {bed.Z:F1}) instead of ({e.Target.X:F1}, {e.Target.Y:F1}, {e.Target.Z:F1})");
        e.Target = bed;
    }

    /// <summary>Whether this character sleeps in a bed that ends the night.</summary>
    internal static bool SleepsThroughNight(EntityId who)
    {
        if (PlayerStates.Read(who) is not { } s || !s.Is(PlayerStateId.Sleeping) || s.Action is not { Host: var host } || host.IsNone) return false;
        if (TemplateOf(host) is not { } template) return false;
        lock (Gate) return Beds.GetValueOrDefault(template) is { SkipsNight: true };
    }

    // Every 50 steps at night: when every player in the world sleeps in such a bed, the clock goes forward to the morning, as the admin menu's time does. Once a night.
    private static void Night(ulong step)
    {
        lock (Gate)
            if (!Beds.Values.Any(b => b.SkipsNight)) return;
        if (!Time.Available || !Time.TryGetState(out var t) || t.IsDay || !Players.Available) return;
        var players = Players.All.Where(p => p.InGame && !p.Entity.IsNone).ToList();
        if (players.Count == 0 || !players.All(p => SleepsThroughNight(p.Entity))) return;
        if (_nightSkipped != 0 && step - _nightSkipped < 600) return;
        _nightSkipped = step;
        BlocksBridge.Log(LogLevel.Info, $"blocks: all {players.Count} player(s) sleep at {t.Clock}: morning comes ({Time.Format(t.DayStart)})");
        Time.Set(t.DayStart);
    }
}
