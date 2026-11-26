// SPDX-License-Identifier: MIT
// Lab test of the world-rule registries (dev.32 registry-r3b): what the game reads after this mod's registry section
// (content/registry.json) at start. Expects the lab's enshrouded_server.json at gameSettingsPreset Custom with
// enemyDamageFactor 8 and enemyHealthFactor 8 (the lab script sets them).
//   - the served world scene has no no-build zones (the registry's clear, written by the boot back-end) and the game's 14
//     playable areas;
//   - the served settings resource has enemyDamageFactor's highest value 10 (the registry's) and enemyHealthFactor's 4 (the game's);
//   - the settings the game started with: enemyDamageFactor 8 (inside the raised bound: the game read the registry's bound when it
//     read the settings at start) and enemyHealthFactor 4 (clamped to the game's bound, the control);
//   - the game's own altar check and gifting distance getter (sigdb limits/AltarLimit, limits/GiftRange), called: Kiln's hooks,
//     installed at start for limits.maxAltars 20 and limits.giftingRange 500, answer 20 (over a scratch balancing table whose
//     levels hold 255) and 500 m squared.
using Kiln;

namespace RegistryWorldTest;

public sealed unsafe class RegistryWorldTestMod : Mod
{
    private static readonly Guid Scene = Guid.Parse("509feadb-4c60-425f-9c7c-deeefd9b6920");
    private static readonly Guid Presets = Guid.Parse("46e14df6-fb8f-4f66-9463-e23b9e19e48e");
    private int _pass, _fail;

    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;
        _ = Run();
    }

    private void Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"registry-world-test: PASS {what}"); }
        else { _fail++; Log.Warn($"registry-world-test: FAIL {what}"); }
    }

    // the game's functions, called as the game calls them: Kiln's hooks answer the registry's limits
    private void LimitsChecks()
    {
        if (Symbols.TryGet("limits/AltarLimit", out var altar) && Constants.TryGet("limits/altars.table", out var off) && off > 0 && off < 0x10000)
        {
            var table = new byte[off + 8 + 32];
            BitConverter.TryWriteBytes(table.AsSpan((int)off), 8);
            BitConverter.TryWriteBytes(table.AsSpan((int)off + 4), 32u);
            table.AsSpan((int)off + 8, 32).Fill(255);
            var registry = new byte[0x40];
            fixed (byte* t = table)
            fixed (byte* r = registry)
            {
                *(nint*)(r + 0x20) = (nint)t;
                var answer = ((delegate* unmanaged<byte*, uint, uint>)altar)(r, 0);
                Check(answer == 20, $"limits.maxAltars 20: the game's altar check answers {answer} over a table of 255 (the game's own code says 16)");
            }
        }
        else
        {
            Check(false, "the game's altar check was not found on this build (limits/AltarLimit, limits/altars.table)");
        }
        if (Symbols.TryGet("limits/GiftRange", out var gift))
        {
            var squared = ((delegate* unmanaged<float>)gift)();
            Check(Math.Abs(Math.Sqrt(squared) - 500) < 1e-3, $"limits.giftingRange 500: the game's gifting getter answers {squared} ({Math.Sqrt(squared):g} m; the game's own 50 m)");
        }
        else
        {
            Check(false, "the game's gifting distance getter was not found on this build (limits/GiftRange)");
        }
    }

    private async Task Run()
    {
        try
        {
            for (var i = 0; i < 600 && !Kiln.Settings.Status.Ready; i++) await Context.Scheduler.NextTick();
            var scene = ReflectedResources.Get(Scene, "keen::SceneResource");
            Check(scene is { Exists: true }, "the world scene is served");
            if (scene is { Exists: true })
            {
                var zones = scene.Count("noBuildZones");
                var areas = scene.Count("resetPlayersOutOfBounds.playableAreas");
                Check(zones == 0, $"world.noBuildZones cleared by the registry: the served scene has {zones} no-build zones");
                Check(areas == 14, $"world.playableAreas untouched: {areas} playable areas (the game's 14)");
            }
            var presets = ReflectedResources.Get(Presets, "keen::GameSettingsPresetsResource");
            Check(presets is { Exists: true }, "the settings resource is served");
            if (presets is { Exists: true })
            {
                var dmg = Convert.ToDouble(presets["maxValues.enemyDamageFactor"]);
                var hp = Convert.ToDouble(presets["maxValues.enemyHealthFactor"]);
                Check(Math.Abs(dmg - 10) < 1e-6, $"difficulty.enemyDamageFactor.max is 10 in the served resource ({dmg})");
                Check(Math.Abs(hp - 4) < 1e-6, $"difficulty.enemyHealthFactor.max is the game's 4 ({hp})");
            }
            LimitsChecks();
            if (Kiln.Settings.Status.Ready)
            {
                var dmg = Convert.ToDouble(Kiln.Settings.Get("enemyDamageFactor"));
                var hp = Convert.ToDouble(Kiln.Settings.Get("enemyHealthFactor"));
                Check(Math.Abs(dmg - 8) < 1e-6, $"effect: the game started with enemyDamageFactor {dmg} (the lab's 8, inside the registry's bound 10; the game's own bound is 2.5)");
                Check(Math.Abs(hp - 4) < 1e-6, $"effect (control): the game started with enemyHealthFactor {hp} (the lab's 8 clamped to the game's 4)");
            }
            else
            {
                Check(false, "the live settings were not found (settings.ready)");
            }
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e.GetType().Name}: {e.Message}");
        }
        Log.Info($"registry-world-test: SUMMARY {_pass} passed, {_fail} failed");
    }
}
