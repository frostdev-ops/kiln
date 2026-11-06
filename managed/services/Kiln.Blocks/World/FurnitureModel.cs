// SPDX-License-Identifier: MIT
// Things a player uses: what happens when they do (InteractBlock), and the furniture that has a use of its own: a container, a door, a bed, a seat. C# records, and the same shape in
// blocks/*.json. Each starts from a game item of its kind, chosen here; Visual.Like names another. docs/kiln/modding/world-blocks.md.
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What happens when a player uses a placed thing. A field left out is what its starter does: a door opens and closes, a bed is slept in. The game's own interaction does the work
/// (its prompt, what the player plays, a lock the game checks); <see cref="Effects"/> and the player checks in <see cref="Requires"/> are Kiln's, on the server.</summary>
public sealed record InteractBlock : IBlock
{
    /// <summary>The prompt's word: one of the game's (<see cref="InteractVerb.Open"/>, <see cref="InteractVerb.Sit"/>, ...), so every language has it.</summary>
    public InteractVerb? Verb { get; init; }

    /// <summary>A prompt of your own instead: the same text in every language.</summary>
    public string? Prompt { get; init; }

    /// <summary>What the player plays when they use it: one of the game's (<see cref="InteractPlay.Sit"/>, <see cref="InteractPlay.Sleep"/>, ...), or a sequence's GUID.</summary>
    public InteractPlay? Plays { get; init; }

    /// <summary>Two states, and each use flips it: the game's toggle, as a door's. The guest's sequence flips it, so a toggle with <see cref="Plays"/> plays one that does.</summary>
    public ToggleBlock? Toggle { get; init; }

    /// <summary>What it takes to use it. Refused, the game's interaction is declined and the player's sequence stops.</summary>
    public RequiresBlock? Requires { get; init; }

    /// <summary>What happens to the player once the game has accepted the use.</summary>
    public IReadOnlyList<EffectRef>? Effects { get; init; }

    /// <summary>What happens to the player when <see cref="Requires"/> refuses them.</summary>
    public IReadOnlyList<EffectRef>? OnRefused { get; init; }
}

/// <summary>A toggle's prompts, and how it starts. A prompt left out is its starter's: a door's open and close.</summary>
public sealed record ToggleBlock
{
    /// <summary>The prompt while it is off: one of the game's words.</summary>
    public InteractVerb? On { get; init; }
    /// <summary>The prompt while it is on: one of the game's words.</summary>
    public InteractVerb? Off { get; init; }
    /// <summary>A prompt of your own while it is off, the same in every language. Wins over <see cref="On"/>.</summary>
    public string? OnPrompt { get; init; }
    /// <summary>A prompt of your own while it is on. Wins over <see cref="Off"/>.</summary>
    public string? OffPrompt { get; init; }
    /// <summary>It is on when placed: a door stands open.</summary>
    public bool StartsOn { get; init; }
}

/// <summary>When a player may use it. <see cref="Item"/>, <see cref="Uses"/> and <see cref="Time"/> are the game's own lock, so a player's game shows it locked; <see cref="When"/>,
/// <see cref="Unless"/> and <see cref="If"/> are checked by Kiln on the server, which declines the use the way the game does.</summary>
public sealed record RequiresBlock
{
    /// <summary>An item in the player's bag: a <c>GameItems</c> constant or an item of this mod.</summary>
    public ItemRef? Item { get; init; }
    /// <summary>How many of <see cref="Item"/>.</summary>
    public int Count { get; init; } = 1;
    /// <summary>The use takes <see cref="Item"/> from the bag, as a lockpick goes.</summary>
    public bool Uses { get; init; }
    /// <summary>Only by day, or only by night.</summary>
    [JsonConverter(typeof(NamedNullableConverter<DayPart>))]
    public DayPart? Time { get; init; }
    /// <summary>Only while the player is in one of these states.</summary>
    public IReadOnlyList<PlayerStateRef>? When { get; init; }
    /// <summary>Not while the player is in any of these.</summary>
    public IReadOnlyList<PlayerStateRef>? Unless { get; init; }
    /// <summary>From code: your own check, on the tick thread.</summary>
    [JsonIgnore]
    public Func<Player, bool>? If { get; init; }
}

/// <summary>Day (the game's day phase, from 06:00 by default) or night.</summary>
public enum DayPart { Day, Night }

/// <summary>A prompt's word: the game's own, so every language has it. <see cref="Id"/> is the game's loca tag; <see cref="Reference"/> its GUID, where the game keeps one.</summary>
[JsonConverter(typeof(PremadeConverter<InteractVerb>))]
public sealed record InteractVerb(string Key, uint Id, string? Reference = null) : IPremade
{
    public static readonly InteractVerb Open = new("open", 0x4e82e309, "0af70198-e619-46df-a6ce-65fb60878a59");
    public static readonly InteractVerb Close = new("close", 0x3959a87f, "da5990c2-f8f0-413c-bb4d-fe057f4b4ca1");
    public static readonly InteractVerb Use = new("use", 0xc22a47e8, "6f9f9f8b-b23a-48e2-a684-86f41cebddf1");
    public static readonly InteractVerb Activate = new("activate", 0xc3d944f7, "b7a2ef75-8469-4c5e-8268-b009016713a0");
    public static readonly InteractVerb Deactivate = new("deactivate", 0x4ac71b17, "2442f0e9-f3b8-4a68-bf0a-2abdbe57dd50");
    public static readonly InteractVerb Flip = new("flip", 0x65bbaf0f, "4ebf2d0d-a845-4d04-8049-39212ece043a");
    public static readonly InteractVerb Read = new("read", 0x631937dc, "b6287321-1aa4-4eb9-a7e6-85cfa241e1c9");
    public static readonly InteractVerb Sit = new("sit", 0xdfb7e778);
    public static readonly InteractVerb Sleep = new("sleep", 0xc4e0f1cb);
    public static readonly InteractVerb Search = new("search", 0x9a599d0b);
    public static readonly InteractVerb Inspect = new("inspect", 0x260798eb);
    public static readonly InteractVerb Talk = new("talk", 0xd9861c4b);
    public static readonly InteractVerb Craft = new("craft", 0xe0a9e31d);
    public static readonly InteractVerb Collect = new("collect", 0x9095e8bd);
    public static readonly InteractVerb Harvest = new("harvest", 0x5e09f8d2);
    public static readonly InteractVerb Drink = new("drink", 0xc2f79d24);
    internal static readonly InteractVerb[] All = [Open, Close, Use, Activate, Deactivate, Flip, Read, Sit, Sleep, Search, Inspect, Talk, Craft, Collect, Harvest, Drink];
}

/// <summary>What a player plays when they use something: one of the game's sequences, or any sequence by GUID (<c>new InteractPlay("mine", guid)</c>; in JSON the GUID).</summary>
[JsonConverter(typeof(InteractPlayConverter))]
public sealed record InteractPlay(string Key, Guid Sequence) : IPremade
{
    /// <summary>Sits down on the spot the seat says: <c>Interaction_Sit</c>.</summary>
    public static readonly InteractPlay Sit = new("sit", new("b5e65ebc-c1fc-4fef-a127-d133512fa701"));
    /// <summary>Lies down and sleeps, the game's rest: <c>Interaction_Sleep</c>.</summary>
    public static readonly InteractPlay Sleep = new("sleep", new("5bd75f9b-d11f-428c-be3a-3c84653953cf"));
    /// <summary>A hand on it: <c>Interaction_Toggle</c>, a door's.</summary>
    public static readonly InteractPlay Toggle = new("toggle", new("241feba6-9ea8-43b2-b10f-e93f3573a697"));
    /// <summary>Opens a chest and its inventory screen: <c>Interaction_InventoryUi_StorageChest</c>.</summary>
    public static readonly InteractPlay Chest = new("chest", new("18078bb8-3071-421c-bce6-43ac7a77b67c"));
    /// <summary>Opens the inventory screen: <c>Interaction_InventoryUi</c>.</summary>
    public static readonly InteractPlay Inventory = new("inventory", new("982133d3-39f4-45ca-a333-14b88556fb42"));
    /// <summary><c>Interaction_Read_Lore</c>.</summary>
    public static readonly InteractPlay Read = new("read", new("de1779b3-7bf7-4c9a-97da-54f97c52ca9c"));
    /// <summary><c>Interaction_Talk</c>.</summary>
    public static readonly InteractPlay Talk = new("talk", new("76b18061-d903-44fd-8401-8e4b58c6ff25"));
    /// <summary><c>Interaction_Drink_from_Container</c>, a well's.</summary>
    public static readonly InteractPlay Drink = new("drink", new("65d657c8-66e9-43b5-af5a-d0c80739dcdc"));
    internal static readonly InteractPlay[] All = [Sit, Sleep, Toggle, Chest, Inventory, Read, Talk, Drink];

    /// <summary>The game's id of the sequence: FNV-1a of its GUID.</summary>
    public uint Id => BlockIds.Fnv(Sequence);
}

/// <summary>Where on the thing the player sits or lies, in its own axes: metres from its origin, and which way they face, in degrees about the vertical (0: its +z).</summary>
public sealed record SpotBlock
{
    public Vector3 At { get; init; }
    public double Facing { get; init; }
}

/// <summary>Something that holds items, as the game's chests do. It starts from the game's small chest: its lid, its sounds, its upgrades.</summary>
public sealed record ContainerBlock : PropBlock
{
    /// <summary>Inventory slots, 1..120. Null: the starter's.</summary>
    public int? Slots { get; init; }

    /// <summary>What it holds when it is first placed: item and count. Once: what a player takes out isn't put back, after a restart either.</summary>
    public IDictionary<ItemRef, int> Holds { get; init; } = new Dictionary<ItemRef, int>();
}

/// <summary>A door: it opens and closes for whoever uses it, and paths and water follow it, as the game's doors do. It starts from the game's wooden door.</summary>
public sealed record DoorBlock : PropBlock;

/// <summary>A bed: the game's sleep and rest (a player who sleeps in it gets the game's rested status, by the comfort around them), and comfort of the bed category. It starts from the game's wooden bed.
/// <see cref="SpawnPoint"/> and <see cref="SkipsNight"/> are Kiln's, on the server: the game's beds do neither.</summary>
public sealed record BedBlock : PropBlock
{
    /// <summary>Where the player lies. Null: the starter's spot, which fits its model.</summary>
    public SpotBlock? Lies { get; init; }
    /// <summary>A player who dies comes back at the last of these beds they slept in, instead of at the flame altar.</summary>
    public bool SpawnPoint { get; init; }
    /// <summary>When every player in the world sleeps at night, in beds that say this, the morning comes.</summary>
    public bool SkipsNight { get; init; }
}

/// <summary>A seat: the game's sitting, and comfort of the chair category. It starts from the game's wooden chair.</summary>
public sealed record SeatBlock : PropBlock
{
    /// <summary>Where the player sits. Null: the starter's spot, which fits its model.</summary>
    public SpotBlock? Sits { get; init; }
}

internal sealed class InteractPlayConverter : JsonConverter<InteractPlay>
{
    public override InteractPlay? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var text = r.GetString();
        if (InteractPlay.All.FirstOrDefault(x => x.Key == text) is { } premade) return premade;
        if (Guid.TryParse(text, out var g)) return new InteractPlay(text!, g);
        throw new JsonException($"\"{text}\" is not an InteractPlay; there are {string.Join(", ", InteractPlay.All.Select(x => x.Key))}, or a sequence's GUID");
    }

    public override void Write(Utf8JsonWriter w, InteractPlay v, JsonSerializerOptions o) => w.WriteStringValue(InteractPlay.All.Contains(v) ? v.Key : v.Sequence.ToString());
}

/// <summary>An optional enum by its camel-case name; a mistake lists the names.</summary>
internal sealed class NamedNullableConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    public override T? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.Null ? null : new NamedEnumConverter<T>().Read(ref r, typeof(T), o);

    public override void Write(Utf8JsonWriter w, T? v, JsonSerializerOptions o)
    {
        if (v is { } x) w.WriteStringValue(NamedEnumConverter<T>.Name(x));
        else w.WriteNullValue();
    }
}

internal static class FurnitureJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("containers", BlockJson.Make<ContainerBlock>), ("doors", BlockJson.Make<DoorBlock>), ("beds", BlockJson.Make<BedBlock>), ("seats", BlockJson.Make<SeatBlock>),
        ("interactions", BlockJson.Make<InteractBlock>), ("blueprints", BlockJson.Make<BlueprintBlock>), ("hair", BlockJson.Make<HairBlock>), ("beards", BlockJson.Make<BeardBlock>),
    ];
}
