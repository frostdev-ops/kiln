// SPDX-License-Identifier: MIT
// Affix kinds and pools as blocks (dev.27): an affix and a pool from this mod's blocks/affixes.json, a pool from
// Blocks.Define that names the file's affix, a roll from the file's pool onto a sword, and its bonus by the game's
// attribute: on in hand, off when put away, as for an affix of Affixes.Define.
using Kiln;

namespace AffixTest;

public sealed partial class AffixTestMod
{
    private async Task FileBlocks()
    {
        await Until(() => Affixes.FindPool("file-rolls") != null, 25 * 10);
        var pool = Affixes.FindPool("file-rolls");
        var might = Affixes.Find("file-might");
        if (!Check(pool != null && might != null, $"blocks/affixes.json defined affix {might?.Id} and pool {pool?.Id} with no call")) return;
        Check(might!.Id == "affix-test:file-might" && pool!.Id == "affix-test:file-rolls", "their ids are \"mod:name\", as Affixes.Define gives");
        Check(ReferenceEquals(Blocks.Find("file-might"), might) && ReferenceEquals(Blocks.Find("file-rolls"), pool), "Blocks.Find and Affixes.Find give the same objects");
        var code = Blocks.Define("code-rolls", new AffixPoolBlock { Items = ["Weapon_T1_1H_SwordEpic_01"], Affixes = [new AffixEntry { Affix = "file-might" }] });
        Check(ReferenceEquals(Affixes.FindPool("code-rolls"), code), "a pool from Blocks.Define names the file's affix, and Affixes.FindPool finds it");

        var wearer = await Make("1_Player", 16, 0);
        Affixes.LabTrack(wearer);
        var sword = await GiveSword(wearer);
        var rolled = pool!.Roll(sword, new Random(3));
        Check(rolled.Ok && rolled.Affixes.Single() is { Affix: "affix-test:file-might", Tier: 1, Value: 4 } && rolled.Affixes[0].Text == "+4 Strength (of Might)",
            $"the file's pool rolls onto the sword: {rolled}");
        Check(Affixes.Of(sword).SequenceEqual(rolled.Affixes), "the roll is on the item");
        var str0 = await Affixes.StatOf(wearer, Stat.Strength);
        Check(await Hold(wearer, sword), "in hand");
        await Until(() => Affixes.Active(wearer).Count == 1, 40);
        await Steps(4);
        var on = await Affixes.StatOf(wearer, Stat.Strength);
        Check(str0 is { } s0 && Near(on, s0 + 4) && Affixes.Active(wearer).Single() is { Affix: "affix-test:file-might", Value: 4 },
            $"in hand the file's affix acts: strength {str0} -> {on}");
        Check(await PutAway(wearer, sword), "put away");
        await Until(() => Affixes.Active(wearer).Count == 0, 40);
        await Steps(4);
        var off = await Affixes.StatOf(wearer, Stat.Strength);
        Check(str0 is { } s1 && Near(off, s1) && Affixes.Active(wearer).Count == 0, $"put away it stops: strength {on} -> {off}");
        var again = code.Roll(sword, new Random(4), replace: true);
        Check(again.Ok && again.Affixes.Single().Affix == "affix-test:file-might", $"the pool from Blocks.Define rolls it too: {again}");
        Affixes.LabTrack(wearer, false);
        Spawn.Destroy(wearer);
        await Steps(10);
    }

    private async Task FileBlocksAfterRestart()
    {
        await Until(() => Affixes.FindPool("file-rolls") != null, 25 * 10);
        Check(Affixes.FindPool("file-rolls") is { Id: "affix-test:file-rolls" } && Affixes.Find("file-might") is { Id: "affix-test:file-might" },
            "after the restart blocks/affixes.json defines its affix and pool again");
    }
}
