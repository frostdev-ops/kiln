// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessSpawn? _spawned;

    /// <summary>The fake kiln.spawn state of this harness (null before the constructor set it up).</summary>
    internal SpawnFake? SpawnFake { get; private set; }

    /// <summary>What Kiln.Spawn (<see cref="global::Kiln.Spawn"/>) did in this server's fake world: the templates the game
    /// has, the entities mods spawned, and every spawn, destroy and cleanup a mod asked for.</summary>
    public HarnessSpawn Spawned => _spawned ??= new HarnessSpawn(this);

    private SpawnFake SpawnFakeOrThrow
    {
        get
        {
            ThrowIfDisposed();
            return SpawnFake ?? throw new InvalidOperationException("the harness has no world yet");
        }
    }

    private void StartSpawn() => SpawnFake = new SpawnFake(this);
}

/// <summary>What a spawn call was (<see cref="FakeSpawnCall.Kind"/>).</summary>
public enum FakeSpawnKind
{
    /// <summary>A template: <see cref="global::Kiln.Spawn.Entity(string, WorldPosition, float, SpawnOptions?)"/> and the like.</summary>
    Entity = 0,
    /// <summary>A loose item pickup: <see cref="global::Kiln.Spawn.Item"/>.</summary>
    Item = 1,
    /// <summary><see cref="global::Kiln.Spawn.Destroy(EntityId)"/>.</summary>
    Destroy = 2,
    /// <summary><see cref="global::Kiln.Spawn.CleanupTracked"/>.</summary>
    Cleanup = 3,
}

/// <summary>One call a mod made to kiln.spawn that changes the world (<see cref="HarnessSpawn.Calls"/>), with its answer.</summary>
public sealed class FakeSpawnCall
{
    internal FakeSpawnCall(ulong step, string? modId, FakeSpawnKind kind, string subject, WorldPosition? position, string tag, int code, EntityId entity)
    {
        Step = step;
        ModId = modId;
        Kind = kind;
        Subject = subject;
        Position = position;
        Tag = tag;
        Code = code;
        Entity = entity;
    }

    /// <summary>The harness step during which the call ran.</summary>
    public ulong Step { get; }

    /// <summary>The mod that asked; null for a call outside any mod.</summary>
    public string? ModId { get; }

    /// <summary>What was asked.</summary>
    public FakeSpawnKind Kind { get; }

    /// <summary>The template's name or GUID text as the mod gave it, <c>item 0x… xN</c>, <c>entity N</c>, or the cleanup's filter.</summary>
    public string Subject { get; }

    /// <summary>Where a spawn was asked for; null for a destroy or a cleanup.</summary>
    public WorldPosition? Position { get; }

    /// <summary>The spawn's tag (an item drop's is "item" by default), the destroyed entity's, or the cleanup's filter.</summary>
    public string Tag { get; }

    /// <summary>The native answer: 0 ok, 1 invalid argument, 2 not found (an unknown template, no such entity), 3 unavailable.</summary>
    public int Code { get; }

    /// <summary>The new or destroyed entity; none for a refused spawn and a cleanup.</summary>
    public EntityId Entity { get; }

    /// <summary>The call, for a log line.</summary>
    public override string ToString() => $"step {Step} {ModId ?? "kiln"}: {Kind} {Subject} -> {Code}{(Entity.IsNone ? "" : $" ({Entity})")}";
}

/// <summary>An entity Kiln.Spawn put into the fake world (<see cref="HarnessSpawn.All"/>).</summary>
public sealed class FakeSpawned
{
    private readonly KilnHarness _harness;
    private readonly SpawnFake.Spawned _record;

    internal FakeSpawned(KilnHarness harness, SpawnFake.Spawned record)
    {
        _harness = harness;
        _record = record;
    }

    /// <summary>The entity.</summary>
    public EntityId Entity => new(_record.Entity);

    /// <summary>The template's name (<see cref="HarnessSpawn.PickupTemplate"/> for an item drop).</summary>
    public string Template => _record.Template.Name;

    /// <summary>The template's GUID.</summary>
    public Guid TemplateId => _record.Template.Id;

    /// <summary>The template's kind.</summary>
    public TemplateKind Kind => _record.Template.Kind;

    /// <summary>Where it was spawned.</summary>
    public WorldPosition SpawnPosition => _record.SpawnPosition;

    /// <summary>Where it stands now (its <c>CurrentTransform</c> in the fake world); null when it is gone.</summary>
    public WorldPosition? Position => _harness.Run(() => _harness.EntitiesFake?.PositionOf(_record.Entity));

    /// <summary>The rotation about the up axis it was spawned with.</summary>
    public float YawDegrees => _record.Yaw;

    /// <summary>The spawn's tag ("" when none; "item" for an item drop without one).</summary>
    public string Tag => _record.Tag;

    /// <summary>The mod that spawned it ("" outside any mod).</summary>
    public string Origin => _record.Origin;

    /// <summary>The level asked for (0: the template's own).</summary>
    public int Level => _record.Level;

    /// <summary>The spawn's flags.</summary>
    public SpawnFlags Flags => (SpawnFlags)_record.Flags;

    /// <summary>An item drop's item; 0 for a template spawn.</summary>
    public uint ItemId => _record.ItemId;

    /// <summary>An item drop's amount; 0 for a template spawn.</summary>
    public int Amount => _record.Amount;

    /// <summary>Kiln's tracked-record serial; 0 for a spawn with <see cref="SpawnFlags.NoTrack"/>.</summary>
    public ulong Serial => _record.Serial;

    /// <summary>The harness step during which it was spawned.</summary>
    public ulong Step => _record.Step;

    /// <summary>The entity and its template, for a log line.</summary>
    public override string ToString() => $"{Entity} {Template}{(ItemId != 0 ? $" (item 0x{ItemId:x8} x{Amount})" : "")} tag '{Tag}' by {(Origin.Length > 0 ? Origin : "kiln")}";
}

/// <summary>
/// The fake kiln.spawn of a harness (<see cref="KilnHarness.Spawned"/>): Kiln.Spawn's own code (<see cref="global::Kiln.Spawn"/>,
/// <see cref="EntitySpawnedEvent"/>, <see cref="EntityDestroyedEvent"/>, <see cref="EntityRef"/> stamps) runs on it, and the
/// entities it makes stand in the harness's fake world (<see cref="KilnHarness.Entities"/>).
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> A spawn creates an entity in the fake world at once, at the position asked (its
/// <c>CurrentTransform</c>, turned by the yaw) with the game's <c>SpawnTime</c> (Kiln's marker for a tracked spawn), as the
/// engine does; the spawn and destroy events are Kiln.Spawn's own. Spawning works once the server has started
/// (<c>spawn.world</c>; before that it loads, as a server loads its save). Templates are the ones the game has loaded: the
/// kit does not know the game's, so a test adds those its mod spawns (<see cref="AddTemplate"/>); an unknown one is
/// refused as the game refuses it. The generic item pickup is there from the start. <c>Destroy</c> removes any non-player
/// entity of the fake world; <c>Find</c>, <c>FindByName</c>, <c>EntityCount</c>, <c>Inspect</c>, <c>Exists</c> and the tracked
/// list read the fake world. The tracked list lives with the harness (the next harness starts with none).</para>
/// <para><b>Not modelled</b> (they report as kiln.spawn reports them when they are off): extra spawn components
/// (<c>spawn.components</c> is off), what a template does (a creature does not move or fight), the level, the
/// <c>DoNotSave</c> tag, saving spawned entities with the world, and re-finding tracked entities after a restart.</para>
/// </remarks>
public sealed class HarnessSpawn
{
    private readonly KilnHarness _harness;

    internal HarnessSpawn(KilnHarness harness) => _harness = harness;

    private SpawnFake Fake => _harness.SpawnFake ?? throw new InvalidOperationException("the harness has no world yet");

    /// <summary>The name of the game's generic item pickup, the template of every item drop.</summary>
    public const string PickupTemplate = SpawnFake.PickupName;

    /// <summary>Says the game has a template loaded, so a mod can spawn it by name, display name or GUID (and
    /// <see cref="global::Kiln.Spawn.Lookup"/>, <see cref="global::Kiln.Spawn.Templates"/> list it). Returns its GUID.</summary>
    /// <param name="name">The engine's template name (<c>Enemy_Vukah_Melee</c>), or a clone's name a mod registers.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="displayName">The in-game name ("Vukah"), or "".</param>
    /// <param name="id">The template's GUID; empty: one made from the name (the same in every run).</param>
    /// <exception cref="ArgumentException">An empty name, a name or GUID the game has already.</exception>
    public Guid AddTemplate(string name, TemplateKind kind = TemplateKind.Creature, string displayName = "", Guid id = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(displayName);
        if (System.Text.Encoding.UTF8.GetByteCount(name) >= 128) throw new ArgumentException("a template name has at most 127 bytes of UTF-8", nameof(name));
        if (System.Text.Encoding.UTF8.GetByteCount(displayName) >= 64) throw new ArgumentException("a display name has at most 63 bytes of UTF-8", nameof(displayName));
        var fake = _harness.Run(() => Fake);
        return fake.AddTemplate(name, kind, displayName, id).Id;
    }

    /// <summary>The entities Kiln.Spawn made that are still in the world, in the order they were spawned.</summary>
    public IReadOnlyList<FakeSpawned> All => _harness.Run(() => (IReadOnlyList<FakeSpawned>)[.. Fake.Alive.Select(s => new FakeSpawned(_harness, s))]);

    /// <summary>Every spawn, item drop, destroy and cleanup a mod asked for, in order, refused ones included.</summary>
    public IReadOnlyList<FakeSpawnCall> Calls => _harness.Run(() => Fake.Calls);

    /// <summary>The live spawned entities of a template (by name, ignoring case).</summary>
    public IReadOnlyList<FakeSpawned> Of(string template)
    {
        ArgumentException.ThrowIfNullOrEmpty(template);
        return [.. All.Where(s => string.Equals(s.Template, template, StringComparison.OrdinalIgnoreCase))];
    }
}
