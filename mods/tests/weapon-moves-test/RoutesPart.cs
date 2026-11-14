// SPDX-License-Identifier: MIT
// Follow-up routes (Kiln.Blocks: MoveBlock.FollowUps, Blocks.MovesStatus) on a dedicated server. The routed sword is the mod
// mods/tests/weapon-routes-fixture, a mod of its own so that it can be unloaded; this part reads what became of it from the game's data and from the events. The first
// application is made to fail (the lab switch refuses its first clone), then tried again:
//   route-overlap-refused                  the fixture's second sword, whose routes overlap, is refused by name and is no block;
//   route-generation-preserved-on-failure  after the refusal the item's slots are the class's and no generation is applied;
//   route-status-refused-reason            the status says Refused with the reason;
//   route-applied-after-slot-switch        after the retry: the slots play the generation's clones, every window of the block leads where the routes
//                                          say (each conditional row), the light chain links up, the game's own sequences are untouched;
//   route-replay-no-event                  the same plan again raises no event;
//   route-unload-restores                  unloading the fixture reports Removed with the generation it had; the sword's slots are its class's
//                                          again (or the item went with the mod) and the game's sequences are as they were.
// Needs "preview": ["blocks.move_followups"] in the lab's kiln.json while the client gate holds the row; skipped with a line otherwise.
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const string Fixture = "weapon-routes-fixture", RouteRow = "blocks.move_followups", GameParry = "Ability_Player_Parry_AllWeapons";
    private readonly List<WeaponMovesApplicationChangedEvent> _routeEvents = new();
    private bool _routes;

    // OnLoad: before content is ready, so the fixture's first application meets the switch
    private void RoutesArm()
    {
        if (Capabilities.IsHeld(RouteRow)) return;
        _routes = true;
        Blocks.LabRefuseMoveClones(1);
        Events.On<WeaponMovesApplicationChangedEvent>(e => { if (e.ModId == Fixture) _routeEvents.Add(e); });
    }

    private static string Show(MoveApplicationResult r) =>
        $"{r.State} ok {r.Ok} requested {r.RequestedGeneration} applied {r.AppliedGeneration} content {r.ContentGeneration} clones {r.LiveClones.Count}{(r.Reason.Length > 0 ? $" reason \"{r.Reason}\"" : "")}";

    private async Task<bool> RouteEvents(int count, int steps = 600)
    {
        for (var i = 0; i < steps && _routeEvents.Count < count; i++) await Context.Scheduler.NextTick();
        return _routeEvents.Count >= count;
    }

    private static string Rows(GameDataEntry s) =>
        string.Join(", ", Openings(s).Select(o => $"#{o.K} {o.Trigger}->{(o.FollowUp == Guid.Empty ? "none" : Content.Sequences.Get(o.FollowUp.ToString())?.Name ?? o.FollowUp.ToString())}"));

    private async Task Routes()
    {
        if (!_routes) { Info($"{RouteRow} is held (game test pending): the routes part is skipped; put \"preview\": [\"{RouteRow}\"] in kiln.json to run it"); return; }
        if (!Mods.IsLoaded(Fixture)) { Info($"the fixture {Fixture} is not loaded here: the routes part is skipped"); return; }
        var report = Blocks.Report().GetValueOrDefault(Fixture);
        var overlap = (report.Errors ?? []).Where(e => e.Contains("route-overlap", StringComparison.Ordinal)).ToList();
        Check(overlap.Any(e => e.Contains("followUps[1]", StringComparison.Ordinal) && e.Contains("followUps[0]", StringComparison.Ordinal)) && !(report.Blocks ?? []).Contains("route-overlap") && !_wrong,
            $"route-overlap-refused: two routes on one window are refused naming both, and the sword is no block: [{string.Join(" | ", overlap)}]");

        var itemName = $"{Fixture}.route-sword";
        if (!Check(await RouteEvents(1) && Content.ItemData.Get(itemName) != null, $"routes: the first application of route-sword ended: {_routeEvents.Count} event(s)")) return;
        var first = _routeEvents[0].After;
        var rows = Slots(Content.ItemData.Get(itemName)!);
        var mine = Content.GameDataDefinitions().Where(d => d.Name.StartsWith($"{itemName}.", StringComparison.Ordinal)).ToList();
        Info($"routes: after the refusal: {Show(first)}; clones staged [{string.Join(", ", mine.Select(d => $"{d.Name} {d.State}"))}]");
        Check(first.State == MoveApplicationState.Refused && first.AppliedGeneration == 0 && rows.Count > 0 && rows.All(r => r.Sequence == r.Orig) && _routeEvents.Count == 1,
            $"route-generation-preserved-on-failure: every one of the item's {rows.Count} slots plays its class's sequence and no generation is applied: {Show(first)}");
        Check(!first.Ok && first.Reason.Contains("refused by the test switch", StringComparison.Ordinal) && first.Reason.Contains("moves", StringComparison.Ordinal) && first.LiveClones.Count == 0 && !_wrong,
            $"route-status-refused-reason: {Show(first)}");

        if (Blocks.Find("route-sword", Fixture) is not BlockHandle<WeaponBlock> sword) { Check(false, "routes: Blocks.Find gives the fixture's sword"); return; }
        Check(Blocks.LabRetryMoves(sword) && await RouteEvents(2), $"routes: the retry ended: {_routeEvents.Count} event(s)");
        if (_routeEvents.Count < 2) return;
        var applied = _routeEvents[1].After;
        Info($"routes: after the retry: {Show(applied)}; {string.Join(", ", applied.LiveClones.Select(c => $"{c.Key}={Content.Sequences.Get(c.Value.ToString())?.Name}"))}");
        var ok = applied is { Ok: true, State: MoveApplicationState.Applied, AppliedGeneration: 2, RequestedGeneration: 2 } && _routeEvents[1].Before.State == MoveApplicationState.Refused;
        Guid Live(string key) => applied.LiveClones.TryGetValue(key, out var g) ? g : Guid.Empty;
        var (p1, p2, block, parry) = (Live("primary/1"), Live("primary/2"), Live("block/1"), Live("parry/1"));
        ok &= new[] { p1, p2, block, parry }.All(g => g != Guid.Empty && Content.Sequences.Get(g.ToString()) is { IsClone: true });
        // the slots: the routed ones play the generation's clones, every other is the class's
        rows = Slots(Content.ItemData.Get(itemName)!);
        var want = new Dictionary<int, Guid>();
        foreach (var i in Where(rows, "primary")) want[i] = p1;
        foreach (var i in Where(rows, "heavyAir")) want[i] = p1;
        foreach (var i in Where(rows, "block")) want[i] = block;
        foreach (var r in rows.Where(r => r.Trigger == "Parry" && r.Requirements.Length == 0 && r.Special is "Invalid" or "")) want[r.Index] = parry;
        var slotsOk = want.Count >= 4 && rows.All(r => r.Sequence == (want.TryGetValue(r.Index, out var g) ? g : r.Orig));
        Info($"routes: slots [{string.Join(", ", rows.Select(r => $"{r.Index}:{r.Trigger}->{Content.Sequences.Get(r.Sequence.ToString())?.Name}"))}]");
        var linksOk = false;
        var gameBlockOk = false;
        if (ok)
        {
            // every conditional row of the block: Parried to this sword's parry, Primary to its first light attack; rows without a follow-up stay without
            var b = Openings(Content.Sequences.Get(block.ToString())!);
            var parried = b.Where(o => o.Trigger == "Parried" && o.Orig != Guid.Empty).ToList();
            var primary = b.Where(o => o.Trigger == "Primary" && o.Orig != Guid.Empty).ToList();
            var chain1 = Openings(Content.Sequences.Get(p1.ToString())!).Where(o => o.Trigger == "Primary" && o.Orig != Guid.Empty).ToList();
            var chain2 = Openings(Content.Sequences.Get(p2.ToString())!).Where(o => o.Trigger == "Primary" && o.Orig != Guid.Empty).ToList();
            linksOk = parried.Count > 0 && primary.Count > 0 && parried.All(o => o.FollowUp == parry) && primary.All(o => o.FollowUp == p1)
                && b.Where(o => o.Orig == Guid.Empty).All(o => o.FollowUp == Guid.Empty)
                && b.Where(o => o.Trigger is not ("Parried" or "Primary")).All(o => o.FollowUp == o.Orig)
                && chain1.Count > 1 && chain1.All(o => o.FollowUp == p2) && chain2.Count > 0 && chain2.All(o => o.FollowUp == p1);
            Info($"routes: block [{Rows(Content.Sequences.Get(block.ToString())!)}]; primary 1 [{Rows(Content.Sequences.Get(p1.ToString())!)}]; primary 2 [{Rows(Content.Sequences.Get(p2.ToString())!)}]");
            gameBlockOk = new[] { GameBlock, GameParry, Light1, Light2 }.All(n => Openings(Seq(n)).All(o => o.FollowUp == o.Orig));
        }
        Check(ok && slotsOk && linksOk && gameBlockOk && !_wrong,
            $"route-applied-after-slot-switch: generation 2 applied after the refusal (status {ok}), the slots play its clones and the others their class's ({slotsOk}), "
            + $"the block's Parried rows lead to the sword's parry and its Primary rows to primary 1, the chain links up ({linksOk}), the game's own sequences are untouched ({gameBlockOk})");

        // the same plan again: no event, the same answer
        var n = _routeEvents.Count;
        Blocks.LabRetryMoves(sword);
        await Steps(80);
        Check(_routeEvents.Count == n && Slots(Content.ItemData.Get(itemName)!).All(r => r.Sequence == (want.TryGetValue(r.Index, out var g) ? g : r.Orig)),
            $"route-replay-no-event: the same plan again raised {_routeEvents.Count - n} event(s) and the slots stayed");

        // unloading the fixture: Removed with the generation it had; the game's own sequences as they were; the item, if the game still has it, on its class's slots
        var done = await Mods.UnloadAsync(Fixture);
        await RouteEvents(n + 1, 200);
        await Steps(40);
        var last = _routeEvents[^1].After;
        var item = Content.ItemData.Get(itemName);
        var restored = item == null || Slots(item).All(r => r.Sequence == r.Orig);
        var untouched = new[] { GameBlock, GameParry, Light1, Light2 }.All(s => Openings(Seq(s)).All(o => o.FollowUp == o.Orig));
        Check(done.Ok && _routeEvents.Count == n + 1 && last is { State: MoveApplicationState.Removed, AppliedGeneration: 2, Ok: false } && last.LiveClones.Count == 0 && restored && untouched,
            $"route-unload-restores: unload {done}: {Show(last)}; the item {(item == null ? "went with the mod" : "is on its class's slots: " + restored)}; the game's sequences untouched: {untouched}");
    }
}
