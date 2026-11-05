// SPDX-License-Identifier: MIT
// Many blocks at once: how many a server takes, and what an author is told about the rest. The count is in <kiln>/data/blocks-scale/count, per kind.
using Kiln;

namespace BlocksScale;

public sealed class BlocksScaleMod : Mod
{
    private const string Mod = "blocks-scale";

    public override void OnLoad()
    {
        var file = Path.Combine(Context.DataDirectory, "count");
        if (!File.Exists(file) || !int.TryParse(File.ReadAllText(file).Trim(), out var count) || count <= 0) return;
        var thrown = new List<string>();
        void Try(Action define)
        {
            try { define(); }
            catch (BlockError e) { thrown.Add(e.Message); }
        }
        var burst = Blocks.Define("burst", new EffectBlock { Damage = Damage.Ice(5) });
        for (var i = 0; i < count; i++)
        {
            var n = i;
            Try(() => Blocks.Define($"item-{n}", new ItemBlock { Name = $"Item {n}", Stack = 10, Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } } }));
            Try(() => Blocks.Define($"sword-{n}", new WeaponBlock { Name = $"Sword {n}", Class = WeaponClass.Sword, Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare }, Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } } }));
            Try(() => Blocks.Define($"mail-{n}", new ArmorBlock { Name = $"Mail {n}", Slot = ArmorSlot.Chest, Stats = new ArmorStats { Level = 5, Physical = 30, Bonus = new Dictionary<EquipBonus, double> { [EquipBonus.Health] = 60 } } }));
            Try(() => Blocks.Define($"wand-{n}", new WandBlock
            {
                Name = $"Wand {n}",
                Primary = new WandAttack { Cast = new CastBlock { ManaCost = 5 }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [burst] } }] } },
            }));
        }
        _ = Report(count, thrown);
    }

    private async Task Report(int count, List<string> thrown)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
        for (var i = 0; i < 600; i++) await Context.Scheduler.NextTick();
        var mine = NewContent.Definitions().Where(d => d.Mod == Mod).ToList();
        var items = mine.Where(d => d.Kind == ContentKind.Items).ToList();
        var inWorld = items.Count(d => d.Worlds.Contains("server"));
        var clones = Content.GameDataDefinitions().Where(d => d.Mod == Mod).ToList();
        var report = Blocks.Report().GetValueOrDefault(Mod);
        Log.Info($"blocks-scale: {count} per kind: {Blocks.Report().GetValueOrDefault(Mod).Blocks?.Count} blocks defined, {thrown.Count} refused when defined; {inWorld} of {items.Count} items in the server world, {clones.Count(c => string.IsNullOrEmpty(c.Error))} of {clones.Count} clones took");
        foreach (var t in thrown.Take(3)) Log.Info($"blocks-scale: refused: {t}");
        var problems = report.Errors ?? [];
        Log.Info($"blocks-scale: {problems.Count} problem(s) reported by name");
        foreach (var p in problems.Take(6)) Log.Info($"blocks-scale: reported: {p}");
        foreach (var d in items.Where(d => !d.Worlds.Contains("server")).Take(3)) Log.Info($"blocks-scale: not in the world: {d.Name}: {d.State}: {d.Problem}");
        Log.Info($"blocks-scale: near memory {System.Text.RegularExpressions.Regex.Match(NewContent.InfoJson(), "\"nearMemory\":\\[[^\\]]*\\]").Value}");
        Log.Info("blocks-scale done");
    }
}
