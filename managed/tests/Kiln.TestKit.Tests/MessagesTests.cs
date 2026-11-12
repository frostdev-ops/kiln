// SPDX-License-Identifier: MIT
using TkLifeBase;
using TkLifeUser;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class MessagesTests
{
    [Fact]
    public void AMessageBetweenModsArrivesAtTheNextStepBeforePre()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkLifeBaseMod>(TestMods.Base());
        kiln.Load<TkLifeUserMod>(TestMods.User());
        kiln.Step();
        kiln.Run(() => Mods.Send("tk-user", "send"));
        kiln.Step();
        var sentAt = Assert.Single(kiln.LogsOf("tk-user"), l => l.Text == "sent hello: True").Step;
        Assert.Empty(kiln.Lines("tk-base", "message:"));
        kiln.Step();
        var message = Assert.Single(kiln.LogsOf("tk-base"), l => l.Text.StartsWith("message:", StringComparison.Ordinal));
        var pre = Assert.Single(kiln.LogsOf("tk-base"), l => l.Text.StartsWith("pre after message", StringComparison.Ordinal));
        Assert.Equal("message: hi from tk-user", message.Text);
        Assert.Equal(sentAt + 1, message.Step);
        Assert.Equal(message.Step, pre.Step);
        Assert.True(message.Sequence < pre.Sequence);
    }

    [Fact]
    public void AMessageFromAnUnloadedSenderIsDropped()
    {
        using var kiln = new KilnHarness();
        var b = kiln.Load<TkLifeBaseMod>(TestMods.Base());
        var u = kiln.Load<TkLifeUserMod>(new ModManifest("tk-user"));  // no dependency: tk-base stays when it goes
        kiln.Step();
        kiln.Run(() => Mods.Send("tk-user", "send"));
        kiln.Step();
        kiln.Unload(u);
        kiln.Step();
        Assert.Empty(kiln.Lines("tk-base", "message:"));
        Assert.True(b.IsLoaded);
    }
}
