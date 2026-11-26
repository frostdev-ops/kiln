// SPDX-License-Identifier: MIT
// A deterministic world and save for the roster: owners stand where the test puts them, bodies are made at once and are
// ready after a set number of steps, health is what the test writes, the save stages and commits when the test says.
using Kiln;

namespace Kiln.PetsTests;

internal sealed class FakeWorld : IPetWorld
{
    public ulong WorldEpoch { get; set; } = 1;
    public ulong Step { get; set; } = 1;
    public double Now { get; set; }
    public bool BodiesOn { get; set; } = true;
    public bool GroundOn { get; set; } = true;
    public int ReadySteps { get; set; } = 1;
    public bool RetireRefuses { get; set; }
    public string? CreateRefusal { get; set; }
    public readonly Dictionary<CharacterKey, PetOwner> Online = [];
    public readonly Dictionary<uint, FakeBody> Bodies = [];
    public readonly List<string> Log = [];
    private uint _next = 2000;
    private ulong _gen;

    internal sealed class FakeBody
    {
        public uint Entity;
        public int Health = 100, Max = 100;
        public bool Dead, DownPending, ProtectionLost;
        public WorldPosition Position;
        public int ReadyIn;
        public bool Active;
        public PetBodyRequest Request = null!;
    }

    public PetOwner Join(CharacterKey key, WorldPosition at = default, int level = 10)
    {
        var o = new PetOwner(key, new EntityRef(new EntityId((uint)(Online.Count + 1)), 1), at, level, (ulong)Online.Count + 100);
        Online[key] = o;
        return o;
    }

    public void Leave(CharacterKey key) => Online.Remove(key);

    public void Move(CharacterKey key, WorldPosition to) => Online[key] = Online[key] with { Position = to };

    public bool CanMakeBodies(out string reason)
    {
        reason = BodiesOn ? "" : "SpawnUnavailable: off in the test";
        return BodiesOn;
    }

    public PetOwner? Owner(PlayerRef who, out string? reason)
    {
        reason = null;
        foreach (var o in Online.Values)
            if (o.Entity.Id == who.Entity.Id) return o;
        reason = "OwnerOffline: the owner is not in game";
        return null;
    }

    public PetOwner? OnlineOwner(CharacterKey key) => Online.TryGetValue(key, out var o) ? o : null;
    public IReadOnlyList<PetOwner> OnlineOwners() => Online.Values.ToList();

    public WorldPosition? SafeGround(WorldPosition near, double minRadius, double maxRadius, PetDefinition definition, PetCreatureInfo? creature) =>
        GroundOn ? new WorldPosition(near.X + Math.Max(minRadius, 1), near.Y, near.Z) : null;

    public PetBody? CreateBody(PetBodyRequest request, out string? reason)
    {
        reason = CreateRefusal;
        if (reason is not null) return null;
        var b = new FakeBody { Entity = _next++, Position = request.Position, ReadyIn = ReadySteps, Request = request };
        b.Health = Math.Clamp(PetState.HealthOf(request.HealthFraction, b.Max), 1, b.Max);
        Bodies[b.Entity] = b;
        Log.Add($"create {b.Entity} for {request.Pet}");
        return new PetBody(new EntityRef(new EntityId(b.Entity), 7), WorldEpoch, ++_gen) { Tag = b };
    }

    public PetBodyPoll Poll(PetBody body, double since, out string? reason)
    {
        reason = null;
        if (!Bodies.TryGetValue(body.Entity.Id.Value, out var b))
        {
            reason = "BodyGone";
            return PetBodyPoll.Failed;
        }
        if (b.ReadyIn-- > 0)
        {
            if (Now - since >= 5)
            {
                reason = "BodyNotReady: not ready within 5 s";
                return PetBodyPoll.Failed;
            }
            return PetBodyPoll.Pending;
        }
        return PetBodyPoll.Ready;
    }

    public bool Activate(PetBody body, out string? reason)
    {
        reason = null;
        Bodies[body.Entity.Id.Value].Active = true;
        return true;
    }

    public PetBodyReading Read(PetBody body) =>
        Bodies.TryGetValue(body.Entity.Id.Value, out var b) && body.WorldEpoch == WorldEpoch
            ? new PetBodyReading(true, b.Health, b.Max, b.Dead, b.DownPending, b.Position, b.ProtectionLost)
            : new PetBodyReading(false, 0, 0, false, false, default);

    public bool Retire(PetBody body, PetChangeCause cause)
    {
        if (RetireRefuses) return false;
        Bodies.Remove(body.Entity.Id.Value);
        Log.Add($"retire {body.Entity.Id.Value} {cause}");
        return true;
    }
}

internal sealed class FakeStore : IPetStore
{
    public bool Available { get; set; } = true;
    public readonly Dictionary<CharacterKey, string> Docs = [];
    public int Session = 1;
    public event Action<ServiceCharacters.StagingInfo>? Staging;
    public event Action<ServiceCharacters.Receipt>? Staged;
    public event Action<ServiceCharacters.CommitInfo>? Committed;

    public string? Load(CharacterKey key) => Docs.GetValueOrDefault(key);
    public void Write(CharacterKey key, string json) => Docs[key] = json;
    public void Delete(CharacterKey key) => Docs.Remove(key);

    /// <summary>One staging as Kiln.Saves does it: participants write, then each document is serialized as its bundle.</summary>
    public void Stage(uint gen, bool snapshot)
    {
        Staging?.Invoke(new ServiceCharacters.StagingInfo(gen, snapshot, false, Session));
        foreach (var (key, json) in Docs)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{{\"format\":1,\"scope\":\"service/kiln.pets/character/{key.Text}\",\"keys\":{{\"record\":{json}}}}}");
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            Staged?.Invoke(new ServiceCharacters.Receipt("kiln.pets", key, gen, snapshot, Session, sha, bytes));
        }
    }

    public void Commit(uint gen) => Committed?.Invoke(new ServiceCharacters.CommitInfo(gen, 1000 + gen, Session));
}
