// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Host.Tests;

public sealed class UiOwnerLookupTests : ModsFixture
{
    [Fact]
    public void Published_metadata_resolves_the_actual_loaded_context_before_and_after_reload()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        var old = mods.Find("hello")!.ModContext;
        var info = Kiln.Mods.Get("hello")!.Info!;
        Assert.Same(old.Info, info);
        Assert.Same(old, ModContext.FindLive(info));
        Assert.True(mods.Reload("hello", out var error), error);
        var next = mods.Find("hello")!.ModContext;
        var nextInfo = Kiln.Mods.Get("hello")!.Info!;
        Assert.NotSame(info, nextInfo);
        Assert.Same(next.Info, nextInfo);
        Assert.Null(ModContext.FindLive(info));
        Assert.Same(next, ModContext.FindLive(nextInfo));
        Assert.True(mods.Unload("hello", out _));
        Assert.Null(ModContext.FindLive(nextInfo));
    }
}
