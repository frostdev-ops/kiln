// SPDX-License-Identifier: MIT
// Mod.OnBoot lab test (chapter 08 §6): OnBoot stages a status, an effect, an item, a spell and a game-tree talent; OnLoad defines nothing.
// Once content is ready it checks that the first start already has them, registered by the boot generation, and that no pending cache was
// written for them. One line per assertion: "boot-definitions-test: PASS|FAIL|NOT BUILT <ID> ...".
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
        Context.Log.Info($"{Id}: done: {_pass} PASS, {_fail} FAIL");
    }
}
