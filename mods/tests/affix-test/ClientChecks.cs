// SPDX-License-Identifier: MIT
// Guided checks of affixes for a real player (Kiln.Testing): what only a player's game can show. They work in a hosted
// world and on a server the player joined; the mod is on the server side only.
using Kiln;
using Kiln.Testing;

namespace AffixTest;

public sealed partial class AffixTestMod
{
    private const string Since = "0.2.0-dev.27";
    private const string Area = "Affixes";
    private static ItemInstanceRef _checkSword;

    private void DefineForPlayers()
    {
        // large on purpose: a bar that visibly grows, a walk that is visibly faster
        Affixes.Define("check-vigor", new AffixBlock { Name = "of Vigor", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 200, Max = 200 }] });
        Affixes.Define("check-wind", new AffixBlock { Name = "of the Wind", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.5, Max = 0.5 }] });
        Affixes.Define("check-lungs", new AffixBlock { Name = "of Lungs", Stat = Stat.MaxStamina, Tiers = [new AffixTier { Min = 100, Max = 100 }] });
    }

    private async Task<bool> Applied(CheckContext c, int count, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (Affixes.Active(c.Player).Count != count && DateTime.UtcNow < until)
        {
            await Context.Scheduler.NextTick();
            c.Cancellation.ThrowIfCancellationRequested();
        }
        return Affixes.Active(c.Player).Count == count;
    }

    [ClientCheck("affixes.in-hand", "A sword's affixes act while it is in your hand", Area = Area, Since = Since, Needs = ClientCheckNeeds.Any, Order = 710)]
    public async Task InHand(CheckContext c)
    {
        c.Expect(Capabilities.Is("blocks.affixes") && Capabilities.Is("blocks.affixes.effects"), "blocks.affixes and its effects are available",
            $"{Capabilities.Why("blocks.affixes")} / {Capabilities.Why("blocks.affixes.effects")}");
        var sword = Items.Lookup("Weapon_T1_1H_SwordEpic_01") ?? Items.All.First(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0);
        var g = await Items.Give(c.Player, sword.Id, 1, new Gear(Math.Max(1, sword.LevelMin), ItemRarity.Rare), GiveOptions.Notify | GiveOptions.NoMerge);
        _checkSword = g.Changes.FirstOrDefault(x => !x.Ref.IsNone).Ref;
        if (!c.Expect(!_checkSword.IsNone, $"a {sword.Label} was put into your backpack", g.ToString()))
        {
            await c.Verdict();
            return;
        }
        var set = Affixes.Set(_checkSword, [new RolledAffix("affix-test:check-vigor", "of Vigor", "max-health", 1, 200), new RolledAffix("affix-test:check-wind", "of the Wind", "move-speed", 1, 0.5),
            new RolledAffix("affix-test:check-lungs", "of Lungs", "max-stamina", 1, 100)]);
        c.Expect(set.Ok, "it carries +200 Max Health, +50% Move Speed and +100 Max Stamina", set.ToString());
        var hp0 = await Affixes.StatOf(c.Player, Stat.MaxHealth);
        c.Instruct("A sword was put into your backpack. Look at your health and stamina bars and walk a few steps. Then put the sword on your action bar and take it in hand.");
        var on = await Applied(c, 3, 180);
        c.Expect(on, "Kiln holds its three stat changes on you once it is in your hand", string.Join(", ", Affixes.Active(c.Player).Select(a => $"{a.Affix} {a.Value}")));
        await Context.Scheduler.NextTick();
        var hp1 = await Affixes.StatOf(c.Player, Stat.MaxHealth);
        c.Expect(hp0 is { } a0 && hp1 is { } a1 && Math.Abs(a1 - a0 - 200) < 1e-3, "the game's max health attribute rose by 200", $"{hp0} -> {hp1}");
        c.Instruct("With the sword in hand: is your health bar clearly longer (+200), your stamina bar longer (+100), and do you walk and run clearly faster (+50%)? " +
                   "Now select another action bar slot (put the sword away).");
        var off = await Applied(c, 0, 180);
        c.Expect(off, "put away, Kiln holds nothing on you", string.Join(", ", Affixes.Active(c.Player).Select(a => $"{a.Affix} {a.Value}")));
        var hp2 = await Affixes.StatOf(c.Player, Stat.MaxHealth);
        c.Expect(hp0 is { } b0 && hp2 is { } b2 && Math.Abs(b2 - b0) < 1e-3, "the max health attribute is back", $"{hp0} -> {hp2}");
        c.Instruct("Pass if the bars grew and you were faster with the sword in hand, and all three went back when you put it away. Fail with what you saw. Keep the sword.");
        await c.Verdict();
    }

    [ClientCheck("affixes.death-and-rejoin", "The affixes follow the sword through death, a chest and a rejoin", Area = Area, Since = Since, Needs = ClientCheckNeeds.Any, Order = 711)]
    public async Task DeathAndRejoin(CheckContext c)
    {
        var mine = Items.Read(c.Player)?.Slots.Where(s => !s.Ref.IsNone && Affixes.Of(s.Ref).Any(a => a.Affix == "affix-test:check-vigor")).Select(s => s.Ref).ToList() ?? [];
        if (!c.Expect(mine.Count > 0, "you carry the sword of affixes.in-hand (after a rejoin it is a new item entity, found by its affixes)"))
        {
            c.Instruct("Run \"affixes.in-hand\" first and keep its sword. Skip now.");
            await c.Verdict();
            return;
        }
        c.Instruct("Take the sword in hand (longer bars, faster). 1. Die (jump from a height) and come back: after the respawn, with the sword in hand again, the bars are long again " +
                   "and not twice as long. 2. Put the sword into a chest: the bars shrink. Take it back in hand: they grow. 3. Leave to the main menu and join again, take it in hand: " +
                   "the bars grow. This check watches what Kiln holds on you; the log keeps every change.");
        using var watch = Events.On<AffixesAppliedEvent>(e =>
        {
            if (e.Wearer == c.Player) c.Log.Info($"affix-test: applied to you: {(e.Now.Count == 0 ? "nothing" : string.Join(", ", e.Now.Select(a => $"{a.Affix} {a.Value}")))}");
        });
        var on = await Applied(c, 3, 600);
        c.Expect(on, "with the sword in hand Kiln holds three stat changes on you, never six", string.Join(", ", Affixes.Active(c.Player).Select(a => $"{a.Affix} {a.Value}")));
        c.Instruct("Pass if after each of the three (death, chest, rejoin) the bars were long with the sword in hand and normal without, never longer than +200 / +100. " +
                   "Fail with what you saw (bars that stayed long without the sword, or doubled, matter most).");
        await c.Verdict();
    }
}
