// SPDX-License-Identifier: MIT
// tools/impact lab check (player-free). kiln.assets overrides the Fireball explosion program 80a2448f (id 0x7a120ee8)
// at boot with a tools/impact clone (impact/cook.json) that also spawns Enemy_Vukah_Melee at the blast. This mod
// spawns Explosion_Player_Spell_Fireball_1 away from everything and reports whether that Vukah appears: proof that a
// tools/impact-built program is loaded by the game and that its code runs. It also reports whether the game still
// resolves the program (getProgram detour) and the ImpactRegistry count.
using System.Collections.Concurrent;
using System.Diagnostics;
using Kiln;

namespace ImpactLab;

public sealed class ImpactLabMod : Mod
{
    private const uint ExplosionProgramId = 0x7a120ee8;
    private static readonly Guid Vukah = Guid.Parse("d5d05217-52dc-467a-9192-26b4f661d8d9");
    private readonly ConcurrentDictionary<uint, int> _resolved = new();
    private readonly ConcurrentQueue<EntityJoinLevelEvent> _joins = new();
    private readonly List<HookHandle> _hooks = new();
    private readonly List<EntityId> _cleanup = new();
    private nuint _registry;
    private IDisposable? _watch;

    public override void OnLoad()
    {
        _watch = Gameplay.WatchEntities(EntityCategories.Other | EntityCategories.Creature);
        _ = Run();
    }

    public override void OnUnload()
    {
        foreach (var h in _hooks) try { h.Dispose(); } catch { }
        foreach (var e in _cleanup) try { Spawn.Destroy(e); } catch { }
        _watch?.Dispose();
    }

    [Subscribe] private void OnJoin(EntityJoinLevelEvent e)
    {
        if (e.Template == Vukah || e.TemplateName.StartsWith("Explosion_Player_Spell_Fireball")) _joins.Enqueue(e);
    }

    private async Task Steps(int n) { for (int i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private async Task Run()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            // getProgram (ImpactRegistry lookup by ProgramId): the signature abilities-research found unique
            var getProgram = Symbols.Find("48 89 5C 24 08 48 89 7C 24 10 83 B9 BC 00 00 00");
            I($"getProgram at +0x{(getProgram == 0 ? 0 : getProgram - Symbols.ImageBase):x}");
            if (getProgram != 0)
                _hooks.Add(Hooks.Detour(getProgram, call =>
                {
                    _registry = call[0];
                    _resolved.AddOrUpdate((uint)call[1], 1, (_, n) => n + 1);
                    return call.CallOriginal();
                }));
            await Steps(30);
            if (_registry != 0 && Memory.TryRead<int>(_registry + 0xbc, out var count))
                I($"ImpactRegistry count(+0xbc) = {count}");
            while (_joins.TryDequeue(out _)) { }

            WorldPosition? anchor = null;
            foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk" })
            {
                var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
                if (!id.IsNone && Spawn.Inspect(id) is { } inf) { anchor = inf.Position; break; }
            }
            if (anchor is not { } a) { I("FAIL no anchor entity found"); return; }
            var at = new WorldPosition(a.X + 60, a.Y + 1, a.Z + 60);
            var expl = await Spawn.Entity("Explosion_Player_Spell_Fireball_1", at, 0,
                new SpawnOptions { Tag = "impact-lab", Flags = SpawnFlags.Transient });
            _cleanup.Add(expl);
            I($"spawned Explosion_Player_Spell_Fireball_1 {expl} at {at}");
            EntityJoinLevelEvent? marker = null;
            for (int i = 0; i < 40 && marker is null; i++)
            {
                await Steps(1);
                while (_joins.TryDequeue(out var j))
                {
                    I($"JOIN {j.Entity} {j.TemplateName} parent {j.Parent} byKiln {j.ByKiln} at {j.Position}");
                    if (j.Template == Vukah && !j.ByKiln) { marker = j; _cleanup.Add(j.Entity); }
                }
            }
            _resolved.TryGetValue(ExplosionProgramId, out var n);
            I($"program 0x{ExplosionProgramId:x8} resolved {n} time(s) since the hook");
            if (marker is not null)
                I($"PASS override program ran: its spawnEntity made Enemy_Vukah_Melee {marker.Entity} (parent {marker.Parent}) " +
                  $"for explosion {expl}");
            else
                I("FAIL no Enemy_Vukah_Melee from the explosion within 40 steps");
        }
        catch (Exception e) { Log.Error($"impact-lab: threw {e}"); }
        finally
        {
            await Steps(5);
            foreach (var e in _cleanup) try { Spawn.Destroy(e); } catch { }
            I("done");
        }
    }

    private void I(string s) => Log.Info($"impact-lab: {s}");
}
