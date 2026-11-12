// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class ResolverTests
{
    private static ModCandidate Mod(string id, string version = "1.0.0", string extra = "") =>
        new() { Directory = "/mods/" + id, Manifest = Manifest.Parse($$"""{"id":"{{id}}","version":"{{version}}","apiVersion":"0.2","entryAssembly":"M.dll"{{extra}}}""") };

    private static ResolveResult Resolve(ManifestSide side = ManifestSide.Server, IReadOnlyDictionary<string, Manifest>? loaded = null,
        params ModCandidate[] mods) =>
        Resolver.Resolve(mods, new ResolveEnvironment(SemVersion.Parse("0.1.0-dev"), SemVersion.Parse(Kiln.Mods.RuntimeApiVersion), side,
            f => f != "missing.feature", loaded ?? ResolveEnvironment.NoneLoaded));

    private static ResolveResult Resolve(params ModCandidate[] mods) => Resolve(ManifestSide.Server, null, mods);

    private static string[] Order(ResolveResult r) => r.Order.Select(c => c.Id).ToArray();
    private static string Why(ResolveResult r, string id) => r.Rejected.Single(x => x.Candidate.Id == id).Reason;

    [Fact]
    public void Orders_dependencies_first_then_by_id()
    {
        var r = Resolve(Mod("c", extra: ""","dependencies":{"b":"*"}"""), Mod("b", extra: ""","dependencies":{"a":"^1"}"""), Mod("a"), Mod("z"), Mod("m"));
        Assert.Equal(["a", "b", "c", "m", "z"], Order(r));
        Assert.Empty(r.Rejected);
    }

    [Fact]
    public void Missing_dependency_fails_only_that_mod()
    {
        var r = Resolve(Mod("needy", extra: ""","dependencies":{"gone":"^1.0"}"""), Mod("fine"));
        Assert.Equal(["fine"], Order(r));
        Assert.Equal("missing dependency gone ^1.0", Why(r, "needy"));
    }

    [Fact]
    public void Version_mismatch_names_both_sides()
    {
        var r = Resolve(Mod("lib", "1.2.0"), Mod("app", extra: ""","dependencies":{"lib":"[2.0,3.0)"}"""));
        Assert.Equal(["lib"], Order(r));
        Assert.Equal("needs lib [2.0,3.0), found lib 1.2.0", Why(r, "app"));
    }

    [Fact]
    public void Optional_dependencies_order_when_present_and_are_skipped_when_absent()
    {
        const string deps = ""","dependencies":{"zlib":{"version":"^1","optional":true},"absent":{"optional":true}}""";
        Assert.Equal(["zlib", "app"], Order(Resolve(Mod("app", extra: deps), Mod("zlib"))));
        Assert.Equal(["app"], Order(Resolve(Mod("app", extra: deps))));
        var wrong = Resolve(Mod("app", extra: deps), Mod("zlib", "2.0.0"));
        Assert.Contains("optional dependency must match", Why(wrong, "app"));
    }

    [Fact]
    public void Cycles_fail_every_member_and_their_dependents_but_not_bystanders()
    {
        var r = Resolve(Mod("a", extra: ""","dependencies":{"b":"*"}"""), Mod("b", extra: ""","dependencies":{"c":"*"}"""),
            Mod("c", extra: ""","dependencies":{"a":"*"}"""), Mod("after", extra: ""","dependencies":{"a":"*"}"""), Mod("free"));
        Assert.Equal(["free"], Order(r));
        Assert.Equal("dependency cycle: a -> b -> c -> a (each must load after the next)", Why(r, "a"));
        Assert.StartsWith("dependency cycle:", Why(r, "b"));
        Assert.StartsWith("dependency a * did not load (failed: dependency cycle", Why(r, "after"));
    }

    [Fact]
    public void LoadBefore_and_loadAfter_order_without_requiring()
    {
        var r = Resolve(Mod("a", extra: ""","loadAfter":["b","nothere"]"""), Mod("b"), Mod("c", extra: ""","loadBefore":["b"]"""));
        Assert.Equal(["c", "b", "a"], Order(r));
        var soft = Resolve(Mod("x", extra: ""","loadAfter":["y"]"""), Mod("y", extra: ""","loadAfter":["x"]"""));
        Assert.Empty(soft.Order);
        Assert.StartsWith("dependency cycle: x -> y -> x", Why(soft, "x"));
    }

    [Fact]
    public void Incompatible_fails_the_declaring_mod()
    {
        var r = Resolve(Mod("lib", "1.2.0"), Mod("rival", extra: ""","incompatible":{"lib":"^1"}"""), Mod("ok", extra: ""","incompatible":{"lib":"^2"}"""));
        Assert.Equal(["lib", "ok"], Order(r));
        Assert.Equal("incompatible with lib 1.2.0 (declared in its incompatible list)", Why(r, "rival"));
    }

    [Fact]
    public void Side_filtering_skips_other_side_mods()
    {
        ModCandidate[] mods = [Mod("s"), Mod("c", extra: ""","side":"client" """), Mod("b", extra: ""","side":"both" """)];
        var server = Resolve(ManifestSide.Server, null, mods);
        Assert.Equal(["b", "s"], Order(server));
        Assert.Equal(ModState.Skipped, server.Rejected.Single().State);
        var client = Resolve(ManifestSide.Client, null, mods);
        Assert.Equal(["b", "c"], Order(client));
        Assert.Contains("side 'server'", Why(client, "s"));
    }

    [Fact]
    public void A_dependency_on_a_skipped_mod_says_so()
    {
        var r = Resolve(Mod("c", extra: ""","side":"client" """), Mod("app", extra: ""","dependencies":{"c":"*"}"""));
        Assert.StartsWith("dependency c * did not load (skipped: side 'client'", Why(r, "app"));
    }

    [Fact]
    public void Duplicates_api_features_and_kiln_version()
    {
        var dup = new ModCandidate { Directory = "/mods/copy", Manifest = Mod("a").Manifest };
        var r = Resolve(Mod("a"), dup,
            new ModCandidate { Directory = "/mods/api", Manifest = Manifest.Parse("""{"id":"api","version":"1","apiVersion":"1.0","entryAssembly":"M.dll"}""") },
            Mod("feat", extra: ""","features":["tick","missing.feature"]"""),
            Mod("newkiln", extra: ""","dependencies":{"kiln":">=0.5"}"""),
            Mod("devkiln", extra: ""","dependencies":{"kiln":"^0.1"}"""),
            new ModCandidate { Directory = "/mods/broken", Error = "kiln.mod.json: id 'Bad' must match" });
        Assert.Equal(["a", "devkiln"], Order(r));
        Assert.Contains("duplicate mod id 'a' (also in folder a)", r.Rejected.Single(x => x.Candidate.Directory == "/mods/copy").Reason);
        Assert.Equal($"needs Kiln API 1.0; this Kiln provides API {Mods.RuntimeApiVersion}", Why(r, "api"));  // the running API, whatever the release
        Assert.Equal("needs feature missing.feature, unavailable on this game build", Why(r, "feat"));
        Assert.Equal("needs Kiln >=0.5; this is Kiln 0.1.0-dev", Why(r, "newkiln"));
        Assert.Equal("kiln.mod.json: id 'Bad' must match", Why(r, "broken"));
    }

    [Fact]
    public void A_bare_api_version_needs_that_api_or_later()
    {
        // an example of the bare-version rule (VersionRange.AtLeastSameMajor), not a statement of any release's number:
        // a mod declaring "0.3" is refused by a runtime with API 0.2.0 and loads on one with API 0.3.0
        static ResolveResult On(string api, ModCandidate mod) =>
            Resolver.Resolve([mod], new ResolveEnvironment(SemVersion.Parse("0.2.0-dev.25"), SemVersion.Parse(api), ManifestSide.Server,
                _ => true, ResolveEnvironment.NoneLoaded));
        ModCandidate Next() => new() { Directory = "/mods/next",
            Manifest = Manifest.Parse("""{"id":"next","version":"1.0.0","apiVersion":"0.3","entryAssembly":"M.dll"}""") };

        var old = On("0.2.0", Next());
        Assert.Empty(old.Order);
        Assert.Equal("needs Kiln API 0.3; this Kiln provides API 0.2.0", Why(old, "next"));
        Assert.Equal(ModState.Failed, old.Rejected.Single().State);

        var now = On("0.3.0", Next());
        Assert.Equal(["next"], Order(now));
        Assert.Empty(now.Rejected);
        // and an older mod keeps loading on the newer minor
        Assert.Equal(["a"], Order(On("0.3.0", Mod("a"))));
    }

    [Fact]
    public void Resolves_against_mods_already_running()
    {
        var loaded = new Dictionary<string, Manifest> { ["lib"] = Mod("lib", "1.5.0").Manifest! };
        var r = Resolve(ManifestSide.Server, loaded, Mod("app", extra: ""","dependencies":{"lib":"^1.2"}"""), Mod("lib"));
        Assert.Equal(["app"], Order(r));
        Assert.Equal("mod lib is already loaded", Why(r, "lib"));
    }
}
