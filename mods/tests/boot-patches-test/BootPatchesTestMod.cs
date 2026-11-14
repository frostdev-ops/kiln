// SPDX-License-Identifier: MIT
// Lab test of a boot generation's resource patches (chapter 08, S10): blocks/patches.json patches
// keen::BalancingTable.experienceNeedPerLevel before the game reads it, and this mod reads the game's own answer back
// once the world is up (Progression.Diagnostics.ExperienceForLevel, the game's experience function). expect.json says
// what each level must need in this case; the lab script writes both files.
using System.Text.Json;
using Kiln;

namespace BootPatchesTest;

public sealed class BootPatchesTestMod : Mod
{
    private int _pass, _fail;

    private void Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"boot-patches-test: PASS {what}"); }
        else { _fail++; Log.Error($"boot-patches-test: FAIL {what}"); }
    }

    // The case "onboot": the same kind staged from code, beside the blocks file's patch (another field of the same table).
    public override void OnBoot(BootContext boot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "expect.json")));
        if (doc.RootElement.GetProperty("case").GetString() != "onboot") return;
        var staged = BootDefinitions.For(boot).Define("xp-code", new ResourcePatchBlock
        {
            Resource = Guid.Parse("82706b40-61b1-4b8f-8b23-dcec6971bda1"), Type = "keen::BalancingTable",
            Set = { ["experienceNeedPerLevel[4]"] = ResourceValue.AtLeast(6000), ["experienceNeedPerLevel[1]"] = ResourceValue.Times(2) },
        });
        if (!staged.Ok) throw new InvalidOperationException(staged.Reason);
    }

    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;
        _ = Run();
    }

    private async Task Run()
    {
        var name = "?";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "expect.json")));
            name = doc.RootElement.GetProperty("case").GetString() ?? "?";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Progression.Available && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            for (var i = 0; i < 120; i++) await Context.Scheduler.NextTick();
            var state = Mods.BootState(Context.Info.Id);
            Log.Info($"boot-patches-test: INFO boot state {state.State}, definitions [{string.Join(", ", state.Definitions)}], {state.Issues.Count} issue(s)");
            Check(Capabilities.Is("mods.boot.resource_patches"), $"{name}: mods.boot.resource_patches is on ({Capabilities.Why("mods.boot.resource_patches")})");
            foreach (var level in doc.RootElement.GetProperty("levels").EnumerateObject())
            {
                var (need, _) = Progression.Diagnostics.ExperienceForLevel(int.Parse(level.Name));
                Check(need == level.Value.GetUInt32(), $"{name}: level {level.Name} needs {need} experience (expected {level.Value.GetUInt32()})");
            }
        }
        catch (Exception e)
        {
            Check(false, $"{name}: exception: {e}");
        }
        Log.Info($"boot-patches-test: DONE case={name} pass={_pass} fail={_fail}");
    }
}
