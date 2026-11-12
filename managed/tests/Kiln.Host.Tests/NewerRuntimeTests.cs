// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

/// <summary>A mod built against a newer Kiln than the runtime: named before it loads (its kilnBuild stamp), and said plainly when
/// it fails on a Kiln member the runtime lacks.</summary>
public sealed class NewerRuntimeTests : ModsFixture
{
    private static readonly KilnBuild Old = new("0.2.0-dev+20261106.10a0d37", "2026-11-06T10:54:02Z");
    private static readonly KilnBuild New = new("0.2.0-dev+20261106.0f0a3f6", "2026-11-06T12:12:16Z");

    [Fact]
    public void Builds_order_by_version_then_commit_time_then_day()
    {
        Assert.True(New.IsNewerThan(Old));
        Assert.False(Old.IsNewerThan(New));
        Assert.False(New.IsNewerThan(New));
        // same day, no time on one side: can't tell, so not newer
        Assert.False(new KilnBuild("0.2.0-dev+20261106.0f0a3f6", null).IsNewerThan(Old));
        Assert.True(new KilnBuild("0.2.0-dev+20261107.0f0a3f6", null).IsNewerThan(Old));
        Assert.True(new KilnBuild("0.2.1-dev+20261101.0f0a3f6", "2026-11-01T00:00:00Z").IsNewerThan(Old));
        Assert.False(new KilnBuild("0.2.0-dev+20261201.0f0a3f6", "2026-12-01T00:00:00Z").IsNewerThan(new KilnBuild("0.3.0", null)));
        Assert.Null(KilnBuild.From("not a version", null));
        Assert.Null(KilnBuild.From("0.2.0", "yesterday")!.Created);
    }

    [Fact]
    public void Kiln_Api_carries_its_build()
    {
        // managed/Directory.Build.targets stamps it from git; a tree without git has none
        if (KilnBuild.Of(typeof(Kiln.Mod).Assembly) is not { } b) return;
        Assert.StartsWith(Kiln.Mods.RuntimeApiVersion.Split('.')[0] + ".", b.Version);
        Assert.Matches(@"\+\d{8}\.[0-9a-f]{7,40}$", b.Version);
        Assert.NotNull(b.Created);
    }

    [Fact]
    public void Manifest_reads_the_stamp_and_never_refuses_for_it()
    {
        const string head = "{\"id\": \"m\", \"version\": \"1.0.0\", \"apiVersion\": \"0.2\", \"entryAssembly\": \"M.dll\", ";
        var m = Manifest.Parse(head + "\"kilnBuild\": {\"version\": \"0.2.0-dev+20261106.0f0a3f6\", \"created\": \"2026-11-06T12:12:16Z\"}}");
        Assert.Equal(New, m.BuiltWith);
        Assert.Empty(m.Warnings);
        var bad = Manifest.Parse(head + "\"kilnBuild\": \"0.2.0\"}");
        Assert.Null(bad.BuiltWith);
        Assert.Contains(bad.Warnings, w => w.StartsWith("kilnBuild must be", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Field", "Kiln.StatusLook.Fire")]
    [InlineData("Method", "Kiln.Blocks.Define(System.String, Kiln.VfxBlock)")]
    [InlineData("Wrapped", "Kiln.StatusLook.Fire")]
    [InlineData("Type", "Kiln.NoSuchType")]
    [InlineData("Assembly", "Kiln.NewService.dll")]
    [InlineData("OwnMethod", null)]
    [InlineData("Other", null)]
    public void Finds_the_Kiln_member_the_runtime_lacks(string kind, string? member)
    {
        Exception e = kind switch
        {
            "Field" => new MissingFieldException("Kiln.StatusLook", "Fire"),
            "Method" => new MissingMethodException("Method not found: 'Void Kiln.Blocks.Define(System.String, Kiln.VfxBlock)'."),
            "Wrapped" => new TargetInvocationException(new TypeInitializationException("M", new MissingFieldException("Kiln.StatusLook", "Fire"))),
            "Type" => Record.Exception(() => typeof(Kiln.Mod).Assembly.GetType("Kiln.NoSuchType", throwOnError: true))!,
            "Assembly" => new FileNotFoundException("Could not load file or assembly", "Kiln.NewService, Version=0.2.0.0, Culture=neutral, PublicKeyToken=null"),
            // a mod's own API that takes a Kiln parameter is not Kiln's member
            "OwnMethod" => new MissingMethodException("Method not found: 'Void OtherMod.Api.Use(Kiln.TickInfo)'."),
            _ => new InvalidOperationException("Field not found: 'Kiln.StatusLook.Fire'."),
        };
        Assert.Equal(member, NewerRuntime.MissingKilnMember(e));
    }

    private string FaultyNeedingANewerKiln()
    {
        var dir = Install("SampleFaulty", "sample-faulty");
        Directory.CreateDirectory(Path.Combine(DataDir, "sample-faulty"));
        File.WriteAllText(Path.Combine(DataDir, "sample-faulty", "missing-member"), "");
        // the stamp as Kiln.Sdk writes it: the Kiln build the mod compiled against
        var manifest = Path.Combine(dir, Manifest.FileName);
        var json = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        json["kilnBuild"] = new JsonObject { ["version"] = New.Version, ["created"] = New.Created };
        File.WriteAllText(manifest, json.ToJsonString());
        return dir;
    }

    [Fact]
    public void OnLoad_failing_on_a_missing_Kiln_member_says_update_Kiln()
    {
        FaultyNeedingANewerKiln();
        var host = new FakeHost();
        using var mods = NewManager(host, new ModManagerOptions
        {
            KilnVersion = "0.2.0-dev", KilnBuild = Old, StateFile = Path.Combine(LogsDir, "mods.txt"),
        });
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        const string said = "sample-faulty needs a newer Kiln runtime (it uses Kiln.StatusLook.Fire, which runtime 0.2.0-dev+20261106.10a0d37 " +
                            "doesn't have; it was built against 0.2.0-dev+20261106.0f0a3f6): update Kiln on the launcher's Runtime page";
        // before it loads: the stamp is newer than this runtime
        Assert.True(host.Has("Warn: mod sample-faulty 0.1.0 was built against Kiln 0.2.0-dev+20261106.0f0a3f6, newer than this runtime " +
                             "(0.2.0-dev+20261106.10a0d37): if it fails, update Kiln on the launcher's Runtime page"), host.Dump());
        Assert.True(host.Has("Error: " + said), host.Dump());
        var entry = Kiln.Mods.Get("sample-faulty")!;
        Assert.Equal(ModState.Failed, entry.State);
        Assert.Equal(said, entry.Error);
        Assert.Contains("sample-faulty 0.1.0 [failed]: " + said, File.ReadAllText(Path.Combine(LogsDir, "mods.txt")));
    }

    [Fact]
    public void Without_stamps_the_runtime_is_named_by_its_version()
    {
        FaultyNeedingANewerKiln();
        var host = new FakeHost();
        using var mods = NewManager(host, new ModManagerOptions { KilnVersion = "0.2.0-dev" });
        mods.LoadAll();
        Assert.False(host.Has("was built against Kiln"), host.Dump());  // no runtime stamp: nothing to compare
        Assert.StartsWith("sample-faulty needs a newer Kiln runtime (it uses Kiln.StatusLook.Fire, which runtime 0.2.0-dev doesn't have",
            Kiln.Mods.Get("sample-faulty")!.Error);
    }

    [Fact]
    public void A_mod_built_against_this_runtime_or_an_older_one_is_not_named()
    {
        InstallHello();  // stamped with the Kiln.Api these tests run on
        var host = new FakeHost();
        using var mods = NewManager(host, new ModManagerOptions { KilnVersion = "0.2.0-dev", KilnBuild = KilnBuild.Of(typeof(Kiln.Mod).Assembly) ?? New });
        mods.LoadAll();
        Assert.Single(mods.Mods);
        Assert.False(host.Has("was built against Kiln"), host.Dump());
    }
}
