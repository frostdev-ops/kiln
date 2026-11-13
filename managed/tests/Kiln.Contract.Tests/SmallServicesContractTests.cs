// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Example, the template service: the contract in its smallest form.</summary>
public sealed class ExampleContractTests : ContractTest
{
    [Fact]
    public void With_no_service_the_row_says_why_and_the_member_throws_naming_it()
    {
        StartRows(typeof(Example).Assembly);
        Assert.False(Example.Available);
        AssertOffline("kiln.example");
        AssertUnavailable("kiln.example", () => _ = Example.Steps);
    }

    [Fact]
    public void Members_carry_their_thread_and_capability()
    {
        AssertHas<AnyThreadAttribute>(Property(typeof(Example), "Available"));
        AssertHas<AnyThreadAttribute>(Property(typeof(Example), "Steps"));
        AssertRequires(Property(typeof(Example), "Steps"), "kiln.example");
    }
}

/// <summary>Kiln.Assets by the contract.</summary>
public sealed class AssetsContractTests : ContractTest
{
    private static readonly AssetKey Key = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), 0x1234, 0);

    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Assets).Assembly);
        Assert.False(Assets.Available);
        AssertOffline("kiln.assets");
        foreach (var name in (string[])["assets.resources", "assets.content", "assets.ui_icons", "assets.textures"])
            AssertOffOffline(name, "needs kiln.assets");
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.assets", () => Assets.ReadResource(Key));
        AssertUnavailable("kiln.assets", () => Assets.ListResources(0x1234));
        AssertUnavailable("kiln.assets", () => Assets.Register(Key, [1, 2, 3]));
        AssertUnavailable("kiln.assets", () => Assets.RegisterIcon(Key.Guid, [1, 2, 3]));
        AssertUnavailable("kiln.assets", () => Assets.OverrideTexture(Key, [1, 2, 3]));
        AssertUnavailable("kiln.assets", () => Assets.AddBlob([1, 2, 3]));
        AssertUnavailable("kiln.assets", () => Assets.Dump("x"));
        AssertUnavailable("kiln.assets", () => Assets.LoadManifest("x"));
    }

    [Fact]
    public void Reads_that_never_threw_still_answer_empty_and_a_bad_key_is_an_argument()
    {
        Assert.Equal(IconState.None, Assets.IconStateOf(Key.Guid));
        Assert.Equal("{\"available\":false}", Assets.Status());
        Assert.Empty(Assets.ListAllBlobs());
        // the player's animation graph is refused before the service is asked
        var graph = new AssetKey(Guid.Parse("21319e83-5087-4fff-8795-22997843649e"), 0xa345050eu, 0);
        Assert.Throws<ArgumentException>(() => Assets.Override(graph, [1]));
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<AnyThreadAttribute>(typeof(Assets));
        var icon = Method(typeof(Assets), "RegisterIcon", typeof(Guid), typeof(byte[]));
        AssertHas<ClientOnlyAttribute>(icon);
        AssertRequires(icon, "assets.ui_icons");
        var texture = Method(typeof(Assets), "OverrideTexture", typeof(AssetKey), typeof(byte[]));
        AssertHas<ClientOnlyAttribute>(texture);
        AssertRequires(texture, "assets.textures");
        AssertRequires(Method(typeof(Assets), "Register", typeof(AssetKey), typeof(byte[]), typeof(bool?)), "assets.resources");
        AssertRequires(Method(typeof(Assets), "AddBlob", typeof(byte[])), "assets.content");
        AssertRequires(Method(typeof(Assets), "Dump", typeof(string)), "kiln.assets");
    }
}

/// <summary>Kiln.Settings by the contract.</summary>
public sealed class SettingsContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Settings).Assembly);
        Assert.False(Settings.Available);
        Assert.False(Settings.Ready);
        Assert.Equal(0, Settings.Revision);
        AssertOffline("kiln.settings");
        foreach (var name in (string[])["settings.ready", "settings.live", "settings.world", "settings.resend", "settings.daytime"])
            AssertOffOffline(name, "needs kiln.settings");
        Assert.False(Capabilities.Get("settings.ready").SessionDependent);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.settings", () => _ = Settings.All);
        AssertUnavailable("kiln.settings", () => _ = Settings.Values);
        AssertUnavailable("kiln.settings", () => Settings.Get("playerHealthFactor"));
        AssertUnavailable("kiln.settings", () => Settings.Set("playerHealthFactor", 2.0));
        AssertUnavailable("kiln.settings", () => Settings.TrySet("playerHealthFactor", 2.0, out _, out _));
        AssertUnavailable("kiln.settings", () => Settings.Reset("playerHealthFactor"));
        AssertUnavailable("kiln.settings", () => Settings.ResetAll());
        AssertUnavailable("kiln.settings", () => Settings.Resend());
        AssertUnavailable("kiln.settings", () => Settings.Save());
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Settings));
        AssertHas<AnyThreadAttribute>(Property(typeof(Settings), "Status"));
        AssertHas<AnyThreadAttribute>(Property(typeof(Settings), "Ready"));
        var set = Method(typeof(Settings), "Set", typeof(string), typeof(object), typeof(bool), typeof(bool));
        AssertHas<TickThreadOnlyAttribute>(set);
        AssertRequires(set, "kiln.settings");
        AssertHas<TickThreadOnlyAttribute>(Property(typeof(Settings), "All"));
    }
}

/// <summary>Kiln.Client and Kiln.Permissions: no row of their own, attributes and the identity overload.</summary>
public sealed class ClientAndPermissionsContractTests : ContractTest
{
    [Fact]
    public void Client_state_is_marked_any_thread_and_answers_with_no_service()
    {
        foreach (var name in (string[])["Available", "IsClient", "State", "InGame", "LocalPlayer", "IsClientThread"])
            AssertHas<AnyThreadAttribute>(Property(typeof(Client), name));
        AssertHas<AnyThreadAttribute>(Method(typeof(Client), "Post", typeof(Action)));
        Assert.False(Client.Available);
        Assert.False(Client.IsClient);
        Assert.True(Client.LocalPlayer.IsNone);
    }

    [Fact]
    public void Permission_reads_are_marked_any_thread_and_edits_tick_thread()
    {
        AssertHas<AnyThreadAttribute>(Method(typeof(Permissions), "Has", typeof(PermissionSubject), typeof(string)));
        AssertHas<AnyThreadAttribute>(Method(typeof(Permissions), "Explain", typeof(PermissionSubject), typeof(string)));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Permissions), "SetGroupNode", typeof(string), typeof(string), typeof(bool?)));
    }

    [Fact]
    public void A_permission_subject_from_a_player_ref_is_asked_by_steam_id()
    {
        const ulong steve = 76561190000000011;
        // a ref of an earlier session: the Steam id decides, the stale session entity is not carried over
        var earlier = new PlayerRef(steve, Guid.NewGuid(), new EntityRef(new EntityId(3), 41));
        var subject = PermissionSubject.ForPerson(earlier);
        Assert.Equal(PermissionSubjectKind.Player, subject.Kind);
        Assert.Equal(steve, subject.SteamId);
        Assert.True(subject.Entity.IsNone);

        Assert.Throws<ArgumentException>(() => PermissionSubject.ForPerson(PlayerRef.None));

        // no Steam id and no session entity: a player nobody knows, never somebody else's entity
        var unknown = PermissionSubject.ForPerson(new PlayerRef(0, Guid.NewGuid(), EntityRef.None));
        Assert.Equal(PermissionSubjectKind.Player, unknown.Kind);
        Assert.Equal(0ul, unknown.SteamId);
        Assert.True(unknown.Entity.IsNone);
    }
}
