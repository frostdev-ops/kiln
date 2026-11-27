// SPDX-License-Identifier: MIT
#pragma warning disable KILNX001  // LabBotInfo is lab-only API
using LabBotTest;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>A lab bot has no Kiln (it sends no Kiln frame), so kiln.net judges it a client without Kiln; on a lab whose
/// mods state clients "kiln" or "mod" (or with requireKiln, or modSync refuse and required mods) that refuses it. It has no
/// client a template could crash: mods/tests/labbot-test lets a live bot's machine in (NetVanillaClientEvent.Kick false)
/// and no other connection.</summary>
public sealed class LabBotPeersTests
{
    private static LabBotInfo Bot(int id, LabBotPhase phase, uint machine) =>
        new(id, phase, $"Bot {id}", 15, machine, 0x55u << 6 | 15, phase == LabBotPhase.InGame ? new EntityId(16) : EntityId.None,
            1, 0x3f, 7, 0, 1, Guid.Empty, 1, 0, "");

    [Fact]
    public void ALiveBotsMachineIsABot()
    {
        LabBotInfo[] bots = [Bot(1, LabBotPhase.Loading, 0x2a90), Bot(2, LabBotPhase.InGame, 0x2a8f)];
        Assert.True(BotPeers.IsBot(0x2a90, bots));  // held at Gameplay Ready while it loads
        Assert.True(BotPeers.IsBot(0x2a8f, bots));
    }

    [Fact]
    public void AnyOtherConnectionIsNot()
    {
        LabBotInfo[] bots = [Bot(1, LabBotPhase.Loading, 0x2a90), Bot(3, LabBotPhase.Left, 0x2a8e), Bot(4, LabBotPhase.Failed, 0x2a8d)];
        Assert.False(BotPeers.IsBot(0x81, bots));       // a player's game
        Assert.False(BotPeers.IsBot(0x2a8e, bots));     // a bot that left: its machine may be a player's now
        Assert.False(BotPeers.IsBot(0x2a8d, bots));
        Assert.False(BotPeers.IsBot(0x2a90, []));
    }

    // labbot-test's use and give wait (the first-time spawn intro, the item catalog) before they act on the bot's entity
    [Fact]
    public void AWaitingOpActsOnlyOnTheSameBotInGame()
    {
        var was = Bot(1, LabBotPhase.InGame, 0x2a90);
        Assert.True(BotPeers.Still(was, was));
        Assert.True(BotPeers.Still(was, was with { SavesAcked = 3 }));
        Assert.False(BotPeers.Still(was, null));  // forgotten
        Assert.False(BotPeers.Still(was, was with { Phase = LabBotPhase.Leaving }));  // logging out
        Assert.False(BotPeers.Still(was, Bot(1, LabBotPhase.Left, 0x2a90)));
        // the next bot in the slot it left has its entity (slot + 1) under a new id: not this bot
        Assert.False(BotPeers.Still(was, Bot(2, LabBotPhase.InGame, 0x2a8f)));
        Assert.False(BotPeers.Still(was, was with { Entity = new EntityId(15) }));
    }
}
