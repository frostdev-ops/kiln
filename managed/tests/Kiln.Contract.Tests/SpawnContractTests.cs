// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Spawn by the contract: its rows, what it throws when one is off, its result shape and attributes.</summary>
public sealed class SpawnContractTests : ContractTest
{
    private static readonly WorldPosition Here = new(10, 20, 30);

    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Spawn).Assembly);
        Assert.False(Spawn.Available);
        AssertOffline("kiln.spawn");
        foreach (var name in (string[])["spawn.create", "spawn.destroy", "spawn.entities", "spawn.templates", "spawn.components", "spawn.world"])
        {
            var row = AssertOffline(name);
            Assert.StartsWith("needs kiln.spawn", row.ReasonText);
        }
        // a row that is off from the start never claims to follow the session
        Assert.False(Capabilities.Get("spawn.world").SessionDependent);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        var e = AssertUnavailable("kiln.spawn", () => Spawn.Destroy(new EntityId(5000)));
        // the sentence the member threw before the contract is still in the message, then the row's reason
        Assert.Contains("kiln.spawn is unavailable on this server", e.Message);
        Assert.Contains("no Kiln core in this process", e.Message);
        AssertUnavailable("kiln.spawn", () => Spawn.Inspect(new EntityId(5000)));
        AssertUnavailable("kiln.spawn", () => Spawn.Find(Guid.NewGuid()));
        AssertUnavailable("kiln.spawn", () => Spawn.CleanupTracked());
        AssertUnavailable("kiln.spawn", () => Spawn.TryEntity("Enemy_Vukah_Melee", Here));
        AssertUnavailable("kiln.spawn", () => Spawn.TryItem(new ItemId(7), 1, Here));
        AssertUnavailable("kiln.spawn", () => Spawn.Destroy(new EntityRef(new EntityId(5000), 9)));
    }

    [Fact]
    public async Task The_task_shapes_fault_with_the_same_exception()
    {
        var task = Spawn.Entity("Enemy_Vukah_Melee", Here);
        Assert.True(task.IsFaulted);
        var e = await Assert.ThrowsAsync<KilnUnavailableException>(() => task);
        Assert.Equal("kiln.spawn", e.Capability);
        await Assert.ThrowsAsync<KilnUnavailableException>(() => Spawn.Item(7, 1, Here));
    }

    [Fact]
    public void Bad_arguments_throw_before_anything_else()
    {
        Assert.Throws<ArgumentException>(() => Spawn.TryEntity(" ", Here));
        Assert.Throws<ArgumentException>(() => Spawn.TryEntity(Guid.Empty, Here));
        Assert.Throws<ArgumentException>(() => Spawn.TryItem(ItemId.None, 1, Here));
        Assert.Throws<ArgumentOutOfRangeException>(() => Spawn.TryItem(new ItemId(7), 0, Here));
        Assert.Throws<ArgumentException>(() => Spawn.Destroy(new EntityRef(new EntityId(3), 1)));  // a player slot
        Assert.False(Spawn.Destroy(EntityRef.None));
    }

    [Fact]
    public void Off_the_tick_thread_the_new_shapes_refuse()
    {
        Backend.TickThread = false;
        Assert.Throws<InvalidOperationException>(() => Spawn.TryEntity("Enemy_Vukah_Melee", Here));
        Assert.Throws<InvalidOperationException>(() => Spawn.TryItem(new ItemId(7), 1, Here));
    }

    [Fact]
    public void Reads_that_never_threw_still_answer_empty()
    {
        Assert.Null(Spawn.Lookup("Vukah"));
        Assert.Null(Spawn.Template(Guid.NewGuid()));
        Assert.Empty(Spawn.Templates);
        Assert.Empty(Spawn.Tracked);
        Assert.False(Spawn.WorldRunning);
        Assert.False(Spawn.SupportsComponents);
        Assert.Null(Spawn.WorldKey);
    }

    [Fact]
    public void The_result_is_a_kiln_result()
    {
        IKilnResult refused = new SpawnResult(false, EntityRef.None, "the engine refused", 7);
        Assert.False(refused.Ok);
        Assert.Equal("the engine refused", refused.Reason);
        IKilnResult made = new SpawnResult(true, new EntityRef(new EntityId(4000), 12), null, 0);
        Assert.True(made.Ok);
        Assert.Null(made.Reason);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Spawn));
        AssertHas<ServerOnlyAttribute>(typeof(SpawnResult));
        var entity = Method(typeof(Spawn), "Entity", typeof(string), typeof(WorldPosition), typeof(float), typeof(SpawnOptions));
        AssertHas<TickThreadOnlyAttribute>(entity);
        AssertRequires(entity, "spawn.world");
        var destroy = Method(typeof(Spawn), "Destroy", typeof(EntityRef));
        AssertHas<TickThreadOnlyAttribute>(destroy);
        AssertRequires(destroy, "spawn.destroy");
        AssertRequires(Method(typeof(Spawn), "Inspect", typeof(EntityId)), "kiln.spawn");
        AssertRequires(Property(typeof(Spawn), "EntityCount"), "spawn.entities");
        AssertRequires(Property(typeof(SpawnOptions), "Components"), "spawn.components");
        AssertHas<AnyThreadAttribute>(Method(typeof(Spawn), "Lookup", typeof(string)));
        AssertHas<AnyThreadAttribute>(Property(typeof(Spawn), "Templates"));
        AssertHas<AnyThreadAttribute>(Property(typeof(Spawn), "WorldRunning"));
    }
}
