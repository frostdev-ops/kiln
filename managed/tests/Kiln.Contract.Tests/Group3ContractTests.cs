// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Chat by the contract.</summary>
public sealed class ChatContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Chat).Assembly);
        Assert.False(Chat.Available);
        AssertOffline("kiln.chat");
        foreach (var name in (string[])["chat.send", "chat.receive", "chat.players"])
            Assert.StartsWith("needs kiln.chat", AssertOffline(name).ReasonText);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.chat", () => Chat.Broadcast("hello"));
        AssertUnavailable("kiln.chat", () => Chat.Tell(new EntityId(3), "hello"));
        AssertUnavailable("kiln.chat", () => Chat.Tell(new PlayerRef(7, Guid.Empty, new EntityRef(new EntityId(3), 4)), "hello"));
        AssertUnavailable("kiln.chat", () => Chat.Say(new EntityId(3), "hello"));
        AssertUnavailable("kiln.chat", () => Chat.Diagnostics.Inject(0, "hello"));
        AssertUnavailable("kiln.chat", () => Chat.Diagnostics.LastLine());
    }

    [Fact]
    public void Reads_that_never_threw_still_answer_empty()
    {
        Assert.Empty(Chat.Players);
        Assert.Null(Chat.FindPlayer(new EntityId(3)));
        Assert.Null(Chat.FindPlayer("Gus"));
        Assert.Equal(default, Chat.Status);
    }

    [Fact]
    public void A_chat_player_without_an_entity_is_no_player_ref()
    {
        var loading = new ChatPlayer(EntityId.None, "Gus", 9, 1, 77, ChatPermissions.None);
        Assert.True(((PlayerRef)loading).IsNone);
        Assert.Throws<ArgumentNullException>(() => (PlayerRef)(ChatPlayer)null!);
        // with an entity the player service decides who that is; here there is none
        var inGame = loading with { Entity = new EntityId(3) };
        var e = Assert.Throws<KilnUnavailableException>(() => (PlayerRef)inGame);
        Assert.Equal("identity.players", e.Capability);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Chat));
        var broadcast = Method(typeof(Chat), "Broadcast", typeof(string));
        AssertHas<TickThreadOnlyAttribute>(broadcast);
        AssertRequires(broadcast, "kiln.chat");
        var tell = Method(typeof(Chat), "Tell", typeof(PlayerRef), typeof(string));
        AssertHas<TickThreadOnlyAttribute>(tell);
        AssertRequires(tell, "kiln.chat");
        AssertHas<AnyThreadAttribute>(Property(typeof(Chat), "Players"));
        var inject = Method(typeof(Chat.Diagnostics), "Inject", typeof(uint), typeof(string));
        AssertHas<AnyThreadAttribute>(inject);
        AssertRequires(inject, "kiln.chat");
        Assert.True(typeof(IKilnResult).IsAssignableFrom(typeof(CommandResult)));
    }
}

/// <summary>Kiln.Hooks and Kiln.Eml by the contract.</summary>
public sealed class HooksAndEmlContractTests : ContractTest
{
    [Fact]
    public void With_no_hooks_service_the_row_says_why_and_the_members_throw_naming_it()
    {
        StartRows(typeof(Hooks).Assembly);
        Assert.False(Hooks.Available);
        AssertOffline("kiln.hooks");
        AssertUnavailable("kiln.hooks", () => Hooks.Stats());
        AssertUnavailable("kiln.hooks", () => _ = Symbols.ImageBase);
    }

    [Fact]
    public void The_hooks_row_is_there_again_after_the_registry_was_reset()
    {
        // kiln.hooks' wrapper looks its table up once per process; the row must not depend on that one lookup
        StartRows(typeof(Hooks).Assembly);
        AssertOffline("kiln.hooks");
        Capabilities.Reset();
        Assert.Equal(CapabilityReason.Unknown, Capabilities.Get("kiln.hooks").Reason);
        StartRows(typeof(Hooks).Assembly);
        AssertOffline("kiln.hooks");
    }

    [Fact]
    public void An_advanced_gate_still_comes_before_the_service()
    {
        // not from mod code: the gate's InvalidOperationException, as before
        Assert.Throws<InvalidOperationException>(() => Symbols.TryGet("SimStep", out _));
    }

    [Fact]
    public void Hooks_members_carry_their_capability()
    {
        AssertRequires(typeof(Symbols), "kiln.hooks");
        AssertRequires(typeof(Memory), "kiln.hooks");
        AssertRequires(typeof(Constants), "kiln.hooks");
        AssertRequires(Method(typeof(Hooks), "Stats"), "kiln.hooks");
        AssertRequires(Method(typeof(Hooks), "Detour", typeof(string), typeof(DetourHandler), typeof(int)), "kiln.hooks");
        AssertRequires(Property(typeof(Hooks), "Available"));
    }

    [Fact]
    public void Emls_pure_helpers_are_marked_any_thread()
    {
        AssertHas<AnyThreadAttribute>(typeof(Eml));
        AssertHas<AnyThreadAttribute>(typeof(DataHash));
        AssertHas<AnyThreadAttribute>(typeof(GuidHash));
    }
}
