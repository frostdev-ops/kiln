// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TkOffline;

/// <summary>Tests that ask for unfaked services (kiln.spawn, and kiln.hooks, which remembers its miss), a game feature
/// that is off, and one that ends in KilnUnavailableException; one asks for nothing.</summary>
public sealed class TkOfflineMod : Mod
{
    [KilnTest]
    public void Reads_spawn_and_passes() => Assert.False(Spawn.Available, "no game here");

    [KilnTest]
    public void Reads_spawn_and_fails() => Assert.True(Spawn.Available, "spawn should be there");

    [KilnTest]
    public void Reads_a_feature() => Assert.False(Game.HasFeature("tk.feature"));

    [KilnTest]
    public void Throws_unavailable() => throw new KilnUnavailableException("tk.capability", "not on this build");

    [KilnTest]
    public void Asks_for_nothing() => Assert.True(Game.IsTickThread);

    // kiln.hooks keeps its miss (HooksNative._looked): only the first look asks the backend
    [KilnTest]
    public void Hooks_first() => Assert.False(Hooks.Available);

    [KilnTest]
    public void Hooks_second() => Assert.False(Hooks.Available);
}
