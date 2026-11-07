// SPDX-License-Identifier: MIT
// The parts of an effect and of a status beyond damage and areas: what is restored, what is spawned, which sound, which stats change.
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What a block of this type asks of players' games, unless one of its fields asks for more. The build works out a mod's
/// <c>clients</c> from these, and lint and analyzers check what a project says. A type without it: <see cref="ClientsLevel.Kiln"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BlockClientsAttribute(ClientsLevel level) : Attribute
{
    public ClientsLevel Level { get; } = level;
}

/// <summary>A field whose value can name the mod's own file or cue, which has to be on every player's game: a value that matches this regular
/// expression asks for <see cref="ClientsLevel.Mod"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ClientsModWhenAttribute(string pattern) : Attribute
{
    public string Pattern { get; } = pattern;
}

/// <summary>A block that lets its author go below it: game fields it does not cover, set on what it made. Written
/// <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>; in JSON <c>"tweak": { "maxStackSize": 5 }</c>. Applied after what the block sets, so
/// it wins. A field of another section than what the block makes is an error.</summary>
public interface ITweakable
{
    /// <summary>The game fields to set on what the block made.</summary>
    FieldValues Tweak { get; }
}

/// <summary>Mana, stamina and health given back. <c>Restore.Mana(20)</c>, <c>Restore.Mana(20) + Restore.Stamina(10)</c>.
/// More than the target's maximum is lost; a target without that attribute (most creatures have no mana) gets nothing.</summary>
public sealed record RestoreBlock
{
    public int Health { get; init; }
    public int Mana { get; init; }
    public int Stamina { get; init; }

    public static RestoreBlock operator +(RestoreBlock a, RestoreBlock b) => new()
    {
        Health = a.Health + b.Health, Mana = a.Mana + b.Mana, Stamina = a.Stamina + b.Stamina,
    };
}

/// <summary>Shorthands for <see cref="RestoreBlock"/>.</summary>
public static class Restore
{
    public static RestoreBlock Health(int n) => new() { Health = n };
    public static RestoreBlock Mana(int n) => new() { Mana = n };
    public static RestoreBlock Stamina(int n) => new() { Stamina = n };
}

/// <summary>Something to spawn: a <c>GameTemplates</c> constant, or a template's GUID. It starts as a child of what spawned it, at the
/// same level and of the same faction, and lives on after it.</summary>
[JsonConverter(typeof(TemplateRefConverter))]
public readonly record struct TemplateRef(string Value)
{
    public static implicit operator TemplateRef(string value) => new(value);
    public static implicit operator TemplateRef(Guid guid) => new(guid.ToString());
    public override string ToString() => Value;
}

/// <summary>A sound: a <see cref="SoundBlock"/> or a cue of this mod's cook recipe by name, <c>"other-mod:name"</c>, or a cue's GUID (<c>GameSounds.Guids</c>).
/// A cue of the mod's own needs the mod on every client (<c>clients: "mod"</c>).</summary>
[JsonConverter(typeof(SoundRefConverter))]
public readonly record struct SoundRef(string Value)
{
    public static implicit operator SoundRef(string value) => new(value);
    public static implicit operator SoundRef(Guid guid) => new(guid.ToString());
    /// <summary>A <see cref="SoundBlock"/> of this mod, by its handle.</summary>
    public static implicit operator SoundRef(BlockHandle<SoundBlock> sound) => new(sound.Guid.ToString());
    public override string ToString() => Value;
}

/// <summary>A place on a body: the game's attachment slots.</summary>
public enum VfxSlot { Root, Head, Chest, LeftHand, RightHand, Feet, Weapon }

/// <summary>A VFX and where it shows: the slot on every field that shows one (<see cref="EffectBlock.Vfx"/>, <see cref="CastBlock.Vfx"/>,
/// <see cref="ProjectileBlock.Vfx"/>, <see cref="StatusBlock.Vfx"/>, <see cref="AreaBlock.Vfx"/>, an item's on-hit). A name in JSON or C# is a <c>VfxBlock</c> of this mod,
/// <c>"other-mod:name"</c> for another mod's; <see cref="Game"/> names one of the game's. A new VFX needs <c>clients: "mod"</c>; one of the game's needs no more than the block does.</summary>
[JsonConverter(typeof(VfxRefConverter))]
public sealed record VfxRef
{
    /// <summary>A name that is a new VFX, not one of the game's: the build reads it for what a mod asks of players' games.</summary>
    internal const string OwnName = "^(?!game:)(?![0-9a-fA-F]{8}-)[^ ]+$";

    public VfxRef(string name) => Name = name;

    /// <summary>The block's name, <c>"mod:name"</c>, or <c>"game:name"</c>.</summary>
    public string Name { get; init; }
    /// <summary>Where on the body it shows, where there is a body. Null: the field's own place, a cast's hand and otherwise the root.</summary>
    public VfxSlot? Slot { get; init; }
    /// <summary>Metres from the slot, in the slot's own axes.</summary>
    public Vector3 Offset { get; init; }
    /// <summary>Follows the slot while it plays (true), or stays where it started (false). What an effect spawns stays where the effect happened.</summary>
    public bool Attach { get; init; } = true;
    /// <summary>On an effect, how long it plays. Null: the VFX's own length; the template's for a game VFX named after one, else 5 s.</summary>
    public double? Seconds { get; init; }
    /// <summary>Named parameters. A game VFX's are its own (<c>["radius"] = 3f, ["color"] = "#ff0000"</c>; a name or type it does not have does nothing). A Kiln VFX (a <c>VfxBlock</c>)
    /// takes five play settings: <c>color</c>, <c>size</c>, <c>speed</c>, <c>brightness</c>, <c>count</c> (<see cref="VfxParameterBlock.Names"/>). An effect, a cast and a status can set them;
    /// a projectile's, a look's or a prop's VFX refuses them. An effect's parameters reach Kiln clients near it through <see cref="Vfx.PlayAt"/>'s relay, not a joiner.</summary>
    public Dictionary<string, VfxValue>? Parameters { get; init; }

    /// <summary>One of the game's VFX, by its catalog name (<c>GameVfx</c>).</summary>
    public static VfxRef Game(string name) => new($"game:{name}");
    public static implicit operator VfxRef(string name) => new(name);
    public static implicit operator VfxRef(BlockHandle handle) => new(handle.ToString());
}

/// <summary>A stat of a character. Premade instances name the game's attributes; <c>new Stat("my-key", guid, Whole: true)</c> names any other.</summary>
[JsonConverter(typeof(StatConverter))]
public sealed record Stat(string Key, Guid Attribute, bool Whole = false, bool Subtract = false) : IPremade
{
    private static Stat Points(string key, string guid) => new(key, new Guid(guid), Whole: true);
    private static Stat Share(string key, string guid) => new(key, new Guid(guid));
    private static Stat Resist(string key, string guid) => new(key, new Guid(guid), Subtract: true);

    // ---- the six attributes, whole points
    public static readonly Stat Strength = Points("strength", "64810163-59a0-4b55-82b9-5bb6290b22d7");
    public static readonly Stat Constitution = Points("constitution", "0b1ceec5-402d-43ff-9a12-43392d53cf26");
    public static readonly Stat Dexterity = Points("dexterity", "4be83310-d59c-4f6d-89dd-1e61e033db86");
    public static readonly Stat Endurance = Points("endurance", "6bd1a147-1ea7-441e-8c6e-6f8b68dbcbfe");
    public static readonly Stat Intelligence = Points("intelligence", "2609852d-a281-4176-804e-b2dc4b8eb8e8");
    public static readonly Stat Spirit = Points("spirit", "0e31beac-22c5-4572-a98a-e39dca2bd1f7");

    // ---- pools and how they refill, whole points
    public static readonly Stat MaxHealth = Points("max-health", "e57246cd-ed4f-40ee-81db-0ddf7710be1d");
    public static readonly Stat MaxMana = Points("max-mana", "8f8780ca-ff9f-4c5a-aadb-3aa0ee0ea3de");
    public static readonly Stat MaxStamina = Points("max-stamina", "904174b4-ef56-476f-b0e2-d5e00add14ca");
    public static readonly Stat HealthRegen = Points("health-regen", "5355a563-fb99-4165-bf0b-7588cc20502c");
    public static readonly Stat ManaRegen = Points("mana-regen", "20696ee7-ca28-403f-bb4f-5f08914dd977");
    public static readonly Stat StaminaRegen = Points("stamina-regen", "9f2613ca-57f1-47eb-bc56-a86133665d56");
    public static readonly Stat MaxOxygen = Points("max-oxygen", "e11444db-848e-4e34-9140-4c1b7b4015b6");
    public static readonly Stat ShroudTime = Points("shroud-time", "1b2d53e6-38ab-4236-9c45-100d4ea31cdb");

    // ---- shares: 0.1 is 10% more, -0.5 is half
    public static readonly Stat MoveSpeed = Share("move-speed", "75f222d4-8cb9-4e4b-bb99-73f692d78f07");
    public static readonly Stat SprintSpeed = Share("sprint-speed", "fb6d1030-dbd4-4c07-89e7-534d28b1a9c0");
    public static readonly Stat MeleeDamage = Share("melee-damage", "a673b002-e7f7-49ea-95db-334c72b418bc");
    public static readonly Stat RangedDamage = Share("ranged-damage", "63262131-9a59-4766-a1be-19bd5c0e81f7");
    public static readonly Stat MagicDamage = Share("magic-damage", "92f40832-6ae3-4bc2-b514-6dcc904eb25c");
    public static readonly Stat CritChance = Share("crit-chance", "df9b8dd0-f4e9-4751-ba27-52eb82e73db9");
    public static readonly Stat CritDamage = Share("crit-damage", "1a6ad674-9023-4647-b6f4-f50534bbfe5b");
    public static readonly Stat Healing = Share("healing", "a45270b3-b791-4dd7-906c-d75ca03f3689");
    public static readonly Stat Parry = Share("parry", "1d05303a-24d0-4bc1-b7f2-d81e599de00a");
    public static readonly Stat StaminaUseClimb = Share("stamina-use-climb", "533f9fc9-316e-466e-95e7-b2a7be0658af");
    public static readonly Stat StaminaUseGlide = Share("stamina-use-glide", "7d8a4cf2-9439-4bb7-b04d-4553f481fd61");
    public static readonly Stat StaminaUseSprint = Share("stamina-use-sprint", "36541843-3894-4203-86d6-a35936f09cb2");

    // ---- armour, points
    public static readonly Stat PhysicalArmor = Points("physical-armor", "68a64242-abf2-4839-bf4c-6c9aaa04dac4");
    public static readonly Stat MagicalArmor = Points("magical-armor", "48d6f3cd-212d-44a2-a26d-ce5e6885b377");
    public static readonly Stat Block = Points("block", "05ce03e9-0006-4668-82d4-58dd92988dbf");

    // ---- resistances: a share of that damage taken away (0.25 takes a quarter less, -0.25 a quarter more)
    public static readonly Stat ResistPhysical = Resist("resist-physical", "9e147516-5765-4c3a-9e75-7405ffee7e9e");
    public static readonly Stat ResistMagical = Resist("resist-magical", "7db690ee-6a37-4221-9516-bc8994f1db03");
    public static readonly Stat ResistCut = Resist("resist-cut", "3c40b8a1-6146-45e1-a5b4-96e26aa8f970");
    public static readonly Stat ResistPierce = Resist("resist-pierce", "763b78f6-21cc-4a16-8a7b-eee0ff735e3e");
    public static readonly Stat ResistBlunt = Resist("resist-blunt", "75bd2050-a925-498c-98fe-9f8ac8a64f07");
    public static readonly Stat ResistFire = Resist("resist-fire", "142aa1a6-a25c-47c6-b6c7-241f525f197f");
    public static readonly Stat ResistIce = Resist("resist-ice", "b4fce925-8793-47af-aa41-b14d72a7831c");
    public static readonly Stat ResistLightning = Resist("resist-lightning", "9335eca1-bd14-476d-b11c-d68ab9866e51");
    public static readonly Stat ResistShroud = Resist("resist-shroud", "12058b0b-fc5f-4c5d-ad6d-db83091c79c1");
    public static readonly Stat ResistPoison = Resist("resist-poison", "96f041eb-8611-4664-a826-0262abc52e58");

    internal static readonly Stat[] All =
    [
        Strength, Constitution, Dexterity, Endurance, Intelligence, Spirit,
        MaxHealth, MaxMana, MaxStamina, HealthRegen, ManaRegen, StaminaRegen, MaxOxygen, ShroudTime,
        MoveSpeed, SprintSpeed, MeleeDamage, RangedDamage, MagicDamage, CritChance, CritDamage, Healing, Parry,
        StaminaUseClimb, StaminaUseGlide, StaminaUseSprint,
        PhysicalArmor, MagicalArmor, Block,
        ResistPhysical, ResistMagical, ResistCut, ResistPierce, ResistBlunt, ResistFire, ResistIce, ResistLightning, ResistShroud, ResistPoison,
    ];
}

internal sealed class TemplateRefConverter : JsonConverter<TemplateRef>
{
    public override TemplateRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, TemplateRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

internal sealed class SoundRefConverter : JsonConverter<SoundRef>
{
    public override SoundRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, SoundRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

// "vfx": "burst", or { "name": "burst", "slot": "chest", "offset": [0, 1, 0], "attach": false }
internal sealed class VfxRefConverter : JsonConverter<VfxRef>
{
    public override VfxRef? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new VfxRef(r.GetString()!);
        using var doc = JsonDocument.ParseValue(ref r);
        string? name = null;
        VfxSlot? slot = null;
        var offset = Vector3.Zero;
        var attach = true;
        double? seconds = null;
        Dictionary<string, VfxValue>? parameters = null;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = p.Value.GetString(); break;
                case "slot": slot = p.Value.Deserialize<VfxSlot>(o); break;
                case "attach": attach = p.Value.GetBoolean(); break;
                case "seconds": seconds = p.Value.GetDouble(); break;
                case "parameters": parameters = p.Value.Deserialize<Dictionary<string, VfxValue>>(o); break;
                case "offset":
                    var a = p.Value.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    if (a.Length != 3) throw new JsonException("offset takes [x, y, z]");
                    offset = new Vector3(a[0], a[1], a[2]);
                    break;
                default: throw new JsonException($"\"{p.Name}\" is not part of a vfx; it takes name, slot, offset, attach, seconds and parameters");
            }
        }
        return new VfxRef(name ?? throw new JsonException("a vfx needs a name")) { Slot = slot, Offset = offset, Attach = attach, Seconds = seconds, Parameters = parameters };
    }

    public override void Write(Utf8JsonWriter w, VfxRef v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        w.WriteString("name", v.Name);
        if (v.Slot is { } slot)
        {
            w.WritePropertyName("slot");
            JsonSerializer.Serialize(w, slot, o);
        }
        w.WriteStartArray("offset");
        w.WriteNumberValue(v.Offset.X); w.WriteNumberValue(v.Offset.Y); w.WriteNumberValue(v.Offset.Z);
        w.WriteEndArray();
        w.WriteBoolean("attach", v.Attach);
        if (v.Seconds is { } seconds) w.WriteNumber("seconds", seconds);
        if (v.Parameters is { Count: > 0 } parameters)
        {
            w.WritePropertyName("parameters");
            JsonSerializer.Serialize(w, parameters, o);
        }
        w.WriteEndObject();
    }
}

// a premade's key ("move-speed"), or a GUID for an attribute with no premade (taken as a share; in C#, new Stat(...) says which)
internal sealed class StatConverter : JsonConverter<Stat>
{
    private static Stat Parse(string? s)
    {
        var known = Stat.All.FirstOrDefault(x => x.Key == s);
        if (known != null) return known;
        if (Guid.TryParse(s, out var g)) return new Stat(s!, g);
        throw new JsonException($"\"{s}\" is not a stat; there are {string.Join(", ", Stat.All.Select(x => x.Key))}");
    }

    public override Stat Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Parse(r.GetString());
    public override void Write(Utf8JsonWriter w, Stat v, JsonSerializerOptions o) => w.WriteStringValue(v.Key);
    public override Stat ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Parse(r.GetString());
    public override void WriteAsPropertyName(Utf8JsonWriter w, Stat v, JsonSerializerOptions o) => w.WritePropertyName(v.Key);
}

// "tweak": { "maxStackSize": 5 }: paths as a data pack writes them, of the section the block makes
internal abstract class TweakConverter(string section) : JsonConverter<FieldValues>
{
    public override FieldValues Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var values = new FieldValues();
        using var doc = JsonDocument.ParseValue(ref r);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("tweak takes an object of game field paths and values");
        foreach (var p in doc.RootElement.EnumerateObject())
            values.Add(new Field<System.Text.Json.Nodes.JsonNode>(section, p.Name), System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText())!);
        return values;
    }

    public override void Write(Utf8JsonWriter w, FieldValues v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        foreach (var (k, x) in v)
        {
            w.WritePropertyName(k);
            JsonSerializer.Serialize(w, x, o);
        }
        w.WriteEndObject();
    }
}

internal sealed class ItemTweakConverter() : TweakConverter("items");
internal sealed class BuffTweakConverter() : TweakConverter("buffs");
internal sealed class TemplateTweakConverter() : TweakConverter("templates");
