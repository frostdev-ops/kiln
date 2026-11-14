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

    // ---- review B5: the C# mirror of KilnBootApiV1 keeps kiln_boot.h's member order ----
    private static string Header([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../../native/core/abi/services/kiln_boot.h"));

    [Fact]
    public void The_table_mirror_has_the_headers_members_in_its_order()
    {
        var text = File.ReadAllText(Header());
        var body = text[text.IndexOf("typedef struct KilnBootApiV1 {", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("} KilnBootApiV1;", StringComparison.Ordinal)];
        var native = Regex.Matches(body, @"\(KILN_CALL\s*\*\s*(\w+)\)").Select(m => m.Groups[1].Value.Replace("_", "")).ToList();
        var managed = typeof(KilnBootApi).GetFields().Where(f => f.Name is not "Size" and not "Version").Select(f => f.Name.ToLowerInvariant()).ToList();
        Assert.Equal(10, native.Count);
        Assert.Equal(native, managed);
        Assert.Equal(8 + 10 * IntPtr.Size, Marshal.SizeOf<KilnBootApi>());
        Assert.Equal(24, Marshal.SizeOf<KilnBootCommitResultV1>());
        Assert.Equal(264, Marshal.SizeOf<KilnBootStageResultV1>());
        Assert.Equal(152, Marshal.SizeOf<KilnBootOwnerStateV1>());
        Assert.Equal(648, Marshal.SizeOf<KilnBootIssueV1>());
    }
}
