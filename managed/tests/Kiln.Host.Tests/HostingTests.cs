// SPDX-License-Identifier: MIT
// A client that hosts a world: side "server" mods load with the hosted server world and unload when it ends
// (docs/kiln/client.md, "Mods and sides").
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.Host.Tests;

public sealed class HostingTests : ModsFixture
{
    private ModManagerOptions Options(FakeHost host, ManifestSide side = ManifestSide.Client, bool hosted = true) => new()
    {
        Side = side, HostedServerMods = hosted, KilnVersion = host.Version, HasFeature = host.HasFeature,
        StateFile = Path.Combine(LogsDir, "mods.txt"), ModsJsonFile = Path.Combine(Root, "state", "mods.json"),
        TestResultsDirectory = Path.Combine(LogsDir, "tests"),
    };

    private void SetManifest(string folder, string key, JsonNode? value)
    {
        var file = Path.Combine(ModsDir, folder, Manifest.FileName);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json[key] = value;
        File.WriteAllText(file, json.ToJsonString());
    }

    private static (ModState, string?) StateOf(string id) => Kiln.Mods.Get(id) is { } e ? (e.State, e.Error) : (ModState.Pending, "unknown");

    private string ModsJson() => File.ReadAllText(Path.Combine(Root, "state", "mods.json"));

    [Fact]
    public void Server_mods_load_while_hosting_and_unload_when_the_world_ends()
    {
        InstallHello();  // side "server"
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host));
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        Assert.Equal((ModState.Skipped, ModManager.HostedReason), StateOf("hello"));
        Assert.True(host.Has("mod hello 0.1.0 skipped: side 'server': loads while this client hosts a world"), host.Dump());

        HostOnce(mods, host);
        Assert.False(mods.Hosting);
        Assert.Empty(mods.Mods);
        Assert.Equal((ModState.Skipped, ModManager.HostedReason), StateOf("hello"));
        Assert.Contains("\"state\": \"skipped\"", ModsJson());
        Assert.True(host.Has("mod hello unloaded (the hosted world ended)"), host.Dump());
        Assert.True(host.Has("goodbye"));

        // the next hosted world gets a new instance; the old one is collectable
        HostOnce(mods, host);
        Assert.Equal(0, mods.CollectAndCountLeaks(out var leaked));
        Assert.Empty(leaked);
        Assert.Equal(2, host.Lines.Count(l => l.Contains("hosting: 1 of 1 server-side mod folder(s) loaded for the hosted world", StringComparison.Ordinal)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HostOnce(ModManager mods, FakeHost host)
    {
        Assert.Equal(["hello"], mods.StartHosting());
        Assert.True(mods.Hosting);
        Assert.Equal((ModState.Loaded, null), StateOf("hello"));
        Run(mods, () => false, max: 3, sleepMs: 0);
        Assert.Equal(["hello"], mods.StopHosting("test"));
    }

    [Fact]
    public void A_both_mod_that_needs_a_server_mod_comes_and_goes_with_it()
    {
        InstallLibSet();  // sample-consumer and sample-tests depend on sample-lib
        SetManifest("sample-lib", "side", "server");
        SetManifest("sample-consumer", "side", "both");
        SetManifest("sample-tests", "side", "both");
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host));
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        Assert.Equal(ModState.Failed, StateOf("sample-consumer").Item1);
        Assert.StartsWith("dependency sample-lib", StateOf("sample-consumer").Item2);

        var loaded = mods.StartHosting();
        Assert.Equal(["sample-lib", "sample-consumer", "sample-tests"], loaded);
        Assert.True(host.Has("\"hello sample-consumer, from sample-lib 1.2.0\""), host.Dump());
        Assert.True(host.Has("(with 2 mod(s) that need them)"));

        var gone = mods.StopHosting("test");
        Assert.Equal(["sample-tests", "sample-consumer", "sample-lib"], gone);  // dependents first
        Assert.Equal((ModState.Skipped, ModManager.HostedReason), StateOf("sample-lib"));
        Assert.Equal(ModState.Failed, StateOf("sample-consumer").Item1);
        Assert.Contains("unloaded with the hosted world", StateOf("sample-consumer").Item2);

        Assert.Equal(["sample-lib", "sample-consumer", "sample-tests"], mods.StartHosting());
        mods.StopHosting("test");
    }

    [Fact]
    public void Client_and_both_mods_stay_loaded_across_hosted_worlds()
    {
        InstallLibSet();
        SetManifest("sample-lib", "side", "both");
        SetManifest("sample-consumer", "side", "client");
        SetManifest("sample-tests", "side", "server");
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host));
        mods.LoadAll();
        Assert.Equal(["sample-lib", "sample-consumer"], mods.Mods.Select(m => m.Id));
        var lib = mods.Find("sample-lib");
        Assert.Equal(["sample-tests"], mods.StartHosting());
        Assert.Equal(["sample-tests"], mods.StopHosting("test"));
        Assert.Equal(["sample-lib", "sample-consumer"], mods.Mods.Select(m => m.Id));
        Assert.Same(lib, mods.Find("sample-lib"));  // the same instance: no reload
    }

    [Fact]
    public void Loads_and_reloads_follow_the_hosting_state()
    {
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host));
        mods.LoadAll();
        InstallHello();
        var menu = mods.Execute(ModOperation.Load, "hello");
        Assert.False(menu.Ok);
        Assert.Equal(ModManager.HostedReason, menu.Message);
        mods.StartHosting();  // picks up the folder loaded by hand before
        Assert.True(mods.Find("hello") is not null, host.Dump());
        var r = mods.Execute(ModOperation.Reload, "hello");
        Assert.True(r.Ok, r.Message);
        var u = mods.Execute(ModOperation.Unload, "hello");
        Assert.True(u.Ok, u.Message);
        Assert.Empty(mods.StopHosting("test"));
        Assert.Equal((ModState.Skipped, ModManager.HostedReason), StateOf("hello"));  // back for the next world
        Assert.Equal(["hello"], mods.StartHosting());
        mods.StopHosting("test");
    }

    [Fact]
    public void Kiln_json_can_keep_server_mods_off_on_a_client()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host, hosted: false));
        mods.LoadAll();
        Assert.Equal((ModState.Skipped, ModManager.HostedOffReason), StateOf("hello"));
        Assert.Empty(mods.StartHosting());
        Assert.Empty(mods.Mods);
        Assert.Empty(mods.StopHosting("test"));
        Assert.Equal((ModState.Skipped, ModManager.HostedOffReason), StateOf("hello"));
    }

    [Fact]
    public void A_dedicated_server_never_enters_hosting()
    {
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host, side: ManifestSide.Server));
        mods.LoadAll();
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
        Assert.Empty(mods.StartHosting());
        Assert.False(mods.Hosting);
        Assert.Empty(mods.StopHosting("test"));
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
    }

    [Fact]
    public void Profile_disabled_server_mods_stay_skipped_when_hosting()
    {
        InstallHello();
        File.WriteAllText(Path.Combine(ModsDir, "hello", ".disabled"), "");
        var host = new FakeHost();
        using var mods = NewManager(host, Options(host));
        mods.LoadAll();
        Assert.Equal((ModState.Skipped, "disabled by a .disabled file"), StateOf("hello"));
        Assert.Empty(mods.StartHosting());
        mods.StopHosting("test");
        Assert.Equal((ModState.Skipped, "disabled by a .disabled file"), StateOf("hello"));
    }

    [Theory]
    [InlineData("server", false, false)]
    [InlineData("server", true, true)]
    [InlineData("client", true, true)]
    [InlineData("both", true, true)]
    public void Resolver_accepts_server_mods_on_a_hosting_client(string side, bool hosting, bool accepted)
    {
        var dir = WriteManifest("m", $$"""{"id":"m","version":"1.0.0","apiVersion":"0.2","entryAssembly":"M.dll","side":"{{side}}"}""");
        var env = new ResolveEnvironment(SemVersion.Parse("0.1.0-dev"), SemVersion.Parse(Kiln.Mods.RuntimeApiVersion), ManifestSide.Client,
            _ => true, ResolveEnvironment.NoneLoaded) { Hosting = hosting };
        var r = Resolver.Resolve([ModCandidate.Read(dir)], env);
        Assert.Equal(accepted, r.Order.Count == 1);
        // a dedicated server ignores the flag
        var server = Resolver.Resolve([ModCandidate.Read(dir)], env with { Side = ManifestSide.Server });
        Assert.Equal(side != "client", server.Order.Count == 1);
    }
}
