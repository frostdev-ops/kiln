// SPDX-License-Identifier: MIT
// A change event that waits for the next step (a handler wrote again during its dispatch) still goes out when the server
// stops or a hosted world closes before that step (docs/kiln/modding/contract.md, "Change events").
using Kiln.Host;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.Host.Tests;

public sealed class ChangeNoticeStopTests : ModsFixture
{
    private sealed class Changed(int key) : KilnEvent
    {
        public int Key { get; } = key;
    }

    private readonly ChangeNotices<Changed, int> _notices = new("test");
    private readonly List<int> _seen = [];

    private EventSubscription Watch() => Events.On<Changed>(e =>
    {
        _seen.Add(e.Key);
        if (e.Key < 3) _notices.Post(e.Key + 1, new Changed(e.Key + 1));  // each handler writes again
    });

    [Fact]
    public void What_waits_goes_out_before_the_mods_unload_at_a_stop()
    {
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        using var sub = Watch();
        _notices.Post(1, new Changed(1));
        Assert.Equal([1], _seen);              // 2 waits for the next step
        mods.UnloadAll("server stopping");     // the stop comes first
        Assert.Equal([1, 2], _seen);           // 3 was written in the last round: not announced, and said so
        Assert.Equal(0, _notices.Waiting);
        Assert.True(host.Has("1 change event(s) of writes made in the last round before mods unloaded (server stopping)"), host.Dump());
    }

    [Fact]
    public void What_waits_goes_out_before_the_server_mods_unload_when_a_hosted_world_closes()
    {
        var host = new FakeHost();
        using var mods = NewManager(host, new ModManagerOptions
        {
            Side = ManifestSide.Client, KilnVersion = host.Version, HasFeature = host.HasFeature,
            StateFile = Path.Combine(LogsDir, "mods.txt"), TestResultsDirectory = Path.Combine(LogsDir, "tests"),
        });
        mods.LoadAll();
        mods.StartHosting();
        using var sub = Watch();
        _notices.Post(1, new Changed(1));
        Assert.Equal([1], _seen);
        mods.StopHosting("the game is closing it");
        Assert.Equal([1, 2], _seen);
        Assert.Equal(0, _notices.Waiting);
    }
}
