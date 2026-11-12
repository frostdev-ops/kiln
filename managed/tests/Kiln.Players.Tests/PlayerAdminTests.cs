// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.PlayerTests;

/// <summary>Players.Kick and Bans without a game: the ban list on disk, the error rule, and the kick seam Kiln.Network
/// fills (here a stand-in). Enforcement at the connection is tested in Kiln.Network.Tests (PlayerGateTests).</summary>
public sealed class PlayerAdminTests : IDisposable
{
    private const ulong Steam = 76561190000000007;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-bans-" + Guid.NewGuid().ToString("n"));
    private readonly IPlayerDirectory _prevRoster = PlayerIdentity.Roster;
    private readonly FakeRoster _roster = new();

    public PlayerAdminTests()
    {
        PlayerAdmin.UseStore(new BanStore(Path.Combine(_dir, "bans.json")));
        PlayerIdentity.Roster = _roster;
        PlayerIdentity.Install();
        PlayerAdmin.Kicker = null;
        PlayerAdmin.KickOff = null;
    }

    public void Dispose()
    {
        PlayerAdmin.Kicker = null;
        PlayerAdmin.KickOff = null;
        PlayerIdentity.Roster = _prevRoster;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private readonly List<(string Name, string Reason, string? By)> _kicks = [];

    private void Network(string? off = null)
    {
        PlayerAdmin.Kicker = (p, r, reason, by, banned) =>
        {
            _kicks.Add((p.Name, reason, by));
            return Task.FromResult(new KickResult(r, p.Name, true, null));
        };
        PlayerAdmin.KickOff = () => off;
    }

    [Fact]
    public void WithoutKilnNetworkKickAndBanThrowKilnUnavailable()
    {
        var p = _roster.Join("Alder", 0, Steam, generation: 1, step: 10);
        Assert.Equal("players.kick", Assert.Throws<KilnUnavailableException>(() => { _ = Players.Kick((PlayerRef)p, "go"); }).Capability);
        Assert.Equal("players.bans", Assert.Throws<KilnUnavailableException>(() => Bans.Add(Steam, "go")).Capability);
        Network(off: "this process serves no clients");
        var e = Assert.Throws<KilnUnavailableException>(() => { _ = Players.Kick((PlayerRef)p, "go"); });
        Assert.Contains("serves no clients", e.Message);
        Assert.Equal("players.bans", Assert.Throws<KilnUnavailableException>(() => Bans.Add(Steam, "go")).Capability);
    }

    [Fact]
    public void BadArgumentsThrowArgumentException()
    {
        Network();
        var p = _roster.Join("Alder", 0, Steam, generation: 1, step: 10);
        Assert.Throws<ArgumentException>(() => { _ = Players.Kick(PlayerRef.None, "go"); });
        Assert.Throws<ArgumentException>(() => { _ = Players.Kick((PlayerRef)p, " "); });
        Assert.Throws<ArgumentException>(() => { _ = Players.Kick((PlayerRef)p, new string('x', 201)); });
        Assert.Throws<ArgumentException>(() => Bans.Add(0UL, "go"));
        Assert.Throws<ArgumentException>(() => Bans.Add(Steam, "go", TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Bans.Add(new PlayerRef(0, Guid.NewGuid(), EntityRef.None), "go"));
        Assert.Empty(_kicks);
    }

    [Fact]
    public async Task KickGoesThroughTheSeamAndAPlayerNotInGameIsARefusal()
    {
        Network();
        var p = _roster.Join("Alder", 0, Steam, generation: 1, step: 10);
        var r = await Players.Kick((PlayerRef)p, "be nice");
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        Assert.Equal([("Alder", "be nice", (string?)null)], _kicks);
        var gone = (PlayerRef)p;
        _roster.Leave(0);
        var r2 = await Players.Kick(gone, "again");
        Assert.False(r2.Ok);
        Assert.Equal("the player is not in game", r2.Reason);
        Assert.Single(_kicks);
    }

    [Fact]
    public void TheBanListIsKeptOnDiskAndMatchesSteamOrAccount()
    {
        Network();
        var a = Bans.Add(Steam, "griefing");
        Assert.Equal("console", a.By);
        Assert.Null(a.Until);
        Assert.Same(a, Bans.Find(Steam));
        var timed = Bans.Add(Steam + 1, "cool off", TimeSpan.FromHours(2));
        Assert.True(timed.Until > DateTimeOffset.UtcNow.AddMinutes(119));
        Assert.Equal(2, Bans.All.Count);
        // a second ban of the same id replaces the first
        Bans.Add(Steam, "griefing again");
        Assert.Equal(2, Bans.All.Count);
        Assert.Equal("griefing again", Bans.Find(Steam)!.Reason);
        // read back from the file by a new store (the next start)
        var reread = new BanStore(Path.Combine(_dir, "bans.json"));
        Assert.Equal("griefing again", reread.Find(Steam, 0, DateTimeOffset.UtcNow)!.Reason);
        Assert.NotNull(reread.Find(0, Steam + 1, DateTimeOffset.UtcNow));  // an account id equal to a banned Steam id matches
        Assert.Null(reread.Find(Steam + 1, 0, DateTimeOffset.UtcNow.AddHours(3)));  // ran out
        Assert.True(Bans.Remove(Steam));
        Assert.False(Bans.Remove(Steam));
        Assert.Null(Bans.Find(Steam));
        Assert.Contains("\"reason\": \"cool off\"", File.ReadAllText(Path.Combine(_dir, "bans.json")));
    }

    [Fact]
    public void AnOnlinePlayersBanCarriesTheAccountIdAndTheName()
    {
        Network();
        var p = _roster.Join("Birch", 1, Steam, generation: 1, step: 10) with { AccountId = 4242 };
        _roster.Connections[0] = p;
        var e = Bans.Add((PlayerRef)p, "spam");
        Assert.Equal((Steam, 4242UL, "Birch"), (e.SteamId, e.AccountId, e.Name));
        Assert.True(e.Matches(0, 4242));
        Assert.False(e.Matches(0, 0));
    }

    [Fact]
    public void BanAndUnbanPostTheirEvents()
    {
        Network();
        var banned = new List<BanEntry>();
        var unbanned = new List<(BanEntry, string)>();
        using var s1 = Events.On<PlayerBannedEvent>(e => banned.Add(e.Ban));
        using var s2 = Events.On<PlayerUnbannedEvent>(e => unbanned.Add((e.Ban, e.By)));
        var b = Bans.Add(Steam, "x");
        Bans.Remove(Steam);
        Assert.Equal([b], banned);
        Assert.Equal([(b, "console")], unbanned);
    }

    [Fact]
    public void ABrokenBanFileIsLeftAloneAndRefusesWrites()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bans.json"), "{ not json");
        var store = new BanStore(Path.Combine(_dir, "bans.json"));
        Assert.Empty(store.Active(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => store.Put(new BanEntry(Steam, 0, "", "x", "t", DateTimeOffset.UtcNow, null), DateTimeOffset.UtcNow));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_dir, "bans.json")));
    }
}
