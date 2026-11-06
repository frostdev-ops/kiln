// SPDX-License-Identifier: MIT
// Elite Vukahs (sample for kiln.creatures). A variant of Enemy_Vukah_Melee with per-creature overrides: level 20
// (health and damage follow through the game's enemy scaling), scale 1.4, a private AI-settings copy with a longer view
// and aggro range and a wider view cone, fire/pierce resistance, no fleeing home, more loot slots and XP. A share of
// natural Vukah spawns become elites (spawn rule), admins spawn one with "/creature spawn elite_vukah".
// Everything is a layer of this mod: unloading it turns every elite back into a plain Vukah (elites it spawned itself
// are removed). Vanilla clients see elites as Vukahs (same template), bigger and tougher.
// Settings: kiln/data/elite-vukah/elite.json (written with the defaults on first start).
using System.Text.Json;
using Kiln;

namespace EliteVukah;

public sealed class Settings
{
    public double Chance { get; set; } = 0.2;
    public int Level { get; set; } = 20;
    public float Scale { get; set; } = 1.4f;
    public float ViewRange { get; set; } = 30;
    public float AggroRange { get; set; } = 16;
    /// <summary>Lab/demo: spawn one elite here once the world runs ([x, y, z]); null = none.</summary>
    public double[]? Demo { get; set; }
}

public sealed class EliteVukahMod : Mod
{
    public const string VariantId = "elite_vukah";
    private const string ViewRangePath = "combatCommitmentSetup.view.range";
    private readonly List<IDisposable> _registrations = [];
    private Settings _s = new();
    private int _elites;

    public override void OnLoad()
    {
        _s = LoadSettings();
        if (!Creatures.Available)
        {
            Log.Warn("elite-vukah: kiln.creatures is not available on this server; nothing to do");
            return;
        }
        _registrations.Add(CreatureVariants.Register(new CreatureVariant
        {
            Id = VariantId,
            Base = "Enemy_Vukah_Melee",
            Name = "Elite Vukah",
            Level = _s.Level,
            Scale = _s.Scale,
            XpFactor = 2.5f,
            DamageFactor = 1.3f,
            Resistances = { [DamageKind.Fire] = 0.5f, [DamageKind.Pierce] = 0.6f },
            LootSlots = (2, 6),
            DisableFeatures = EnemyFeatures.FleeHome,
            // The game's sight check reads combatCommitmentSetup: view.range (times its night and water factors),
            // view.coneHalfAngle, and gain.inRange.range, the distance at which a seen target is engaged at once.
            // aggroSetup.viewRange only sizes the candidate query (at least 64 m), so it matters only beyond 64 m.
            Ai =
            {
                [ViewRangePath] = _s.ViewRange.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["combatCommitmentSetup.gain.inRange.range"] = _s.AggroRange.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["combatCommitmentSetup.view.coneHalfAngle"] = "120",
            },
        }));
        if (_s.Chance > 0) _registrations.Add(CreatureVariants.AddSpawnRule(new CreatureSpawnRule("Enemy_Vukah_Melee", VariantId, _s.Chance)));
        _registrations.Add(CreatureAdminCommands.Register());
        Log.Info($"elite-vukah: variant {VariantId} (level {_s.Level}, scale {_s.Scale}, view {_s.ViewRange} m), " +
                 $"{_s.Chance:P0} of natural Vukah spawns; /creature registered");
        if (_s.Demo is { Length: 3 } d) _ = SpawnDemo(new WorldPosition(d[0], d[1], d[2]));
    }

    [Subscribe]
    private void OnElite(CreatureVariantAppliedEvent e)
    {
        if (e.Variant.Id != VariantId) return;
        _elites++;
        var i = Creatures.Of(e.Entity).Inspect();
        Log.Info($"elite-vukah: elite {e.Entity} ({(e.Natural ? "natural spawn" : "spawned")}): level {i?.Level}, " +
                 $"scale {i?.Scale:0.##}, view range {Creatures.Of(e.Entity).Ai.Get(ViewRangePath)}");
    }

    private async Task SpawnDemo(WorldPosition at)
    {
        while (!Spawn.WorldRunning) await Context.Scheduler.NextTick();
        for (var i = 0; i < 25; i++) await Context.Scheduler.NextTick();
        var e = await Creatures.Spawn(VariantId, at, 0, new CreatureSpawnOptions
        {
            Tag = "elite-demo", Flags = SpawnFlags.Transient, DespawnOnUnload = false,
        });
        Log.Info($"elite-vukah: demo elite {e} at {at}");
    }

    public override void OnUnload()
    {
        foreach (var r in _registrations) r.Dispose();
        Log.Info($"elite-vukah: unloading ({_elites} elite(s) this session); elites turn back into Vukahs");
    }

    private Settings LoadSettings()
    {
        var path = Path.Combine(Context.DataDirectory, "elite.json");
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), json) ?? new Settings();
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new Settings(), json));
        }
        catch (Exception e)
        {
            Log.Warn($"elite-vukah: {path}: {e.Message}; using the defaults");
        }
        return new Settings();
    }
}
