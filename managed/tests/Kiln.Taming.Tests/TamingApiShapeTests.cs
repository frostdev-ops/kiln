// SPDX-License-Identifier: MIT
// The public shape of Kiln.Taming before mod API 0.2.27 freezes it (dev.32 API-SHAPE-2; findings docs-api-ref A4-A13,
// docs-guides B5 and docs-skill C2/C3): one test group per finding.
using System.Reflection;
using Kiln;
using Xunit;
using static Kiln.TamingTests.TamingFixture;

namespace Kiln.TamingTests;

public sealed class TamingApiShapeTests
{
    private static TamingDefinition FeedTwice() => Def(new FeedStep(Bait, 2), new FeedStep(Bait, 1));

    // ---- A4: one vocabulary with Kiln.Pets, as a value (TamingResult.Refusal) ----

    [Theory]
    [InlineData("Capacity: 512 attempts run in this world", TamingRefusal.Capacity)]
    [InlineData("NotOwner: the attempt is another player's", TamingRefusal.NotOwner)]
    [InlineData("RefusedByMod: a mod refused it", TamingRefusal.RefusedByMod)]
    [InlineData("CommitDeferred: Capacity: the roster is full", TamingRefusal.CommitDeferred)]
    [InlineData("SomethingNew", TamingRefusal.Other)]
    public void A_refusal_is_a_value_read_from_the_reasons_word(string reason, TamingRefusal refusal)
    {
        Assert.Equal(refusal, new TamingResult(false, reason, null, null, Guid.NewGuid(), false, []).Refusal);
        Assert.Equal(TamingRefusal.None, new TamingResult(true, null, null, null, Guid.NewGuid(), false, []).Refusal);
        Assert.Equal(refusal, new NativeStepResult(false, reason).Refusal);
    }

    [Fact]
    public void The_same_meaning_has_the_same_word_in_Pets_and_Taming()
    {
        foreach (var shared in new[] { "Busy", "Capacity", "NotOwner", "OwnerOffline", "OwnerLeft", "RequestConflict", "OutOfRange", "BodyGone",
                     "DefinitionUnknown", "DefinitionInUse", "UncertainCommit", "NoCharacter", "WorldEnded", "RefusedByMod" })
        {
            Assert.True(Enum.TryParse<TamingRefusal>(shared, out _), $"TamingRefusal has no {shared}");
            Assert.True(Enum.TryParse<PetRefusal>(shared, out _), $"PetRefusal has no {shared}");
        }
        Assert.False(Enum.TryParse<TamingRefusal>("Cap", out _));
        Assert.False(Enum.TryParse<TamingRefusal>("WrongOwner", out _));
    }

    [Fact]
    public void Another_players_attempt_is_NotOwner_a_mods_veto_RefusedByMod_and_a_full_world_Capacity()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        Assert.Equal(TamingRefusal.NotOwner, f.Advance(p, f.Stack(Bait, owner: f.B), who: f.B).Refusal);
        f.Refuse = ev => ev is TamingBeginningEvent;
        Assert.Equal(TamingRefusal.RefusedByMod, f.Begin(f.Dog).Refusal);
        f.Refuse = ev => ev is TamingAdvancingEvent;
        var vetoed = f.Advance(p, f.Stack(Bait));
        Assert.Equal(TamingRefusal.RefusedByMod, vetoed.Refusal);
        Assert.Equal(TamingState.Active, vetoed.Progress!.State);   // a veto is not a cancel of the attempt
        var offline = new PlayerRef(76561198000000009, Guid.NewGuid(), new EntityRef(new EntityId(19), 1));
        Assert.Equal(TamingRefusal.OwnerOffline, f.Begin(who: offline).Refusal);
    }

    [Fact]
    public void A_roster_that_cannot_hold_the_pet_is_Capacity_as_in_Kiln_Pets()
    {
        var b = new PetsTamingBinder
        {
            CharacterOf = _ => new CharacterKey(1, Guid.NewGuid()),
            PetDefinition = _ => new PetDefinition { Id = "mod-a:pet-wolf", Creature = "game:enemy_wildbeast_wolf", Limits = new PetLimits { RetainedPerCharacter = 1 } },
            PetsOf = _ => [new PetSnapshot { Definition = "mod-a:pet-wolf" }],
        };
        var reason = b.Reserve(new TamingBindRequest(new TameAttemptId(Guid.NewGuid()), default, default, Def(new WeakenStep(0.5)) with { Pet = "mod-a:pet-wolf" }, Guid.NewGuid()));
        Assert.StartsWith("Capacity:", reason);
    }

    [Fact]
    public void Every_reason_word_Kiln_Taming_writes_is_a_refusal_member()
    {
        var notResults = new HashSet<string> { "ItemUnknown" };   // TamingJson's: an ArgumentException's text, never a result
        var words = SourceWords("Kiln.Taming").Where(w => !notResults.Contains(w)).ToHashSet();
        Assert.Contains("AttemptClosed", words);
        Assert.Empty(words.Where(w => !Enum.TryParse<TamingRefusal>(w, out _)).Order());
    }

    internal static IEnumerable<string> SourceWords(string service, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../services", service));
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\\$?\"([A-Z][a-z]+(?:[A-Z][A-Za-z]*)?):"))
                yield return m.Groups[1].Value;
    }

    // ---- A5: one request shape: TamingRequest (its id, and the token of the view it was made on), as PetRequest ----

    [Fact]
    public void A_request_is_its_id_and_the_token_of_the_last_answer()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        Assert.Equal(new TamingToken(p.StepChallenge, p.Revision, p.CompletedSteps), p.Token);
        var request = TamingRequest.New(p.Token);
        Assert.NotEqual(Guid.Empty, request.RequestId);
        var r = Done(f.Engine.Advance(f.A, p.Id, request, f.Stack(Bait)));
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(request.RequestId, r.RequestId);
        Assert.Same(f.Engine.Advance(f.A, p.Id, request, f.Stack(Bait)), f.Engine.Advance(f.A, p.Id, request, f.Stack(Bait)));   // the replay
        Assert.Single(f.Costs.Calls);
        // an advance names the step it expects
        Assert.Throws<ArgumentException>(() => { _ = f.Engine.Advance(f.A, p.Id, TamingRequest.New(), f.Stack(Bait)); });
        Assert.Null(typeof(TamingToken).GetProperty("RequestId"));
        Assert.Equal(typeof(TamingRequest), typeof(Taming).GetMethod(nameof(Taming.Advance))!.GetParameters()[2].ParameterType);
    }

    // ---- A6 (and docs-guides B5): Begin answers as a task, as Advance, Retry and Cancel do ----

    [Fact]
    public void Begin_returns_a_task_like_the_other_actions()
    {
        foreach (var name in new[] { nameof(Taming.Begin), nameof(Taming.Advance), nameof(Taming.Retry), nameof(Taming.Cancel) })
            Assert.Equal(typeof(Task<TamingResult>), typeof(Taming).GetMethod(name)!.ReturnType);
    }

    // ---- A7: Choices lists the first 256 and says it cut the list ----

    [Fact]
    public void Choices_past_256_slots_lists_the_first_256_and_says_so()
    {
        var f = new TamingFixture().With(FeedTwice());
        f.Costs.Inventory = Enumerable.Range(1, 300)
            .Select(i => new InventorySlot(new SlotId((ulong)i), Bait, 5, EntityId.None, 0, null, default, 0, false, false, false, 0)).ToList();
        var p = f.Begin().Progress!;
        var c = f.Engine.Choices(f.A, p.Id);
        Assert.True(c.Ok, c.Reason);
        Assert.True(c.Truncated);
        Assert.Equal(256, c.Choices.Count);
        Assert.Equal(new SlotId(1), c.Choices[0].Selection.Slot);
        Assert.Equal(p.Token, c.Token);
        f.Costs.Inventory = f.Costs.Inventory.Take(3).ToList();
        Assert.False(f.Engine.Choices(f.A, p.Id).Truncated);
        Assert.Equal(TamingRefusal.NotOwner, f.Engine.Choices(f.B, p.Id).Refusal);
        Assert.Equal(TamingRefusal.AttemptClosed, f.Engine.Choices(f.A, new TameAttemptId(Guid.NewGuid())).Refusal);
    }

    // ---- A8: Cancel works from a stale view (by attempt id), and a token, when given, must still be current ----

    [Fact]
    public void Cancel_needs_no_current_challenge_and_a_stale_token_given_still_refuses()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        Assert.True(f.Advance(p, f.Stack(Bait)).Ok);   // the view p is stale now
        Assert.Equal(TamingRefusal.StaleStep, Done(f.Engine.Cancel(f.A, p.Id, TamingRequest.New(p.Token))).Refusal);
        Assert.Equal(TamingRefusal.NotOwner, Done(f.Engine.Cancel(f.B, p.Id, TamingRequest.New())).Refusal);
        var cancel = TamingRequest.New();
        var c = Done(f.Engine.Cancel(f.A, p.Id, cancel));
        Assert.True(c.Ok, c.Reason);
        Assert.Equal(TamingState.Canceled, c.Progress!.State);
        Assert.Same(f.Engine.Cancel(f.A, p.Id, cancel), f.Engine.Cancel(f.A, p.Id, cancel));   // the same request, the same answer
        Assert.Equal(TamingRefusal.AttemptClosed, Done(f.Engine.Cancel(f.A, p.Id, TamingRequest.New())).Refusal);
    }

    // ---- A9: NativeTamingState.TamedBy is filled only once native attribution is on (taming.native_observe) ----

    [Fact]
    public void TamedBy_names_its_capability_and_is_set_by_Kiln_only()
    {
        Assert.Equal("taming.native_observe", NativeTaming.ObserveCapability);
        var prop = typeof(NativeTamingState).GetProperty(nameof(NativeTamingState.TamedBy))!;
        Assert.Contains(prop.GetCustomAttributes<RequiresCapabilityAttribute>(), a => a.Name == NativeTaming.ObserveCapability);
        Assert.NotNull(prop.SetMethod);   // an init the watch fills once attribution is proven (TAME-2), never a constant null
        var s = new NativeTamingState(new EntityId(5), true, 3, 3, NativeTamingPhase.Tamed, EntityId.None, Guid.Empty, 10, TimeSpan.FromSeconds(8));
        Assert.Null(s.TamedBy);
    }

    // ---- A11: the definitions are read on any thread, as Kiln.Pets' are ----

    [Fact]
    public void Definition_and_Definitions_are_any_thread()
    {
        foreach (var m in new MemberInfo[] { typeof(Taming).GetMethod(nameof(Taming.Definition))!, typeof(Taming).GetProperty(nameof(Taming.Definitions))! })
        {
            Assert.Contains(m.GetCustomAttributes(false), a => a.GetType().Name == "AnyThreadAttribute");
            Assert.DoesNotContain(m.GetCustomAttributes(false), a => a.GetType().Name == "TickThreadOnlyAttribute");
        }
        var f = new TamingFixture().With(FeedTwice());
        var seen = Task.Run(() => (f.Engine.Definition("mod-a:wolf"), f.Engine.Definitions.Count)).Result;
        Assert.Equal(("mod-a:wolf", 1), (seen.Item1!.Id, seen.Count));
    }

    // ---- C2: durations are TimeSpan; JSON keeps its seconds ----

    [Fact]
    public void A_definitions_timeouts_and_an_attempts_times_are_TimeSpan()
    {
        var d = Def(new WeakenStep(0.5)) with { InactivityTimeout = TimeSpan.FromSeconds(60), MaxAttemptDuration = TimeSpan.FromMinutes(5) };
        Assert.Equal(TimeSpan.FromSeconds(30), Def(new WeakenStep(0.5)).InactivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), Def(new WeakenStep(0.5)).MaxAttemptDuration);
        var f = new TamingFixture().With(d);
        var p = f.Begin().Progress!;
        Assert.Equal(TimeSpan.FromSeconds(100), p.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(160), p.ExpiresAt);
        Assert.Equal(p.StartedAt, p.LastProgressAt);
        var json = TamingJson.Read("""{ "id": "mod-a:pup", "creatures": ["game:animal_wildlife_dog_*"], "steps": [ { "kind": "native" } ], "inactivitySeconds": 45, "maxAttemptSeconds": 90 }""",
            (_, _) => ItemId.None);
        Assert.Equal((TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)), (json[0].InactivityTimeout, json[0].MaxAttemptDuration));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(d with { InactivityTimeout = TimeSpan.FromSeconds(301) }));
    }

    // ---- C3: Taming.Define answers with a result, as Pets.Define does ----

    [Fact]
    public void Define_answers_DefinitionInUse_as_a_result_and_keeps_the_running_definition()
    {
        var f = new TamingFixture().With(FeedTwice());
        Assert.True(f.Engine.Define(FeedTwice(), "mod-a").Ok);   // the same one again
        _ = f.Begin();
        var other = Def(new WeakenStep(0.5));
        var r = f.Engine.Define(other, "mod-a");
        Assert.False(r.Ok);
        Assert.Equal(TamingRefusal.DefinitionInUse, r.Refusal);
        Assert.Equal(2, f.Engine.Definition("mod-a:wolf")!.Steps.Count);
        Assert.Equal(TamingRefusal.DefinitionInUse, f.Engine.Define(FeedTwice(), "mod-b").Refusal);   // another mod's id
        Assert.Equal(typeof(TamingDefinitionResult), typeof(Taming).GetMethod(nameof(Taming.Define))!.ReturnType);
        Assert.Equal(TamingRefusal.DefinitionInUse, new TamingDefinitionResult(false, "DefinitionInUse: x", null).Refusal);
    }
}
