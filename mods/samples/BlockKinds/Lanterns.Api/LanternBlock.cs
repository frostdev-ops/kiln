// SPDX-License-Identifier: MIT
using Kiln;

namespace Lanterns;

/// <summary>A lantern: a torch that burns for <see cref="Seconds"/>. In blocks/*.json: section "lanterns:lanterns",
/// <c>{ "format": 1, "name": "Camp lantern", "seconds": 600 }</c>.</summary>
public sealed record LanternBlock : IBlock
{
    /// <summary>What players read.</summary>
    public string Name { get; init; } = "";
    /// <summary>How long it burns.</summary>
    public double Seconds { get; init; } = 300;
    /// <summary>Twigs to craft one.</summary>
    public int Twigs { get; init; } = 2;
}
