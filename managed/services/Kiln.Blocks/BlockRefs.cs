// SPDX-License-Identifier: MIT
// One rule for every block-typed field: an inline block, a handle from Blocks.Define, or a name (in JSON: a string). A name is a
// block of this mod, "other-mod:name" for another mod's.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What <see cref="Blocks.Define{T}"/> gives back: a typed reference to the block and what it became.</summary>
public abstract class BlockHandle
{
    internal BlockHandle(string mod, string name, Guid guid, string gameName)
    {
        Mod = mod;
        Name = name;
        Guid = guid;
        GameName = gameName;
    }

    /// <summary>The mod that defined it.</summary>
    public string Mod { get; }
    /// <summary>The block's name in that mod.</summary>
    public string Name { get; }
    /// <summary>The GUID of what it became in the game (an item, a buff). The same for ever for this mod id and block name.</summary>
    public Guid Guid { get; }
    /// <summary>The game's id of it (FNV-1a of the GUID).</summary>
    public uint Id => BlockIds.Fnv(Guid);
    /// <summary>The debug name it has in the game: what <c>Content.Items.Get</c>, <c>Items.Give</c> and data packs take.</summary>
    public string GameName { get; }

    public override string ToString() => $"{Mod}:{Name}";
}

/// <inheritdoc />
public sealed class BlockHandle<T> : BlockHandle where T : class, IBlock
{
    internal BlockHandle(string mod, string name, Guid guid, string gameName, T block) : base(mod, name, guid, gameName) => Block = block;

    /// <summary>The block as defined.</summary>
    public T Block { get; }
}

/// <summary>An effect: inline, from <see cref="Blocks.Define{T}"/>, or by name.</summary>
[JsonConverter(typeof(RefConverter<EffectRef, EffectBlock>))]
public sealed class EffectRef : IBlockRef<EffectBlock>
{
    internal EffectRef(EffectBlock? inline, string? name) { Inline = inline; Name = name; }
    public EffectBlock? Inline { get; }
    public string? Name { get; }
    public static implicit operator EffectRef(EffectBlock block) => new(block, null);
    public static implicit operator EffectRef(BlockHandle<EffectBlock> handle) => new(handle.Block, handle.ToString());
    public static implicit operator EffectRef(string name) => new(null, name);
    static IBlockRef<EffectBlock> IBlockRef<EffectBlock>.Make(EffectBlock? inline, string? name) => new EffectRef(inline, name);
}

/// <summary>A status: inline, from <see cref="Blocks.Define{T}"/>, or by name.</summary>
[JsonConverter(typeof(RefConverter<StatusRef, StatusBlock>))]
public sealed class StatusRef : IBlockRef<StatusBlock>
{
    internal StatusRef(StatusBlock? inline, string? name) { Inline = inline; Name = name; }
    public StatusBlock? Inline { get; }
    public string? Name { get; }
    public static implicit operator StatusRef(StatusBlock block) => new(block, null);
    public static implicit operator StatusRef(BlockHandle<StatusBlock> handle) => new(handle.Block, handle.ToString());
    public static implicit operator StatusRef(string name) => new(null, name);
    static IBlockRef<StatusBlock> IBlockRef<StatusBlock>.Make(StatusBlock? inline, string? name) => new StatusRef(inline, name);
}

/// <summary>An item a recipe needs: a <c>GameItems</c> constant, or an item this mod defines (a handle or its block name).</summary>
[JsonConverter(typeof(ItemRefConverter))]
public readonly record struct ItemRef(string Value)
{
    public static implicit operator ItemRef(string value) => new(value);
    public static implicit operator ItemRef(BlockHandle handle) => new(handle.GameName);
    public override string ToString() => Value;
}

public interface IBlockRef<T> where T : class
{
    T? Inline { get; }
    string? Name { get; }
    static abstract IBlockRef<T> Make(T? inline, string? name);
}

internal sealed class RefConverter<TRef, TBlock> : JsonConverter<TRef> where TRef : class, IBlockRef<TBlock> where TBlock : class
{
    public override TRef? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return (TRef)TRef.Make(null, r.GetString());
        return (TRef)TRef.Make(JsonSerializer.Deserialize<TBlock>(ref r, o), null);
    }

    public override void Write(Utf8JsonWriter w, TRef v, JsonSerializerOptions o)
    {
        if (v.Name != null) w.WriteStringValue(v.Name);
        else JsonSerializer.Serialize(w, v.Inline, o);
    }
}

// "applies": "chilled", or { "status": "chilled", "seconds": 4 }
internal sealed class StatusApplyConverter : JsonConverter<StatusApply>
{
    public override StatusApply? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new StatusApply { Status = r.GetString()! };
        using var doc = JsonDocument.ParseValue(ref r);
        var root = doc.RootElement;
        StatusRef? status = null;
        double? seconds = null;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name == "status") status = p.Value.Deserialize<StatusRef>(o);
            else if (p.Name == "seconds") seconds = p.Value.GetDouble();
            else throw new JsonException($"\"{p.Name}\" is not part of applies; it takes status and seconds");
        }
        return new StatusApply { Status = status ?? throw new JsonException("applies needs a status"), Seconds = seconds };
    }

    public override void Write(Utf8JsonWriter w, StatusApply v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        w.WritePropertyName("status");
        JsonSerializer.Serialize(w, v.Status, o);
        if (v.Seconds is { } s) w.WriteNumber("seconds", s);
        w.WriteEndObject();
    }
}

internal sealed class ItemRefConverter : JsonConverter<ItemRef>
{
    public override ItemRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, ItemRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
    public override ItemRef ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void WriteAsPropertyName(Utf8JsonWriter w, ItemRef v, JsonSerializerOptions o) => w.WritePropertyName(v.Value);
}

internal sealed class PremadeConverter<T> : JsonConverter<T> where T : class, IPremade
{
    private static IReadOnlyList<T> All => (IReadOnlyList<T>)typeof(T).GetField("All", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    public override T? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var key = r.GetString();
        return All.FirstOrDefault(x => x.Key == key)
               ?? throw new JsonException($"\"{key}\" is not a {typeof(T).Name}; there are {string.Join(", ", All.Select(x => x.Key))}");
    }

    public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WriteStringValue(v.Key);
}
