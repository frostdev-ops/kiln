// SPDX-License-Identifier: MIT
// The creatures and items the taming sample, the taming pages and Kiln.Taming's own examples name are ones the game has:
// every game: creature id matches a template of the creatures catalog Kiln ships (native/services/creatures/catalog_data.inc)
// by the registry's id rule (a trailing * a prefix of at least one), and every game: item id an item of Kiln.Items' item
// list (managed/services/Kiln.Items/Data/items.tsv). As the pets review found, game:enemy_wildbeast_wolf is no creature on
// this build.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.TamingTests;

public sealed partial class SampleNamesTests
{
    private const string Catalog = "native/services/creatures/catalog_data.inc";
    private const string ItemList = "managed/services/Kiln.Items/Data/items.tsv";

    private static readonly string[] Files =
    [
        "mods/samples/TamingSample/TamingSampleMod.cs",
        "docs/kiln/modding/taming.md",
        "docs/kiln/services/taming.md",
        "managed/services/Kiln.Taming/Taming.cs",
        "managed/services/Kiln.Taming/TamingTypes.cs",
        "managed/services/Kiln.Taming/TamingJson.cs",
    ];

    private static string Repo([CallerFilePath] string self = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(self) ?? ""); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, Catalog))) return d.FullName;
        throw new DirectoryNotFoundException("the repository");
    }

    [GeneratedRegex("""^\{"[0-9a-f-]{36}", "([^"]*)",""", RegexOptions.Multiline)]
    private static partial Regex CatalogRow();

    [GeneratedRegex("""game:(?:enemy|animal)_[a-z0-9_]+\*?""")]
    private static partial Regex CreatureId();

    [GeneratedRegex("""game:(?:food|material|tool|weapon|ammo|armor)_[a-z0-9_]+""")]
    private static partial Regex ItemId();

    [Fact]
    public void Every_creature_and_item_the_sample_and_the_pages_name_is_one_the_game_has()
    {
        var root = Repo();
        var creatures = CatalogRow().Matches(File.ReadAllText(Path.Combine(root, Catalog))).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        var items = File.ReadAllLines(Path.Combine(root, ItemList)).Select(l => l.Split('\t')).Where(c => c.Length > 1).Select(c => c[1].ToLowerInvariant()).ToHashSet();
        Assert.True(creatures.Count > 600 && items.Count > 1000, $"read {creatures.Count} creatures, {items.Count} items");
        var wrong = new List<string>();
        var named = 0;
        foreach (var file in Files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            foreach (var id in CreatureId().Matches(text).Select(m => m.Value).Distinct())
            {
                named++;
                var name = id["game:".Length..];
                var ok = name.EndsWith('*') ? creatures.Any(c => c.StartsWith(name[..^1], StringComparison.Ordinal)) : creatures.Contains(name);
                if (!ok) wrong.Add($"{file}: creature {id}");
            }
            foreach (var id in ItemId().Matches(text).Select(m => m.Value).Distinct())
            {
                named++;
                if (!items.Contains(id["game:".Length..])) wrong.Add($"{file}: item {id}");
            }
        }
        Assert.True(named >= 5, $"only {named} names found");
        Assert.Empty(wrong);
    }
}
