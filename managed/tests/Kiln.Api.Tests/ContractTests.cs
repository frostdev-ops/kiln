// SPDX-License-Identifier: MIT
using System.Reflection;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>The error rule's types (KilnUnavailableException, IKilnResult) and the side/thread attributes.</summary>
public sealed class ContractTests
{
    private sealed record FakeResult(bool Ok, string? Reason) : IKilnResult;

    private static void ThrowOff() => throw new KilnUnavailableException("kiln.test", "the service is not loaded");

    [Fact]
    public void Unavailable_is_caught_as_NotSupportedException_and_names_the_capability()
    {
        NotSupportedException? caught = null;
        try
        {
            ThrowOff();
        }
        catch (NotSupportedException e)
        {
            caught = e;
        }
        var k = Assert.IsType<KilnUnavailableException>(caught);
        Assert.Equal("kiln.test", k.Capability);
        Assert.Equal("the service is not loaded", k.Reason);
        Assert.Equal("kiln.test is not available: the service is not loaded", k.Message);
    }

    [Fact]
    public void Unavailable_without_a_reason_and_with_an_inner_error()
    {
        var inner = new InvalidOperationException("boom");
        var k = new KilnUnavailableException("kiln.other", innerException: inner);
        Assert.Null(k.Reason);
        Assert.Equal("kiln.other is not available here.", k.Message);
        Assert.Same(inner, k.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unavailable_needs_a_capability(string? capability)
    {
        Assert.Throws<ArgumentException>(() => new KilnUnavailableException(capability!, "x"));
    }

    [Fact]
    public void A_result_record_can_implement_IKilnResult()
    {
        IKilnResult refused = new FakeResult(false, "the game refused");
        Assert.False(refused.Ok);
        Assert.Equal("the game refused", refused.Reason);
        IKilnResult done = new FakeResult(true, null);
        Assert.True(done.Ok);
        Assert.Null(done.Reason);
    }

    [ServerOnly, AnyThread]
    private static void ServerAnyThread() { }

    [ClientOnly, RenderThread]
    private static void ClientRender() { }

    [WorkerThread]
    private sealed class Worker;

    [Fact]
    public void The_side_and_thread_attributes_apply()
    {
        var m1 = typeof(ContractTests).GetMethod(nameof(ServerAnyThread), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(m1.GetCustomAttribute<ServerOnlyAttribute>());
        Assert.NotNull(m1.GetCustomAttribute<AnyThreadAttribute>());
        Assert.Null(m1.GetCustomAttribute<ClientOnlyAttribute>());
        var m2 = typeof(ContractTests).GetMethod(nameof(ClientRender), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(m2.GetCustomAttribute<ClientOnlyAttribute>());
        Assert.NotNull(m2.GetCustomAttribute<RenderThreadAttribute>());
        Assert.NotNull(typeof(Worker).GetCustomAttribute<WorkerThreadAttribute>());

        foreach (var t in new[] { typeof(ServerOnlyAttribute), typeof(ClientOnlyAttribute), typeof(AnyThreadAttribute),
                     typeof(RenderThreadAttribute), typeof(WorkerThreadAttribute) })
        {
            Assert.Equal(typeof(TickThreadOnlyAttribute).Assembly, t.Assembly);
            Assert.True(t.IsSealed && t.IsPublic, t.Name);
            Assert.Equal(AttributeTargets.All, t.GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        }
    }

    [Fact]
    public void The_identity_members_carry_TickThreadOnly()
    {
        Assert.NotNull(typeof(EntityRef).GetMethod(nameof(EntityRef.Of))!.GetCustomAttribute<TickThreadOnlyAttribute>());
        Assert.NotNull(typeof(EntityRef).GetProperty(nameof(EntityRef.IsAlive))!.GetCustomAttribute<TickThreadOnlyAttribute>());
        Assert.NotNull(typeof(ItemInstanceRef).GetProperty(nameof(ItemInstanceRef.IsAlive))!.GetCustomAttribute<TickThreadOnlyAttribute>());
        Assert.NotNull(typeof(PlayerRef).GetMethod(nameof(PlayerRef.Of))!.GetCustomAttribute<TickThreadOnlyAttribute>());
        Assert.NotNull(typeof(PlayerRef).GetMethod(nameof(PlayerRef.Resolve))!.GetCustomAttribute<TickThreadOnlyAttribute>());
        Assert.NotNull(typeof(PlayerRef).GetProperty(nameof(PlayerRef.IsOnline))!.GetCustomAttribute<TickThreadOnlyAttribute>());
    }
}
