// SPDX-License-Identifier: MIT
// ScreenEffectBlock: a full-screen effect as a block, in C# and (the same shape) in the "screenEffects" section of blocks/*.json. Its fields are
// ScreenEffect's (Kiln.Vfx); a block adds a name others can use, and where it shows when an effect plays it at a place.
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// A full-screen effect: colour grading, a tint, a vignette, a fade or a flash. Play it from code with <see cref="ScreenEffects"/>, or attach it:
/// <code>
/// var cold = Blocks.Define("cold", new ScreenEffectBlock { Temperature = -0.6f, Saturation = 0.7f, Vignette = new ScreenVignette { Color = "#10243a", Amount = 0.6f }, FadeIn = 1, FadeOut = 1 });
/// Blocks.Define("frozen", new StatusBlock { Name = "Frozen", Seconds = 10, Screen = cold });            // while the status is on a player
/// Blocks.Define("flashbang", new EffectBlock { Screen = new ScreenEffectBlock { Fill = "#ffffff", Seconds = 0.2f, FadeOut = 1.5f } });
/// ScreenEffects.Play(cold.Block);
/// </code>
/// Players' games need nothing for it: the server sends the whole effect to each Kiln client that should see it, and a vanilla client sees nothing.
/// It is drawn over the frame the game presents, so it covers the game's UI too.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record ScreenEffectBlock : ScreenEffect, IBlock
{
    /// <summary>Metres: when an effect plays it at a place, the players this near see it. Default 30.</summary>
    public float? Range { get; init; }
    /// <summary>The name this block had before a rename.</summary>
    public string? Was { get; init; }

    /// <summary>A note in a data file, ignored.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>A screen effect: inline, from <see cref="Blocks.Define{T}"/>, or by name.</summary>
[JsonConverter(typeof(RefConverter<ScreenRef, ScreenEffectBlock>))]
public sealed class ScreenRef : IBlockRef<ScreenEffectBlock>
{
    internal ScreenRef(ScreenEffectBlock? inline, string? name) { Inline = inline; Name = name; }
    public ScreenEffectBlock? Inline { get; }
    public string? Name { get; }
    public static implicit operator ScreenRef(ScreenEffectBlock block) => new(block, null);
    public static implicit operator ScreenRef(BlockHandle<ScreenEffectBlock> handle) => new(handle.Block, handle.ToString());
    public static implicit operator ScreenRef(string name) => new(null, name);
    static IBlockRef<ScreenEffectBlock> IBlockRef<ScreenEffectBlock>.Make(ScreenEffectBlock? inline, string? name) => new ScreenRef(inline, name);
}
