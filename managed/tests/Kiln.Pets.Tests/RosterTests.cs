// SPDX-License-Identifier: MIT
// Package 2 (19d §4.1, §4.2, §4.7): every row of the life/body table with its life, presence, health, order and event
// sequence, idempotent repeats posting nothing; caps; release tombstones; logout and login; the exact save receipt
// (gate 6: a snapshot that captured 17 acknowledges 17, not the 18 made while the file was written).
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed class RosterTests : IDisposable
{
    internal readonly FakeWorld World = new();
    internal readonly FakeStore Store = new();
    internal readonly List<KilnEvent> Events = [];
    internal readonly PetRoster Roster;
    internal readonly CharacterKey A = new(0xA, new Guid("aaaaaaaa-0000-0000-0000-00000000000a"));
    internal readonly CharacterKey B = new(0xA, new Guid("bbbbbbbb-0000-0000-0000-00000000000b"));
    internal PetDownedOutcome? DowningAnswer;

    public RosterTests()
    {
        PetsRuntime.ResetForTests();
        PetCreatures.ResetForTests();
        PetCreatures.Source = new FakeCreatures();
        Pets.DefineAs(new PetDefinition { Id = "mod-a:wolf", Creature = "game:enemy_wildbeast_wolf" }, "mod-a");
        Pets.DefineAs(new PetDefinition { Id = "mod-a:loyal", Creature = "game:enemy_wildbeast_wolf", Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Release } }, "mod-a");
        Pets.DefineAs(new PetDefinition { Id = "mod-a:few", Creature = "game:enemy_wildbeast_wolf", Limits = new PetLimits { RetainedPerCharacter = 1 } }, "mod-a");
        PetsRuntime.DrainEventsForTests(out _);
        Roster = new PetRoster(World, Store) { Post = Events.Add, AskDowning = e => { if (DowningAnswer is { } a) e.Outcome = a; return e; } };
    }

    public void Dispose()
    {
        Roster.Detach();
        PetsRuntime.ResetForTests();
        PetCreatures.ResetForTests();
    }

    internal void Step(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            Roster.Step();
            World.Step++;
            World.Now += 1 / 60.0;
        }
    }

    internal PlayerRef Who(CharacterKey key) => new(0, key.Id, World.Online[key].Entity);

    internal PetResult Run(Task<PetResult> t, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps && !t.IsCompleted; i++) Step();
        Assert.True(t.IsCompleted, "the request did not complete");
        return t.Result;
    }

    internal PetRoster.Op Op(PetRoster.Kind kind, CharacterKey owner, PetId pet, Guid? id = null, ulong expected = 0) => new()
    {
        Kind = kind, OperationId = id ?? Guid.NewGuid(), OwnerKey = owner, Who = World.Online.ContainsKey(owner) ? Who(owner) : default, Pet = pet,
        Expected = expected, Payload = $"{kind}|{pet}|{expected}",
    };

    internal PetSnapshot Grant(CharacterKey owner, string def = "mod-a:wolf")
    {
        var r = Run(Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Grant, OperationId = Guid.NewGuid(), OwnerKey = owner, Definition = def, Payload = "grant|" + def }));
        Assert.True(r.Ok, r.Reason);
        return r.Pet!;
    }

    internal PetResult Do(PetRoster.Kind kind, CharacterKey owner, PetId pet, Guid? id = null, ulong expected = 0, int maxStepsOverride = 20) =>
        Run(Roster.Submit(Op(kind, owner, pet, id, expected)), maxStepsOverride);

    internal List<string> Names() { var n = Events.Select(e => e.GetType().Name.Replace("Pet", "").Replace("Event", "")).ToList(); Events.Clear(); return n; }

    internal FakeWorld.FakeBody BodyOf(PetId pet) => World.Bodies[Roster.Get(pet)!.Body.Id.Value];

    internal PetSnapshot Summoned(CharacterKey owner)
    {
        World.Join(owner, new WorldPosition(10, 0, 10));
        var pet = Grant(owner);
        var r = Do(PetRoster.Kind.Summon, owner, pet.Id);
        Assert.True(r.Ok, r.Reason);
        Events.Clear();
        return r.Pet!;
    }

    // ---- grant ----

    [Fact]
    public void A_granted_pet_is_ready_absent_with_a_random_id()
    {
        World.Join(A);
        var p = Grant(A);
        Assert.False(p.Id.IsNone);
        Assert.Equal((PetLife.Ready, PetPresence.Absent, PetTransition.None), (p.Life, p.Presence, p.Transition));
        Assert.False(p.WantsSummoned);
        Assert.Equal(1ul, p.StateRevision);
        Assert.Equal(["Granted"], Names());
        Assert.NotEqual(p.Id, Grant(A).Id);
    }

    [Fact]
    public void A_definitions_cap_and_an_unknown_definition_refuse_a_grant()
    {
        World.Join(A);
        Grant(A, "mod-a:few");
        var again = Run(Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Grant, OperationId = Guid.NewGuid(), OwnerKey = A, Definition = "mod-a:few", Payload = "g" }));
        Assert.False(again.Ok);
        Assert.StartsWith("Capacity", again.Reason);
        var unknown = Run(Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Grant, OperationId = Guid.NewGuid(), OwnerKey = A, Definition = "mod-a:none", Payload = "g2" }));
        Assert.StartsWith("DefinitionUnknown", unknown.Reason);
    }

    // ---- 4.2 rows ----

    [Fact]
    public void Ready_absent_summon_makes_a_protected_body_following_and_wanted()
    {
        World.Join(A, new WorldPosition(10, 0, 10));
        var pet = Grant(A);
        Events.Clear();
        var r = Do(PetRoster.Kind.Summon, A, pet.Id);
        Assert.True(r.Ok, r.Reason);
        var p = r.Pet!;
        Assert.Equal((PetLife.Ready, PetPresence.Live, PetTransition.None), (p.Life, p.Presence, p.Transition));
        Assert.True(p.WantsSummoned);
        Assert.Equal(PetOrder.Follow, p.RequestedOrder);
        Assert.False(p.Body.IsNone);
        Assert.NotEqual(0ul, p.BodyGeneration);
        Assert.Equal(["TransitionChanged", "Summoned"], Names());
        Assert.True(World.Bodies[p.Body.Id.Value].Active);
    }

    [Fact]
    public void Ready_live_summon_is_idempotent_with_no_health_reset_or_event()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 40;
        Step();
        Events.Clear();
        var again = Do(PetRoster.Kind.Summon, A, p.Id);
        Assert.True(again.Ok);
        Assert.Equal(40, again.Pet!.Health);
        Assert.Empty(Names());
        Assert.Single(World.Bodies);
    }

    [Fact]
    public void Ready_live_dismiss_retires_the_body_keeps_health_and_clears_the_wish()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 30;
        var r = Do(PetRoster.Kind.Dismiss, A, p.Id);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal((PetLife.Ready, PetPresence.Absent), (r.Pet!.Life, r.Pet.Presence));
        Assert.False(r.Pet.WantsSummoned);
        Assert.True(r.Pet.Body.IsNone);
        Assert.Equal(30, r.Pet.Health);
        Assert.Empty(World.Bodies);
        Assert.Equal(["StatsChanged", "TransitionChanged", "BodyRetired", "Dismissed"], Names());
        // a new summon comes back at the kept health: dismissing never heals
        var back = Do(PetRoster.Kind.Summon, A, p.Id);
        Assert.Equal(30, back.Pet!.Health);
    }

    [Fact]
    public void Ready_absent_dismiss_posts_only_when_the_wish_changed()
    {
        World.Join(A);
        var p = Grant(A);
        Events.Clear();
        Assert.True(Do(PetRoster.Kind.Dismiss, A, p.Id).Ok);
        Assert.Empty(Names());
    }

    [Fact]
    public void A_lethal_body_goes_down_to_a_recovery_anchor_after_its_body_is_gone()
    {
        var p = Summoned(A);
        var body = BodyOf(p.Id);
        body.Position = new WorldPosition(12, 0, 10);
        Step();
        body.Health = 0;
        Step();
        var d = Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Downed, PetPresence.RecoveryAnchor), (d.Life, d.Presence));
        Assert.Equal(0, d.Health);
        Assert.True(d.Body.IsNone);
        Assert.Equal(new WorldPosition(12, 0, 10), d.RecoveryAnchor);
        Assert.Empty(World.Bodies);
        Assert.Equal(["TransitionChanged", "BodyRetired", "Downed"], Names());
        Assert.Contains("retire", World.Log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_downed_pet_is_the_mods_choice_absent_or_released()
    {
        var p = Summoned(A);
        DowningAnswer = PetDownedOutcome.Absent;
        BodyOf(p.Id).Health = 0;
        Step();
        Assert.Equal((PetLife.Downed, PetPresence.Absent), (Roster.Get(p.Id)!.Life, Roster.Get(p.Id)!.Presence));
        DowningAnswer = null;
        var loyal = Grant(A, "mod-a:loyal");
        Assert.True(Do(PetRoster.Kind.Summon, A, loyal.Id).Ok);
        BodyOf(loyal.Id).Dead = true;
        Events.Clear();
        Step();
        Assert.Equal(PetLife.Released, Roster.Get(loyal.Id)!.Life);
        Assert.Equal(["TransitionChanged", "BodyRetired", "Downed", "Released"], Names());
        Assert.DoesNotContain(Roster.Of(A), x => x.Id == loyal.Id);
    }

    [Fact]
    public void A_lost_body_is_down_and_never_rebuilt_by_itself()
    {
        var p = Summoned(A);
        World.Bodies.Clear();
        Step();
        var d = Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Equal(["BodyLost", "Downed"], Names());
        Step(30);
        Assert.Empty(World.Bodies);
        Assert.Equal(PetLife.Downed, Roster.Get(p.Id)!.Life);
    }

    [Fact]
    public void The_events_of_one_change_share_its_revision()
    {
        var p = Summoned(A);
        World.Bodies.Clear();
        Step();
        var changes = Events.OfType<PetChangeEvent>().ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal(changes[0].Change.After.StateRevision, changes[1].Change.After.StateRevision);
        Assert.Equal(p.StateRevision + 1, changes[0].Change.After.StateRevision);
    }

    [Fact]
    public void Downed_dismiss_hides_the_anchor_and_never_heals()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 0;
        Step();
        Events.Clear();
        var r = Do(PetRoster.Kind.Dismiss, A, p.Id);
        Assert.True(r.Ok);
        Assert.Equal((PetLife.Downed, PetPresence.Absent, 0), (r.Pet!.Life, r.Pet.Presence, r.Pet.Health));
        Assert.False(r.Pet.WantsSummoned);
        Assert.Equal(["RecoveryAnchorChanged", "Dismissed"], Names());
    }

    [Fact]
    public void Downed_absent_summon_shows_the_anchor_next_to_the_owner_with_no_body_or_heal()
    {
        var p = Summoned(A);
        DowningAnswer = PetDownedOutcome.Absent;
        BodyOf(p.Id).Health = 0;
        Step();
        Events.Clear();
        World.Move(A, new WorldPosition(100, 0, 100));
        var r = Do(PetRoster.Kind.Summon, A, p.Id);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal((PetLife.Downed, PetPresence.RecoveryAnchor, 0), (r.Pet!.Life, r.Pet.Presence, r.Pet.Health));
        Assert.True(r.Pet.RecoveryAnchor!.Value.DistanceTo(new WorldPosition(100, 0, 100)) <= 3);
        Assert.Empty(World.Bodies);
        Assert.Equal(["RecoveryAnchorChanged", "Summoned"], Names());
    }

    [Fact]
    public void Downed_anchor_summon_reanchors_only_past_three_metres()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 0;
        Step();
        World.Move(A, Roster.Get(p.Id)!.RecoveryAnchor!.Value);
        Events.Clear();
        Assert.True(Do(PetRoster.Kind.Summon, A, p.Id).Ok);
        Assert.Empty(Names());
        World.Move(A, new WorldPosition(50, 0, 50));
        Assert.True(Do(PetRoster.Kind.Summon, A, p.Id).Ok);
        Assert.Equal(["RecoveryAnchorChanged"], Names());
    }

    [Fact]
    public void Release_of_a_live_pet_retires_its_body_then_leaves_a_tombstone()
    {
        var p = Summoned(A);
        var r = Do(PetRoster.Kind.Release, A, p.Id);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(PetLife.Released, r.Pet!.Life);
        Assert.Empty(World.Bodies);
        Assert.Equal(["TransitionChanged", "BodyRetired", "Released"], Names());
        Assert.Empty(Roster.Of(A));
        Assert.NotNull(Roster.Get(p.Id));
        var again = Do(PetRoster.Kind.Summon, A, p.Id);
        Assert.StartsWith("AlreadyReleased", again.Reason);
    }

    [Fact]
    public void A_failed_retirement_keeps_the_record_and_its_protection()
    {
        var p = Summoned(A);
        World.RetireRefuses = true;
        var r = Do(PetRoster.Kind.Release, A, p.Id, maxStepsOverride: PetRoster.RetireRetries + 5);
        Assert.False(r.Ok);
        Assert.StartsWith("RetireFailed", r.Reason);
        Assert.Equal((PetLife.Ready, PetPresence.Live), (Roster.Get(p.Id)!.Life, Roster.Get(p.Id)!.Presence));
        Assert.Single(World.Bodies);
    }

    [Fact]
    public void Requests_during_a_transition_are_busy_and_a_repeat_of_the_same_operation_is_the_same_answer()
    {
        World.Join(A, new WorldPosition(10, 0, 10));
        World.ReadySteps = 4;
        var pet = Grant(A);
        var id = Guid.NewGuid();
        var first = Roster.Submit(Op(PetRoster.Kind.Summon, A, pet.Id, id));
        Step(2);
        Assert.Equal(PetTransition.Spawning, Roster.Get(pet.Id)!.Transition);
        var busy = Do(PetRoster.Kind.Dismiss, A, pet.Id);
        Assert.StartsWith("Busy", busy.Reason);
        var repeat = Roster.Submit(Op(PetRoster.Kind.Summon, A, pet.Id, id));
        Assert.Same(first, repeat);
        var conflict = Roster.Submit(Op(PetRoster.Kind.Dismiss, A, pet.Id, id));
        Assert.StartsWith("RequestConflict", conflict.Result.Reason);
        Assert.True(Run(first).Ok);
    }

    [Fact]
    public void A_stale_expected_revision_refuses_with_no_change()
    {
        var p = Summoned(A);
        var r = Do(PetRoster.Kind.Dismiss, A, p.Id, expected: p.StateRevision - 1);
        Assert.StartsWith("StaleRevision", r.Reason);
        Assert.Equal(PetPresence.Live, Roster.Get(p.Id)!.Presence);
        Assert.True(Do(PetRoster.Kind.Dismiss, A, p.Id, expected: Roster.Get(p.Id)!.StateRevision).Ok);
    }

    [Fact]
    public void Another_characters_request_is_refused()
    {
        var p = Summoned(A);
        World.Join(B);
        var r = Do(PetRoster.Kind.Dismiss, B, p.Id);
        Assert.StartsWith("NotOwner", r.Reason);
    }

    [Fact]
    public void The_live_cap_is_the_definitions_and_a_failed_spawn_rolls_back_only_the_reservation()
    {
        var p = Summoned(A);
        var second = Grant(A);
        var r = Do(PetRoster.Kind.Summon, A, second.Id);
        Assert.StartsWith("Capacity", r.Reason);
        Assert.True(Do(PetRoster.Kind.Dismiss, A, p.Id).Ok);
        World.CreateRefusal = "SpawnRefused: test";
        var refused = Do(PetRoster.Kind.Summon, A, second.Id);
        Assert.StartsWith("SpawnRefused", refused.Reason);
        Assert.Equal((PetLife.Ready, PetPresence.Absent, PetTransition.None), (refused.Pet!.Life, refused.Pet.Presence, refused.Pet.Transition));
    }

    [Fact]
    public void A_body_not_ready_within_five_seconds_is_retired_and_the_record_stays()
    {
        World.Join(A, new WorldPosition(10, 0, 10));
        World.ReadySteps = 1000;
        var pet = Grant(A);
        var r = Do(PetRoster.Kind.Summon, A, pet.Id, maxStepsOverride: 400);
        Assert.StartsWith("BodyNotReady", r.Reason);
        Assert.Empty(World.Bodies);
        Assert.Equal(PetPresence.Absent, r.Pet!.Presence);
    }

    [Fact]
    public void No_safe_ground_refuses_before_a_body_is_made()
    {
        World.Join(A);
        World.GroundOn = false;
        var pet = Grant(A);
        Assert.StartsWith("NoSafeGround", Do(PetRoster.Kind.Summon, A, pet.Id).Reason);
        Assert.Empty(World.Bodies);
    }

    // ---- logout, login, world end ----

    [Fact]
    public void Logout_retires_the_body_keeps_the_wish_and_login_brings_it_back()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 70;
        Step();
        World.Leave(A);
        Step();
        var gone = Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Ready, PetPresence.Absent), (gone.Life, gone.Presence));
        Assert.True(gone.WantsSummoned);
        Assert.Empty(World.Bodies);
        World.Join(A, new WorldPosition(5, 0, 5));
        Step(4);
        var back = Roster.Get(p.Id)!;
        Assert.Equal(PetPresence.Live, back.Presence);
        Assert.Equal(70, back.Health);
        Assert.NotEqual(p.BodyGeneration, back.BodyGeneration);
    }

    [Fact]
    public void A_downed_pet_comes_back_at_login_as_its_anchor_never_its_health()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 0;
        Step();
        World.Leave(A);
        Step();
        Assert.Equal(PetPresence.Absent, Roster.Get(p.Id)!.Presence);
        World.Join(A, new WorldPosition(40, 0, 40));
        Step(3);
        var back = Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Downed, PetPresence.RecoveryAnchor, 0), (back.Life, back.Presence, back.Health));
        Assert.Empty(World.Bodies);
    }

    [Fact]
    public void The_world_ending_ends_requests_and_restores_nothing_into_the_next_world()
    {
        World.Join(A, new WorldPosition(10, 0, 10));
        World.ReadySteps = 50;
        var pet = Grant(A);
        var t = Roster.Submit(Op(PetRoster.Kind.Summon, A, pet.Id));
        Step(2);
        World.WorldEpoch = 2;
        Step();
        Assert.True(t.IsCompleted);
        Assert.StartsWith("WorldEnded", t.Result.Reason);
        Assert.Null(Roster.Get(pet.Id));
    }

    // review of dev.32 pets (19d §5.3: closed world ids never replay): the operation ids of the world that ended were still
    // retained, so the same id in the next world got the old world's answer (here: a pet that does not exist there)
    [Fact]
    public void An_operation_id_of_a_world_that_ended_is_a_new_request_in_the_next()
    {
        World.Join(A);
        var id = Guid.NewGuid();
        PetRoster.Op GrantOp() => new() { Kind = PetRoster.Kind.Grant, OperationId = id, OwnerKey = A, Definition = "mod-a:wolf", Payload = "grant|mod-a:wolf" };
        var first = Run(Roster.Submit(GrantOp()));
        Assert.True(first.Ok);
        World.WorldEpoch = 2;
        Step();
        var again = Run(Roster.Submit(GrantOp()));
        Assert.True(again.Ok, again.Reason);
        Assert.NotEqual(first.Pet!.Id, again.Pet!.Id);
        Assert.NotNull(Roster.Get(again.Pet.Id));   // a pet of this world
    }

    // ---- the save ----

    [Fact]
    public void A_record_survives_a_restart_with_its_id_and_session_fields_normalized()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 55;
        Step();
        Store.Stage(1, snapshot: true);
        Store.Commit(1);
        var restarted = new PetRoster(new FakeWorld(), Store) { Post = _ => { } };
        var back = restarted.Of(A).Single();
        Assert.Equal(p.Id, back.Id);
        Assert.Equal((PetLife.Ready, PetPresence.Absent), (back.Life, back.Presence));
        Assert.True(back.Body.IsNone);
        Assert.True(back.WantsSummoned);
        Assert.Equal(55, back.Health);
        Assert.Equal(back.StateRevision, back.DurableRevision);
        restarted.Detach();
    }

    [Fact]
    public void The_receipt_acknowledges_exactly_the_serialized_revision()
    {
        var p = Summoned(A);
        Store.Stage(7, snapshot: false);
        Store.Stage(7, snapshot: true);
        var captured = Roster.Get(p.Id)!.StateRevision;
        BodyOf(p.Id).Health = 10;   // a change while the file is written
        Step();
        var now = Roster.Get(p.Id)!.StateRevision;
        Assert.Equal(captured + 1, now);
        Events.Clear();
        Store.Commit(7);
        var after = Roster.Get(p.Id)!;
        Assert.Equal(captured, after.DurableRevision);
        Assert.Equal(now, after.StateRevision);
        Assert.Equal(7u, after.LastCommit!.WorldRevision);
        var committed = Assert.Single(Events.OfType<PetRecordCommittedEvent>());
        Assert.Equal(captured, committed.CapturedStateRevision);
    }

    [Fact]
    public void A_failed_save_acknowledges_nothing()
    {
        var p = Summoned(A);
        var before = Roster.Get(p.Id)!.DurableRevision;
        Store.Stage(3, snapshot: true);
        // no commit (the world write failed)
        Store.Commit(4);   // another generation that was never staged
        Assert.Equal(before, Roster.Get(p.Id)!.DurableRevision);
    }

    [Fact]
    public void A_lethal_reading_at_the_snapshot_is_written_downed_and_retired_with_that_revision()
    {
        var p = Summoned(A);
        BodyOf(p.Id).Health = 0;            // lethal after this step's Pre, before the save
        Store.Stage(9, snapshot: true);     // the final staging re-reads the body
        var doc = PetRecordJson.Read(Store.Docs[A], A)!;
        var rec = doc.Pets.Single();
        Assert.Equal(PetLife.Downed, rec.Life);
        Assert.Equal(0, rec.HealthFraction);
        Step();                              // the retirement and the events follow, with the written revision
        var d = Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Equal(rec.StateRevision, d.StateRevision);
        Assert.Empty(World.Bodies);
        Assert.Contains("Downed", Names());
        // a crash after that save: the record loads Downed with no body
        var restarted = new PetRoster(new FakeWorld(), Store) { Post = _ => { } };
        Assert.Equal(PetLife.Downed, restarted.Of(A).Single().Life);
        restarted.Detach();
    }

    [Fact]
    public void A_released_tombstone_stays_until_a_committed_save_holds_it()
    {
        var p = Summoned(A);
        Assert.True(Do(PetRoster.Kind.Release, A, p.Id).Ok);
        Store.Stage(2, snapshot: true);
        Assert.Contains("\"life\":2", Store.Docs[A], StringComparison.Ordinal);
        Assert.NotNull(Roster.Get(p.Id));
        Store.Commit(2);
        Assert.Null(Roster.Get(p.Id));
        Store.Stage(3, snapshot: true);
        Assert.False(Store.Docs.ContainsKey(A));
    }

    [Fact]
    public void Records_of_another_character_on_the_same_account_are_apart()
    {
        var p = Summoned(A);
        World.Join(B);
        Assert.Empty(Roster.Of(B));
        Assert.Single(Roster.Of(A));
        Store.Stage(1, snapshot: true);
        Assert.False(Store.Docs.ContainsKey(B));
    }

    [Fact]
    public void An_unreadable_document_is_kept_and_never_overwritten()
    {
        Store.Docs[A] = "{\"schema\":99,\"pets\":[]}";
        World.Join(A);
        Step();
        var g = Run(Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Grant, OperationId = Guid.NewGuid(), OwnerKey = A, Definition = "mod-a:wolf", Payload = "g" }));
        Assert.StartsWith("RecordsUnreadable", g.Reason);
        Store.Stage(1, snapshot: true);
        Assert.Equal("{\"schema\":99,\"pets\":[]}", Store.Docs[A]);
    }

    // review of dev.32 pets: a record that did not read (or named a pet another character holds) was left out of the roster
    // and so out of the character's next document: the next save of another change erased it for good
    [Fact]
    public void A_record_that_does_not_read_is_written_back_as_it_was()
    {
        var good = Guid.NewGuid().ToString("N");
        Store.Docs[A] = "{\"schema\":1,\"pets\":[{\"id\":\"" + good + "\",\"definition\":\"mod-a:wolf\",\"life\":0,\"stateRevision\":3}," +
                        "{\"id\":\"not-a-guid\",\"definition\":\"mod-a:wolf\",\"life\":0,\"note\":\"kept\"}]}";
        Store.Docs[B] = "{\"schema\":1,\"pets\":[{\"id\":\"" + good + "\",\"definition\":\"mod-a:wolf\",\"life\":0,\"stateRevision\":9}]}";
        World.Join(A);
        World.Join(B);
        Assert.Single(Roster.Of(A));
        Assert.Empty(Roster.Of(B));   // one PetId, one owner
        Grant(A);                      // both documents are written again
        Grant(B);
        Store.Stage(1, snapshot: true);
        Assert.Contains("{\"id\":\"not-a-guid\",\"definition\":\"mod-a:wolf\",\"life\":0,\"note\":\"kept\"}", Store.Docs[A], StringComparison.Ordinal);
        Assert.Equal(2, PetRecordJson.Read(Store.Docs[A], A)!.Pets.Count);
        Assert.Contains(good, Store.Docs[B], StringComparison.Ordinal);
        Assert.Equal(2, PetRecordJson.Read(Store.Docs[B], B)!.Pets.Count);
    }

    [Fact]
    public void A_document_with_a_schema_that_is_not_a_number_is_kept_and_never_overwritten()
    {
        Store.Docs[A] = "{\"schema\":\"one\",\"pets\":[]}";
        World.Join(A);
        Step();
        Assert.Empty(Roster.Of(A));
        Store.Stage(1, snapshot: true);
        Assert.Equal("{\"schema\":\"one\",\"pets\":[]}", Store.Docs[A]);
    }
}

public sealed class UnloadTests : IDisposable
{
    private readonly RosterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void A_definitions_mod_unloading_retires_its_bodies_and_keeps_the_records()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).Health = 40;
        _t.Step();
        _t.Names();
        _t.Roster.Unload(["mod-a:wolf"]);
        var after = _t.Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Ready, PetPresence.Absent), (after.Life, after.Presence));
        Assert.True(after.WantsSummoned);
        Assert.Equal(40, after.Health);
        Assert.Empty(_t.World.Bodies);
        Assert.Contains("Unload", _t.World.Log[^1], StringComparison.Ordinal);
        var ev = Assert.IsType<PetBodyRetiredEvent>(Assert.Single(_t.Events));
        Assert.Equal(PetChangeCause.Unload, ev.Change.Cause);
    }

    [Fact]
    public void Pets_of_other_definitions_are_untouched()
    {
        var p = _t.Summoned(_t.A);
        _t.Roster.Unload(["mod-b:other"]);
        Assert.Equal(PetPresence.Live, _t.Roster.Get(p.Id)!.Presence);
        Assert.Single(_t.World.Bodies);
    }
}

public sealed class VoluntaryRetireTests : IDisposable
{
    private readonly RosterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void A_lethal_body_is_never_hidden_by_a_logout()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).DownPending = true;   // latched after this step's Pre
        _t.World.Leave(_t.A);
        _t.Step();
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Contains("Downed", _t.Names());
    }

    [Fact]
    public void A_lethal_body_is_never_hidden_by_a_dismiss()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).Health = 0;
        var t = _t.Roster.Submit(_t.Op(PetRoster.Kind.Dismiss, _t.A, p.Id));
        _t.Step();
        Assert.True(t.IsCompleted);
        Assert.Equal(PetLife.Downed, _t.Roster.Get(p.Id)!.Life);
    }

    // review of dev.32 pets (19d §4.5): a dismiss, release or logout whose destroy had to be tried again (a rider getting
    // off, a destroy that did not take) read the wounds only at its first try, so a down latched in between was hidden and
    // the pet settled Ready with its health
    [Fact]
    public void A_down_during_a_retried_dismiss_is_a_down()
    {
        var p = _t.Summoned(_t.A);
        _t.World.RetireRefuses = true;
        var t = _t.Roster.Submit(_t.Op(PetRoster.Kind.Dismiss, _t.A, p.Id));
        _t.Step();
        Assert.Equal(PetTransition.Dismissing, _t.Roster.Get(p.Id)!.Transition);
        _t.BodyOf(p.Id).DownPending = true;   // a hostile's lethal hit, latched while the dismiss waits
        _t.World.RetireRefuses = false;
        _t.Step();
        Assert.True(t.IsCompleted);
        Assert.StartsWith("BodyDowned", t.Result.Reason);
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Downed, 0), (d.Life, d.Health));
        Assert.Contains("Downed", _t.Names());
        Assert.Empty(_t.World.Bodies);
    }

    [Fact]
    public void A_down_during_a_retried_release_goes_down_then_releases()
    {
        var p = _t.Summoned(_t.A);
        _t.World.RetireRefuses = true;
        var t = _t.Roster.Submit(_t.Op(PetRoster.Kind.Release, _t.A, p.Id));
        _t.Step();
        _t.BodyOf(p.Id).Health = 0;
        _t.World.RetireRefuses = false;
        _t.Step();
        Assert.True(t.Result.Ok, t.Result.Reason);
        Assert.Equal(PetLife.Released, _t.Roster.Get(p.Id)!.Life);
        var names = _t.Names();
        Assert.True(names.IndexOf("Downed") >= 0 && names.IndexOf("Downed") < names.IndexOf("Released"), string.Join(",", names));
    }

    [Fact]
    public void A_down_during_a_retried_logout_is_a_down()
    {
        var p = _t.Summoned(_t.A);
        _t.World.RetireRefuses = true;
        _t.World.Leave(_t.A);
        _t.Step();
        Assert.Equal(PetPresence.Retiring, _t.Roster.Get(p.Id)!.Presence);
        _t.BodyOf(p.Id).Health = 0;
        _t.World.RetireRefuses = false;
        _t.Step();
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal((PetLife.Downed, 0), (d.Life, d.Health));
        Assert.True(d.WantsSummoned);   // a logout never clears the wish
        Assert.Contains("Downed", _t.Names());
    }

    [Fact]
    public void A_lethal_body_being_dismissed_at_the_snapshot_is_written_downed_and_the_dismiss_reports_it()
    {
        var p = _t.Summoned(_t.A);
        _t.World.RetireRefuses = true;
        var t = _t.Roster.Submit(_t.Op(PetRoster.Kind.Dismiss, _t.A, p.Id));
        _t.Step();
        _t.BodyOf(p.Id).Health = 0;
        _t.Store.Stage(6, snapshot: true);
        var rec = PetRecordJson.Read(_t.Store.Docs[_t.A], _t.A)!.Pets.Single();
        Assert.Equal(PetLife.Downed, rec.Life);
        _t.World.RetireRefuses = false;
        _t.Step();
        Assert.True(t.IsCompleted);
        Assert.StartsWith("BodyDowned", t.Result.Reason);
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Equal(rec.StateRevision, d.StateRevision);
        Assert.Null(_t.Roster.State(p.Id)!.Running);   // never left Busy
    }

    [Fact]
    public void A_lethal_body_released_goes_down_first()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).Dead = true;
        var r = _t.Run(_t.Roster.Submit(_t.Op(PetRoster.Kind.Release, _t.A, p.Id)));
        // the reconcile of that step already downed it; the release then finds a downed pet and releases it
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(PetLife.Released, r.Pet!.Life);
        Assert.Contains("Downed", _t.Names());
    }
}
