// SPDX-License-Identifier: MIT
// The kit's Kiln.Pets (KilnHarness.Pets, API-SHAPE-2 C11): the real roster on the kit's world, driven by the harness's steps.
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class PetsFakeTests
{
    private static T Settle<T>(KilnHarness kiln, Func<Task<T>> request)
    {
        var t = kiln.Run(request);
        kiln.StepUntil(() => t.IsCompleted, maxSteps: 50);
        return t.Result;
    }

    [Fact]
    public void A_pet_is_granted_summoned_downed_and_duels_are_listed_on_the_kits_world()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        var a = HarnessPets.Character(1);
        var b = HarnessPets.Character(2);
        var ada = kiln.Pets.Join(a, new WorldPosition(10, 0, 10));
        kiln.Pets.Join(b);
        Assert.True(kiln.Run(() => Kiln.Pets.Define(new PetDefinition { Id = "kit-test:wolf", Creature = "game:enemy_wildbeast_wolf" })).Ok);
        var granted = Settle(kiln, () => Kiln.Pets.Grant(ada, "kit-test:wolf", PetRequest.New()));
        Assert.True(granted.Ok, granted.Reason);
        var pet = granted.Pet!.Id;
        var summoned = Settle(kiln, () => Kiln.Pets.Summon(ada, pet, PetRequest.New()));
        Assert.True(summoned.Ok, summoned.Reason);
        Assert.Single(kiln.Pets.Bodies);
        Assert.Equal(PetPresence.Live, Assert.Single(kiln.Pets.Of(a)).Presence);

        kiln.Pets.Down(pet);
        kiln.StepUntil(() => kiln.Pets.Of(a).Single().Life == PetLife.Downed, maxSteps: 50);
        Assert.Empty(kiln.Pets.Bodies);

        kiln.Run(() => Kiln.Pets.SetHostile(a, b, true));
        Assert.Contains(kiln.Pets.HostilePairs, p => (p.A, p.B) == (a, b) || (p.A, p.B) == (b, a));
        Assert.Equal([b], kiln.Run(() => Kiln.Pets.HostileTo(a)));
        kiln.Run(() => Kiln.Pets.SetHostile(a, b, false));
        Assert.Empty(kiln.Pets.HostilePairs);
    }

    [Fact]
    public void A_feed_taming_takes_the_selected_bait_and_tames_on_the_kits_world()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        var who = kiln.Taming.Join(HarnessPets.Character(3), new WorldPosition(0, 0, 0));
        var wolf = kiln.Taming.Place("game:enemy_wildbeast_wolf", new WorldPosition(2, 0, 0));
        var bait = new ItemId(0xb59dfc2c);
        var slot = kiln.Taming.Give(who, bait, 5);
        var def = new TamingDefinition { Id = "kit-test:wolf", Creatures = ["game:enemy_wildbeast_wolf"], Steps = [new FeedStep(bait, 2)], Refund = TamingRefundPolicy.Escrow };
        Assert.True(kiln.Run(() => Kiln.Taming.Define(def)).Ok);
        var begun = Settle(kiln, () => Kiln.Taming.Begin(who, wolf, "kit-test:wolf", TamingRequest.New()));
        Assert.True(begun.Ok, begun.Reason);
        var p = begun.Progress!;
        var choices = kiln.Run(() => Kiln.Taming.Choices(who, p.Id));
        Assert.Equal(slot.Slot, Assert.Single(choices.Choices).Selection.Slot);
        var tamed = Settle(kiln, () => Kiln.Taming.Advance(who, p.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection));
        Assert.True(tamed.Ok, tamed.Reason);
        Assert.Equal(TamingState.Committed, tamed.Progress!.State);
        Assert.Equal(3, kiln.Taming.Count(who, bait));
    }

    [Fact]
    public void An_escrow_taming_canceled_gives_the_bait_back_on_the_kits_world()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        var who = kiln.Taming.Join(HarnessPets.Character(4));
        var wolf = kiln.Taming.Place("game:enemy_wildbeast_wolf", new WorldPosition(1, 0, 0));
        var bait = new ItemId(0xb59dfc2c);
        kiln.Taming.Give(who, bait, 4);
        var def = new TamingDefinition { Id = "kit-test:slow-wolf", Creatures = ["game:enemy_wildbeast_wolf"], Steps = [new FeedStep(bait, 1), new FeedStep(bait, 1)], Refund = TamingRefundPolicy.Escrow };
        Assert.True(kiln.Run(() => Kiln.Taming.Define(def)).Ok);
        var p = Settle(kiln, () => Kiln.Taming.Begin(who, wolf, "kit-test:slow-wolf", TamingRequest.New())).Progress!;
        var c = kiln.Run(() => Kiln.Taming.Choices(who, p.Id));
        Assert.True(Settle(kiln, () => Kiln.Taming.Advance(who, p.Id, TamingRequest.New(c.Token), c.Choices[0].Selection)).Ok);
        Assert.Equal(3, kiln.Taming.Count(who, bait));
        var canceled = Settle(kiln, () => Kiln.Taming.Cancel(who, p.Id, TamingRequest.New()));   // a stale view: no token
        Assert.True(canceled.Ok, canceled.Reason);
        Assert.Equal(4, kiln.Taming.Count(who, bait));   // escrow: the paid step came back
    }

    // ---- review of API-SHAPE-2: the kit's isolation and cleanup ----

    /// <summary>A mod that declares its taming when it loads, as the docs say a mod does.</summary>
    private sealed class EarlyTamer : Mod
    {
        public override void OnLoad() =>
            _ = Kiln.Taming.Define(new TamingDefinition { Id = "kit-tame:early-wolf", Creatures = ["game:enemy_wildbeast_wolf"], Steps = [new WeakenStep(1)] });
    }

    [Fact]
    public void A_taming_a_mod_defined_when_it_loaded_begins_once_the_test_uses_kiln_Taming_and_goes_with_the_mod()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<EarlyTamer>(new ModManifest("kit-tame"));
        kiln.Step(2);   // the mod's OnLoad defined it, before the test first reached for kiln.Taming
        var who = kiln.Taming.Join(HarnessPets.Character(7));
        var wolf = kiln.Taming.Place("game:enemy_wildbeast_wolf", new WorldPosition(1, 0, 0));
        Assert.NotNull(Kiln.Taming.Definition("kit-tame:early-wolf"));
        var begun = Settle(kiln, () => Kiln.Taming.Begin(who, wolf, "kit-tame:early-wolf", TamingRequest.New()));
        Assert.True(begun.Ok, begun.Reason);
        kiln.Unload(mod);
        Assert.Null(Kiln.Taming.Definition("kit-tame:early-wolf"));   // still the mod's: its unload takes it
    }
}
