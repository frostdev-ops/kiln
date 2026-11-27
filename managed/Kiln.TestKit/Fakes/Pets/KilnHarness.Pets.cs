// SPDX-License-Identifier: MIT
// The test kit's Kiln.Pets (API-SHAPE-2 C11): the real roster (definitions, records, bodies, orders, revives, downs, the
// hostile pairs and every event) on a world and a save of the kit's, with owners the test joins. A mod's Pets.Grant,
// Summon, Dismiss, Command, Release, Of and SetHostile run in kiln.Run (or its handlers) as on a server, and each step
// runs the roster's own step. A body is the kit's record of one (no game entity): the test reads them and downs them.
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessPets? _pets;

    /// <summary>The fake of Kiln.Pets' world (created and installed on first use, gone with the harness).</summary>
    public HarnessPets Pets => _pets ??= new HarnessPets(this);

    internal HarnessPets? PetsIfUsed => _pets;
}

/// <summary>Kiln.Pets offline: the owners in game, the pets' bodies and the hostile pairs a mod declared.</summary>
public sealed class HarnessPets
{
    private readonly KilnHarness _kiln;
    private readonly KitPetWorld _world = new();
    private readonly KitPetStore _store = new();

    internal HarnessPets(KilnHarness kiln)
    {
        _kiln = kiln;
        PetsRuntime.TestRows = true;   // every pets.* row on: the kit plays the world, the broker and the guard
        PetCreatures.Source = new KitCreatures();
        kiln.Run(() => { PetsRuntime.UseForTests(_world, _store); });
    }

    /// <summary>A character id for tests: account 0x2222..., id n.</summary>
    public static CharacterKey Character(int n) => new(0x2222333344445555, new Guid(n, 0, 0x4000, 0x80, 0, 0, 0, 0, 0, 0, (byte)n));

    /// <summary>The character is in game at <paramref name="at"/>: the player its requests name (<c>Pets.Summon(player,
    /// ...)</c>). Its records are read when Kiln.Pets first needs them.</summary>
    public PlayerRef Join(CharacterKey character, WorldPosition at = default, int level = 10) => _world.Join(character, at, level);

    /// <summary>The character left: its pets' bodies are retired at the next step (records and wishes kept).</summary>
    public void Leave(CharacterKey character) => _world.Leave(character);

    /// <summary>The character's pets, as <c>Pets.Of</c> answers on the tick thread.</summary>
    public IReadOnlyList<PetSnapshot> Of(CharacterKey character) => _kiln.Run(() => Kiln.Pets.Of(character));

    /// <summary>The pets' live bodies (the kit's entities: no game entity stands behind them).</summary>
    public IReadOnlyList<EntityRef> Bodies => _world.LiveBodies();

    /// <summary>The pet's body takes a lethal hit: the roster sees it at the next step and the pet goes down as its
    /// definition says.</summary>
    /// <exception cref="ArgumentException">The pet has no live body.</exception>
    public void Down(PetId pet) => _world.Down(pet);

    /// <summary>The character pairs a mod declared hostile (<c>Pets.SetHostile</c>), each pair once.</summary>
    public IReadOnlyList<(CharacterKey A, CharacterKey B)> HostilePairs =>
        _kiln.Run(() => PetsRuntime.Roster?.HostilePairs ?? []);

    internal void End()
    {
        PetsRuntime.StopForTests();
        PetsRuntime.TestRows = false;
        PetCreatures.ResetForTests();
    }
}

/// <summary>Every <c>game:</c> creature id is a walking enemy of the kit (a template of its own, levels 1..255); creature
/// blocks resolve as Kiln.Pets registers them.</summary>
internal sealed class KitCreatures : IPetCreatureSource
{
    public bool Ready => true;

    public PetCreatureInfo? Resolve(string id, out string? reason)
    {
        reason = null;
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return new PetCreatureInfo(id, new Guid(h.AsSpan(0, 16)), null, PetDefinitionRules.MaxLevel, id[(id.IndexOf(':') + 1)..]);
    }
}

/// <summary>The kit's world for the roster: owners the test joined, bodies kept here, ground everywhere.</summary>
internal sealed class KitPetWorld : IPetWorld
{
    private static readonly GamePetWorld Clock = new();
    private readonly Dictionary<CharacterKey, PetOwner> _online = [];
    private readonly Dictionary<uint, (PetBodyRequest Request, int Health, int Max, bool Down)> _bodies = [];
    private uint _next = 0x7000_0000;
    private ulong _sessions, _generations;

    public ulong WorldEpoch => 1;
    public ulong Step => ServiceTick.Step;
    public double Now => Clock.Now;

    public PlayerRef Join(CharacterKey key, WorldPosition at, int level)
    {
        if (key.IsNone || key.IsAccountOnly) throw new ArgumentException("a pet owner is a character (account and character id)", nameof(key));
        var entity = new EntityRef(new EntityId(0x6000_0000u + (uint)_online.Count + 1), 1);
        if (_online.TryGetValue(key, out var was)) entity = was.Entity;
        _online[key] = new PetOwner(key, entity, at, level, ++_sessions);
        return new PlayerRef(key.Account, key.Id, entity);
    }

    public void Leave(CharacterKey key) => _online.Remove(key);

    public IReadOnlyList<EntityRef> LiveBodies() => _bodies.Keys.Select(e => new EntityRef(new EntityId(e), 1)).ToList();

    public void Down(PetId pet)
    {
        var e = _bodies.FirstOrDefault(b => b.Value.Request.Pet == pet).Key;
        if (e == 0) throw new ArgumentException($"pet {pet} has no live body", nameof(pet));
        _bodies[e] = _bodies[e] with { Health = 0, Down = true };
    }

    public bool CanMakeBodies(out string reason)
    {
        reason = "";
        return true;
    }

    public PetOwner? Owner(PlayerRef who, out string? reason)
    {
        reason = null;
        foreach (var o in _online.Values)
            if (!who.Entity.IsNone ? o.Entity.Id == who.Entity.Id : who.CharacterId != Guid.Empty && o.Key.Id == who.CharacterId) return o;
        reason = who.IsNone ? "NoOwner: no player" : "OwnerOffline: the owner is not in game";
        return null;
    }

    public PetOwner? OnlineOwner(CharacterKey key) => _online.TryGetValue(key, out var o) ? o : null;
    public IReadOnlyList<PetOwner> OnlineOwners() => _online.Values.ToList();

    public WorldPosition? SafeGround(WorldPosition near, double minRadius, double maxRadius, PetDefinition definition, PetCreatureInfo? creature, EntityId owner) =>
        new WorldPosition(near.X + (float)Math.Max(minRadius, 1), near.Y, near.Z);

    public PetBody? CreateBody(PetBodyRequest request, out string? reason)
    {
        reason = null;
        var e = _next++;
        _bodies[e] = (request, 100, 100, false);
        return new PetBody(new EntityRef(new EntityId(e), 1), WorldEpoch, ++_generations);
    }

    public PetBodyPoll Poll(PetBody body, double since, out string? reason)
    {
        reason = _bodies.ContainsKey(body.Entity.Id.Value) ? null : "BodyGone";
        return reason is null ? PetBodyPoll.Ready : PetBodyPoll.Failed;
    }

    public bool Activate(PetBody body, out string? reason)
    {
        reason = null;
        return true;
    }

    public PetBodyReading Read(PetBody body) =>
        _bodies.TryGetValue(body.Entity.Id.Value, out var b)
            ? new PetBodyReading(true, b.Health, b.Max, false, b.Down, b.Request.Position)
            : new PetBodyReading(false, 0, 0, false, false, default);

    public int? OwnerHealth(PetOwner owner) => 100;
    public PetTargetInfo Classify(EntityRef target) => default;
    public void SyncHostility(IReadOnlyCollection<(CharacterKey A, CharacterKey B)> pairs) { }

    public bool Order(PetBody body, PetOrder order, EntityRef target, out string? reason)
    {
        reason = null;
        return true;
    }

    public bool Retire(PetBody body, PetChangeCause cause)
    {
        _bodies.Remove(body.Entity.Id.Value);
        return true;
    }
}

/// <summary>The kit's save for the roster: records in memory, no staging (a test of save receipts uses Kiln.Pets.Tests').</summary>
internal sealed class KitPetStore : IPetStore
{
    private readonly Dictionary<CharacterKey, string> _docs = [];
    public bool Available => true;
    public string? Load(CharacterKey key) => _docs.GetValueOrDefault(key);
    public void Write(CharacterKey key, string json) => _docs[key] = json;
    public void Delete(CharacterKey key) => _docs.Remove(key);
#pragma warning disable CS0067 // the kit never stages a save
    public event Action<ServiceCharacters.StagingInfo>? Staging;
    public event Action<ServiceCharacters.Receipt>? Staged;
    public event Action<ServiceCharacters.CommitInfo>? Committed;
#pragma warning restore CS0067
}
