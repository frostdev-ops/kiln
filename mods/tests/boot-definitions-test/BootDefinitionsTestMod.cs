// SPDX-License-Identifier: MIT
// Mod.OnBoot lab test (chapter 08 §6): OnBoot stages a status, an effect, an item, a spell and a game-tree talent; OnLoad defines nothing.
// Once content is ready it checks that the first start already has them, registered by the boot generation, and that no pending cache was
// written for them. One line per assertion: "boot-definitions-test: PASS|FAIL|NOT BUILT|NOT RUN|RECORDED <ID> ...". BOOT-SAME-IDS-RESTART needs
// two starts (the first records, the second compares); BOOT-CALLBACK-EXCLUDED runs when the fault mods are in the start.
using Kiln;

namespace BootDefinitionsTest;

public sealed class TestMod : Mod
{
    const string Id = "boot-definitions-test";
    int _pass, _fail;
    BootDefinitionsCommittedEvent? _committed;

    public override void OnBoot(BootContext boot)
    {
        var d = BootDefinitions.For(boot);
        void Must(bool ok, string? why) { if (!ok) throw new InvalidOperationException(why); }
        Must(d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }).Ok, "cold");
        Must(d.Define("cold-hit", new EffectBlock { Applies = new StatusApply { Status = "cold" } }).Ok, "cold-hit");
        Must(d.Define("wood-token", new ItemBlock { Name = "Boot Token", Stack = 20 }).Ok, "wood-token");
        Must(d.Define("cold-bolt", new SpellBlock { Name = "Boot Bolt", Cast = new CastBlock { ManaCost = 2 }, Delivery = new ProjectileBlock { OnHit = ["cold-hit"] } }).Ok, "cold-bolt");
        Must(d.Define("cold-talent", new TalentBlock { Name = "Boot Cold Talent", Keeps = "cold", Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, 0] } }).Ok, "cold-talent");
        // what OnBoot wants OnLoad to have goes through a file: the instance and its load context are thrown away
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Path.Combine(Context.DataDirectory, "from-boot.txt"), $"generation {boot.Generation}, pid {Environment.ProcessId}");
    }

    public override void OnLoad()
    {
        Events.On<BootDefinitionsCommittedEvent>(e => { if (e.ModId == Id) _committed = e; });
        Content.OnReady(e => { _ = Check(); });
    }

    void Line(string verdict, string id, string text)
    {
        if (verdict == "PASS") _pass++;
        else if (verdict == "FAIL") _fail++;
        var line = $"{Id}: {verdict} {id} {text}";
        if (verdict == "FAIL") Context.Log.Error(line); else Context.Log.Info(line);
    }

    void Check(bool ok, string id, string text) => Line(ok ? "PASS" : "FAIL", id, text);

    async Task Check()
    {
        for (var i = 0; i < 24 * 30 && _committed == null; i++) await Context.Scheduler.NextTick();
        var report = Mods.BootState(Id);
        Check(report.State == BootModState.Committed, "BOOT-STATE", $"Mods.BootState: {report.State}, generation {report.Generation}, {report.Definitions.Count} definition(s)");
        Check(_committed is { Definitions.Count: 5 }, "BOOT-EVENT", $"BootDefinitionsCommittedEvent: {(_committed == null ? "none" : $"{_committed.Definitions.Count} definition(s), generation {_committed.Generation}")}");
        var items = NewContent.Definitions();
        string Of(string name) => items.FirstOrDefault(x => x.Name == $"{Id}.{name}") is { } x ? $"{x.State} (owner {x.Mod})" : "missing";
        Check(items.Any(x => x.Name == $"{Id}.wood-token" && x.State == "registered"), "BOOT-ITEM-FIRST", $"item {Id}.wood-token: {Of("wood-token")}");
        Check(items.Any(x => x.Name == $"{Id}.cold-bolt" && x.State == "registered"), "BOOT-SPELL-FIRST", $"spell item {Id}.cold-bolt: {Of("cold-bolt")}");
        var buffs = Content.Buffs.Definitions();
        var cold = buffs.FirstOrDefault(b => b.Name == $"{Id}.cold");
        Check(cold is { Error: null } && cold.State is "registered" or "defined", "BOOT-STATUS-FIRST", $"buff {Id}.cold: {cold?.State ?? "missing"} {cold?.Error}");
        var templates = Content.Templates.Definitions().Where(t => t.Name.StartsWith($"{Id}.cold-bolt", StringComparison.Ordinal)).ToList();
        Check(templates.Count > 0 && templates.All(t => t.Error == null), "BOOT-EFFECT-FIRST",
            $"cold-bolt's templates (its impact runs cold-hit): {string.Join(", ", templates.Select(t => $"{t.Name} {t.State}"))}");
        var node = GameTree.Definitions.FirstOrDefault(n => n.Name == $"{Id}.cold-talent");
        Check(node != null && node.Error == null && node.Index >= 0, "BOOT-TALENT-FIRST", $"node {Id}.cold-talent: {node?.State ?? "missing"} index {node?.Index}");
        var data = Path.GetFullPath(Path.Combine(Context.DataDirectory, ".."));
        var pending = new[] { Path.Combine(data, "skills", "defined", Id + ".json"), Path.Combine(data, "worldtables", "pending", Id), Path.Combine(data, "vfx", "pending", Id) }
            .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        Check(pending.Count == 0, "BOOT-NO-PENDING", pending.Count == 0 ? "no pending cache of this mod" : $"pending caches: {string.Join(", ", pending)}");
        foreach (var (id, kind) in new[] { ("BOOT-NPC-FIRST", "npcs"), ("BOOT-WORLDTABLE-FIRST", "buildingMaterials"), ("BOOT-PROP-FIRST", "props") })
            Line("NOT BUILT", id, $"{kind} are not a boot kind in this build");

        var fromBoot = Path.Combine(Context.DataDirectory, "from-boot.txt");
        var handed = File.Exists(fromBoot) ? File.ReadAllText(fromBoot) : null;
        Check(handed == $"generation {report.Generation}, pid {Environment.ProcessId}", "BOOT-DATA-DIR",
            $"what OnBoot wrote into Context.DataDirectory ({Context.DataDirectory}): {handed ?? "nothing"}");

        // the same ids on the next start: this start's are kept in the mod's data folder, the next start compares
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in items.Where(x => x.Name.StartsWith(Id + ".", StringComparison.Ordinal))) ids[$"item {x.Name}"] = $"{x.Guid} {x.Id}";
        if (cold != null) ids[$"buff {cold.Name}"] = $"{cold.Guid} {cold.Id}";
        foreach (var t in templates) ids[$"template {t.Name}"] = $"{t.Guid} {t.Id}";
        if (node != null) ids[$"node {node.Name}"] = $"{node.Guid} {node.Id} {node.Index}";
        var file = Path.Combine(Context.DataDirectory, "boot-ids.json");
        var now = System.Text.Json.JsonSerializer.Serialize(ids);
        if (File.Exists(file))
        {
            var before = System.Text.Json.JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(file)) ?? [];
            var changed = ids.Keys.Union(before.Keys).Where(k => ids.GetValueOrDefault(k) != before.GetValueOrDefault(k)).ToList();
            Check(changed.Count == 0 && ids.Count > 0, "BOOT-SAME-IDS-RESTART",
                changed.Count == 0 ? $"{ids.Count} ids as on the start before" : $"changed: {string.Join("; ", changed.Select(k => $"{k}: {before.GetValueOrDefault(k) ?? "none"} -> {ids.GetValueOrDefault(k) ?? "none"}"))}");
        }
        else
        {
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(file, now);
            Line("RECORDED", "BOOT-SAME-IDS-RESTART", $"first start: {ids.Count} ids kept; the next start compares");
        }

        // the fault mods (a separate set: boot-callback-fail throws after staging, boot-dependent requires it and has a skills file)
        var fail = Mods.BootState("boot-callback-fail");
        var dependent = Mods.BootState("boot-dependent");
        if (fail.State == BootModState.NotParticipating && dependent.State == BootModState.NotParticipating)
            Line("NOT RUN", "BOOT-CALLBACK-EXCLUDED", "boot-callback-fail and boot-dependent are not in this start");
        else
        {
            var leftovers = items.Where(x => x.Name.StartsWith("boot-callback-fail.", StringComparison.Ordinal)).Select(x => x.Name)
                .Concat(GameTree.Definitions.Where(n => n.Name == "bootdep_file_node").Select(n => n.Name)).ToList();
            Check(fail.State == BootModState.ExcludedCallback && dependent.State == BootModState.ExcludedDependency && leftovers.Count == 0, "BOOT-CALLBACK-EXCLUDED",
                $"boot-callback-fail {fail.State} ({fail.Issues.FirstOrDefault()?.Reason}), boot-dependent {dependent.State}; " +
                (leftovers.Count == 0 ? "none of their staged or file definitions is there" : $"still there: {string.Join(", ", leftovers)}"));
        }
        Context.Log.Info($"{Id}: done: {_pass} PASS, {_fail} FAIL");
    }
}
