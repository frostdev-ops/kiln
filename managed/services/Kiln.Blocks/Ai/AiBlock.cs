// SPDX-License-Identifier: MIT
// What an AIBlock says beyond sight, aggro and leash (NpcModel.cs): its night sight and hearing, how it roams and flees, its feature switches, who it
// treats as an enemy (its own relations to factions, and a SenseFilter for single creatures and players), on the game's own AI. Each creature of the
// block gets its own copy of the AI's values (kiln.creatures' private copies), so nothing here changes the game's other creatures.
// docs/kiln/modding/npc-ai.md.
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What a creature's check about another answers: does it fight it, is it on its side. The game's own answer unless set.</summary>
public enum Relation
{
    /// <summary>The game's own answer (equal factions and the players' side are friends; most others fight).</summary>
    Game = 0,
    /// <summary>It fights them.</summary>
    Hostile = 1,
    /// <summary>They are on its side: it does not fight them, and it helps them as the game's friends do.</summary>
    Friendly = 2,
    /// <summary>It ignores them: no fight, not on its side.</summary>
    Neutral = 3,
}

/// <summary>How a creature runs from a fight: below a share of its health, as far as the game's flee goes. The game's <c>fleeSetup</c> and its Flee state.</summary>
public sealed record FleeBlock
{
    /// <summary>0..1: it runs once its health is at or below this share. Null: only when a rule says so (<c>Act.Flee()</c>).</summary>
    public double? Below { get; init; }
    /// <summary>Metres it runs at least (<c>fleeSetup.minFleeRadius</c>).</summary>
    public double? Min { get; init; }
    /// <summary>Metres it runs at most (<c>fleeSetup.maxFleeRadius</c>).</summary>
    public double? Max { get; init; }
    /// <summary>Seconds it runs at most before it turns again (<c>fleeSetup.maxFleeDuration</c>).</summary>
    public double? Seconds { get; init; }
    /// <summary>Seconds before <see cref="Below"/> can make it run again.</summary>
    public double Cooldown { get; init; } = 20;
}

/// <summary>How a creature wanders while it has nothing to do. The game's <c>roamingSetup</c>; it turns the game's Roaming on.</summary>
public sealed record RoamBlock
{
    /// <summary>Metres of the shortest walk.</summary>
    public double? Min { get; init; }
    /// <summary>Metres of the longest walk.</summary>
    public double? Max { get; init; }
    /// <summary>How far from home it may get while roaming (<c>roamingSetup.maxHomeZoneDistance</c>).</summary>
    public double? FromHome { get; init; }
}

public sealed partial record AIBlock
{
    /// <summary>0..1: the share of <see cref="Sight"/> it sees at night (<c>combatCommitmentSetup.view.nightRangeFactor</c>; the Vukah's 0.6).</summary>
    public double? NightSight { get; init; }
    /// <summary>False: it does not react to noise (<c>noiseListenerSetup.isEnabled</c>).</summary>
    public bool? Hears { get; init; }
    /// <summary>How far it hears, as a share of the game's noise ranges, 1 as the game has it (<c>noiseListenerSetup.listenRangeFactor</c>).</summary>
    public double? Hearing { get; init; }
    /// <summary>How much it draws enemies' attention, 1 as the game has it (<c>threatFactor</c>).</summary>
    public double? Threat { get; init; }
    /// <summary>How it wanders. Null: as its template does.</summary>
    public RoamBlock? Roam { get; init; }
    /// <summary>How it runs from a fight. Null: as its template does.</summary>
    public FleeBlock? Flee { get; init; }
    /// <summary>False: it never walks home after a fight (the game's ReturnHome and FleeHome off).</summary>
    public bool? ReturnsHome { get; init; }
    /// <summary>True: it calls the friends around it into its fights (the game's CallForHelp); false: it never does.</summary>
    public bool? CallsForHelp { get; init; }
    /// <summary>Metres its call reaches (<c>callForHelpSetup.radius</c>).</summary>
    public double? HelpRadius { get; init; }
    /// <summary>The game's feature switches to turn on, beyond what the other fields say (<c>EnemyFeatures.Prowl | EnemyFeatures.Circling</c>).</summary>
    public EnemyFeatures? Enable { get; init; }
    /// <summary>The game's feature switches to turn off.</summary>
    public EnemyFeatures? Disable { get; init; }

    /// <summary>
    /// Who it fights, by faction, for this creature only: <c>{ [Faction.Player] = Relation.Neutral, [Faction.Scavenger] = Relation.Hostile }</c>. Its own
    /// view: the others' views of it stay theirs (<see cref="Factions.Relate"/> changes the world's). Needs kiln.npcai.
    /// </summary>
    public IDictionary<Faction, Relation>? Relations { get; init; }

    /// <summary>
    /// Decides, for one creature or player it could notice, whether it treats it as an enemy: true fights as the game would, false leaves it alone.
    /// Runs on the tick thread every <see cref="SenseEvery"/> seconds for what is within its sight; the game's targeting reads the answer. In JSON a
    /// name: <c>Blocks.SenseFilter("name", (npc, other) =&gt; ...)</c>. Needs kiln.npcai.
    /// </summary>
    [JsonIgnore]
    public Func<NpcView, EntityId, bool>? SenseFilter { get; init; }
    /// <summary>In JSON: a sense filter registered by name with <c>Blocks.SenseFilter</c>.</summary>
    [JsonPropertyName("senseFilter")]
    public string? SenseFilterName { get; init; }
    /// <summary>Seconds between a SenseFilter's looks. Default 0.5.</summary>
    public double? SenseEvery { get; init; }
}
