// SPDX-License-Identifier: MIT
// The kit's recording fake of kiln.combat: Kiln.Combat's own code, health on the fake world's entities, the game's hits.
using System.Runtime.CompilerServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class CombatFakeTests
{
    private const ulong AdaSteam = 76561198000000801;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static (KilnHarness Kiln, FakePlayer Ada, EntityId Wolf) Start(KilnHarnessOptions? options = null)
    {
        var kiln = new KilnHarness(options);
        kiln.Load<TkFakesMod>();
        var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(0, 0, 0));
        var wolf = kiln.Entities.Create(new WorldPosition(4, 0, 0));
        kiln.Combat.SetHealth(wolf, 100, 100);
        kiln.Step(2);
        kiln.Combat.SetHealth(ada.Entity, 150, 150);
        return (kiln, ada, wolf);
    }

    private static List<string> Lines(KilnHarness kiln, string prefix) => kiln.Lines("tk-fakes", prefix);

    [Fact]
    public void AModsDamageAppliesAtTheNextStepAndADeathFollows()
    {
        var (kiln, ada, wolf) = Start();
        using var _ = kiln;
        Assert.Equal("hp: 100/100 dead False god False", Said(kiln.RunCommand($"tkf-hp {wolf.Value}")));
        kiln.RunCommand(ada, $"tkf-damage {wolf.Value} 30");
        // Kiln.Combat's own EntityHurtEvent for a mod's hit comes at once; the hit applies at the next step
        Assert.Equal([$"hurt: {wolf.Value} by 1 for 30 lethal False origin Kiln"], Lines(kiln, "hurt:"));
        Assert.Equal(100, kiln.Combat.Health(wolf)!.Current);
        kiln.Step();
        Assert.Equal(70, kiln.Combat.Health(wolf)!.Current);
        Assert.Equal(["damage done: Applied 100->70 killing False"], Lines(kiln, "damage done:"));
        Assert.Equal([$"damaged: {wolf.Value} 100->70 by 1"], Lines(kiln, "damaged:"));

        kiln.RunCommand(ada, $"tkf-kill {wolf.Value}");
        kiln.Step();
        Assert.True(kiln.Combat.Health(wolf)!.Dead);
        Assert.Equal(["kill done: Applied 70->0 killing True"], Lines(kiln, "kill done:"));
        Assert.Equal([$"died: {wolf.Value} killer 1 origin Kiln"], Lines(kiln, "died:"));
        var hit = kiln.Combat.Hits[^1];
        Assert.Equal((FakeHitState.Applied, true, "tk-fakes", false), (hit.State, hit.KillingBlow, hit.ModId, hit.FromGame));

        // heals, a set to 0 kills without a killer, god mode drops damage
        Assert.Equal("healed 0", Said(kiln.RunCommand($"tkf-heal {ada.Entity.Value} 20")));  // already full
        Assert.Equal("set 40", Said(kiln.RunCommand($"tkf-sethp {ada.Entity.Value} 40")));
        Assert.Equal("healed 20", Said(kiln.RunCommand($"tkf-heal {ada.Entity.Value} 20")));
        Assert.Equal("set 0", Said(kiln.RunCommand($"tkf-sethp {ada.Entity.Value} 0")));
        kiln.Step();
        Assert.Contains($"died: {ada.Entity.Value} killer 0 origin Other", Lines(kiln, "died:"));
        kiln.Combat.Respawn(ada.Entity, 150);
        kiln.Step();
        Assert.Equal([$"respawned: {ada.Entity.Value} with 150/150"], Lines(kiln, "respawned:"));
        kiln.RunCommand($"tkf-god {ada.Entity.Value} on");
        Assert.Contains("god True", Said(kiln.RunCommand($"tkf-hp {ada.Entity.Value}")));
        kiln.RunCommand($"tkf-damage {ada.Entity.Value} 50");
        kiln.Step();
        Assert.Equal("damage done: Dropped -1->-1 killing False", Lines(kiln, "damage done:")[^1]);
        Assert.Equal("set 1", Said(kiln.RunCommand($"tkf-sethp {ada.Entity.Value} 0")));  // god mode keeps 1
    }

    [Fact]
    public void TheGamesHitIsHeldForTheHandlersWhoMayChangeOrCancelIt()
    {
        var (kiln, ada, wolf) = Start();
        using var _ = kiln;
        kiln.Step();  // the handlers' interest reached kiln.combat: hits are held from now
        kiln.Combat.Hit(ada.Entity, 40, attacker: wolf);
        kiln.Step();
        Assert.Equal([$"hurt: 1 by {wolf.Value} for 40 lethal False origin Game"], Lines(kiln, "hurt:"));
        Assert.Equal(150, kiln.Combat.Health(ada.Entity)!.Current);  // held: not applied yet
        kiln.Step();
        Assert.Equal(110, kiln.Combat.Health(ada.Entity)!.Current);
        Assert.Equal([$"damaged: 1 150->110 by {wolf.Value}"], Lines(kiln, "damaged:"));

        kiln.RunCommand("tkf-shield 1");
        kiln.Combat.Hit(ada.Entity, 60, attacker: wolf);  // a handler halves it
        var thirteen = kiln.Combat.Hit(ada.Entity, 13, attacker: wolf);  // a handler cancels it
        kiln.Step(2);
        Assert.Equal(80, kiln.Combat.Health(ada.Entity)!.Current);
        var hits = kiln.Combat.Hits.Where(h => h.FromGame).ToList();
        Assert.Equal([(40, FakeHitState.Applied), (30, FakeHitState.Applied), (13, FakeHitState.Dropped)], hits.Select(h => (h.Amount, h.State)));
        Assert.Equal(thirteen, hits[2].Id);

        kiln.Combat.Hit(wolf, 500, attacker: ada.Entity);  // a lethal hit of the game
        kiln.Step(2);
        Assert.Contains($"hurt: {wolf.Value} by 1 for 500 lethal True origin Game", Lines(kiln, "hurt:"));
        Assert.Equal([$"died: {wolf.Value} killer 1 origin Game"], Lines(kiln, "died:"));
        Assert.True(kiln.Combat.Hits[^1].KillingBlow);
    }

    [Fact]
    public void WithNoHandlerTheGamesHitAppliesAtOnceAndSwitchedOffNothingWorks()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Step();
            var e = kiln.Entities.Create();
            kiln.Combat.SetHealth(e, 10, 10);
            kiln.Combat.Hit(e, 4);
            kiln.Step();
            Assert.Equal(6, kiln.Combat.Health(e)!.Current);
            Assert.Equal(6, kiln.Run(() => Combat.GetHealth(e))!.Value.Current);
            Assert.Null(kiln.Run(() => Combat.GetHealth(kiln.Entities.Create())));  // no health
            kiln.Entities.Destroy(e);
            Assert.Null(kiln.Combat.Health(e));  // gone with its entity
            kiln.Capabilities.Override("kiln.combat", false);
            Assert.False(kiln.Run(() => Combat.Available));
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Combat.SetHealth(kiln.Entities.Create(), 1)));
            kiln.Capabilities.ClearOverride("kiln.combat");
            Assert.True(kiln.Run(() => Combat.Available));
            Assert.Contains("offline", kiln.Capabilities.Why("combat.attributes"));
        }
        Assert.False(Combat.Available);
        using var next = new KilnHarness();
        next.Step();
        Assert.Empty(next.Combat.Hits);
    }

    private static int SizeOf(Type t) => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;

    [Fact]
    public void TheTableMirrorsTheHeaderAsTheServiceReadsIt()
    {
        Assert.Equal((16, 32, 24, 144, 88), (Unsafe.SizeOf<CombatAbi.Health>(), Unsafe.SizeOf<CombatAbi.DamageRequest>(), Unsafe.SizeOf<CombatAbi.HitStatus>(),
            Unsafe.SizeOf<CombatAbi.Event>(), Unsafe.SizeOf<CombatAbi.Table>()));
        var native = typeof(Combat).Assembly.GetType("Kiln.CombatNative", true)!;
        foreach (var (mine, theirs) in new[] { (typeof(CombatAbi.Health), "Health"), (typeof(CombatAbi.DamageRequest), "DamageRequest"),
                     (typeof(CombatAbi.HitStatus), "HitStatus"), (typeof(CombatAbi.Event), "Event"), (typeof(CombatAbi.Status), "Status"), (typeof(CombatAbi.Table), "Table") })
            Assert.Equal(SizeOf(mine), Mirror.Size(native, theirs));
    }
}
