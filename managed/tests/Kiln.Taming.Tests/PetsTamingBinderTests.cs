// SPDX-License-Identifier: MIT
// A finished Kiln tame binds a Kiln.Pets record (PetsTamingBinder): a roster slot is held from Begin and counted against the
// definition's cap, the final check is transient when the roster is full, the bind is Pets.Grant (a new operation each try,
// its refusals mapped), and after the commit the wild body goes and the pet is summoned as the definition says; a
// definition with no pet binds nothing. Then the engine with this binder, end to end over fakes.
using Kiln;
using Xunit;
using static Kiln.TamingTests.TamingFixture;

namespace Kiln.TamingTests;

public sealed class PetsTamingBinderTests
{
    private static readonly CharacterKey Owner = new(1, Guid.NewGuid());
    private readonly List<PetSnapshot> _pets = new();
    private readonly List<(string Pet, Guid Operation)> _grants = new();
    private readonly List<PetId> _summons = new();
    private readonly List<EntityRef> _removed = new();
    private readonly List<string> _log = new();
    private PetResult? _grantAnswer;
    private TaskCompletionSource<PetResult>? _grantHold;

    private static PetDefinition Wolf(int cap = 2) => new() { Id = "mod-a:pet-wolf", Creature = "game:enemy_wildbeast_wolf", Caps = new PetCaps { RetainedPerCharacter = cap } };

    private PetsTamingBinder Binder(Func<string, TamingDefinition?>? defs = null, bool character = true) => new(defs ?? (_ => null))
    {
        CharacterOf = _ => character ? Owner : null,
        PetDefinition = id => id == "mod-a:pet-wolf" ? Wolf() : null,
        PetsOf = _ => _pets,
        Grant = (owner, pet, req) =>
        {
            _grants.Add((pet, req.OperationId));
            if (_grantHold is { } h) return h.Task;
            return Task.FromResult(_grantAnswer ?? new PetResult(true, null, new PetSnapshot { Id = new PetId(Guid.NewGuid()), Definition = pet }, req.OperationId, []));
        },
        Summon = (who, pet, req) =>
        {
            _summons.Add(pet);
            return Task.FromResult(new PetResult(true, null, null, req.OperationId, []));
        },
        RemoveTarget = t =>
        {
            _removed.Add(t);
            return true;
        },
        Log = _log.Add,
    };

    private static TamingBindRequest Request(TamingDefinition d, TameAttemptId? id = null) =>
        new(id ?? new TameAttemptId(Guid.NewGuid()), new PlayerRef(7, Guid.NewGuid(), new EntityRef(new EntityId(10), 1)), new EntityRef(new EntityId(500), 7), d, Guid.NewGuid());

    private static TamingDefinition WithPet(bool remove = true, bool summon = true) =>
        Def(new WeakenStep(1)) with { Pet = "mod-a:pet-wolf", RemoveTarget = remove, SummonPet = summon };

    [Fact]
    public void A_definition_with_no_pet_binds_nothing()
    {
        var b = Binder();
        var r = Request(Def(new WeakenStep(1)));
        Assert.Null(b.Reserve(r));
        Assert.Null(b.Preflight(r));
        var bound = b.Bind(r).Result;
        Assert.True(bound.Ok);
        Assert.Null(bound.Pet);
        b.Committed(r, bound);
        Assert.Empty(_grants);
        Assert.Empty(_removed);
        Assert.Equal(0, b.Held);
    }

    [Fact]
    public void Holds_count_against_the_definition_s_cap_until_released()
    {
        var b = Binder();
        _pets.Add(new PetSnapshot { Id = new PetId(Guid.NewGuid()), Definition = "mod-a:pet-wolf" });
        var first = Request(WithPet());
        Assert.Null(b.Reserve(first));                       // 1 record + 1 hold = the cap of 2
        var second = Request(WithPet());
        Assert.StartsWith("Cap", b.Reserve(second));
        Assert.Null(b.Preflight(first));                     // its own hold is not counted against it
        _pets.Add(new PetSnapshot { Id = new PetId(Guid.NewGuid()), Definition = "mod-a:pet-wolf" });   // granted elsewhere meanwhile
        var full = b.Preflight(first);
        Assert.True(full is { Transient: true });            // a player can release a pet and retry
        b.Release(first);
        Assert.Equal(0, b.Held);
    }

    [Fact]
    public void An_unknown_pet_definition_or_no_character_refuses_for_good()
    {
        var bad = Def(new WeakenStep(1)) with { Pet = "mod-a:nope" };
        Assert.StartsWith("DefinitionUnknown", Binder().Reserve(Request(bad)));
        Assert.True(Binder().Preflight(Request(bad)) is { Transient: false });
        Assert.StartsWith("NoCharacter", Binder(character: false).Reserve(Request(WithPet())));
    }

    [Fact]
    public void The_bind_is_a_grant_and_its_refusals_map_to_transient_or_final()
    {
        var b = Binder();
        var ok = b.Bind(Request(WithPet())).Result;
        Assert.True(ok.Ok);
        Assert.Equal("mod-a:pet-wolf", ok.Pet!.Definition);
        _grantAnswer = new PetResult(false, "Capacity: the character keeps 2 pets of mod-a:pet-wolf already", null, Guid.NewGuid(), []);
        Assert.True(b.Bind(Request(WithPet())).Result is { Ok: false, Transient: true });
        _grantAnswer = new PetResult(false, "RecordsUnreadable: newer Kiln", null, Guid.NewGuid(), []);
        Assert.True(b.Bind(Request(WithPet())).Result is { Ok: false, Transient: false });
        Assert.Equal(3, _grants.Select(g => g.Operation).Distinct().Count());   // a new operation each try
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void After_the_commit_the_wild_body_goes_and_the_pet_comes_out_as_the_definition_says(bool remove, bool summon)
    {
        var b = Binder();
        var r = Request(WithPet(remove, summon));
        var bound = b.Bind(r).Result;
        b.Committed(r, bound);
        Assert.Equal(remove ? 1 : 0, _removed.Count);
        Assert.Equal(summon ? new[] { bound.Pet!.Id } : [], _summons.ToArray());
    }

    [Fact]
    public void End_to_end_a_finished_tame_binds_one_pet_record()
    {
        var f = new TamingFixture();
        var binder = Binder(f.Engine.Definition);
        f.Engine.Binder = binder;
        f.With(Def(new WeakenStep(1), new FeedStep(Bait)) with { Pet = "mod-a:pet-wolf" });
        var p = f.Begin().Progress!;
        Assert.Equal(1, binder.Held);
        p = f.Advance(p).Progress!;
        _grantHold = new TaskCompletionSource<PetResult>();
        var t = f.Engine.Advance(f.A, p.Id, Token(p), f.Stack(Bait));
        Assert.False(t.IsCompleted);                          // the record is made at the roster's next step
        var pet = new PetSnapshot { Id = new PetId(Guid.NewGuid()), Definition = "mod-a:pet-wolf" };
        _grantHold.SetResult(new PetResult(true, null, pet, Guid.NewGuid(), []));
        f.Tick();
        Assert.True(t.Result.Ok, t.Result.Reason);
        Assert.Same(pet, t.Result.Pet);
        Assert.Same(pet, f.Events.OfType<TamedEvent>().Single().Pet);
        Assert.Single(_grants);
        Assert.Equal(new[] { f.Wolf }, _removed.ToArray());
        Assert.Equal(new[] { pet.Id }, _summons.ToArray());
        Assert.Equal(0, binder.Held);
    }
}
