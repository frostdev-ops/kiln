// SPDX-License-Identifier: MIT
using Kiln;

namespace TkFaulty;

/// <summary>Throws in every Pre.</summary>
public sealed class TkFaultyMod : Mod
{
    public override void OnLoad() => Context.Tick.Pre += (in TickInfo t) => throw new InvalidOperationException($"tk-faulty broke at step {t.Step}");
}
