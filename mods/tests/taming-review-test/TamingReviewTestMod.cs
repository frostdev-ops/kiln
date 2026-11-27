// SPDX-License-Identifier: MIT
// Lab probe of the dev.32 TAME review, with a LABBOT bot player as the tamer (it needs a signed-in player: Kiln.Players'
// session, an inventory, a character for the pet record). Lines "taming-review: PASS/FAIL/OBS ..." and
// "taming-review done: N PASS, M FAIL" in kiln.log:
//   1. a taming defined in OnLoad, before any world (Taming.Define needs only kiln.taming);
//   2. the tamings of blocks/tamings.json (TamingBlock) are defined, their feed item resolved to the game's item;
//   3. NativeTaming.PlaceBait refuses (taming.native_bait off) and spawns nothing;
//   4. Kiln's steps end to end on a wolf (the block's taming): a weaken, a feed of 2 from the selected slot, the tame bound
//      to a pet record, the wild body removed, and the pet SUMMONED (a live body near the bot: the pets summon fix);
//   5. an escrow attempt fed once and canceled gives its meat back once; ItemCosts.Refund of the same paid receipt under a
//      new request id is AlreadyRefunded and gives nothing more;
//   6. a roster that fills during an attempt refuses the last step before its cost (nothing taken).
using Kiln;

namespace TamingReviewTest;

public sealed class TamingReviewTestMod : Mod
{
    private const string Wolf = "Enemy_Wildbeast_Wolf_earlygame";
    private int _pass, _fail;
    private bool _stop, _ran;
    private string _onLoad = "not run";
    private readonly List<EntityId> _spawned = new();

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"taming-review: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Obs(string what) => Log.Info($"taming-review: OBS {what}");

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    public override void OnUnload()
    {
        _stop = true;
        foreach (var e in _spawned) Spawn.Destroy(e);
    }

    public override void OnLoad()
    {
        // 1: a definition is data: no world runs yet
        try
        {
            Taming.Define(new TamingDefinition
            {
                Id = "taming-review-test:onload", Creatures = ["game:enemy_wildbeast_wolf_earlygame"], Steps = [new WeakenStep(1)],
            });
            _onLoad = $"defined (world running: {Lifecycle.IsServerRunning}, taming.steps {Capabilities.Get(Taming.Capability).State})";
        }
        catch (Exception e)
        {
            _onLoad = $"threw {e.GetType().Name}: {e.Message} (world running: {Lifecycle.IsServerRunning})";
        }
        Context.Tick.Pre += (in TickInfo t) =>
        {
            if (_ran || t.Step % 25 != 0 || !Lifecycle.IsServerRunning || !Spawn.WorldRunning) return;
            _ran = true;
            _ = Run();
        };
    }

    private async Task<EntityId> Make(string template, WorldPosition at)
    {
        var e = await Spawn.Entity(template, at, options: new SpawnOptions { Flags = SpawnFlags.Transient });
        if (!e.IsNone) _spawned.Add(e);
        await Steps(10);
        TryGet(() => { Creatures.Of(e).Faction = Faction.Wildlife; return null; });   // a lab wolf that does not attack the bot
        return e;
    }

    private static TamingToken Token(TamingProgress p) => new(Guid.NewGuid(), p.StepChallenge, p.Revision, p.CompletedSteps);

    private async Task Run()
    {
        try
        {
            Check(_onLoad.StartsWith("defined", StringComparison.Ordinal), $"a taming defined in OnLoad, before any world: {_onLoad}");
            await Steps(25 * 4);
            Player? bot = null;
            for (var i = 0; i < 25 * 120 && !_stop && bot is null; i++)
            {
                bot = Players.All.FirstOrDefault(p => p.InGame && p.Position is not null);
                if (bot is null) await Steps(1);
            }
            if (bot is null)
            {
                Check(false, "a bot player in game within 120 s");
                return;
            }
            Obs($"player {bot.Name} (entity {bot.Entity}) at {bot.Position}");
            var who = PlayerRef.Of(bot.Entity);
            var meat = Items.Lookup("Food_T1_raw_bad_meat_LeanMeat")!.Id;
            int Meat() => Items.Read(bot.Entity)?.Slots.Where(s => s.Item == meat).Sum(s => s.Count) ?? 0;
            var character = CharacterData.KeyOf(bot.Entity);

            // 2: the block tamings, their item resolved
            for (var i = 0; i < 25 * 10 && Taming.Definition("taming-review-test:block-wolf") is null; i++) await Steps(1);
            var blockWolf = Taming.Definition("taming-review-test:block-wolf");
            Check(blockWolf is { Pet: "taming-review-test:block-wolf-pet" } && blockWolf.Steps.Count == 2 && blockWolf.Steps[1] is FeedStep { Count: 2 } f && f.Item == meat,
                $"the TamingBlock of blocks/tamings.json is defined with the game's item: {(blockWolf is null ? "none" : $"{blockWolf.Id} pet {blockWolf.Pet}, steps {string.Join(", ", blockWolf.Steps)}")}");
            Check(Taming.Definition("taming-review-test:block-escrow") is { Refund: TamingRefundPolicy.Escrow }, "the escrow TamingBlock is defined");

            // 3: PlaceBait refused, nothing spawned
            var dog = await Make("Animal_Wildlife_Dog_05_white_AG2", bot.Position!.Value with { Z = bot.Position.Value.Z - 6 });
            var bait = "not thrown";
            try { bait = (await NativeTaming.PlaceBait(dog)).ToString(); }
            catch (KilnUnavailableException e) { bait = $"KilnUnavailableException {e.Capability}"; }
            Check(bait == $"KilnUnavailableException {NativeTaming.BaitCapability}" && Capabilities.Get(NativeTaming.BaitCapability).State != CapabilityState.Available,
                $"NativeTaming.PlaceBait refuses loudly: {bait}; row {Capabilities.Get(NativeTaming.BaitCapability).State} {Capabilities.Get(NativeTaming.BaitCapability).ReasonText}");

            // 4: Kiln's steps end to end, the pet summoned
            var give = await Items.Give(bot.Entity, meat, 6);
            Check(give.Status == ItemsStatus.Done && Meat() >= 6, $"the bot holds raw lean meat: {Meat()} ({give.Status})");
            var wolf = await Make(Wolf, bot.Position!.Value with { X = bot.Position.Value.X + 2.5f });
            var before = Meat();
            var begin = Taming.Begin(who, EntityRef.Of(wolf), "taming-review-test:block-wolf", Guid.NewGuid());
            Check(begin.Ok, $"Begin on a wolf with the bot as the tamer: {begin.Reason} {begin.Progress?.State}");
            if (!begin.Ok) return;
            var p = begin.Progress!;
            var weaken = await Taming.Advance(who, p.Id, Token(p));
            Check(weaken is { Ok: true, StepApplied: true }, $"the weaken step applies with no cost: {weaken.Reason}");
            p = weaken.Progress!;
            var choices = Taming.Choices(who, p.Id);
            if (choices.Choices.Count == 0)
            {
                Check(false, $"Choices lists the bot's meat: {choices.Reason}");
                return;
            }
            var feed = await Taming.Advance(who, p.Id, new TamingToken(Guid.NewGuid(), choices.StepChallenge, choices.Revision, p.CompletedSteps), choices.Choices[0].Selection);
            Check(feed is { Ok: true, Pet: not null } && feed.Progress!.State == TamingState.Committed && Meat() == before - 2,
                $"the last step tamed it, took exactly 2 meat ({before} -> {Meat()}) and bound pet {feed.Pet?.Id}: {feed.Reason}");
            PetSnapshot? live = null;
            for (var i = 0; i < 25 * 15 && live is null; i++)
            {
                live = Pets.Of(character).FirstOrDefault(x => x.Id == feed.Pet?.Id && x.Presence == PetPresence.Live && !x.Body.IsNone && World.Exists(x.Body.Id));
                if (live is null) await Steps(1);
            }
            var now = Pets.Of(character).FirstOrDefault(x => x.Id == feed.Pet?.Id);
            var dist = live is not null && World.Position(live.Body.Id) is { } bp && bot.Position is { } op ? bp.DistanceTo(op) : -1;
            Check(live is not null, $"SummonPet after the tame: the pet has a live body near its owner ({dist:0.0} m): {now?.Presence} {now?.Transition} body {now?.Body}");
            Check(!World.Exists(wolf), $"the tamed wild body was removed: exists {World.Exists(wolf)}");

            // 5: escrow given back once
            var wolf2 = await Make(Wolf, bot.Position!.Value with { Z = bot.Position.Value.Z + 2.5f });
            var held = Meat();
            var b2 = Taming.Begin(who, EntityRef.Of(wolf2), "taming-review-test:block-escrow", Guid.NewGuid());
            Check(b2.Ok, $"an escrow attempt begins: {b2.Reason}");
            if (!b2.Ok) return;
            var q = b2.Progress!;
            var c2 = Taming.Choices(who, q.Id);
            var fed = c2.Choices.Count > 0 ? await Taming.Advance(who, q.Id, new TamingToken(Guid.NewGuid(), c2.StepChallenge, c2.Revision, 0), c2.Choices[0].Selection) : null;
            var spent = Meat();
            q = fed?.Progress ?? q;
            var cancel = await Taming.Cancel(who, q.Id, Token(q));
            for (var i = 0; i < 10 && Meat() != held; i++) await Steps(1);
            var refund = cancel.Progress?.Refunds.SingleOrDefault();
            Check(fed is { StepApplied: true } && spent == held - 1 && cancel.Ok && refund?.Refund is { Committed: true, Taken: 1 } && Meat() == held,
                $"escrow: one meat paid ({held} -> {spent}), the cancel gave it back ({Meat()}): refund {refund?.Refund}");
            if (refund is not null)
            {
                var again = await ItemCosts.Refund(refund.Paid, Guid.NewGuid());
                await Steps(5);
                Check(again is { Committed: false, Refusal: CostRefusal.AlreadyRefunded } && Meat() == held,
                    $"the same paid receipt under a new request id is never given back twice: {again.Refusal} {again.Reason}; meat {Meat()}");
            }

            // 6: a roster that fills during the attempt refuses the last step before its cost
            var cap = Pets.Define(new PetDefinition { Id = "taming-review-test:cap-pet", Creature = "game:enemy_wildbeast_wolf_earlygame", Caps = new PetCaps { RetainedPerCharacter = 1 } });
            Taming.Define(new TamingDefinition
            {
                Id = "taming-review-test:cap", Creatures = ["game:enemy_wildbeast_wolf_earlygame"], Steps = [new FeedStep(meat, 1), new FeedStep(meat, 1)],
                Pet = "taming-review-test:cap-pet", RangeMeters = 8,
            });
            var wolf3 = await Make(Wolf, bot.Position!.Value with { X = bot.Position.Value.X - 2.5f });
            var b3 = Taming.Begin(who, EntityRef.Of(wolf3), "taming-review-test:cap", Guid.NewGuid());
            Check(b3.Ok, $"a capped attempt begins (its roster slot held): {b3.Reason}; pet definition {cap.Ok}");
            if (!b3.Ok) return;
            var r = b3.Progress!;
            var c3 = Taming.Choices(who, r.Id);
            var first = c3.Choices.Count > 0 ? await Taming.Advance(who, r.Id, new TamingToken(Guid.NewGuid(), c3.StepChallenge, c3.Revision, 0), c3.Choices[0].Selection) : null;
            var grant = await Pets.Grant(character, "taming-review-test:cap-pet", PetRequest.New());   // the roster fills meanwhile
            r = first?.Progress ?? r;
            var c4 = Taming.Choices(who, r.Id);
            var beforeLast = Meat();
            var last = c4.Choices.Count > 0 ? await Taming.Advance(who, r.Id, new TamingToken(Guid.NewGuid(), c4.StepChallenge, c4.Revision, r.CompletedSteps), c4.Choices[0].Selection) : null;
            await Steps(3);
            Check(first is { StepApplied: true } && grant.Ok && last is { Ok: false, StepApplied: false } && last.Reason!.StartsWith("Cap", StringComparison.Ordinal) && last.Receipts.Count == 0 && Meat() == beforeLast,
                $"a roster filled during the attempt refuses the last step before its cost: {last?.Reason}; meat {beforeLast} -> {Meat()}; grant {grant.Ok} {grant.Reason}");
            var end = await Taming.Cancel(who, r.Id, Token(last?.Progress ?? r));
            Obs($"the capped attempt canceled: {end.Reason} {end.Progress?.State}");
        }
        catch (Exception e)
        {
            Check(false, $"the probe threw: {e}");
        }
        finally
        {
            Log.Info($"taming-review done: {_pass} PASS, {_fail} FAIL");
        }
    }

    private static string TryGet(Func<string?> f)
    {
        try { return f() ?? "null"; }
        catch (Exception e) { return $"({e.GetType().Name}: {e.Message})"; }
    }
}
