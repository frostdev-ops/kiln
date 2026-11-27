// SPDX-License-Identifier: MIT
// NativeTaming offline, over a made-up world holding the game's taming components as bytes (docs/re/taming.md): the
// reads of Wildlife / DynamicWildlife, the phases, the step's refusals (not eating, complete, tamed, another pet sequence),
// the bait placement in front of the animal, and the watch's events (a step, a startle, a bait eaten without a step, the
// tame). What the game does is the lab probe mods/tests/native-taming-test.
using System.Buffers.Binary;
using Kiln;
using Xunit;

namespace Kiln.TamingTests;

public sealed class NativeTamingTests : IDisposable
{
    private static readonly Guid DogBait = Guid.Parse("21eb90be-0ff2-477d-a804-dee22e9a2623");
    private static readonly EntityId Dog = new(2042);

    private sealed class Backend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    internal sealed class FakeWorld : INativeTamingWorld
    {
        public readonly Dictionary<(EntityId, string), byte[]> Parts = new();
        public readonly Dictionary<EntityId, Dictionary<string, string>> Fields = new();
        public readonly Dictionary<EntityId, Guid> Templates = new();
        public int FieldReads;
        public readonly HashSet<EntityId> Alive = new();
        public readonly List<(Guid Template, WorldPosition At)> Spawned = new();
        public readonly List<(EntityId, Guid)> Played = new();
        public WorldPosition At = new(10, 5, 20);
        public float YawDeg;
        public bool PlayOk = true;

        public byte[]? ReadBytes(EntityId e, string component) => Parts.TryGetValue((e, component), out var b) ? (byte[])b.Clone() : null;

        public string? TemplateField(EntityId e, string path)
        {
            FieldReads++;
            return Fields.TryGetValue(e, out var f) && f.TryGetValue(path, out var v) ? v : null;
        }

        public Guid? TemplateOf(EntityId e) => Templates.TryGetValue(e, out var g) ? g : null;
        public bool Has(EntityId e, string component) => Parts.ContainsKey((e, component));
        public bool Exists(EntityId e) => Alive.Contains(e);
        public IReadOnlyList<EntityId> Find(string component) => Parts.Keys.Where(k => k.Item2 == component).Select(k => k.Item1).ToList();
        public WorldPosition? Position(EntityId e) => Alive.Contains(e) ? At : null;
        public float? Yaw(EntityId e) => YawDeg;

        public Task<EntityId> SpawnTransient(Guid template, WorldPosition at)
        {
            Spawned.Add((template, at));
            var e = new EntityId(9000u + (uint)Spawned.Count);
            Alive.Add(e);
            return Task.FromResult(e);
        }

        public bool PlaySequence(EntityId e, Guid sequence)
        {
            Played.Add((e, sequence));
            return PlayOk;
        }

        // as the game has it (lab 11-27): the taming setup in the Wildlife template, read by its reflection paths; the
        // entity has DynamicWildlife and no Wildlife component
        public void AddDog(EntityId e, byte steps = 2, bool tameable = true, Guid? petSequence = null, Guid? template = null)
        {
            Fields[e] = new Dictionary<string, string>
            {
                ["Wildlife.enabledFeatures"] = tameable ? "Roaming|Flee|Tameable|RunAway" : "Roaming|Flee|RunAway",
                ["Wildlife.tamingSetup.favoriteFood"] = DogBait.ToString(),
                ["Wildlife.tamingSetup.necessaryTamingSteps"] = steps.ToString(),
                ["Wildlife.tamingSetup.foodBaitSensorRange"] = "8.5",
                ["Wildlife.tamingSetup.foodBaitEatMaxDuration"] = "16",
                ["Wildlife.tamingPetInteractionSequence"] = (petSequence ?? NativeTamingRuntime.PlayerPetSequence).ToString(),
            };
            Templates[e] = template ?? Guid.NewGuid();
            Parts[(e, "DynamicWildlife")] = new byte[NativeTamingRuntime.DynamicSize];
            Alive.Add(e);
        }

        public void Set(EntityId e, byte state, uint bait = 0, byte progress = 0)
        {
            var d = Parts[(e, "DynamicWildlife")];
            d[NativeTamingRuntime.State] = state;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(NativeTamingRuntime.Bait), bait);
            d[NativeTamingRuntime.Progress] = progress;
        }
    }

    private readonly FakeWorld _w = new();
    private readonly List<KilnEvent> _events = new();
    private readonly IKilnBackend _was = Kiln.Backend.Current;

    public NativeTamingTests()
    {
        Kiln.Backend.Current = new Backend();
        NativeTamingRuntime.Reset();
        NativeTamingRuntime.World = _w;
        NativeTamingRuntime.Available = () => true;
        NativeTamingRuntime.Sink = _events.Add;
        NativeTamingRuntime.Listening = () => true;
        _w.AddDog(Dog);
    }

    public void Dispose()
    {
        NativeTamingRuntime.Reset();
        Kiln.Backend.Current = _was;
    }

    [Fact]
    public void A_wild_dog_reads_as_the_game_has_it()
    {
        var s = NativeTaming.Read(Dog)!;
        Assert.Equal((true, 0, 2, NativeTamingPhase.Wild, DogBait, 8.5, 16.0), (s.Tameable, s.Progress, s.Required, s.Phase, s.BaitTemplate, s.SensorRangeMeters, s.EatSeconds));
        Assert.Null(s.TamedBy);
        _w.Set(Dog, 14, bait: 9001);
        Assert.Equal((NativeTamingPhase.PursuingBait, new EntityId(9001)), (NativeTaming.Read(Dog)!.Phase, NativeTaming.Read(Dog)!.Bait));
        _w.Set(Dog, 15, bait: 9001, progress: 1);
        Assert.Equal((NativeTamingPhase.EatingBait, 1), (NativeTaming.Read(Dog)!.Phase, NativeTaming.Read(Dog)!.Progress));
        _w.Parts[(Dog, "IsTame")] = new byte[1];
        Assert.Equal(NativeTamingPhase.Tamed, NativeTaming.Read(Dog)!.Phase);
        Assert.Null(NativeTaming.Read(new EntityId(5)));
        _w.AddDog(new EntityId(7), tameable: false);
        Assert.False(NativeTaming.Read(new EntityId(7))!.Tameable);
        _w.AddDog(new EntityId(8));
        _w.Parts[(new EntityId(8), "DynamicWildlife")] = new byte[16];  // another build's layout: refused, not misread
        Assert.Null(NativeTaming.Read(new EntityId(8)));
        _w.Parts[(new EntityId(9), "DynamicWildlife")] = new byte[NativeTamingRuntime.DynamicSize];   // no Wildlife template
        Assert.Null(NativeTaming.Read(new EntityId(9)));
    }

    [Fact]
    public void The_taming_setup_is_read_once_per_template()
    {
        Assert.NotNull(NativeTaming.Read(Dog));
        var reads = _w.FieldReads;
        Assert.True(reads > 0);
        NativeTaming.Read(Dog);
        _w.AddDog(new EntityId(30), template: _w.Templates[Dog]);   // another dog of the same template
        Assert.Equal(2, NativeTaming.Read(new EntityId(30))!.Required);
        Assert.Equal(reads, _w.FieldReads);
    }

    [Fact]
    public void A_step_is_refused_unless_the_animal_eats_its_bait_and_needs_it()
    {
        Assert.Equal("NotEating", Reason(NativeTaming.PetStep(Dog)));
        _w.Set(Dog, 15, bait: 9001, progress: 2);
        Assert.Equal("Complete", Reason(NativeTaming.PetStep(Dog)));
        _w.Set(Dog, 15, bait: 9001, progress: 1);
        var ok = NativeTaming.PetStep(Dog);
        Assert.True(ok.Ok);
        Assert.Equal((Dog, NativeTamingRuntime.AnimalPetSequence), Assert.Single(_w.Played));
        _w.PlayOk = false;
        Assert.Equal("SequenceRefused", Reason(NativeTaming.PetStep(Dog)));
        _w.AddDog(new EntityId(11), petSequence: Guid.NewGuid());
        _w.Set(new EntityId(11), 15, bait: 9002);
        Assert.Equal("OtherPetSequence", Reason(NativeTaming.PetStep(new EntityId(11))));
        _w.AddDog(new EntityId(12), tameable: false);
        Assert.Equal("NotTameable", Reason(NativeTaming.PetStep(new EntityId(12))));
        Assert.Equal("NotWild", Reason(NativeTaming.PetStep(new EntityId(13))));
        _w.Parts[(Dog, "IsTame")] = new byte[1];
        Assert.Equal("Tamed", Reason(NativeTaming.PetStep(Dog)));
        Assert.Equal(2, _w.Played.Count);  // the step and the refused play; nothing else played
        NativeTamingRuntime.Available = () => false;
        Assert.Equal(NativeTaming.Capability, Assert.Throws<KilnUnavailableException>(() => NativeTaming.PetStep(Dog)).Capability);
    }

    private static string Reason(NativeStepResult r) => r.Ok ? "ok" : r.Reason!.Split(':')[0];

    [Fact]
    public async Task Its_own_bait_is_placed_in_front_of_it_and_nothing_else_is()
    {
        _w.YawDeg = 90;  // facing +X
        var bait = await NativeTaming.PlaceBait(Dog);
        var (template, at) = Assert.Single(_w.Spawned);
        Assert.Equal(DogBait, template);
        Assert.Equal(12.5, at.X, 3);
        Assert.Equal(20, at.Z, 3);
        Assert.False(bait.IsNone);
        await NativeTaming.PlaceBait(Dog, new WorldPosition(1, 2, 3));
        Assert.Equal(new WorldPosition(1, 2, 3), _w.Spawned[1].At);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = NativeTaming.PlaceBait(Dog, distance: 9); });
        _w.AddDog(new EntityId(7), tameable: false);
        Assert.Throws<ArgumentException>(() => { _ = NativeTaming.PlaceBait(new EntityId(7)); });
    }

    [Fact]
    public void The_watch_reports_steps_startles_wasted_baits_and_the_tame()
    {
        NativeTamingRuntime.Tick(5);    // first look: the baseline, no event
        Assert.Empty(_events);
        _w.Alive.Add(new EntityId(9001));
        _w.Set(Dog, 15, bait: 9001);
        NativeTamingRuntime.Tick(10);
        Assert.Empty(_events);
        // eaten to the end without a petting: the bait is gone, no step
        _w.Alive.Remove(new EntityId(9001));
        _w.Set(Dog, 0);
        NativeTamingRuntime.Tick(15);
        var wasted = Assert.IsType<NativeBaitEatenEvent>(Assert.Single(_events));
        Assert.Equal((Dog, new EntityId(9001), false), (wasted.Creature, wasted.Bait, wasted.Counted));
        // a step: the bait consumed with progress + 1
        _events.Clear();
        _w.Alive.Add(new EntityId(9002));
        _w.Set(Dog, 15, bait: 9002);
        NativeTamingRuntime.Tick(20);
        _w.Alive.Remove(new EntityId(9002));
        _w.Set(Dog, 0, progress: 1);
        NativeTamingRuntime.Tick(25);
        var p = _events.OfType<NativeTamingProgressEvent>().Single();
        Assert.Equal((0, 1, 2, NativeTamingCause.Step), (p.Before, p.After, p.Required, p.Cause));
        Assert.True(_events.OfType<NativeBaitEatenEvent>().Single().Counted);
        // a startle costs it
        _events.Clear();
        _w.Set(Dog, 9, progress: 0);
        NativeTamingRuntime.Tick(30);
        Assert.Equal(NativeTamingCause.Startle, _events.OfType<NativeTamingProgressEvent>().Single().Cause);
        // two steps and the farm transformation
        _events.Clear();
        _w.Set(Dog, 0, progress: 2);
        _w.Parts[(Dog, "IsTame")] = new byte[1];
        NativeTamingRuntime.Tick(35);
        Assert.Equal(2, _events.OfType<NativeTamingProgressEvent>().Single().After);
        Assert.Equal(Dog, _events.OfType<NativeTamedEvent>().Single().Creature);
        // nothing changes, nothing is posted; the off-steps do no work
        _events.Clear();
        NativeTamingRuntime.Tick(40);
        NativeTamingRuntime.Tick(41);
        Assert.Empty(_events);
    }

    [Fact]
    public void Nobody_listening_reads_nothing()
    {
        NativeTamingRuntime.Listening = () => false;
        _w.Parts.Clear();
        NativeTamingRuntime.Tick(5);  // a Find would see no parts; nothing throws, nothing is posted
        Assert.Empty(_events);
    }
}
