// SPDX-License-Identifier: MIT
// The revive-cancel sweep: does a cancel that lands while a revive's cost is being taken come back with its receipt?
// Kiln.Pets takes the cost (Items.Take, which settles in a later step) once the revive's inert preview body is ready, and a
// CancelRevive is started at the next step. So the sweep starts a revive of this mod's mule (0.5 s channel, one twig) and
// calls CancelRevive k steps after the preview body was set up (its Faction change, as this mod: the mule's definer),
// for k = 0, 1, 2, ...: too early answers CanceledNoCost (nothing taken), too late AlreadyCompleted (revived, one twig
// taken); in between the revive answers CanceledCostCommitted with the receipt and the cancel carries the same receipt.
// Every attempt records both answers, their receipts and the twigs before and after. PetsSample refuses a revive within
// 30 s of the owner's last (its PetRevivingEvent rule, for every pet of the owner), so after a completed revive it waits.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsE2eLab;

public sealed partial class PetsE2eLabMod
{
    [Command("e2e sweep", Description = "the revive-cancel sweep with this mod's mule (attacker: a creature for the lethal hits)", Permission = CommandPermission.Admin)]
    private void SweepCommand(CommandContext c, string attacker, int max)
    {
        var (who, key) = Caller(c);
        var from = Entity(attacker);
        Later("sweep", () => Sweep(who, key, from, max));
        c.Reply("{}");
    }

    private async Task Sweep(EntityId who, CharacterKey key, EntityId attacker, int max)
    {
        try { await SweepAttempts(who, key, attacker, max); }
        finally { Write("sweepdone", new JsonObject()); }
    }

    private async Task SweepAttempts(EntityId who, CharacterKey key, EntityId attacker, int max)
    {
        var owner = PlayerRef.Of(who);
        var twigs = Items.Lookup("Material_T1_Twigs") ?? throw new InvalidOperationException("no twigs");
        int Count() => Items.Read(who)?.Count(twigs.Id) ?? -1;
        if (!_muleDefined) DefineMule();
        var mule = Pets.Of(key).FirstOrDefault(p => p.Definition == Mule && p.Life != PetLife.Released);
        if (mule is null)
        {
            var g = await Pets.Grant(key, Mule, PetRequest.New());
            Write("sweepstep", new JsonObject { ["step"] = "grant", ["result"] = Result(g) });
            if (!g.Ok || g.Pet is null) return;
            mule = g.Pet;
        }
        var id = mule.Id;
        var wall = Stopwatch.StartNew();
        var lastRevived = double.NegativeInfinity;
        var k = 0;
        int? early = null, late = null;
        var found = 0;
        for (var attempt = 1; attempt <= max; attempt++)
        {
            var p = Pets.Get(id);
            if (p is null) return;
            if (p.Life == PetLife.Ready && p.Presence != PetPresence.Live)
            {
                var s = await Pets.Summon(owner, id, PetRequest.New());
                Write("sweepstep", new JsonObject { ["step"] = "summon", ["attempt"] = attempt, ["result"] = Result(s) });
                if (!s.Ok) return;
            }
            p = Pets.Get(id)!;
            if (p.Life == PetLife.Ready)
            {
                await Wait(1);
                p = Pets.Get(id)!;
                if (p.Body.IsNone) return;
                _ = Combat.Damage(p.Body.Id, 1_000_000, attacker);
                var down = await Until(() => Pets.Get(id) is { Life: PetLife.Downed, Presence: PetPresence.RecoveryAnchor, Transition: PetTransition.None }, 10);
                Write("sweepstep", new JsonObject { ["step"] = "down", ["attempt"] = attempt, ["ok"] = down, ["life"] = Pets.Get(id)?.Life.ToString() });
                if (!down) return;
            }
            // the anchor within reach (8 m): a downed pet's Summon puts it next to the owner when it is past 3 m
            if (Pets.Get(id)!.RecoveryAnchor is { } a && World.Position(who) is { } at && a.DistanceTo(at) > 6)
            {
                var s = await Pets.Summon(owner, id, PetRequest.New());
                Write("sweepstep", new JsonObject { ["step"] = "anchor", ["attempt"] = attempt, ["result"] = Result(s) });
            }
            var wait = lastRevived + 31 - wall.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Write("sweepstep", new JsonObject { ["step"] = "wait", ["attempt"] = attempt, ["seconds"] = R(wait) });
                var until = wall.Elapsed.TotalSeconds + wait;
                while (wall.Elapsed.TotalSeconds < until) await Steps(1);
            }
            var before = Count();
            _previewStep = null;
            _awaitPreview = false;
            var req = PetRequest.New();
            var revive = Pets.Revive(owner, id, req);
            await Until(() => _previewStep is not null || revive.IsCompleted, 15);
            Task<PetResult>? cancel = null;
            ulong? cancelStep = null;
            if (_previewStep is { } preview && !revive.IsCompleted)
            {
                while (_step < preview + (ulong)k && !revive.IsCompleted) await Steps(1);
                if (!revive.IsCompleted)
                {
                    cancelStep = _step;
                    cancel = Pets.CancelRevive(owner, id, req.OperationId, PetRequest.New());
                }
            }
            var rr = await revive;
            var cr = cancel is null ? null : await cancel;
            await Wait(1.5);
            var after = Count();
            if (rr.Ok) lastRevived = wall.Elapsed.TotalSeconds;
            var verdict = rr.Refusal switch
            {
                PetRefusal.None => "late",
                PetRefusal.CanceledCostCommitted => "during",
                PetRefusal.CanceledNoCost => "early",
                PetRefusal.RefusedByMod => "refused",
                _ => "other",
            };
            Write("sweep", new JsonObject
            {
                ["attempt"] = attempt, ["k"] = k, ["previewStep"] = _previewStep, ["cancelStep"] = cancelStep, ["verdict"] = verdict,
                ["revive"] = Result(rr), ["cancel"] = cr is null ? null : Result(cr), ["twigsBefore"] = before, ["twigsAfter"] = after,
                ["taken"] = before >= 0 && after >= 0 ? before - after : null,
            });
            switch (verdict)
            {
                case "during":
                    found++;
                    if (found >= 2) return;
                    break;
                case "early":
                    early = Math.Max(early ?? -1, k);
                    k++;
                    break;
                case "late":
                    late = Math.Min(late ?? int.MaxValue, k);
                    if (k == 0) return;
                    k--;
                    break;
                case "refused":
                    lastRevived = wall.Elapsed.TotalSeconds;
                    break;
                default:
                    return;
            }
            if (early is { } e && late is { } l && l == e + 1 && found == 0)
            {
                Write("sweepstep", new JsonObject { ["step"] = "bracketed", ["early"] = e, ["late"] = l });
                // once more at each side, then stop: a window of no step
                if (attempt >= 8) return;
            }
        }
    }
}
