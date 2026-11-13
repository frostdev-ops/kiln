// SPDX-License-Identifier: MIT
// Affix kinds and pools as blocks (dev.27): Blocks.Define, the "affixes" and "affixPools" sections of blocks/*.json and
// Affixes.Define/DefinePool/Load are one registration. The ids rolls keep are the ones Affixes.Define gave before, a
// block-defined affix rolls and acts as one from Affixes.Define, and a wrong one is refused as every block is. Same
// fake game and item store as AffixTests.cs (this is the same class).
using System.Text.Json.Nodes;
using Xunit;
using static Kiln.InstanceHarness;
using AffixBlockFace;

namespace Kiln
{

    public sealed partial class AffixTests
    {
        private readonly List<string> _blockFolders = [];

        private void DeleteBlockFolders()
        {
            foreach (var d in _blockFolders)
                try { Directory.Delete(d, recursive: true); }
                catch (IOException) { }
        }

        /// <summary>A mod with these blocks/*.json files, read as the loader reads them (nothing compiled yet), and the folder.
        /// Its code then defines through <c>TestMods.As</c> with the context this returns: the same ModBlocks.</summary>
        private (ModContext Code, ModBlocks Blocks, string Folder) ModWithFiles(string id, params (string Name, string Json)[] files)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"kiln-affix-blocks-{Guid.NewGuid():N}");
            _blockFolders.Add(dir);
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            foreach (var (name, json) in files) File.WriteAllText(Path.Combine(dir, "blocks", name), json);
            var mod = TheBlocks.ModFor(id, () => 0);
            mod.Reset();                       // a test before may have used the id
            (mod.Owner, mod.OwnedByMod) = (0, false);
            BlockJson.Read(mod, dir);
            return (TestMods.Create(id), mod, dir);
        }

        /// <summary>The data pass after OnLoad: what the files define and nothing named yet.</summary>
        private static int DataPass(ModBlocks mod, string folder) => BlockJson.LoadMod(mod, folder);

        private static readonly AffixBlock Keen = new() { Name = "Keen", Stat = Stat.CritChance, Stacks = false, Group = "edge", Tiers = [new AffixTier { Min = 0.02, Max = 0.05 }] };

        private const string KeenJson = """{ "name": "Keen", "stat": "crit-chance", "stacks": false, "group": "edge", "tiers": [ { "min": 0.02, "max": 0.05 } ] }""";

        // What dev27/integration (9d90c0c0, affixes not yet blocks) gave, pinned there by this file's own tests: the id of an
        // affix is "<mod id>:<name>" (Affixes.Define("curse").Id == "sword-mod:curse"), and an item keeps its rolls as this JSON
        // (TheRollsAreItemDataOfKilnsOwnAndComeBackAfterARestart, one roll of it here).
        private const string RolledBefore = """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":2,"Value":6}]}""";

        [Fact]
        public void TheIdsAreTodaysWhicheverRouteDefinesThem()
        {
            // the same name in four mods, one route each: "<mod>:<name>", as Affixes.Define gave before
            var cs = TestMods.Create("route-code");
            var af = TestMods.Create("route-define");
            var load = TestMods.Create("route-load");
            var (_, fileBlocks, folder) = ModWithFiles("route-file", ("a.json", $$"""{ "format": 1, "affixes": { "keen": {{KeenJson}} }, "affixPools": { "rolls": { "affixes": [ { "affix": "keen" } ] } } }"""));
            Assert.Equal(2, DataPass(fileBlocks, folder));
            var ids = new[]
            {
                TestMods.As(cs, () => TheBlocks.Define("keen", Keen)).Id,
                TestMods.As(af, () => Affixes.Define("keen", Keen)).Id,
                TestMods.As(load, () => Affixes.Load($$"""{ "format": 1, "affixes": { "keen": {{KeenJson}} } }""")).Affixes.Single().Id,
                ((AffixHandle)TheBlocks.Find("keen", "route-file")!).Id,
            };
            Assert.Equal(["route-code:keen", "route-define:keen", "route-load:keen", "route-file:keen"], ids);
            Assert.Equal("route-code:rolls", TestMods.As(cs, () => TheBlocks.Define("rolls", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "keen" }] })).Id);
            Assert.Equal("route-define:rolls", TestMods.As(af, () => Affixes.DefinePool("rolls", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "keen" }] })).Id);
            Assert.Equal("route-file:rolls", Affixes.FindPool("route-file:rolls")!.Id);
            Assert.Equal(["route-file:keen"], AffixRuntime.FindPool("route-file:rolls")!.Entries.Select(e => e.Affix));
            Assert.Equal("sword-mod:curse", Define("curse", new AffixBlock { Name = "Cursed", Stat = Stat.Strength, Tiers = [new AffixTier { Min = -5, Max = -1 }] }).Id);   // the pin itself
        }

        [Fact]
        public void AnItemRolledBeforeKeepsItsBytesAndActsForTheAffixInEveryRoute()
        {
            var chest = _h.World.Chest(ChestTemplate, A);
            var kept = _h.World.Give(chest, 2, Sword);
            Assert.True(As(() => Affixes.Set(kept, [new RolledAffix("sword-mod:bear", "of the Bear", "strength", 2, 6)])).Ok);
            _h.Save(1);
            var bundle = _h.Bundle(1, InstanceStore.WorldPrefix + Affixes.DataMod)!;
            Assert.Equal(RolledBefore, bundle["holders"]![0]!["records"]![0]!["keys"]![Affixes.DataKey]!.ToJsonString());   // the stored bytes: as before

            // a sword in hand with that roll: it acts for "sword-mod:bear" however "bear" is defined now
            var sword = Give(3, Sword, worn: true);
            Assert.True(As(() => Affixes.Set(sword, Affixes.Of(kept))).Ok);
            var mod = TheBlocks.ModFor("sword-mod", () => 0);
            mod.Reset();                                                         // bear, keen and fleet go: the roll waits
            Assert.Null(Affixes.Find("sword-mod:bear"));
            Pass();
            Assert.Equal(0, _game.Sum(_hero, Stat.Strength));

            var dir = Path.Combine(Path.GetTempPath(), $"kiln-affix-blocks-{Guid.NewGuid():N}");
            _blockFolders.Add(dir);
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            File.WriteAllText(Path.Combine(dir, "blocks", "bear.json"), """{ "affixes": { "bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 1, "max": 3 }, { "min": 4, "max": 8 } ] } } }""");
            Assert.Equal(1, DataPass(mod, dir));
            Assert.Equal("sword-mod:bear", ((AffixHandle)TheBlocks.Find("bear", "sword-mod")!).Id);
            Pass();
            Assert.Equal(6, _game.Sum(_hero, Stat.Strength));                    // a data file's bear acts for it

            var bear = new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 3 }, new AffixTier { Min = 4, Max = 8 }] };
            Assert.Equal("sword-mod:bear", As(() => TheBlocks.Define("bear", bear)).Id);
            Pass();
            Assert.Equal(6, _game.Sum(_hero, Stat.Strength));                    // TheBlocks.Define's: the same id, the same change
            Assert.Equal("sword-mod:bear", Define("bear", bear).Id);
            Pass();
            Assert.Equal(6, _game.Sum(_hero, Stat.Strength));                    // and Affixes.Define's
            Assert.Single(_game.Running);                                        // one change all along, never one per route
        }

        [Fact]
        public void ADefinitionIsOneObjectWhicheverRouteMadeIt()
        {
            var keen = As(() => TheBlocks.Define("cs-keen", Keen));
            Assert.Same(keen, TheBlocks.Find("cs-keen", "sword-mod"));
            Assert.Same(keen, Affixes.Find("sword-mod:cs-keen"));
            Assert.Same(keen, As(() => Affixes.Find("cs-keen")));
            Assert.Equal(("sword-mod", "cs-keen", Keen), (keen.Mod, keen.Name, keen.Block));
            Assert.IsType<AffixHandle>(As(() => TheBlocks.Define<AffixBlock>("generic", Keen)));   // the generic route makes the same handle
            Assert.Same(_bear, TheBlocks.Find("bear", "sword-mod"));                 // Affixes.Define's are blocks
            var pool = Pool("cs-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "cs-keen" }] });
            Assert.Same(pool, TheBlocks.Find("cs-pool", "sword-mod"));
            Assert.Same(pool, Affixes.FindPool("sword-mod:cs-pool"));
            var lowered = JsonNode.Parse(TheBlocks.Lowered("cs-pool", "sword-mod")!)!;
            Assert.Equal("sword-mod:cs-keen", lowered["affixPool"]!["affixes"]![0]!["affix"]!.GetValue<string>());
            Assert.Equal("crit-chance", JsonNode.Parse(TheBlocks.Lowered("cs-keen", "sword-mod")!)!["affix"]!["stat"]!.GetValue<string>());
        }

        [Fact]
        public void CodeAndDataFilesNameEachOthersAffixesInEitherOrder()
        {
            var (code, mod, folder) = ModWithFiles("mixed-mod", ("affixes.json", """
                { "format": 1,
                  // a pool before its affixes, naming one of the file (not named by anything before) and one of the code
                  "affixPools": { "file-pool": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "file-late" }, { "affix": "cs-keen", "weight": 2 } ], "min": 2, "max": 2 } },
                  "affixes": {
                    "file-bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 2 } ] },
                    "file-late": { "name": "Late", "stat": "max-health", "tiers": [ { "min": 9, "max": 9 } ] },
                    "file-ward": { "name": "Ember Ward", "stat": "resist-fire", "tiers": [ { "min": 0.1, "max": 0.1 } ] } } }
                """));
            // OnLoad: an affix in code, then pools in code that name the file's affixes (compiled there and then) and another mod's
            var keen = TestMods.As(code, () => TheBlocks.Define("cs-keen", Keen));
            var csPool = TestMods.As(code, () => TheBlocks.Define("cs-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "file-bear" }, new AffixEntry { Affix = "cs-keen" }, new AffixEntry { Affix = "sword-mod:bear" }], Min = 3, Max = 3 }));
            var afPool = TestMods.As(code, () => Affixes.DefinePool("af-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "file-ward" }, new AffixEntry { Affix = "mixed-mod:cs-keen" }] }));
            Assert.NotNull(Affixes.Find("mixed-mod:file-bear"));                  // compiled when the code named it
            Assert.Null(Affixes.Find("mixed-mod:file-late"));                     // not named yet: waits for the data pass
            Assert.Equal(4, DataPass(mod, folder));                               // every block of the file; file-bear and file-ward were compiled when named, file-late when the pool named it
            Assert.Empty(mod.Errors);

            var bear = (AffixHandle)TheBlocks.Find("file-bear", "mixed-mod")!;
            Assert.Same(bear, Affixes.Find("mixed-mod:file-bear"));
            Assert.Equal(["mixed-mod:file-bear", "mixed-mod:cs-keen", "sword-mod:bear"], AffixRuntime.FindPool(csPool.Id)!.Entries.Select(e => e.Affix));
            Assert.Equal(["mixed-mod:file-ward", "mixed-mod:cs-keen"], AffixRuntime.FindPool(afPool.Id)!.Entries.Select(e => e.Affix));
            var filePool = Affixes.FindPool("mixed-mod:file-pool")!;
            Assert.Same(filePool, TheBlocks.Find("file-pool", "mixed-mod"));
            Assert.Equal(["mixed-mod:file-late", "mixed-mod:cs-keen"], AffixRuntime.FindPool(filePool.Id)!.Entries.Select(e => e.Affix));
            Assert.Same(keen, Affixes.Find("mixed-mod:cs-keen"));

            // they roll and act as any affix
            var sword = Give(3, Sword, worn: true);
            var rolled = As(() => csPool.Roll(sword, new Random(1)));
            Assert.True(rolled.Ok, rolled.Reason);
            Assert.Equal(["mixed-mod:cs-keen", "mixed-mod:file-bear", "sword-mod:bear"], rolled.Affixes.Select(a => a.Affix).Order(StringComparer.Ordinal));
            Pass();
            Assert.Equal(2 + rolled.Affixes.Single(a => a.Affix == "sword-mod:bear").Value, _game.Sum(_hero, Stat.Strength));
            Assert.Equal(rolled.Affixes.Single(a => a.Affix == "mixed-mod:cs-keen").Value, _game.Sum(_hero, Stat.CritChance), 6);
        }

        [Fact]
        public void TheSameNameTwiceIsWhatItIsInOneRouteAndAnotherKindIsRefused()
        {
            // in one route (as before): the later replaces the earlier, and the earlier handle is not the pool any more
            var sword = Give(3, Sword, worn: false);
            var first = Pool("twice", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] });
            var second = As(() => TheBlocks.Define("twice", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "keen" }] }));   // across routes: the same
            Assert.Same(second, Affixes.FindPool("sword-mod:twice"));
            Assert.Same(second, TheBlocks.Find("twice", "sword-mod"));
            Assert.Throws<ArgumentException>(() => As(() => first.Roll(sword, replace: true)));
            Assert.Equal("sword-mod:keen", Assert.Single(As(() => second.Roll(sword, new Random(1), replace: true)).Affixes).Affix);
            var again = Define("bear", new AffixBlock { Name = "Bear Again", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 1, Max = 1 }] });
            Assert.Same(again, Affixes.Find("sword-mod:bear"));
            var third = As(() => TheBlocks.Define("bear", new AffixBlock { Name = "Bear Thrice", Stat = Stat.MaxMana, Tiers = [new AffixTier { Min = 1, Max = 1 }] }));
            Assert.Same(third, Affixes.Find("sword-mod:bear"));
            Assert.Same(third, TheBlocks.Find("bear", "sword-mod"));

            // a data file and code with the same name: the file's, compiled at the data pass, replaces the code's, as for every block
            var (code, mod, folder) = ModWithFiles("dup-mod", ("dup.json", $$"""{ "affixes": { "keen": {{KeenJson}} } }"""));
            var fromCode = TestMods.As(code, () => Affixes.Define("keen", Keen with { Name = "Keen (code)" }));
            Assert.Equal(1, DataPass(mod, folder));
            var fromFile = Affixes.Find("dup-mod:keen")!;
            Assert.NotSame(fromCode, fromFile);
            Assert.Equal("Keen", fromFile.Block.Name);
            Assert.Same(fromFile, TheBlocks.Find("keen", "dup-mod"));

            // another kind of block by the same name is refused, both ways: a name is one block of a mod
            As(() => TheBlocks.Define("zap", new EffectBlock { Heal = 5 }));
            var onEffect = Assert.Throws<BlockError>(() => Define("zap", Keen));
            Assert.Null(onEffect.Field);
            Assert.Contains("is an effect of this mod already", onEffect.Message);
            Assert.Contains("is an affix of this mod already", Assert.Throws<BlockError>(() => Pool("keen", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] })).Message);
            Assert.Contains("is an affix pool of this mod already", Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("twice", Keen))).Message);
            Assert.Contains("is an affix of this mod already", Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("keen", new EffectBlock { Heal = 5 }))).Message);
            Assert.Same(_keen, Affixes.Find("sword-mod:keen"));                  // and nothing changed
        }

        [Fact]
        public void AWrongAffixBlockSaysWhichBlockAndFieldInCodeAndInAFile()
        {
            // code: BlockError by block and field, as every block
            var tiers = Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("bad", new AffixBlock { Name = "Bad", Stat = Stat.Strength, Tiers = [] })));
            Assert.Equal(("affix bad", "tiers"), (tiers.Block, tiers.Field));
            Assert.StartsWith("block affix bad, tiers: ", tiers.Message);
            var range = Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("bad", new AffixBlock { Name = "Bad", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 3 }, new AffixTier { Min = 2.5, Max = 4 }] })));
            Assert.Equal("tiers[1]", range.Field);
            var name = Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("Bad", Keen)));
            Assert.Null(name.Field);
            Assert.Contains("lower-case", name.Message);
            var lost = Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("bad-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "nope" }] })));
            Assert.Equal(("affix pool bad-pool", "affixes[1].affix"), (lost.Block, lost.Field));
            Assert.Contains("no affix \"sword-mod:nope\"", lost.Message);
            As(() => TheBlocks.Define("zap", new EffectBlock { Heal = 5 }));
            var kind = Assert.Throws<BlockError>(() => As(() => TheBlocks.Define("bad-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "zap" }] })));
            Assert.Equal("affixes[0].affix", kind.Field);
            Assert.Contains("\"zap\" is an effect block of this mod, not an affix", kind.Message);
            Assert.Null(TheBlocks.Find("bad", "sword-mod"));
            Assert.Null(TheBlocks.Find("bad-pool", "sword-mod"));

            // a file: each wrong block is an error of the mod naming file, block and field, and costs only itself, as a wrong
            // loot table beside it does
            var (_, mod, folder) = ModWithFiles("file-bad", ("affixes.json", """
                { "format": 1,
                  "affixes": {
                    "good": { "name": "Good", "stat": "strength", "tiers": [ { "min": 1, "max": 2 } ] },
                    "zero": { "name": "Zero", "stat": "strength", "tiers": [ { "min": 0, "max": 2 } ] },
                    "typo": { "name": "Typo", "stat": "strength", "tier": [ { "min": 1, "max": 2 } ] },
                    "Upper": { "name": "Upper", "stat": "strength", "tiers": [ { "min": 1, "max": 2 } ] } },
                  "affixPools": {
                    "lost": { "affixes": [ { "affix": "nowhere" } ] },
                    "fine": { "affixes": [ { "affix": "good" } ] } },
                  "lootTables": { "bad-loot": { "entries": [] } } }
                """));
            Assert.Equal(2, DataPass(mod, folder));
            string ErrorOf(string what) => Assert.Single(mod.Errors, e => e.Contains(what));
            Assert.Equal(5, mod.Errors.Count);
            Assert.StartsWith("blocks/affixes.json: block affix zero, tiers[0]: 0 to 2: a range never holds 0", ErrorOf("affix zero"));
            Assert.StartsWith("blocks/affixes.json: affixes.typo", ErrorOf("typo"));
            Assert.StartsWith("blocks/affixes.json: block affix Upper: a name is lower-case", ErrorOf("Upper"));
            Assert.StartsWith("blocks/affixes.json: block affix pool lost, affixes[0].affix: no affix \"file-bad:nowhere\" is defined", ErrorOf("lost"));
            Assert.StartsWith("blocks/affixes.json: block loottable bad-loot, entries: ", ErrorOf("bad-loot"));
            Assert.NotNull(Affixes.Find("file-bad:good"));
            Assert.NotNull(Affixes.FindPool("file-bad:fine"));
            Assert.Null(Affixes.Find("file-bad:zero"));
            Assert.Null(TheBlocks.Find("zero", "file-bad"));
            Assert.Null(Affixes.FindPool("file-bad:lost"));
        }

        [Fact]
        public void RollsAndBonusesThroughBlocksAreThoseOfAffixesDefine()
        {
            // RollsStayInTheirRangesAndRepeatWithASeed's pool: once from Affixes.Define (this class's bear, keen, fleet), once from a
            // data file of another mod with the same blocks
            var defined = Pool("blades", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "keen" }, new AffixEntry { Affix = "fleet", Weight = 2 }], Min = 1, Max = 2 });
            var (_, mod, folder) = ModWithFiles("file-twin", ("twin.json", """
                { "format": 1,
                  "affixes": {
                    "bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 1, "max": 3 }, { "min": 4, "max": 8, "weight": 0.5 } ] },
                    "keen": { "name": "Keen", "stat": "crit-chance", "stacks": false, "group": "edge", "tiers": [ { "min": 0.02, "max": 0.05 } ] },
                    "fleet": { "name": "Fleet", "stat": "move-speed", "group": "edge", "tiers": [ { "min": 0.05, "max": 0.15, "step": 0.05 } ] } },
                  "affixPools": { "blades": { "affixes": [ { "affix": "bear" }, { "affix": "keen" }, { "affix": "fleet", "weight": 2 } ], "min": 1, "max": 2 } } }
                """));
            Assert.Equal(4, DataPass(mod, folder));
            var file = Affixes.FindPool("file-twin:blades")!;
            var a = Give(3, Sword, worn: false);
            var b = Give(4, Sword, worn: false);
            static (string, string, string, int, double) Plain(RolledAffix r) => (r.Affix[(r.Affix.IndexOf(':') + 1)..], r.Name, r.Stat, r.Tier, r.Value);
            for (var seed = 0; seed < 100; seed++)
            {
                var x = As(() => defined.Roll(a, new Random(seed), replace: true));
                var y = As(() => file.Roll(b, new Random(seed), replace: true));
                Assert.True(x.Ok && y.Ok);
                Assert.Equal(x.Affixes.Select(Plain), y.Affixes.Select(Plain));
                Assert.All(y.Affixes, r => Assert.StartsWith("file-twin:", r.Affix));
            }

            // and they act the same: worn alone, each sword's rolls are what the wearer holds, and taken off nothing is
            var applied = new List<AffixesAppliedEvent>();
            using var sub = Events.On<AffixesAppliedEvent>(applied.Add);
            double Held(Stat s) => _game.Sum(_hero, s);
            var held = new List<(double, double, double)>();
            foreach (var (item, prefix) in new[] { (a, "sword-mod:"), (b, "file-twin:") })
            {
                Wear(item);
                Pass();
                var rolls = As(() => Affixes.Of(item));
                Assert.Equal(rolls.Select(r => (r.Affix, r.Value)).Order(), As(() => Affixes.Active(_hero)).Select(x => (x.Affix, x.Value)).Order());
                Assert.All(As(() => Affixes.Active(_hero)), x => Assert.StartsWith(prefix, x.Affix));
                held.Add((Held(Stat.Strength), Held(Stat.CritChance), Held(Stat.MoveSpeed)));
                TakeOff(item);
                Pass();
                Assert.Empty(_game.Running);
            }
            Assert.Equal(held[0], held[1]);
            Assert.Equal([true, false, true, false], applied.Select(e => e.Now.Count > 0));
        }

        // ---- review round: the shapes a file can hold, the data pass's order, a refused removal ----

        [Fact]
        public void ASectionOrABlockOfAnotherShapeCostsOnlyItselfAndTheRestLoads()
        {
            // a section that is not an object (a list, a string, a number, null, true) is that file's error naming the
            // section, and the file's other sections and the later files load; a block below a section that is not an
            // object is that block's error, as a wrong block is
            var (_, mod, folder) = ModWithFiles("shape-mod",
                ("a.json", """{ "format": 1, "affixes": [], "affixPools": { "fine": { "affixes": [ { "affix": "good" } ] } } }"""),
                ("b.json", """{ "affixes": "bear", "affixPools": 3, "statuses": [] }"""),
                ("c.json", """{ "affixes": null, "affixPools": true }"""),
                ("d.json", """
                    { "affixes": { "good": { "name": "Good", "stat": "strength", "tiers": [ { "min": 1, "max": 2 } ] }, "list": [], "text": "bear", "number": 3, "nothing": null },
                      "affixPools": { "plist": [], "ptext": "x", "pnumber": 1, "pnothing": null } }
                    """));
            Assert.Equal(2, DataPass(mod, folder));
            Assert.NotNull(Affixes.Find("shape-mod:good"));
            Assert.NotNull(Affixes.FindPool("shape-mod:fine"));
            string ErrorOf(string start) => Assert.Single(mod.Errors, e => e.StartsWith(start, StringComparison.Ordinal));
            Assert.Contains("is a list, not an object of block name -> block", ErrorOf("blocks/a.json: \"affixes\" "));
            Assert.Contains("is a string", ErrorOf("blocks/b.json: \"affixes\" "));
            Assert.Contains("is a number", ErrorOf("blocks/b.json: \"affixPools\" "));
            Assert.Contains("is a list", ErrorOf("blocks/b.json: \"statuses\" "));      // the same rule holds for the sections before affixes
            Assert.Contains("is null", ErrorOf("blocks/c.json: \"affixes\" "));
            Assert.Contains("is true", ErrorOf("blocks/c.json: \"affixPools\" "));
            foreach (var block in new[] { "affixes.list", "affixes.text", "affixes.number", "affixes.nothing", "affixPools.plist", "affixPools.ptext", "affixPools.pnumber", "affixPools.pnothing" })
                ErrorOf($"blocks/d.json: {block}: ");
            Assert.Equal(14, mod.Errors.Count);
        }

        [Fact]
        public void ADataFilesAffixIsTheModsAfterOnLoadWhetherCodeNamedItFirstOrNot()
        {
            const string File = """{ "affixes": { "bear": { "name": "File Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 2 } ] } } }""";
            var codeBear = new AffixBlock { Name = "Code Bear", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 9, Max = 9 }] };
            var rolls = new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] };

            // a pool in code names bear first (the file's is compiled there and then), and code defines bear after it
            var (first, firstBlocks, firstFolder) = ModWithFiles("named-first", ("bear.json", File));
            var namedPool = TestMods.As(first, () => TheBlocks.Define("rolls", rolls));
            var fileBear = Affixes.Find("named-first:bear")!;
            Assert.Equal("File Bear", fileBear.Block.Name);
            TestMods.As(first, () => TheBlocks.Define("bear", codeBear));
            Assert.Equal("Code Bear", Affixes.Find("named-first:bear")!.Block.Name);   // during OnLoad, the code's
            Assert.Equal(1, DataPass(firstBlocks, firstFolder));
            Assert.Same(fileBear, Affixes.Find("named-first:bear"));                      // after it, the file's: the same object
            Assert.Same(fileBear, TheBlocks.Find("bear", "named-first"));
            Assert.Equal("File Bear", JsonNode.Parse(TheBlocks.Lowered("bear", "named-first")!)!["affix"]!["name"]!.GetValue<string>());

            // code defines bear first, then a pool in code names it (the code's), then the data pass
            var (code, codeBlocks, codeFolder) = ModWithFiles("code-first", ("bear.json", File));
            TestMods.As(code, () => TheBlocks.Define("bear", codeBear));
            var codePool = TestMods.As(code, () => TheBlocks.Define("rolls", rolls));
            Assert.Equal(1, DataPass(codeBlocks, codeFolder));
            Assert.Equal("File Bear", Affixes.Find("code-first:bear")!.Block.Name);
            Assert.Same(Affixes.Find("code-first:bear"), TheBlocks.Find("bear", "code-first"));

            // either way the pool code defined rolls the file's bear
            foreach (var (pool, slot) in new[] { (namedPool, 3), (codePool, 4) })
            {
                var rolled = As(() => pool.Roll(Give(slot, Sword, worn: false), new Random(1)));
                Assert.True(rolled.Ok, rolled.Reason);
                Assert.Equal(("File Bear", "strength", 2.0), (rolled.Affixes.Single().Name, rolled.Affixes.Single().Stat, rolled.Affixes.Single().Value));
            }
        }

        [Fact]
        public void ARemovalTheItemDataStoreRefusesAnswersFalseAndChangesNothing()
        {
            var sword = Give(3, Sword, worn: false);
            Assert.True(As(() => Affixes.Add(sword, "bear", 1, 2)).Ok);
            var changes = new List<AffixesChangedEvent>();
            using var sub = Events.On<AffixesChangedEvent>(changes.Add);
            var real = AffixRuntime.RemoveData;
            AffixRuntime.RemoveData = (_, _) => false;   // the store had nothing to take any more
            try
            {
                Assert.False(As(() => Affixes.Remove(sword, "bear")));
                Assert.False(As(() => Affixes.Clear(sword)));
                var set = As(() => Affixes.Set(sword, []));
                Assert.False(set.Ok);
                Assert.Contains("not taken off", set.Reason);
                Assert.Equal("sword-mod:bear", Assert.Single(set.Affixes).Affix);
                Assert.Empty(changes);                                                // no event for what did not happen
            }
            finally
            {
                AffixRuntime.RemoveData = real;
            }
            Assert.True(As(() => Affixes.Remove(sword, "bear")));
            Assert.Empty(As(() => Affixes.Of(sword)));
            Assert.Single(changes);
        }

        // ---- final review: stored data is not trusted, writes in a handler, one block's surprise ----

        /// <summary>Puts <paramref name="json"/> on the item as its stored rolls, as a save edited by hand would bring it.</summary>
        private void StoreRaw(ItemInstanceRef item, string json)
        {
            Assert.True(As(() => Affixes.Set(item, [new RolledAffix("sword-mod:bear", "of the Bear", "strength", 1, 1)])).Ok);   // the key's slot is there
            var info = Store.Instances.KeyOf(Affixes.DataMod, Affixes.DataKey)!;
            var slot = InstanceStore.SlotOf(Store.Instances.Lookup(item, info, create: false)!, info, create: false)!;
            (slot.Value, slot.Type, slot.Json) = (null, null, json);
        }

        /// <summary>The server's pass as it runs: the items and their rolls read from item data (AffixRuntime.Affixed).</summary>
        private void ServerPass() => AffixRuntime.Pass(_game, As(AffixRuntime.Affixed));

        [Fact]
        public void OneItemsStoredRollsOfAnyShapeCostOnlyThatItemAndThePassGoesOn()
        {
            var good = Give(3, Sword, worn: true);
            As(() => Affixes.Add(good, "bear", 1, 3));
            var shapes = new[]
            {
                """{"Rolls":null}""", """{"Rolls":5}""", """[1,2]""", """{"Other":1}""",
                """{"Rolls":[null,5,"x",{"Name":"no id"},{"Id":""},{"Id":"sword-mod:keen","Value":"much"}]}""",
            };
            var bad = shapes.Select((json, i) => { var r = Give(10 + i, Ring, worn: true); StoreRaw(r, json); return r; }).ToList();
            // what reads as rolls of a mixed item acts: the first of two "bear" counts, the rest is left out
            var mixed = Give(20, Ring, worn: true);
            StoreRaw(mixed, """{"Rolls":[7,{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":1,"Value":2},{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":1,"Value":3}],"Extra":true}""");
            ServerPass();
            Assert.Equal(3 + 2, _game.Sum(_hero, Stat.Strength));
            Assert.Equal(bad.Count + 1, AffixRuntime.ItemNotes);               // one line per item
            ServerPass();
            ServerPass();
            Assert.Equal(bad.Count + 1, AffixRuntime.ItemNotes);               // not one per pass
            TakeOff(good);
            ServerPass();
            Assert.Equal(2, _game.Sum(_hero, Stat.Strength));                    // the pass still takes bonuses off
            Assert.Equal(["sword-mod:bear"], As(() => Affixes.Of(mixed)).Select(a => a.Affix));
            Assert.All(bad, b => Assert.Empty(As(() => Affixes.Of(b))));
            // the stored bytes are kept as they were found
            _h.Save(1);
            var saved = string.Concat(_h.Scopes(1).Select(scope => _h.Bundle(1, scope)?.ToJsonString()));   // the player's items are in the player's scope
            Assert.All(shapes, json => Assert.Contains(JsonNode.Parse(json)?.ToJsonString() ?? "null", saved));
            Assert.Contains("\"Extra\":true", saved);
        }

        [Fact]
        public void AnItemActsWithAtMostEightRollsTheFirstInStoredOrder()
        {
            for (var i = 0; i < 10; i++) Define($"m{i}", new AffixBlock { Name = $"M{i}", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 1 }] });
            var ring = Give(4, Ring, worn: true);
            StoreRaw(ring, "{\"Rolls\":[" + string.Join(",", Enumerable.Range(0, 10).Select(i => $$"""{"Id":"sword-mod:m{{i}}","Name":"M{{i}}","Stat":"strength","Tier":1,"Value":1}""")) + "]}");
            ServerPass();
            Assert.Equal(Affixes.MaxPerItem, _game.Sum(_hero, Stat.Strength));
            Assert.Equal(Enumerable.Range(0, 8).Select(i => $"sword-mod:m{i}").Order(), As(() => Affixes.Active(_hero)).Select(a => a.Affix).Order());
            ServerPass();
            Assert.Equal(1, AffixRuntime.ItemNotes);
        }

        [Fact]
        public void AStoredValueBeyondWhatItsAffixRollsNeverActs()
        {
            // bear rolls 1 to 8: a stored 9, or 1e100 (infinity as the stat program's float), does nothing; -8 to 8 acts
            var a = Give(3, Sword, worn: true);
            var b = Give(4, Sword, worn: true);
            var c = Give(5, Ring, worn: true);
            StoreRaw(a, """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":2,"Value":1e100}]}""");
            StoreRaw(b, """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":2,"Value":9}]}""");
            StoreRaw(c, """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"strength","Tier":2,"Value":8},{"Id":"sword-mod:keen","Name":"Keen","Stat":"crit-chance","Tier":1,"Value":0.5}]}""");
            // a roll of another stat than bear's now (bear defined again since): bear's tiers say nothing of it, the bound of
            // every affix value does (1,000,000); 7 acts, 2e6 does not
            var d = Give(8, Ring, worn: true);
            var e = Give(9, Ring, worn: true);
            StoreRaw(d, """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"max-health","Tier":1,"Value":7}]}""");
            StoreRaw(e, """{"Rolls":[{"Id":"sword-mod:bear","Name":"of the Bear","Stat":"max-health","Tier":1,"Value":2e6}]}""");
            ServerPass();
            Assert.Equal(8, _game.Sum(_hero, Stat.Strength));
            Assert.Equal(0, _game.Sum(_hero, Stat.CritChance));                 // keen rolls up to 0.05
            Assert.Equal(7, _game.Sum(_hero, Stat.MaxHealth));
            Assert.Equal(4, AffixRuntime.ItemNotes);
            // the calls refuse such values where they are given
            Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(Give(6, Ring, false), "bear", 2, 9)));
            Assert.Throws<ArgumentException>(() => As(() => Affixes.Set(Give(7, Ring, false), [new RolledAffix("other-mod:x", "X", "strength", 1, 2e6)])));
            Assert.Equal("tiers[0]", Assert.Throws<BlockError>(() => Define("huge", new AffixBlock { Name = "Huge", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 2e6 }] })).Field);
        }

        [Fact]
        public void AHandlerThatWritesTheItemAgainGetsOneEventPerChangeAndNoException()
        {
            var sword = Give(3, Sword, worn: false);
            var seen = new List<AffixesChangedEvent>();
            var sub = Events.On<AffixesChangedEvent>(e =>
            {
                seen.Add(e);
                As(() => Affixes.Set(e.Item, e.Now));                            // writes the item again
            });
            try
            {
                Assert.True(As(() => Affixes.Add(sword, "bear", 1, 2)).Ok);
                Assert.Single(seen);                                             // the change, at once
                Assert.Equal(1, AffixRuntime.ChangedNotices.Waiting);            // the handler's own write: at the next step
                ChangeNoticeQueue.FlushAll();
                Assert.Equal(2, seen.Count);                                     // one event for that write
                Assert.Equal(1, AffixRuntime.ChangedNotices.Waiting);            // and its handler's write waits again
                Assert.Equal(seen[0].Now, seen[1].Now);
            }
            finally
            {
                sub.Dispose();
                ChangeNoticeQueue.FlushAll();                                    // what waits goes to nobody
            }
            Assert.Equal(0, AffixRuntime.ChangedNotices.Waiting);
        }

        [Fact]
        public void ABlockWhoseCompileThrowsWhatNobodyForesawCostsOnlyItself()
        {
            var (code, mod, folder) = ModWithFiles("surprise-mod",
                ("a.json", """{ "effects": { "zap": { "area": { "radius": 1, "effects": null } }, "fine": { "heal": 1 } } }"""),
                ("b.json", """{ "affixes": { "later": { "name": "Later", "stat": "strength", "tiers": [ { "min": 1, "max": 2 } ] } } }"""));
            // code that names it gets that block's BlockError, not the exception itself
            var named = Assert.Throws<BlockError>(() => TestMods.As(code, () => TheBlocks.Define("uses", new EffectBlock { Then = ["zap"] })));
            Assert.Contains("could not be compiled", named.Message);
            Assert.Equal(2, DataPass(mod, folder));                            // fine and later: the file's other block and the next file
            var error = Assert.Single(mod.Errors, e => e.Contains("zap"));
            Assert.StartsWith("blocks/a.json: ", error);
            Assert.NotNull(TheBlocks.Find("fine", "surprise-mod"));
            Assert.NotNull(Affixes.Find("surprise-mod:later"));
        }
    }
}

namespace AffixBlockFace
{
    using Kiln;

    /// <summary>Kiln.Blocks, the class, by its plain name. Inside namespace Kiln this project's own namespace Kiln.Blocks.Tests
    /// hides it (FurnitureFieldsTests reaches it by reflection); from here a using directive finds it.</summary>
    internal static class TheBlocks
    {
        public static AffixHandle Define(string name, AffixBlock block) => Blocks.Define(name, block);
        public static AffixPoolHandle Define(string name, AffixPoolBlock block) => Blocks.Define(name, block);
        public static BlockHandle<T> Define<T>(string name, T block) where T : class, IBlock => Blocks.Define(name, block);
        public static BlockHandle? Find(string name, string? mod = null) => Blocks.Find(name, mod);
        public static string? Lowered(string name, string? mod = null) => Blocks.Lowered(name, mod);
        internal static ModBlocks ModFor(string id, Func<ulong> owner) => Blocks.ModFor(id, owner);
    }
}
