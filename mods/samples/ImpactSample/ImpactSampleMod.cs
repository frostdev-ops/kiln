// SPDX-License-Identifier: MIT
// ImpactSample (docs/kiln/modding/impact.md). The programs live in impact/ (sources and a recipe) and are cooked into
// assets/cooked/impact/ by build-assets.sh; kiln.assets serves them and kiln.abilities registers them at world start.
// This part:
//   - registers the managed Kiln native impactsample.Ping (declared in impact/natives.json);
//   - logs every ImpactSignalEvent the sample's programs post (kiln.Signal tags 0x5A11, 0xC4A1, 0x4E01);
//   - runs a player-free demo once the world runs: signal_explosion (kiln.Signal + Ping on spawn) far from
//     everything, and chain_explosion 4 m from a Vukah it spawns (the chain code runs right after the empty
//     collision loop, so no player is needed). PASS/FAIL lines in kiln.log.
using Kiln;

namespace ImpactSample;

public sealed class ImpactSampleMod : Mod
{
    internal static readonly Guid HealOverTime = Guid.Parse("aad149f6-5485-4607-875e-62722822c77f");
    internal static readonly Guid ChainExplosion = Guid.Parse("7e24a487-bff5-4bae-aefc-b273059dc90f");
    internal static readonly Guid SignalExplosion = Guid.Parse("3db38d34-9ac2-4ebb-8a49-a3774d25b7cd");
    internal static readonly Guid SignalProjectile = Guid.Parse("3fb2242d-e7b7-4ee2-aed7-b4c8a4c4595e");
    private const uint TagSpawned = 0x5A11, TagChain = 0xC4A1, TagProjectile = 0x4E01;

    private readonly List<ImpactSignalEvent> _signals = new();
    private readonly List<EntityId> _spawned = new();
    private int _pings;

    public override void OnLoad()
    {
        if (!Abilities.Available)
        {
            Log.Warn("impactsample: kiln.abilities is not available; the programs are served but not registered");
            return;
        }
        _ = Demo();
    }

    public override void OnUnload()
    {
        foreach (var e in _spawned) try { Spawn.Destroy(e); } catch { }
    }

    [Subscribe] private void OnSignal(ImpactSignalEvent e)
    {
        if (e.Tag is not (TagSpawned or TagChain or TagProjectile)) return;
        _signals.Add(e);
        Log.Info($"impactsample: signal 0x{e.Tag:x4} from {e.Impact} site 0x{e.Site:x8}: entity {e.AsEntity}");
    }

    private byte Ping(NativeInvocation call)
    {
        Interlocked.Increment(ref _pings);
        Log.Info($"impactsample: Ping from {call.Impact} site 0x{call.Site:x8}: entity {call.GetEntity(0)}");
        return 1;
    }

    private async Task Steps(int n) { for (int i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private async Task Demo()
    {
        try
        {
            while (!Spawn.WorldRunning) await Context.Scheduler.NextTick();
            Abilities.Natives.Register("Ping", 1, Ping);
            await Steps(10);
            foreach (var (name, g) in new[] { ("heal_over_time", HealOverTime), ("chain_explosion", ChainExplosion),
                                              ("signal_explosion", SignalExplosion), ("signal_projectile", SignalProjectile) })
            {
                var info = Abilities.Programs.Info(Abilities.IdOf(g));
                Log.Info(info is null
                    ? $"impactsample: FAIL program {name} {g} is not registered"
                    : $"impactsample: program {name} 0x{info.Id:x8}: {info.CodeWords} code words, {info.Variables} variables, from Kiln {info.FromKiln}");
            }

            // somewhere away from the world's entities: an altar is a fixed landmark in every save
            var anchor = Spawn.FindByName("Altar", max: 1).FirstOrDefault();
            if (anchor.IsNone || Spawn.Inspect(anchor) is not { } a) { Log.Warn("impactsample: no anchor; demo skipped"); return; }
            var far = new WorldPosition(a.Position.X + 80, a.Position.Y + 1, a.Position.Z - 80);

            // 1. signal_explosion: kiln.Signal + impactsample.Ping right after its opening getSelf
            _signals.Clear();
            var e1 = await Abilities.RunProgram(SignalExplosion, far, new ImpactOptions { Tag = "impactsample" });
            _spawned.Add(e1);
            await Steps(20);
            var sig = _signals.FirstOrDefault(s => s.Tag == TagSpawned);
            Log.Info(sig is not null && _pings > 0
                ? $"impactsample: PASS signal_explosion: signal from self {sig.AsEntity} (spawned {e1}), Ping called {_pings}x"
                : $"impactsample: FAIL signal_explosion: signal {(sig is null ? "missing" : "ok")}, Ping calls {_pings}");

            // 2. chain_explosion: an enemy 4 m away gets a Fireball I explosion and the chain signal
            _signals.Clear();
            var vukahAt = new WorldPosition(far.X + 30, far.Y, far.Z);
            var vukah = await Spawn.Entity("Enemy_Vukah_Melee", vukahAt, 0, new SpawnOptions { Tag = "impactsample", Flags = SpawnFlags.Transient });
            _spawned.Add(vukah);
            await Steps(5);
            // no player: the impact entity itself is the root, so give it the Player faction (7) for isEnemy
            var e2 = await Abilities.RunProgram(ChainExplosion, new WorldPosition(vukahAt.X - 4, vukahAt.Y, vukahAt.Z),
                new ImpactOptions { Tag = "impactsample", Faction = 7 });
            _spawned.Add(e2);
            await Steps(20);
            var chain = _signals.FirstOrDefault(s => s.Tag == TagChain);
            Log.Info(chain is not null
                ? $"impactsample: PASS chain_explosion: chained to {chain.AsEntity} (Vukah {vukah})"
                : $"impactsample: FAIL chain_explosion: no chain signal (Vukah {vukah}, explosion {e2})");
        }
        catch (Exception ex) { Log.Error($"impactsample: demo threw {ex}"); }
        finally
        {
            await Steps(5);
            foreach (var e in _spawned) try { Spawn.Destroy(e); } catch { }
            _spawned.Clear();
        }
    }
}
