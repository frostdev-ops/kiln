// SPDX-License-Identifier: MIT
// Lab test of loot table, creature drops and recipe blocks. No player: a creature block is spawned and killed with Combat.SetHealth(0), and what it drops
// is read from the pickups Kiln spawns; a recipe of its own is read from the game's recipe registry; a table is rolled, dropped and given. PASS and FAIL
// lines in kiln.log, then "blocks-loot-test done: N PASS, M FAIL".
using Kiln;

namespace BlocksLootTest;

public sealed class BlocksLootTestMod : Mod
{
    private const string Me = "blocks-loot-test";
    private const uint Workbench = 0x4da88f73;
    private readonly List<EntityId> _mine = [];
    private readonly List<EntitySpawnedEvent> _pickups = [];
    private IDisposable? _sub;
    private int _pass, _fail;
    private LootTableHandle? _csCache;
    private WorldPosition _at = new(3855.8, 833.0, 1399.8);
    private EntityId _chest;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Info(string s) => Log.Info($"{Me}: {s}");

    public override void OnLoad()
    {
        // the C# twins of blocks/loot.json, under cs- names
        var shard = Blocks.Define("cs-shard", new ItemBlock { Name = "Cs Shard", Stack = 100 });
        _csCache = Blocks.Define("cs-cache", new LootTableBlock
        {
            Entries = [new LootEntry { Item = shard, CountMin = 3 }, new LootEntry { Item = "Material_T1_Metal_Scraps", CountMin = 2, CountMax = 2 }],
        });
        Blocks.Define("cs-scavenger", new CreatureBlock { Name = "Cs Scavenger", Body = Bodies.Vukah, Stats = new CreatureStats { Level = 3 }, Drops = _csCache });
        Blocks.Define("cs-charcoal", new RecipeBlock
        {
            Output = "Material_T1_Charcoal", Makes = 2, Station = StationKind.Workbench.Workshop,
            Needs = { [shard] = 2, ["Material_T1_Wood_Logs"] = 1 },
        });
        _sub = Events.On<EntitySpawnedEvent>(e =>
        {
            if (e.Kind == SpawnedKind.Item && e.Tag is { } tag && tag.StartsWith("blocks:" + Me, StringComparison.Ordinal)) _pickups.Add(e);
        });
        _ = Run();
    }

    public override void OnUnload()
    {
        _sub?.Dispose();
        foreach (var e in _mine) try { Spawn.Destroy(e); } catch (Exception) { }
        _mine.Clear();
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(120);
            for (var i = 0; i < 25 * 120 && Items.Status() is not { Transactions: true, Read: true }; i++) await Context.Scheduler.NextTick();
            foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            {
                if (Spawn.Inspect(e) is not { } at) continue;
                (_chest, _at) = (e, at.Position with { Y = at.Position.Y + 1.5 });
                break;
            }
            Info($"chest {_chest}, creatures and pickups at {_at}");
            await Section("mistakes", Mistakes);
            await Section("twins", Twins);
            await Section("tables", Tables);
            await Section("recipes", Recipes);
            await Section("drops", Drops);
        }
        catch (Exception e) { Check(false, $"exception: {e}"); }
        OnUnload();
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Section(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception {e}"); }
    }

    private static string G(string name) => $"{Me}.{name}";

    private static uint IdOf(string game) => Items.Lookup(game)?.Id.Value ?? 0;

    private static string Show(IEnumerable<LootStack> stacks) => string.Join(", ", stacks.Select(s => $"{s.Item} x{s.Count}"));

    // ---------------------------------------------------------------------------------------------------- mistakes

    private async Task Mistakes()
    {
        string? Field(Action define)
        {
            try
            {
                define();
                return null;
            }
            catch (BlockError e) { return e.Field ?? "(no field)"; }
        }
        Check(Field(() => Blocks.Define("bad-empty", new LootTableBlock { Entries = [] })) == "entries", "a table with no entries is refused at entries");
        Check(Field(() => Blocks.Define("bad-chance", new LootTableBlock { Entries = [new LootEntry { Item = "Material_T1_Twigs", Chance = 2 }] })) == "entries[0].chance", "a chance of 2 is refused at entries[0].chance");
        Check(Field(() => Blocks.Define("bad-creature", new CreatureBlock { Body = Bodies.Vukah, Drops = "nowhere" })) == "drops", "a creature naming no table is refused at drops");
        Check(Field(() => Blocks.Define("bad-recipe", new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } })) == "output", "a recipe block with no output is refused at output");
        Check(Field(() => Blocks.Define("bad-output", new RecipeBlock { Output = "cs-cache", Needs = { ["Material_T1_Twigs"] = 1 } })) == "output", "a recipe that makes a loot table is refused at output");
        Check(Field(() => Blocks.Define("bad-station", new RecipeBlock { Output = "Material_T1_Charcoal", Station = "no-such-station", Needs = { ["Material_T1_Twigs"] = 1 } })) == "station", "a recipe at a station that is none is refused at station");

        // the data file's wrong blocks load as errors by name, and the item the game lacks is said once content is ready
        var errors = Blocks.Report().TryGetValue(Me, out var report) ? report.Errors : [];
        Info($"errors of the data files: {string.Join(" | ", errors)}");
        Check(errors.Any(x => x.Contains("blocks/broken.json", StringComparison.Ordinal) && x.Contains("a loot table with no entries", StringComparison.Ordinal)), "broken.json: the empty table is an error that names the file and the block");
        Check(errors.Any(x => x.Contains("blocks/broken.json", StringComparison.Ordinal) && x.Contains("nowhere", StringComparison.Ordinal)), "broken.json: the creature that names no table is an error");
        Check(errors.Any(x => x.Contains("No_Such_Item_xyz", StringComparison.Ordinal) && x.Contains("the game has no item", StringComparison.Ordinal)), "broken.json: an item the game lacks is said once content is ready");
        Check(errors.Count(x => x.Contains("No_Such_Item_xyz", StringComparison.Ordinal)) == 1, "and said once");
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------------ twins

    private Task Twins()
    {
        // ids come from the names, so what differs is the names and the GUIDs they make
        static string? Same(string? doc) => doc == null ? null : System.Text.RegularExpressions.Regex.Replace(doc.Replace("cs-", "ember-", StringComparison.Ordinal), "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<guid>");
        foreach (var (data, cs) in new[] { ("ember-cache", "cs-cache"), ("ember-scavenger", "cs-scavenger"), ("ember-charcoal", "cs-charcoal") })
        {
            var a = Same(Blocks.Lowered(data));
            var b = Same(Blocks.Lowered(cs));
            Check(a != null && a == b, $"{data} (data) and {cs} (C#) lower to the same document");
        }
        var creature = Blocks.Lowered("ember-scavenger") ?? "";
        Check(creature.Contains("\"drops\"", StringComparison.Ordinal) && creature.Contains("Material_T1_Metal_Scraps", StringComparison.Ordinal), "a creature's lowered document says what it drops");
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------------------------------------------- tables

    private async Task Tables()
    {
        var cache = (LootTableHandle)Blocks.Find("ember-cache")!;
        var shard = G("ember-shard");
        var first = cache.Roll(new Random(1));
        Info($"ember-cache rolled: {Show(first)}");
        Check(first.Count == 2 && first.Any(s => s.Item == shard && s.Count == 3) && first.Any(s => s.Item == "Material_T1_Metal_Scraps" && s.Count == 2), $"a table rolls its entries: {Show(first)}");
        Check(Show(cache.Roll(new Random(9))) == Show(cache.Roll(new Random(9))), "the same seed rolls the same");

        var maybe = (LootTableHandle)Blocks.Find("ember-maybe")!;
        var rng = new Random(4);
        var hits = Enumerable.Range(0, 400).Count(_ => maybe.Roll(rng).Count > 0);
        Check(hits is > 140 and < 260, $"a chance of one half gave {hits} of 400");

        var pick = (LootTableHandle)Blocks.Find("ember-pick")!;
        var picked = pick.Roll(new Random(2));
        var total = picked.Sum(s => s.Count);
        var shards = picked.Where(s => s.Item == shard).Sum(s => s.Count);
        Check(total == 24 && shards is > 10 and < 23, $"pick one, 24 rolls, weights 3:1: {Show(picked)}");

        // out into the world, and into an inventory
        var shardId = IdOf(shard);
        Check(shardId != 0, $"the table's own item is in the game: 0x{shardId:x8}");
        _pickups.Clear();
        var ids = await cache.Drop(_at);
        _mine.AddRange(ids);
        await Steps(4);
        var spawned = _pickups.ToList();
        Check(ids.Count == 2 && ids.All(Spawn.Exists), $"Drop makes one pickup per stack: {ids.Count} ({string.Join(", ", ids)})");
        Check(spawned.Any(p => p.ItemId == shardId && p.Amount == 3) && spawned.Any(p => p.ItemId == IdOf("Material_T1_Metal_Scraps") && p.Amount == 2), $"of the right items and amounts: {string.Join(", ", spawned.Select(p => $"0x{p.ItemId:x8} x{p.Amount}"))}");
        // a table that names an item the game lacks gives the rest, and says so
        _pickups.Clear();
        var lacking = (LootTableHandle)Blocks.Find("no-such-item")!;
        var rest = await lacking.Drop(_at);
        _mine.AddRange(rest);
        await Steps(4);
        Check(rest.Count == 1 && _pickups.Count == 1 && _pickups[0].ItemId == IdOf("Material_T1_Twigs") && _pickups[0].Amount == 2, $"an entry whose item the game lacks drops nothing and the rest drops: {rest.Count} pickup(s), {string.Join(", ", _pickups.Select(p => $"0x{p.ItemId:x8} x{p.Amount}"))}");

        // into an inventory: a chest of the world, else a loot container made for the purpose
        var target = _chest;
        if (target.IsNone)
        {
            target = await Spawn.Entity("LootContainer_Enemy_Scavenger_Melee01", _at with { Y = _at.Y + 3 }, options: new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
            _mine.Add(target);
            for (var i = 0; i < 100 && Items.Read(target) is not { UsedSlots: > 0 }; i++) await Context.Scheduler.NextTick();
        }
        var before = Items.Read(target)?.Count(new ItemId(shardId)) ?? 0;
        var results = await cache.Give(target);
        await Steps(4);
        var after = Items.Read(target)?.Count(new ItemId(shardId)) ?? 0;
        Check(results.Count == 2 && results.All(r => r.Succeeded) && after - before == 3, $"Give puts the stacks into an inventory ({target}): {string.Join("; ", results)}; shards {before} -> {after}");
    }

    // ---------------------------------------------------------------------------------------------------- recipes

    private Task Recipes()
    {
        var shard = new ItemId(IdOf(G("ember-shard")));
        var charcoal = Content.Recipes.Get(G("ember-charcoal"));
        Check(charcoal != null && (uint)charcoal.Get("workshopId").Integer == Workbench, $"a recipe of its own is at the game's workbench: 0x{(uint)(charcoal?.Get("workshopId").Integer ?? 0):x8}");
        Check(charcoal?.Inputs.Count == 2 && charcoal.Inputs.Any(i => i.Item == shard && i.Count == 2) && charcoal.Inputs.Any(i => i.Item.Value == IdOf("Material_T1_Wood_Logs") && i.Count == 1),
            $"with its inputs: {string.Join(", ", charcoal?.Inputs ?? [])}");
        Check(charcoal?.Outputs.Count == 1 && charcoal.Outputs[0].Item.Value == IdOf("Material_T1_Charcoal") && charcoal.Outputs[0].Count == 2, $"and its output, a game item: {string.Join(", ", charcoal?.Outputs ?? [])}");
        var sticks = Content.Recipes.Get(G("ember-sticks"));
        Check(sticks?.Outputs.Count == 1 && sticks.Outputs[0].Item == shard && (uint)sticks.Get("workshopId").Integer == Workbench, $"an item of the mod made at the game's workbench: {string.Join(", ", sticks?.Outputs ?? [])}");
        var def = NewContent.Definitions().FirstOrDefault(d => d.Name == G("ember-charcoal"));
        Check(def is { State: "registered" }, $"registered with the game: {def}");
        var listed = Content.Recipes.Where(r => r.Name.StartsWith(Me + ".", StringComparison.Ordinal) && (uint)r.Get("workshopId").Integer == Workbench).Select(r => r.Name).Order().ToList();
        Info($"recipes of this mod at the workbench: {string.Join(", ", listed)}");
        Check(listed.SequenceEqual([G("cs-charcoal"), G("ember-charcoal"), G("ember-sticks")]), "the workbench lists the three");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------------ drops

    private async Task Drops()
    {
        if (!Spawn.Available || !Creatures.Available)
        {
            Info("SKIP drops: kiln.spawn or kiln.creatures is unavailable");
            return;
        }
        var shardId = IdOf(G("ember-shard"));
        var csShardId = IdOf(G("cs-shard"));
        var scraps = IdOf("Material_T1_Metal_Scraps");
        var logs = IdOf("Material_T1_Wood_Logs");
        foreach (var (name, expect) in new (string, (uint Item, int Count)[])[]
                 {
                     ("ember-scavenger", [(shardId, 3), (scraps, 2)]),
                     ("cs-scavenger", [(csShardId, 3), (scraps, 2)]),
                     ("ember-inline", [(logs, 5)]),
                     ("ember-bare", []),
                 })
        {
            var e = await Blocks.Spawn(name, _at, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
            _mine.Add(e);
            await Steps(10);
            _pickups.Clear();
            var set = Combat.SetHealth(e, 0);
            var tag = $"blocks:{Me}:{name}";
            for (var i = 0; i < (expect.Length == 0 ? 80 : 200) && _pickups.Count < expect.Length; i++) await Context.Scheduler.NextTick();
            await Steps(6);
            var mine = _pickups.Where(p => p.Tag == tag).ToList();
            foreach (var p in mine) _mine.Add(p.Entity);
            var text = string.Join(", ", mine.Select(p => $"0x{p.ItemId:x8} x{p.Amount}"));
            if (expect.Length == 0)
            {
                Check(mine.Count == 0, $"{name} dies with SetHealth({set}) and drops nothing: {mine.Count} pickups");
                continue;
            }
            var right = expect.All(x => mine.Any(p => p.ItemId == x.Item && p.Amount == x.Count)) && mine.Count == expect.Length;
            var near = mine.All(p => Spawn.Inspect(p.Entity) is { } at && at.Position.DistanceTo(_at) < 4);
            Check(right, $"{name} killed with Combat.SetHealth(0) drops its table: {text}");
            Check(near && mine.All(p => Spawn.Exists(p.Entity)), $"{name}: the pickups are in the world, where it fell");
        }
    }
}
