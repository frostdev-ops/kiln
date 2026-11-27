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
}
