// SPDX-License-Identifier: MIT
using Kiln;

namespace GameplayTest;

/// <summary>
/// Player-free checks of kiln.gameplay on a copied world: status and hooks, zones, the pending-event interception
/// (crafting, interaction and loot requests sent through Kiln's own program, as a client or villager would), their
/// cancellation, pickups, and teleport events (a villager moved by kiln.world: allowed, canceled, redirected).
/// Prints "gameplay-test: PASS|FAIL ..." lines and a summary.
/// </summary>
public sealed class GameplayTestMod : Mod
{
    private int _pass, _fail;

    private readonly List<CraftEvent> _crafts = new();
    private readonly List<ItemCraftedEvent> _crafted = new();
    private readonly List<ItemPickupEvent> _pickups = new();
    private readonly List<InteractEvent> _interacts = new();
    private readonly List<InteractionResultEvent> _results = new();
    private readonly List<LootEvent> _loots = new();
    private readonly List<TeleportEvent> _teleports = new();
    private readonly List<TeleportedEvent> _teleported = new();
    private readonly List<BuildEvent> _builds = new();
    private readonly List<NaturalSpawnEvent> _spawns = new();

    private bool _cancelCraft, _cancelInteract, _cancelLoot, _cancelTeleport, _overrideZone;
    private WorldPosition? _redirect;

    // [Subscribe] alone: Kiln registers these before OnLoad; kiln.log shows whether Kiln.Gameplay was already listening.
    [Subscribe(ReceiveCanceled = true)]
    private void OnCraft(CraftEvent e)
    {
        _crafts.Add(e);
        if (_cancelCraft) e.Cancel();
        Log.Info($"gameplay-test: event CraftEvent operator {e.Operator} station {e.Station} recipe {e.RecipeId:x8} x{e.Amount} injected {e.Injected} canceled {e.Canceled} zone {e.DeniedByZone}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnCrafted(ItemCraftedEvent e)
    {
        _crafted.Add(e);
        Log.Info($"gameplay-test: event ItemCraftedEvent operator {e.Operator} station {e.Station} recipe {e.RecipeId:x8} workshop {e.WorkshopId:x8} result {e.Result} ({e.RawResult}) outputs [{string.Join(", ", e.Outputs.Select(o => $"{o.ItemId:x8} x{o.Count}"))}]");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnPickup(ItemPickupEvent e)
    {
        _pickups.Add(e);
        Log.Info($"gameplay-test: event ItemPickupEvent receiver {e.Receiver} source {e.Source} item {e.ItemId:x8} x{e.Count} slot {e.SlotInventory}:{e.SlotIndex} hidden {e.HiddenInUi}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnInteract(InteractEvent e)
    {
        _interacts.Add(e);
        if (_cancelInteract && e.Injected) e.Cancel();
        if (_overrideZone && e.DeniedByZone != 0) e.Canceled = false;
        Log.Info($"gameplay-test: event InteractEvent guest {e.Guest} host {e.Host} offer {e.OfferId} at {e.HostPosition} injected {e.Injected} canceled {e.Canceled} zone {e.DeniedByZone} '{e.ZoneName}'");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnResult(InteractionResultEvent e)
    {
        _results.Add(e);
        Log.Info($"gameplay-test: event InteractionResultEvent guest {e.Guest} host {e.Host} offer {e.OfferId} accepted {e.Accepted}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnLoot(LootEvent e)
    {
        _loots.Add(e);
        if (_cancelLoot && e.Injected) e.Cancel();
        Log.Info($"gameplay-test: event LootEvent guest {e.Guest} host {e.Host} offer {e.OfferId} lootAll {e.LootAll} bypass {e.BypassInteraction} injected {e.Injected} canceled {e.Canceled} zone {e.DeniedByZone}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnTeleport(TeleportEvent e)
    {
        _teleports.Add(e);
        if (_cancelTeleport) e.Cancel();
        else if (_redirect is { } r) e.Target = r;
        Log.Info($"gameplay-test: event TeleportEvent {e.Entity} from {e.From} to {e.Target} target entity {e.TargetEntity} ground {e.SearchesGround} canceled {e.Canceled}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnTeleported(TeleportedEvent e)
    {
        _teleported.Add(e);
        Log.Info($"gameplay-test: event TeleportedEvent {e.Entity} from {e.From} to {e.To} count {e.Count}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnBuild(BuildEvent e)
    {
        _builds.Add(e);
        Log.Info($"gameplay-test: event BuildEvent {e.Kind} player {e.Player} item {e.ItemId:x8} at {e.Position} {e.Outcome} zone {e.DeniedByZone}");
    }

    [Subscribe(ReceiveCanceled = true)]
    private void OnSpawn(NaturalSpawnEvent e)
    {
        _spawns.Add(e);
        Log.Info($"gameplay-test: event NaturalSpawnEvent {e.Entity} template {e.Template} at {e.Position} zone {e.DeniedByZone}");
    }

    public override void OnLoad()
    {
        Log.Info("gameplay-test loaded");
        Gameplay.Enable();
        _ = RunAsync();
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"gameplay-test: {(ok ? "PASS" : "FAIL")} {what}");
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
        Log.Info($"gameplay-test: done, {_pass} PASS, {_fail} FAIL");
    }

    private async Task Run()
    {
        // The save loads ~10 s after the first step; wait for the world to be populated and items to work.
        for (var i = 0; i < 3000 && !(World.Available && World.EntityCount > 1500 && Items.CanTransact); i++) await Steps(1);
        await Steps(100);

        var st = Gameplay.Status;
        Check(st is not null, "kiln.gameplay is available");
        if (st is null) return;
        Log.Info($"gameplay-test: status {st}");
        Check(st.Intercept, "pending events are intercepted on this build");
        Check(st.Inject, "Kiln's program kiln_gameplay is active (inject)");
        Check(st.CraftHook && st.BuildHooks && st.TerraformHook && st.SpawnHook, "craft, building, terraform and spawn hooks installed");
        Check(st.Teleports, "Teleport components are watched");

        ZoneChecks();

        var all = World.Find("Crafting");
        foreach (var e in all)
            Log.Info($"gameplay-test: Crafting entity {e}: workshop {Workshop(e):x8}{(World.Has(e, "FactoryStation") ? " factory" : "")}{(World.Has(e, "NpcCommon") ? " npc" : "")}{(World.Has(e, "InventorySetup") ? " inventory" : "")}");
        var stations = all.Where(e => !e.IsPlayer && !World.Has(e, "FactoryStation")).ToList();
        var containers = Items.FindInventories().Where(c => !World.Has(c, "Crafting")).ToList();
        Log.Info($"gameplay-test: {all.Count} entities with Crafting ({stations.Count} non-factory stations), {containers.Count} containers");
        var chests = containers.Select(c => Items.Read(c)).Where(c => c is { Initialized: true } && c.FreeSlots() >= 4)
            .OrderBy(c => c!.UsedSlots).ToList();
        Check(chests.Count >= 2, $"two containers with room: {string.Join(", ", chests.Take(2).Select(c => c!.Entity))}");
        if (chests.Count < 2) return;
        var chest = chests[0]!.Entity;
        var other = chests[1]!.Entity;
        var (station, recipes) = PickStation(stations);
        Check(!station.IsNone && recipes.Count > 0, $"a station with simple recipes: {station} ({recipes.Count} candidate recipe(s))");
        if (station.IsNone) return;

        await CraftChecks(chest, station, recipes);
        await InteractChecks(chest, station);
        await LootChecks(chest, other);
        await ZonePreChecks(chest, station);
        await TeleportChecks();

        var claim = Gameplay.Zones.FirstOrDefault(z => z.Zone.Name == "Lab claim");
        if (claim is not null)
            Check(Gameplay.IsDenied(new EntityId(9), claim.Zone.Center, GameplayActions.Build, out var cz) && cz == claim.Id &&
                  Gameplay.IsDenied(EntityId.None, claim.Zone.Center, GameplayActions.Spawn, out _),
                $"the Claims sample's zone '{claim.Zone.Name}' (id {claim.Id}) denies building and natural spawns inside");
        Check(_builds.Count == 0, $"no building events without players ({_builds.Count})");
        Log.Info($"gameplay-test: natural spawn events seen: {_spawns.Count}");
        var end = Gameplay.Status!;
        Log.Info($"gameplay-test: status at the end {end}");
        Check(end.EventsSeen > 0 && end.Dispatched > 0, $"pending events read ({end.EventsSeen}) and dispatched ({end.Dispatched})");
    }

    private static uint Workshop(EntityId e)
    {
        var b = World.ReadBytes(e, "Crafting");
        return b is { Length: >= 0x14 } ? BitConverter.ToUInt32(b, 0x10) : 0xFFFFFFFF;
    }

    private void ZoneChecks()
    {
        var center = new WorldPosition(5000, 900, 5000);
        using (var z = Gameplay.Protect(new ProtectionZone
               {
                   Name = "test-zone", Center = center, Radius = 10, Deny = GameplayActions.All, AllowAdmins = false,
                   AllowedSteamIds = [76561190000000001UL],
               }))
        {
            Check(Gameplay.Status!.Zones >= 1, $"zone {z.Id} registered");
            Check(Gameplay.IsDenied(new EntityId(7), center, GameplayActions.Build, out var zid) && zid == z.Id,
                "a player not on the list may not build inside");
            Check(!Gameplay.IsDenied(new EntityId(7), new WorldPosition(5020, 900, 5000), GameplayActions.Build, out _),
                "... and may outside (20 m away)");
            Check(!Gameplay.IsDenied(new EntityId(3000), center, GameplayActions.Build, out _), "non-player actors are not restricted");
            Check(Gameplay.IsDenied(EntityId.None, center, GameplayActions.Spawn, out _), "natural spawns are refused inside");
            z.Update(z.Zone with { Deny = GameplayActions.Interact });
            Check(!Gameplay.IsDenied(new EntityId(7), center, GameplayActions.Build, out _) &&
                  Gameplay.IsDenied(new EntityId(7), center, GameplayActions.Interact, out _), "update: only Interact is denied now");
        }
        Check(!Gameplay.IsDenied(new EntityId(7), center, GameplayActions.Interact, out _), "disposed zone denies nothing");
        var box = Gameplay.Protect(new ProtectionZone
        {
            Name = "box", Shape = ZoneShape.Box, Min = new WorldPosition(0, 0, 0), Max = new WorldPosition(10, 10, 10),
            Deny = GameplayActions.Terraform,
        });
        Check(Gameplay.IsDenied(new EntityId(2), new WorldPosition(5, 5, 5), GameplayActions.Terraform, out _) &&
              !Gameplay.IsDenied(new EntityId(2), new WorldPosition(5, 11, 5), GameplayActions.Terraform, out _), "box zone");
        box.Dispose();
    }

    private async Task<int> Count(EntityId inv, ItemId item)
    {
        await Task.CompletedTask;
        return Items.Read(inv)?.Count(item) ?? -1;
    }

    // A non-factory station and its simple recipes: concrete item inputs only, no crafting props.
    private (EntityId, List<GameplayRecipe>) PickStation(List<EntityId> stations)
    {
        foreach (var s in stations)
        {
            var ws = Workshop(s);
            var ids = Gameplay.WorkshopRecipes(ws);
            var simple = ids.Select(Gameplay.Recipe).Where(r => r is { HasCategoryInputs: false, RequiredProps: 0 } &&
                    r.Inputs.Count is > 0 and <= 3 && r.Inputs.All(i => i.Count <= 20) && r.Outputs.Count > 0)
                .Select(r => r!).ToList();
            Log.Info($"gameplay-test: station {s} workshop {ws:x8}: {ids.Count} recipe(s), {simple.Count} simple");
            if (simple.Count > 0) return (s, simple);
        }
        return (EntityId.None, []);
    }

    private async Task GiveInputs(EntityId chest, GameplayRecipe r)
    {
        foreach (var i in r.Inputs)
        {
            var g = await Items.Give(chest, new ItemId(i.ItemId), i.Count);
            if (!g.Succeeded) Log.Warn($"gameplay-test: give {i.ItemId:x8} x{i.Count}: {g}");
        }
    }

    private async Task TakeBack(EntityId chest, IEnumerable<GameplayItemStack> items, Dictionary<uint, int> baseline)
    {
        foreach (var id in items.Select(i => i.ItemId).Distinct())
        {
            var now = await Count(chest, new ItemId(id));
            var extra = now - baseline.GetValueOrDefault(id);
            if (extra > 0) await Items.Take(chest, new ItemId(id), extra);
        }
    }

    private async Task CraftChecks(EntityId chest, EntityId station, List<GameplayRecipe> recipes)
    {
        var baseline = new Dictionary<uint, int>();
        foreach (var r in recipes.Take(4))
            foreach (var i in r.Inputs.Concat(r.Outputs))
                baseline[i.ItemId] = await Count(chest, new ItemId(i.ItemId));

        // 1. allowed: try simple recipes until the game crafts one (some need knowledge, levels or comfort)
        GameplayRecipe? used = null;
        ItemCraftedEvent? done = null;
        foreach (var r in recipes.Take(4))
        {
            Log.Info($"gameplay-test: trying recipe {r.Id:x8}: [{string.Join(", ", r.Inputs.Select(i => $"{i.ItemId:x8} x{i.Count}"))}] -> [{string.Join(", ", r.Outputs.Select(i => $"{i.ItemId:x8} x{i.Count}"))}]");
            await GiveInputs(chest, r);
            _crafts.Clear(); _crafted.Clear(); _pickups.Clear();
            Gameplay.Inject.Craft(chest, station, r.Id);
            await Steps(4);
            var seen = _crafts.FirstOrDefault(c => c.Injected && c.Operator == chest);
            Check(seen is not null && seen.Station == station && seen.RecipeId == r.Id && seen.Amount == 1,
                $"CraftEvent before the game handles the request ({seen?.Operator} @ {seen?.Station}, recipe {seen?.RecipeId:x8})");
            done = _crafted.FirstOrDefault(c => c.Operator == chest);
            Log.Info($"gameplay-test: recipe {r.Id:x8}: {(done is null ? "no craft core call" : done.Result.ToString())}");
            if (done is { Succeeded: true }) { used = r; break; }
            await TakeBack(chest, r.Inputs, baseline);
        }
        Check(used is not null && done is not null, $"ItemCraftedEvent after the game's craft core: {done?.Result}, recipe {used?.Id:x8}");
        if (used is null || done is null) return;
        var outItem = new ItemId(used.Outputs[0].ItemId);
        var outCount = used.Outputs[0].Count;
        var in0 = new ItemId(used.Inputs[0].ItemId);
        Check(done.Outputs.Count > 0 && done.Outputs[0].ItemId == outItem.Value, $"the event's outputs match the recipe ({done.Outputs[0].ItemId:x8} x{done.Outputs[0].Count})");
        var outNow = await Count(chest, outItem);
        var inNow = await Count(chest, in0);
        Check(outNow == baseline.GetValueOrDefault(outItem.Value) + outCount && inNow == baseline.GetValueOrDefault(in0.Value),
            $"chest after the craft: output {outItem} {baseline.GetValueOrDefault(outItem.Value)} -> {outNow}, input {in0} back to {inNow}");
        Check(_pickups.Any(p => p.Receiver == chest && p.ItemId == outItem.Value),
            $"ItemPickupEvent for the craft output into the chest ({_pickups.Count} pickup event(s))");

        // 2. canceled: materials again; the request must not reach the game.
        await GiveInputs(chest, used);
        var in2 = await Count(chest, in0);
        var out2 = await Count(chest, outItem);
        _crafts.Clear(); _crafted.Clear();
        _cancelCraft = true;
        Gameplay.Inject.Craft(chest, station, used.Id);
        await Steps(4);
        _cancelCraft = false;
        Check(_crafts.Any(c => c.Injected && c.Canceled), "the CraftEvent was seen and canceled");
        Check(_crafted.All(c => c.Operator != chest), "no craft ran after the cancel");
        var in3 = await Count(chest, in0);
        var out3 = await Count(chest, outItem);
        Check(in3 == in2 && out3 == out2, $"chest unchanged by the canceled craft: input {in2} -> {in3}, output {out2} -> {out3}");

        await TakeBack(chest, used.Inputs.Concat(used.Outputs), baseline);
        Log.Info($"gameplay-test: chest {chest} cleaned: input {await Count(chest, in0)}, output {await Count(chest, outItem)}");
    }

    private async Task InteractChecks(EntityId host, EntityId guest)
    {
        var rt = World.ReadBytes(host, "InteractionOfferRuntime");
        Log.Info($"gameplay-test: host {host} InteractionOfferRuntime {(rt is null ? "none" : Convert.ToHexString(rt))}");
        var offer = rt is { Length: >= 0x2C } ? BitConverter.ToUInt32(rt, 0x28) : 1u;
        // 1. allowed: the game answers (accepted or declined) in the next step
        _interacts.Clear(); _results.Clear();
        Gameplay.Inject.Interact(guest, host, offer);
        await Steps(4);
        Check(_interacts.Any(i => i.Injected && i.Guest == guest && i.Host == host && i.OfferId == offer && !i.Canceled),
            $"InteractEvent for the attempt (guest {guest}, host {host}, offer {offer})");
        var answer = _results.FirstOrDefault(r => r.Guest == guest && r.Host == host);
        Check(answer is not null, $"the game answered the attempt: {(answer is null ? "nothing" : answer.Accepted ? "accepted" : "declined")}");
        // 2. canceled: becomes a DeclinedInteraction (kiln.log: "delivery check"), the game sends no answer of its own
        _interacts.Clear(); _results.Clear();
        _cancelInteract = true;
        Gameplay.Inject.Interact(guest, host, offer);
        await Steps(4);
        _cancelInteract = false;
        Check(_interacts.Any(i => i.Injected && i.Canceled), "the InteractEvent was seen and canceled");
        Check(!_results.Any(r => r.Guest == guest && r.Host == host && r.Accepted), "no InteractionAccepted for the canceled attempt");
    }

    private async Task LootChecks(EntityId guest, EntityId host)
    {
        var logs = Items.Lookup("Material_T1_Wood_Logs")?.Id ?? new ItemId(0x2b0cf1d5);
        var hostBefore = Items.Read(host)!;
        var g = await Items.Give(host, logs, 2);
        Check(g.Succeeded, $"2 logs put into the looted container {host}: {g}");
        var hostLogs0 = await Count(host, logs);
        var guestLogs0 = await Count(guest, logs);
        // 1. canceled first (the container keeps everything)
        _loots.Clear(); _pickups.Clear();
        _cancelLoot = true;
        Gameplay.Inject.Loot(guest, host);
        await Steps(4);
        _cancelLoot = false;
        Check(_loots.Any(l => l.Injected && l.Canceled && l.Host == host), "LootEvent seen and canceled");
        var hostLogs1 = await Count(host, logs);
        Check(hostLogs1 == hostLogs0 && await Count(guest, logs) == guestLogs0, $"nothing moved after the cancel (host logs {hostLogs0} -> {hostLogs1})");
        // 2. allowed: loot all from the host into the guest (bypass interaction, as villagers' jobs do)
        _loots.Clear(); _pickups.Clear();
        Gameplay.Inject.Loot(guest, host);
        await Steps(4);
        Check(_loots.Any(l => l.Injected && !l.Canceled && l.Host == host && l.LootAll), "LootEvent seen (allowed)");
        var hostLogs2 = await Count(host, logs);
        var guestLogs2 = await Count(guest, logs);
        Log.Info($"gameplay-test: loot all: host logs {hostLogs0} -> {hostLogs2}, guest logs {guestLogs0} -> {guestLogs2}, {_pickups.Count} pickup event(s)");
        Check(hostLogs2 < hostLogs0 && guestLogs2 > guestLogs0, "the game moved the looted items");
        Check(_pickups.Any(p => p.Receiver == guest && p.Source == host), "ItemPickupEvent receiver = guest, source = host");
        // clean up: move back everything loot all moved, then take this test's 2 logs out of the host
        foreach (var p in _pickups.Where(p => p.Receiver == guest && p.Source == host).ToList())
        {
            var t = await Items.Take(guest, new ItemId(p.ItemId), p.Count);
            var g2 = await Items.Give(host, new ItemId(p.ItemId), p.Count);
            if (!t.Succeeded || !g2.Succeeded) Log.Warn($"gameplay-test: restoring {p.ItemId:x8} x{p.Count}: {t}; {g2}");
        }
        var extra = await Count(host, logs) - hostBefore.Count(logs);
        if (extra > 0) await Items.Take(host, logs, extra);
        var moved = Items.Read(guest)!;
        Log.Info($"gameplay-test: after cleanup guest {guest} {moved.UsedSlots} used slot(s), host {host} {Items.Read(host)!.UsedSlots} (was {hostBefore.UsedSlots})");
    }

    // Zones on the Pre path: a request by a player entity (7: no such player, so nobody is allowed) inside a zone
    // arrives already canceled with the zone's id; a handler may override the zone for one event.
    private async Task ZonePreChecks(EntityId host, EntityId station)
    {
        var fake = new EntityId(7);
        var hp = World.Position(host)!.Value;
        var sp = World.Position(station)!.Value;
        using var z1 = Gameplay.Protect(new ProtectionZone
        {
            Name = "pre-zone", Center = hp, Radius = 4, Deny = GameplayActions.Interact | GameplayActions.Loot, AllowAdmins = false,
        });
        using var z2 = Gameplay.Protect(new ProtectionZone
        {
            Name = "station-zone", Center = sp, Radius = 4, Deny = GameplayActions.Craft, AllowAdmins = false,
        });
        await Steps(2);
        _interacts.Clear(); _loots.Clear(); _crafts.Clear(); _results.Clear();
        Gameplay.Inject.Interact(fake, host, 0);
        Gameplay.Inject.Loot(fake, host);
        Gameplay.Inject.Craft(fake, station, 0x2cd4c47e);
        await Steps(4);
        Check(_interacts.Any(i => i.Guest == fake && i.Canceled && i.DeniedByZone == z1.Id && i.ZoneName == "pre-zone"),
            "a zone denies a player's interaction before the game reads it");
        Check(_loots.Any(l => l.Guest == fake && l.Canceled && l.DeniedByZone == z1.Id), "a zone denies a player's loot");
        Check(_crafts.Any(c => c.Operator == fake && c.Canceled && c.DeniedByZone == z2.Id), "a zone denies a player's craft at a station inside");
        Check(_results.All(r => r.Guest != fake), "no game answer for the zone-denied interaction (it became a decline)");
        // a handler overrides the zone: the game gets the request (and answers it: guest 7 does not exist -> declined)
        _interacts.Clear(); _results.Clear();
        _overrideZone = true;
        Gameplay.Inject.Interact(fake, host, 0);
        await Steps(4);
        _overrideZone = false;
        Check(_interacts.Any(i => i.Guest == fake && i.DeniedByZone == z1.Id), "zone-denied interaction seen again");
        Check(_results.Any(r => r.Guest == fake && r.Host == host), $"after a handler un-canceled it, the game answered it ({_results.Count} answer(s))");
    }

    private async Task TeleportChecks()
    {
        var npc = World.Find("NpcCommon").FirstOrDefault(e => World.Has(e, "TeleportCount"));
        Check(!npc.IsNone, $"a villager to teleport: {npc}");
        if (npc.IsNone || World.Position(npc) is not { } home) return;
        Gameplay.WatchTeleports(npc);
        await Steps(2);
        var hop = home with { X = home.X + 3 };

        // 1. allowed
        _teleports.Clear(); _teleported.Clear();
        var r1 = await Teleport.To(npc, hop);
        await Steps(2);
        var tp = _teleports.FirstOrDefault(t => t.Entity == npc);
        Check(tp is not null && tp.Target.DistanceTo(hop) < 0.01 && tp.From.DistanceTo(home) < 0.5,
            $"TeleportEvent before the move: from {tp?.From} to {tp?.Target}");
        Check(r1.Moved, $"teleport allowed: {r1}");
        var tpd = _teleported.FirstOrDefault(t => t.Entity == npc);
        Check(tpd is not null && tpd.To.DistanceTo(hop) < 0.5 && tpd.From.DistanceTo(home) < 0.5,
            $"TeleportedEvent after the move: from {tpd?.From} to {tpd?.To} (count {tpd?.Count})");

        // 2. canceled: stays at hop
        _teleports.Clear(); _teleported.Clear();
        _cancelTeleport = true;
        var t2 = Teleport.To(npc, home);
        await Steps(12);
        _cancelTeleport = false;
        var p2 = World.Position(npc);
        Check(_teleports.Any(t => t.Entity == npc && t.Canceled), "TeleportEvent seen and canceled");
        Check(p2 is { } q && q.DistanceTo(hop) < 0.5 && _teleported.All(t => t.Entity != npc), $"the canceled teleport did not move it ({p2})");
        var r2 = await t2;
        Log.Info($"gameplay-test: canceled teleport's kiln.world result: {r2}");

        // 3. redirected: asked for home, sent 2 m north of home instead
        _teleports.Clear(); _teleported.Clear();
        var elsewhere = home with { Z = home.Z + 2 };
        _redirect = elsewhere;
        var r3 = await Teleport.To(npc, home);
        _redirect = null;
        var p3 = World.Position(npc);
        Check(p3 is { } q3 && q3.DistanceTo(elsewhere) < 0.5, $"redirected teleport landed at {p3} (asked {home}, redirected to {elsewhere}; kiln.world: {r3})");

        await Teleport.To(npc, home);
        Gameplay.WatchTeleports(npc, false);
        Log.Info($"gameplay-test: villager {npc} back at {World.Position(npc)} (home {home})");
    }
}
