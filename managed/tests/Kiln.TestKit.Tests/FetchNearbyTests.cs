// SPDX-License-Identifier: MIT
// The tasks guide's example mod (mods/samples/FetchNearby, docs/kiln/modding/tasks.md) on the kit's fakes: its search runs
// in a Kiln task through the task-thread world reads (and on the tick where they are off), its items move on the tick,
// nearest container first; a fetch cancelled by an unload or replaced by the next one moves nothing, and a full inventory
// sends what did not fit back to its container. A sample is what a mod author copies: it must do what the guide says.
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class FetchNearbyTests
{
    private const ulong AdaSteam = 76561198000000801;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private sealed record World(KilnHarness Kiln, ModHandle Mod, FakePlayer Ada, ItemId Wood, FakeInventory Near, FakeInventory Middle, FakeInventory Far);

    // Ada at (10, 0, 10); wood in a container 4 m away (5), 8 m away (10) and 30 m away (50).
    private static World Start(bool reads = true)
    {
        var kiln = new KilnHarness();
        if (!reads) kiln.Capabilities.Override("world.ecs_read", false);
        var mod = kiln.LoadFolder(TestMods.Folder("FetchNearby"));
        var wood = kiln.Items.AddItem("Wood", 100);
        var ada = kiln.Players.Join("Ada", AdaSteam, position: new WorldPosition(10, 0, 10));
        FakeInventory Container(double x, int count)
        {
            var inv = kiln.Items.AddContainer(kiln.Entities.Create(new WorldPosition(x, 0, 10)));
            inv.Put(wood, count);
            return inv;
        }
        var w = new World(kiln, mod, ada, wood, Container(14, 5), Container(18, 10), Container(40, 50));
        kiln.Step(2);
        return w;
    }

    private static int Wood(World w) => w.Kiln.Items.Of(w.Ada.Entity)?.Count(w.Wood) ?? 0;

    private static IEnumerable<string> ChatOf(World w) => w.Ada.Chat.Select(l => l.Text);

    // The search ends in its task; then the mod's own tick code moves the items, a transaction a step, and says so.
    private static void Finish(KilnHarness kiln, FakePlayer player)
    {
        kiln.Tasks.Settle();
        kiln.StepUntil(() => player.Chat.Any(l => l.Text.Contains("fetched", StringComparison.Ordinal) || l.Text.Contains(" found ", StringComparison.Ordinal)), maxSteps: 50);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fetch_takes_from_the_nearest_containers_first(bool reads)
    {
        var w = Start(reads);   // Ada, and wood in containers 4 m, 8 m and 30 m away; the reads switched off when !reads
        using var _ = w.Kiln;
        Assert.Equal("looking for 12 x Wood within 12 m", Said(w.Kiln.RunCommand(w.Ada, "fetch Wood 12")));
        Finish(w.Kiln, w.Ada);  // kiln.Tasks.Settle(), then steps until the moves are done
        Assert.Equal(12, Wood(w));
        Assert.Equal((0, 3, 50), (w.Near.Count(w.Wood), w.Middle.Count(w.Wood), w.Far.Count(w.Wood)));
        // the command's reply, the task's word from the tick (PostToTick), the result of the moves
        Assert.Equal(["[Server] looking for 12 x Wood within 12 m", "[Server] searching 2 container(s) within 12 m", "[Server] fetched 12 of 12 Wood"], ChatOf(w));
        // the search ran in the task through the world reads, or on the tick where they are off
        Assert.Equal(reads, w.Kiln.Run(() => WorldConcurrent.IsAvailable));
        Assert.Equal(reads, w.Kiln.EntitiesFake!.Reads.CallsOf("Query") > 0);
    }

    [Fact]
    public void Nothing_in_reach_says_so()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("FetchNearby"));
        kiln.Items.AddItem("Wood", 100);
        var ada = kiln.Players.Join("Ada", AdaSteam, position: new WorldPosition(500, 0, 500));
        kiln.Step(2);
        kiln.RunCommand(ada, "fetch Wood");
        Finish(kiln, ada);
        Assert.Equal(["[Server] looking for 1 x Wood within 12 m", "[Server] no container within 12 m", "[Server] no Wood found within 12 m"],
            ada.Chat.Select(l => l.Text));
    }

    [Fact]
    public void An_unload_before_the_plan_moves_nothing()
    {
        var w = Start();
        using var _ = w.Kiln;
        w.Kiln.RunCommand(w.Ada, "fetch Wood 12");
        // no step yet: the task's way back to the tick waits for one, and the unload comes first
        w.Kiln.Unload(w.Mod, checkCollected: false);
        w.Kiln.Tasks.Settle();
        w.Kiln.Step(5);
        Assert.Equal(0, Wood(w));
        Assert.Equal((5, 10, 50), (w.Near.Count(w.Wood), w.Middle.Count(w.Wood), w.Far.Count(w.Wood)));
    }

    [Fact]
    public void A_new_fetch_replaces_the_one_still_searching()
    {
        var w = Start();
        using var _ = w.Kiln;
        w.Kiln.RunCommand(w.Ada, "fetch Wood 12");
        w.Kiln.RunCommand(w.Ada, "fetch Wood 2");  // before a step: the first one is cancelled
        Finish(w.Kiln, w.Ada);
        w.Kiln.Step(5);
        Assert.Equal(2, Wood(w));
        Assert.Contains("[Server] fetched 2 of 2 Wood", ChatOf(w));
        Assert.DoesNotContain("[Server] fetched 12 of 12 Wood", ChatOf(w));
    }

    [Fact]
    public void What_does_not_fit_goes_back_to_its_container()
    {
        var w = Start();
        using var _ = w.Kiln;
        var stone = w.Kiln.Items.AddItem("Stone", 1);
        var backpack = w.Kiln.Items.Of(w.Ada.Entity)!;
        backpack.Put(stone, 1000);  // one per slot: every slot of the backpack is taken
        w.Kiln.RunCommand(w.Ada, "fetch Wood 3");
        Finish(w.Kiln, w.Ada);
        Assert.Equal(0, Wood(w));
        Assert.Equal(5, w.Near.Count(w.Wood));  // taken, then given back
        Assert.Contains("[Server] no Wood found within 12 m", ChatOf(w));
    }

    [Fact]
    public void A_give_back_that_does_not_fit_is_reported_not_lost_quietly()
    {
        var w = Start();
        using var _ = w.Kiln;
        var stone = w.Kiln.Items.AddItem("Stone", 1);
        w.Kiln.Items.Of(w.Ada.Entity)!.Put(stone, 1000);  // Ada's backpack is full
        w.Kiln.RunCommand(w.Ada, "fetch Wood 3");
        w.Kiln.Tasks.Settle();
        // the nearest container gave 3; before they go back, the game fills it up
        w.Kiln.StepUntil(() => w.Near.Count(w.Wood) == 2, maxSteps: 50);
        w.Near.Put(stone, 7);
        w.Near.Put(w.Wood, 98);
        w.Kiln.StepUntil(() => w.Ada.Chat.Any(l => l.Text.Contains(" found ", StringComparison.Ordinal)), maxSteps: 50);
        Assert.Contains("[Server] 3 Wood could not go back into the container and were lost", ChatOf(w));
        Assert.Contains(w.Kiln.LogsOf(w.Mod), l => l.Text.Contains("3 x Wood fitted neither the player nor container", StringComparison.Ordinal));
    }
}
