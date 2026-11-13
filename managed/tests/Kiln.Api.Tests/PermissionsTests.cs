// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

public sealed class PermissionsTests : KilnTest
{
    private const ulong Steve = 76561190000000011, Gus = 76561190000000012;

    private void ServerGroups() => File.WriteAllText(Path.Combine(Path.GetDirectoryName(Root)!, "enshrouded_server.json"), """
        { "userGroups": [
          { "name": "Admin", "password": "a", "canKickBan": true, "canAccessInventories": true, "canEditBase": true, "canEditWorld": true, "canExtendBase": true },
          { "name": "Friend", "password": "f", "canKickBan": false, "canAccessInventories": true, "canEditBase": true, "canEditWorld": true, "canExtendBase": false },
          { "name": "Guest", "password": "g", "canKickBan": false, "canAccessInventories": false, "canEditBase": false, "canEditWorld": true, "canExtendBase": false }
        ] }
        """);

    private void Rules(string json) => File.WriteAllText(Path.Combine(Root, "config", "permissions.json"), json);

    [Fact]
    public void Patterns_and_specificity()
    {
        Assert.Equal("mymod.fly", PermissionRules.Normalize(" MyMod.Fly "));
        Assert.Null(PermissionRules.Normalize("a..b"));
        Assert.Null(PermissionRules.Normalize("*.a"));
        Assert.Equal("a.*", PermissionRules.Normalize("a.*"));
        Assert.Equal(1000, PermissionRules.Match("a.b", "a.b"));
        Assert.Equal(2, PermissionRules.Match("a.b.*", "a.b.c.d"));
        Assert.Equal(-1, PermissionRules.Match("a.b.*", "a.b"));
        Assert.Equal(0, PermissionRules.Match("*", "x"));
        var best = PermissionRules.Best([new PermissionRule("a.*", true), new PermissionRule("a.b.*", false), new PermissionRule("*", true)], "a.b.c",
            PermissionLevel.Admin);
        Assert.Equal(new PermissionRule("a.b.*", false), best);
        Assert.False(PermissionRules.Best([new PermissionRule("a.*", true), new PermissionRule("a.*", false)], "a.x", PermissionLevel.Admin)!.Value.Allow);
        Assert.Null(PermissionRules.Best([new PermissionRule("*", true), new PermissionRule("a.*", true)], "a.x", PermissionLevel.Console));
    }

    [Fact]
    public void Default_levels_without_rules()
    {
        Permissions.Register("t.open", PermissionLevel.Everyone);
        Permissions.Register("t.admin", PermissionLevel.Admin);
        Permissions.Register("t.console", PermissionLevel.Console);
        var guest = PermissionSubject.ForSteamId(Gus, "Gus", PlayerPermissions.EditWorld);
        var admin = PermissionSubject.ForSteamId(Steve, "Steve", PlayerPermissions.KickBan);
        Assert.True(Permissions.Has(guest, "t.open"));
        Assert.False(Permissions.Has(guest, "t.admin"));
        Assert.True(Permissions.Has(admin, "t.admin"));
        Assert.False(Permissions.Has(admin, "t.console"));
        Assert.True(Permissions.Has(PermissionSubject.Console, "t.console"));
        Assert.True(Permissions.Has(PermissionSubject.Remote("dash"), "t.console"));
        Assert.False(Permissions.Has(PermissionSubject.Remote("dash", admin: false), "t.admin"));
        Assert.True(Permissions.Has(PermissionSubject.Remote("dash", admin: false), "t.open"));
        Assert.False(Permissions.Has(guest, "t.unregistered"));  // unregistered = admin node
        Assert.True(File.Exists(Permissions.FilePath));  // default file written at first use
    }

    [Fact]
    public void User_groups_map_by_rights_and_ids()
    {
        ServerGroups();
        Rules("""
            {
              "groups": {
                "default": { "nodes": ["t.chat"] },
                "builders": { "inherits": ["default"], "nodes": ["t.build.*", "-t.build.nuke"] },
                "staff": { "admin": true, "nodes": ["*"] }
              },
              "userGroups": { "Friend": ["builders"], "Admin": ["staff"] },
              "userGroupIds": { "7": "Friend" },
              "players": {}
            }
            """);
        Permissions.Register("t.build.wall", PermissionLevel.Admin);
        Permissions.Register("t.build.nuke", PermissionLevel.Admin);
        var friend = PermissionSubject.ForSteamId(Gus, "Gus", PlayerPermissions.AccessInventories | PlayerPermissions.EditBase | PlayerPermissions.EditWorld | PlayerPermissions.ReceiveExp);
        Assert.Equal("Friend", Permissions.UserGroupOf(friend));
        Assert.Equal(["builders", "default"], Permissions.GroupsOf(friend));
        Assert.True(Permissions.Has(friend, "t.build.wall"));
        Assert.False(Permissions.Has(friend, "t.build.nuke"));
        Assert.True(Permissions.Has(friend, "t.chat"));
        var byId = PermissionSubject.ForSteamId(Steve, "Steve", PlayerPermissions.EditWorld, userGroupId: 7);
        Assert.Equal("Friend", Permissions.UserGroupOf(byId));
        var admin = PermissionSubject.ForSteamId(Steve, "Steve", (PlayerPermissions)0x3F);
        Assert.Equal("Admin", Permissions.UserGroupOf(admin));
        Assert.True(Permissions.IsAdmin(admin));
        var d = Permissions.Explain(admin, "t.build.nuke");
        Assert.True(d.Allowed);
        Assert.Equal("group:staff", d.From);
        Assert.Equal(3, Permissions.ServerUserGroups.Count);
    }

    [Fact]
    public void Player_rules_beat_group_rules()
    {
        Rules("""
            { "groups": { "default": { "nodes": ["t.*"] } },
              "players": { "76561190000000012": { "name": "Gus", "groups": [], "nodes": ["-t.fly"] } } }
            """);
        var gus = PermissionSubject.ForSteamId(Gus);
        Assert.True(Permissions.Has(gus, "t.walk"));
        var d = Permissions.Explain(gus, "t.fly");
        Assert.False(d.Allowed);
        Assert.Equal("player", d.From);
        Assert.Equal("-t.fly", d.Rule);
    }

    [Fact]
    public void Edits_are_written_and_reloads_raise_the_event()
    {
        var events = new List<PermissionsChangedEvent>();
        Events.On<PermissionsChangedEvent>(events.Add);
        Permissions.Register("t.home", PermissionLevel.Admin);
        var gus = PermissionSubject.ForSteamId(Gus);
        Assert.False(Permissions.Has(gus, "t.home"));
        Permissions.SetPlayerNode(Gus, "t.home", true, "Gus");
        Assert.True(Permissions.Has(gus, "t.home"));
        Permissions.SetGroup("vip", admin: true);
        Permissions.AddPlayerToGroup(Steve, "vip");
        Assert.True(Permissions.IsAdmin(PermissionSubject.ForSteamId(Steve)));
        var text = File.ReadAllText(Permissions.FilePath);
        Assert.Contains("\"76561190000000012\": { \"name\": \"Gus\", \"groups\": [], \"nodes\": [\"t.home\"] }", text);
        Assert.Contains("\"vip\": { \"admin\": true", text);
        Assert.True(PermissionStore.Parse(text, out var round, out _));
        Assert.Equal(PermissionStore.Format(round), text);
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("api", e.Reason));

        Rules(text.Replace("\"nodes\": [\"t.home\"]", "\"nodes\": [\"-t.home\"]"));
        File.SetLastWriteTimeUtc(Permissions.FilePath, DateTime.UtcNow.AddSeconds(3));
        PermissionStore.Load("file");
        Assert.False(Permissions.Has(gus, "t.home"));
        Assert.Equal("file", events[^1].Reason);
    }

    [Fact]
    public void A_player_ref_is_asked_by_steam_id_also_after_its_session()
    {
        Rules("""{ "players": { "76561190000000012": { "name": "Gus", "groups": [], "nodes": ["t.fly"] } } }""");
        Permissions.Register("t.fly", PermissionLevel.Admin);
        // the session entity is stale (slot 3 belongs to someone else by now): the Steam id still decides
        var gus = new PlayerRef(Gus, Guid.NewGuid(), new EntityRef(new EntityId(3), 41));
        Assert.True(Permissions.Has(gus, "t.fly"));
        Assert.False(Permissions.Has(new PlayerRef(Steve, Guid.Empty, EntityRef.None), "t.fly"));
        Assert.Equal(Permissions.Has(PermissionSubject.ForSteamId(Gus), "t.fly"), Permissions.Has(gus, "t.fly"));
        Assert.Throws<ArgumentException>(() => Permissions.Has(PlayerRef.None, "t.fly"));
    }

    [Fact]
    public void Broken_file_keeps_the_rules()
    {
        Permissions.SetPlayerNode(Gus, "t.x", true);
        Rules("{ \"players\": oops");
        PermissionStore.Load("file");
        Assert.True(Permissions.Has(PermissionSubject.ForSteamId(Gus), "t.x"));
        Assert.True(Backend.Logged("keeping the previous rules"));
    }

    [Fact]
    public void Nodes_belong_to_their_mod()
    {
        var mod = NewMod("owner");
        As(mod, () => Permissions.Register("owner.fly", PermissionLevel.Everyone, "fly"));
        Assert.Equal("owner", Permissions.Find("owner.fly")!.Owner);
        Assert.Contains(Permissions.Nodes, n => n.Node == "owner.fly");
        mod.Retire();
        Assert.Null(Permissions.Find("owner.fly"));
        Assert.Throws<ArgumentException>(() => Permissions.Register("bad node!"));
    }
}
