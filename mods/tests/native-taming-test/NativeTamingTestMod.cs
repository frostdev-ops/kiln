// SPDX-License-Identifier: MIT
// Lab probe of the game's own taming (docs/re/taming.md), server-side and player-free. What the static reading says
// (wildlife program 0x21f880, its behavior 0x21e8b0 and handlers): a Tameable wildlife senses a bait entity of its
// favourite-food template within foodBaitSensorRange, pursues it (state 14) and eats it (state 15) for at most
// foodBaitEatMaxDuration; eating alone ends with the bait destroyed and hasFullyAtenFoodBait set but NO progress; a step
// is the TameWildlifeEvent of the animal's synced pet sequence (Enemy_Wildlife_Pet_Taming, the player's petting while it
// eats): the bait is consumed and tamingProgress + 1 (at most necessaryTamingSteps); at the threshold the farm
// transformer sets faction FarmAnimal and adds IsTame; a startle costs progress by the world setting
// tamingStartleRepercussion. This probe checks each of these with the server playing the animal's own pet sequence
// (Abilities.PlaySequence) instead of a player's petting. Lines "native-taming: PASS/FAIL/OBS ..." and
// "native-taming done: N PASS, M FAIL" in kiln.log. The one raw write is step F's (a lab dog's progress, as the dossier's
// probe did), after the natural flow; with no player near, the wildlife program does not run (lab 11-27), so A to E need
// a signed-in player (LABBOT) to show the bait, the eating and the petting.
using System.Buffers.Binary;
using Kiln;

namespace NativeTamingTest;

public sealed class NativeTamingTestMod : Mod
{
    private const string Dog = "Animal_Wildlife_Dog_05_white_AG2";
    private const string DogBait = "FoodBait_T2_Dog";
    private const string CatBait = "FoodBait_T2_Cat";
    private static readonly Guid PetTaming = Guid.Parse("09ad9f39-f2b6-4051-8618-f444961bf6c1");  // Enemy_Wildlife_Pet_Taming
    private static readonly Guid DogBaitGuid = Guid.Parse("21eb90be-0ff2-477d-a804-dee22e9a2623");

    private int _pass, _fail;
    private bool _stop, _ran;
    private readonly List<EntityId> _spawned = new();

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"native-taming: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Obs(string what) => Log.Info($"native-taming: OBS {what}");

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
        Context.Tick.Pre += (in TickInfo t) =>
        {
            if (_ran || t.Step % 25 != 0 || !Lifecycle.IsServerRunning || !Spawn.WorldRunning) return;
            _ran = true;
            _ = Run();
        };
    }

    // ---- reads ----
    private readonly record struct Wild(byte State, byte Last, long EatTimer, long T70, long T78, uint Bait, bool Sensed, bool Ate, byte Progress)
    {
        public override string ToString() => $"state {State} (last {Last}) bait {Bait} sensed {Sensed} ate {Ate} progress {Progress} eatTimer {EatTimer} t70 {T70} t78 {T78}";
    }

    private static Wild? ReadWild(EntityId e)
    {
        var b = World.ReadBytes(e, "DynamicWildlife");
        if (b is null || b.Length < 0x90) return null;
        return new Wild(b[0], b[1], BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x60)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x70)),
            BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(0x78)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x88)), b[0x8c] != 0, b[0x8d] != 0, b[0x8e]);
    }

    private static string Net(EntityId e) =>
        World.ReadBytes(e, "NetworkWildlife") is { Length: >= 0x12 } n ? $"net alert {n[0]} progress {n[0x10]} eating {n[0x11]}" : "no NetworkWildlife";

    private static string Offer(EntityId e) =>
        World.ReadBytes(e, "InteractionOfferRuntime") is { Length: >= 0x18 } o
            ? $"offer default seq {BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(8)):x8} verb {BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(0x10)):x8} override seq {BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(0xc)):x8} verb {BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(0x14)):x8}"
            : "no InteractionOfferRuntime";

    private async Task<EntityId> Make(string template, WorldPosition at)
    {
        var e = await Spawn.Entity(template, at, options: new SpawnOptions { Flags = SpawnFlags.Transient });
        if (!e.IsNone) _spawned.Add(e);
        return e;
    }

    private void Drop(EntityId e)
    {
        if (e.IsNone) return;
        if (World.Exists(e)) Spawn.Destroy(e);
        _spawned.Remove(e);
    }

    // Polls until `until` holds or `steps` pass, logging every change of the dog's taming state.
    private async Task<Wild?> Watch(EntityId dog, int steps, Func<Wild, bool> until, string label)
    {
        Wild? last = null;
        for (var i = 0; i < steps && !_stop; i++)
        {
            var w = ReadWild(dog);
            if (w is { } now && now != last)
            {
                Obs($"{label} step+{i}: {now}; {Net(dog)}");
                last = now;
            }
            if (w is { } x && until(x)) return x;
            await Steps(1);
        }
        return last;
    }

    private async Task Run()
    {
        try
        {
            await Steps(25 * 4);
            // a player near (a lab bot): the wildlife program runs only with one (lab 11-27); the probe works next to it
            Player? bot = null;
            for (var i = 0; i < 25 * 120 && !_stop && bot is null; i++)
            {
                bot = Players.All.FirstOrDefault(p => p.InGame && p.Position is not null);
                if (bot is null) await Steps(1);
            }
            var a = bot?.Position is { } bp ? bp with { X = bp.X + 4 } : new WorldPosition(3865, 833, 1434);
            Obs(bot is null ? "no player in game after 120 s: the natural flow cannot run (section 6)" : $"player {bot.Name} (entity {bot.Entity}) at {bot.Position}: working 4 m from it");
            var dog = await Make(Dog, a);
            await Steps(12);
            if (dog.IsNone)
            {
                Check(false, $"a wild {Dog} spawned");
                return;
            }
            // what the entity holds: per-entity components and their sizes (the Wildlife template data is shared, the
            // entity's Wildlife component may only refer to it), then the template's taming setup by reflection path
            foreach (var c in new[] { "Wildlife", "DynamicWildlife", "NetworkWildlife", "IsTame", "Animal", "FoodBait", "InteractionOfferRuntime", "AnimalSaved" })
                Obs($"component {c}: has {World.Has(dog, c)}, size {World.ComponentSize(dog, c)}{(World.ReadBytes(dog, c) is { Length: > 0 and <= 64 } raw ? $", bytes {Convert.ToHexString(raw)}" : "")}");
            Check(World.Has(dog, "DynamicWildlife") && World.ComponentSize(dog, "DynamicWildlife") == 0xb0 && !World.Has(dog, "IsTame"),
                $"a wild {Dog} (entity {dog}), not tame, with its 176-byte DynamicWildlife");
            var fields = new Dictionary<string, string>();
            foreach (var f in new[] { "enabledFeatures", "tamingSetup.favoriteFood", "tamingSetup.necessaryTamingSteps", "tamingSetup.foodBaitSensorRange",
                         "tamingSetup.foodBaitEatMaxDuration", "tamingSetup.isWildlifeBehaviorActiveWhileTamed", "tamingPetInteractionSequence", "tamingPetInteractionVerb" })
            {
                var v = TryGet(() => Creatures.Of(dog).Ai.Get($"Wildlife.{f}", out var priv) + (priv ? " (private)" : " (template)"));
                fields[f] = v;
                Obs($"Wildlife.{f} = {v}");
            }
            var steps = int.TryParse(fields["tamingSetup.necessaryTamingSteps"].Split(' ')[0], out var st) ? st : 2;
            Check(fields["enabledFeatures"].Contains("Tameable") && steps == 2 && fields["tamingSetup.foodBaitSensorRange"].StartsWith("8.5"),
                $"the template's taming setup by reflection: Tameable, 2 steps, 8.5 m ({fields["enabledFeatures"]}; {steps}; {fields["tamingSetup.foodBaitSensorRange"]})");
            Check(fields["tamingSetup.favoriteFood"].Contains(DogBaitGuid.ToString(), StringComparison.OrdinalIgnoreCase),
                $"the favourite food is {DogBait}'s template ({DogBaitGuid}): {fields["tamingSetup.favoriteFood"]}");
            Obs($"startle setting: {TryGet(() => Settings.Get("tamingStartleRepercussion")?.ToString())}; {Offer(dog)}");
            Obs($"rows: {string.Join("; ", new[] { "taming.native", "kiln.taming", "taming.steps", "taming.native_observe" }.Select(r => $"{r} {Capabilities.Get(r).State} {Capabilities.Get(r).ReasonText}"))}");
            var read = TryGet(() => NativeTaming.Read(dog)?.ToString());
            Check(TryRead(dog) is { Tameable: true, Required: 2, Phase: NativeTamingPhase.Wild } r && r.BaitTemplate == DogBaitGuid && Math.Abs(r.SensorRangeMeters - 8.5) < 1e-6 && Math.Abs(r.EatSeconds - 16) < 1e-6,
                $"NativeTaming.Read of the wild dog (its template's setup by reflection): {read}");
            var p0 = World.Position(dog);
            var d0 = World.ReadBytes(dog, "DynamicWildlife") is { } raw0 ? Convert.ToHexString(raw0) : "-";


            // A: another animal's bait is not sensed
            var cat = await Make(CatBait, a with { X = a.X + 2.5f });
            var w = await Watch(dog, 25 * 6, x => x.Bait != 0, "cat bait");
            Check(w is { Bait: 0 } && w.Value.State is not (14 or 15), $"{CatBait} 2.5 m away is not sensed in 6 s: {w}");
            var p1 = World.Position(dog);
            var d1 = World.ReadBytes(dog, "DynamicWildlife") is { } raw1 ? Convert.ToHexString(raw1) : "-";
            Obs($"is the dog simulated with no player near? position {p0} -> {p1} over 6 s; DynamicWildlife {(d0 == d1 ? "unchanged" : "changed")}: {d1}");
            Obs($"the cat bait: FoodBait {World.Has(cat, "FoodBait")} size {World.ComponentSize(cat, "FoodBait")}, PickupItem {World.Has(cat, "PickupItem")}, DoNotSave {World.Has(cat, "DoNotSave")}");
            Drop(cat);
            await Steps(10);

            // B: its own bait, no petting: pursue, eat, the bait is gone, no progress
            var bait = await Make(DogBait, a with { X = a.X + 2.5f });
            w = await Watch(dog, 25 * 8, x => x.Bait != 0 && x.State == 15, "dog bait");
            Check(w is { Bait: var b0 } && b0 == bait.Value && w.Value.State == 15, $"its own bait (entity {bait}) is sensed and eaten (state 15): {w}");
            Obs($"while eating: {Offer(dog)}; {Net(dog)}");
            w = await Watch(dog, 25 * 25, x => x.Bait == 0 && x.State != 15, "eating");
            await Steps(2);
            Check(w is { Progress: 0 } && !World.Exists(bait), $"eaten to the end without petting: the bait is gone and progress stays 0: {w}; bait exists {World.Exists(bait)}");
            Drop(bait);
            await Steps(25 * 7);  // the game's pause after a fully eaten bait (6 s, static)

            // C: its own bait, and the animal's own pet sequence while it eats: one step
            for (var step = 1; step <= steps; step++)
            {
                bait = await Make(DogBait, a with { X = a.X + 2.5f + step });
                w = await Watch(dog, 25 * 10, x => x.Bait != 0 && x.State == 15, $"bait {step}");
                if (w is not { State: 15 })
                {
                    Check(false, $"step {step}: the dog did not start eating bait {bait}: {w}");
                    break;
                }
                Pet(dog, $"step {step}");
                w = await Watch(dog, 25 * 8, x => x.Progress >= step, $"pet {step}");
                await Steps(2);
                Check(w is { } got && got.Progress == step && !World.Exists(bait), $"step {step}: the pet sequence's TameWildlifeEvent while eating: progress {step}, the bait consumed: {w}; bait exists {World.Exists(bait)}");
                Drop(bait);
                await Steps(25 * 7);
            }
            await Steps(25);
            var faction = TryGet(() => Creatures.Of(dog).Faction.ToString());
            Check(World.Has(dog, "IsTame") && faction == "FarmAnimal", $"at {steps} steps the farm transformer tamed it: IsTame {World.Has(dog, "IsTame")}, faction {faction}; {ReadWild(dog)}; {Offer(dog)}");

            // D: the pet sequence without a bait being eaten counts nothing
            var dog2 = await Make(Dog, a with { Z = a.Z + 12 });
            await Steps(12);
            Pet(dog2, "not eating");
            await Steps(25 * 6);
            Check(ReadWild(dog2) is { Progress: 0 }, $"the pet sequence while not eating counts nothing: {ReadWild(dog2)}");

            // E: a startle (a hit) after one step, with the world's setting
            var bait2 = await Make(DogBait, a with { X = a.X + 2.5f, Z = a.Z + 12 });
            w = await Watch(dog2, 25 * 10, x => x.State == 15, "startle bait");
            if (w is { State: 15 })
            {
                Pet(dog2, "startle");
                w = await Watch(dog2, 25 * 8, x => x.Progress >= 1, "startle step");
                Check(w is { Progress: 1 }, $"one step before the startle: {w}");
                var bait3 = await Make(DogBait, a with { X = a.X + 3.5f, Z = a.Z + 12 });
                await Watch(dog2, 25 * 10, x => x.State == 15, "startle eat");
                var hit = await Combat.Damage(dog2, 1);
                Obs($"hit while eating: {hit}");
                w = await Watch(dog2, 25 * 6, x => x.Progress != 1, "after hit");
                Obs($"after the startle (setting {TryGet(() => Settings.Get("tamingStartleRepercussion")?.ToString())}): {w}; faction {TryGet(() => Creatures.Of(dog2).Faction.ToString())}");
                Drop(bait3);
            }
            Drop(bait2);

            // F: the farm transformation with no player near (the dossier's injection, lab only): a fresh dog's progress is
            // written to its required steps (DynamicWildlife is the server's working state: neither saved nor sent); the
            // farm transformer, which runs with no player near, makes it a farm animal; Kiln's watch reports both
            var seen = new List<string>();
            var dog3 = await Make(Dog, a with { Z = a.Z - 12 });
            using var onProgress = Events.On<NativeTamingProgressEvent>(e => { if (e.Creature == dog3) seen.Add(e.ToString()); });
            using var onTamed = Events.On<NativeTamedEvent>(e => { if (e.Creature == dog3) seen.Add(e.ToString()); });
            await Steps(75);   // the watch lists wild animals every 50 steps and sees each once before it reports a change
            var wrote = World.WriteRaw(dog3, "DynamicWildlife", 0x8e, (byte)steps);
            await Steps(40);
            var after = TryRead(dog3);
            var faction3 = TryGet(() => Creatures.Of(dog3).Faction.ToString());
            Check(wrote && World.Has(dog3, "IsTame") && faction3 == "FarmAnimal" && after is { Phase: NativeTamingPhase.Tamed, Progress: var pr } && pr == steps,
                $"progress written to {steps} with no player near: the farm transformer tamed it (IsTame {World.Has(dog3, "IsTame")}, faction {faction3}); NativeTaming.Read: {after}");
            Check(seen.Any(x => x.Contains($"taming 0 -> {steps}")) && seen.Any(x => x.Contains("tamed after")),
                $"Kiln's watch reported the progress and the tame: {string.Join(" | ", seen)}");
            Obs($"PetStep on the tamed dog: {TryGet(() => NativeTaming.PetStep(dog3).ToString())}; on the other wild dog: {TryGet(() => NativeTaming.PetStep(dog2).ToString())}");

            if (bot is not null) await KilnSteps(bot, a);
        }
        catch (Exception e)
        {
            Check(false, $"the probe threw: {e}");
        }
        finally
        {
            Log.Info($"native-taming done: {_pass} PASS, {_fail} FAIL");
        }
    }

    // the animal's own pet sequence as NativeTaming.PetStep plays it (abortCurrent false: the eating goes on); only when that
    // is refused, again aborting what runs, so the log says which one the game needs
    private bool Pet(EntityId dog, string label)
    {
        var played = Abilities.PlaySequence(dog, PetTaming, abortCurrent: false);
        Obs($"{label}: PlaySequence(Enemy_Wildlife_Pet_Taming, abortCurrent false) {played}; sequence now {Abilities.SequenceOf(dog)}");
        if (played) return true;
        played = Abilities.PlaySequence(dog, PetTaming, abortCurrent: true);
        Obs($"{label}: PlaySequence(Enemy_Wildlife_Pet_Taming, abortCurrent true) {played}; sequence now {Abilities.SequenceOf(dog)}");
        return played;
    }

    // G: Kiln's own steps end to end, the bot as the tamer: a weaken and a feed of 2 raw lean meat from the bot's inventory
    // (kiln.item_costs), bound to a Kiln.Pets record (its summon is off: the pets review fixes summon); then an escrow
    // attempt fed once and canceled gives its meat back
    private async Task KilnSteps(Player bot, WorldPosition near)
    {
        const string Wolf = "Enemy_Wildbeast_Wolf_earlygame";
        var who = PlayerRef.Of(bot.Entity);
        var meat = Items.Lookup("Food_T1_raw_bad_meat_LeanMeat")!.Id;
        int Meat() => Items.Read(bot.Entity)?.Slots.Where(s => s.Item == meat).Sum(s => s.Count) ?? 0;
        var give = await Items.Give(bot.Entity, meat, 3);
        Check(give.Status == ItemsStatus.Done && Meat() >= 3, $"the bot holds raw lean meat: {Meat()} ({give.Status})");
        var petDef = Pets.Define(new PetDefinition { Id = "native-taming-test:wolf-pet", Creature = "game:enemy_wildbeast_wolf_earlygame" });
        Obs($"pet definition: {petDef}");
        Taming.Define(new TamingDefinition
        {
            Id = "native-taming-test:wolf", Creatures = ["game:enemy_wildbeast_wolf_earlygame"], Steps = [new WeakenStep(1), new FeedStep(meat, 2)],
            Pet = "native-taming-test:wolf-pet", SummonPet = false, RangeMeters = 8,
        });
        Taming.Define(new TamingDefinition
        {
            Id = "native-taming-test:escrow", Creatures = ["game:enemy_wildbeast_wolf_earlygame"], Steps = [new FeedStep(meat, 1), new FeedStep(meat, 1)],
            Refund = TamingRefundPolicy.Escrow, RangeMeters = 8,
        });
        var events = new List<string>();
        using var onTamed = Events.On<TamedEvent>(e => events.Add(e.ToString()));
        using var onStep = Events.On<TamingStepChangedEvent>(e => events.Add(e.ToString()));
        using var onCancel = Events.On<TamingCanceledEvent>(e => events.Add($"{e} refunds {e.After.Refunds.Count}"));

        var wolf = await Make(Wolf, bot.Position!.Value with { X = bot.Position.Value.X + 2.5f });
        await Steps(10);
        TryGet(() => { Creatures.Of(wolf).Faction = Faction.Wildlife; return null; });   // a lab wolf that does not attack the bot
        var before = Meat();
        var begin = await Taming.Begin(who, EntityRef.Of(wolf), "native-taming-test:wolf", TamingRequest.New());
        Check(begin.Ok, $"Begin with the bot as the tamer: {begin.Reason} {begin.Progress?.State}");
        if (!begin.Ok) return;
        var p = begin.Progress!;
        var weaken = await Taming.Advance(who, p.Id, TamingRequest.New(p.Token));
        Check(weaken is { Ok: true, StepApplied: true }, $"the weaken step (health at most 100 %) applies with no cost: {weaken.Reason} {weaken.Progress?.CompletedSteps}");
        p = weaken.Progress!;
        var choices = Taming.Choices(who, p.Id);
        Check(choices.Ok && choices.Choices.Count > 0, $"Choices lists the bot's meat: {string.Join(", ", choices.Choices.Select(c => c.Label))}");
        if (choices.Choices.Count == 0) return;
        var feed = await Taming.Advance(who, p.Id, TamingRequest.New(choices.Token), choices.Choices[0].Selection);
        await Steps(3);
        var pets = TryGet(() => string.Join(", ", Pets.Of(CharacterData.KeyOf(bot.Entity)).Select(x => $"{x.Id} {x.Definition} {x.Life}")));
        Check(feed is { Ok: true, Pet: { } pet } && feed.Progress!.State == TamingState.Committed,
            $"the last step tamed it and bound a Kiln.Pets record: {feed.Reason} {feed.Progress?.State} pet {feed.Pet?.Id}; the bot's pets: {pets}");
        Check(feed.Receipts.SingleOrDefault() is { Committed: true, Taken: 2, NativeTicket: > 0 } && Meat() == before - 2,
            $"the feed took exactly 2 meat from the selected slot ({before} -> {Meat()}): {feed.Receipts.SingleOrDefault()}");
        Check(!World.Exists(wolf), $"the tamed wild body was removed (RemoveTarget): exists {World.Exists(wolf)}");

        var wolf2 = await Make(Wolf, bot.Position!.Value with { Z = bot.Position.Value.Z + 2.5f });
        await Steps(10);
        TryGet(() => { Creatures.Of(wolf2).Faction = Faction.Wildlife; return null; });
        var held = Meat();
        var b2 = await Taming.Begin(who, EntityRef.Of(wolf2), "native-taming-test:escrow", TamingRequest.New());
        Check(b2.Ok, $"an escrow attempt begins: {b2.Reason}");
        if (!b2.Ok) return;
        var q = b2.Progress!;
        var c2 = Taming.Choices(who, q.Id);
        var fed = c2.Choices.Count > 0 ? await Taming.Advance(who, q.Id, TamingRequest.New(c2.Token), c2.Choices[0].Selection) : null;
        var spent = Meat();
        q = fed?.Progress ?? q;
        var cancel = await Taming.Cancel(who, q.Id, TamingRequest.New(q.Token));
        for (var i = 0; i < 10 && Meat() != held; i++) await Steps(1);
        var refund = cancel.Progress?.Refunds.SingleOrDefault();
        Check(fed is { StepApplied: true } && spent == held - 1 && cancel.Ok && refund?.Refund is { Committed: true, Taken: 1 } && Meat() == held,
            $"escrow: one meat paid ({held} -> {spent}), the cancel gave it back ({Meat()}): {cancel.Reason}; refund {refund?.Refund}");
        Obs($"taming events: {string.Join(" | ", events)}");
        Drop(wolf2);
    }

    private static NativeTamingState? TryRead(EntityId e)
    {
        try { return NativeTaming.Read(e); }
        catch (Exception x) when (x is KilnUnavailableException or InvalidOperationException) { return null; }
    }

    private static string TryGet(Func<string?> f)
    {
        try { return f() ?? "null"; }
        catch (Exception e) { return $"({e.GetType().Name}: {e.Message})"; }
    }
}
