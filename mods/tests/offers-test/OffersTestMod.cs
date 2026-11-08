// SPDX-License-Identifier: MIT
using Kiln;

namespace OffersTest;

/// <summary>
/// Kiln.Gameplay Offers on a lab server (no player): spawns a barrel, adds an offer, checks the three components are
/// there, refuses an offer on a game interactable, removes it and checks the components are gone. Prints
/// "offers-test: PASS|FAIL ..." lines and "offers-test: done, N PASS, M FAIL". The prompt and a real accept need a
/// player (client checklist).
/// </summary>
public sealed class OffersTestMod : Mod
{
    private static readonly string[] Components = ["InteractionOfferRuntime", "ClientInteractionOffer", "CheckPlayerInRange"];
    private int _pass, _fail;
    private readonly List<EntityId> _props = [];

    public override void OnLoad()
    {
        Log.Info("offers-test loaded");
        _ = RunAsync();
    }

    public override void OnUnload()
    {
        // Kiln takes this mod's offers off by itself; the props are ours to destroy.
        foreach (var p in _props) Spawn.Destroy(p);
        Log.Info($"offers-test: unloaded, destroyed {_props.Count} prop(s)");
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"offers-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task<bool> Until(Func<bool> f, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            if (f()) return true;
            await Steps(1);
        }
        return f();
    }

    private string Presence(EntityId e) => string.Join(" ", Components.Select(c => $"{c}={(World.Has(e, c) ? "yes" : "no")}"));

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
        Log.Info($"offers-test: done, {_pass} PASS, {_fail} FAIL");
    }

    private async Task Run()
    {
        for (var i = 0; i < 3000 && !(World.Available && World.EntityCount > 1500 && Spawn.WorldRunning); i++) await Steps(1);
        await Steps(100);
        Check(Offers.Available, "Offers.Available (kiln.gameplay component_change and kiln.world)");
        if (!Offers.Available) return;

        WorldPosition? anchor = null;
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
        {
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (Spawn.Inspect(id) is { } info)
                {
                    anchor = info.Position;
                    break;
                }
            if (anchor is not null) break;
        }
        Check(anchor is not null, $"an anchor to spawn near: {anchor}");
        if (anchor is null) return;
        var at = new WorldPosition(anchor.Value.X + 20, anchor.Value.Y + 0.5, anchor.Value.Z + 20);
        var barrel = Spawn.Lookup("Prop_Deco_Steppeprops_Barrel_01")?.Name ?? "Prop_Deco_Steppeprops_Barrel_01";
        var opts = new SpawnOptions { Tag = "offers-test", Flags = SpawnFlags.Transient };
        var prop = await Spawn.Entity(barrel, at, 0, opts);
        _props.Add(prop);
        await Steps(3);
        Log.Info($"offers-test: prop {prop} before: {Presence(prop)}");
        Check(!World.Has(prop, "InteractionOfferRuntime"), "the barrel has no offer of its own");
        var hadRange = World.Has(prop, "CheckPlayerInRange");  // the barrel template has one: Kiln must leave it

        // add
        var accepts = new List<OfferAccepted>();
        Offers.Add(prop, "Ring the bell", 0, a =>
        {
            accepts.Add(a);
            Log.Info($"offers-test: accepted by {a.Player} on {a.Entity}");
        }, new OfferOptions { RangeMeters = 6 });
        Check(Offers.IsActive(prop) && Offers.Status(prop) == OfferStatus.Pending, $"IsActive right after Add ({Offers.Status(prop)})");
        var active = await Until(() => Offers.Status(prop) == OfferStatus.Active, 30);
        Log.Info($"offers-test: prop {prop} after add: {Presence(prop)}");
        Check(active, $"the offer became Active ({Offers.Status(prop)})");
        Check(Components.All(c => World.Has(prop, c)), "all three offer components are on the prop");
        var rt = World.ReadBytes(prop, "InteractionOfferRuntime");
        var verb = rt is { Length: >= 20 } ? BitConverter.ToUInt32(rt, 16) : 0;
        Log.Info($"offers-test: InteractionOfferRuntime {(rt is null ? "none" : Convert.ToHexString(rt))}");
        Check(verb == Offers.CustomVerbId("Ring the bell"), $"defaultVerbId is the custom verb ({verb:x8})");
        var range = World.ReadBytes(prop, "CheckPlayerInRange");
        Check(range is { Length: >= 1 } && (hadRange || range[0] == 6), $"CheckPlayerInRange = 6 m ({(range is null ? "none" : range[0].ToString())})");
        Check(Offers.List().Any(o => o.Entity == prop && o.Owner == "offers-test"), "Offers.List shows it with its owner");

        // same offer again: only the handler; a different one is refused
        Check(Offers.TryAdd(prop, "Ring the bell", 0, _ => { }, out _, new OfferOptions { RangeMeters = 6 }), "the same offer again is fine");
        Check(!Offers.TryAdd(prop, null, OfferVerbs.Open, _ => { }, out var why1) && why1.Contains("remove it first"), $"a different offer is refused: {why1}");

        // a game interactable refuses
        var chest = World.Find("InteractionOfferRuntime").FirstOrDefault(e => !e.IsPlayer && e != prop);
        if (!chest.IsNone)
            Check(!Offers.TryAdd(chest, null, OfferVerbs.Use, _ => { }, out var why2) && why2.Contains("own offer"),
                $"an entity with the game's offer ({chest}) is refused: {why2}");

        // an injected accept (a station as guest, as gameplay-test does); the game may decline it without a player
        var offerId = rt is { Length: >= 0x2C } ? BitConverter.ToUInt32(World.ReadBytes(prop, "InteractionOfferRuntime")!, 0x28) : 0;
        var guest = World.Find("Crafting").FirstOrDefault(e => !e.IsPlayer);
        if (!guest.IsNone && offerId != 0)
        {
            Gameplay.Inject.Interact(guest, prop, offerId);
            await Steps(6);
            Log.Info($"offers-test: INFO injected accept (guest {guest}, offer {offerId}): handler ran {accepts.Count} time(s)");
        }
        else Log.Info($"offers-test: INFO no injected accept (guest {guest}, offer id {offerId}: the game sets it when it offers)");

        // remove
        Check(Offers.Remove(prop), "Remove returns true");
        Check(!Offers.IsActive(prop), $"not active after Remove ({Offers.Status(prop)})");
        var gone = await Until(() => Offers.Status(prop) == OfferStatus.None && !World.Has(prop, "InteractionOfferRuntime") &&
                                     !World.Has(prop, "ClientInteractionOffer") && World.Has(prop, "CheckPlayerInRange") == hadRange, 30);
        Log.Info($"offers-test: prop {prop} after remove: {Presence(prop)}");
        Check(gone, $"the components Kiln added are gone, the template's CheckPlayerInRange stays ({hadRange}), the offer is None");
        Check(!Offers.Remove(prop), "a second Remove returns false");

        // SpawnWithOffer, left in place: Kiln removes it when this mod unloads
        var second = await Offers.SpawnWithOffer(barrel, at with { X = at.X + 3 }, null, OfferVerbs.Inspect, _ => { }, null, opts);
        _props.Add(second);
        Check(await Until(() => Offers.Status(second) == OfferStatus.Active, 30), $"SpawnWithOffer: {second} is Active; {Presence(second)}");
    }
}
