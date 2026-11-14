// SPDX-License-Identifier: MIT
// Lab test: GameTypes, Reflected (checked writes), GameSystems (switch, profile), GameEvents (post, read back), and reflected
// resources (read, find by field, claim-backed writes, restore) with the EML helper ports (GuidHash, DataImage).
using Kiln;

namespace TypesTest;

public sealed class TypesTestMod : Mod
{
    private ulong _start;
    private int _posted = -1;
    // Bronze Sword (Weapon_T1_1H_SwordEpic_01): keen::ItemInfo, the resource assets-test reads too.
    private static readonly Guid BronzeSword = Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95");
    private const string ItemInfo = "keen::ItemInfo";

    private void Check(string name, bool ok, object? detail = null) =>
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");

    public override void OnLoad()
    {
        KilnDevTools.Start();
        Context.Tick.Post += OnPost;
    }

    private void OnPost(in TickInfo tick)
    {
        // Start once the world has loaded its entities (a fresh server steps for a while before it does).
        if (_start == 0)
        {
            if (tick.Step % 25 != 0 || Reflected.All("Health").Count < 10) return;
            _start = tick.Step - 39;
        }
        var n = tick.Step - _start;
        try
        {
            if (n == 40) Basics();
            if (n == 43) Resources();
            if (n == 44) ImpactNames();
            if (n == 50) ResourcesAgain();
            // Two 4-byte events in one step: both must come back (the stream packs them at 4 bytes, the poster at 8+8).
            if (n == 41)
                _posted = (GameEvents.Post("TestEvent1", new Dictionary<string, object?> { ["data"] = 7 }) ? 1 : 0)
                          + (GameEvents.Post("TestEvent1", new Dictionary<string, object?> { ["data"] = 8 }) ? 1 : 0);
            if (n == 42)
            {
                int seen7 = 0, seen8 = 0;
                GameEvents.Each("TestEvent1", e => { var d = Convert.ToInt64(e["data"]); seen7 += d == 7 ? 1 : 0; seen8 += d == 8 ? 1 : 0; });
                Check("GameEvents.Post then Each", _posted == 2 && seen7 == 1 && seen8 == 1, $"posted {_posted}, seen {seen7}+{seen8}");
            }
            if (n == 150)
            {
                // the peer mod (types-peer-test) was refused the field this mod holds; give it back now and the peer takes it
                var held = Overrides.Active.Where(i => i.Mod == "types-test").ToList();
                var gone = Overrides.RestoreAll();
                Check("Overrides.RestoreAll gives the claims back", gone >= 1 && !Overrides.Active.Any(i => i.Mod == "types-test"), $"{held.Count} held, {gone} given back");
            }
            if (n == 160)
            {
                var top = GameSystems.Stats();
                Check("GameSystems.Stats", top.Count > 0 && GameSystems.ProfileSeconds > 0, $"{top.Count} systems, top {top.FirstOrDefault()?.Name}");
                GameSystems.Profile(false);
                Log.Info("types-test: done");
            }
        }
        catch (Exception e)
        {
            Check($"step {n}", false, e.Message);
        }
    }

    // ---- reflected resources ----------------------------------------------------------------------------------------

    private string? _claimField;

    private void Resources()
    {
        Check("ReflectedResources.Available", ReflectedResources.Available, $"types {GameTypes.Available}, assets {Assets.Status()}");
        var sword = ReflectedResources.Get(BronzeSword, ItemInfo);
        Check("ReflectedResources.Get ItemInfo", sword is { Type.QualifiedName: ItemInfo }, sword);
        if (sword == null) return;

        // identity: itemId is the hash of the GUID (docs/re/items.md); GuidHash is the port of EML's game.guid.hash
        var itemId = sword.Get<uint>("itemId");
        Check("itemId == GuidHash.IdOf(guid)", itemId == GuidHash.IdOf(BronzeSword) && itemId == 0x43108313, $"0x{itemId:x8} vs 0x{GuidHash.IdOf(BronzeSword):x8}");
        Check("GuidHash.Of == EML's fnv1a32 of the bytes", GuidHash.Of(BronzeSword) == DataHash.Fnv1a32(BronzeSword.ToByteArray()));

        // a BlobString through the blob offsets
        var name = sword.Get<string>("debugName");
        Check("reflected BlobString (debugName)", name == "Weapon_T1_1H_SwordEpic_01", name);
        Check("types.of a resource", GameTypes.TypeOf(sword)?.QualifiedName == ItemInfo);

        // Describe walks every kind of field the type has
        var text = sword.Describe(maxArray: 3);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check("Describe lists the fields", lines.Length > 40 && lines.Any(l => l.StartsWith("debugName = \"")) && lines.Any(l => l.StartsWith("itemId = ")), $"{lines.Length} lines");
        foreach (var l in lines.Take(8).Concat(lines.Where(l => l.StartsWith("debugName")))) Log.Info($"types-test: describe: {l}");

        // find by field value
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var byName = ReflectedResources.FindByField(ItemInfo, "debugName", "Weapon_T1_1H_SwordEpic_01");
        var byId = ReflectedResources.FindByField(ItemInfo, "itemId", 0x43108313u);
        var scanMs = watch.ElapsedMilliseconds;
        var all = ReflectedResources.All(ItemInfo).Count();
        Check("FindByField text and number", byName.Count == 1 && byName[0].Guid == BronzeSword && byId.Count == 1 && byId[0].Guid == BronzeSword,
              $"{byName.Count}+{byId.Count} hits among {all} items, 2 scans {scanMs} ms");
        Check("ReflectedResources.All", all > 1000, all);
        var refused = false;
        try { ReflectedResources.FindByField(ItemInfo, "noSuchField", 1); } catch (ArgumentException) { refused = true; }
        Check("FindByField refuses a path that is no field", refused);

        // pick numeric fields to change: an integer and a float, whatever this build calls them
        var intField = new[] { "maxStackSize", "stackSize" }.FirstOrDefault(f => Try(() => sword[f]) is long);
        var floatField = Try(() => sword["damageSetup.dmgMod"]) is double ? "damageSetup.dmgMod" : null;
        Check("a numeric field of ItemInfo to change", intField != null, $"int {intField}, float {floatField}");
        if (intField == null) return;

        var before = sword.Bytes;
        var original = sword.Get<long>(intField);
        var changed = original == 77 ? 78 : 77;
        var result = sword.Set(intField, changed);
        Log.Info($"types-test: set: {result}");
        Check("write: Set is applied", result.Ok, result.Message);
        Check("write: read back through a fresh view", ReflectedResources.Get(BronzeSword, ItemInfo)!.Get<long>(intField) == changed);
        Check("write: Original and HeldBy", sword.Original(intField) is long o && o == original && sword.HeldBy(intField) == "types-test", sword.HeldBy(intField));
        Check("write: only that field's bytes changed", sword.Bytes.Length == before.Length && sword.Bytes.Where((b, i) => b != before[i]).Count() is > 0 and <= 2);
        Check("write: Assets sees the same bytes (copy-on-write through kiln.assets)",
              Assets.ReadResource(new AssetKey(BronzeSword, "keen::ItemInfo"))!.AsSpan().SequenceEqual(sword.Bytes));
        if (floatField != null)
        {
            var f0 = sword.Get<double>(floatField);
            Check("write: a float field", sword.Set(floatField, 1.25).Ok && sword.Get<double>(floatField) == 1.25, $"{f0} -> {sword[floatField]}");
        }

        var bad = false;
        try { sword.Set(intField, 1L << 40); } catch (ArgumentException) { bad = true; }
        Check("write is checked (out of range refused, nothing written)", bad && sword.Get<long>(intField) == changed);

        // this mod holds the field until step 150: the peer mod (types-peer-test) asks for it and must be refused (see its lines)
        _claimField = intField;
        Log.Info($"types-test: claimField {intField}");
    }

    // step 50: give one field back and check the original returns (the other claim stays until step 150)
    private void ResourcesAgain()
    {
        var sword = ReflectedResources.Get(BronzeSword, ItemInfo);
        if (sword == null || _claimField == null) return;
        var floatField = "damageSetup.dmgMod";
        if (sword.HeldBy(floatField) == "types-test")
        {
            var was = sword.Original(floatField);
            Check("restore: the field gives the original back", sword.Restore(floatField) && Equals(sword[floatField], was) && sword.HeldBy(floatField) == null, $"{sword[floatField]}");
        }
        Check("Overrides.Active lists this mod's claim", Overrides.Active.Any(i => i.Mod == "types-test" && i.Detail.StartsWith(_claimField + " = ")), string.Join("; ", Overrides.Active.Select(i => $"{i.Mod}: {i.Detail}")));
    }

    private static object? Try(Func<object?> read)
    {
        try { return read(); } catch (Exception) { return null; }
    }

    // types.impact_names (0.2.0-dev.28): the name Impact scripts use, read from the game's own metadata. PASS only when a
    // known type's Impact name differs from its short and qualified names and finds the type again.
    private void ImpactNames()
    {
        const string cap = "types.impact_names";
        Log.Info($"types-test: {Capabilities.Why(cap)}");
        if (!Capabilities.Is(cap)) { Check("types.impact-names", false, Capabilities.Why(cap)); return; }
        var health = GameTypes.Find("keen::ecs::Health")!;
        var name = health.ImpactName;
        var all = name is null ? [] : GameTypes.FindAllByImpactName(name);
        var single = name is null ? null : GameTypes.ByImpactName(name);
        var f32 = GameTypes.Find("keen::float32");
        var f32Name = f32?.ImpactName;
        var ok = name == "ecs.Health" && name != health.QualifiedName && name != health.Name && all.Contains(health) &&
                 single == (all.Count == 1 ? health : null) && f32Name is not null && GameTypes.ByImpactName(f32Name) == f32 &&
                 GameTypes.ByImpactName("keen::ecs::Health") is null && GameTypes.ByImpactName("Health") is null;
        Check("types.impact-names", ok,
              $"{health.QualifiedName} -> '{name}' (short '{health.Name}'); FindAll: {string.Join(", ", all.Select(t => t.QualifiedName))}; " +
              $"ByImpactName: {single?.QualifiedName ?? "null (ambiguous)"}; keen::float32 -> '{f32Name}'");
        var bad = false;
        try { GameTypes.ByImpactName(""); } catch (ArgumentException) { bad = true; }
        Check("types.impact-names: an empty name is a bad argument", bad);
        var named = GameTypes.All.Count(t => t.ImpactName is not null);
        Check("types.impact-names: every type has one", named == GameTypes.Count, $"{named} of {GameTypes.Count}");
    }

    private void Basics()
    {
        var health = GameTypes.Find("Health");
        Check("GameTypes.Find Health", health is { Kind: GameTypeKind.Struct }, $"{health?.QualifiedName}, {GameTypes.Count} types");
        Check("base class", health?.Base?.QualifiedName == "keen::ecs::SInt32Attribute", health?.Base);
        Check("enum values", GameTypes.Find("ItemRarity")?.EnumValues.Any(v => v.Name == "Legendary") == true);
        Check("components list", GameTypes.Components.Count > 600, GameTypes.Components.Count);

        var ids = Reflected.All("Health");
        var id = ids.FirstOrDefault(e => Reflected.Component(e, "Health")?.Get<long>("dataStorage[2]") > 1);
        Check("Reflected.All Health", ids.Count > 0, ids.Count);
        var h = Reflected.Component(id, "Health");
        if (h != null)
        {
            var max = h.Get<long>("dataStorage[2]");
            var before = h.Get<long>("dataStorage[0]");
            h["dataStorage[0]"] = max - 1;
            var hurt = Reflected.Component(id, "Health")!.Get<long>("dataStorage[0]");
            h["dataStorage[0]"] = Math.Max(before, max);
            Check("Reflected write reads back", hurt == max - 1 && h.Get<long>("dataStorage[0]") == Math.Max(before, max), $"entity {id}: {before} -> {hurt} -> {h["dataStorage[0]"]}");
            var refused = false;
            try { h["dataStorage[0]"] = 1L << 40; } catch (ArgumentException) { refused = true; }
            Check("Reflected write is checked", refused && h.Get<long>("dataStorage[0]") == Math.Max(before, max));
        }

        var sys = GameSystems.Find("stamina_depletion_sprint");
        Check("GameSystems.Find", sys != null && GameSystems.All.Count > 500, $"{GameSystems.All.Count} systems");
        GameSystems.SetEnabled("stamina_depletion_sprint", false);
        var off = GameSystems.Find("stamina_depletion_sprint")!.Enabled == false;
        GameSystems.SetEnabled("stamina_depletion_sprint", true);
        Check("GameSystems.SetEnabled", off && GameSystems.Find("stamina_depletion_sprint")!.Enabled);
        GameSystems.Profile(true);
    }
}
