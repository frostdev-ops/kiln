// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>A test's handle on one mod of a <see cref="KilnHarness"/>: what a test keeps in a local. It holds the mod's id
/// and folder only and looks the running instance up on each read, so after an unload it references nothing of the mod
/// and the mod's load context can be collected. The instance is the second, isolated copy the loader made (as in the
/// game), so it is a <see cref="Kiln.Mod"/>, never the test project's own <c>TMod</c> type.</summary>
public sealed class ModHandle
{
    private readonly KilnHarness _harness;

    internal ModHandle(KilnHarness harness, string id, string folder)
    {
        _harness = harness;
        Id = id;
        Folder = folder;
    }

    /// <summary>The mod id.</summary>
    public string Id { get; }

    /// <summary>The mod's folder, <c>kiln/mods/&lt;folder&gt;</c> in the harness's temp server folder.</summary>
    public string Folder { get; }

    /// <summary>What the mod list says: <see cref="ModState.Pending"/> while it waits for the server's first step,
    /// Loaded, Disabled (fault policy), Failed, Skipped or Unloaded.</summary>
    public ModState State => _harness.EntryOf(Id)?.State ?? ModState.Pending;

    /// <summary>Why the mod is not loaded or was disabled; null otherwise.</summary>
    public string? Error => _harness.EntryOf(Id)?.Error;

    /// <summary><see cref="State"/> is Loaded.</summary>
    public bool IsLoaded => State == ModState.Loaded;

    /// <summary>The running instance (loaded or disabled). Do not keep it in a field or local past the next unload: it
    /// is the mod's code, and holding it keeps the mod's load context alive, so <see cref="KilnHarness.Unload"/>'s check
    /// fails. In a Debug build even a temporary of the test method can stay on the stack: read what reaches into the mod
    /// (this, <see cref="Context"/>, <see cref="Events.Subscriptions"/>) inside <see cref="KilnHarness.Run{T}(Func{T})"/>
    /// or a helper method of its own.</summary>
    public Mod Instance => _harness.LiveOf(Id).Instance;

    /// <summary>The running instance's context (loaded or disabled). Same rule as <see cref="Instance"/>.</summary>
    public ModContext Context => _harness.LiveOf(Id).ModContext;

    /// <summary>The manifest's metadata, once the loader has read it.</summary>
    public ModInfo Info => _harness.EntryOf(Id)?.Info ?? throw new InvalidOperationException($"mod {Id} has not been read by the loader yet (the server loads its mods at the first step)");

    /// <summary>"id (state)".</summary>
    public override string ToString() => $"{Id} ({State.ToString().ToLowerInvariant()})";
}
