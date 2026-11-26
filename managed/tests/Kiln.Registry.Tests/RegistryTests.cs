// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Kiln.Registry;
using Xunit;

namespace Kiln.RegistryTests;

/// <summary>A store of cells in a dictionary: what the registry stage gives an entry, with every edit recorded.</summary>
internal sealed class FakeCells : IRegistryCells
{
    public readonly Dictionary<(int, int), object?> Values = new();
    public readonly Dictionary<int, int> Counts = new();
    public readonly List<string> Edits = new();

    public bool TryGet(int field, int index, out object? value) => Values.TryGetValue((field, index), out value);

    public void Set(int field, int index, object? value)
    {
        Values[(field, index)] = value;
        Edits.Add($"set {field:x8}[{index}] = {Show(value)}");
    }

    public int Count(int listField) => Counts.TryGetValue(listField, out var n) ? n : 0;

    public void ListOp(int listField, RegistryListOp op, IReadOnlyList<object?> values)
    {
        Edits.Add($"{op} {listField:x8} [{string.Join(", ", values.Select(Show))}]");
        int n = Count(listField);
        Counts[listField] = op switch
        {
            RegistryListOp.Clear => 0,
            RegistryListOp.Append => n + values.Count,
            RegistryListOp.Remove => n - values.Count,
            _ => values.Count,
        };
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        IReadOnlyDictionary<string, object?> d => "{" + string.Join(", ", d.Select(kv => kv.Key + ": " + Show(kv.Value))) + "}",
        string[] a => "{" + string.Join("|", a) + "}",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

public class RegistryTests
{
    private static readonly Assembly Asm = typeof(RegistrySchema).Assembly;

    // schema/registry/ids.lock.json, copied next to the test assembly by the project file
    private static string LockFile() => Path.Combine(AppContext.BaseDirectory, "ids.lock.json");

    private static IEnumerable<(PropertyInfo Prop, RegistryFieldAttribute Attr)> Attributed() =>
        from t in Asm.GetTypes()
        from p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        let a = p.GetCustomAttribute<RegistryFieldAttribute>()
        where a is not null
        select (p, a);

    [Fact]
    public void EveryCatalogFieldHasExactlyOneProperty()
    {
        var byField = Attributed().GroupBy(x => x.Attr.Field).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var f in RegistrySchema.Fields)
        {
            Assert.True(byField.TryGetValue(f.Field, out var props), $"{f.Field} has no property");
            Assert.Single(props!);
            Assert.Equal(f.Applies, props![0].Attr.Applies);
            Assert.Equal(f.JoinedClient, props[0].Attr.JoinedClient);
            Assert.Contains(f.JoinedClient, new[] { "now", "worldLoad", "rejoin", "restart", "serverOnly", "localOnly", "unknown" });
            Assert.Equal(f.ReadOnly || f.Type == RegistryFieldType.List, props[0].Prop.SetMethod is null);
            if (f.Type == RegistryFieldType.List)
            {
                var gen = props[0].Prop.PropertyType.GetGenericTypeDefinition();
                var want = f.Ops == RegistryOps.Indexed ? typeof(RegistryFixedList<>)
                    : f.Element == RegistryFieldType.Struct ? typeof(RegistryEntryList<>) : typeof(RegistryValueList<>);
                Assert.True(f.ReadOnly || gen == want, $"{f.Field} is a {gen.Name}, not a {want.Name}");
            }
            if (f.Type == RegistryFieldType.Ref)
                Assert.Equal(f.Optional, props[0].Prop.PropertyType == typeof(RegistryId?));
        }
        Assert.Equal(RegistrySchema.Fields.Count, byField.Count);
    }

    [Fact]
    public void CatalogFollowsTheIdsLock()
    {
        var lockJson = JsonNode.Parse(File.ReadAllText(LockFile()))!;
        var regs = lockJson["registries"]!.AsObject();
        int n = 0;
        foreach (var (name, reg) in regs)
        {
            int rid = reg!["id"]!.GetValue<int>();
            Assert.Equal(reg["version"]!.GetValue<string>(), RegistrySchema.Versions[name]);
            foreach (var (path, field) in reg["fields"]!.AsObject())
            {
                var spec = RegistrySchema.Find(name + "." + path);
                Assert.True(spec is not null, $"{name}.{path} is locked but not in the catalog");
                Assert.Equal((rid << 16) | field!["id"]!.GetValue<int>(), spec!.Id);
                Assert.Equal(field["since"]!.GetValue<string>(), spec.Since);
                n++;
            }
        }
        Assert.Equal(n, RegistrySchema.Fields.Count);
    }

    [Fact]
    public void CatalogRowsAreKilnNames()
    {
        foreach (var f in RegistrySchema.Fields)
        {
            Assert.Matches("^[a-z][A-Za-z0-9]*\\.[a-z][A-Za-z0-9.\\[\\]]*$", f.Field);
            Assert.DoesNotContain("0x", f.Field);
            Assert.InRange((int)f.Applies, 1, 3);
        }
        // the five R1 registries and dev.32's world rules (world, difficulty) and limits
        Assert.Equal(new[] { "balance", "difficulty", "drops", "items", "limits", "loot", "recipes", "world" },
            RegistrySchema.Versions.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPublicTypeIsExperimental()
    {
        foreach (var t in Asm.GetExportedTypes())
        {
            var a = t.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>();
            Assert.True(a is not null, $"{t.FullName} is not [Experimental]");
            Assert.Equal("KILNX031", a!.DiagnosticId);
        }
    }

    [Fact]
    public void SettingAPropertyRecordsAnAbsoluteEditInKilnShapes()
    {
        var c = new FakeCells();
        var item = new ItemEntry(RegistryId.Game("material_t1_wood_logs"), c);
        item.MaxStack = 500;
        item.Rarity = RegistryRarity.Epic;
        item.Damage.Fire = 0.5;
        item.Levels.Min = 3;
        item.Fuel.BurnTime = TimeSpan.FromSeconds(120);
        Assert.Equal(new[]
        {
            $"set {ItemFields.MaxStack:x8}[-1] = 500",
            $"set {ItemFields.Rarity:x8}[-1] = epic",
            $"set {ItemFields.DamageFire:x8}[-1] = 0.5",
            $"set {ItemFields.LevelsMin:x8}[-1] = 3",
            $"set {ItemFields.FuelBurnTime:x8}[-1] = 00:02:00",
        }, c.Edits);
        Assert.Equal(500, item.MaxStack);
        Assert.Equal(RegistryRarity.Epic, item.Rarity);
        Assert.Equal(0.5, item.Damage.Fire);
        Assert.Equal(RegistrySchema.Find("items.maxStack")!.Id, ItemFields.MaxStack);
    }

    [Fact]
    public void FlagsAreKilnNameSets()
    {
        var c = new FakeCells();
        var loot = new ItemLootEntry(RegistryId.Game("material_t1_wood_logs"), c);
        loot.Rarities = RegistryRarities.Rare | RegistryRarities.Legendary;
        Assert.Equal($"set {ItemLootFields.Rarities:x8}[-1] = {{rare|legendary}}", c.Edits.Single());
        Assert.Equal(RegistryRarities.Rare | RegistryRarities.Legendary, loot.Rarities);
        Assert.Throws<ArgumentException>(() => loot.Rarities = (RegistryRarities)(1u << 9));
    }

    [Fact]
    public void ReadsOfAWrongShapeAreNotSilent()
    {
        var c = new FakeCells();
        c.Values[(ItemFields.Rarity, -1)] = "mythic";
        var item = new ItemEntry(RegistryId.Game("x"), c);
        Assert.Throws<InvalidOperationException>(() => item.Rarity);
        Assert.Throws<ArgumentException>(() => item.Rarity = (RegistryRarity)42);
        Assert.Throws<ArgumentException>(() => item.Speed = double.NaN);
    }

    [Fact]
    public void ListsRecordEditsByValue()
    {
        var c = new FakeCells();
        var recipe = new RecipeEntry(RegistryId.Game("ammo_arrow_t1_flint"), c);
        var input = recipe.Inputs.AppendNew();
        input.Item = RegistryId.Game("material_t1_wood_logs");
        input.Count = 2;
        Assert.Equal(0, input.Index);
        Assert.Equal(1, recipe.Inputs.Count);
        Assert.Equal(1, recipe.Inputs.RemoveWhere(x => x.Item == RegistryId.Game("material_t1_wood_logs")));
        var item = new ItemEntry(RegistryId.Game("weapon_x"), c);
        item.Perks.Append(new RegistryId("game:perk_fire"));
        item.Perks.Remove(new RegistryId("game:perk_fire"));
        item.Perks.Clear();
        Assert.Equal(new[]
        {
            $"Append {RecipeFields.Inputs:x8} [null]",
            $"set {RecipeFields.InputsItem:x8}[0] = game:material_t1_wood_logs",
            $"set {RecipeFields.InputsCount:x8}[0] = 2",
            $"Remove {RecipeFields.Inputs:x8} [{{category: null, count: 2, item: game:material_t1_wood_logs}}]",
            $"Append {ItemFields.Perks:x8} [game:perk_fire]",
            $"Remove {ItemFields.Perks:x8} [game:perk_fire]",
            $"Clear {ItemFields.Perks:x8} []",
        }, c.Edits);
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.Inputs[5]);
    }

    [Fact]
    public void OnlyComposableListEditsExist()
    {
        // no edit by index; values are appended to value lists, blank entries to entry lists
        foreach (var t in new[] { typeof(RegistryValueList<>), typeof(RegistryEntryList<>), typeof(RegistryFixedList<>) })
            Assert.DoesNotContain(t.GetMethods(), m => m.Name is "RemoveAt" or "Insert");
        Assert.DoesNotContain(typeof(RegistryValueList<>).GetMethods(), m => m.Name == "AppendNew");
        Assert.DoesNotContain(typeof(RegistryEntryList<>).GetMethods(), m => m.Name is "Append" or "Replace");
        Assert.DoesNotContain(typeof(RegistryFixedList<>).GetMethods(), m => m.Name is "Append" or "AppendNew" or "Clear" or "Remove" or "Replace");
    }

    [Fact]
    public void OptionalRefsClear()
    {
        var c = new FakeCells();
        var recipe = new RecipeEntry(RegistryId.Game("x"), c);
        var input = recipe.Inputs.AppendNew();
        input.Category = RegistryId.Game("wood");
        input.Item = null;
        Assert.Null(input.Item);
        Assert.Equal(RegistryId.Game("wood"), input.Category);
        Assert.Equal($"set {RecipeFields.InputsItem:x8}[0] = null", c.Edits.Last());
        Assert.False(RegistrySchema.Find("recipes.outputs[].item")!.Optional);
        Assert.True(RegistrySchema.Find("recipes.inputs[].category")!.Optional);
    }

    [Fact]
    public void FixedListsAreSetPerElement()
    {
        var c = new FakeCells();
        c.Counts[BalanceFields.XpPerLevel] = 5;
        var b = new BalanceEntry(RegistryId.Game("balance"), c);
        b.XpPerLevel[2] = 1500;
        Assert.Equal(1500, b.XpPerLevel[2]);
        Assert.Equal($"set {BalanceFields.XpPerLevel:x8}[2] = 1500", c.Edits.Single());
        Assert.Throws<ArgumentOutOfRangeException>(() => b.XpPerLevel[5] = 1);
        Assert.Equal(5, RegistrySchema.Find("balance.xpPerLevel")!.FixedLength);
        Assert.Equal("flameLevelMax", RegistrySchema.Find("balance.altarsPerFlameLevel")!.LengthField);
        Assert.Equal(RegistryOps.Indexed, RegistrySchema.Find("balance.altarsPerFlameLevel")!.Ops);
        Assert.True(RegistrySchema.Find("balance.flameLevelMax")!.ReadOnly);
    }

    [Fact]
    public void ReadOnlyFieldsHaveNoSetter()
    {
        Assert.Null(typeof(ItemEntry).GetProperty(nameof(ItemEntry.HasDamage))!.SetMethod);
        Assert.Null(typeof(RecipeEntry).GetProperty(nameof(RecipeEntry.Station))!.SetMethod);
        Assert.Null(typeof(BalanceGear).GetProperty(nameof(BalanceGear.Seed))!.SetMethod);
        Assert.NotNull(typeof(ItemEntry).GetProperty(nameof(ItemEntry.MaxStack))!.SetMethod);
    }

    [Fact]
    public void FindNormalisesIndexes()
    {
        Assert.Equal("recipes.inputs[].count", RegistrySchema.Find("recipes.inputs[2].count")!.Field);
        Assert.Equal("drops.entries[].count.min", RegistrySchema.Find("drops.entries[*].count.min")!.Field);
        Assert.Equal(RegistryApplies.WorldLoad, RegistrySchema.Find("items.maxStack")!.Applies);
        Assert.Equal(RegistryApplies.Restart, RegistrySchema.Find("balance.levelCap")!.Applies);
        Assert.Equal("gear.power", RegistrySchema.Find("items.power")!.Formula);
        Assert.Equal("gear.armor", RegistrySchema.Find("items.armor.fire")!.Formula);
        Assert.Null(RegistrySchema.Find("items.maxStackSize"));
    }

    [Theory]
    [InlineData("game:material_t1_wood_logs", true, "game", "material_t1_wood_logs")]
    [InlineData("bigstacks:crate", false, "bigstacks", "crate")]
    [InlineData("game:template_x~1a2b3c4d", true, "game", "template_x~1a2b3c4d")]
    [InlineData("game:_hidden_thing", true, "game", "_hidden_thing")]
    public void Ids(string text, bool game, string mod, string name)
    {
        Assert.True(RegistryId.TryParse(text, out var id));
        Assert.Equal(game, id.IsGame);
        Assert.Equal(mod, id.Mod);
        Assert.Equal(name, id.Name);
        Assert.Equal(text, id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Material_T1")]
    [InlineData("game:Material_T1")]
    [InlineData("game:")]
    [InlineData(":x")]
    [InlineData("a:b:c")]
    [InlineData("game:x~zz")]
    [InlineData("game:x~1a2b")]
    [InlineData("game:a b")]
    [InlineData("game:x~1a2b3c4d~1a2b3c4d")]
    [InlineData("_mod:x")]
    public void BadIds(string text)
    {
        Assert.False(RegistryId.TryParse(text, out _));
        Assert.Throws<ArgumentException>(() => new RegistryId(text));
    }
}
