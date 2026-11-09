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
    private readonly List<ItemCraftedEvent> _crafted = new();
    [Subscribe] private void OnCrafted(ItemCraftedEvent e) => _crafted.Add(e);

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
        Blocks.Define("high-bed", new BedBlock { Name = "High Bed", Lies = new SpotBlock { At = new(0.1f, 1.2f, -0.3f), Facing = 90 } });
        Blocks.Define("own-seat", new SeatBlock { Name = "Own Seat", Visual = new VisualBlock { Model = "item:Prop_Decoration_Comfort3_T2_Chair" }, Sits = new SpotBlock { At = new(0, 0.45f, 0.05f) } });
        Blocks.Define("inline-cache", new ContainerBlock { Name = "Inline Cache", DefaultLoot = new LootTableBlock { Entries = [new LootEntry { Item = Twigs, CountMin = 5 }] } });
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
            await EnsureFixture();
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
                await Part("default loot", () => DefaultLoot(at));
                await Part("fields of doors, lids and colliders", () => { Fields(); return Task.CompletedTask; });
                await Part("door sides", () => DoorSides(at));
                await Part("several colliders", () => Colliders(at));
                await Part("crafting props", () => CraftingProps(at, "-json"));
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
        foreach (var home in Build.Bases)
            if (!home.Abandoned && World.Position(home.Altar) is { } at) return at;
        throw new InvalidOperationException($"this world has no station or active base for the persistence fixture ({Build.Bases.Count} base zones)");
    }

    // A disposable lab may start from a clean save. Found a real base through the public build API so the restart assertions exercise the game's save rules.
    private async Task EnsureFixture()
    {
        if (Build.Bases.Count == 0)
        {
            var altars = Content.Templates.Where(t => t.Name.Contains("Altar", StringComparison.OrdinalIgnoreCase) && Has(t, "BaseIdComponent")).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
            Info($"base fixture templates: {string.Join(", ", altars.Select(t => t.Name))}");
            var altar = altars.FirstOrDefault() ?? throw new InvalidOperationException("the game's catalog has no flame altar template for the lab fixture");
            var ground = await OpenGround(3800, 1434);
            var placed = Build.PlaceProp(altar.Guid, ground, null, 0, new BuildOptions { Flags = BuildFlags.AllowAltars | BuildFlags.InBases });
            if (!Check(!placed.IsNone && await Until(() => Build.Bases.Any(b => b.Altar == placed && !b.Abandoned), 240), $"a real flame altar founds the lab fixture base: {altar.Name}, {placed}"))
                throw new InvalidOperationException("the fixture altar did not produce an active build zone");
        }
        if (World.Find("FactoryStation").Count == 0)
        {
            var home = Build.Bases.First(b => !b.Abandoned);
            var at = World.Position(home.Altar)!.Value;
            var station = Build.PlaceProp("T1_Forge_Factory", new WorldPosition(at.X + 1, at.Y, at.Z + 1), null, 0, InBases);
            await Steps(30);
            if (!Check(!station.IsNone && World.Has(station, "FactoryStation"), $"a game forge anchors the fixture's station regressions: {station}"))
                throw new InvalidOperationException("the fixture forge is not a station");
        }
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
        Named("lid-stuck", "lid", "moves");
        Named("empty-loot", "defaultLoot.entries", "at least one");
        Named("no-loot-table", "defaultLoot", "gone-table");
        Named("bad-opens", "opens", "front");
        Named("bad-sides", "sides", "front");
        Named("both-colliders", "colliders", "not both");
        Named("flat-collider", "colliders[1].size", "more than 0");
        Named("needs-other", "needsProps", "provides");
        Named("no-such-prop", "needsProps", "nothing-here");
        Named("factory-props", "needsProps", "manual station");
        Named("hair-jaw", "variants[jaw03]", "Jaw03");
        Named("refused-lock", "onRefused");
        Named("no-such-interaction", "onInteract", "gone");
        Named("far-spot", "lies.at");
        Named("bad-play", "dance", "sit");
        Named("empty-interaction", "changes nothing");
        Named("no-cells", "cells");
        Named("bad-cell", "cells[0][0]", "'x'");
        Named("too-big", "cells", "33");
        Named("hair-fits", "look.fits");
        Named("hair-attach", "look.attach");
        Named("beard-hat", "variants", "hat");
        string[] broken = ["factory-props", "lid-stuck", "empty-loot", "no-loot-table", "bad-opens", "bad-sides", "both-colliders", "flat-collider", "needs-other", "no-such-prop", "no-name", "many-slots", "typo-field", "bad-time", "empty-requires", "bad-count", "uses-nothing", "bad-state", "bad-verb", "blank-prompt", "verb-and-prompt", "hair-jaw", "refused-lock", "no-such-interaction", "far-spot", "bad-play", "empty-interaction", "no-cells", "bad-cell", "too-big", "hair-fits", "hair-attach", "beard-hat"];
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
        var plain = "";
        try { Blocks.Define("bad-needs", new ItemBlock { Name = "x", Recipe = new RecipeBlock { Needs = { [Twigs] = 1 }, NeedsProps = [$"{Sample}:trestle"] } }); }
        catch (BlockError e) { plain = e.Message; }
        Check(plain.Contains("recipe.needsProps[0]", StringComparison.Ordinal) && plain.Contains("provides", StringComparison.Ordinal), $"C#: a recipe that needs what provides no crafting prop: \"{plain}\"");
    }

    // ---- it is all there ----------------------------------------------------------------------------------------

    private void Registered()
    {
        var report = Blocks.Report().GetValueOrDefault(Sample);
        Check(report.Errors is { Count: 0 }, $"the sample's blocks came through with no word from the services: [{string.Join(" | ", report.Errors ?? [])}]");
        Check(Blocks.Find("jsonc-table") != null && WorldTables.CraftingProps.Any(p => p.Mod == Me && p.Block == "jsonc-table"),
            "the mixed JSONC file with comments, trailing commas and comment braces reaches both BlockJson and the native boot merge");
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

    // a prompt of its own is named by a tag from the template's GUID, which follows from the name; a crafting prop's id and place follow from its GUID, which follows from the name
    private static string Normal(string? json) => Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "-json", ""), "(?<=\"InteractionOfferRuntime\\.defaultVerbId\": )[0-9]+", "TAG"),
            "(\"id\": \"0x[0-9a-f]{8}\"|\"index\": [0-9]+|\"RequiredCraftingProp\\.craftingPropId\": [0-9]+)", "CRAFT"),
            "(?<=joltObjects\\[0\\]\\.hash\": )[0-9]+|(?<=\"Crafting.workshopId\": )[0-9]+|(?<=\"CraftingInteraction.workshopId\": )[0-9]+", "IDENTITY");

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twins()
    {
        foreach (var n in new[] { "lockbox", "night-door", "camp-bed", "milking-stool", "pray", "prayer-bench", "arched-window", "short-crop", "trim", "scrap-pile", "supply-cache", "barrel", "one-way-door", "trestle" })
        {
            // Code's shapes are first learned after the boot merge; JSON's were scanned before it. On that first start compare the full shape,
            // and require the documented registration states. After the restart the complete lowered definitions must match too.
            if (n == "arched-window" && JsonNode.Parse(Blocks.Lowered(n, Sample) ?? "{}") is { } shape && (string?)shape["status"] == "pending")
            {
                var twin = JsonNode.Parse(Blocks.Lowered(n + "-json", Sample) ?? "{}");
                Check((string?)twin?["status"] == "registered" && JsonNode.DeepEquals(shape["blueprint"], twin?["blueprint"]) && shape["items"] == null && twin?["items"] is JsonArray { Count: 1 },
                    "arched-window: C# and JSON have the same shape; code waits for the next boot merge and JSON already has its item");
                continue;
            }
            var cs = Normal(Blocks.Lowered(n, Sample));
            var json = Normal(Blocks.Lowered(n + "-json", Sample));
            Check(cs.Length > 10 && cs == json, $"{n}: blocks/twins.json lowers to what the C# does ({cs.Length} characters of definitions)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
        Check(Blocks.Find("lockbox", Sample)!.Guid != Blocks.Find("lockbox-json", Sample)!.Guid, "and each has its own ids");
        var csHash = T(G("trestle") + ".prop").Get("ColliderResourceComponent.colliders[0].joltObjects[0].hash").Integer;
        var jsonHash = T(G("trestle-json") + ".prop").Get("ColliderResourceComponent.colliders[0].joltObjects[0].hash").Integer;
        Check(csHash != 0 && jsonHash != 0 && csHash != jsonHash, "the two trestles have distinct physics cache keys derived from their identities");
    }

    // A shape's cells as the game keeps them: the game's own 4 m door wall is c3 c3 c3 c3 c3 ff ff ff (VoxelBlueprintItemRegistryResource).
    private void Shapes()
    {
        var wall = JsonNode.Parse(Blocks.Lowered("door-wall") ?? "{}")?["blueprint"];
        Check((string?)wall?["cells"] == "c3c3c3c3c3ffffff" && wall?["size"]?.ToJsonString() == "[8,8,1]", $"the door wall's cells are the game's own door wall's bytes: {wall?["cells"]} {wall?["size"]?.ToJsonString()}");
        var arch = JsonNode.Parse(Blocks.Lowered("arched-window", Sample) ?? "{}")?["blueprint"];
        Check((string?)arch?["cells"] == "ffffc3c3c3e7ffff" && (int?)arch?["full"] == 50 && (int?)arch?["cost"] == 50, $"the arched window: {arch?["cells"]}, {arch?["full"]} cells, cost {arch?["cost"]} (a block per cell, as the game's shapes)");
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

    // ---- default loot ---------------------------------------------------------------------------------------------

    private static int Count(EntityId e, string item) => Items.Read(e)?.Count(new ItemId(I(item).Id)) ?? 0;
    private readonly List<EntityId> _kept = new();

    private async Task<bool> Until(Func<bool> done, int steps = 240)
    {
        for (var i = 0; i < steps && !done(); i += 2) await Steps(2);
        return done();
    }

    private async Task<EntityId> PlaceKept(string template, WorldPosition at)
    {
        var e = Build.PlaceProp(template, at, null, 0, InBases);
        _kept.Add(e);
        await Steps(10);
        return e;
    }

    private void Drop(EntityId e)
    {
        if (World.Exists(e)) Build.RemoveProp(e, InBases);
        _placed.Remove(e);
        _kept.Remove(e);
    }

    private async Task DefaultLoot(WorldPosition at)
    {
        var cache = G("supply-cache") + ".prop";
        var before = ContainerRuntime.Filled;
        var a = await PlaceKept(cache, Near(at, 5));
        Check(await Until(() => Count(a, Scraps) > 0), $"a placed container is given what its default loot rolls: scraps {Count(a, Scraps)}, twigs {Count(a, Twigs)}");
        Check(Count(a, Scraps) == 3 && Count(a, Twigs) == 2, $"the table's fixed counts: 3 scraps and 2 twigs: {Count(a, Scraps)}, {Count(a, Twigs)}");
        Check(ContainerRuntime.Filled == before + 1, $"and it is counted once: {ContainerRuntime.Filled - before}");
        Check(Count(a, Scraps) > 0 && Items.Read(a)?.AvailableSlots == 24, $"the container is the game's small chest still: {Items.Read(a)?.AvailableSlots} slots");
        // what a player takes out stays out: it is not filled again, however many times the world is searched (every 20 steps)
        Check((await Items.Take(a, new ItemId(I(Scraps).Id), 1)).Succeeded, "a player takes a scrap out");
        await Steps(130);
        Check(Count(a, Scraps) == 2 && Count(a, Twigs) == 2 && ContainerRuntime.Filled == before + 1, $"it is not filled again: scraps {Count(a, Scraps)}, twigs {Count(a, Twigs)}, filled {ContainerRuntime.Filled - before}");

        // the same spot again: the container was taken away and placed once more
        var b = await Place(cache, Near(at, 2));
        await Until(() => Count(b, Scraps) > 0);
        Check(Count(b, Scraps) == 3, $"a second container on another spot is its own fill: {Count(b, Scraps)}");
        Drop(b);
        await Steps(10);
        var again = await Place(cache, Near(at, 2));
        await Steps(130);
        Check(Count(again, Scraps) == 0 && Count(again, Twigs) == 0, $"placed again on the same spot, it is not filled again: scraps {Count(again, Scraps)}, twigs {Count(again, Twigs)}");
        Drop(again);

        // a new spot is a new fill; the data file's twin and a table written in place fill alike; a container with no default loot is left alone
        var c = await PlaceKept(cache, Near(at, 7));
        await Until(() => Count(c, Scraps) > 0);
        Check(Count(c, Scraps) == 3 && Count(c, Twigs) == 2, $"another spot, another fill: {Count(c, Scraps)}, {Count(c, Twigs)}");
        var json = await Place(G("supply-cache-json") + ".prop", Near(at, 2));
        var inline = await Place($"{Me}.inline-cache.prop", Near(at, 3));
        var lockbox = await Place(G("lockbox") + ".prop", Near(at, 4));
        await Until(() => Count(json, Scraps) > 0 && Count(inline, Twigs) > 0);
        await Steps(60);
        Check(Count(json, Scraps) == 3 && Count(json, Twigs) == 2, $"the data file's twin: {Count(json, Scraps)}, {Count(json, Twigs)}");
        Check(Count(inline, Twigs) == 5, $"a table written in the block: {Count(inline, Twigs)}");
        Check((Items.Read(lockbox)?.Slots.Sum(x => x.Count) ?? -1) == 0, "a container with no default loot stays empty");
        foreach (var e in new[] { json, inline, lockbox }) Drop(e);
        await Steps(6);
        Check(ContainerRuntime.Failed == 0, $"no container was given up on: {ContainerRuntime.Failed}");
    }

    // ---- a lid, a swing, boxes ------------------------------------------------------------------------------------

    private static readonly Guid OpenState = Guid.Parse("bcf39dc8-d81e-450a-a77d-3b626d4106c4");

    private static int Boxes(GameDataEntry t)
    {
        var n = 0;
        while (n < 32)
        {
            try { t.Get($"ColliderResourceComponent.colliders[0].dataArray[{n}].shape.halfSize.x"); }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException) { return n; }
            n++;
        }
        return n;
    }

    private void Fields()
    {
        var supply = T(G("supply-cache") + ".prop");
        Check(Has(supply, "ToggleAnimation") && Has(supply, "InventorySetup"), "a container with a default loot keeps its starter's lid");
        foreach (var n in new[] { "barrel", "barrel-json" })
        {
            var t = T(G(n) + ".prop");
            Check(!Has(t, "ToggleAnimation") && Has(t, "InventorySetup"), $"{n}: Lid.Moves = false takes the toggle animation off, and it is still a container");
        }
        foreach (var n in new[] { "one-way-door", "one-way-door-json" })
        {
            var t = T(G(n) + ".prop");
            Check(t.Get("DirectionalInteraction.frontInteraction.animationState").Guid == OpenState && t.Get("DirectionalInteraction.backInteraction.animationState").Guid == OpenState,
                $"{n}: Opens = front: both ways play the front state, as the game's doors that swing one way only: {t.Get("DirectionalInteraction.backInteraction.animationState").Guid}");
        }
        var wood = T(G("night-door") + ".prop");
        Check(wood.Get("DirectionalInteraction.backInteraction.animationState").Guid != OpenState, "a door that says nothing of its swing keeps its starter's two ways");
        foreach (var n in new[] { "trestle", "trestle-json" })
        {
            var t = T(G(n) + ".prop");
            var boxes = Boxes(t);
            Check(boxes == 3, $"{n}: three boxes in its template's collider, and no more: {boxes}");
            var halves = Enumerable.Range(0, 3).Select(i => t.Get($"ColliderResourceComponent.colliders[0].dataArray[{i}].shape.halfSize.x").Number).ToArray();
            var offsets = Enumerable.Range(0, 3).Select(i => t.Get($"ColliderResourceComponent.colliders[0].dataArray[{i}].offset.x").Number).ToArray();
            Check(Math.Abs(halves[0] - 1) < 1e-4 && Math.Abs(halves[1] - 0.075) < 1e-4 && Math.Abs(halves[2] - 0.075) < 1e-4 && Math.Abs(offsets[1] + 0.85) < 1e-4 && Math.Abs(offsets[2] - 0.85) < 1e-4,
                $"{n}: each box has its own size and place: halves [{string.Join(", ", halves)}], offsets [{string.Join(", ", offsets)}]");
            var topY = t.Get("ColliderResourceComponent.colliders[0].dataArray[0].offset.y").Number;
            var legY = t.Get("ColliderResourceComponent.colliders[0].dataArray[2].shape.halfSize.y").Number;
            Check(Math.Abs(topY - 0.9) < 1e-4 && Math.Abs(legY - 0.425) < 1e-4, $"{n}: and so do their heights: top at {topY}, legs {legY} half-height");
            Check(Has(t, "ColliderResourceComponent") && t.Get("ColliderResourceComponent.colliders[0].joltObjects[0].hash").Integer != 0, $"{n}: one physics shape for all of them: hash {t.Get("ColliderResourceComponent.colliders[0].joltObjects[0].hash").Integer}");
            var indices = Enumerable.Range(0, 3).Select(i => t.Get($"ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList[{i}]").Integer).ToArray();
            Check(indices.SequenceEqual(new long[] { 0, 1, 2 }), $"{n}: the physics object uses every box: [{string.Join(", ", indices)}]");
            Check(Content.ItemData.Get(G(n))!.Get("equipment.placementColliders[0].joltObjects[0].hash").Integer != t.Get("ColliderResourceComponent.colliders[0].joltObjects[0].hash").Integer,
                $"{n}: the placement box and the three placed boxes have different physics cache keys");
        }
        var vase = Content.Templates.Get(Content.ItemData.Get("Prop_Decoration_T2_Vase_Clay_01")!.Get("equipment.placedEntity").Guid)!;
        Info($"the game's own vase: {Boxes(vase)} box(es), so the trestle's three are the clone's: its template is longer than its starter's");
    }

    // ---- door sides -----------------------------------------------------------------------------------------------

    private async Task DoorSides(WorldPosition at)
    {
        var door = await Place(G("one-way-door") + ".prop", Near(at, 0));
        var info = Spawn.Inspect(door);
        var p = World.Position(door)!.Value;
        Info($"the one-way door stands at {p}, turned {info?.YawDegrees}");
        var yaw = (info?.YawDegrees ?? 0) * Math.PI / 180;
        WorldPosition Side(double along) => new(p.X + Math.Sin(yaw) * along, p.Y, p.Z + Math.Cos(yaw) * along);
        var front = await Guest(Side(1.3));
        var back = await Guest(Side(-1.3));
        Check(InteractRuntime.SideOf(door, front) == DoorSide.Front && InteractRuntime.SideOf(door, back) == DoorSide.Back, $"Kiln knows the sides of the door: {InteractRuntime.SideOf(door, front)}, {InteractRuntime.SideOf(door, back)}");
        _attempts.Clear();
        var fromBack = await Use(back, door);
        Check(fromBack != true && _attempts.Any(a => a.Host == door && a.Guest == back && a.Canceled), $"Sides = front: a player on the back side is declined by Kiln, and nothing accepts it ({fromBack})");
        var state = DirectionalState(door);
        var fromFront = await Use(front, door);
        Check(fromFront == true, $"and one on the front side is accepted by the game: {fromFront}");
        Info($"the door's directional interaction after the front guest used it: {state} -> {DirectionalState(door)}");
        Spawn.Destroy(back);
        Spawn.Destroy(front);
        // a door with no say about its sides is used from either
        var open = await Place($"{Me}.coded-door.prop", Near(at, 2));
        var p2 = World.Position(open)!.Value;
        var behind = await Guest(new WorldPosition(p2.X, p2.Y, p2.Z - 1.3));
        Check(await Use(behind, open) == true, "a door that says nothing of its sides is used from the back too");
        Remove();
        await Steps(4);
    }

    private static string DirectionalState(EntityId door) => World.ReadBytes(door, "DirectionalInteraction") is { Length: > 0 } b ? $"state {b[0]}" : "none";

    // ---- several colliders ----------------------------------------------------------------------------------------

    private async Task Colliders(WorldPosition at)
    {
        foreach (var n in new[] { "trestle", "trestle-json" })
        {
            var e = await Place(G(n) + ".prop", Near(at, 1));
            Check(!e.IsNone && Spawn.Inspect(e)?.Template == T(G(n) + ".prop").Guid, $"{n}: a prop whose template has three boxes places: {e} {Spawn.Inspect(e)?.TemplateName}");
            await Steps(20);
            var collider = World.ComponentSize(e, "Collider");
            Check(World.Exists(e) && Spawn.Inspect(e)?.Template == T(G(n) + ".prop").Guid && Boxes(T(G(n) + ".prop")) == 3, $"{n}: and stays placed with its own three-box template");
            Info($"{n}: runtime Collider has {collider} bytes on this headless server; physical bumping needs a joined player");
            Remove();
            await Steps(4);
        }
    }

    // ---- crafting props -------------------------------------------------------------------------------------------

    private static long Mask(EntityId station) => World.ReadBytes(station, "CraftingInteraction") is { Length: >= 8 } b ? BitConverter.ToInt64(b, 0) : -1;

    private async Task<ItemCraftedEvent?> CraftOnce(EntityId guest, EntityId station, uint recipe)
    {
        _crafted.Clear();
        Gameplay.Inject.Craft(guest, station, recipe);
        await Until(() => _crafted.Any(e => e.Operator == guest && e.RecipeId == recipe));
        return _crafted.LastOrDefault(e => e.Operator == guest && e.RecipeId == recipe);
    }

    // JSON's prop is in the table from the first start; code's from the second. Craft requests use the game's real prop check.
    private async Task CraftingProps(WorldPosition at, string suffix)
    {
        var table = "rune-table" + suffix;
        var benchName = "rune-bench" + suffix;
        var props = WorldTables.CraftingProps;
        Info($"crafting props in the game's table: {string.Join(", ", props.Select(x => $"{x.Mod}:{x.Block} place {x.Index} id 0x{x.Id:x8}"))}");
        var mine = props.FirstOrDefault(x => x.Mod == Sample && x.Block == table);
        Check(mine != null, $"{table} has a place in the game's table");
        if (mine == null) return;
        var guid = Compiler.CraftingPropGuid(Sample, table);
        Check(mine.Index is >= 51 and <= 63 && mine.Guid == guid && mine.Id == BlockIds.Fnv(guid) && WorldTables.CraftingPropIndex(guid) == mine.Index, $"{table}: a free place of the game's 64 ({mine.Index}), the id of its GUID (0x{mine.Id:x8})");
        Check(WorldTables.Hash != WorldTablesNative.EmptyHash, $"the handshake hash says it: {WorldTables.Hash}");
        var t = T(G(table) + ".prop");
        Check((uint)t.Get("RequiredCraftingProp.craftingPropId").Integer == mine.Id, $"{table}: the template is the crafting prop of that id");
        var recipe = Content.Recipes.Get(G(benchName + ".makes.Material_T1_Wood_Logs"));
        Check(recipe != null, $"{benchName}: the recipe that needs it is made");
        if (recipe == null) return;
        var wanted = recipe.Get("requiredPropsMask").Integer;
        Check(wanted == 1L << mine.Index, $"{benchName}: the recipe's props mask is that place: 0x{wanted:x}");

        var bench = await Place(G(benchName) + ".station", Near(at, 0));
        var guest = await Guest(Near(at, 3));
        await Items.Give(guest, Twigs, 6);
        var before = await CraftOnce(guest, bench, recipe.Id);
        Check(before is { Result: CraftResult.MissingCraftingProps } && (Mask(bench) & wanted) == 0 && Count(guest, Twigs) == 6 && Count(guest, "Material_T1_Wood_Logs") == 0,
            $"no rune table near: the game refuses the craft (raw {before?.RawResult}), takes no inputs and gives no output; mask 0x{Mask(bench):x}");
        var rune = await Place(G(table) + ".prop", Near(at, 1));
        Check(await Until(() => (Mask(bench) & wanted) != 0, 160), $"with the table 2.5 m away the station's props in range have its place: mask 0x{Mask(bench):x}");
        var made = await CraftOnce(guest, bench, recipe.Id);
        Check(made is { Succeeded: true } && Count(guest, Twigs) == 4 && Count(guest, "Material_T1_Wood_Logs") == 1,
            $"with the table the real craft request succeeds (raw {made?.RawResult}): twigs {Count(guest, Twigs)}, logs {Count(guest, "Material_T1_Wood_Logs")}");
        Build.RemoveProp(rune, InBases);
        _placed.Remove(rune);
        Check(await Until(() => (Mask(bench) & wanted) == 0, 160), $"the table taken away, the place goes from the station's mask: 0x{Mask(bench):x}");
        var after = await CraftOnce(guest, bench, recipe.Id);
        Check(after is { Result: CraftResult.MissingCraftingProps } && Count(guest, Twigs) == 4 && Count(guest, "Material_T1_Wood_Logs") == 1,
            $"after removal the game refuses again (raw {after?.RawResult}) and keeps inventory unchanged");
        Spawn.Destroy(guest);
        Remove();
        await Steps(4);
    }

    // ---- restarts -----------------------------------------------------------------------------------------------

    private async Task<WorldPosition> OpenGround(double x = 3024, double z = 1550)
    {
        var box = Build.BoxBetween(new WorldPosition(x, 700, z), new WorldPosition(x, 1100, z));
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
        var open = await OpenGround();
        var outside = Build.PlaceProp(G("camp-bed") + ".prop", open, null, 0, new BuildOptions());
        await Steps(24);
        var state = new JsonObject
        {
            ["box"] = Pos(box), ["door"] = Pos(door), ["bed"] = Pos(bed), ["outside"] = Pos(outside),
            ["doorState"] = World.ReadBytes(door, "ServerToggleState")?[0] ?? 255, ["gameDoor"] = Pos(gameDoor),
            ["itemBox"] = I(G("lockbox")).Guid.ToString(), ["templateBox"] = T(G("lockbox") + ".prop").Guid.ToString(), ["templateDoor"] = T($"{Me}.coded-door.prop").Guid.ToString(),
            ["templateBed"] = T(G("camp-bed") + ".prop").Guid.ToString(), ["hair"] = I(G("short-crop")).Guid.ToString(),
            ["cacheA"] = Pos(_kept[0]), ["cacheC"] = Pos(_kept[1]),
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
        Twins();
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

        // the default loot was given once: the record is in the world's data, so a restart fills nothing again and what a player took out stays out
        var cacheT = T(G("supply-cache") + ".prop").Guid;
        var a = At(s["cacheA"], cacheT);
        var c = At(s["cacheC"], cacheT);
        Check(!a.IsNone && Count(a, Scraps) == 2 && Count(a, Twigs) == 2, $"after the restart the first cache holds what was left in it, and is not filled again: scraps {(a.IsNone ? -1 : Count(a, Scraps))}, twigs {(a.IsNone ? -1 : Count(a, Twigs))}");
        Check(!c.IsNone && Count(c, Scraps) == 3 && Count(c, Twigs) == 2, $"and the second holds its fill, no more: scraps {(c.IsNone ? -1 : Count(c, Scraps))}, twigs {(c.IsNone ? -1 : Count(c, Twigs))}");
        Check(ContainerRuntime.Filled == 0, $"the restart filled nothing: {ContainerRuntime.Filled}");
        foreach (var e in new[] { a, c })
            if (!e.IsNone) Build.RemoveProp(e, InBases);
        await Steps(4);

        // the code's crafting prop and station: in the game's table from this start, and what they lowered to is what the data file's twins lowered to
        foreach (var n in new[] { "rune-table", "rune-bench" })
        {
            var cs = Normal(Blocks.Lowered(n, Sample));
            var js = Normal(Blocks.Lowered(n + "-json", Sample));
            Check(cs.Length > 10 && cs == js, $"{n}: blocks/twins.json lowers to what the C# does ({cs.Length} characters of definitions)");
            if (cs != js) Info($"C#: {cs}\nJSON: {js}");
        }
        await CraftingProps(BaseSpot(), "");
        var saved = await Saves.Request();
        Check(saved.Success, $"saved without them: {saved}");
    }
}
