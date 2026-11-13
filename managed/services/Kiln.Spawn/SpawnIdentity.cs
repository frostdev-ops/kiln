// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Kiln.Spawn's start (ServiceTick.AutoStart runs this static constructor at the first service tick): puts
/// kiln.spawn's entity source last in <see cref="IdentitySources"/>' order, so <see cref="EntityRef.Of"/> and
/// <see cref="EntityRef.IsAlive"/> work for every entity kiln.spawn can inspect.</summary>
internal static class SpawnStartup
{
    static SpawnStartup()
    {
        Spawn.Rows.Ensure();  // the capability rows, also when no mod has used Spawn yet
        // Lab switch (mods/tests/instances-test shows its checks fail without the entity sources).
        if (Environment.GetEnvironmentVariable("KILN_LAB_NO_ENTITY_SOURCES") == "1")
        {
            Backend.Current.Log(0, LogLevel.Warn, "kiln.spawn: KILN_LAB_NO_ENTITY_SOURCES=1: no entity source (lab switch)");
            return;
        }
        IdentitySources.SetEntitySource(EntitySourceOrder.Spawn, SpawnStampSource.Instance);
    }
}

/// <summary>kiln.spawn's entity stamps. Every entity <see cref="Spawn.Inspect"/> finds is <c>Alive</c>, nothing is
/// <c>NotMine</c>; an id it does not find is <c>Absent</c>. The stamp is a property of the entity that cannot change while
/// it lives: the game's <c>SpawnTime</c> when the entity has one (every entity Kiln.Spawn makes, and what the game gives
/// its own spawns), else the tracker serial of a record kiln.spawn re-found after a restart (entities loaded from the
/// save carry no SpawnTime), else 0 (the ref can then only tell that the id exists). The two kinds of value live in two
/// halves of the Spawn place (bit 59), so a SpawnTime never compares equal to a tracker serial.</summary>
internal sealed class SpawnStampSource : IEntityStampSource
{
    internal static readonly SpawnStampSource Instance = new();

    /// <summary>Bit 59 of a stamp value: 0 = a SpawnTime, 1 = a tracker serial.</summary>
    internal const ulong SerialRange = 1UL << 59;

    private const ulong ValueMask = SerialRange - 1;

    public EntitySerialAnswer Query(EntityId entity, out ulong stamp)
    {
        stamp = 0;
        // Inspect answers "not found" while kiln.spawn has no world yet: that must not read as Absent.
        if (!Spawn.Available)
            throw new KilnUnavailableException(EntityRef.Capability, "kiln.spawn is unavailable on this server");
        if (!Spawn.WorldRunning)
            throw new KilnUnavailableException(EntityRef.Capability, "the server world is not running yet");
        EntityInfo? info;
        try
        {
            info = Spawn.Inspect(entity);
        }
        catch (Exception e) when (e is NotSupportedException or SpawnException)
        {
            throw new KilnUnavailableException(EntityRef.Capability, $"kiln.spawn could not inspect entity {entity}: {e.Message}", e);
        }
        if (info is null) return EntitySerialAnswer.Absent;
        stamp = StampOf(info.SpawnTime, info.IsTracked ? info.Serial : 0);
        return EntitySerialAnswer.Alive;
    }

    /// <summary>The stamp for an entity with this SpawnTime (null: none) and tracker serial (0: not tracked).</summary>
    internal static ulong StampOf(long? spawnTime, ulong trackerSerial)
    {
        if (spawnTime is { } t && ((ulong)t & ValueMask) != 0)
            return IdentitySources.Stamp(EntitySourceOrder.Spawn, (ulong)t & ValueMask);
        if ((trackerSerial & ValueMask) != 0)
            return IdentitySources.Stamp(EntitySourceOrder.Spawn, SerialRange | (trackerSerial & ValueMask));
        return 0;
    }
}
