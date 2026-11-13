// SPDX-License-Identifier: MIT
// The kit's recording fake of kiln.chat: Kiln.Chat's own code, the fake players as the chat's players.
using System.Runtime.CompilerServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class ChatFakeTests
{
    private const ulong AdaSteam = 76561198000000601, BoSteam = 76561198000000602;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static (KilnHarness Kiln, FakePlayer Ada, FakePlayer Bo) Start(KilnHarnessOptions? options = null)
    {
        var kiln = new KilnHarness(options);
        kiln.Load<TkFakesMod>();
        var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        var bo = kiln.Players.Join("Bo", BoSteam);
        kiln.Step(2);
        return (kiln, ada, bo);
    }

    private static string[] Texts(FakePlayer p) => [.. p.Chat.Select(l => l.Text)];

    [Fact]
    public void ServerLinesReachThePlayersTheyAreFor()
    {
        var (kiln, ada, bo) = Start();
        using var _ = kiln;
        Assert.Equal("tell: True", Said(kiln.RunCommand(ada, "tkf-tell Bo meet at the gate")));
        Assert.Equal(["[Server] meet at the gate"], Texts(bo));
        Assert.Equal(["[Server] tell: True"], Texts(ada));  // a command's reply goes to the player who ran it
        Assert.Equal("tell: nobody", Said(kiln.RunCommand("tkf-tell Cy hello")));
        Assert.Equal("broadcast to 2", Said(kiln.RunCommand("tkf-broadcast the storm comes")));
        Assert.Equal("[Server] the storm comes", Texts(ada)[^1]);
        Assert.Equal("[Server] the storm comes", Texts(bo)[^1]);
        Assert.Equal("say: True", Said(kiln.RunCommand(ada, "tkf-say hello everyone")));
        var said = bo.Chat[^1];
        Assert.Equal(("Ada: hello everyone", "Ada", "tk-fakes"), (said.Text, said.Speaker, said.ModId));
        var tell = kiln.Chat.Sent[0];
        Assert.Equal(("[Server] meet at the gate", (string?)null, "tk-fakes", bo), (tell.Text, tell.Speaker, tell.ModId, tell.To));
        Assert.Null(kiln.Chat.Sent.Single(l => l.Text == "[Server] the storm comes").To);
        Assert.Equal(kiln.CurrentStep, tell.Step);
        Assert.Equal($"chatters: Ada/1/KickBan,Bo/2/None", Said(kiln.RunCommand("tkf-chatters")));
        // a player who left is not there to tell
        bo.Leave();
        kiln.Step();
        Assert.Equal("tell: nobody", Said(kiln.RunCommand(ada, "tkf-tell Bo still there?")));
        Assert.False(kiln.Run(() => Chat.Tell(bo.Entity, "gone")));
    }

    [Fact]
    public void ALineAPlayerTypesReachesHandlersAndCommandsAtTheNextStep()
    {
        var (kiln, ada, bo) = Start();
        using var _ = kiln;
        ada.Say("hello all");
        Assert.Empty(kiln.Lines("tk-fakes", "chat received:"));
        kiln.Step();
        Assert.Single(kiln.Lines("tk-fakes", "chat received:"), l => l.EndsWith("Ada (1): hello all command False", StringComparison.Ordinal));
        Assert.Equal(["Ada: hello all"], Texts(bo));
        Assert.Equal(["Ada: hello all"], Texts(ada));  // the sender sees their own line through the echo

        ada.Say("my secret is out");  // a handler cancels it: nobody sees it
        bo.Say("/tkf-broadcast big news");  // a command line runs as Bo and is not shown
        kiln.Step();
        Assert.Equal(["Ada: hello all", "[Server] big news"], Texts(ada));
        Assert.Equal(["Ada: hello all", "[Server] big news", "[Server] broadcast to 2"], Texts(bo));
        Assert.Equal([("hello all", false), ("my secret is out", true), ("/tkf-broadcast big news", true)],
            kiln.Chat.Typed.Select(m => (m.Text, m.Consumed)));
        Assert.Equal([ada, ada, bo], kiln.Chat.Typed.Select(m => m.Player));

        ada.Say("shout");  // ChatMessageEvent rewrites it
        bo.Say("/tkf-nope");  // an unknown command
        kiln.Step();
        Assert.Equal("Ada: SHOUT", Texts(bo)[^2]);
        Assert.Contains("tkf-nope", Texts(bo)[^1]);
    }

    [Fact]
    public void BeforeTheServerStartsNothingCanBeSentAndSwitchedOffNothingWorks()
    {
        var (kiln, ada, _) = Start(new KilnHarnessOptions { WaitForWorldRunning = true });
        using (kiln)
        {
            Assert.False(kiln.Run(() => Chat.Status.SendReady));
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Chat.Broadcast("too early")));
            kiln.ReportWorldRunning();
            kiln.Step();
            Assert.Equal(new ChatStatus(true, true, 2, kiln.Run(() => Chat.Status.Subscribers), 0, 0, 0, 0), kiln.Run(() => Chat.Status));
            Assert.Equal(2, kiln.Run(() => Chat.Broadcast("now")));
            kiln.Capabilities.Override("chat.send", false);
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Chat.Broadcast("off")));
            kiln.Capabilities.ClearOverride("chat.send");
            kiln.Capabilities.Override("kiln.chat", false);
            Assert.Empty(kiln.Run(() => Chat.Players));
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Chat.Tell(ada.Entity, "off")));
            kiln.Capabilities.ClearOverride("kiln.chat");
            Assert.True(kiln.Run(() => Chat.Tell(ada.Entity, "on")));
            Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Chat.Diagnostics.LastLine()));  // the inbox is not modelled
        }
        Assert.Empty(Chat.Players);
        Assert.False(Chat.Status.SendReady);
        using var next = new KilnHarness();
        next.Step();
        Assert.Empty(next.Chat.Sent);
        Assert.Empty(next.Chat.Typed);
    }

    [Fact]
    public void TheTableMirrorsTheHeaderAsTheServiceReadsIt()
    {
        Assert.Equal((96, 128, 48, 32, 80), (Unsafe.SizeOf<ChatAbi.Player>(), Unsafe.SizeOf<ChatAbi.Message>(), Unsafe.SizeOf<ChatAbi.Status>(),
            Unsafe.SizeOf<ChatAbi.LineInfo>(), Unsafe.SizeOf<ChatAbi.Table>()));
        var chat = typeof(Chat);
        Assert.Equal(Unsafe.SizeOf<ChatAbi.Player>(), Mirror.Size(chat, "NativePlayer"));
        Assert.Equal(Unsafe.SizeOf<ChatAbi.Message>(), Mirror.Size(chat, "NativeMessage"));
        Assert.Equal(Unsafe.SizeOf<ChatAbi.Status>(), Mirror.Size(chat, "NativeStatus"));
        Assert.Equal(Unsafe.SizeOf<ChatAbi.LineInfo>(), Mirror.Size(chat, "NativeLineInfo"));
        Assert.Equal(Unsafe.SizeOf<ChatAbi.Table>(), Mirror.Size(chat, "Table"));
    }
}
