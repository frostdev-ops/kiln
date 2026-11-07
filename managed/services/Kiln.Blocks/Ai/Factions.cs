// SPDX-License-Identifier: MIT
// The world's faction relations: who fights whom, for every creature of a faction, on top of the game's own rule (equal factions and the players'
// side are friends; most others fight; a few species leave each other alone). Server side: kiln.npcai's hooks on the game's relation checks.
namespace Kiln;

/// <summary>What kiln.npcai reports: which hooks it has, what is set, how often the game asked and Kiln answered.</summary>
public sealed record FactionStatus(bool Available, bool Installed, int Relations, int CreatureRows, int Pairs, int Mapped, ulong Calls, ulong Answered,
    ulong ByPair, ulong ByRow, ulong ByTable)
{
    public override string ToString() =>
        $"kiln.npcai {(Available ? "on" : "off")}, hooks {(Installed ? "in" : "out")}: {Relations} relation(s), {CreatureRows} creature(s) with their own, {Pairs} pair(s), " +
        $"{Mapped} mapped; {Calls} call(s), {Answered} answered by Kiln (pair {ByPair}, creature {ByRow}, table {ByTable})";
}

/// <summary>
/// Who fights whom in the world, by faction: <c>Factions.Relate(Faction.Vukah, Faction.Scavenger, Relation.Hostile)</c>. The game's targeting, sensors and
/// "is it on my side" checks answer from it. A creature's own view goes on its <see cref="AIBlock.Relations"/> and <see cref="AIBlock.SenseFilter"/>.
/// Settings belong to the calling mod and go when it unloads; two mods on one pair: the last set wins. Tick thread. Server side (and a world a client hosts).
/// </summary>
public static unsafe class Factions
{
    /// <summary>kiln.npcai is here and found the game's checks on this build.</summary>
    public static bool Available
    {
        get
        {
            var t = AiNative.T;
            if (t == null) return false;
            var s = new AiNative.Status { Size = (uint)sizeof(AiNative.Status) };
            return t->GetStatus(&s) == AiNative.Ok && (s.Resolved & 31) == 31 && (s.Reserved & 1) == 0;
        }
    }

    /// <summary>a toward b, and b toward a unless <paramref name="mutual"/> is false. <see cref="Relation.Game"/> gives the pair back to the game.</summary>
    public static void Relate(Faction a, Faction b, Relation relation, bool mutual = true)
    {
        Set(a, b, relation);
        if (mutual && a != b) Set(b, a, relation);
    }

    /// <summary>Gives every relation the calling mod set back to the game.</summary>
    public static void Reset()
    {
        var t = Require("Factions.Reset");
        RequireTick("Factions.Reset");
        var mod = CallerMod("Factions.Reset");
        AiNative.Check(t->OwnerRelease(AiNative.Owner(mod)), "Factions.Reset");
        AiRuntime.Released(mod);
    }

    /// <summary>The relation the world's table holds for a toward b (any mod's); <see cref="Relation.Game"/> when none.</summary>
    public static Relation Get(Faction a, Faction b)
    {
        var t = AiNative.T;
        return t == null ? Relation.Game : (Relation)t->RelationGet((byte)a, (byte)b);
    }

    /// <summary>What the game's own check answers now: would a fight b. With Kiln's table in it.</summary>
    public static bool Fights(Faction a, Faction b)
    {
        var t = Require("Factions.Fights");
        uint h = 0, f = 0;
        AiNative.Check(t->QueryFactions((byte)a, (byte)b, &h, &f), "Factions.Fights");
        return h != 0;
    }

    /// <summary>What the game's own check answers now: is b on a's side.</summary>
    public static bool Friends(Faction a, Faction b)
    {
        var t = Require("Factions.Friends");
        uint h = 0, f = 0;
        AiNative.Check(t->QueryFactions((byte)a, (byte)b, &h, &f), "Factions.Friends");
        return f != 0;
    }

    /// <summary>What the game's checks answer now for two entities, a about b, with Kiln's table, a's own relations and its SenseFilter in them.
    /// A creature's own relations and filter answers count from the next step. Tick thread.</summary>
    public static (bool Fights, bool Friends) Between(EntityId a, EntityId b)
    {
        var t = Require("Factions.Between");
        RequireTick("Factions.Between");
        uint h = 0, f = 0;
        AiNative.Check(t->Query(a.Value, b.Value, &h, &f), $"Factions.Between({a}, {b})");
        return (h != 0, f != 0);
    }

    /// <summary>What kiln.npcai reports, or null without it.</summary>
    public static FactionStatus? Status
    {
        get
        {
            var t = AiNative.T;
            if (t == null) return null;
            var s = new AiNative.Status { Size = (uint)sizeof(AiNative.Status) };
            if (t->GetStatus(&s) != AiNative.Ok) return null;
            return new FactionStatus((s.Resolved & 31) == 31 && (s.Reserved & 1) == 0, (s.Installed & 1) != 0, (int)s.Relations, (int)s.Rows, (int)s.Pairs, (int)s.Mapped, s.Calls, s.Answered,
                s.ByPair, s.ByRow, s.ByTable);
        }
    }

    private static void Set(Faction a, Faction b, Relation r)
    {
        if (a == Faction.None || b == Faction.None) throw new ArgumentException("Faction.None is not a faction");
        var t = Require("Factions.Relate");
        RequireTick("Factions.Relate");
        var mod = CallerMod("Factions.Relate");
        AiRuntime.Watch(mod);
        AiNative.Check(t->RelationSet(AiNative.Owner(mod), (byte)a, (byte)b, (uint)r), $"Factions.Relate({a}, {b}, {r})");
    }

    internal static AiNative.Table* Require(string api)
    {
        var t = AiNative.T;
        return t != null ? t : throw new NotSupportedException($"{api}: kiln.npcai is not loaded (an unknown game build, or not a server world)");
    }

    private static void RequireTick(string api) => Game.AssertTickThread();

    private static string CallerMod(string api) => BlocksBridge.CallerModId ?? throw new InvalidOperationException($"{api}: call it from mod code (relations belong to a mod)");
}
