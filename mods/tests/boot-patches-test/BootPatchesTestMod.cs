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
