// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>World progress ops (Kiln.Quests' <see cref="WorldProgress"/> and <see cref="Quests.Of(EntityId)"/>;
/// docs/kiln/services/world-progress.md "Kiln.Admin"). Read-only: changes stay with the knowledge and quest ops.</summary>
internal static partial class Ops
{
    internal static bool RegisterWorldStatusSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterWorldStatus(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"world status ops off: Kiln.Quests is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterWorldStatus(Bridge bridge, Logger log)
    {
        _ = WorldProgress.Status;  // starts reading the game's data in the background
        // {detail?: bool}: the world's flame, difficulty, spires, bosses, survivors and every player's counts; detail adds the
        // shrine, root and obelisk lists
        bridge.Register("world_status", req =>
        {
            var detail = req["detail"] is JsonValue d && d.TryGetValue<bool>(out var b) && b;
            var o = new JsonObject
            {
                ["index"] = WorldIndexJson(WorldProgress.Status),
                ["flame"] = WorldProgress.Flame is { } f ? FlameJson(f) : null,
                ["difficulty"] = WorldProgress.Difficulty is { } diff
                    ? new JsonObject { ["preset"] = diff.Preset, ["custom"] = diff.IsCustom, ["settings"] = SettingsJson(diff.Settings) }
                    : null,
                ["spires"] = new JsonArray([.. WorldProgress.Spires.Select(s => (JsonNode)SpireJson(s, EntityId.None))]),
                ["bosses"] = new JsonArray([.. WorldProgress.Bosses.Select(x => (JsonNode)BossJson(x, EntityId.None))]),
                ["survivors"] = new JsonArray([.. WorldProgress.Survivors.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["nameTag"] = Hex(s.NameTag), ["rescued"] = s.Rescued,
                })]),
                ["counts"] = new JsonObject
                {
                    ["spires"] = WorldProgress.Spires.Count, ["spiresUnlocked"] = WorldProgress.Spires.Count(s => s.Unlocked == true),
                    ["shrines"] = WorldProgress.Shrines.Count, ["shroudRoots"] = WorldProgress.ShroudRoots.Count,
                    ["elixirWells"] = WorldProgress.ElixirWells.Count, ["obelisks"] = WorldProgress.Obelisks.Count,
                    ["bosses"] = WorldProgress.Bosses.Count, ["bossesTracked"] = WorldProgress.Bosses.Count(x => x.Tracked),
                    ["bossesKilled"] = WorldProgress.Bosses.Count(x => x.Killed == true),
                },
                ["players"] = new JsonArray([.. Players.All.Where(p => p.InGame).Select(p => (JsonNode)PlayerProgressJson(p, false))]),
            };
            if (detail)
            {
                o["shrines"] = new JsonArray([.. WorldProgress.Shrines.Select(s => (JsonNode)ShrineJson(s, EntityId.None))]);
                o["shroudRoots"] = new JsonArray([.. WorldProgress.ShroudRoots.Select(r => (JsonNode)RootJson(r, EntityId.None))]);
                o["obelisks"] = new JsonArray([.. WorldProgress.Obelisks.Select(x => (JsonNode)ObeliskJson(x, EntityId.None))]);
            }
            return o;
        });
        // {target}: one player's progress with every list and their quests (vanilla and Kiln)
        bridge.Register("world_status_player", req =>
        {
            var target = KnowTarget(req);
            if (target.IsNone || Players.Find(target) is not { InGame: true } p) throw new AdminError("target must be a player in game");
            return PlayerProgressJson(p, true);
        });
        // {x, y, z, radius?}: the chests placed within radius (default 8 m, at most 64) and their state where loaded
        bridge.Register("world_chests", req =>
        {
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var radius = req["radius"] is JsonValue rv && rv.TryGetValue<double>(out var r) && double.IsFinite(r) ? Math.Clamp(r, 0.5, 64) : 8;
            return new JsonObject
            {
                ["chests"] = new JsonArray([.. WorldProgress.Chests(at, radius).Select(c => (JsonNode)new JsonObject
                {
                    ["lootId"] = Hex(c.LootId), ["template"] = c.Template, ["position"] = Pos(c.Position), ["loaded"] = c.Loaded,
                    ["entity"] = c.Entity.IsNone ? null : c.Entity.Value, ["opened"] = c.Opened, ["looted"] = c.Looted,
                })]),
            };
        });
        log.Info("world status ops: world_status, world_status_player, world_chests");
        return true;
    }

    private static JsonObject WorldIndexJson(WorldProgressStatus s) => new()
    {
        ["knowledge"] = s.Knowledge, ["world"] = s.World, ["settings"] = s.Settings, ["assets"] = s.Assets,
        ["places"] = s.Places.ToString(), ["bosses"] = s.Bosses.ToString(), ["placesSeconds"] = Math.Round(s.PlacesSeconds, 3),
        ["bossesSeconds"] = Math.Round(s.BossesSeconds, 3), ["problems"] = new JsonArray([.. s.Problems.Select(x => (JsonNode)x)]),
    };

    private static JsonObject FlameJson(FlameStatus f) => new()
    {
        ["level"] = f.Level, ["altars"] = f.Altars, ["highestAltarLevel"] = f.HighestAltarLevel, ["waterSources"] = f.WaterSources,
    };

    private static JsonObject SettingsJson(IReadOnlyDictionary<string, object> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
            o[k] = v switch
            {
                bool b => b,
                double d => d,
                long l => l,
                string s => s,
                _ => v.ToString(),
            };
        return o;
    }

    private static JsonObject FactJson(ProgressFact f) => new()
    {
        ["id"] = Hex(f.Id), ["type"] = f.Query.Type.ToString(), ["scope"] = f.Scope.ToString().ToLowerInvariant(),
    };

    private static JsonObject SpireJson(Spire s, EntityId who)
    {
        var o = new JsonObject
        {
            ["id"] = s.Id, ["template"] = s.Template, ["nameTag"] = Hex(s.NameTag), ["position"] = Pos(s.Position),
            ["unlock"] = FactJson(s.Unlock), ["unlocked"] = s.Unlock.Holds(who),
            ["beaconLit"] = s.BeaconLit,
        };
        if (!who.IsNone) o["topReached"] = s.TopReached?.Holds(who);
        return o;
    }

    private static JsonObject ShrineJson(FlameShrine s, EntityId who) => new()
    {
        ["id"] = s.Id, ["kind"] = s.Kind.ToString().ToLowerInvariant(), ["region"] = s.Region, ["template"] = s.Template, ["nameTag"] = Hex(s.NameTag),
        ["position"] = s.Position is { } p ? Pos(p) : null, ["knowledge"] = FactJson(s.Discovery), ["found"] = who.IsNone ? null : s.Discovery.Holds(who),
    };

    private static JsonObject RootJson(ShroudRoot r, EntityId who) => new()
    {
        ["id"] = r.Id, ["region"] = r.Region, ["elixirWell"] = r.ElixirWell, ["skillPoints"] = r.SkillPoints,
        ["position"] = r.Position is { } p ? Pos(p) : null, ["knowledge"] = FactJson(r.Destruction), ["destroyed"] = who.IsNone ? null : r.Destruction.Holds(who),
    };

    private static JsonObject ObeliskJson(Obelisk o, EntityId who) => new()
    {
        ["id"] = o.Id, ["region"] = o.Region, ["position"] = o.Position is { } p ? Pos(p) : null, ["knowledge"] = FactJson(o.Reading),
        ["read"] = who.IsNone ? null : o.Reading.Holds(who),
    };

    private static JsonObject BossJson(Boss b, EntityId who) => new()
    {
        ["id"] = b.Id, ["template"] = b.Template, ["spawner"] = b.Spawner, ["nameTag"] = Hex(b.NameTag), ["position"] = Pos(b.Position),
        ["kill"] = b.Kill is { } k ? FactJson(k) : null, ["killed"] = b.Kill?.Holds(who),
    };

    private static JsonObject PlayerProgressJson(Player p, bool full)
    {
        var e = p.Entity;
        var c = WorldProgress.For(e);
        var log = Quests.Of(e);
        var o = new JsonObject
        {
            ["entity"] = e.Value, ["name"] = p.Name,
            ["spiresUnlocked"] = c.SpiresUnlocked.Count, ["spireTopsReached"] = c.SpireTopsReached.Count, ["shrinesFound"] = c.ShrinesFound.Count,
            ["rootsDestroyed"] = c.RootsDestroyed.Count, ["elixirWellsCleared"] = c.RootsDestroyed.Count(r => r.ElixirWell),
            ["obelisksRead"] = c.ObelisksRead.Count, ["bossesKilled"] = c.BossesKilled.Count,
            ["questsActive"] = log.Active.Count(), ["questsDone"] = log.Completed.Count(),
        };
        if (!full) return o;
        o["spires"] = new JsonArray([.. WorldProgress.Spires.Select(s => (JsonNode)SpireJson(s, e))]);
        o["shrines"] = new JsonArray([.. WorldProgress.Shrines.Select(s => (JsonNode)ShrineJson(s, e))]);
        o["shroudRoots"] = new JsonArray([.. WorldProgress.ShroudRoots.Select(r => (JsonNode)RootJson(r, e))]);
        o["obelisks"] = new JsonArray([.. WorldProgress.Obelisks.Select(x => (JsonNode)ObeliskJson(x, e))]);
        o["bosses"] = new JsonArray([.. WorldProgress.Bosses.Where(b => b.Tracked).Select(b => (JsonNode)BossJson(b, e))]);
        o["quests"] = new JsonArray([.. log.All.Where(q => q.Status != QuestStatus.Locked).Select(q => (JsonNode)new JsonObject
        {
            ["id"] = q.Id, ["name"] = q.Name, ["kind"] = q.Kind.ToString().ToLowerInvariant(), ["status"] = q.Status.ToString(),
            ["step"] = q.Step, ["steps"] = q.Steps, ["nameTag"] = q.NameTag == 0 ? null : Hex(q.NameTag),
        })]);
        return o;
    }
}
