// SPDX-License-Identifier: MIT
// A manifest's "features" against the capability registry (ResolveEnvironment.Capability), and the refusal on its way
// to kiln/state/mods.json and kiln.log.
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class CapabilityResolverTests
{
    private static readonly Dictionary<string, Capability> Rows = new()
    {
        ["tick"] = new("tick", CapabilityState.Available, CapabilityReason.None, "", "sigdb"),
        ["items.fuel"] = new("items.fuel", CapabilityState.Unavailable, CapabilityReason.SymbolMissing,
            "symbol items/FuelTick: find[0]: 0 matches", "sigdb"),
        ["net.kick"] = new("net.kick", CapabilityState.Unavailable, CapabilityReason.Policy, "", "sigdb"),
        ["items.moves"] = new("items.moves", CapabilityState.Pending, CapabilityReason.Session, "no world is loaded yet", "host")
            { SessionDependent = true },
        ["anim.player_states"] = new("anim.player_states", CapabilityState.Available, CapabilityReason.None, "", "host")
            { SessionDependent = true },
    };

    private static Capability Lookup(string name) => Rows.TryGetValue(name, out var row)
        ? row
        : new Capability(name, CapabilityState.Unavailable, CapabilityReason.Unknown, "no capability of this name is registered", "");

    private static ModCandidate Mod(string id, params string[] features) =>
        new()
        {
            Directory = "/mods/" + id,
            Manifest = Manifest.Parse($$"""{"id":"{{id}}","version":"1.0.0","apiVersion":"0.2","entryAssembly":"M.dll","features":[{{string.Join(",", features.Select(f => $"\"{f}\""))}}]}"""),
        };

    private static ResolveEnvironment Env(Func<string, Capability>? capability) =>
        new(SemVersion.Parse("0.1.0-dev"), SemVersion.Parse(Kiln.Mods.ApiVersion), ManifestSide.Server, f => f == "tick",
            ResolveEnvironment.NoneLoaded) { Capability = capability };

    private static string Why(ResolveResult r, string id) => r.Rejected.Single(x => x.Candidate.Id == id).Reason;

    [Fact]
    public void An_unavailable_feature_carries_the_rows_reason_and_code()
    {
        var r = Resolver.Resolve([Mod("fuel", "tick", "items.fuel")], Env(Lookup));
        Assert.Empty(r.Order);
        var rejected = Assert.Single(r.Rejected);
        Assert.Equal(ModState.Failed, rejected.State);
        Assert.Equal("needs feature items.fuel, unavailable: symbol items/FuelTick: find[0]: 0 matches [symbol-missing]", rejected.Reason);
    }

    [Fact]
    public void An_unavailable_feature_without_text_names_only_the_code()
    {
        Assert.Equal("needs feature net.kick, unavailable [policy]", Why(Resolver.Resolve([Mod("kick", "net.kick")], Env(Lookup)), "kick"));
    }

    [Fact]
    public void A_name_nobody_registered_is_refused_as_unknown()
    {
        Assert.Equal("needs feature unknown, unavailable: no capability of this name is registered [unknown]",
            Why(Resolver.Resolve([Mod("odd", "unknown")], Env(Lookup)), "odd"));
        Assert.Equal("needs feature kiln.missing, unavailable: no capability of this name is registered [unknown]",
            Why(Resolver.Resolve([Mod("odd", "kiln.missing")], Env(Lookup)), "odd"));
    }

    [Theory]
    [InlineData("items.result_peek")]
    [InlineData("anim.player_moves")]
    [InlineData("no.such")]
    public void An_unknown_service_row_explains_registration_order_and_the_runtime_check(string name)
    {
        var r = Resolver.Resolve([Mod("late", name)], Env(Lookup));
        Assert.Empty(r.Order);
        var reason = Why(r, "late");
        Assert.Contains("[unknown]", reason);
        Assert.Contains("service registers", reason);
        Assert.Contains("only after manifests are checked", reason);
        Assert.Contains($"Check Capabilities.Is(\"{name}\") at run time instead", reason);
    }

    [Fact]
    public void A_session_row_cannot_be_required_pending_or_not()
    {
        var r = Resolver.Resolve([Mod("moves", "items.moves"), Mod("anim", "anim.player_states")], Env(Lookup));
        Assert.Empty(r.Order);
        Assert.Equal("requires items.moves, which depends on the session (pending: no world is loaded yet); a manifest may only " +
                     "require capabilities that are fixed at boot. Check Capabilities.Is(\"items.moves\") at run time instead",
            Why(r, "moves"));
        Assert.Equal("requires anim.player_states, which depends on the session (available); a manifest may only " +
                     "require capabilities that are fixed at boot. Check Capabilities.Is(\"anim.player_states\") at run time instead",
            Why(r, "anim"));
    }

    [Fact]
    public void Several_failures_make_one_refusal_in_manifest_order()
    {
        var r = Resolver.Resolve([Mod("many", "items.moves", "tick", "items.fuel", "net.kick")], Env(Lookup));
        var rejected = Assert.Single(r.Rejected);
        Assert.Equal(ModState.Failed, rejected.State);
        Assert.Equal(
            "requires items.moves, which depends on the session (pending: no world is loaded yet); a manifest may only require " +
            "capabilities that are fixed at boot. Check Capabilities.Is(\"items.moves\") at run time instead; " +
            "needs feature items.fuel, unavailable: symbol items/FuelTick: find[0]: 0 matches [symbol-missing]; " +
            "needs feature net.kick, unavailable [policy]", rejected.Reason);
    }

    [Fact]
    public void Fixed_available_rows_pass_and_HasFeature_is_not_asked()
    {
        var env = Env(Lookup) with { HasFeature = _ => throw new InvalidOperationException("HasFeature asked") };
        var r = Resolver.Resolve([Mod("ok", "tick"), Mod("none")], env);
        Assert.Equal(["none", "ok"], r.Order.Select(c => c.Id));
        Assert.Empty(r.Rejected);
    }

    [Fact]
    public void Without_the_registry_the_text_is_todays()
    {
        var r = Resolver.Resolve([Mod("fuel", "tick", "items.fuel", "net.kick")], Env(null));
        Assert.Equal("needs features items.fuel, net.kick, unavailable on this game build", Why(r, "fuel"));
        Assert.Equal("needs feature items.fuel, unavailable on this game build",
            Why(Resolver.Resolve([Mod("one", "items.fuel")], Env(null)), "one"));
    }
}

/// <summary>The refusal reaches kiln/state/mods.json and kiln.log through the code that writes them.</summary>
public sealed class CapabilityRefusalTests : ModsFixture
{
    public CapabilityRefusalTests() => Capabilities.Reset();

    public override void Dispose()
    {
        Capabilities.Reset();
        base.Dispose();
    }

    [Fact]
    public void A_refused_feature_is_in_mods_json_and_the_log_with_its_reason()
    {
        var host = new FakeHost();  // also the backend: no kiln.caps, so the registry keeps its rows in memory
        Capabilities.Report("items.fuel", CapabilityState.Unavailable, CapabilityReason.SymbolMissing,
            "symbol items/FuelTick: find[0]: 0 matches");
        WriteManifest("fuel-user", """{"id":"fuel-user","version":"1.0.0","apiVersion":"0.2","features":["tick","items.fuel"]}""");
        var json = Path.Combine(Root, "state", "mods.json");
        using var mods = NewManager(host, new ModManagerOptions
        {
            KilnVersion = host.Version, HasFeature = host.HasFeature, Capability = Capabilities.Get,
            StateFile = Path.Combine(LogsDir, "mods.txt"), ModsJsonFile = json,
        });
        mods.LoadAll();
        const string reason = "needs feature items.fuel, unavailable: symbol items/FuelTick: find[0]: 0 matches [symbol-missing]";
        var entry = JsonNode.Parse(File.ReadAllText(json))!["mods"]!.AsArray().Single(m => (string?)m!["id"] == "fuel-user")!;
        Assert.Equal("failed", (string?)entry["state"]);
        Assert.Equal(reason, (string?)entry["reason"]);
        Assert.True(host.Has($"mod fuel-user 1.0.0 not loaded: {reason}"), host.Dump());
        Assert.Equal(ModState.Failed, Kiln.Mods.Get("fuel-user")!.State);
    }

    [Fact]
    public void Without_the_registry_option_the_manager_keeps_todays_text()
    {
        var host = new FakeHost();
        WriteManifest("odd", """{"id":"odd","version":"1.0.0","apiVersion":"0.2","features":["no.such.feature"]}""");
        var json = Path.Combine(Root, "state", "mods.json");
        using var mods = NewManager(host, new ModManagerOptions
        {
            KilnVersion = host.Version, HasFeature = host.HasFeature, ModsJsonFile = json,
        });
        mods.LoadAll();
        var entry = JsonNode.Parse(File.ReadAllText(json))!["mods"]!.AsArray().Single(m => (string?)m!["id"] == "odd")!;
        Assert.Equal("needs feature no.such.feature, unavailable on this game build", (string?)entry["reason"]);
    }
}
