// SPDX-License-Identifier: MIT
// A weapon's moves: what each of its attack slots plays, as an author writes it. The same shape in C# and blocks/*.json:
//   "moves": { "primary": [ { "animation": "cinder-slash-1", "like": "Attack_Player_1H_Single_LightChain1", "hitAt": 0.28 }, { … } ],
//              "heavy": { "animation": "cinder-cleave", "hitAt": 0.9, "length": 2.2 }, "block": { "animation": "cinder-guard" } }
// What Kiln makes of it is in MoveCompiler.cs; docs/kiln/modding/item-blocks.md, "Moves".
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What a weapon's attack slots play, slot by slot. A slot left out keeps its class's. <see cref="Primary"/> and <see cref="Heavy"/> take one move or a
/// chain (<c>Primary = [first, second]</c>): each link hands over to the next, the last to the first. A slot the weapon's class does not have is refused by name.</summary>
public sealed record WeaponMoves
{
    /// <summary>The light attack on the ground, one move or a chain.</summary>
    public MoveChain? Primary { get; init; }
    /// <summary>The heavy attack on the ground, one move or a chain.</summary>
    public MoveChain? Heavy { get; init; }
    /// <summary>The heavy attack in the air. Null: the first link of <see cref="Primary"/> when that is given, as the game's weapons have it; else the class's.</summary>
    public MoveBlock? HeavyAir { get; init; }
    /// <summary>The attack out of a dodge.</summary>
    public MoveBlock? DodgeAttack { get; init; }
    /// <summary>The attack in a jump, before the fall.</summary>
    public MoveBlock? JumpAttack { get; init; }
    /// <summary>The heavy attack falling onto what is below.</summary>
    public MoveBlock? PlungeAttack { get; init; }
    /// <summary>The block. It lands no blow, so it takes no <see cref="MoveBlock.HitAt"/>.</summary>
    public MoveBlock? Block { get; init; }
    /// <summary>The parry. It lands no blow, so it takes no <see cref="MoveBlock.HitAt"/>.</summary>
    public MoveBlock? Parry { get; init; }
    /// <summary>The special ability, once a player has the game's skill for it.</summary>
    public MoveBlock? Special { get; init; }
    /// <summary>The finishing blow on a staggered enemy.</summary>
    public MoveBlock? Execution { get; init; }
    /// <summary>The attack from sneaking.</summary>
    public MoveBlock? SneakAttack { get; init; }
}

/// <summary>One move: the motion an attack slot plays, when its blow lands, and where its follow-up windows lead. Each field is optional, but a move with none of them changes nothing and is refused.</summary>
public sealed record MoveBlock
{
    /// <summary>The state the move's motion plays: a state of this mod by name, a game state by name (<c>"State"</c> or <c>"Machine/State"</c>), by its GUID, or by its id
    /// <c>"0x1234abcd"</c> (the catalog's <c>graphs</c>, docs/kiln/modding/catalogs.md). A list names each of the motion's state events in turn (a plunge has three:
    /// start, fall, landing); a null in it keeps that one. One name is the first. Null: the motion's own.</summary>
    public MoveAnimation? Animation { get; init; }
    /// <summary>The game sequence this move starts from: a sub-sequence name or a sequence GUID. Null: what the class has in this slot (for the n-th link of a chain,
    /// the class's own chain followed n-1 times).</summary>
    public string? Like { get; init; }
    /// <summary>Seconds into the move its blow lands: the first damage event moves here, and what comes before it is scaled to fit. Null: the motion's own.</summary>
    public double? HitAt { get; init; }
    /// <summary>Seconds the whole move lasts: what comes after the blow is scaled to end here. Null: the motion's own, shifted with <see cref="HitAt"/>.</summary>
    public double? Length { get; init; }
    /// <summary>Where the motion's follow-up windows lead, when not where the class has them: each entry points the windows for one input at another move
    /// of this weapon (a block, on a parried blow, into the weapon's own parry). A weapon with any applies all its moves at once or none of them:
    /// <see cref="Blocks.MovesStatus"/> says which. At most 64 per move. Null: the windows stay (a primary and a heavy chain still link up by themselves).</summary>
    [RequiresCapability("blocks.move_followups")]
    public IReadOnlyList<MoveFollowUpBlock>? FollowUps { get; init; }
}

/// <summary>One move or a chain of them: <c>Primary = move</c>, <c>Primary = [first, second]</c>, in JSON an object or a list. One move and a list of one are the same.</summary>
[JsonConverter(typeof(MoveChainConverter))]
public sealed class MoveChain : IEnumerable<MoveBlock>
{
    private readonly List<MoveBlock> _moves = new();
    public MoveChain() { }
    /// <summary>A chain of these moves, in order.</summary>
    public MoveChain(IEnumerable<MoveBlock> moves) => _moves.AddRange(moves);
    /// <summary>Adds a move at the end (collection initializers use this).</summary>
    public void Add(MoveBlock move) => _moves.Add(move);
    /// <summary>The number of moves.</summary>
    public int Count => _moves.Count;
    /// <summary>The move at <paramref name="i"/>, from 0.</summary>
    public MoveBlock this[int i] => _moves[i];
    /// <summary>The moves in order.</summary>
    public IEnumerator<MoveBlock> GetEnumerator() => _moves.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>A chain of one move.</summary>
    public static implicit operator MoveChain(MoveBlock move) => new() { move };
    /// <summary>A chain of these moves, in order.</summary>
    public static implicit operator MoveChain(MoveBlock[] moves) => new(moves);
}

/// <summary>The states a move plays: one name (<c>Animation = "cinder-guard"</c>) or one per state event of the motion, in order (<c>Animation = ["start", null, "land"]</c>,
/// a null keeping that one). In JSON a string or a list.</summary>
[JsonConverter(typeof(MoveAnimationConverter))]
public sealed class MoveAnimation : IEnumerable<string?>
{
    private readonly List<string?> _names = new();
    public MoveAnimation() { }
    /// <summary>These state names, one per state event in order; a null keeps that one.</summary>
    public MoveAnimation(IEnumerable<string?> names) => _names.AddRange(names);
    /// <summary>Adds a state name at the end (collection initializers use this); null keeps that state.</summary>
    public void Add(string? name) => _names.Add(name);
    /// <summary>The number of names.</summary>
    public int Count => _names.Count;
    /// <summary>The name at <paramref name="i"/>, from 0; null keeps that state.</summary>
    public string? this[int i] => _names[i];
    /// <summary>The names in order.</summary>
    public IEnumerator<string?> GetEnumerator() => _names.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>One state name for the whole move.</summary>
    public static implicit operator MoveAnimation(string name) => new() { name };
    /// <summary>These state names, in order.</summary>
    public static implicit operator MoveAnimation(string?[] names) => new(names);
    /// <summary>The one name, or the list as <c>[a, null, b]</c>.</summary>
    public override string ToString() => _names.Count == 1 ? _names[0] ?? "" : $"[{string.Join(", ", _names.Select(n => n ?? "null"))}]";
}

internal sealed class MoveChainConverter : JsonConverter<MoveChain>
{
    public override MoveChain? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.StartArray) return new MoveChain(JsonSerializer.Deserialize<List<MoveBlock>>(ref r, o) ?? []);
        return JsonSerializer.Deserialize<MoveBlock>(ref r, o) is { } one ? new MoveChain { one } : null;
    }

    public override void Write(Utf8JsonWriter w, MoveChain v, JsonSerializerOptions o)
    {
        if (v.Count == 1) JsonSerializer.Serialize(w, v[0], o);
        else JsonSerializer.Serialize(w, v.ToList(), o);
    }
}

internal sealed class MoveAnimationConverter : JsonConverter<MoveAnimation>
{
    public override MoveAnimation? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new MoveAnimation { r.GetString() };
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("an animation is a state's name, GUID or 0x id, or a list of them");
        return new MoveAnimation(JsonSerializer.Deserialize<List<string?>>(ref r, o) ?? []);
    }

    public override void Write(Utf8JsonWriter w, MoveAnimation v, JsonSerializerOptions o)
    {
        if (v.Count == 1 && v[0] != null) w.WriteStringValue(v[0]);
        else JsonSerializer.Serialize(w, v.ToList(), o);
    }
}

/// <summary>A premade motion with its key, or an object that starts from one and changes it: <c>"heavy"</c>, <c>{ "like": "heavy", "animation": …, "hitAt": 1.1 }</c>.
/// In C#: <c>WandMoves.Heavy with { HitAt = 1.1 }</c>. An object with no <c>like</c> starts from the premade the place has when nothing is said.</summary>
internal sealed class MotionConverter<T> : JsonConverter<T> where T : class, IPremade
{
    private static IReadOnlyList<T> All => (IReadOnlyList<T>)typeof(T).GetField("All", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    private static T Find(string? key) => All.FirstOrDefault(x => x.Key == key)
        ?? throw new JsonException($"\"{key}\" is not a {typeof(T).Name}; there are {string.Join(", ", All.Select(x => x.Key))}. Another sequence is not taken here: its shots are not known");

    public override T? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return Find(r.GetString());
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException($"a {typeof(T).Name} is a premade's key or an object with like, animation and hitAt");
        using var doc = JsonDocument.ParseValue(ref r);
        string? like = null;
        MoveAnimation? animation = null;
        double? hitAt = null;
        foreach (var p in doc.RootElement.EnumerateObject())
            switch (p.Name)
            {
                case "like": like = p.Value.GetString(); break;
                case "animation": animation = p.Value.Deserialize<MoveAnimation>(o); break;
                case "hitAt": hitAt = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetDouble(); break;
                default: throw new JsonException($"\"{p.Name}\" is not a field of a {typeof(T).Name}: like, animation, hitAt");
            }
        object made = typeof(T) == typeof(WandMoves)
            ? (like == null ? WandMoves.Unset : (WandMoves)(object)Find(like)) with { Animation = animation, HitAt = hitAt }
            : (like == null ? CastAnimation.FastBolt : (CastAnimation)(object)Find(like)) with { Animation = animation, HitAt = hitAt };
        return (T)made;
    }

    public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o)
    {
        var (animation, hitAt) = v switch { WandMoves m => (m.Animation, m.HitAt), CastAnimation c => (c.Animation, c.HitAt), _ => (null, null) };
        if (animation == null && hitAt == null)
        {
            w.WriteStringValue(v.Key);
            return;
        }
        w.WriteStartObject();
        if (v.Key.Length > 0) w.WriteString("like", v.Key);
        if (animation != null)
        {
            w.WritePropertyName("animation");
            JsonSerializer.Serialize(w, animation, o);
        }
        if (hitAt is { } h) w.WriteNumber("hitAt", h);
        w.WriteEndObject();
    }
}
