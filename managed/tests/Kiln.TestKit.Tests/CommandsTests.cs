// SPDX-License-Identifier: MIT
using TkCommands;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class CommandsTests
{
    [Fact]
    public void AModsCommandsRunAsTheConsole()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkCommandsMod>();  // its own TkCommands.kiln.mod.json
        kiln.Step();
        Assert.Equal("0.3.0", mod.Info.Version);

        var echo = kiln.RunCommand("/tk-echo hello there");
        Assert.True(echo.Ok, echo.Text);
        Assert.Equal(["echo: hello there", "tick thread True"], echo.Output);

        var add = kiln.RunCommand(CommandSource.Remote("dashboard", admin: false), "tk-add 2 3 4");
        Assert.True(add.Ok, add.Text);
        Assert.Equal(["sum 9"], add.Output);

        var help = kiln.RunCommand("help tk-add");
        Assert.True(help.Ok);
        Assert.Contains(help.Output, l => l.Contains("adds numbers", StringComparison.Ordinal));
        Assert.True(kiln.HasLog("command /tk-echo by console", "tk-commands"));
    }

    [Fact]
    public void AnUnknownCommandAndAFailingHandlerAreResults()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkCommandsMod>();
        kiln.Step();
        var unknown = kiln.RunCommand("tk-nope");
        Assert.False(unknown.Found);
        Assert.Equal("Unknown command '/tk-nope'. Type /help for a list.", unknown.Error);

        var failed = kiln.RunCommand("tk-fail");
        Assert.True(failed.Found);
        Assert.False(failed.Ok);
        Assert.Equal("/tk-fail failed (see the server log)", failed.Error);
        Assert.Contains(kiln.LogsOf("tk-commands"), l => l.Level == LogLevel.Error && l.Text.Contains("the handler broke", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommandsGoWithTheMod()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkCommandsMod>();
        kiln.Step();
        kiln.Unload(mod);
        Assert.False(kiln.RunCommand("tk-echo hi").Found);
        Assert.Null(Commands.Find("tk-add"));
    }
}
