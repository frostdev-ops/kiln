// SPDX-License-Identifier: MIT
// IPetWorld in a game: owners from kiln.players and Kiln.Saves' character keys, bodies through kiln.spawn (a game creature,
// transient: never written to the save) or kiln.creatures (a creature block's variant), placement through kiln.queries'
// voxel ground. Kiln translates the creature's Kiln id to its template here and nowhere else.
using System.Globalization;

namespace Kiln;

internal sealed partial class GamePetWorld : IPetWorld
{
    private ulong _generations;
    private readonly Dictionary<ulong, PetBody> _live = [];   // bodies made and not yet retired, by generation
    private static double _seconds;

    public ulong WorldEpoch => Lifecycle.IsServerRunning ? (ulong)Lifecycle.ServerSession : 0;
    public ulong Step => ServiceTick.Step;

    /// <summary>Seconds of the game's own 60 Hz world clock, as kiln.creature_control's waits count them: a step adds what
    /// the game's clock advanced in it (2 to 4 of its ticks on a dedicated server, which steps 15 to 25 times a second,
    /// never 60). A paused world's clock stands still (a hosting client in its menu); after a stall it catches up in the
    /// next step. The same for every GamePetWorld; it holds between steps.</summary>
    public double Now => Volatile.Read(ref _seconds);

    /// <summary>kiln.pets' service tick, before the roster's step (tick thread).</summary>
    internal static void Advance(in TickInfo tick) => Volatile.Write(ref _seconds, _seconds + tick.ClockDelta / 60.0);

    public bool CanMakeBodies(out string reason)
    {
        if (!Lifecycle.IsServerRunning) { reason = "WorldNotRunning: no server world runs here"; return false; }
        if (!Spawn.Available || !Spawn.WorldRunning) { reason = "SpawnUnavailable: kiln.spawn cannot make bodies here"; return false; }
        if (!Capabilities.Is("creature_control")) { reason = "ControlUnavailable: the creature control broker takes no new leases (creature_control is off)"; return false; }
        if (!Creatures.Available) { reason = "CreaturesUnavailable: kiln.creatures is not loaded"; return false; }
        reason = "";
        return true;
    }

    public PetOwner? Owner(PlayerRef who, out string? reason)
    {
        reason = null;
        if (who.IsNone)
        {
            reason = "NoOwner: no player";
            return null;
        }
        Player? p = null;
        try { p = !who.Entity.IsNone ? Players.Find(who.Entity.Id) : Players.FindPerson(who); }
        catch (NotSupportedException) { /* no kiln.players */ }
        if (p is not { InGame: true })
        {
            reason = "OwnerOffline: the owner is not in game";
            return null;
        }
        var o = OwnerOf(p);
        if (o is null) reason = "NoCharacter: the owner's character id is not known on this build (account-only players cannot own pets)";
        return o;
    }

    public PetOwner? OnlineOwner(CharacterKey key)
    {
        foreach (var o in OnlineOwners())
            if (o.Key == key) return o;
        return null;
    }

    public IReadOnlyList<PetOwner> OnlineOwners()
    {
        if (!Players.Available) return [];
        var list = new List<PetOwner>();
        foreach (var p in Players.All)
            if (p.InGame && OwnerOf(p) is { } o) list.Add(o);
        return list;
    }

    private static PetOwner? OwnerOf(Player p)
    {
        var key = CharacterData.KeyOf(p.Entity);
        if (key.IsNone || key.IsAccountOnly) return null;
        var level = 0;
        try { level = Creatures.Available ? Creatures.Of(p.Entity).Level : 0; }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
        EntityRef entity;
        try { entity = EntityRef.Of(p.Entity); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { entity = new EntityRef(p.Entity, 0); }
        if (entity.IsNone) entity = new EntityRef(p.Entity, 0);
        return new PetOwner(key, entity, p.Position ?? default, level, ((ulong)p.Handle << 32) ^ p.JoinedStep);
    }

    // ---- placement (package 4 refines it: footprint samples, clearance, settling) ----

    public WorldPosition? SafeGround(WorldPosition near, double minRadius, double maxRadius, PetDefinition definition, PetCreatureInfo? creature, EntityId owner) =>
        PetPlacement.Find(near, minRadius, maxRadius, definition, creature, owner);

    // ---- bodies ----

    /// <summary>A body's broker leases (GamePetMover reads the authority through it).</summary>
    internal interface IHasControl
    {
        PetControl? Control { get; }
    }

    private sealed class BodyTag : IHasControl
    {
        public double HealthFraction = 1;
        public bool HealthSet;
        public PetBodyRequest Request = null!;
        public PetControl? Control { get; set; }
        public bool Quarantined;
        public ulong QuarantinedAt;
    }

    public PetBody? CreateBody(PetBodyRequest request, out string? reason)
    {
        reason = null;
        if (!CanMakeBodies(out var off))
        {
            reason = off;
            return null;
        }
        EntityId e;
        try
        {
            if (request.Creature.Variant is { } variant)
            {
                var t = Creatures.Spawn(variant, request.Position, 0, new CreatureSpawnOptions { Tag = "kiln.pets", Flags = SpawnFlags.Transient });
                if (!t.IsCompleted)
                {
                    reason = "SpawnRefused: the creature block's body did not spawn at once";
                    return null;
                }
                if (t.IsFaulted)
                {
                    reason = $"SpawnRefused: {t.Exception?.GetBaseException().Message}";
                    return null;
                }
                e = t.Result;
            }
            else
            {
                var r = Spawn.TryEntity(request.Creature.Template, request.Position, 0,
                    new SpawnOptions { Tag = "kiln.pets", Level = request.Level, Flags = SpawnFlags.Transient });
                if (!r.Ok)
                {
                    reason = $"SpawnRefused: {r.Reason}";
                    return null;
                }
                e = r.Entity.Id;
            }
        }
        catch (Exception x) when (x is ArgumentException or InvalidOperationException or NotSupportedException or SpawnException)
        {
            reason = $"SpawnRefused: {x.Message}";
            return null;
        }
        if (e.IsNone || e.Value < 1024 || e.Value > 0x7fffffff)
        {
            if (!e.IsNone && !e.IsPlayer) Spawn.Destroy(e);
            reason = "SpawnRefused: the body is not a counter-range entity";
            return null;
        }
        // the players' side, at once (the guard's rows and the broker's lease come with packages 3 and 5)
        try
        {
            var c = Creatures.Of(e);
            c.Faction = Faction.PlayerAlly;
            if (request.Creature.Variant is not null) c.Level = request.Level;
            if (Math.Abs(request.Definition.Level.Scale - 1) > 1e-6) c.Scale = (float)request.Definition.Level.Scale;
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Spawn.Destroy(e);
            reason = $"BodyRefused: its faction could not be set ({x.Message})";
            return null;
        }
        EntityRef body;
        try { body = EntityRef.Of(e); }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException) { body = new EntityRef(e, 0); }
        if (body.IsNone) body = new EntityRef(e, 0);
        var pb = new PetBody(body, WorldEpoch, ++_generations) { Tag = new BodyTag { HealthFraction = request.HealthFraction, Request = request } };
        if (!Attach(pb, request, out reason)) return Abandon(pb, reason);
        _live[pb.Generation] = pb;
        return pb;
    }

    private PetBody? Abandon(PetBody b, string? reason)
    {
        Spawn.Destroy(b.Entity.Id);
        PetsRuntime.Log(LogLevel.Info, $"Kiln.Pets: a body for pet was refused: {reason}");
        return null;
    }

    public PetBodyPoll Poll(PetBody body, double since, out string? reason)
    {
        reason = null;
        var info = Inspect(body.Entity.Id);
        if (info is null)
        {
            reason = "BodyGone: the new body disappeared";
            return PetBodyPoll.Failed;
        }
        var tag = (BodyTag)body.Tag!;
        var control = ControlPoll(body, out var why);
        if (control == PetBodyPoll.Failed)
        {
            reason = why;
            return PetBodyPoll.Failed;
        }
        if (info.Value.HealthMax > 0 && info.Value.Health > 0 && !info.Value.Dead && control == PetBodyPoll.Ready)
        {
            if (!tag.HealthSet)
            {
                // the stored share of its maximum (at least 1): dismiss and relogin never heal a pet
                var hp = Math.Clamp(PetState.HealthOf(tag.HealthFraction, info.Value.HealthMax), 1, info.Value.HealthMax);
                if (hp != info.Value.Health) Combat.SetHealth(body.Entity.Id, hp);
                tag.HealthSet = true;
            }
            return PetBodyPoll.Ready;
        }
        if (Now - since >= 5)
        {
            reason = why ?? "BodyNotReady: the body was not ready within 5 s (no player near it?)";
            return PetBodyPoll.Failed;
        }
        return PetBodyPoll.Pending;
    }

    public PetBodyReading Read(PetBody body)
    {
        var info = Inspect(body.Entity.Id);
        if (info is null || body.WorldEpoch != WorldEpoch) return new PetBodyReading(false, 0, 0, false, false, default);
        var (hp, max, dead, pos) = info.Value;
        return new PetBodyReading(true, hp, max, dead, NativeDownPending(body), pos, ProtectionLost(body), ControlLost(body));
    }

    private static (int Health, int HealthMax, bool Dead, WorldPosition Position)? Inspect(EntityId e)
    {
        try
        {
            if (!Spawn.Exists(e)) return null;
            var c = Creatures.Of(e).Inspect();
            World.TryGetPosition(e, out var pos);
            if (c is null) return (0, 0, false, pos);
            return (c.Health, c.HealthMax, c.State == EnemyState.Dead, pos);
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool Retire(PetBody body, PetChangeCause cause)
    {
        if (body.WorldEpoch != WorldEpoch)
        {
            _live.Remove(body.Generation);
            return true;  // another world: nothing of it is here to remove
        }
        if (!Detach(body, cause)) return false;   // producers revoked; a rider gets its two steps to get off first
        try
        {
            if (Spawn.Exists(body.Entity.Id)) Spawn.Destroy(body.Entity.Id);
            var gone = !Spawn.Exists(body.Entity.Id);
            if (gone)
            {
                Retired(body);
                _live.Remove(body.Generation);
            }
            return gone;
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException)
        {
            PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: body {body} could not be retired: {x.Message}");
            return false;
        }
    }

    public bool Activate(PetBody body, out string? reason) => ActivateBody(body, out reason);

    public bool Order(PetBody body, PetOrder order, EntityRef target, out string? reason) => SetOrder(body, order, target, out reason);

    // ---- the parts packages 3 (broker) and 5 (native guard) fill ----
    private partial bool Attach(PetBody body, PetBodyRequest request, out string? reason);
    private partial PetBodyPoll ControlPoll(PetBody body, out string? reason);
    private partial string? ControlLost(PetBody body);
    private partial bool ActivateBody(PetBody body, out string? reason);
    private partial bool Detach(PetBody body, PetChangeCause cause);
    private partial void Retired(PetBody body);
    private partial bool SetOrder(PetBody body, PetOrder order, EntityRef target, out string? reason);
    private partial bool NativeDownPending(PetBody body);
    private partial bool ProtectionLost(PetBody body);

    internal static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>IPetStore over Kiln.Saves' service records (scope service/kiln.pets/character/&lt;key&gt;).</summary>
internal sealed class GamePetStore : IPetStore
{
    public bool Available => Lifecycle.IsServerRunning;
    public string? Load(CharacterKey key) => ServiceCharacters.Get(PetsRuntime.ServiceName, key);
    public void Write(CharacterKey key, string json) => ServiceCharacters.Set(PetsRuntime.ServiceName, key, json);
    public void Delete(CharacterKey key) => ServiceCharacters.Remove(PetsRuntime.ServiceName, key);

    public event Action<ServiceCharacters.StagingInfo>? Staging
    {
        add => ServiceCharacters.Staging += value;
        remove => ServiceCharacters.Staging -= value;
    }

    public event Action<ServiceCharacters.Receipt>? Staged
    {
        add => ServiceCharacters.Staged += value;
        remove => ServiceCharacters.Staged -= value;
    }

    public event Action<ServiceCharacters.CommitInfo>? Committed
    {
        add => ServiceCharacters.Committed += value;
        remove => ServiceCharacters.Committed -= value;
    }
}
