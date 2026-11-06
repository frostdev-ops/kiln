// SPDX-License-Identifier: MIT
// What Kiln.Blocks makes of furniture, interactions, shapes, hair and beards, on a dedicated server with no player: what the templates and items say, interactions through the game's
// own interaction code with a spawned character as the guest, C# and JSON the same, the errors a mistake gets, and what comes back after a restart in a base and outside one.
// Start it twice: the second run checks what the first one saved.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace FurnitureTest;

public sealed class FurnitureTestMod : Mod
{
    private const string Sample = "furniture-blocks", Me = "furniture-test";
    private const string Scraps = "Material_T1_Metal_Scraps", Twigs = "Material_T1_Twigs";
    private static readonly BuildOptions InBases = new() { Flags = BuildFlags.InBases };
    // keen::ecs::InteractionOfferRuntime and its neighbours on the builds Kiln knows (docs/re/gameplay.md 4)
    private const int OfferSequence = 0x08, OfferVerb = 0x10, OfferId = 0x28, OfferActive = 0x2d;
    private int _pass, _fail;
    private readonly List<EntityId> _placed = new(), _spawned = new();
    private readonly List<InteractEvent> _attempts = new();
    private readonly List<InteractionResultEvent> _results = new();
    private bool _allow = true;

    private void Info(string s) => Log.Info($"furniture-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"furniture-test: PASS {what}"); }
        else { _fail++; Log.Error($"furniture-test: FAIL {what}"); }
        return ok;
    }

    [Subscribe(ReceiveCanceled = true)] private void OnAttempt(InteractEvent e) => _attempts.Add(e);
    [Subscribe] private void OnResult(InteractionResultEvent e) => _results.Add(e);

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static string G(string block) => $"{Sample}.{block}";
    private static ContentEntry I(string item) => Content.Items.Get(item) ?? throw new InvalidOperationException($"no item {item}");
    private static bool Has(GameDataEntry t, string component)
    {
        try { return t.GetJson(component) != null; }
        catch (KeyNotFoundException) { return false; }
    }

    private static GameDataEntry T(string template) => Content.Templates.Get(template) ?? throw new InvalidOperationException($"no template {template}");

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        // what data files can't say: a check in code, and blocks the tests need beside the sample's
        Blocks.Define("glow", new StatusBlock { Name = "Glow", Seconds = 60 });
        Blocks.Define("door-wall", new BlueprintBlock
        {
            Name = "Door Wall",
            Cells = [["##....##"], ["##....##"], ["##....##"], ["##....##"], ["##....##"], ["########"], ["########"], ["########"]],
        });
        Blocks.Define("sleepers-only", new DoorBlock { Name = "Sleepers Only", OnInteract = new InteractBlock { Requires = new RequiresBlock { When = [PlayerStateId.Sleeping] }, OnRefused = [new EffectBlock { Heal = 1 }] } });
        Blocks.Define("coded-door", new DoorBlock
        {
            Name = "Coded Door",
            OnInteract = new InteractBlock { Requires = new RequiresBlock { If = _ => _allow }, Effects = [new EffectBlock { Applies = "glow" }] },
        });
        Blocks.Define("lever-chest", new ContainerBlock { Name = "Lever Chest", OnInteract = new InteractBlock { Verb = InteractVerb.Flip, Toggle = new ToggleBlock { StartsOn = true } } });
        Blocks.Define("key-door", new DoorBlock { Name = "Key Door", OnInteract = new InteractBlock { Requires = new RequiresBlock { Item = Twigs, Count = 2, Uses = true } } });
        Blocks.Define("bell-door", new DoorBlock { Name = "Bell Door", OnInteract = new InteractBlock { Prompt = "Ring the bell", Toggle = new ToggleBlock { OnPrompt = "Push", Off = InteractVerb.Sit } } });
        Blocks.Define("stocked-box", new ContainerBlock { Name = "Stocked Box", Holds = { [Twigs] = 5, [Scraps] = 2 } });
        Blocks.Define("high-bed", new BedBlock { Name = "High Bed", Lies = new SpotBlock { At = new(0.1f, 1.2f, -0.3f), Facing = 90 } });
        Blocks.Define("own-seat", new SeatBlock { Name = "Own Seat", Visual = new VisualBlock { Model = "item:Prop_Decoration_Comfort3_T2_Chair" }, Sits = new SpotBlock { At = new(0, 0.45f, 0.05f) } });
        Blocks.Define("plain-lamp", new PropBlock { Name = "Plain Lamp", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, OnInteract = new InteractBlock { Effects = [new EffectBlock { Heal = 1 }] } });
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!(Spawn.WorldRunning && Build.Available && Build.Grid is not null) && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            var saved = Path.Combine(Context.DataDirectory, "saved.json");
            if (File.Exists(saved))
            {
                await Part("after the restart", () => AfterRestart(saved));
                File.Delete(saved);
            }
            else
            {
                await Part("mistakes", () => { Mistakes(); return Task.CompletedTask; });
                await Part("registered", () => { Registered(); return Task.CompletedTask; });
                await Part("twins", () => { Twins(); return Task.CompletedTask; });
                await Part("shapes", () => { Shapes(); return Task.CompletedTask; });
                var at = BaseSpot();
                Info($"working next to a station of the world's base, at {at}");
                await Part("placed", () => Placed(at));
                await Part("interacting", () => Interacting(at));
                await Part("hair and beards", () => HairAndBeards(at));
                await Part("beds", () => Beds(at));
                await Part("leaving for a restart", () => Leave(at, saved));
            }
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"furniture-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Part(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception: {e}"); }
    }

    private static WorldPosition BaseSpot()
    {
        foreach (var e in World.Find("FactoryStation"))
            if (World.Position(e) is { } p) return p;
        throw new InvalidOperationException("this world has no station to stand next to");
    }

    private static WorldPosition Near(WorldPosition at, int index) => new(at.X + 2.5 * index, at.Y, at.Z + 4);

    private async Task<EntityId> Place(string template, WorldPosition at, BuildOptions? options = null)
    {
        var e = Build.PlaceProp(template, at, null, 0, options ?? InBases);
        _placed.Add(e);
        await Steps(10);
        return e;
    }

    private void Remove()
    {
        foreach (var e in _placed)
            if (World.Exists(e)) Build.RemoveProp(e, InBases);
        _placed.Clear();
    }

    // ---- mistakes ----------------------------------------------------------------------------------------------

    private void Mistakes()
    {
        var mine = Blocks.Report().GetValueOrDefault(Me);
        foreach (var e in mine.Errors ?? []) Info($"error: {e}");
        void Named(string block, params string[] words)
        {
            var e = mine.Errors?.FirstOrDefault(x => Regex.IsMatch(x, $@"(?<![-\w]){Regex.Escape(block)}(?![-\w])"));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        Named("no-name", "name");
        Named("many-slots", "slots", "500");
        Named("typo-field", "slotz");
        Named("bad-time", "time", "dusk", "night");
        Named("empty-requires", "requires", "nothing");
        Named("bad-count", "requires.count", "0");
        Named("uses-nothing", "requires.uses");
        Named("bad-state", "requires.when[0]", "Levitating");
        Named("bad-verb", "yodel", "open");
        Named("blank-prompt", "toggle.onPrompt", "words");
        Named("verb-and-prompt", "prompt", "not both");
        Named("bad-holds", "holds[Material_T1_Twigs]", "0");
        Named("hair-jaw", "variants[jaw03]", "Jaw03");
        Named("refused-lock", "onRefused");
        Named("no-such-interaction", "onInteract", "gone");
        Named("far-spot", "lies.at");
        Named("bad-play", "dance", "sit");
        Named("empty-interaction", "changes nothing");
        Named("no-cells", "cells");
        Named("bad-cell", "cells[0][0]", "'x'");
        Named("too-big", "cells", "33");
        Named("bad-cost", "cost", "0");
        Named("hair-fits", "look.fits");
        Named("hair-attach", "look.attach");
        Named("beard-hat", "variants", "hat");
        string[] broken = ["no-name", "many-slots", "typo-field", "bad-time", "empty-requires", "bad-count", "uses-nothing", "bad-state", "bad-verb", "blank-prompt", "verb-and-prompt", "bad-holds", "hair-jaw", "refused-lock", "no-such-interaction", "far-spot", "bad-play", "empty-interaction", "no-cells", "bad-cell", "too-big", "bad-cost", "hair-fits", "hair-attach", "beard-hat"];
        var made = (mine.Blocks ?? []).Intersect(broken).ToList();
        Check(made.Count == 0 && Content.Items.Get($"{Me}.many-slots") == null && Content.Templates.Get($"{Me}.many-slots.prop") == null, $"none of the broken blocks became anything ({string.Join(", ", made)})");
        var late = mine.Errors?.FirstOrDefault(x => x.Contains("plain-lamp", StringComparison.Ordinal));
        Check(late != null && late.Contains("plays", StringComparison.Ordinal), $"a look with no interaction of its own and none said is named when the game's templates are there: \"{late}\"");

        var thrown = "";
        try { Blocks.Define("bad", new SeatBlock { Name = "Bad", Sits = new SpotBlock { At = new(0, 0, 99) } }); }
        catch (BlockError e) { thrown = e.Message; }
        Check(thrown.Contains("seat bad", StringComparison.Ordinal) && thrown.Contains("sits.at", StringComparison.Ordinal), $"C#: a wrong value throws with the block and the field: \"{thrown}\"");
        var refused = "";
        try { Blocks.Define("bad-refused", new DoorBlock { Name = "x", OnInteract = new InteractBlock { Requires = new RequiresBlock { Item = Twigs }, OnRefused = [new EffectBlock { Heal = 1 }] } }); }
        catch (BlockError e) { refused = e.Message; }
        Check(refused.Contains("onInteract.onRefused", StringComparison.Ordinal), $"C#: the game's own lock refuses with no effects: \"{refused}\"");
    }

    // ---- it is all there ----------------------------------------------------------------------------------------

    private void Registered()
    {
        var report = Blocks.Report().GetValueOrDefault(Sample);
        Check(report.Errors is { Count: 0 }, $"the sample's blocks came through with no word from the services: [{string.Join(" | ", report.Errors ?? [])}]");
        var clients = File.ReadAllText(Path.Combine(Mods.Get(Sample)!.Directory, "kiln.mod.json"));
        Check(clients.Contains("\"both\"", StringComparison.Ordinal) && clients.Contains("\"mod\"", StringComparison.Ordinal), "the SDK made the manifest side both, clients mod: a shape is merged into each game's own table at its start");

        foreach (var (n, starter, family) in new[] { ("lockbox", "Prop_Decoration_T1_Storage_24", "InventorySetup"), ("night-door", "Prop_Door_Small_Wood_Crafted", "InteractionToggle"), ("camp-bed", "", "InteractionAttachment"), ("milking-stool", "", "InteractionAttachment") })
        {
            var h = Blocks.Find(n, Sample)!;
            var e = I(G(n));
            var t = T(G(n) + ".prop");
            Check(e.Guid == BlockIds.Derive(Sample, "item", n) && e.Id == h.Id, $"{n}: the item's GUID follows from the mod and the name: {e.Guid}");
            Check(t.Guid == BlockIds.Derive(Sample, "template", $"{n}.prop") && e.GetRef("equipment.placedEntity") == t.Guid, $"{n}: the item places a template of its own: {t.Guid}");
            Check(Has(t, family), $"{n}: its template has what its kind needs: {family}");
        }
        var box = T(G("lockbox") + ".prop");
        Check(box.Get("InventorySetup.availableSlotCount").Integer == 8 && box.Get("InventorySetup.genericSlotCount").Integer == 8, $"a container's slots: {box.Get("InventorySetup.availableSlotCount").Integer}");
        var scraps = I(Scraps).Id;
        Info($"lockbox lock: item {box.Get("InteractionLock.requiredItem").Integer:x8}, amount {box.Get("InteractionLock.requiredItemAmount").Integer}, mask {box.Get("InteractionLock.lockMask").Name}");
        Check((uint)box.Get("InteractionLock.requiredItem").Integer == scraps && box.Get("InteractionLock.requiredItemAmount").Integer == 1, $"Requires.Item is the game's lock: item 0x{(uint)box.Get("InteractionLock.requiredItem").Integer:x8} (scraps 0x{scraps:x8})");
        var door = T(G("night-door") + ".prop");
        Check(door.Get("InteractionLock.enableDaytimeLock").Integer != 0 && door.Get("InteractionLock.daytimeLock.lockedTime").Name == "Daytime", $"Requires.Time = night: the game's daytime lock, locked by day: {door.Get("InteractionLock.daytimeLock.lockedTime").Name}");
        var keyDoor = T($"{Me}.key-door.prop");
        Check(keyDoor.Get("InteractionLock.requiredItemAmount").Integer == 2 && keyDoor.Get("InteractionLock.lockMask").Name?.Contains("Consume") == true, $"Uses: the lock takes the item: {keyDoor.Get("InteractionLock.lockMask").Name}, {keyDoor.Get("InteractionLock.requiredItemAmount").Integer}");
        var bench = T(G("prayer-bench") + ".prop");
        Check((uint)bench.Get("InteractionOfferRuntime.defaultVerbId").Integer == BlockIds.Fnv(Compiler.LocaTag(bench.Guid, "kiln:verb")), $"a shared interaction's prompt of its own is each template's own text: 0x{(uint)bench.Get("InteractionOfferRuntime.defaultVerbId").Integer:x8}");
        var key = T($"{Me}.key-door.prop");
        Check((uint)T($"{Me}.lever-chest.prop").Get("InteractionOfferRuntime.defaultVerbId").Integer == InteractVerb.Flip.Id, "Verb: the game word's loca tag");
        var bed = T(G("camp-bed") + ".prop");
        Check((uint)bed.Get("InteractionOfferRuntime.defaultSequence").Integer == InteractPlay.Sleep.Id && bed.Get("ComfortProvider.category").Name == "Bed", $"a bed sleeps by the game's own sequence: 0x{(uint)bed.Get("InteractionOfferRuntime.defaultSequence").Integer:x8}, comfort {bed.Get("ComfortProvider.category").Name} {bed.Get("ComfortProvider.comfort").Integer}");
        var stool = T(G("milking-stool") + ".prop");
        Check((uint)stool.Get("InteractionOfferRuntime.defaultSequence").Integer == InteractPlay.Sit.Id, $"a seat sits: 0x{(uint)stool.Get("InteractionOfferRuntime.defaultSequence").Integer:x8}");
        var lever = T($"{Me}.lever-chest.prop");
        Check(Has(lever, "InteractionToggle") && lever.Get("ServerToggleState.state").Integer != 0 && (uint)lever.Get("InteractionOfferRuntime.defaultSequence").Integer == InteractPlay.Toggle.Id && (uint)lever.Get("InteractionOfferRuntime.defaultVerbId").Integer == InteractVerb.Flip.Id,
            $"Toggle: added to a chest that had none, starting on, played by the hand on it: sequence 0x{(uint)lever.Get("InteractionOfferRuntime.defaultSequence").Integer:x8}");
        var bell = T($"{Me}.bell-door.prop");
        var bellGuid = bell.Guid;
        Check((uint)bell.Get("InteractionOfferRuntime.defaultVerbId").Integer == BlockIds.Fnv(Compiler.LocaTag(bellGuid, "kiln:verb")), $"Prompt: the verb is the tag of the template's own text: 0x{(uint)bell.Get("InteractionOfferRuntime.defaultVerbId").Integer:x8}");
        Check(bell.Get("InteractionToggle.offToOnVerb").Guid == Compiler.LocaTag(bellGuid, "kiln:on"), $"OnPrompt: the toggle's word is the template's own text's tag: {bell.Get("InteractionToggle.offToOnVerb").Guid}");
        Check(BlockIds.Fnv(bell.Get("InteractionToggle.onToOffVerb").Guid) == InteractVerb.Sit.Id, $"Off = a game word with no reference of its own: 16 bytes with its id: {bell.Get("InteractionToggle.onToOffVerb").Guid}");
        var texts = NewContentTexts(bellGuid);
        Info($"the bell door's clone as kiln.gamedata keeps it: {texts}");
        Check(texts.Contains("Ring the bell", StringComparison.Ordinal) && texts.Contains("Push", StringComparison.Ordinal), "and kiln.gamedata keeps the template's texts, which it gives the loca hooks of every Kiln client");
        var stocked = T($"{Me}.stocked-box.prop");
        Check(Has(stocked, "DefaultInventory"), "Holds: the container keeps the game's flag of what it got first");
        var high = T($"{Me}.high-bed.prop");
        Check(Math.Abs(high.Get("InteractionAttachment.guestOffset.localOffset.y").Number - 1.2) < 1e-4 && Math.Abs(high.Get("InteractionAttachment.guestOffset.orientationOffset.y").Number - Math.Sin(Math.PI / 4)) < 1e-4,
            $"Lies: where the guest lies, turned: y {high.Get("InteractionAttachment.guestOffset.localOffset.y").Number}, q.y {high.Get("InteractionAttachment.guestOffset.orientationOffset.y").Number}");
        var own = T($"{Me}.own-seat.prop");
        Check(own.Get("ModelResource.model").Guid == I("Prop_Decoration_Comfort3_T2_Chair").GetRef("iconModel") && Math.Abs(own.Get("InteractionAttachment.guestOffset.localOffset.y").Number - 0.45) < 1e-4, $"a seat with another model keeps its kind's sitting, at its own spot: {own.Get("ModelResource.model").Guid}");
        var hair = I(G("short-crop"));
        Check(hair.GetRef("equipment.visualModel") == I("VC_Hair_04_02_frenchcrop").GetRef("equipment.visualModel") && hair.Get("equipment.slot").Name == "BodyPart_Hair", $"hair: an item of the hair slot with the look it names: {hair.Get("equipment.slot").Name}");
        var beard = I(G("trim"));
        Check(beard.Get("equipment.slot").Name == "BodyPart_Beard", $"a beard: the beard slot: {beard.Get("equipment.slot").Name}");
        var problems = (Blocks.Report().GetValueOrDefault(Me).Errors ?? []).Where(x => !x.StartsWith("blocks/broken.json", StringComparison.Ordinal) && !x.Contains("plain-lamp", StringComparison.Ordinal)).ToList();
        Check(problems.Count == 0, $"nothing else from the services about this mod's blocks: [{string.Join(" | ", problems)}]");
    }

    // The clone's definition as kiln.gamedata reports it, texts included.
    private static string NewContentTexts(Guid template) =>
        Content.GameDataDefinitions().FirstOrDefault(d => d.Guid == template) is { } d ? $"{d.State} {d.DefinitionJson}" : "none";

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twins()
    {
        // a prompt of its own is named by a tag from the template's GUID, which follows from the name
        static string Normal(string? json) => Regex.Replace(Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "-json", ""), "(?<=\"InteractionOfferRuntime\\.defaultVerbId\": )[0-9]+", "TAG");
        foreach (var n in new[] { "lockbox", "night-door", "camp-bed", "milking-stool", "pray", "prayer-bench", "arched-window", "short-crop", "trim" })
        {
            var cs = Normal(Blocks.Lowered(n, Sample));
            var json = Normal(Blocks.Lowered(n + "-json", Sample));
            Check(cs.Length > 10 && cs == json, $"{n}: blocks/twins.json lowers to what the C# does ({cs.Length} characters of definitions)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
        Check(Blocks.Find("lockbox", Sample)!.Guid != Blocks.Find("lockbox-json", Sample)!.Guid, "and each has its own ids");
    }

    // A shape's cells as the game keeps them: the game's own 4 m door wall is c3 c3 c3 c3 c3 ff ff ff (VoxelBlueprintItemRegistryResource).
    private void Shapes()
    {
        var wall = JsonNode.Parse(Blocks.Lowered("door-wall") ?? "{}")?["blueprint"];
        Check((string?)wall?["cells"] == "c3c3c3c3c3ffffff" && wall?["size"]?.ToJsonString() == "[8,8,1]", $"the door wall's cells are the game's own door wall's bytes: {wall?["cells"]} {wall?["size"]?.ToJsonString()}");
        var arch = JsonNode.Parse(Blocks.Lowered("arched-window", Sample) ?? "{}")?["blueprint"];
        Check((string?)arch?["cells"] == "ffffc3c3c3e7ffff" && (int?)arch?["full"] == 50 && (int?)arch?["cost"] == 16, $"the arched window: {arch?["cells"]}, {arch?["full"]} cells, cost {arch?["cost"]} (the walls' 20 per 64)");
        var status = (string?)JsonNode.Parse(Blocks.Lowered("arched-window", Sample) ?? "{}")?["status"];
        Info($"shape status: {status}; warnings: {string.Join(" | ", Blocks.Warnings(Sample))}");
        Check(status == "registered" ? Content.Items.Get(G("arched-window")) != null : Content.Items.Get(G("arched-window")) == null,
            $"a shape's item is made only when the shapes' table has it ({status}): a blueprint item with no cells is one the game can't place");
    }

    // ---- placed, and used through the game's code ------------------------------------------------------------------

    private static byte[]? Offer(EntityId e) => World.ReadBytes(e, "InteractionOfferRuntime");

    private async Task Placed(WorldPosition at)
    {
        var i = 0;
        foreach (var n in new[] { "lockbox", "night-door", "camp-bed", "milking-stool", "prayer-bench" })
        {
            var e = await Place(G(n) + ".prop", Near(at, i++));
            var info = Spawn.Inspect(e);
            Check(!e.IsNone && info?.Template == T(G(n) + ".prop").Guid, $"{n} places: {e} {info?.TemplateName}");
            if (Offer(e) is { Length: 56 } o) Info($"{n}: offer sequence 0x{BitConverter.ToUInt32(o, OfferSequence):x8} verb 0x{BitConverter.ToUInt32(o, OfferVerb):x8} id {BitConverter.ToUInt32(o, OfferId)} active {o[OfferActive]}");
        }
        var box = _placed[0];
        Check(Items.Read(box)?.AvailableSlots == 8, $"the lockbox holds 8: {Items.Read(box)?.AvailableSlots}");
        var bench = Offer(_placed[4]);
        Check(bench is { Length: 56 } && BitConverter.ToUInt32(bench, OfferVerb) == BlockIds.Fnv(Compiler.LocaTag(T(G("prayer-bench") + ".prop").Guid, "kiln:verb")), "a placed entity starts with the template's verb");
        foreach (var (what, e) in new[] { ("ours", EntityId.None), ("game chest", await Place("Prop_Decoration_T1_Storage_24", Near(at, 7))) })
        {
            var probe = what == "ours" ? Build.PlaceProp($"{Me}.stocked-box.prop", Near(at, 8), null, 0, InBases) : e;
            if (what == "ours") { _placed.Add(probe); await Steps(2); }
            foreach (var c in new[] { "DefaultInventory", "InventorySetupInit", "InventoryEmptyState", "RandomLootSlotCount", "InventorySetupOverwrite", "UpgradeStorage", "OnSuccess", "Inventory", "UsedItem" })
                Info($"{what} {probe}: {c} {World.ComponentSize(probe, c)} {(World.ReadBytes(probe, c) is { } b ? Convert.ToHexString(b.Take(24).ToArray()) : "-")}");
        }
        var stocked = await Place($"{Me}.stocked-box.prop", Near(at, 5));
        await Steps(12);
        var twigs = Items.Read(stocked)?.Count(new ItemId(I(Twigs).Id)) ?? -1;
        var scraps = Items.Read(stocked)?.Count(new ItemId(I(Scraps).Id)) ?? -1;
        Check(twigs == 5 && scraps == 2 && World.ReadBytes(stocked, "DefaultInventory")?[0x11] == 1, $"Holds: a placed container gets what it holds, once: twigs {twigs}, scraps {scraps}, the game's flag {World.ReadBytes(stocked, "DefaultInventory")?[0x11]}");
        var lever = await Place($"{Me}.lever-chest.prop", Near(at, 6));
        Check(World.ReadBytes(lever, "ServerToggleState") is { } s && s[0] == 1, "a toggle that starts on is on when placed");
        Remove();
        await Steps(4);
    }

    private async Task<EntityId> Guest(WorldPosition at)
    {
        var e = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(40);
        Check(World.Has(e, "PlayerInput"), $"a spawned character {e} to be the guest");
        return e;
    }

    // The game's interaction_attempt accepts an AcceptOfferedInteractionEvent when the host's offer is active and the id is its offer's. Without a player connected the game keeps
    // offers inactive (interaction_offer runs only with a player in range): the lab sets the flag a player in range would set, and leaves the rest to the game.
    private async Task<bool?> Use(EntityId guest, EntityId host, bool force = true)
    {
        var o = Offer(host);
        if (o is not { Length: 56 }) return null;
        if (o[OfferActive] == 0 && force)
        {
            World.TryWrite(host, "InteractionOfferRuntime", OfferActive, (byte)1);
            await Steps(1);
            o = Offer(host)!;
        }
        _results.Clear();
        _attempts.Clear();
        Gameplay.Inject.Interact(guest, host, BitConverter.ToUInt32(o, OfferId));
        for (var n = 0; n < 12 && !_results.Any(r => r.Host == host && r.Guest == guest); n++) await Steps(1);
        var r = _results.FirstOrDefault(r => r.Host == host && r.Guest == guest);
        Info($"use {host} by {guest}: offer {BitConverter.ToUInt32(o, OfferId)} active {o[OfferActive]} -> attempt canceled {_attempts.FirstOrDefault(a => a.Host == host)?.Canceled}, accepted {r?.Accepted}");
        return r?.Accepted;
    }

    private async Task Interacting(WorldPosition at)
    {
        var door = await Place($"{Me}.coded-door.prop", Near(at, 0));
        var guest = await Guest(new WorldPosition(Near(at, 0).X + 1, at.Y, Near(at, 0).Z - 1));
        var before = World.ReadBytes(door, "ServerToggleState")?[0];
        var o = Offer(door)!;
        Info($"the door's own offer with no player near: active {o[OfferActive]}");
        _allow = true;
        Check(await Use(guest, door) == true, "the game accepts a use of a door of ours");
        await Steps(6);
        var after = World.ReadBytes(door, "ServerToggleState")?[0];
        // the guest's own Interaction_Toggle flips it (ToggleInteractionActionEvent at 0.43 s): a spawned guest plays no sequence, so the door stays; a player's opens it (manual check)
        Info($"the door after an accept with no sequence played: state {before} -> {after}");
        await Steps(10);
        var buffs = PlayerStates.BuffsOf(guest);
        Check(buffs.Any(b => b.Id == Blocks.Find("glow")!.Id), $"Effects ran on the guest once the game accepted: [{string.Join(", ", buffs.Select(b => $"0x{b.Id:x8}"))}]");
        _allow = false;
        var state = World.ReadBytes(door, "ServerToggleState")?[0];
        Check(await Use(guest, door) != true && _attempts.Any(a => a.Host == door && a.Canceled), "Requires.If false: Kiln declines the attempt the way the game does, and nothing accepts it");
        await Steps(6);
        Check(World.ReadBytes(door, "ServerToggleState")?[0] == state, "and the door stays as it was");
        _allow = true;

        var sleepers = await Place($"{Me}.sleepers-only.prop", Near(at, 1));
        Check(await Use(guest, sleepers) != true && _attempts.Any(a => a.Host == sleepers && a.Canceled), "Requires.When: a guest not in the state is declined");

        // the game's own lock: an item in the guest's bag
        var keyDoor = await Place($"{Me}.key-door.prop", Near(at, 2));
        Check(await Use(guest, keyDoor) == false && !_attempts.Any(a => a.Host == keyDoor && a.Canceled), "the game's lock declines a guest without the item, and Kiln has no part in it");
        var given = await Items.Give(guest, Twigs, 3);
        Info($"gave the guest twigs: {given}");
        var accepted = await Use(guest, keyDoor);
        await Steps(6);
        var left = Items.Read(guest)?.Count(new ItemId(I(Twigs).Id)) ?? -1;
        Check(accepted == true, $"with the item the game's lock lets them through (twigs left {left})");
        Info($"Uses = true: the guest's twigs 3 -> {left}");

        // the game's daytime lock
        var night = await Place(G("night-door") + ".prop", Near(at, 3));
        var day = Time.TryGetState(out var t) ? t.IsDay : (bool?)null;
        var used = await Use(guest, night);
        Check(day == null || used == !day, $"the night door at {(Time.TryGetState(out var t2) ? t2.Clock : "?")} (day {day}): accepted {used}");

        // a seat of ours, through the game's code
        var stool = await Place(G("milking-stool") + ".prop", Near(at, 4));
        Check(await Use(guest, stool) == true, "the game accepts a seat of ours");
        Remove();
        await Steps(4);
    }

    private async Task HairAndBeards(WorldPosition at)
    {
        var guest = await Guest(Near(at, 5));
        var was = Barber.Of(guest);
        Info($"the spawned character's hair and beard: {was}");
        var hair = Blocks.Find("short-crop", Sample)!;
        var beard = Blocks.Find("trim", Sample)!;
        Check(Barber.Wear(guest, hair) && Barber.Wear(guest, beard), "Barber.Wear writes the character's setup");
        await Steps(4);
        Check(Barber.Of(guest) == (hair.Id, beard.Id), $"and the character wears them: {Barber.Of(guest)}");
        Spawn.Destroy(guest);
    }

    private async Task Beds(WorldPosition at)
    {
        var bed = await Place(G("camp-bed") + ".prop", Near(at, 6));
        var guest = await Guest(Near(at, 7));
        InteractRuntime.SleptIn(guest, bed, "test:1");
        var spawn = InteractRuntime.SpawnOf("test:1");
        var p = World.Position(bed)!.Value;
        Check(spawn is { } s && Math.Abs(s.X - p.X) < 0.01 && Math.Abs(s.Z - p.Z) < 0.01 && s.Y > p.Y, $"a spawn bed is remembered for a player: {spawn}");
        Check(InteractRuntime.SleepsThroughNight(guest) == false, "an awake character is no sleeper");
        Abilities.PlaySequence(guest, InteractPlay.Sleep.Sequence);
        await Steps(30);
        Info($"a character sleeping with no bed: sleeping {PlayerStates.Is(guest, PlayerStateId.Sleeping)}, through the night {InteractRuntime.SleepsThroughNight(guest)}");
        Check(!InteractRuntime.SleepsThroughNight(guest), "sleeping on the floor ends no night");
        Build.RemoveProp(bed, InBases);
        _placed.Remove(bed);
        await Steps(6);
        Check(InteractRuntime.SpawnOf("test:1") == null, "a bed taken away is no spawn point");
        Spawn.Destroy(guest);
    }

    // ---- restarts -----------------------------------------------------------------------------------------------

    private async Task<WorldPosition> OpenGround()
    {
        double x = 3024, z = 1550;
        var box = Build.BoxBetween(new WorldPosition(x, 850, z), new WorldPosition(x, 960, z));
        var cells = await Build.ReadAsync(box);
        for (var y = box.SizeY - 1; y >= 0; y--)
        {
            var c = cells[box.Index(0, y, 0)];
            if (c.Fill > 0 && c.Material is > 0 and < 128) return new WorldPosition(x, Build.CornerOf(new CellPos(box.X, box.Y + y, box.Z)).Y + c.Fill / 255.0 * 0.5 + 0.05, z);
        }
        throw new InvalidOperationException("no ground at the open spot");
    }

    private static JsonArray Pos(EntityId e) => new(World.Position(e)!.Value.X, World.Position(e)!.Value.Y, World.Position(e)!.Value.Z);

    private async Task Leave(WorldPosition at, string file)
    {
        var box = await Place(G("lockbox") + ".prop", Near(at, 0));
        await Items.Give(box, Twigs, 7);
        var door = await Place($"{Me}.coded-door.prop", Near(at, 1));
        var guest = await Guest(Near(at, 2));
        await Use(guest, door);
        await Steps(6);
        // what a player's Interaction_Toggle does to the door: it stands open. Whether a write holds is the game's (its logic graph may own the state)
        var gameDoor = await Place("Prop_Door_Small_Wood_Crafted", Near(at, 4));
        World.TryWrite(door, "ServerToggleState", 0, (byte)1);
        World.TryWrite(gameDoor, "ServerToggleState", 0, (byte)1);
        await Steps(24);
        Info($"the door's toggle written open, 24 steps later: {World.ReadBytes(door, "ServerToggleState")?[0]}");
        var bed = await Place(G("camp-bed") + ".prop", Near(at, 3));
        InteractRuntime.SleptIn(guest, bed, "test:2");
        var stocked = await Place($"{Me}.stocked-box.prop", Near(at, 5));
        await Steps(12);
        await Items.Take(stocked, new ItemId(I(Twigs).Id), 1);
        await Steps(4);
        var open = await OpenGround();
        var outside = Build.PlaceProp(G("camp-bed") + ".prop", open, null, 0, new BuildOptions());
        await Steps(24);
        var state = new JsonObject
        {
            ["box"] = Pos(box), ["door"] = Pos(door), ["bed"] = Pos(bed), ["outside"] = Pos(outside),
            ["doorState"] = World.ReadBytes(door, "ServerToggleState")?[0] ?? 255, ["gameDoor"] = Pos(gameDoor), ["stocked"] = Pos(stocked),
            ["stockedTwigs"] = Items.Read(stocked)?.Count(new ItemId(I(Twigs).Id)) ?? -1,
            ["itemBox"] = I(G("lockbox")).Guid.ToString(), ["templateBox"] = T(G("lockbox") + ".prop").Guid.ToString(), ["templateDoor"] = T($"{Me}.coded-door.prop").Guid.ToString(),
            ["templateBed"] = T(G("camp-bed") + ".prop").Guid.ToString(), ["hair"] = I(G("short-crop")).Guid.ToString(),
        };
        Info($"left: lockbox {box} with 7 twigs, door {door} toggle {state["doorState"]}, bed {bed} in the base, bed {outside} on open ground at {open}");
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(file, state.ToJsonString());
        var saved = await Saves.Request();
        Check(saved.Success, $"saved: {saved}");
        _placed.Clear();
        Info("restart the lab for the second phase");
    }

    private static EntityId At(JsonNode? where, Guid template)
    {
        var (x, y, z) = ((double)where![0]!, (double)where[1]!, (double)where[2]!);
        foreach (var e in Spawn.Find(template, new WorldPosition(x, y, z), 0.5, 8))
            if (World.Position(e) is { } p && Math.Abs(p.X - x) < 0.25 && Math.Abs(p.Z - z) < 0.25) return e;
        return EntityId.None;
    }

    private async Task AfterRestart(string file)
    {
        var s = JsonNode.Parse(File.ReadAllText(file))!;
        Check(I(G("lockbox")).Guid.ToString() == (string?)s["itemBox"] && T(G("lockbox") + ".prop").Guid.ToString() == (string?)s["templateBox"] && I(G("short-crop")).Guid.ToString() == (string?)s["hair"], "the ids are what they were");
        var box = At(s["box"], T(G("lockbox") + ".prop").Guid);
        var door = At(s["door"], T($"{Me}.coded-door.prop").Guid);
        var bed = At(s["bed"], T(G("camp-bed") + ".prop").Guid);
        var outside = At(s["outside"], T(G("camp-bed") + ".prop").Guid);
        Check(!box.IsNone && Items.Read(box)?.Count(new ItemId(I(Twigs).Id)) == 7 && Items.Read(box)?.AvailableSlots == 8, $"the lockbox came back in the base, with what it held: {box}, twigs {(box.IsNone ? -1 : Items.Read(box)?.Count(new ItemId(I(Twigs).Id)))}");
        var gameDoor = At(s["gameDoor"], T("Prop_Door_Small_Wood_Crafted").Guid);
        var mine = door.IsNone ? (byte)255 : World.ReadBytes(door, "ServerToggleState")?[0] ?? 254;
        var theirs = gameDoor.IsNone ? (byte)255 : World.ReadBytes(gameDoor, "ServerToggleState")?[0] ?? 254;
        Info($"both doors were left open ({s["doorState"]}); after the restart ours is {mine}, the game's own {theirs}");
        Check(!door.IsNone && !gameDoor.IsNone && mine == theirs, $"the door came back, as the game's own door does: open {mine}, the game's {theirs}");
        if (!gameDoor.IsNone) Build.RemoveProp(gameDoor, InBases);
        Check(!bed.IsNone, $"the bed came back in the base: {bed}");
        var stocked = At(s["stocked"], T($"{Me}.stocked-box.prop").Guid);
        var left = stocked.IsNone ? -1 : Items.Read(stocked)?.Count(new ItemId(I(Twigs).Id)) ?? -1;
        Check(!stocked.IsNone && left == (int)s["stockedTwigs"]!, $"a container that got what it holds isn't filled again by a restart: twigs {left} (left with {s["stockedTwigs"]})");
        if (!stocked.IsNone) Build.RemoveProp(stocked, InBases);
        var spawn = InteractRuntime.SpawnOf("test:2");
        Check(spawn is { } sp && !bed.IsNone && Math.Abs(sp.X - World.Position(bed)!.Value.X) < 0.01, $"and a player's spawn bed is still theirs after the restart: {spawn}");
        if (!door.IsNone)
        {
            var guest = await Guest(new WorldPosition(World.Position(door)!.Value.X + 1, World.Position(door)!.Value.Y, World.Position(door)!.Value.Z - 1));
            World.TryWrite(door, "ServerToggleState", 0, (byte)0);
            Check(await Use(guest, door) == true, "after the restart the game accepts a use of the door");
            await Steps(10);
            Check(PlayerStates.BuffsOf(guest).Any(b => b.Id == Blocks.Find("glow")!.Id), "and the block's effects run again: Kiln made its interactions again with the blocks");
        }
        Info($"the bed on open ground: {(outside.IsNone ? "gone" : "came back")}, as every prop outside a base");
        Check(outside.IsNone, "outside a base the world's save keeps none of it, as for every placed prop");
        foreach (var e in new[] { box, door, bed })
            if (!e.IsNone) Build.RemoveProp(e, InBases);
        await Steps(4);
        var saved = await Saves.Request();
        Check(saved.Success, $"saved without them: {saved}");
    }
}
