// SPDX-License-Identifier: MIT
using Kiln;

namespace WorldTest;

/// <summary>
/// Player-free checks of kiln.world on a copied world: status, hashes, find, positions, then teleports a villager a few
/// metres and back (and the refusal paths). Prints "world-test: PASS|FAIL ..." lines and a summary.
/// </summary>
public sealed class WorldTestMod : Mod
{
    private int _pass, _fail;

    private Task<TeleportResult>? _probe;

    public override void OnLoad()
    {
        Log.Info("world-test loaded");
        // Loaded into a running world (hot reload): leave one teleport in flight, so an unload right after must drop
        // it (kiln.world cancels it on Retiring; reload-test then checks the mod's context is collectible).
        if (World.Available && World.Status.WorldLive)
        {
            var npc = World.Find("NpcCommon").FirstOrDefault(e => World.Has(e, "TeleportCount"));
            if (!npc.IsNone && World.Position(npc) is { } p)
            {
                _probe = Teleport.To(npc, p);
                Log.Info($"world-test: probe teleport of {npc} in flight");
            }
        }
        _ = RunAsync();
    }

    public override void OnUnload() =>
        Log.Info($"world-test unloading; probe {(_probe is null ? "none" : _probe.Status.ToString())}");

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"world-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunAsync()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"unexpected exception: {e}");
        }
        Log.Info($"world-test: done, {_pass} passed, {_fail} failed");
    }

    private async Task Run()
    {
        Check(World.Available, "kiln.world service is available");
        if (!World.Available) return;
        for (var i = 0; i < 1500 && !World.Status.WorldLive; i++) await Context.Scheduler.NextTick();
        var st = World.Status;
        Log.Info($"world-test: status {st}");
        Check(st.WorldLive && st.Programs && st.Find, "world live, programs and find on");
        Check(st.Teleport && Teleport.Available, "teleport available");
        Check(World.ComponentHash("CurrentTransform") == 0x027ac564u, "ComponentHash(CurrentTransform) = 0x027ac564");
        Check(World.ComponentHash("Teleport") == 0x23384d40u, "ComponentHash(Teleport) = 0x23384d40");

        await Steps(50);  // let the world settle after load
        var count = World.EntityCount;
        Check(count > 100, $"EntityCount = {count}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var withTc = World.Find("TeleportCount");
        var findMs = sw.Elapsed.TotalMilliseconds;
        var npcs = World.Find("NpcCommon");
        var players = World.Find("PlayerInput");
        Check(withTc.Count > 0, $"Find(TeleportCount) = {withTc.Count} entities ({findMs:F2} ms over {count} entities)");
        Log.Info($"world-test: Find(NpcCommon) = {npcs.Count}, Find(PlayerInput) = {players.Count}");
        Check(World.Find("NoSuchComponentKiln").Count == 0, "Find(unknown component) is empty");
        Check(withTc.All(e => World.Has(e, "TeleportCount")), "every Find(TeleportCount) result Has(TeleportCount)");

        var tcSet = withTc.ToHashSet();
        var candidates = npcs.Where(e => tcSet.Contains(e) && World.Has(e, "LastTransform") && World.Position(e) is not null)
            .OrderBy(e => e.Value).ToList();
        foreach (var e in candidates.Take(12))
            Log.Info($"world-test: npc {e} at {World.Position(e)} yaw {World.Transform(e)?.YawDegrees:F0}");
        Check(candidates.Count > 0, $"{candidates.Count} villagers/NPCs can be teleported");
        if (candidates.Count == 0) return;

        var npc = candidates[0];
        var home = World.Position(npc)!.Value;
        var c0 = Counter(npc);
        Log.Info($"world-test: test subject {npc} at {home}, TeleportCount {c0}");

        // 1. a few metres east, no ground search
        var hop = home with { X = home.X + 3 };
        var r1 = await Teleport.To(npc, hop);
        Log.Info($"world-test: hop result: {r1}");
        Check(r1.Moved && r1.Distance < 0.5, $"teleport 3 m: {r1}");
        var p1 = World.Position(npc);
        Check(p1 is { } a && a.DistanceTo(hop) < 1.0, $"CurrentTransform after the hop: {p1} (target {hop})");
        Check(Counter(npc) == (byte)(c0 + 1), $"TeleportCount {c0} -> {Counter(npc)}");

        // 2. a second request while one is in flight is refused
        await Steps(3);
        var first = Teleport.To(npc, home);
        var dup = await Teleport.To(npc, hop);
        Check(!dup.Moved && dup.Reason.Contains("in flight"), $"duplicate request refused: {dup.Reason}");
        var back = await first;
        Check(back.Moved && back.Distance < 0.5, $"teleport back home: {back}");
        Check(Counter(npc) == (byte)(c0 + 2), $"TeleportCount now {Counter(npc)} (expected {(byte)(c0 + 2)})");

        // 3. ground search from 3 m up, then home again
        await Steps(3);
        var up = home with { Y = home.Y + 3 };
        var g = await Teleport.To(npc, up, new TeleportOptions { FindGround = true, YawDegrees = 90 });
        Log.Info($"world-test: ground search from {up}: {g}; yaw now {World.Transform(npc)?.YawDegrees:F0}");
        Check(g.Moved, $"FindGround teleport moved (landed y {g.Position.Y:F2}, home y {home.Y:F2})");
        await Steps(3);
        var g2 = await Teleport.To(npc, home);
        Check(g2.Moved && g2.Distance < 0.5, $"home again: {g2}");

        // 4. next to another NPC (the admin's "to another player"), and back
        if (candidates.Count > 1)
        {
            var other = candidates.FirstOrDefault(e => e != npc && World.Position(e) is { } q && q.DistanceTo(home) < 300);
            if (!other.IsNone)
            {
                await Steps(3);
                var to = await Teleport.ToEntity(npc, other);
                var op = World.Position(other);
                Check(to.Moved && op is { } q2 && to.Position.DistanceTo(q2) < 5, $"ToEntity {npc} -> {other} at {op}: {to}");
                await Steps(3);
                var b2 = await Teleport.To(npc, home);
                Check(b2.Moved, $"back home after ToEntity: {b2}");
            }
        }

        // 5. refusals
        var prop = World.Find("CurrentTransform").FirstOrDefault(e =>
            e.Value > 1023 && !tcSet.Contains(e) && !World.Has(e, "LastTransform"));
        if (!prop.IsNone)
        {
            var rp = await Teleport.To(prop, World.Position(prop)!.Value);
            Check(!rp.Moved && rp.Reason.Contains("TeleportCount"), $"prop {prop} refused: {rp.Reason}");
        }
        var bad = await Teleport.To(npc, new WorldPosition(double.NaN, 0, 0));
        Check(!bad.Moved && bad.Reason.Contains("invalid"), $"NaN target refused: {bad.Reason}");
        var none = await Teleport.To(new EntityId(0xFFFFFF0), home);
        Check(!none.Moved, $"unknown entity refused: {none.Reason}");

        var final = World.Position(npc);
        Check(final is { } f && f.DistanceTo(home) < 2.0, $"{npc} ends at {final} (home {home})");
        Log.Info($"world-test: TeleportCount of {npc} ended at {Counter(npc)} (started {c0})");
    }

    private static byte Counter(EntityId e) => World.TryRead<byte>(e, "TeleportCount", out var c) ? c : (byte)0;
}
