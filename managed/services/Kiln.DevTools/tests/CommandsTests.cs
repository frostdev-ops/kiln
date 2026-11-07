// SPDX-License-Identifier: MIT
using Kiln;
using Xunit;

public sealed class DevToolsCommandTests
{
    [Fact]
    public void ComponentWriteParsesAsAChildOfPathAndRequiresAnAdmin()
    {
        var mod = HostAccess.CreateContext(new ModInfo("starts-tools", "starts-tools", "1", "/tmp"), 456, "/tmp");
        HostAccess.Current = mod;
        try { KilnDevTools.Start(); }
        finally { HostAccess.Current = null; }
        mod.Retire(); // Shared command registrations must survive the initiating mod's unload.
        var reader = CommandSource.Remote("reader", admin: false);
        var read = Commands.Execute(reader, "component 1 Health dataStorage[0]");
        Assert.True(read.Ok, read.Error); // Missing world is an ordinary no-component reply.
        var denied = Commands.Execute(reader, "component 1 Health dataStorage[0] 7");
        Assert.True(denied.Denied, denied.Error);
        var allowed = Commands.Execute(CommandSource.Console, "component 1 Health dataStorage[0] 7");
        Assert.False(allowed.Denied);
        Assert.False(allowed.ParseError, allowed.Error); // Reached the write handler, then refused the missing component.
        Assert.Contains("has no Health", allowed.Error);
    }
}

public sealed class DevToolsDispatchTests
{
    [Fact]
    public void RenderActionsWaitForTickAndOneRefusalDoesNotDropLaterChanges()
    {
        var ran = false;
        KilnDevTools.ChangeOnTick(() => throw new NotSupportedException("protected system"));
        KilnDevTools.ChangeOnTick(() => ran = true);
        Assert.False(ran);
        KilnDevTools.RunChanges();
        Assert.True(ran);
    }
}
