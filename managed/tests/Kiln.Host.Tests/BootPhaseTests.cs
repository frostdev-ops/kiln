// SPDX-License-Identifier: MIT
// Mod.OnBoot's host side (chapter 08 §4.2-4.4, S3): the boot manifest key, the transient author, the load rules after the phase
// (BOOT-LATE-MISSED, BOOT-RUNTIME-REACTIVATE, RestartRequiredBoot) and the ABI mirror of kiln.boot's table (review B5).
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

[Collection("boot generation")]  // BootGeneration is process-wide
public sealed class BootPhaseTests : ModsFixture
{
    private static int _stageResult, _commitCalls;
    private static bool _lateFault;
    private static uint _lastFinish;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Begin(ulong owner, KilnStr id, uint roles, ulong* generation) { *generation = 1; return KilnAbi.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Finish(ulong owner, ulong generation, uint accept) { _lastFinish = accept; return KilnAbi.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int AcceptEnvelope(ulong owner, ulong gen, KilnStr kind, KilnStr key, KilnStr json, KilnBootStageResultV1* result)
    { result->Ok = 1; return KilnAbi.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Publish(ulong owner, ulong gen, KilnBootCommitResultV1* result)
    { result->Status = BootPhase.StatusPublished; result->Generation = 1; return KilnAbi.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OwnerState(KilnStr id, KilnBootOwnerStateV1* result)
    { result->State = (uint)(_lateFault ? BootModState.ActivationFault : BootModState.Committed); return KilnAbi.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int NoIssues(ulong gen, KilnBootIssueV1* issues, uint capacity, uint stride, uint* required)
    { *required = 0; return KilnAbi.Ok; }

    [Fact]
    public unsafe void A_late_native_activation_fault_updates_BootState_and_refuses_runtime_loading()
    {
        var dir = WriteManifest("boot-gallery", Boot);
        var host = new FakeHost();
        using var mods = NewManager(host);
        _lateFault = false;
        var table = new KilnBootApi { Size = (uint)sizeof(KilnBootApi), Version = 1, Status = &Collecting,
            BeginOwner = &Begin, FinishOwner = &Finish, Stage = &AcceptEnvelope, Commit = &Publish,
            OwnerState = &OwnerState, Issues = &NoIssues };
        var address = (nint)(&table);
        Capabilities.Report("mods.boot", CapabilityState.Available, CapabilityReason.None);
        try
        {
            BootPhase.Run((_, _) => address, 1, mods, host.Log);
            Assert.Equal(BootModState.Committed, Mods.BootState("boot-gallery").State);
            _lateFault = true;
            Assert.Equal(BootModState.ActivationFault, Mods.BootState("boot-gallery").State);
            Assert.StartsWith("RestartRequiredBoot:", mods.BootLoadRefusal(Manifest.Read(dir), dir));
        }
        finally { Capabilities.Reset(); BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>()); }
    }

    [Theory]
    [InlineData("api")]
    [InlineData("feature")]
    [InlineData("dependency-version")]
    [InlineData("dependency-side")]
    public unsafe void An_invalid_boot_graph_closes_the_author_as_excluded_before_running_it(string problem)
    {
        var manifest = problem switch
        {
            "api" => Boot.Replace("\"apiVersion\": \"0.2\"", "\"apiVersion\": \"99.0\""),
            "feature" => Boot.Replace("\"boot\": true", "\"boot\": true, \"features\": [\"no.such.feature\"]"),
            _ => Boot.Replace("\"boot\": true", "\"boot\": true, \"dependencies\": {\"helper\": \">=2.0.0\"}"),
        };
        WriteManifest("boot-gallery", manifest);
        if (problem.StartsWith("dependency", StringComparison.Ordinal))
            WriteManifest("helper", $$"""{ "id": "helper", "version": "{{(problem == "dependency-version" ? "1.0.0" : "2.0.0")}}", "apiVersion": "0.2", "side": "{{(problem == "dependency-side" ? "client" : "server")}}" }""");
        var host = new FakeHost();
        using var mods = NewManager(host);
        _lateFault = false;
        _lastFinish = uint.MaxValue;
        var table = new KilnBootApi { Size = (uint)sizeof(KilnBootApi), Version = 1, Status = &Collecting,
            BeginOwner = &Begin, FinishOwner = &Finish, Stage = &AcceptEnvelope, Commit = &Publish,
            OwnerState = &OwnerState, Issues = &NoIssues };
        var address = (nint)(&table);
        Capabilities.Report("mods.boot", CapabilityState.Available, CapabilityReason.None);
        try
        {
            BootPhase.Run((_, _) => address, 1, mods, host.Log);
            Assert.Equal(2u, _lastFinish);  // KILN_BOOT_FINISH_DEPENDENCY, so its files stay out too
        }
        finally { Capabilities.Reset(); BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>()); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Collecting(KilnBootCommitResultV1* result)
    {
        result->Status = BootPhase.StatusCollecting;
        result->Generation = 1;
        return KilnAbi.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int RefuseEnvelope(ulong owner, ulong gen, KilnStr kind, KilnStr key, KilnStr json, KilnBootStageResultV1* result)
    {
        result->Ok = 0;
        return _stageResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int UnexpectedCommit(ulong owner, ulong gen, KilnBootCommitResultV1* result)
    {
        ++_commitCalls;
        return KilnAbi.ErrUnavailable;
    }

    [Theory]
    [InlineData(KilnAbi.ErrInvalidArgument)]
    [InlineData(KilnAbi.Ok)]
    public unsafe void A_refused_envelope_is_never_committed_as_a_file_only_generation(int stageResult)
    {
        _stageResult = stageResult;
        _commitCalls = 0;
        var table = new KilnBootApi { Size = (uint)sizeof(KilnBootApi), Version = 1,
            Status = &Collecting, Stage = &RefuseEnvelope, Commit = &UnexpectedCommit };
        var address = (nint)(&table);
        var host = new FakeHost();
        using var mods = NewManager(host);
        BootPhase.Run((_, _) => address, 1, mods, host.Log);
        Assert.True(host.Has("refused the IR envelope"), host.Dump());
        Assert.Equal(0, _commitCalls);
    }

    public BootPhaseTests() => BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>());

    public override void Dispose()
    {
        BootGeneration.Record(0, [], new Dictionary<string, IReadOnlyList<BootStaged>>());
        base.Dispose();
    }

    private const string Boot = """{ "id": "boot-gallery", "name": "g", "version": "1.0.0", "apiVersion": "0.2", "side": "both", "boot": true }""";

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("\"required\"", 2)]
    public void The_boot_key_reads_true_false_and_required(string value, int expected) =>
        Assert.Equal((ManifestBoot)expected, Manifest.Parse($$"""{ "id": "b", "version": "1.0.0", "apiVersion": "0.2", "boot": {{value}} }""").Boot);

    [Fact]
    public void Another_boot_value_is_refused_and_none_is_none()
    {
        Assert.Throws<InvalidDataException>(() => Manifest.Parse("""{ "id": "b", "version": "1.0.0", "apiVersion": "0.2", "boot": "yes" }"""));
        var m = Manifest.Parse("""{ "id": "b", "version": "1.0.0", "apiVersion": "0.2" }""");
        Assert.Equal(ManifestBoot.None, m.Boot);
        Assert.DoesNotContain(m.Warnings, w => w.Contains("boot"));
    }

    [Fact]
    public void BOOT_LATE_MISSED_an_absent_boot_mod_needs_a_restart()
    {
        var dir = WriteManifest("boot-gallery", Boot);
        BootGeneration.Record(1, [], new Dictionary<string, IReadOnlyList<BootStaged>>());
        using var mods = NewManager(new FakeHost());
        var why = mods.BootLoadRefusal(Manifest.Read(dir), dir);
        Assert.StartsWith("RestartRequiredBoot:", why);
        var report = Mods.BootState("boot-gallery");
        Assert.Equal(BootModState.MissedPhase, report.State);
        Assert.Contains(report.Issues, i => i.Code == "MissedPhase");
    }

    [Fact]
    public void BOOT_RUNTIME_REACTIVATE_an_unchanged_committed_mod_loads_again_a_changed_one_needs_a_restart()
    {
        var dir = WriteManifest("boot-gallery", Boot);
        var m = Manifest.Read(dir);
        BootGeneration.Record(1, [new BootModReport("boot-gallery", 1, BootModState.Committed, ["statuses/cold"], [])],
            new Dictionary<string, IReadOnlyList<BootStaged>> { ["boot-gallery"] = [new BootStaged("statuses", "cold", "{}")] });
        using var mods = NewManager(new FakeHost());
        mods.SetBootFingerprint("boot-gallery", ModManager.BootFingerprint(m, dir));
        Assert.Null(mods.BootLoadRefusal(m, dir));  // load
        Assert.Null(mods.BootLoadRefusal(m, dir));  // and after a runtime unload, again: no OnBoot, the same generation
        Assert.Equal(1ul, Mods.BootState("boot-gallery").Generation);
        File.WriteAllText(Path.Combine(dir, Manifest.FileName), Boot.Replace("1.0.0", "1.0.1"));
        Assert.StartsWith("RestartRequiredBoot:", mods.BootLoadRefusal(Manifest.Read(dir), dir));
    }

    [Theory]
    [InlineData(BootModState.ExcludedCallback)]
    [InlineData(BootModState.RejectedPrepare)]
    [InlineData(BootModState.ExcludedDependency)]
    public void A_boot_mod_whose_content_is_out_does_not_load_until_a_restart(BootModState state)
    {
        var dir = WriteManifest("boot-gallery", Boot);
        BootGeneration.Record(1, [new BootModReport("boot-gallery", 1, state, [], [])], new Dictionary<string, IReadOnlyList<BootStaged>>());
        using var mods = NewManager(new FakeHost());
        Assert.StartsWith("RestartRequiredBoot:", mods.BootLoadRefusal(Manifest.Read(dir), dir));
    }

    [Fact]
    public void A_plain_mod_loads_as_before()
    {
        var dir = WriteManifest("plain", """{ "id": "plain", "version": "1.0.0", "apiVersion": "0.2" }""");
        using var mods = NewManager(new FakeHost());
        Assert.Null(mods.BootLoadRefusal(Manifest.Read(dir), dir));
    }

    [Fact]
    public void The_transient_author_runs_OnBoot_once_and_closes_its_frame()
    {
        var dir = InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        var boot = new BootContext("hello", 1, BootRoles.DedicatedServer, new UnavailableGameData());
        Assert.True(mods.RunBootAuthor(Manifest.Read(dir), dir, 77, boot, out var why), why);
        Assert.False(boot.Open);
        Assert.Null(BootFrame.Current);
        Assert.Empty(mods.Mods);  // nothing of it stays loaded: the runtime instance is a separate one
        Assert.True(host.Has("OnBoot staged 0 definition(s)"));
    }

    [Fact]
    public void The_boot_roles_follow_the_side()
    {
        using var mods = NewManager(new FakeHost());
        Assert.Equal(BootRoles.DedicatedServer, mods.BootRoles(Manifest.Parse(Boot)));
        Assert.Equal((BootRoles)0, mods.BootRoles(Manifest.Parse(Boot.Replace("\"both\"", "\"client\""))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolved_boot_roles_union_the_menu_and_enabled_hosting_graphs(bool hosted)
    {
        var host = new FakeHost();
        using var mods = NewManager(host, new ModManagerOptions { Side = ManifestSide.Client, HostedServerMods = hosted,
            KilnVersion = host.Version, HasFeature = host.HasFeature });
        var candidates = new List<(Manifest Manifest, string Directory)>();
        foreach (var side in new[] { "both", "server", "client" })
            candidates.Add((Manifest.Parse(Boot.Replace("boot-gallery", side).Replace("\"both\"", $"\"{side}\"")), side));
        var roles = mods.ResolveBootRoles(candidates, out _);
        Assert.Equal(hosted ? BootRoles.HostedServer | BootRoles.Client : BootRoles.Client, roles["both"]);
        Assert.Equal(BootRoles.Client, roles["client"]);
        if (hosted) Assert.Equal(BootRoles.HostedServer, roles["server"]);
        else Assert.False(roles.ContainsKey("server"));
    }

    // ---- review B5: the C# mirror of KilnBootApiV1 keeps kiln_boot.h's member order ----
    private static string Header([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../../native/core/abi/services/kiln_boot.h"));

    [Fact]
    public unsafe void GameData_off_names_the_row_once()
    {
        _ = new FakeHost();  // also the backend: the registry keeps its rows in memory
        Capabilities.Report("mods.boot.game_data", CapabilityState.Unavailable, CapabilityReason.Offline, "no game-data snapshot matches this game build");
        try
        {
            var data = new BootGameData(null, 1);  // the row is off: the table is never touched
            var e = Assert.Throws<KilnUnavailableException>(() => data.Read("item:Material_T1_Wood_Logs"));
            Assert.Equal("mods.boot.game_data", e.Capability);
            Assert.Equal("mods.boot.game_data is not available: no game-data snapshot matches this game build", e.Message);
        }
        finally
        {
            Capabilities.Reset();
        }
    }

    [Fact]
    public void The_table_mirror_has_the_headers_members_in_its_order()
    {
        var text = File.ReadAllText(Header());
        var body = text[text.IndexOf("typedef struct KilnBootApiV1 {", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("} KilnBootApiV1;", StringComparison.Ordinal)];
        var native = Regex.Matches(body, @"\(KILN_CALL\s*\*\s*(\w+)\)").Select(m => m.Groups[1].Value.Replace("_", "")).ToList();
        var managed = typeof(KilnBootApi).GetFields().Where(f => f.Name is not "Size" and not "Version").Select(f => f.Name.ToLowerInvariant()).ToList();
        // the mirror is the table up to active_plan: what was appended after it (activation_fault, which collectors call)
        // the host does not use, and it reads no member beyond its own size
        Assert.Equal(11, native.Count);
        Assert.Equal("activationfault", native[10]);
        Assert.Equal(native.Take(10), managed);
        Assert.Equal(8 + 10 * IntPtr.Size, Marshal.SizeOf<KilnBootApi>());
        Assert.Equal(24, Marshal.SizeOf<KilnBootCommitResultV1>());
        Assert.Equal(264, Marshal.SizeOf<KilnBootStageResultV1>());
        Assert.Equal(152, Marshal.SizeOf<KilnBootOwnerStateV1>());
        Assert.Equal(648, Marshal.SizeOf<KilnBootIssueV1>());
    }
}
