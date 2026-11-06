// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>What a Kiln client makes of a REJECT: last-server.json's <c>missing</c> and the notice Kiln.Ui shows.</summary>
public sealed class RefusalTests
{
    // A server with net-test (side both, no clients: required by default from side), npcsample and quests-sample (both
    // state clients: "mod"), refusing a client that has none of them under the default policy.
    private static readonly string[] DefaultPolicyDetails = ["npcsample 0.1.0 (missing)", "quests-sample 2.0.0 (missing)"];
    private static readonly string[] Lacking = ["net-test", "npcsample", "quests-sample"];

    [Fact]
    public void Missing_is_what_the_server_named_not_every_required_mod_the_client_lacks() =>
        Assert.Equal(["npcsample", "quests-sample"], NetRefusal.Missing(DefaultPolicyDetails, Lacking));

    [Fact]
    public void Under_refuse_the_server_names_every_shared_mod_and_missing_follows()
    {
        string[] details = ["net-test 0.1.0 (missing)", "npcsample 0.1.0 (missing)", "quests-sample 2.0.0 (missing)", "channel net-test:echo v1 (missing)"];
        Assert.Equal(Lacking, NetRefusal.Missing(details, Lacking));
    }

    [Fact]
    public void A_reject_without_details_falls_back_to_every_lacking_required_mod() =>
        Assert.Equal(Lacking, NetRefusal.Missing([], Lacking));

    [Fact]
    public void A_refusal_for_something_else_names_no_mod()
    {
        string[] details = ["channel net-test:echo v1 (client has v2)", "Kiln network protocol 2 (client speaks 1)",
            "the world's material tables differ from this game's"];
        Assert.Empty(NetRefusal.Missing(details, Lacking));
    }

    [Fact]
    public void Named_mods_keep_the_server_order_and_the_clients_version()
    {
        var named = NetRefusal.NamedMods(["b-mod 1.0.0 (client has 0.9.0)", "a.mod_2 v2.1.0-beta.1+x (missing)", "b-mod 1.0.0 (missing)"]);
        Assert.Equal([("b-mod", "1.0.0", "0.9.0"), ("a.mod_2", "v2.1.0-beta.1+x", (string?)null)], named);
    }

    [Theory]
    [InlineData("channel net-test:echo v1 (missing)")]   // a mod named "channel" would still need a version second
    [InlineData("Kiln network protocol 2 (client speaks 1)")]
    [InlineData("npcsample 0.1.0 missing")]
    [InlineData("NpcSample 0.1.0 (missing)")]
    [InlineData("npcsample 0.1.0 (missing) extra")]
    [InlineData("")]
    public void Other_details_are_no_mod(string detail) => Assert.Empty(NetRefusal.NamedMods([detail]));

    [Fact]
    public void The_notice_names_the_server_and_only_the_mods_it_refused_for()
    {
        var text = NetRefusal.Notice("Kiln lab mylab", "This server requires mods you do not have: npcsample 0.1.0 (missing), quests-sample 2.0.0 (missing)",
            DefaultPolicyDetails, modpack: false);
        Assert.StartsWith("Kiln lab mylab refused this game.\n", text);
        Assert.Contains("npcsample 0.1.0, quests-sample 2.0.0", text);
        Assert.DoesNotContain("net-test", text);
        Assert.Contains("Kiln launcher", text);
        Assert.DoesNotContain("modpack", text);
        Assert.True(text.Split('\n').All(l => l.Length <= 90), text);
    }

    [Fact]
    public void The_notice_points_at_the_modpack_when_the_server_has_one() =>
        Assert.Contains("modpack", NetRefusal.Notice("Ember Isles", "", ["isle-icons 1.1.0 (missing)"], modpack: true));

    [Fact]
    public void The_notice_shows_the_clients_version_and_cuts_long_lists()
    {
        var details = Enumerable.Range(0, 12).Select(i => $"mod-{i:D2} 1.0.{i} (client has 0.9.0)").ToArray();
        var text = NetRefusal.Notice(null, "", details, modpack: false);
        Assert.StartsWith("The server refused this game.", text);
        Assert.Contains("mod-00 1.0.0 (you have 0.9.0)", text);
        Assert.Contains("mod-07", text);
        Assert.DoesNotContain("mod-08", text);
        Assert.Contains("and 4 more", text);
        Assert.True(text.Split('\n').All(l => l.Length <= 90), text);
    }

    [Fact]
    public void Without_named_mods_the_notice_wraps_the_reason()
    {
        var reason = "This server's mods need Kiln on your game. Join with the Kiln launcher, with an up-to-date Kiln. " +
                     "Some more words, so that the reason needs more than one line on the screen, as a long one would.";
        var text = NetRefusal.Notice("Lab", reason, [], modpack: false);
        var lines = text.Split('\n');
        Assert.Equal("Lab refused this game.", lines[0]);
        Assert.True(lines.Length >= 3 && lines.Skip(1).All(l => l.Length <= 80), text);
        Assert.Equal(reason, string.Join(" ", lines.Skip(1)));
    }

    [Fact]
    public void A_refusal_entry_carries_the_named_mods_as_missing()
    {
        var advert = new NetServerAdvert("srv-lab", "Kiln lab mylab", "0.2.0-dev", null,
            [new("net-test", "0.1.0", "kdh1:" + new string('a', 64)), new("npcsample", "0.1.0", "kdh1:" + new string('b', 64)),
             new("quests-sample", "2.0.0", "kdh1:" + new string('c', 64))], 3, false, false);
        var e = LastServer.Build("rejected", "needs mods", advert, null, NetRefusal.Missing(DefaultPolicyDetails, Lacking), DateTime.UtcNow);
        Assert.Equal(["npcsample", "quests-sample"], e["missing"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(3, e["required"]!.AsArray().Count);  // the modpack's list stays whole
    }
}
