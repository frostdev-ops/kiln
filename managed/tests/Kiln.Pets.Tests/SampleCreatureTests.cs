// SPDX-License-Identifier: MIT
// The creatures that the pets sample, the pets pages and the pet API's own examples name are ones the game has: each
// game: id matches exactly one template of the creatures catalog Kiln ships (native/services/creatures/catalog_data.inc,
// the survey of the game's templates) by the registry's id rules, and it is an enemy that is no boss, as Pets.Define
// admits. Review of dev.32 pets: the sample's hound and every example named game:enemy_wildbeast_wolf, which the game
// does not have (the clearance lab: refused CreatureUnknown); its wolves are _earlygame, _heavy and _hook_ag2.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed partial class SampleCreatureTests
{
    private const string Catalog = "native/services/creatures/catalog_data.inc";

    private static readonly string[] Files =
    [
        "mods/samples/PetsSample/PetsSampleMod.cs",
        "mods/samples/PetsSample/blocks/pets.json",
        "docs/kiln/modding/pets.md",
        "docs/kiln/services/pets.md",
        "managed/services/Kiln.Pets/PetDefinitions.cs",
        "managed/services/Kiln.Blocks/Pets/PetBlocks.cs",
        "schema/kiln-blocks.schema.json",
    ];

    private static string Repo([CallerFilePath] string self = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(self) ?? ""); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, Catalog))) return d.FullName;
        throw new DirectoryNotFoundException("the repository");
    }

    // { guid, template name, display name, kind, flags, ... }: kind 1 enemy AI; flag 1 boss (gen_catalog.py)
    [GeneratedRegex("""^\{"([0-9a-f-]{36})", "([^"]*)", "[^"]*", (\d+), 0x([0-9a-f]+),""", RegexOptions.Multiline)]
    private static partial Regex Row();

    [GeneratedRegex("""game:[a-z0-9_]+(?:~[0-9a-f]{8})?""")]
    private static partial Regex GameId();

    [Fact]
    public void Every_creature_the_sample_and_the_pages_name_is_one_the_game_has()
    {
        var root = Repo();
        var rows = Row().Matches(File.ReadAllText(Path.Combine(root, Catalog)))
            .Select(m => (Template: Guid.Parse(m.Groups[1].Value), Name: m.Groups[2].Value, Kind: int.Parse(m.Groups[3].Value),
                Flags: Convert.ToInt32(m.Groups[4].Value, 16)))
            .ToList();
        Assert.True(rows.Count > 600, $"the catalog read {rows.Count} templates");
        var named = 0;
        var wrong = new List<string>();
        foreach (var file in Files)
            foreach (var id in GameId().Matches(File.ReadAllText(Path.Combine(root, file))).Select(m => m.Value).Distinct())
            {
                named++;
                var (template, why) = PetCreatures.Match(id, rows.Select(r => (r.Template, r.Name)));
                if (why is not null) wrong.Add($"{file}: {why}");
                else if (rows.First(r => r.Template == template) is var row && (row.Kind != 1 || (row.Flags & 1) != 0))
                    wrong.Add($"{file}: {id} is not an enemy that is no boss (kind {row.Kind}, flags 0x{row.Flags:x})");
            }
        Assert.True(named >= Files.Length, $"only {named} creature ids found in {Files.Length} files");
        Assert.Empty(wrong);
    }
}
