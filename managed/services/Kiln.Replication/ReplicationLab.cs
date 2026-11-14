// SPDX-License-Identifier: MIT
// The loopback harness for lab tests (kiln_repl.h, "lab"): the game's own replication for fake players in a lab
// without a player. docs/kiln/modding/testing.md, "Fake players for lab tests".
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Kiln;

/// <summary>
/// Lab only: fake players for tests that need the game's replication jobs, in a lab server without a player.
/// <para>The game's own pipeline replicates to each fake on a free machine index: relevance, snapshot, writer, delta
/// and send-callback jobs run on the game's worker threads every frame for the entities you make relevant to it; what
/// it is sent is decoded by the server exe's own copy of the client decoder, compared with what the server coded, and
/// acknowledged back, so baselines advance as with a real client. What it does not give: a client world, a player
/// entity, a session (bandwidth, receive), and the sim's parallel mode (it follows the live player entities, and a fake
/// has none).</para>
/// <para>Lab servers only: capability <c>repl.loopback</c> needs kiln.json's policy <c>"any"</c> and kiln.net's lab
/// port (<c>labPort</c> in <c>kiln/config/network.json</c>). Remove the fakes before your test ends; they also go when
/// the mod that added them unloads, or the world stops.</para>
/// </summary>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ReplicationLab
{
    private static ModContext? _owner;

    /// <summary>Lab only: adds <paramref name="count"/> fake players on the highest free machine indices (16, 15, …)
    /// and free player slots (15, 14, …). They replicate from the next frame on; nothing is relevant to them until
    /// <see cref="ForceRelevant"/>.</summary>
    /// <returns>Each fake's machine index and player slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not 1 to 16.</exception>
    /// <exception cref="InvalidOperationException">Fakes are active already (remove them first), there are not that
    /// many free machines or slots, or there is no server world yet.</exception>
    /// <exception cref="KilnUnavailableException"><c>repl.loopback</c> is off (not a lab server).</exception>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(ReplicationCaps.Loopback)]
    public static IReadOnlyList<(int Machine, int Slot)> AddFakePeers(int count)
    {
        Services.RequireTickThread("ReplicationLab.AddFakePeers");
        Require();
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 16);
        Span<ReplNative.Fake> fakes = stackalloc ReplNative.Fake[16];
        var rc = ReplNative.AddFakes((uint)count, fakes, out var added);
        switch (rc)
        {
            case ReplNative.Ok:
                break;
            case ReplNative.InvalidArgument:
                throw new InvalidOperationException("fake peers are active already: remove them first");
            case ReplNative.NotFound:
                throw new InvalidOperationException($"not {count} free machine indices and player slots in this world");
            case ReplNative.Unavailable:
                throw new InvalidOperationException("no server world yet, or the harness could not run (see kiln.log)");
            default:
                throw new InvalidOperationException($"kiln.repl refused the fakes ({rc})");
        }
        var list = new List<(int, int)>((int)added);
        for (var i = 0; i < (int)added; i++) list.Add(((int)fakes[i].Machine, (int)fakes[i].Slot));
        if (ModContext.CurrentMod is { } mod && !ReferenceEquals(mod, _owner))
        {
            _owner = mod;
            mod.Retiring += OwnerRetiring;
        }
        return list;
    }

    /// <summary>Lab only: makes <paramref name="entity"/> relevant to fake <paramref name="machine"/> on both of its
    /// channels (client and prediction), from the next frame on: the game then sends it a create and, after each
    /// change, an update. At most 64 entities per fake.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="EntityId.None"/>,
    /// <paramref name="machine"/> is not a fake, or it has 64 already.</exception>
    /// <exception cref="KilnUnavailableException"><c>repl.loopback</c> is off.</exception>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(ReplicationCaps.Loopback)]
    public static void ForceRelevant(int machine, EntityId entity)
    {
        Services.RequireTickThread("ReplicationLab.ForceRelevant");
        Require();
        if (entity.IsNone) throw new ArgumentException("no entity", nameof(entity));
        if (machine is < 0 or > 16) throw new ArgumentException($"machine {machine} is not a fake", nameof(machine));
        var rc = ReplNative.ForceRelevant((uint)machine, entity.Value);
        if (rc == ReplNative.InvalidArgument)
            throw new ArgumentException($"machine {machine} is not a fake, or it has 64 forced entities", nameof(machine));
        if (rc != ReplNative.Ok) throw new InvalidOperationException($"kiln.repl refused ({rc})");
    }

    /// <summary>Lab only: stops the fakes; the game frees their replication objects and their player slots (through
    /// its own reset). <see cref="LoopbackStats"/> keeps their counters. Nothing happens when there are none.</summary>
    /// <exception cref="KilnUnavailableException"><c>repl.loopback</c> is off.</exception>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(ReplicationCaps.Loopback)]
    public static void RemoveFakePeers()
    {
        Services.RequireTickThread("ReplicationLab.RemoveFakePeers");
        Require();
        var rc = ReplNative.RemoveFakes();
        if (rc != ReplNative.Ok) throw new InvalidOperationException($"kiln.repl refused ({rc})");
        Forget();
    }

    /// <summary>Lab only: what the harness decoded and compared since the fakes were added (counters start over when
    /// fakes are added again), and the replication objects and slot bits active now.</summary>
    /// <exception cref="KilnUnavailableException"><c>repl.loopback</c> is off.</exception>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(ReplicationCaps.Loopback)]
    public static ReplLoopbackStats LoopbackStats()
    {
        Services.RequireTickThread("ReplicationLab.LoopbackStats");
        Require();
        var rc = ReplNative.Stats(out var s);
        if (rc != ReplNative.Ok) throw new InvalidOperationException($"kiln.repl refused ({rc})");
        return new ReplLoopbackStats((long)s.Decoded, (long)s.ComponentsEqual, (long)s.ComponentsDiffer, (long)s.CustomDiffer,
            (long)s.DecodeFaults)
        {
            Messages = (long)s.Messages,
            CustomEqual = (long)s.CustomEqual,
            NoBaseline = (long)s.NoBaseline,
            Acked = (long)s.Acked,
            Dropped = (long)s.Dropped,
            DestroyLists = (long)s.DestroyLists,
            Fakes = (int)s.Fakes,
            ActiveObjects = (int)s.ActiveObjects,
            SlotMask = (int)s.SlotMask,
        };
    }

    /// <summary>Lab only: the newest state the harness decoded for <paramref name="entity"/> on fake
    /// <paramref name="machine"/>'s client channel: the bytes of <paramref name="component"/> in it (what that fake's
    /// client would hold) and the server tick of the message. Null when nothing was decoded for the entity, or that
    /// state has no such component.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="EntityId.None"/>, the component name
    /// is empty, or <paramref name="machine"/> is out of range.</exception>
    /// <exception cref="KilnUnavailableException"><c>repl.loopback</c> is off.</exception>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(ReplicationCaps.Loopback)]
    public static ReplLabDecoded? Decoded(int machine, EntityId entity, string component)
    {
        Services.RequireTickThread("ReplicationLab.Decoded");
        Require();
        ArgumentException.ThrowIfNullOrEmpty(component);
        if (entity.IsNone) throw new ArgumentException("no entity", nameof(entity));
        if (machine is < 0 or > 16) throw new ArgumentException($"no machine {machine}", nameof(machine));
        var rc = ReplNative.Decoded((uint)machine, entity.Value, ReplNative.ComponentHash(component), out var bytes, out var tick);
        return rc switch
        {
            ReplNative.Ok => new ReplLabDecoded(tick, bytes),
            ReplNative.NotFound => null,
            _ => throw new InvalidOperationException($"kiln.repl refused ({rc})"),
        };
    }

    private static void Require()
    {
        ReplicationCaps.Rows.Ensure();
        if ((ReplNative.Capabilities() & ReplNative.CapLoopback) == 0 || !Capabilities.Is(ReplicationCaps.Loopback))
            throw ReplicationCaps.Off(ReplicationCaps.Loopback, "the loopback harness runs on lab servers only");
    }

    private static void OwnerRetiring()
    {
        // the mod that added the fakes unloads: they go with it (its tick thread, in the unload)
        try
        {
            if ((ReplNative.Capabilities() & ReplNative.CapLoopback) != 0) ReplNative.RemoveFakes();
        }
        finally
        {
            _owner = null;
        }
    }

    private static void Forget()
    {
        if (_owner is { } mod) mod.Retiring -= OwnerRetiring;
        _owner = null;
    }
}

/// <summary>Lab only: what the loopback harness decoded since the fakes were added (<see cref="ReplicationLab"/>).</summary>
/// <param name="Decoded">Entity messages decoded.</param>
/// <param name="ComponentsEqual">Components with the game's generic byte delta whose decoded bytes equal the server's
/// snapshot of that message.</param>
/// <param name="ComponentsDiffer">Such components that differ, or a decoded component set that is not the server's: 0
/// when replication is faithful.</param>
/// <param name="CustomDiffer">Components with a codec of their own (CurrentTransform and four more) whose decoded bytes
/// differ: these codecs are lossy, so a difference is expected and not a failure.</param>
/// <param name="DecodeFaults">Decodes or template states that faulted or returned nothing.</param>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record ReplLoopbackStats(long Decoded, long ComponentsEqual, long ComponentsDiffer, long CustomDiffer, long DecodeFaults)
{
    /// <summary>Messages captured from the fakes' batches (every type).</summary>
    public long Messages { get; init; }

    /// <summary>Components with a codec of their own whose decoded bytes equal the server's snapshot.</summary>
    public long CustomEqual { get; init; }

    /// <summary>Updates whose baseline the harness did not have, or creates of a template it could not find.</summary>
    public long NoBaseline { get; init; }

    /// <summary>Message seqs acknowledged back through the game's ack handler.</summary>
    public long Acked { get; init; }

    /// <summary>Messages or snapshots that did not fit the harness's capture buffers.</summary>
    public long Dropped { get; init; }

    /// <summary>Destroy-list messages the fakes were sent.</summary>
    public long DestroyLists { get; init; }

    /// <summary>Bit per machine index: the fakes active now.</summary>
    public int Fakes { get; init; }

    /// <summary>Bit per machine index: the game's replication objects active now (a real player's too).</summary>
    public int ActiveObjects { get; init; }

    /// <summary>The world's mask of player slots being replicated, now (a real player's too).</summary>
    public int SlotMask { get; init; }
}

/// <summary>Lab only: one component of a state the loopback harness decoded (<see cref="ReplicationLab.Decoded"/>).</summary>
/// <param name="Tick">The server tick of the message it came in.</param>
/// <param name="Bytes">The component's bytes as the fake's client decoded them.</param>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record ReplLabDecoded(uint Tick, ReadOnlyMemory<byte> Bytes);
