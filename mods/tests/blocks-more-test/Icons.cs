// SPDX-License-Identifier: MIT
// A status's and a perk's own PNG icon: the texture GUID follows from the block, the clone names it once content is ready, and a server registers no
// texture (it draws nothing). What a client draws is the manual test's.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private void DefineIcons()
    {
        Blocks.Define("chill", new StatusBlock { Name = "Chill", Seconds = 6, Look = StatusLook.Frost, Icon = "icons/chill.png", OnTick = [new EffectBlock { Damage = Damage.Ice(1) }] });
        Blocks.Define("ember-blessing", new StatusBlock { Name = "Ember Blessing", Kind = StatusKind.Buff, Seconds = 30, Icon = "icons/ember.png", Modifies = { [Stat.Strength] = 2 } });
        Blocks.Define("ember-edge", new PerkBlock { Name = "Ember Edge", Icon = "icons/ember.png", Modifies = { [Stat.Strength] = 3 } });
        Blocks.Define("game-icon", new StatusBlock { Name = "Borrowed", Seconds = 5, Icon = "0x5f425faf", OnApply = [new EffectBlock { Heal = 1 }] });
        Refused("a status's icon that is no PNG", () => Blocks.Define("status-not-png", new StatusBlock { Name = "x", Seconds = 1, Icon = "icons/not-a-png.png", OnApply = [new EffectBlock { Heal = 1 }] }), "status-not-png", "icon", "not a PNG");
        Refused("an icon too large", () => Blocks.Define("status-huge", new StatusBlock { Name = "x", Seconds = 1, Icon = "icons/huge.png", OnApply = [new EffectBlock { Heal = 1 }] }), "icon", "1100x4", "at most 1024");
        Refused("an icon outside the mod's folder", () => Blocks.Define("status-outside", new StatusBlock { Name = "x", Seconds = 1, Icon = "../other/chill.png", OnApply = [new EffectBlock { Heal = 1 }] }), "icon", "in the mod's folder");
        Refused("a perk's icon that isn't there", () => Blocks.Define("perk-missing", new PerkBlock { Name = "x", Icon = "icons/nope.png", Modifies = { [Stat.Strength] = 1 } }), "perk-missing", "icon", "not in the mod's folder");
    }

    private static Guid Derive(string kind, string name) => BlockIds.Derive(Me, kind, name);

    private static string? IconOf(GameDataRegistry r, string entry) => r.Get(entry)?.GetJson("icon")?["value"]?["image"]?.ToString();

    private void Icons()
    {
        foreach (var (section, registry, block) in new[] { ("buffs", Content.Buffs, "chill"), ("buffs", Content.Buffs, "ember-blessing"), ("perks", Content.Perks, "ember-edge"), ("buffs", Content.Buffs, "chill-json"), ("perks", Content.Perks, "ember-edge-json") })
        {
            var texture = Derive("icon", $"{section}:{block}");
            var named = IconOf(registry, $"{Me}.{block}");
            Info($"{block}: icon {registry.Get($"{Me}.{block}")?.GetJson("icon")?.ToJsonString()}");
            Check(named == texture.ToString(), $"{section} {block}: the clone names its own icon texture {texture}: {named}");
            var low = JsonNode.Parse(Blocks.Lowered(block) ?? "{}");
            Check((string?)low?["icon"]?["texture"] == texture.ToString() && ((string?)low?["icon"]?["file"])?.EndsWith(".png") == true, $"{block}: what it became says the file and the texture");
        }
        // the JSON twin is the same block but for its name: the same file
        var a = JsonNode.Parse(Blocks.Lowered("chill") ?? "{}")?["icon"]?["file"]?.ToString();
        var b = JsonNode.Parse(Blocks.Lowered("chill-json") ?? "{}")?["icon"]?["file"]?.ToString();
        Check(a != null && a == b, $"C# and JSON name the same file: {a}, {b}");
        // a game status's icon is still taken as before
        var borrowed = IconOf(Content.Buffs, $"{Me}.game-icon");
        var poison = IconOf(Content.Buffs, "0x5f425faf");
        Check(borrowed != null && borrowed == poison, $"a game status's icon still borrows: {borrowed} = {poison}");
        // players get the name through the content sync: the round trip takes it and gives it back unchanged
        var rt = ContentSync.CheckRoundTrip();
        var mine = rt.Problems.Where(p => p.Contains(Me, StringComparison.Ordinal) || p.Contains("icon", StringComparison.Ordinal)).ToList();
        Check(mine.Count == 0, $"the content sync's round trip keeps the icons: {string.Join("; ", mine.Take(2))} ({rt.Problems.Count} problem(s) in all)");
        // a server draws nothing: kiln.assets has no icon registered under the GUID
        Check(Assets.IconStateOf(Derive("icon", "buffs:chill")) == IconState.None, $"the server registers no texture: {Assets.IconStateOf(Derive("icon", "buffs:chill"))}");
    }
}
