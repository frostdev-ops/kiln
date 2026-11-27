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
//   - dev.32 review: kiln.settings accepts enemyDamageFactor 9 under the registry's bound 10 (and refuses 11; enemyHealthFactor
//     keeps its own 4);
//   - dev.32 review: a pass-through hook of the altar check after Kiln's (as More Flame Altars places its own) leaves Kiln's
//     answer 20 and makes kiln.content report a conflict line in registry.json.
using Kiln;

namespace RegistryWorldTest;

public sealed class RegistryWorldTestMod : Mod
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
    private unsafe void LimitsChecks()
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

    // dev.32 review (coordinator decision 1): Kiln's own settings write path takes the registry's raised bound (10) in place of
    // its table's 5, and keeps the table's range where no mod set one (enemyHealthFactor: 4)
    private void SettingsChecks()
    {
        var max = Kiln.Settings.Info("enemyDamageFactor").Max;
        Check(Math.Abs(max - 10) < 1e-6, $"kiln.settings' range of enemyDamageFactor ends at the registry's 10 ({max}; its table says 5)");
        var raised = Kiln.Settings.TrySet("enemyDamageFactor", 9.0, out _, out var why, resend: false);
        Check(raised, $"kiln.settings accepts enemyDamageFactor 9 under the registry's bound 10{(raised ? "" : ": " + why)}");
        var over = Kiln.Settings.TrySet("enemyDamageFactor", 11.0, out _, out var why2, resend: false);
        Check(!over && why2.Contains("allowed 0.25 to 10"), $"kiln.settings refuses 11 above the registry's bound: {why2}");
        var hp = Kiln.Settings.TrySet("enemyHealthFactor", 5.0, out _, out var why3, resend: false);
        Check(!hp && why3.Contains("allowed 0.25 to 4"), $"control: enemyHealthFactor keeps kiln.settings' own range (no mod set its bound): {why3}");
        Kiln.Settings.TrySet("enemyDamageFactor", 8.0, out _, out _, resend: false);  // the world's own value again
    }

    // dev.32 review (coordinator decision 2): another mod's own hook of the altar check, as More Flame Altars' AltarLimitHook
    // places it in its OnLoad (Hooks.Detour, after the stage): a pass-through link here. Kiln's link runs first and answers by
    // itself (still 20); kiln.content warns and writes registry.json again with a conflict line (the lab script checks the file).
    private static HookHandle? _passThrough;
    private void OtherHookCheck()
    {
        try
        {
            _passThrough = Hooks.Detour("limits/AltarLimit", call => call.CallOriginal());
            Log.Info("registry-world-test: a pass-through hook of the game's altar check is in (as More Flame Altars' own)");
            LimitsChecks();  // Kiln's link is first in the chain: its answer stands
        }
        catch (Exception e)
        {
            Check(false, $"the pass-through hook of the altar check could not go in: {e.Message}");
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
            if (Kiln.Settings.Status.Ready) SettingsChecks();
            OtherHookCheck();
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
        // kiln.content looks at the other hooks from its tick at most every 2 s: give it a few before the summary ends the lab run
        for (var i = 0; i < 300; i++) await Context.Scheduler.NextTick();
        Log.Info($"registry-world-test: SUMMARY {_pass} passed, {_fail} failed");
    }
}
