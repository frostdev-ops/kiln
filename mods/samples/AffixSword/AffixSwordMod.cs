// SPDX-License-Identifier: MIT
// Affixes (docs/kiln/modding/affixes.md): a sword that rolls one of three affixes. The affixes and the pool are blocks
// in blocks/affixes.json; the code only rolls.
//
//   /affix-sword     puts the sword into your backpack with one rolled affix: "+3 Strength (of the Bear)",
//                    "+10% Move Speed (Fleet)" or "+17% Resist Fire (Ember Ward)"
//   /affixes         what the item in your hand carries, and what Kiln holds on you for it
//   /affix-reroll    rolls the item in your hand again (admin)
//
// The roll belongs to that one sword: it stays with it in a chest, through a save and a restart, and when another player
// gets it. Its bonus acts while the sword is in someone's hand and stops when it is put away: Kiln does that, the mod
// only defines the affixes and rolls. The mod runs on both sides: in your game it shows what your items rolled.
using Kiln;

namespace AffixSword;

public sealed class AffixSwordMod : Mod
{
    private const string Sword = "Weapon_T1_1H_SwordEpic_01";
    // Capability rows (the /caps command lists them): rolling and reading affixes, "the item in your hand", and the
    // server's copies of an item's data in a player's game. Giving the sword is kiln.items' (Items.Available).
    private const string Rolls = "blocks.affixes", InHand = "items.instances", Sync = "saves.instance_data.sync";

    // The affixes and the pool are blocks in blocks/affixes.json, which Kiln reads by itself a few steps after the mod
    // loaded. The same in C#, in OnLoad (Affixes.Define and Affixes.DefinePool are the same calls):
    //   Blocks.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength,
    //       Tiers = [new AffixTier { Min = 2, Max = 4 }, new AffixTier { Min = 5, Max = 8, Weight = 0.5, MinItemLevel = 20 }] });
    //   var blades = Blocks.Define("blades", new AffixPoolBlock { Items = [Sword], Affixes = [new AffixEntry { Affix = "bear" }, ...] });
    private static AffixPoolHandle Blades =>
        Affixes.FindPool("blades") ?? throw new CommandException("the affixes are not defined (yet): see kiln.log for blocks/affixes.json");

    public override void OnLoad()
    {
        Log.Info("affix-sword: the affixes and the pool \"blades\" come from blocks/affixes.json");
        // In a player's game: asking once for the server's copies opens the channel they arrive on.
        if (Client.IsClient && Capabilities.Is(Sync)) Affixes.SyncedItems();
    }

    [Command("affix-sword", Description = "a sword with one rolled affix, into your backpack")]
    private async Task Give(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a backpack");
        if (!Items.Available || !Capabilities.Is(Rolls)) throw new CommandException(NotHere("kiln.items", Rolls));
        var blades = Blades;
        var given = await Items.Give(c.Source.Player, Sword, 1, options: GiveOptions.Notify | GiveOptions.NoMerge);
        var sword = given.Changes.FirstOrDefault(x => !x.Ref.IsNone).Ref;   // this one sword, not "a sword"
        if (sword.IsNone) throw new CommandException($"the sword did not fit: {given.Status}");
        var rolled = blades.Roll(sword);
        c.Reply(rolled.Ok ? $"your sword rolled {rolled.Affixes[0].Text}; take it in hand" : $"not rolled: {rolled.Reason}");
    }

    [Command("affixes", Description = "what the item in your hand carries")]
    private void List(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a hand");
        if (!Capabilities.Is(InHand) || !Capabilities.Is(Rolls)) throw new CommandException(NotHere(InHand, Rolls));
        var held = Items.Held(c.Source.Player);
        if (held.IsNone) throw new CommandException("hold a weapon or a tool");
        var affixes = Affixes.Of(held);
        c.Reply(affixes.Count == 0 ? "the item in your hand has no affixes" : string.Join(", ", affixes.Select(a => a.Text)));
        foreach (var a in Affixes.Active(c.Source.Player)) c.Reply($"  acting on you: {a.Affix}: {a.Stat} {a.Value:+0.##;-0.##}");
    }

    [Command("affix-reroll", Description = "rolls the item in your hand again", Permission = CommandPermission.Admin)]
    private string Reroll(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a hand");
        if (!Capabilities.Is(InHand) || !Capabilities.Is(Rolls)) throw new CommandException(NotHere(InHand, Rolls));
        var held = Items.Held(c.Source.Player);
        if (held.IsNone) throw new CommandException("hold the sword");
        var rolled = Blades.Roll(held, replace: true);
        return rolled.Ok ? $"rolled {string.Join(", ", rolled.Affixes.Select(a => a.Text))}" : $"not rolled: {rolled.Reason}";   // e.g. the pool is not for this item
    }

    // In the player's game: the server's copy of an item's rolls arrived or changed (they travel as item data of Kiln's
    // own: Affixes.DataMod, Affixes.DataKey). The game's own item tooltip cannot be drawn into yet, so: a notice.
    [Subscribe]
    private void OnSynced(InstanceDataSyncedEvent e)
    {
        if (e.Mod != Affixes.DataMod || e.Key != Affixes.DataKey || e.Change != InstanceSyncChange.Set || !e.Item.Own || !Capabilities.Is(Sync)) return;
        if (Affixes.Synced(e.Item.Entity) is not { Count: > 0 } affixes) return;
        var text = string.Join(", ", affixes.Select(a => a.Text));
        Log.Info($"affix-sword: the item in slot {e.Item.FlatSlot} carries {text}");
        if (Ui.Available) Hud.Toast(text, ToastKind.Success, 6);
    }

    // A command's answer when a row it needs is off: the first such row, and why.
    private static string NotHere(params string[] rows) =>
        rows.FirstOrDefault(row => !Capabilities.Is(row)) is { } off ? $"not on this server ({off} is off: {Capabilities.Why(off)})" : "not on this server right now";
}
