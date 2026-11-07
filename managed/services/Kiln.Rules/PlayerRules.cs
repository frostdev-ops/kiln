// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// What a rule set asks of the game for the players it applies to. Every member is optional: null means "no opinion",
/// and the game's own rule stands unless another rule set says otherwise. Several rule sets can apply to one player at
/// once (from several mods, scopes and times); <see cref="Rules"/> combines them member by member:
/// <list type="bullet">
/// <item>switches (<c>bool?</c>): <c>true</c> from any rule set grants it, an explicit <c>false</c> from any rule set
/// denies it, and a denial wins. The server's <c>kiln/config/rules.json</c> can deny any switch for everyone;</item>
/// <item>multipliers (<see cref="BuildCost"/>, <see cref="BuildRefund"/>, <see cref="DigDamage"/>): multiplied
/// together, then held to the server's limits;</item>
/// <item>floors (<see cref="MinHealth"/>, <see cref="MinStamina"/>, <see cref="MinMana"/>, <see cref="MinShroudTime"/>): the
/// highest wins;</item>
/// <item><see cref="Water"/>: the most generous wins (the fastest rate; the items of every rule set).</item>
/// </list>
/// The same object reads from and writes to JSON (camelCase): a preset in <c>kiln/config/rules/&lt;name&gt;.json</c> is
/// one of these.
/// </summary>
public sealed record PlayerRules
{
    // ---- movement ----

    /// <summary>The game's building fly mode outside bases too (the game allows it only inside a base, for players with
    /// the building permission and the fly-mode unlock; with this none of those is needed). Needs Kiln in the player's
    /// game.</summary>
    public bool? FlyAnywhere { get; init; }

    /// <summary>The character goes where the player flew: when the flight ends (the fly key, a hit, or the rule ending
    /// outside a base) Kiln puts it on the ground below the spark, invulnerable until it stands there. (The game leaves it
    /// sitting where the flight began.) Default: on wherever <see cref="FlyAnywhere"/> is on.</summary>
    public bool? FlightCarriesBody { get; init; }

    // ---- placement (the build cursor's refusals; needs Kiln in the player's game) ----

    /// <summary>Build outside bases (the game's "outside a building zone").</summary>
    public bool? BuildOutsideBases { get; init; }

    /// <summary>Build inside the game's no-build zones (around points of interest, dungeons, altars).</summary>
    public bool? IgnoreNoBuildZones { get; init; }

    /// <summary>Build with enemies nearby.</summary>
    public bool? IgnoreEnemiesNearby { get; init; }

    /// <summary>Build inside the Shroud.</summary>
    public bool? BuildInShroud { get; init; }

    /// <summary>Build in other players' bases without their permission. Off unless the server's rules.json allows it.</summary>
    public bool? IgnoreBasePermissions { get; init; }

    /// <summary>Keep what the player builds outside bases in the save (the game saves terrain only inside bases): Kiln
    /// adds those places to its kept regions (Kiln.Build). Default: on wherever <see cref="BuildOutsideBases"/> is.</summary>
    public bool? KeepBuildsOutsideBases { get; init; }

    // ---- costs ----

    /// <summary>x what placing blocks, terrain and props takes from the inventory (0 = free; free also lifts the
    /// cursor's "missing materials").</summary>
    public double? BuildCost { get; init; }

    /// <summary>x what dismantling and undo give back. Pair a low <see cref="BuildCost"/> with a low refund, or placing
    /// and dismantling turns into a material source.</summary>
    public double? BuildRefund { get; init; }

    /// <summary>Requests crafting without input costs. Currently unavailable: effective rules deny it until an
    /// authoritative transaction hook is available; the game keeps its own crafting behavior.</summary>
    public bool? FreeCrafting { get; init; }

    /// <summary>Requests keeping used consumables. Currently unavailable: slot-change events cannot prove consumption.</summary>
    public bool? KeepConsumables { get; init; }

    // ---- mining ----

    /// <summary>x the damage the player's tools deal to terrain and building blocks (1000 breaks anything breakable in
    /// one hit; unbreakable stays unbreakable).</summary>
    public double? DigDamage { get; init; }

    // ---- vitals ----

    /// <summary>No damage from hits (Kiln.Combat's invulnerability).</summary>
    public bool? Invulnerable { get; init; }

    /// <summary>No damage from falls, from any height, and no hard-landing stagger (Kiln.Combat's fall event, canceled
    /// on the server). <see cref="Invulnerable"/> already covers falls; this is for rules that keep other damage.</summary>
    public bool? NoFallDamage { get; init; }

    /// <summary>Health is kept at least at this fraction of its max (1 = always full).</summary>
    public double? MinHealth { get; init; }

    /// <summary>Stamina is kept at least at this fraction of its max (1 = never runs out).</summary>
    public double? MinStamina { get; init; }

    /// <summary>Mana is kept at least at this fraction of its max.</summary>
    public double? MinMana { get; init; }

    // ---- the Shroud ----

    /// <summary>The Shroud timer does not go down while the player is in the Shroud, and the Shroud cannot kill them (the
    /// game kills by setting health to 0 when the timer runs out, which <see cref="Invulnerable"/> does not stop). When
    /// the rule ends the timer counts down again from where it stands. A timer already at 0 recovers first.</summary>
    public bool? FreezeShroudTimer { get; init; }

    /// <summary>The Shroud timer never drops below this fraction of its max (1 = always full): below it, it recovers at
    /// the game's own rate (25 s per second); at it, it holds. Also keeps the Shroud from killing the player.</summary>
    public double? MinShroudTime { get; init; }

    // ---- gliding (needs Kiln in the player's game: the glide runs in the game's prediction too) ----

    /// <summary>The glider never forces the player down for a gameplay reason: gliding costs no stamina and does not end
    /// when stamina would have run out. (Landing, a hit, water and letting go still end it, as in the game.)</summary>
    public bool? UnlimitedGliding { get; init; }

    /// <summary>x how fast the glider sinks (1 = the game's; 0 = holds its altitude). Multiplied with other rule sets'
    /// values.</summary>
    public double? GlideSink { get; init; }

    /// <summary>The glider flies like a plane: it climbs and dives where the player looks (climbing costs speed, diving
    /// gives it), and holding the sprint key pushes it along that path, up to a speed cap and a climb rate; letting go
    /// glides on and settles into the game's glide. The most generous of every rule set wins (each value separately).</summary>
    public GlideBoostRule? GlideBoost { get; init; }

    // ---- water ----

    /// <summary>The player's water containers get water back (Kiln.Items' refill policies).</summary>
    public WaterRule? Water { get; init; }

    /// <summary>What <c>/creative</c> requests in addition to the preset where it says nothing. Effective rules still
    /// enforce the server's limits and feature availability; an explicit false in the preset keeps a request off.</summary>
    public static PlayerRules CreativeExtras { get; } = new() { FreeCrafting = true, KeepConsumables = true };

    /// <summary>Kiln's built-in "creative" preset: free flight and building anywhere outside other players' bases,
    /// nothing costs or runs out, tools break blocks in one hit, no damage. No levels, XP, skill points or unlocks.</summary>
    public static PlayerRules Creative { get; } = new()
    {
        FlyAnywhere = true, FlightCarriesBody = true,
        BuildOutsideBases = true, IgnoreNoBuildZones = true, IgnoreEnemiesNearby = true, BuildInShroud = true,
        KeepBuildsOutsideBases = true,
        BuildCost = 0, BuildRefund = 0, DigDamage = 1000,
        Invulnerable = true, MinHealth = 1, MinStamina = 1, MinMana = 1,
        FreezeShroudTimer = true,
        UnlimitedGliding = true, GlideSink = 0, GlideBoost = GlideBoostRule.Default,
        Water = WaterRule.KeepFull(),
    };

    // ---------------------------------------------------------------------------------------- JSON ----------

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
    };

    /// <summary>Reads rules from JSON (camelCase members, comments allowed). Throws <see cref="JsonException"/> naming
    /// the member that is wrong or unknown.</summary>
    public static PlayerRules FromJson(string json) =>
        JsonSerializer.Deserialize<PlayerRules>(json, Json) ?? throw new JsonException("empty rules");

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>These rules with every member <paramref name="other"/> sets taking its place.</summary>
    public PlayerRules With(PlayerRules other) => new()
    {
        FlyAnywhere = other.FlyAnywhere ?? FlyAnywhere, FlightCarriesBody = other.FlightCarriesBody ?? FlightCarriesBody,
        BuildOutsideBases = other.BuildOutsideBases ?? BuildOutsideBases, IgnoreNoBuildZones = other.IgnoreNoBuildZones ?? IgnoreNoBuildZones,
        IgnoreEnemiesNearby = other.IgnoreEnemiesNearby ?? IgnoreEnemiesNearby, BuildInShroud = other.BuildInShroud ?? BuildInShroud,
        IgnoreBasePermissions = other.IgnoreBasePermissions ?? IgnoreBasePermissions,
        KeepBuildsOutsideBases = other.KeepBuildsOutsideBases ?? KeepBuildsOutsideBases,
        BuildCost = other.BuildCost ?? BuildCost, BuildRefund = other.BuildRefund ?? BuildRefund,
        FreeCrafting = other.FreeCrafting ?? FreeCrafting, KeepConsumables = other.KeepConsumables ?? KeepConsumables, DigDamage = other.DigDamage ?? DigDamage,
        Invulnerable = other.Invulnerable ?? Invulnerable, NoFallDamage = other.NoFallDamage ?? NoFallDamage, MinHealth = other.MinHealth ?? MinHealth,
        MinStamina = other.MinStamina ?? MinStamina, MinMana = other.MinMana ?? MinMana,
        FreezeShroudTimer = other.FreezeShroudTimer ?? FreezeShroudTimer, MinShroudTime = other.MinShroudTime ?? MinShroudTime,
        UnlimitedGliding = other.UnlimitedGliding ?? UnlimitedGliding, GlideSink = other.GlideSink ?? GlideSink,
        GlideBoost = other.GlideBoost ?? GlideBoost,
        Water = other.Water ?? Water,
    };

    /// <summary>Out-of-range values, by member name; empty when the rules are fine.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        void Mult(string name, double? v)
        {
            if (v is { } x && (!double.IsFinite(x) || x < 0)) p.Add($"{name}: {x} is not a multiplier (0 or more)");
        }
        void Frac(string name, double? v)
        {
            if (v is { } x && (!double.IsFinite(x) || x < 0 || x > 1)) p.Add($"{name}: {x} is not a fraction 0..1");
        }
        Mult("buildCost", BuildCost);
        Mult("buildRefund", BuildRefund);
        Mult("digDamage", DigDamage);
        Frac("minHealth", MinHealth);
        Frac("minStamina", MinStamina);
        Frac("minMana", MinMana);
        Frac("minShroudTime", MinShroudTime);
        Mult("glideSink", GlideSink);
        if (GlideBoost is { } g)
        {
            Mult("glideBoost.thrust", g.Thrust);
            Mult("glideBoost.maxSpeed", g.MaxSpeed);
            Mult("glideBoost.lift", g.Lift);
            Mult("glideBoost.coast", g.Coast);
            Mult("glideBoost.climb", g.Climb);
            if (g.MaxSpeed > GlideBoostRule.SpeedLimit) p.Add($"glideBoost.maxSpeed: {g.MaxSpeed} is over {GlideBoostRule.SpeedLimit} m/s");
            if (g.Climb > GlideBoostRule.SpeedLimit) p.Add($"glideBoost.climb: {g.Climb} is over {GlideBoostRule.SpeedLimit} m/s");
        }
        if (Water is { } w && (double.IsNaN(w.ChargesPerSecond) || w.ChargesPerSecond <= 0))
            p.Add($"water.chargesPerSecond: {w.ChargesPerSecond} must be more than 0 (Infinity keeps the containers full)");
        return p;
    }
}

/// <summary>Water a player's containers get back (Kiln.Items' refill policies, on the player's inventories).</summary>
public sealed record WaterRule
{
    /// <summary>Charges per second of game time; <c>Infinity</c> (the default) keeps the containers full.</summary>
    public double ChargesPerSecond { get; init; } = double.PositiveInfinity;

    /// <summary>The items refilled: debug names (<c>Tool_T1_WateringCan</c>, a mod's <c>&lt;mod&gt;.&lt;block&gt;</c>) or
    /// ids (<c>0x8ec15337</c>). Null or empty: every container that holds water.</summary>
    public IReadOnlyList<string>? Items { get; init; }

    [JsonIgnore]
    public bool KeepsFull => double.IsPositiveInfinity(ChargesPerSecond);

    public static WaterRule KeepFull(params string[] items) => new() { Items = items.Length == 0 ? null : items };

    public static WaterRule Rate(double chargesPerSecond, params string[] items) =>
        new() { ChargesPerSecond = chargesPerSecond, Items = items.Length == 0 ? null : items };

    public bool Equals(WaterRule? other) =>
        other is not null && ChargesPerSecond.Equals(other.ChargesPerSecond) && SameItems(Items, other.Items);

    public override int GetHashCode() => HashCode.Combine(ChargesPerSecond, Items?.Count ?? 0);

    private static bool SameItems(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a is null || a.Count == 0) ? b is null || b.Count == 0 : b is not null && a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));

    public override string ToString() =>
        (KeepsFull ? "keep full" : $"{ChargesPerSecond:0.##} charges/s") + (Items is { Count: > 0 } ? $" ({string.Join(", ", Items)})" : "");
}

/// <summary>Gliding like a plane (<see cref="PlayerRules.GlideBoost"/>): the glider's path takes its pitch from where the
/// player looks (a view within about 5 degrees of level flies level), climbing trades speed for height and diving height
/// for speed, and the sprint key pushes along the path. It runs the same way in the server's world and the player's own
/// prediction, so the server agrees with what the player sees.</summary>
public sealed record GlideBoostRule
{
    /// <summary>The fastest any rule may make a boosted glider, m/s.</summary>
    public const double SpeedLimit = 120;

    /// <summary>Acceleration along the glider's heading (it turns toward where the player looks) while sprint is held,
    /// m/s². It replaces the game's air drag along the heading, so it is the same with every glider. Default 15: about
    /// 1.6 s from a plain glide (11-20 m/s) to the default cap.</summary>
    public double Thrust { get; init; } = 15;

    /// <summary>The boost accelerates up to this speed, m/s (default 35). It only caps the push: speed from a dive stays.</summary>
    public double MaxSpeed { get; init; } = 35;

    /// <summary>Upward acceleration while boosting, m/s² (default 0: the boost goes where the glider points; a
    /// "jetpack" might use 8-10).</summary>
    public double Lift { get; init; }

    /// <summary>After letting go of sprint the glider slows down at most this fast, m/s² (default 8: about 2-3 s back
    /// to a plain glide; 0 = the game's drag, which stops a fast glider almost at once).</summary>
    public double Coast { get; init; } = 8;

    /// <summary>The fastest the glider climbs while the player looks up, m/s (default 15; 0 = it never climbs: looking up
    /// then only slows it). Boosting while looking up climbs at up to this rate; without the boost a climb lasts as long
    /// as the speed it trades.</summary>
    public double Climb { get; init; } = 15;

    public static GlideBoostRule Default { get; } = new();

    public override string ToString() =>
        $"boost {Thrust:0.#} m/s² up to {MaxSpeed:0.#} m/s" + (Lift > 0 ? $", lift {Lift:0.#} m/s²" : "") + $", coast {Coast:0.#} m/s²" +
        (Climb > 0 ? $", climbs up to {Climb:0.#} m/s" : ", no climbing");
}

/// <summary>A player's rules after every rule set that applies was combined, and the server's limits held.</summary>
public sealed record EffectiveRules
{
    public EntityId Player { get; init; }
    public bool FlyAnywhere { get; init; }
    public bool FlightCarriesBody { get; init; }
    public bool BuildOutsideBases { get; init; }
    public bool IgnoreNoBuildZones { get; init; }
    public bool IgnoreEnemiesNearby { get; init; }
    public bool BuildInShroud { get; init; }
    public bool IgnoreBasePermissions { get; init; }
    public bool KeepBuildsOutsideBases { get; init; }
    public double BuildCost { get; init; } = 1;
    public double BuildRefund { get; init; } = 1;
    public bool FreeCrafting { get; init; }
    public bool KeepConsumables { get; init; }
    public double DigDamage { get; init; } = 1;
    public bool Invulnerable { get; init; }
    public bool NoFallDamage { get; init; }
    public double MinHealth { get; init; }
    public double MinStamina { get; init; }
    public double MinMana { get; init; }
    public bool FreezeShroudTimer { get; init; }
    public double MinShroudTime { get; init; }
    public bool UnlimitedGliding { get; init; }
    public double GlideSink { get; init; } = 1;
    public GlideBoostRule? GlideBoost { get; init; }
    public WaterRule? Water { get; init; }

    /// <summary>The rule sets that applied, in the order they were set.</summary>
    public IReadOnlyList<RuleSet> From { get; init; } = Array.Empty<RuleSet>();

    /// <summary>What the server's limits or unavailable features took away, when they did.</summary>
    public IReadOnlyList<string> Denied { get; init; } = Array.Empty<string>();

    public static EffectiveRules None(EntityId player) => new() { Player = player };

    /// <summary>True when nothing differs from the game's own rules.</summary>
    public bool IsDefault => !FlyAnywhere && !BuildOutsideBases && !IgnoreNoBuildZones && !IgnoreEnemiesNearby && !BuildInShroud &&
                             !IgnoreBasePermissions && !KeepBuildsOutsideBases && BuildCost == 1 && BuildRefund == 1 && !FreeCrafting && !KeepConsumables && DigDamage == 1 &&
                             !Invulnerable && !NoFallDamage && MinHealth == 0 && MinStamina == 0 && MinMana == 0 && !FreezeShroudTimer &&
                             MinShroudTime == 0 && !UnlimitedGliding && GlideSink == 1 && GlideBoost is null && Water is null;

    internal RulesNativePart Native => new(
        (FlyAnywhere ? RulesNative.FlyAnywhere : 0) | (BuildOutsideBases ? RulesNative.BuildOutsideBases : 0) |
        (IgnoreNoBuildZones ? RulesNative.IgnoreNoBuildZones : 0) | (IgnoreEnemiesNearby ? RulesNative.IgnoreEnemies : 0) |
        (BuildInShroud ? RulesNative.IgnoreShroud : 0) | (IgnoreBasePermissions ? RulesNative.IgnoreBasePermissions : 0) |
        (FreezeShroudTimer ? RulesNative.ShroudFreeze : 0) | (UnlimitedGliding ? RulesNative.GlideUnlimited : 0) |
        (GlideBoost is not null ? RulesNative.GlideBoost : 0),
        (float)BuildCost, (float)BuildRefund, (float)DigDamage, (float)MinShroudTime, (float)GlideSink,
        (float)(GlideBoost?.Thrust ?? 0), (float)(GlideBoost?.MaxSpeed ?? 0), (float)(GlideBoost?.Lift ?? 0),
        (float)(GlideBoost?.Coast ?? 0), (float)(GlideBoost?.Climb ?? 0));

    /// <summary>The same values, ignoring <see cref="From"/>.</summary>
    public bool SameAs(EffectiveRules o) => this with { From = o.From, Denied = o.Denied } == o;

    public override string ToString()
    {
        if (IsDefault) return "the game's rules";
        var parts = new List<string>();
        if (FlyAnywhere) parts.Add(FlightCarriesBody ? "fly anywhere (landing where the flight ends)" : "fly anywhere");
        if (BuildOutsideBases) parts.Add("build outside bases" + (KeepBuildsOutsideBases ? " (kept)" : ""));
        if (IgnoreNoBuildZones) parts.Add("no-build zones ignored");
        if (IgnoreEnemiesNearby) parts.Add("build near enemies");
        if (BuildInShroud) parts.Add("build in the Shroud");
        if (IgnoreBasePermissions) parts.Add("build in others' bases");
        if (BuildCost != 1) parts.Add(BuildCost == 0 ? "free building" : $"building x{BuildCost:0.##}");
        if (BuildRefund != 1) parts.Add($"refunds x{BuildRefund:0.##}");
        if (FreeCrafting) parts.Add("free crafting");
        if (KeepConsumables) parts.Add("consumables kept");
        if (DigDamage != 1) parts.Add(DigDamage >= 1000 ? "instant mining" : $"dig x{DigDamage:0.##}");
        if (Invulnerable) parts.Add("invulnerable");
        else if (NoFallDamage) parts.Add("no fall damage");
        if (MinHealth > 0) parts.Add(MinHealth >= 1 ? "health full" : $"health >= {MinHealth:P0}");
        if (MinStamina > 0) parts.Add(MinStamina >= 1 ? "stamina full" : $"stamina >= {MinStamina:P0}");
        if (MinMana > 0) parts.Add(MinMana >= 1 ? "mana full" : $"mana >= {MinMana:P0}");
        if (FreezeShroudTimer) parts.Add("Shroud timer frozen");
        if (MinShroudTime > 0) parts.Add(MinShroudTime >= 1 ? "Shroud timer full" : $"Shroud timer >= {MinShroudTime:P0}");
        if (UnlimitedGliding) parts.Add("unlimited gliding");
        if (GlideSink != 1) parts.Add(GlideSink == 0 ? "gliding holds altitude" : $"glide sink x{GlideSink:0.##}");
        if (GlideBoost is { } g) parts.Add("glide " + g);
        if (Water is { } w) parts.Add("water: " + w);
        return string.Join(", ", parts);
    }
}

/// <summary>How <see cref="PlayerRules"/> combine (pure: unit-tested without a game).</summary>
internal static class RulesMath
{
    public static EffectiveRules Combine(EntityId player, IReadOnlyList<(RuleSet? Set, PlayerRules Rules)> sets, RulesLimits limits)
    {
        bool Switch(Func<PlayerRules, bool?> get, bool fallback = false)
        {
            bool grant = false, deny = false;
            foreach (var (_, r) in sets)
            {
                var v = get(r);
                if (v == true) grant = true;
                else if (v == false) deny = true;
            }
            return !deny && (grant || fallback);
        }
        double Product(Func<PlayerRules, double?> get)
        {
            var m = 1.0;
            foreach (var (_, r) in sets)
                if (get(r) is { } v && double.IsFinite(v) && v >= 0) m *= v;
            return m;
        }
        double Floor(Func<PlayerRules, double?> get)
        {
            var f = 0.0;
            foreach (var (_, r) in sets)
                if (get(r) is { } v && double.IsFinite(v)) f = Math.Max(f, Math.Clamp(v, 0, 1));
            return f;
        }

        var fly = Switch(r => r.FlyAnywhere);
        var outside = Switch(r => r.BuildOutsideBases);
        var e = new EffectiveRules
        {
            Player = player,
            FlyAnywhere = fly,
            FlightCarriesBody = fly && Switch(r => r.FlightCarriesBody, fallback: true),
            BuildOutsideBases = outside,
            IgnoreNoBuildZones = Switch(r => r.IgnoreNoBuildZones),
            IgnoreEnemiesNearby = Switch(r => r.IgnoreEnemiesNearby),
            BuildInShroud = Switch(r => r.BuildInShroud),
            IgnoreBasePermissions = Switch(r => r.IgnoreBasePermissions),
            KeepBuildsOutsideBases = Switch(r => r.KeepBuildsOutsideBases, fallback: outside),
            BuildCost = Product(r => r.BuildCost),
            BuildRefund = Product(r => r.BuildRefund),
            FreeCrafting = Switch(r => r.FreeCrafting),
            KeepConsumables = Switch(r => r.KeepConsumables),
            DigDamage = Product(r => r.DigDamage),
            Invulnerable = Switch(r => r.Invulnerable),
            NoFallDamage = Switch(r => r.NoFallDamage),
            MinHealth = Floor(r => r.MinHealth),
            MinStamina = Floor(r => r.MinStamina),
            MinMana = Floor(r => r.MinMana),
            FreezeShroudTimer = Switch(r => r.FreezeShroudTimer),
            MinShroudTime = Floor(r => r.MinShroudTime),
            UnlimitedGliding = Switch(r => r.UnlimitedGliding),
            GlideSink = Product(r => r.GlideSink),
            GlideBoost = Boost(sets.Select(s => s.Rules.GlideBoost)),
            Water = Water(sets.Select(s => s.Rules.Water)),
            From = sets.Where(s => s.Set is not null).Select(s => s.Set!).ToArray(),
        };
        return limits.Hold(e);
    }

    // The most generous of each value.
    public static GlideBoostRule? Boost(IEnumerable<GlideBoostRule?> rules)
    {
        GlideBoostRule? best = null;
        foreach (var b in rules)
        {
            if (b is null) continue;
            static double V(double v) => double.IsFinite(v) && v > 0 ? v : 0;
            best = best is null
                ? new GlideBoostRule { Thrust = V(b.Thrust), MaxSpeed = V(b.MaxSpeed), Lift = V(b.Lift), Coast = V(b.Coast), Climb = V(b.Climb) }
                : new GlideBoostRule
                {
                    Thrust = Math.Max(best.Thrust, V(b.Thrust)), MaxSpeed = Math.Max(best.MaxSpeed, V(b.MaxSpeed)),
                    Lift = Math.Max(best.Lift, V(b.Lift)), Coast = Math.Max(best.Coast, V(b.Coast)), Climb = Math.Max(best.Climb, V(b.Climb)),
                };
        }
        return best;
    }

    // The most generous: the fastest rate; the items of every rule set (any without a list means every container).
    public static WaterRule? Water(IEnumerable<WaterRule?> rules)
    {
        WaterRule? best = null;
        var all = false;
        var items = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in rules)
        {
            if (w is null || double.IsNaN(w.ChargesPerSecond) || w.ChargesPerSecond <= 0) continue;
            if (best is null || w.ChargesPerSecond > best.ChargesPerSecond) best = w;
            if (w.Items is not { Count: > 0 }) all = true;
            else items.UnionWith(w.Items);
        }
        return best is null ? null : new WaterRule { ChargesPerSecond = best.ChargesPerSecond, Items = all ? null : items.ToArray() };
    }
}

/// <summary>The server's limits on everyone's rules (<c>kiln/config/rules.json</c>), as plain values.</summary>
internal sealed record RulesLimits(bool AllowFlyAnywhere = true, bool AllowBuildOutsideBases = true, bool AllowIgnoreNoBuildZones = true,
    bool AllowIgnoreEnemiesNearby = true, bool AllowBuildInShroud = true, bool AllowIgnoreBasePermissions = false,
    bool AllowInvulnerable = true, double MinBuildCost = 0, double MaxDigDamage = 1000, double MaxStaminaFloor = 1,
    double MaxHealthFloor = 1, double MaxManaFloor = 1, bool AllowWater = true, bool AllowFreezeShroudTimer = true,
    double MaxShroudFloor = 1, bool AllowUnlimitedGliding = true, bool AllowGlideBoost = true, double MaxGlideSpeed = 60,
    bool AllowNoFallDamage = true, bool AllowFreeCrafting = true, bool AllowKeepConsumables = true)
{
    public static readonly RulesLimits Default = new();

    public EffectiveRules Hold(EffectiveRules e)
    {
        var denied = new List<string>();
        bool Cap(bool v, bool allowed, string name)
        {
            if (v && !allowed) denied.Add(name);
            return v && allowed;
        }
        var fly = Cap(e.FlyAnywhere, AllowFlyAnywhere, "flyAnywhere");
        var r = e with
        {
            FlyAnywhere = fly,
            FlightCarriesBody = fly && e.FlightCarriesBody,
            BuildOutsideBases = Cap(e.BuildOutsideBases, AllowBuildOutsideBases, "buildOutsideBases"),
            IgnoreNoBuildZones = Cap(e.IgnoreNoBuildZones, AllowIgnoreNoBuildZones, "ignoreNoBuildZones"),
            IgnoreEnemiesNearby = Cap(e.IgnoreEnemiesNearby, AllowIgnoreEnemiesNearby, "ignoreEnemiesNearby"),
            BuildInShroud = Cap(e.BuildInShroud, AllowBuildInShroud, "buildInShroud"),
            IgnoreBasePermissions = Cap(e.IgnoreBasePermissions, AllowIgnoreBasePermissions, "ignoreBasePermissions"),
            Invulnerable = Cap(e.Invulnerable, AllowInvulnerable, "invulnerable"),
            NoFallDamage = Cap(e.NoFallDamage, AllowNoFallDamage, "noFallDamage"),
            FreeCrafting = Cap(e.FreeCrafting, AllowFreeCrafting, "freeCrafting"),
            KeepConsumables = Cap(e.KeepConsumables, AllowKeepConsumables, "keepConsumables"),
            BuildCost = Math.Max(e.BuildCost, Math.Max(MinBuildCost, 0)),
            DigDamage = Math.Min(e.DigDamage, Math.Max(MaxDigDamage, 0)),
            MinHealth = Math.Min(e.MinHealth, MaxHealthFloor),
            MinStamina = Math.Min(e.MinStamina, MaxStaminaFloor),
            MinMana = Math.Min(e.MinMana, MaxManaFloor),
            FreezeShroudTimer = Cap(e.FreezeShroudTimer, AllowFreezeShroudTimer, "freezeShroudTimer"),
            MinShroudTime = Math.Min(e.MinShroudTime, MaxShroudFloor),
            UnlimitedGliding = Cap(e.UnlimitedGliding, AllowUnlimitedGliding, "unlimitedGliding"),
            GlideBoost = e.GlideBoost is { } g && Cap(true, AllowGlideBoost, "glideBoost")
                ? g with
                {
                    MaxSpeed = Math.Min(g.MaxSpeed, Math.Clamp(MaxGlideSpeed, 0, GlideBoostRule.SpeedLimit)),
                    Climb = Math.Min(g.Climb, Math.Clamp(MaxGlideSpeed, 0, GlideBoostRule.SpeedLimit)),
                }
                : null,
            Water = AllowWater ? e.Water : null,
        };
        if (e.Water is not null && !AllowWater) denied.Add("water");
        r = r with { KeepBuildsOutsideBases = r.KeepBuildsOutsideBases && r.BuildOutsideBases };
        return denied.Count == 0 ? r : r with { Denied = denied };
    }
}
