// SPDX-License-Identifier: MIT
// Lab test of registry P1 (S9 live back-end, S10 boot back-end, S12 compatibility): Kiln's older paths and the registry on
// the same fields in one start (owner decision D1: a registry edit layers on every earlier writer and wins, reported; relative
// edits compose on the earlier value; old mods are never refused).
//   - registry-compat-test, the older mod (earlier in the load order): an old data pack, Material_T1_Wood_Logs maxStackSize
//     x2, and an old boot patch on keen::BalancingTable (experienceNeedPerLevel[0] 1500, playerLevelCap 55);
//   - content/registry.json: the registry section: items maxStack x4 (composes on the pack: x8), a recipe's duration 7 s,
//     balance levelCap 60 (over the old patch's 55), and since dev.32 skills.cost x2 of Airborne Architect (composes on the
//     older mod's old skills section, cost 2: 4);
//   - OnBoot: a C# registry edit through kiln.registry's submit (the native form of P2's Mod.OnRegistry; this test reaches it
//     through Kiln.Content's internal service lookup, which no mod should do): items plant fibre maxStack 500, balance
//     xpPerLevel[0] 2222 (over the old patch's 1500). Its copied IR uses a process-lived test owner, not the transient
//     boot-author owner that Host revokes before the registry stage. No callbacks into the boot assembly are retained.
//     Real mods get their retained owner through P2's OnRegistry integration, not this test-only bridge.
// After the world load: the live values read back, registry.json's rows. Then three pack hot reloads: the section's x4 made
// x3 (x6), the section removed (the old pack's x2 alone: no residue), the section back (x8). The C# edit stays throughout.
// dev.32: the same for a creature's sight, an arsenal's first attack's range and a perk's armour over the older mod's old
// gamedata sections (templates x2, arsenals +1, perks x2): x1.5 (x3), 9, x3 (x6) at the world load, gone with the section.
// With <data>/old-only present (the compat runner's baseline session) the mod does nothing of the registry's and checks
// only the old pack. Lines "registry-mixed-test: PASS/FAIL ..." and "registry-mixed-test: DONE pass=N fail=M" in kiln.log.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Kiln;

namespace RegistryMixedTest;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnStr
{
    public byte* Ptr;
    public nuint Len;
}

public sealed unsafe class RegistryMixedTestMod : Mod
{
    private const string Wood = "Material_T1_Wood_Logs", Fibre = "Material_T1_Plant_Fibre", Recipe = "Material_T1_Factory_String_SpinningWheel";
    private int _pass, _fail, _step;
    private double _wood0;
    private string _section = "";

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"registry-mixed-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private bool OldOnly => File.Exists(Path.Combine(Context.DataDirectory, "old-only"));
    private string SectionPath => Path.Combine(Context.Info.Directory, "content", "registry.json");

    public override void OnBoot(BootContext boot)
    {
        if (OldOnly) return;
        var ir = new JsonObject
        {
            ["ir"] = 1, ["mod"] = "registry-mixed-test", ["stage"] = "alter", ["source"] = "code:RegistryMixedTestMod.OnBoot",
            ["edits"] = new JsonArray(
                new JsonObject { ["n"] = 0, ["registry"] = "items", ["select"] = new JsonObject { ["id"] = "game:material_t1_plant_fibre" }, ["field"] = "maxStack", ["op"] = 500 },
                new JsonObject { ["n"] = 1, ["registry"] = "balance", ["select"] = new JsonObject { ["id"] = "game:balance" }, ["field"] = "xpPerLevel[0]", ["op"] = 2222 }),
        };
        var (rc, report) = Submit(ir.ToJsonString());
        Log.Info($"registry-mixed-test: OnBoot submit rc={rc}: {report}");
    }

    // kiln.registry v1's submit (offset 40) with a process-lived fixture owner bound to this mod.
    // Only copied IR survives OnBoot; never retain a delegate or a pointer into that collectible assembly.
    private (int, string) Submit(string json)
    {
        var bridge = typeof(Content).Assembly.GetType("Kiln.BlocksBridge");
        var table = (nint)(bridge?.GetMethod("ServiceTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { "kiln.registry", 1u }) ?? (nint)0);
        if (table == 0) return (-1, "kiln.registry is not here");
        if (*(uint*)table < 48) return (-1, "kiln.registry has no submit");
        var owner = (ulong)(bridge?.GetMethod("ServiceOwner", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
            .Invoke(null, new object[] { "registry-test:registry-mixed-test" }) ?? 0UL);
        if (owner == 0) return (-1, "the process-lived registry test owner could not be created");
        var submit = *(delegate* unmanaged[Cdecl]<ulong, KilnStr, void*, byte*, nuint, nuint*, int>*)(table + 40);
        var bytes = Encoding.UTF8.GetBytes(json);
        var rep = new byte[4096];
        nuint len = 0;
        int rc;
        fixed (byte* p = bytes, r = rep)
            rc = submit(owner, new KilnStr { Ptr = p, Len = (nuint)bytes.Length }, null, r, (nuint)rep.Length, &len);
        return (rc, Encoding.UTF8.GetString(rep, 0, (int)Math.Min((nuint)rep.Length, len)));
    }

    public override void OnLoad()
    {
        Content.OnReady(e =>
        {
            try { OnContent(e.Reload); }
            catch (Exception x) { Check(false, $"the test threw: {x}"); Done(); }
        });
    }

    private double Num(string item) => Content.Items.Get(item)?.GetNumber("maxStackSize") ?? double.NaN;
    // dev.32: a creature's sight, an arsenal's first attack's range and a perk's armour, over registry-compat-test's old gamedata sections
    private const string Sight = "Enemy01BehaviorState.combatCommitmentSetup.view.range", Range = "attacks[0].description.maxRange", Fog = "perkArmorSetup.distribution.fog";
    private static GameDataEntry? Vukah => Content.Templates.Get("Enemy_Vukah_Melee");
    private static GameDataEntry? VukahArsenal => Content.Arsenals.Get("template:Enemy_Vukah_Melee");
    private static GameDataEntry? FogPerk => Content.Perks.Get("Perk_Armor_Fog");
    private static double Gd(GameDataEntry? e, string path) => e?.GetNumber(path) ?? double.NaN;
    private static double Gd0(GameDataEntry? e, string path) => e?.Original(path).AsDouble ?? double.NaN;
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-4 * Math.Max(1, Math.Abs(b));
    // the registry over the old pack (registry: true), or the old pack alone
    private void CheckGamedata(string when, bool registry)
    {
        double s0 = Gd0(Vukah, Sight), r0 = Gd0(VukahArsenal, Range), f0 = Gd0(FogPerk, Fog);
        Check(Near(Gd(Vukah, Sight), (registry ? 3 : 2) * s0), $"{when}: creatures.sight.range {(registry ? "x1.5 composed on the old pack's x2" : "the old pack's x2 alone")}: {Gd(Vukah, Sight)} (game {s0})");
        Check(Near(Gd(VukahArsenal, Range), registry ? 9 : r0 + 1), $"{when}: arsenals attacks[0].maxRange {(registry ? "9 over the old pack's +1" : "the old pack's +1 alone")}: {Gd(VukahArsenal, Range)} (game {r0})");
        Check(Near(Gd(FogPerk, Fog), (registry ? 6 : 2) * f0), $"{when}: perks.armor.shroud {(registry ? "x3 composed on the old pack's x2" : "the old pack's x2 alone")}: {Gd(FogPerk, Fog)} (game {f0})");
    }
    // dev.32: Airborne Architect's cost (the skills registry over kiln.gamedata's skills slot): the old pack sets 2, the section x2
    private const uint Node = 0x4475e8dd;
    private static double Cost() => Content.Skills.Get(Node)?.GetNumber("costs") ?? double.NaN;

    private JsonNode? Row(JsonNode doc, string registry, string id, string field)
    {
        foreach (var f in doc["fields"]?.AsArray() ?? new JsonArray())
            if ((string?)f?["registry"] == registry && (string?)f?["id"] == id && (string?)f?["field"] == field) return f;
        return null;
    }

    private void OnContent(bool reload)
    {
        var wood = Content.Items.Get(Wood);
        if (wood is null) { Check(false, $"{Wood} is in the item registry"); Done(); return; }
        if (_step == 0)
        {
            _wood0 = wood.Original("maxStackSize").AsDouble;
            if (OldOnly)
            {
                Check(Num(Wood) == 2 * _wood0, $"old-only: the old pack's x2 alone: {Num(Wood)} (game {_wood0})");
                CheckGamedata("old-only", false);
                Check(Cost() == 2, $"old-only: the old skills section's cost 2 alone: {Cost()}");
                _step = 99;
                Done();
                return;
            }
            _section = File.ReadAllText(SectionPath);
            Check(Num(Wood) == 8 * _wood0, $"world load: the registry's x4 composed on the old pack's x2: {Num(Wood)} = 8 x {_wood0}");
            Check(Num(Fibre) == 500, $"world load: the C# edit's maxStack 500 on {Fibre}: {Num(Fibre)}");
            CheckGamedata("world load", true);
            var d = Content.Recipes.Get(Recipe)?.GetNumber("craftingDuration") ?? double.NaN;
            Check(Math.Abs(d - 7) < 1e-6, $"world load: {Recipe} craftingDuration 7 s: {d}");
            var state = Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", "state", "registry.json"));
            var doc = JsonNode.Parse(File.ReadAllText(state))!;
            var cap = Row(doc, "balance", "game:balance", "levelCap");
            Check(cap is not null && (int?)cap["result"] == 60 && cap["writers"]!.AsArray().Any(w => (string?)w?["kind"] == "oldPatch"),
                $"registry.json: levelCap 60 over the old boot patch's 55: {cap?.ToJsonString()}");
            var xp = Row(doc, "balance", "game:balance", "xpPerLevel");
            Check(xp is not null && (int?)xp["result"]?[0] == 2222, $"registry.json: the C# edit's xpPerLevel[0] 2222 over the old patch's 1500: {xp?["result"]?.ToJsonString()}");
            var conflicts = doc["conflicts"]?.AsArray() ?? new JsonArray();
            bool Line(string field, string kind, string loser) => conflicts.Any(c => ((string?)c?["field"] ?? "").Contains(field) && (string?)c?["kind"] == kind &&
                                                                                    ((string?)c?["loser"] ?? "").Contains(loser));
            Check(Line("xpPerLevel", "overridden", "registry-compat-test"), "registry.json: the C# element set over the older mod's boot patch is reported overridden");
            Check(Line("levelCap", "overridden", "registry-compat-test"), "registry.json: levelCap over the older mod's boot patch is reported overridden");
            var ms = Row(doc, "items", "game:material_t1_wood_logs", "maxStack");
            Check(ms is not null && (int?)ms["result"] == (int)(8 * _wood0) && ms["writers"]!.AsArray().Any(w => (string?)w?["kind"] == "oldPack"),
                $"registry.json at world load: maxStack's row has the older mod's pack below and the live result: {ms?.ToJsonString()}");
            Check(Line("maxStack", "composedOn", "registry-compat-test"), "registry.json at world load: the x4 composed on the older mod's pack");
            // dev.32: the same report for a gamedata registry's live field over the older mod's old gamedata section
            var sight = Row(doc, "creatures", "game:enemy_vukah_melee", "sight.range");
            Check(sight is not null && Near((double?)sight["result"] ?? double.NaN, 3 * Gd0(Vukah, Sight)) && sight["writers"]!.AsArray().Any(w => (string?)w?["kind"] == "oldPack"),
                $"registry.json at world load: creatures sight.range's row has the older mod's pack below and the live result: {sight?.ToJsonString()}");
            Check(Line("sight.range", "composedOn", "registry-compat-test"), "registry.json at world load: the creatures x1.5 composed on the older mod's pack");
            Check(Cost() == 4, $"world load: skills.cost x2 composed on the old skills section's 2: {Cost()}");
            var cost = Row(doc, "skills", "game:skill_4475e8dd", "cost");
            Check(cost is not null && (int?)cost["result"] == 4 && cost["writers"]!.AsArray().Any(w => (string?)w?["kind"] == "oldPack"),
                $"registry.json at world load: skills.cost's row has the old skills section below and the live result: {cost?.ToJsonString()}");
            _step = 1;
            File.WriteAllText(SectionPath, _section.Replace("\"mul\": 4", "\"mul\": 3"));
            Content.ReloadPacks();
            return;
        }
        if (!reload) return;
        if (_step == 1)
        {
            Check(Num(Wood) == 6 * _wood0, $"reload, the section edited (x3): {Num(Wood)} = 6 x {_wood0}");
            CheckGamedata("reload, the section edited", true);
            Check(Num(Fibre) == 500, $"reload: the C# edit stays: {Num(Fibre)}");
            Check(Cost() == 4, $"reload, the section edited: skills.cost x2 stays: {Cost()}");
            _step = 2;
            File.Delete(SectionPath);
            Content.ReloadPacks();
        }
        else if (_step == 2)
        {
            Check(Num(Wood) == 2 * _wood0, $"reload, the section removed: the old pack's x2 alone, no residue: {Num(Wood)}");
            var d = Content.Recipes.Get(Recipe)?.GetNumber("craftingDuration") ?? double.NaN;
            var d0 = Content.Recipes.Get(Recipe)?.Original("craftingDuration").AsDouble ?? double.NaN;
            Check(d == d0, $"reload, the section removed: {Recipe} craftingDuration back to the game's {d0}: {d}");
            CheckGamedata("reload, the section removed (no residue)", false);
            Check(Cost() == 2, $"reload, the section removed: the old skills section's cost 2 alone, no residue: {Cost()}");
            _step = 3;
            File.WriteAllText(SectionPath, _section);
            Content.ReloadPacks();
        }
        else if (_step == 3)
        {
            Check(Num(Wood) == 8 * _wood0, $"reload, the section back: x8 again: {Num(Wood)}");
            CheckGamedata("reload, the section back", true);
            Check(Cost() == 4, $"reload, the section back: skills.cost 4 again: {Cost()}");
            _step = 99;
            Done();
        }
    }

    private void Done() => Log.Info($"registry-mixed-test: DONE pass={_pass} fail={_fail}");
}
