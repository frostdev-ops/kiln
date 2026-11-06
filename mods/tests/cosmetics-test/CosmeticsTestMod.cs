// SPDX-License-Identifier: MIT
// What Kiln.Blocks makes of cosmetic blocks, with no player, on a dedicated server: registration, ids across a restart, C# and JSON the same, the mistakes a look can make, per-body models, and a piece
// in a vanity slot of a player-template entity. Drawing it on a body is a client's: docs/kiln/modding/cosmetics.md "Manual checks".
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace CosmeticsTest;

public sealed class CosmeticsTestMod : Mod
{
    private const string Sample = "cosmetic-sample", Me = "cosmetics-test", Low = "cosmetics-low";
    private const string Tricorn = "Vanity_TX_Helmet_05_tricorn", WarriorChest = "Armor_T1_Warrior_DPS_Chest";
    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private WorldPosition _at;
    private EntityId _chest;

    private void Info(string s) => Log.Info($"cosmetics-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"cosmetics-test: PASS {what}"); }
        else { _fail++; Log.Error($"cosmetics-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static string G(string mod, string block) => $"{mod}.{block}";
    private static GameDataEntry D(string item) => Content.ItemData.Get(item) ?? throw new InvalidOperationException($"no item {item}");
    private static JsonNode? V(GameDataEntry d, string path) => d.GetJson(path)?["value"];
    private static List<string> Flags(GameDataEntry d, string path) => V(d, path)?.AsArray().Select(x => x!.ToString()).ToList() ?? [];
    private static string Ref(GameDataEntry d, string path) => V(d, path)?.ToString() ?? "";

    private CosmeticBlock? _coat;

    /// <summary>How many entries a list of an item has: a path past its end is refused.</summary>
    private static int Count(GameDataEntry d, string list)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                if (d.GetJson($"{list}[{i}]") is null) return i;
            }
            catch (KeyNotFoundException)
            {
                return i;
            }
        }
    }

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        Blocks.Define("coat", _coat = new CosmeticBlock
        {
            Name = "Long Coat",
            Slot = CosmeticSlot.UpperBody,
            Dyes = true,
            Look = new WornLook
            {
                Model = "models/plain.gltf",
                Female = "models/female.gltf",
                Variants = { [Fit.Shield] = "models/shield.gltf" },
                Attach = WornAttach.Chest,
                Fits = Fit.NoArmor,
                Hides = [BodyPart.Chest, BodyPart.Waist, BodyPart.ArmorLowerBody],
            },
        });
        Blocks.Define("hides-shield", new CosmeticBlock
        {
            Name = "Shield Hider",
            Slot = CosmeticSlot.Arms,
            Look = new WornLook { Model = "models/plain.gltf", Attach = WornAttach.Chest, Female = "models/female.gltf", Hides = [BodyPart.Shield, BodyPart.Hands] },
        });
        // armor wears a look too
        Blocks.Define("coat-armor", new ArmorBlock
        {
            Name = "Coat Armor", Slot = ArmorSlot.Chest, Stats = new ArmorStats { Level = 5, Physical = 30 },
            Look = new WornLook { Model = "models/plain.gltf", Female = "models/female.gltf", Attach = WornAttach.Chest, Hides = [BodyPart.Chest, BodyPart.Waist] },
        });
        Blocks.Define("like-armor", new ArmorBlock { Name = "Like Armor", Slot = ArmorSlot.Chest, Visual = new VisualBlock { Like = WarriorChest } });
        Blocks.Define("mixed-armor", new ArmorBlock
        {
            Name = "Mixed Armor", Slot = ArmorSlot.Chest, Visual = new VisualBlock { Like = WarriorChest },
            Look = new WornLook { Model = "models/plain.gltf", Attach = WornAttach.Chest, Hides = [BodyPart.Chest] },
        });
        var wornSet = Blocks.Define("worn-set", new ArmorSet { Head = new WornLook { Model = "models/shield.gltf", Attach = WornAttach.Chest }, Chest = new VisualBlock { Like = WarriorChest } });
        Blocks.Define("set-helm", new ArmorBlock { Name = "Set Helm", Slot = ArmorSlot.Head, Set = wornSet });
        Blocks.Define("set-mail", new ArmorBlock { Name = "Set Mail", Slot = ArmorSlot.Chest, Set = wornSet });
        var part = Blocks.Define("a-handle-look", new WornLook { Like = Tricorn, Hides = [BodyPart.Hair] });
        Blocks.Define("by-handle", new CosmeticBlock { Name = "By Handle", Slot = CosmeticSlot.Head, Look = part });
        foreach (var slot in Enum.GetValues<CosmeticSlot>())
            foreach (var dyes in new[] { false, true })
                Blocks.Define($"slot-{slot}-{dyes}".ToLowerInvariant(), new CosmeticBlock { Name = $"{slot} {dyes}", Slot = slot, Dyes = dyes, Look = new WornLook { Like = Tricorn } });
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            await Part("mistakes", () => { Mistakes(); return Task.CompletedTask; });
            await Part("registered", () => { Registered(); return Task.CompletedTask; });
            await Part("slots", () => { Slots(); return Task.CompletedTask; });
            await Part("twins", () => { Twins(); return Task.CompletedTask; });
            await Part("game looks", () => { GameLooks(); return Task.CompletedTask; });
            await Part("per body", () => { PerBody(); return Task.CompletedTask; });
            await Part("files", () => { Files(); return Task.CompletedTask; });
            await Part("armor looks", () => { ArmorLooks(); return Task.CompletedTask; });
            FindChest();
            await Part("ids", Ids);
            _at = await Anchor();
            await Part("worn", Worn);
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"cosmetics-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Part(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception: {e}"); }
    }

    private void Refused(string what, Func<CosmeticBlock> block, params string[] words)
    {
        try
        {
            Blocks.Define("refused", block());
            Check(false, $"{what}: nothing said no");
        }
        catch (BlockError e)
        {
            Check(words.All(w => e.Message.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{what}: \"{e.Message}\"");
        }
    }

    private void Mistakes()
    {
        var mine = Blocks.Report().GetValueOrDefault(Me);
        void Named(string block, params string[] words)
        {
            var e = mine.Errors?.FirstOrDefault(x => Regex.IsMatch(x, $@"\b{Regex.Escape(block)}\b"));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
            Check(Blocks.Find(block) == null && Content.ItemData.Get(G(Me, block)) == null, $"{block} made nothing");
        }
        Named("nothing-look", "model", "like");
        Named("bad-attach", "attach", "Tail", "Head");
        Named("bad-sizes", "pixelSizes", "2 pixel sizes", "1 level");
        Named("no-such-slot", "neck");
        Named("no-such-look", "look", "gone-look");
        Named("wrong-kind-look", "look", "by-name");
        Named("no-name-piece", "name");
        Named("typo-piece", "dies");
        Named("missing-file", "model", "models/gone.glb", "not in the mod's folder");
        Named("outside-file", "model", "relative");
        Named("not-a-model", "model", ".png", ".glb");
        Named("bad-fit", "wig", "hood");
        Named("female-fit", "fits", "female");
        Named("bad-part", "tail");
        Named("unheard-variant", "variants[tight]", "Head", "Tight");
        Named("same-variant", "variants[female]", "same");
        Named("attach-game-look", "attach", "model file");
        Named("bad-recipe", "recipe.needs");
        Check(Blocks.Find("shared-look") != null && Blocks.Find("by-name") != null, "a wrong block leaves the file's others alone");

        Refused("code: a look with nothing", () => new CosmeticBlock { Name = "x", Slot = CosmeticSlot.Head, Look = new WornLook() }, "model");
        Refused("code: no name", () => new CosmeticBlock { Name = " ", Slot = CosmeticSlot.Head, Look = new WornLook { Like = Tricorn } }, "name");
        Refused("code: a slot that isn't", () => new CosmeticBlock { Name = "x", Slot = (CosmeticSlot)9, Look = new WornLook { Like = Tricorn } }, "slot", "UpperBody");
        Refused("code: a part of the wrong kind", () => new CosmeticBlock { Name = "x", Slot = CosmeticSlot.Head, Look = "coat" }, "look", "coat");
        Refused("code: a file not there", () => new CosmeticBlock { Name = "x", Slot = CosmeticSlot.Head, Look = new WornLook { Model = "models/nope.glb" } }, "not in the mod's folder");
        Refused("code: a variant on a flag its slot doesn't hear", () => new CosmeticBlock { Name = "x", Slot = CosmeticSlot.Feet, Look = new WornLook { Model = "models/plain.gltf", Variants = { [Fit.Tight] = "models/shield.gltf" } } }, "Feet", "Tight");
        Refused("code: a fit that is no variant", () => new CosmeticBlock { Name = "x", Slot = CosmeticSlot.UpperBody, Look = new WornLook { Model = "models/plain.gltf", Variants = { [Fit.None] = "models/shield.gltf" } } }, "variant");
        Check(Blocks.Find("refused") == null, "what was refused is not there");

        var low = Blocks.Report().GetValueOrDefault(Low);
        Check(low.Errors?.Any(e => e.Contains("needs-files") && e.Contains("look.model") && e.Contains("<KilnClients>mod")) == true, $"a look with files needs clients mod: \"{low.Errors?.FirstOrDefault(e => e.Contains("needs-files"))}\"");
        Check(low.Errors?.Any(e => e.Contains("needs-icon") && e.Contains("look.icon") && e.Contains("<KilnClients>mod")) == true, $"a PNG icon needs clients mod: \"{low.Errors?.FirstOrDefault(e => e.Contains("needs-icon"))}\"");
        Check(Content.ItemData.Get(G(Low, "game-look")) != null, "a look like a game item's asks no more than kiln");
    }

    private void Registered()
    {
        var hood = D(G(Sample, "frost-hood"));
        Check(Content.Items.Get(G(Sample, "frost-hood")) != null, "the hood is an item of the game's registry");
        Check(hood.Get("equipment.slot").Name == "Armour_Head", $"it says the head's armour slot: {hood.Get("equipment.slot").Name}");
        var flags = Flags(hood, "flags");
        Check(flags.Contains("IsVanityItem") && flags.Contains("IsNonTintable") && !flags.Contains("HasPIDE"), $"a vanity item that takes no dye: {string.Join(",", flags)}");
        Check(!flags.Contains("TwitchDrop") && !flags.Contains("CommerceItem"), "with no drop or shop flag");
        Check(Flags(hood, "equipment.hideMask").SequenceEqual(["BodyPart_Hair"]), $"it hides the hair: {string.Join(",", Flags(hood, "equipment.hideMask"))}");
        Check(Flags(hood, "equipment.blendShapeMask").SequenceEqual(["Hood"]), $"it raises Hood: {string.Join(",", Flags(hood, "equipment.blendShapeMask"))}");
        var want = BlockIds.Derive(Sample, "model", "models/frost-hood").ToString();
        Check(Ref(hood, "equipment.visualModel") == want, $"its model is what the cook names: {Ref(hood, "equipment.visualModel")}");
        Check(Ref(hood, "iconModel") == want, "and its icon draws it");
        Check(Ref(hood, "equipment.visualEntity") is "" or "none" or "00000000-0000-0000-0000-000000000000", $"with no visual entity: \"{Ref(hood, "equipment.visualEntity")}\"");
        Check(Count(hood, "equipment.visualModels") == 0, "and no per-body models: only its own model is drawn");
        Check(V(hood, "uiValues")?.AsArray().Count is null or 0, "its tooltip has no rows");
        var recipe = Content.Recipes.Get(G(Sample, "frost-hood") + ".recipe");
        Check(recipe != null && recipe.Inputs.Count == 1 && recipe.Inputs[0].Count == 12, $"it has its recipe: {recipe?.Name}, {string.Join(", ", recipe?.Inputs ?? [])}");

        var cape = D(G(Sample, "wander-cape"));
        var capeFlags = Flags(cape, "flags");
        Check(cape.Get("equipment.slot").Name == "Armour_UpperBody", "the cape goes on the upper body");
        Check(capeFlags.Contains("HasPIDE") && !capeFlags.Contains("IsNonTintable"), $"Dyes: an item entity, and tintable: {string.Join(",", capeFlags)}");
        Check(Flags(cape, "equipment.hideMask").Count == 0, $"nothing said hidden is hidden, whatever its starter hid: {string.Join(",", Flags(cape, "equipment.hideMask"))}");
        Check(Flags(cape, "equipment.blendShapeMask").Count == 0, "and nothing raised");
    }

    private void Slots()
    {
        var names = new Dictionary<CosmeticSlot, string> { [CosmeticSlot.Head] = "Armour_Head", [CosmeticSlot.UpperBody] = "Armour_UpperBody", [CosmeticSlot.Arms] = "Armour_Arms", [CosmeticSlot.LowerBody] = "Armour_LowerBody", [CosmeticSlot.Feet] = "Armour_Feet" };
        foreach (var slot in Enum.GetValues<CosmeticSlot>())
            foreach (var dyes in new[] { false, true })
            {
                var d = D(G(Me, $"slot-{slot}-{dyes}".ToLowerInvariant()));
                var flags = Flags(d, "flags");
                Check(d.Get("equipment.slot").Name == names[slot] && flags.Contains("IsVanityItem") && flags.Contains("HasPIDE") == dyes && flags.Contains("IsNonTintable") != dyes && !flags.Contains("TwitchDrop") && !flags.Contains("CommerceItem"),
                    $"{slot}, dyes {dyes}: {d.Get("equipment.slot").Name}, {string.Join(",", flags)}");
                var armor = d.GetJson("armorSetup.isSet")?["value"]?.GetValue<bool>();
                var maxLevel = d.Get("itemLevelRange.maxLevel").Number;
                Check(armor != true && d.Get("armorSetup.distribution.physical").Number == 0, $"it gives no armour: {slot} physical {d.Get("armorSetup.distribution.physical").Number}");
                Check(d.Get("generateRarity").Number == 0 && d.Get("itemLevelRange.minLevel").Number == maxLevel, $"made as it reads: one level, level {maxLevel}, rarity {d.Get("rarity").Name}");
            }
    }

    private void Twins()
    {
        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", m => m.Value == BlockIds.Derive(Sample, "model", "models/frost-hood").ToString() ? "MODEL" : "GUID"), "-data", "");
        var cs = Normal(Blocks.Lowered("frost-hood", Sample));
        var json = Normal(Blocks.Lowered("frost-hood-data", Sample));
        Check(cs.Length > 40 && cs == json, $"blocks/cosmetics.json lowers to what the C# does ({cs.Length} characters of definitions)");
        if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        var a = D(G(Sample, "frost-hood"));
        var b = D(G(Sample, "frost-hood-data"));
        foreach (var path in new[] { "equipment.slot", "equipment.visualModel", "equipment.hideMask", "equipment.blendShapeMask", "iconModel", "flags", "rarity", "maxStackSize", "equipment.impactEntity", "pidEntity" })
            Check(a.GetJson(path)?["value"]?.ToJsonString() == b.GetJson(path)?["value"]?.ToJsonString(), $"{path} is the same: {a.GetJson(path)?["value"]?.ToJsonString()}");
        Check(Blocks.Find("frost-hood", Sample)!.Guid != Blocks.Find("frost-hood-data", Sample)!.Guid, "as two items, each with a name of its own");
    }

    private void GameLooks()
    {
        var game = D(Tricorn);
        var plain = D(G(Sample, "plain-tricorn"));
        Check(Ref(plain, "equipment.visualModel") == Ref(game, "equipment.visualModel"), $"a look like a game item's has its model: {Ref(plain, "equipment.visualModel")}");
        Check(Ref(plain, "iconModel") == Ref(game, "iconModel"), "and its icon");
        Check(Flags(plain, "equipment.blendShapeMask").SequenceEqual(Flags(game, "equipment.blendShapeMask")), $"and what it raises: {string.Join(",", Flags(plain, "equipment.blendShapeMask"))}");
        Check(Flags(plain, "equipment.hideMask").SequenceEqual(Flags(game, "equipment.hideMask")), "and what it hides");
        var hides = D(G(Me, "by-handle"));
        Check(Flags(hides, "equipment.hideMask").SequenceEqual(["BodyPart_Hair"]), $"what a look says over the item's wins: hides {string.Join(",", Flags(hides, "equipment.hideMask"))}");
        Check(Flags(hides, "equipment.blendShapeMask").SequenceEqual(Flags(game, "equipment.blendShapeMask")), "and what it doesn't say stays the item's");
        Check(Ref(hides, "equipment.visualModel") == Ref(game, "equipment.visualModel"), "a look by its handle");
        var named = D(G(Me, "by-name"));
        Check(Flags(named, "equipment.blendShapeMask").SequenceEqual(["Hat"]) && Ref(named, "equipment.visualModel") == Ref(game, "equipment.visualModel"), "a look by its name, from a data file");
        var other = D(G(Me, "other-mods-look"));
        Check(Ref(other, "equipment.visualModel") == BlockIds.Derive(Sample, "model", "models/frost-hood").ToString(), $"a look of another mod: its file is that mod's: {Ref(other, "equipment.visualModel")}");
    }

    private static uint Mask(params string[] names) => names.Aggregate(0u, (m, n) => m | (uint)Enum.Parse<Fit>(n));

    private void PerBody()
    {
        var plain = BlockIds.Derive(Me, "model", "models/plain").ToString();
        var female = BlockIds.Derive(Me, "model", "models/female").ToString();
        var shield = BlockIds.Derive(Me, "model", "models/shield").ToString();
        var coat = D(G(Me, "coat"));
        Check(Ref(coat, "equipment.visualModel") == plain, "the coat's plain model");
        var entries = new Dictionary<uint, string>();
        for (var i = 0; i < Count(coat, "equipment.visualModels"); i++)
        {
            var mask = coat.GetJson($"equipment.visualModels[{i}].mask")?["value"];
            if (mask == null) break;
            var m = mask is JsonArray arr ? arr.Select(x => x!.ToString()).Aggregate(0u, (acc, n) => acc | (uint)Enum.Parse<Fit>(n)) : mask.GetValue<uint>();
            entries[m] = Ref(coat, $"equipment.visualModels[{i}].model");
        }
        Info($"visualModels: {string.Join(", ", entries.Select(e => $"0x{e.Key:x}={(e.Value == plain ? "plain" : e.Value == female ? "female" : e.Value == shield ? "shield" : e.Value)}"))}");
        // the upper body hears Female, Shield, Tight and ArmsTight01. Of the 15 sets that aren't empty, 3 have neither Female nor Shield and stay with the plain model
        Check(entries.Count == 12, $"every set of flags with a variant has its entry: {entries.Count} of 12");
        void Is(string what, string want, params string[] flags) => Check(entries.GetValueOrDefault(Mask(flags)) == want, $"{what}: {string.Join("+", flags)} draws {(want == female ? "the female file" : "the shield file")}");
        Is("a female body", female, "Female");
        Is("the trap: tight gloves would have dropped a female body to the plain model", female, "Female", "Tight");
        Is("and with every flag", female, "Female", "Tight", "ArmsTight01");
        Is("a shield", shield, "Shield");
        Is("a shield with tight gloves", shield, "Shield", "Tight");
        Is("a tie goes to the body", female, "Female", "Shield");
        Check(!entries.ContainsKey(Mask("Tight")) && !entries.ContainsKey(Mask("ArmsTight01")), "flags with no variant are left to the plain model");
        Check(Flags(coat, "equipment.hideMask").SequenceEqual(["BodyPart_Chest", "BodyPart_Waist", "Armour_LowerBody"]), $"it hides parts and another slot's piece: {string.Join(",", Flags(coat, "equipment.hideMask"))}");
        Check(Flags(coat, "equipment.blendShapeMask").SequenceEqual(["NoArmor"]), "and raises NoArmor");

        var arms = D(G(Me, "hides-shield"));
        var n = Count(arms, "equipment.visualModels");
        Check(n == 1 && Ref(arms, "equipment.visualModels[0].model") == female, $"a look that hides the shield doesn't hear Shield: its one flag is Female, {n} entry");

        var sync = JsonNode.Parse(NewContent.DefinitionsJson());
        Check(sync is JsonArray a && a.Count > 0, "the definitions are there for a Kiln client");
    }

    private void ArmorLooks()
    {
        var plain = BlockIds.Derive(Me, "model", "models/plain").ToString();
        var female = BlockIds.Derive(Me, "model", "models/female").ToString();
        var shield = BlockIds.Derive(Me, "model", "models/shield").ToString();
        var warrior = D(WarriorChest);

        var coat = D(G(Me, "coat-armor"));
        Check(Ref(coat, "equipment.visualModel") == plain && Ref(coat, "iconModel") == plain, $"armor with a look of its own draws it: {Ref(coat, "equipment.visualModel")}");
        var n = Count(coat, "equipment.visualModels");
        var all = Enumerable.Range(0, n).All(i => Ref(coat, $"equipment.visualModels[{i}].model") == female);
        Check(n == 8 && all, $"and the female file on every set of flags with Female: {n} entries");
        Check(Flags(coat, "equipment.hideMask").SequenceEqual(["BodyPart_Chest", "BodyPart_Waist"]), $"what it hides: {string.Join(",", Flags(coat, "equipment.hideMask"))}");
        Check(Ref(coat, "equipment.visualEntity") is "" or "none" or "00000000-0000-0000-0000-000000000000", "no visual entity of the starter's");
        Check(coat.Get("armorSetup.distribution.physical").Number > 0 && Flags(coat, "flags").Contains("IsVanityItem") == false, $"and it is still armor: physical share {coat.Get("armorSetup.distribution.physical").Number}");

        var like = D(G(Me, "like-armor"));
        Check(Ref(like, "equipment.visualModel") == Ref(warrior, "equipment.visualModel"), $"Visual.Like still gives a game item's look: {Ref(like, "equipment.visualModel")}");
        Check(Flags(like, "equipment.hideMask").SequenceEqual(Flags(warrior, "equipment.hideMask")) && Count(like, "equipment.visualModels") == Count(warrior, "equipment.visualModels"),
            $"with what it hides and its per-body models: {Count(like, "equipment.visualModels")} of {Count(warrior, "equipment.visualModels")}");

        var mixed = D(G(Me, "mixed-armor"));
        Check(Ref(mixed, "equipment.visualModel") == plain, "Look wins over Visual.Like for what is worn");
        Check(Flags(mixed, "equipment.hideMask").SequenceEqual(["BodyPart_Chest"]), $"and its hides win over the item's: {string.Join(",", Flags(mixed, "equipment.hideMask"))}");
        Check(Count(mixed, "equipment.visualModels") == 0, $"and the item's per-body models are gone: {Count(mixed, "equipment.visualModels")}");
        Check(Flags(mixed, "equipment.blendShapeMask").Count == 0, $"and what the item raised: {string.Join(",", Flags(mixed, "equipment.blendShapeMask"))}");

        Check(Ref(D(G(Me, "set-helm")), "equipment.visualModel") == shield, "a set's worn look for the head");
        Check(Ref(D(G(Me, "set-mail")), "equipment.visualModel") == Ref(warrior, "equipment.visualModel"), "and a game item's look for the chest, in the same set");
        Check(Ref(D(G(Me, "set-helm-json")), "equipment.visualModel") == shield, "the same set in data");

        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "-json", "");
        foreach (var name in new[] { "coat-armor", "set-helm" })
        {
            var cs = Normal(Blocks.Lowered(name, Me));
            var json = Normal(Blocks.Lowered(name + "-json", Me));
            Check(cs.Length > 40 && cs == json, $"{name}: blocks/armor.json lowers to what the C# does ({cs.Length} characters)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
        try
        {
            Blocks.Define("ring-look", new ArmorBlock { Name = "x", Slot = ArmorSlot.Ring, Look = new WornLook { Like = Tricorn } });
            Check(false, "a ring with a look: nothing said no");
        }
        catch (BlockError e)
        {
            Check(e.Message.Contains("ring") && e.Message.Contains("visual"), $"a ring isn't drawn on the body: \"{e.Message}\"");
        }
    }

    private void Files()
    {
        var dir = Mods.Get(Sample)!.Directory;
        var side = Path.Combine(dir, "assets", "cooked", "models_frost-hood.model.json");
        if (!Check(File.Exists(side), "the sample is cooked")) return;
        var j = JsonNode.Parse(File.ReadAllText(side))!;
        Check((string?)j["guid"] == BlockIds.Derive(Sample, "model", "models/frost-hood").ToString(), $"the cook and the block say the same GUID: {j["guid"]}");
        Check((string?)j["skeleton"] == "87b29df6-6518-43ef-9298-e3f85a6204b9", "on the player's skeleton");
        Check(j["morphs"]!.AsArray().Select(x => (string?)x).SequenceEqual(["Female"]), "with a Female shape");
        Check(j["joints"]!.AsArray().Count == 1, $"rigid, on one joint: {j["joints"]!.ToJsonString()}");
        Check(Assets.Available && Assets.ReadResource(new AssetKey(BlockIds.Derive(Sample, "model", "models/frost-hood"), "keen::RenderModel")) is { Length: > 0 }, "kiln.assets serves the model");
        Check(Blocks.Warnings(Sample).Count == 0, $"no warning for the sample on this server: {string.Join("; ", Blocks.Warnings(Sample))}");
        Check(Blocks.Report().GetValueOrDefault(Sample).Errors?.Count == 0, $"no error: {string.Join("; ", Blocks.Report().GetValueOrDefault(Sample).Errors ?? [])}");
    }

    private void FindChest()
    {
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 4 && (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                _chest = e;
                break;
            }
        Check(!_chest.IsNone, $"a saved container to work with: {_chest}");
    }

    private async Task Ids()
    {
        string[] names = ["frost-hood", "frost-hood-data", "wander-cape", "plain-tricorn"];
        var now = new JsonObject();
        foreach (var n in names)
        {
            var h = Blocks.Find(n, Sample)!;
            now[n] = h.Guid.ToString();
            Check(h.Guid == BlockIds.Derive(Sample, "item", n), $"{n}: its id follows from its name: {h.Guid}");
        }
        now["recipe"] = Content.Recipes.Get(G(Sample, "frost-hood") + ".recipe")!.Guid.ToString();
        now["model"] = BlockIds.Derive(Sample, "model", "models/frost-hood").ToString();
        Directory.CreateDirectory(Context.DataDirectory);
        var file = Path.Combine(Context.DataDirectory, "ids.json");
        string[] stored = ["frost-hood", "wander-cape"];
        if (File.Exists(file))
        {
            var was = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var (k, v) in now) Check(was[k]?.ToString() == v?.ToString(), $"after the restart {k} is still {v}");
            foreach (var block in stored)
            {
                var id = new ItemId(Blocks.Find(block, Sample)!.Id);
                var holder = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty).FirstOrDefault(e => Items.Read(e)?.Count(id) > 0);
                var c = holder.IsNone ? null : Items.Read(holder);
                var want = (int?)was[$"count:{block}"] ?? 1;
                Check(c?.Count(id) == want, $"the save kept {block}: {c?.Count(id)} of {want}, in {holder}");
                if (!holder.IsNone) await Items.Take(holder, id, c!.Count(id));
            }
            File.Delete(file);
            return;
        }
        foreach (var block in stored)
        {
            var g = await Items.Give(_chest, new ItemId(Blocks.Find(block, Sample)!.Id), 1);
            Check(g.Status == ItemsStatus.Done, $"give {block} to the chest for the restart: {g}");
            await Steps(2);
            now[$"count:{block}"] = Items.Read(_chest)?.Count(new ItemId(Blocks.Find(block, Sample)!.Id)) ?? 0;
        }
        File.WriteAllText(file, now.ToJsonString());
        Info("ids written: restart the lab for the second phase");
    }

    private async Task<WorldPosition> Anchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 40, inf.Position.Y + 0.5, inf.Position.Z + 40);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

    private async Task<EntityId> Make(string template, WorldPosition at)
    {
        var e = await Spawn.Entity(template, at, 0, new SpawnOptions { Tag = "cosmetics-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(10);
        return e;
    }

    // keen::EquipmentSlot: Vanity_Head to Vanity_Feet are 0..4, Armour_Head to Armour_Feet 5..9
    private const int VanityHead = 0, VanityUpperBody = 1, ArmourUpperBody = 6;

    private static string Armour(EntityId wearer)
    {
        if (World.ReadBytes(wearer, "ArmorPoints") is not { Length: >= 16 } b) return "?";
        var off = BitConverter.ToUInt16(b, 4);
        var r = new List<uint>();
        for (var o = off; o + 4 <= Math.Min(b.Length, off + 24); o += 4) r.Add(BitConverter.ToUInt32(b, o));
        return string.Join(",", r);
    }

    private static List<uint> Equipped(EntityId wearer)
    {
        var r = new List<uint>();
        if (World.ReadBytes(wearer, "Equipment") is not { } e) return r;
        for (var slot = 0; 8 + slot * 0x48 + 12 <= e.Length; slot++) r.Add(BitConverter.ToUInt32(e, 8 + slot * 0x48 + 8));
        return r;
    }

    // A spawned player-template entity has no equipment slots set (a join sets them), so the test says which slot is which, through a switch only a lab has. This goes as far as a server can:
    // the piece sits in the slot, and the game's armour program leaves it out. What a client resolves and draws is the client's.
    private async Task Worn()
    {
        var wearer = await Make("1_Player", new WorldPosition(_at.X - 4, _at.Y, _at.Z - 4));
        var inv = Items.Read(wearer)!;
        var equipment = inv.Slots.First(s => s.Category == InventoryCategory.Equipment).Slot.Inventory;
        var hood = Blocks.Find("frost-hood", Sample)!;
        var coat = Blocks.Find("coat", Me)!;
        var mail = Content.Items.Get("Armor_T0_Fur_Chest")!;

        async Task<bool> Put(ItemId item, int slot, string what)
        {
            var r = await Items.Give(wearer, item, 1, slot: SlotId.Of(equipment, slot));
            await Steps(4);
            var at = Items.Read(wearer)!.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item == item);
            var ok = Check(at?.Slot == SlotId.Of(equipment, slot), $"{what} went into equipment slot {slot}: {r}, at {at?.Slot}");
            if (ok) Check(GameTree.GameRequest(wearer, 8 | (slot << 8), equipment.Value | ((uint)slot << 24)), $"the test says slot {slot} is that slot");
            await Steps(40);
            return ok;
        }

        var before = Armour(wearer);
        var healthBefore = Combat.GetHealth(wearer)?.Max;
        await Put(new ItemId(hood.Id), VanityHead, "the hood");
        Info($"hood on: ArmorPoints [{before}] -> [{Armour(wearer)}], Equipment [{string.Join(",", Equipped(wearer).Where(x => x != 0).Select(x => $"0x{x:x8}"))}]");
        Check(Armour(wearer) == before, $"a cosmetic gives no armour: [{before}] -> [{Armour(wearer)}]");
        Check(Combat.GetHealth(wearer)?.Max == healthBefore, $"and no health: {healthBefore} -> {Combat.GetHealth(wearer)?.Max}");

        await Put(new ItemId(mail.Id), ArmourUpperBody, "armour");
        var armoured = Armour(wearer);
        var equipped = Equipped(wearer);
        Info($"armour on: ArmorPoints [{armoured}], Equipment [{string.Join(",", equipped.Where(x => x != 0).Select(x => $"0x{x:x8}"))}]");
        Check(armoured != before, $"armour under it counts: [{before}] -> [{armoured}]");
        Check(equipped.Contains(mail.Id), "the game's Equipment holds the armour");
        await Put(new ItemId(coat.Id), VanityUpperBody, "the coat, over the armour");
        Check(Armour(wearer) == armoured, $"a cosmetic over armour takes nothing from it: [{armoured}] -> [{Armour(wearer)}]");
        Check(Equipped(wearer).Contains(mail.Id), "the armour is still what the game's Equipment holds");
        Info($"coat on: Equipment [{string.Join(",", Equipped(wearer).Where(x => x != 0).Select(x => $"0x{x:x8}"))}]");

        var taken = await Items.Take(wearer, new ItemId(coat.Id), 1);
        await Steps(10);
        Check(taken.Status == ItemsStatus.Done && Armour(wearer) == armoured, $"taken off, the armour is as it was: {taken}, [{Armour(wearer)}]");

        // armor with a look of its own is armor: in the chest slot, its numbers count
        await Items.Take(wearer, new ItemId(mail.Id), 1);
        await Steps(10);
        var coatArmor = Blocks.Find("coat-armor", Me)!;
        await Put(new ItemId(coatArmor.Id), ArmourUpperBody, "armor with a look of its own");
        Check(Equipped(wearer).Contains(coatArmor.Id) && Armour(wearer).Split(',').Contains("30"), $"it is worn, and its Physical 30 counts: [{Armour(wearer)}]");
        Spawn.Destroy(wearer);
    }
}
